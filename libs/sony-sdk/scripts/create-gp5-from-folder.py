"""Create a flat Prospero GP5, or build a complete plaintext-PKG bundle from a folder.

Unlike a rootdir GP5, the generated manifest names every package input explicitly.
That prevents artifacts from a prior extraction/build from leaking back into the next
package. In GP5-only mode, SDK-generated sce_sys files (keystone, pfs-version, PlayGo
tables, image digests, and about/right.sprx) are left for the publisher to regenerate
unless explicitly retained. Actual input metadata such as param.json, presentation media,
trophy/UCP and game payloads remain included; extracted license.info/license.dat files are
excluded by default.

The source tree is never modified. A normalized param.json with
applicationDrmType=standard and any pic*.png files recovered from DDS-only sce_sys media
are written below a .gp5-assets directory beside the generated GP5.
Application projects use 100 PlayGo chunks by default. Game files remain in chunk zero;
each language declared by playgo-scenario.json gets a small generated, uncompressed payload
in its own chunk so Publishing Tools records a non-zero language size. Remaining chunks are
empty placeholders. Source scenarios and localized presentation strings are retained, and
every scenario contains all 100 chunks and marks all of them as initial/required.

With --build, the output is a new directory containing the GP5, a verified copy of
the patched SDK runtime, build logs, the directly generated LibProsperoPkg-compatible
package, reproducibility scripts, and a SHA-256 manifest. This mode uses
the custom-keystone profile and therefore always includes an exact 96-byte source
sce_sys/keystone instead of asking Publishing Tools to generate one.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import xml.etree.ElementTree as ET


GENERATED_SCE_SYS_FILES = frozenset({
    "keystone",
    "pfs-version.dat",
    "disc_info.dat",
    "ext_info.dat",
    "imagedigs.dat",
    "license.info",
    "license.dat",
    "playgo-chunk.dat",
    "playgo-hash-table.dat",
    "playgo-ficm.dat",
    "playgo-scenario.json",
    "playgo-manifest.xml",
    "playgo-chunk.crc",
    "pfsimage.xml",
    "param.sfo",
    "param_cp_values.json",
    "pronunciation.sig",
    "origin-deltainfo.dat",
    "target-deltainfo.dat",
})
GENERATED_SCE_SYS_DIRECTORIES = frozenset({"about"})
RESERVED_SCE_SYS_SUFFIXES = frozenset({".dds", ".auth_info"})
PROJECT_SUFFIXES = frozenset({".gp4", ".gp5", ".esbak"})
EXCLUDED_ROOT_FILES = frozenset({"ampr_emu.index"})
EXCLUDED_FAKE_LIBRARIES = frozenset({"libsceampr.sprx", "libsceplaygo.sprx"})
GENERATED_ASSET_DIRECTORY = ".gp5-assets"
DEFAULT_PLAYGO_CHUNK_COUNT = 100
PLAYGO_LANGUAGE_PAYLOAD_SIZE = 1024 * 1024
PLAYGO_LANGUAGES = (
    "ja-JP", "en-US", "fr-FR", "es-ES", "de-DE", "it-IT", "nl-NL", "pt-PT",
    "ru-RU", "ko-KR", "zh-Hant", "zh-Hans", "fi-FI", "sv-SE", "da-DK", "no-NO",
    "pl-PL", "pt-BR", "en-GB", "tr-TR", "es-419", "ar-AE", "fr-CA", "cs-CZ",
    "hu-HU", "el-GR", "ro-RO", "th-TH", "vi-VN", "id-ID", "uk-UA",
)
# Runtime API limit in the supported Prospero SDK (SCE_PLAYGO_MAX_SCENARIO).
MAX_PLAYGO_SCENARIO_COUNT = 5
VOLUME_TYPES = {
    "app": "prospero_app",
    "patch": "prospero_patch",
    "ac": "prospero_ac",
    "al": "prospero_al",
}
DEFAULT_PUBLISHING_TOOLS = Path(
    r"C:\SCE\Prospero\Tools\Publishing Tools_2.7.9-plaintext-custom-keystone-v3\bin")
SUPPORTED_PATCH_PROFILES = frozenset({
    "sdk279-plaintext-direct-v3",
    "sdk313-plaintext-direct-v3",
})
CONTENT_ID_PATTERN = re.compile(
    r"^[A-Z]{2}[0-9]{4}-[A-Z]{4}[0-9]{5}_[0-9]{2}-[A-Z0-9]{16}$")


def relative_posix(root: Path, path: Path) -> str:
    return path.relative_to(root).as_posix()


def source_path(project_directory: Path, file: Path, absolute: bool) -> str:
    if absolute:
        return str(file)
    try:
        return os.path.relpath(file, project_directory)
    except ValueError:
        # Windows cannot express a relative path between different drive letters.
        return str(file)


def is_service_artifact(relative: str) -> bool:
    parts = relative.split("/")
    if parts[0].casefold() != "sce_sys":
        return False
    if len(parts) >= 2 and parts[1].casefold() in GENERATED_SCE_SYS_DIRECTORIES:
        return True
    if len(parts) == 2 and parts[1].casefold() in GENERATED_SCE_SYS_FILES:
        return True
    # Publishing Tools converts presentation PNG files into reserved DDS nodes.
    # DDS and SELF-decryption auth-info sidecars found in extracted packages are
    # outputs, not valid GP5 inputs.
    return Path(parts[-1]).suffix.casefold() in RESERVED_SCE_SYS_SUFFIXES


def collect_files(root: Path, output: Path, keep_service_paths: set[str]) -> tuple[list[Path], list[str]]:
    included: list[Path] = []
    skipped: list[str] = []

    def walk(directory: Path) -> None:
        for item in sorted(directory.iterdir(), key=lambda entry: entry.name.casefold()):
            if item.is_symlink():
                raise ValueError(f"symbolic link/junction is not a GP5 input: {item}")
            relative = relative_posix(root, item)
            if item == output:
                skipped.append(relative + " (output GP5)")
                continue
            if item.is_dir():
                if (item.parent == root and
                        item.name.casefold() == GENERATED_ASSET_DIRECTORY.casefold()):
                    skipped.append(relative + "/ (generated GP5 assets)")
                    continue
                walk(item)
                continue
            if not item.is_file():
                skipped.append(relative + " (not a regular file)")
                continue
            if item.suffix.casefold() in PROJECT_SUFFIXES:
                skipped.append(relative + " (project artifact)")
                continue
            if item.name.casefold().endswith(".playgo-scenario.json"):
                skipped.append(relative + " (generated GP5 scenario sidecar)")
                continue
            normalized = relative.casefold()
            if normalized in EXCLUDED_ROOT_FILES:
                skipped.append(relative + " (host emulator index)")
                continue
            if (normalized.startswith("fakelib/") and
                    normalized.removeprefix("fakelib/") in EXCLUDED_FAKE_LIBRARIES):
                skipped.append(relative + " (SDK/runtime fakelib module)")
                continue
            if is_service_artifact(relative) and normalized not in keep_service_paths:
                skipped.append(relative + " (reserved/SDK-generated sce_sys artifact)")
                continue
            included.append(item)

    walk(root)
    return included, skipped


def content_id(param_path: Path) -> str | None:
    try:
        value = json.loads(param_path.read_text(encoding="utf-8-sig"))
    except (OSError, UnicodeDecodeError, json.JSONDecodeError) as error:
        raise ValueError(f"cannot parse {param_path}: {error}") from error
    result = value.get("contentId")
    if not isinstance(result, str) or not result:
        raise ValueError(f"{param_path} has no non-empty contentId")
    if CONTENT_ID_PATTERN.fullmatch(result) is None:
        raise ValueError(f"{param_path} has invalid Prospero contentId: {result!r}")
    return result


def default_language(param_path: Path) -> str:
    try:
        value = json.loads(param_path.read_text(encoding="utf-8-sig"))
    except (OSError, UnicodeDecodeError, json.JSONDecodeError) as error:
        raise ValueError(f"cannot parse {param_path}: {error}") from error
    localized = value.get("localizedParameters")
    language = localized.get("defaultLanguage") if isinstance(localized, dict) else None
    return language if isinstance(language, str) and language else "en-US"


def write_standard_param(source: Path, destination: Path) -> None:
    try:
        value = json.loads(source.read_text(encoding="utf-8-sig"))
    except (OSError, UnicodeDecodeError, json.JSONDecodeError) as error:
        raise ValueError(f"cannot parse {source}: {error}") from error
    if not isinstance(value, dict):
        raise ValueError(f"{source} must contain a JSON object")
    value["applicationDrmType"] = "standard"
    destination.parent.mkdir(parents=True, exist_ok=True)
    destination.write_text(
        json.dumps(value, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")


def missing_presentation_pngs(root: Path) -> list[tuple[Path, str]]:
    system = root / "sce_sys"
    if not system.is_dir():
        return []
    files = {
        item.name.casefold(): item
        for item in system.iterdir()
        if item.is_file()
    }
    missing: list[tuple[Path, str]] = []
    for name, dds in sorted(files.items()):
        if not name.startswith("pic") or not name.endswith(".dds"):
            continue
        png_name = dds.with_suffix(".png").name
        if png_name.casefold() not in files:
            missing.append((dds, png_name))
    return missing


def find_dds_converter(explicit: Path | None) -> Path:
    toolkit_root = Path(__file__).resolve().parents[1]
    candidates: list[Path] = []
    if explicit is not None:
        candidates.append(explicit)
    environment = os.environ.get("LIBPROSPERO_DDS_CONVERTER")
    if environment:
        candidates.append(Path(environment))
    candidates.extend([
        toolkit_root / "prospero-dds2png.exe",
        toolkit_root / ".analysis" / "native-dds-converter" / "prospero-dds2png.exe",
    ])
    for candidate in candidates:
        resolved = candidate.expanduser().resolve()
        if resolved.is_file():
            return resolved
    raise FileNotFoundError(
        "a sce_sys/pic*.dds file has no matching PNG, but no prospero-dds2png converter "
        "was found; rebuild the toolkit or use --dds-converter")


def recover_presentation_pngs(
    missing: list[tuple[Path, str]], generated_system: Path,
    converter_path: Path | None,
) -> list[tuple[str, Path]]:
    if not missing:
        return []
    converter = find_dds_converter(converter_path)
    recovered: list[tuple[str, Path]] = []
    for source, png_name in missing:
        destination = generated_system / png_name
        if destination.exists() or destination.is_symlink():
            raise FileExistsError(f"generated PNG already exists: {destination}")
        destination.parent.mkdir(parents=True, exist_ok=True)
        print(f"Recovering sce_sys/{png_name} from {source.name}...")
        command = ([sys.executable, str(converter)]
                   if converter.suffix.casefold() == ".py" else [str(converter)])
        command.extend([str(source), str(destination)])
        if source.stem.casefold() == "pic2":
            command.append("--preserve-alpha")
        completed = subprocess.run(
            command, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
            text=True, encoding="utf-8", errors="replace", timeout=300)
        if completed.returncode != 0:
            raise RuntimeError(
                f"DDS converter failed for {source} with exit code "
                f"{completed.returncode}:\n{completed.stdout.rstrip()}")
        if not destination.is_file() or not destination.read_bytes().startswith(b"\x89PNG\r\n\x1a\n"):
            raise ValueError(f"DDS converter did not create a valid PNG: {destination}")
        recovered.append((f"sce_sys/{png_name}", destination))
    return recovered


def default_scenario(language: str) -> dict[str, object]:
    return {
        "chunkDefaultLanguage": language,
        "chunkSupportedLanguages": [language],
        "scenarioCount": 1,
        "scenarioDefaultId": 0,
        "scenarioDefaultLanguage": language,
        "scenarios": [{
            "id": 0,
            "type": "playmode",
            language: {"title": "Scenario #0", "description": "Default play scenario"},
        }],
    }


def write_scenario(
    path: Path, source: Path, fallback_language: str,
) -> tuple[int, int, list[tuple[int, str, str]], str, list[str]]:
    if source.is_file():
        try:
            value = json.loads(source.read_text(encoding="utf-8-sig"))
        except (OSError, UnicodeDecodeError, json.JSONDecodeError) as error:
            raise ValueError(f"cannot parse {source}: {error}") from error
    else:
        value = default_scenario(fallback_language)
    if not isinstance(value, dict):
        raise ValueError(f"{source} must contain a JSON object")

    count = value.get("scenarioCount")
    default_id = value.get("scenarioDefaultId")
    language = value.get("scenarioDefaultLanguage")
    scenarios = value.get("scenarios")
    if type(count) is not int or not 1 <= count <= MAX_PLAYGO_SCENARIO_COUNT:
        raise ValueError(
            f"{source} scenarioCount must be between 1 and {MAX_PLAYGO_SCENARIO_COUNT}")
    if type(default_id) is not int or not 0 <= default_id < count:
        raise ValueError(f"{source} scenarioDefaultId is outside the scenario table")
    if not isinstance(language, str) or not language.strip():
        raise ValueError(f"{source} has no valid scenarioDefaultLanguage")
    language = language.strip()
    language_codes = {item.casefold(): item for item in PLAYGO_LANGUAGES}
    if language.casefold() not in language_codes:
        raise ValueError(f"{source} has unsupported scenarioDefaultLanguage: {language!r}")
    language = language_codes[language.casefold()]
    if not isinstance(scenarios, list) or len(scenarios) != count:
        raise ValueError(f"{source} scenarios must contain exactly {count} entries")

    definitions: list[tuple[int, str, str]] = []
    ids: set[int] = set()
    for scenario in scenarios:
        if not isinstance(scenario, dict):
            raise ValueError(f"{source} contains a non-object scenario entry")
        scenario_id = scenario.get("id")
        scenario_type = scenario.get("type")
        if (type(scenario_id) is not int or not 0 <= scenario_id < count or
                scenario_id in ids):
            raise ValueError(f"{source} has an invalid or duplicate scenario id: {scenario_id!r}")
        if not isinstance(scenario_type, str) or not scenario_type.strip():
            raise ValueError(f"{source} scenario {scenario_id} has no valid type")
        localized = scenario.get(language)
        if not isinstance(localized, dict):
            raise ValueError(
                f"{source} scenario {scenario_id} has no {language!r} localization")
        title = localized.get("title")
        label = (title.strip() if isinstance(title, str) and title.strip()
                 else f"Scenario #{scenario_id}")
        ids.add(scenario_id)
        definitions.append((scenario_id, scenario_type, label))
    if ids != set(range(count)):
        raise ValueError(f"{source} scenario ids must be contiguous from 0 through {count - 1}")

    chunk_default_language = value.get("chunkDefaultLanguage", language)
    if not isinstance(chunk_default_language, str) or not chunk_default_language.strip():
        raise ValueError(f"{source} has no valid chunkDefaultLanguage")
    chunk_default_language = chunk_default_language.strip()
    if chunk_default_language.casefold() not in language_codes:
        raise ValueError(
            f"{source} has unsupported chunkDefaultLanguage: {chunk_default_language!r}")
    chunk_default_language = language_codes[chunk_default_language.casefold()]
    supported = value.get("chunkSupportedLanguages")
    if supported is None:
        # Older scenario files omit the explicit chunk list. Retain every localization key
        # common to at least one scenario and always include the scenario default language.
        supported = [chunk_default_language]
        for scenario in scenarios:
            for key, localized in scenario.items():
                canonical = language_codes.get(key.casefold())
                if (canonical is not None and isinstance(localized, dict) and
                        canonical not in supported):
                    supported.append(canonical)
    if (not isinstance(supported, list) or not supported or
            any(not isinstance(item, str) or not item.strip() for item in supported)):
        raise ValueError(f"{source} chunkSupportedLanguages must be a non-empty string array")
    unsupported = [item for item in supported if item.strip().casefold() not in language_codes]
    if unsupported:
        raise ValueError(f"{source} contains unsupported chunk languages: {unsupported}")
    chunk_languages = [language_codes[item.strip().casefold()] for item in supported]
    if len({item.casefold() for item in chunk_languages}) != len(chunk_languages):
        raise ValueError(f"{source} chunkSupportedLanguages contains duplicates")
    if chunk_default_language.casefold() not in {item.casefold() for item in chunk_languages}:
        raise ValueError(
            f"{source} chunkDefaultLanguage is not present in chunkSupportedLanguages")
    if len(chunk_languages) >= DEFAULT_PLAYGO_CHUNK_COUNT:
        raise ValueError(
            f"{source} declares too many chunk languages for "
            f"{DEFAULT_PLAYGO_CHUNK_COUNT} PlayGo chunks")

    path.write_text(
        json.dumps(value, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    return (count, default_id, sorted(definitions),
            chunk_default_language, chunk_languages)


def write_language_payloads(
    generated_root: Path, languages: list[str],
) -> list[tuple[str, Path, int]]:
    """Create harmless per-language chunk members outside reserved sce_sys paths."""
    result: list[tuple[str, Path, int]] = []
    payload_directory = generated_root / "playgo-languages"
    for index, language in enumerate(languages, start=1):
        safe_language = re.sub(r"[^A-Za-z0-9._-]", "_", language)
        payload = payload_directory / f"{index:02d}-{safe_language}.bin"
        if payload.exists() or payload.is_symlink():
            raise FileExistsError(f"generated PlayGo language payload already exists: {payload}")
        payload.parent.mkdir(parents=True, exist_ok=True)
        payload.write_bytes(bytes(PLAYGO_LANGUAGE_PAYLOAD_SIZE))
        destination = f"playgo-languages/{index:02d}-{safe_language}.bin"
        result.append((destination, payload, index))
    return result


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for data in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(data)
    return digest.hexdigest()


def indent(element: ET.Element, level: int = 0) -> None:
    # ElementTree.indent is only available in Python 3.9+. Keep the script usable with the
    # Python versions commonly bundled beside Publishing Tools.
    prefix = "\n" + "  " * level
    if len(element):
        if not element.text or not element.text.strip():
            element.text = prefix + "  "
        for child in element:
            indent(child, level + 1)
        if not element[-1].tail or not element[-1].tail.strip():
            element[-1].tail = prefix
    if level and (not element.tail or not element.tail.strip()):
        element.tail = prefix


def build_gp5(
    root: Path, output: Path, volume: str, passcode: str,
    absolute_paths: bool, keep_service_paths: set[str], converter_path: Path | None,
) -> tuple[int, list[str]]:
    if len(passcode) != 32:
        raise ValueError("passcode must contain exactly 32 characters")
    if not root.is_dir():
        raise NotADirectoryError(root)
    if output.exists() or output.is_symlink():
        raise FileExistsError(f"output already exists: {output}")
    scenario_input = output.with_suffix(".playgo-scenario.json")
    if scenario_input.exists() or scenario_input.is_symlink():
        raise FileExistsError(f"generated PlayGo scenario already exists: {scenario_input}")
    output.parent.mkdir(parents=True, exist_ok=True)

    files, skipped = collect_files(root, output, keep_service_paths)
    param = root / "sce_sys" / "param.json"
    if param not in files:
        reason = "excluded as a service artifact" if param.exists() else "missing"
        raise ValueError(f"required sce_sys/param.json is {reason}")
    package_content_id = content_id(param)
    (scenario_count, scenario_default_id, scenario_definitions,
     chunk_default_language, chunk_languages) = write_scenario(
        scenario_input, root / "sce_sys" / "playgo-scenario.json", default_language(param))
    generated_root = output.parent / GENERATED_ASSET_DIRECTORY / output.stem
    generated_system = generated_root / "sce_sys"
    generated_param = generated_system / "param.json"
    if generated_param.exists() or generated_param.is_symlink():
        raise FileExistsError(f"generated param.json already exists: {generated_param}")
    write_standard_param(param, generated_param)
    recovered_pngs = recover_presentation_pngs(
        missing_presentation_pngs(root), generated_system, converter_path)
    language_payloads = (write_language_payloads(generated_root, chunk_languages)
                         if volume == "app" else [])

    project = ET.Element("psproject", {"fmt": "gp5"})
    volume_node = ET.SubElement(project, "volume")
    ET.SubElement(volume_node, "volume_type").text = VOLUME_TYPES[volume]
    package = ET.SubElement(volume_node, "package", {"passcode": passcode})
    package.set("content_id", package_content_id)
    if volume == "app":
        chunk_info = ET.SubElement(volume_node, "chunk_info", {
            "chunk_count": str(DEFAULT_PLAYGO_CHUNK_COUNT),
            "scenario_count": str(scenario_count)})
        chunks = ET.SubElement(chunk_info, "chunks", {
            "supported_languages": " ".join(chunk_languages),
            "default_language": chunk_default_language,
        })
        for chunk_id in range(DEFAULT_PLAYGO_CHUNK_COUNT):
            attributes = {
                "id": str(chunk_id),
                "label": f"Chunk #{chunk_id}",
                "layer_no": "0",
                "languages": (chunk_languages[chunk_id - 1]
                              if 1 <= chunk_id <= len(chunk_languages)
                              else " ".join(chunk_languages)),
            }
            ET.SubElement(chunks, "chunk", attributes)
        scenarios = ET.SubElement(
            chunk_info, "scenarios", {"default_id": str(scenario_default_id)})
        for scenario_id, scenario_type, label in scenario_definitions:
            scenario = ET.SubElement(scenarios, "scenario", {
                "id": str(scenario_id),
                "type": scenario_type,
                "initial_chunk_count": str(DEFAULT_PLAYGO_CHUNK_COUNT),
                "label": label,
            })
            scenario.text = f"0-{DEFAULT_PLAYGO_CHUNK_COUNT - 1}"

    files_node = ET.SubElement(project, "files")
    ET.SubElement(files_node, "file", {
        "dst_path": "sce_sys/playgo-scenario.json",
        "src_path": source_path(output.parent, scenario_input, absolute_paths),
        "chunk": "0",
    })
    for destination, recovered in recovered_pngs:
        ET.SubElement(files_node, "file", {
            "dst_path": destination,
            "src_path": source_path(output.parent, recovered, absolute_paths),
            "chunk": "0",
        })
    for destination, payload, chunk_id in language_payloads:
        ET.SubElement(files_node, "file", {
            "dst_path": destination,
            "src_path": source_path(output.parent, payload, absolute_paths),
            "chunk": str(chunk_id),
            "pfs_compression": "disable",
        })
    for file in files:
        actual_source = generated_param if file == param else file
        ET.SubElement(files_node, "file", {
            "dst_path": relative_posix(root, file),
            "src_path": source_path(output.parent, actual_source, absolute_paths),
            "chunk": "0",
        })
    indent(project)
    ET.ElementTree(project).write(output, encoding="utf-8", xml_declaration=True)
    return len(files) + len(recovered_pngs) + len(language_payloads) + 1, skipped


def copy_toolchain(
    source: Path, destination: Path,
) -> tuple[str, list[dict[str, object]]]:
    manifest_path = source / "patch-manifest.json"
    if not manifest_path.is_file():
        raise FileNotFoundError(
            f"patched SDK manifest is missing: {manifest_path}; "
            "run the matching scripts/create-sdk*-plaintext.py first")
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    profile = manifest.get("profile")
    if profile not in SUPPORTED_PATCH_PROFILES:
        raise ValueError(f"unsupported patched SDK profile in {manifest_path}")
    records: list[dict[str, object]] = []
    files = manifest.get("files")
    if not isinstance(files, list) or not files:
        raise ValueError(f"patched SDK manifest has no runtime file list: {manifest_path}")
    for record in files:
        relative = record.get("file")
        expected = record.get("output_sha256")
        if not isinstance(relative, str) or not isinstance(expected, str):
            raise ValueError(f"invalid runtime record in {manifest_path}")
        input_file = source / Path(relative)
        if not input_file.is_file() or sha256_file(input_file) != expected:
            raise ValueError(f"patched SDK runtime failed verification: {input_file}")
        output_file = destination / Path(relative)
        output_file.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(input_file, output_file)
        if sha256_file(output_file) != expected:
            raise OSError(f"copied SDK runtime failed verification: {output_file}")
        records.append({"file": relative, "size": output_file.stat().st_size, "sha256": expected})
    shutil.copy2(manifest_path, destination / manifest_path.name)
    readme = source / "README-PLAINTEXT-TEST.md"
    if readme.is_file():
        shutil.copy2(readme, destination / readme.name)
    required = [destination / "prospero-pub-cmd.exe", destination / "libScePubTools.dll",
                destination / "ext" / "sc2.exe"]
    if not all(path.is_file() for path in required):
        raise ValueError("patched SDK manifest did not provide the required publisher runtime")
    return profile, records


def run_logged(command: list[str], log_path: Path, cwd: Path) -> None:
    completed = subprocess.run(
        command, cwd=cwd, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
    log_path.parent.mkdir(parents=True, exist_ok=True)
    log_path.write_bytes(completed.stdout)
    if completed.returncode != 0 or b"[Error]" in completed.stdout:
        raise RuntimeError(
            f"command failed with exit code {completed.returncode}; see {log_path}")


def write_rebuild_script(bundle: Path, package_name: str) -> None:
    script = f'''param([switch]$Force)
$ErrorActionPreference = "Stop"
$bundle = $PSScriptRoot
$final = Join-Path $bundle "{package_name}"
$partial = Join-Path $bundle (([IO.Path]::GetFileNameWithoutExtension($final)) + ".partial.pkg")
$partialMetric = $partial + ".naps_metric.json"
$finalMetric = $final + ".naps_metric.json"
$logDir = Join-Path $bundle "logs"
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
if ((Test-Path -LiteralPath $partial) -or (Test-Path -LiteralPath $partialMetric) -or
    (Test-Path -LiteralPath $final) -or (Test-Path -LiteralPath $finalMetric)) {{
    if (-not $Force) {{ throw "Output PKG already exists; rerun with -Force to replace it." }}
    Remove-Item -LiteralPath $partial,$partialMetric,$final,$finalMetric `
        -Force -ErrorAction SilentlyContinue
}}
& (Join-Path $bundle "toolchain/prospero-pub-cmd.exe") img_create --oformat nwonly `
    --no_progress_bar (Join-Path $bundle "project.gp5") $partial 2>&1 |
    Tee-Object -FilePath (Join-Path $logDir "img-create.log")
if ($LASTEXITCODE -ne 0) {{ throw "prospero-pub-cmd failed with exit code $LASTEXITCODE" }}
Move-Item -LiteralPath $partial -Destination $final
if (Test-Path -LiteralPath $partialMetric) {{
    Move-Item -LiteralPath $partialMetric -Destination $finalMetric
}}
$manifestPath = Join-Path $bundle "build-manifest.json"
if (Test-Path -LiteralPath $manifestPath) {{
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    $outputs = [ordered]@{{}}
    foreach ($path in @($final, $finalMetric)) {{
        if (Test-Path -LiteralPath $path) {{
            $item = Get-Item -LiteralPath $path
            $outputs[$item.Name] = [ordered]@{{
                size = $item.Length
                sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash.ToLowerInvariant()
            }}
        }}
    }}
    $manifest.outputs = $outputs
    $manifest | Add-Member -NotePropertyName last_rebuilt_utc `
        -NotePropertyValue ([DateTime]::UtcNow.ToString("o")) -Force
    $json = $manifest | ConvertTo-Json -Depth 10
    [IO.File]::WriteAllText($manifestPath, $json + [Environment]::NewLine,
        [Text.UTF8Encoding]::new($false))
}}
Write-Host "Created $final"
'''
    (bundle / "build.ps1").write_text(script, encoding="utf-8-sig")


def build_bundle(
    root: Path, destination: Path, volume: str, passcode: str,
    keep_service_paths: set[str], publishing_tools: Path,
    converter_path: Path | None,
) -> tuple[Path, int, list[str]]:
    if volume != "app":
        raise ValueError("--build currently supports only the verified debug APP/nwonly profile")
    if "sce_sys/keystone" not in keep_service_paths:
        raise ValueError(
            "the custom-keystone build profile requires a 96-byte source "
            "sce_sys/keystone")
    if destination.exists() or destination.is_symlink():
        raise FileExistsError(f"build destination already exists: {destination}")
    destination.mkdir(parents=True)
    project_path = destination / "project.gp5"
    # Bundle GP5 paths are relative to the bundle. They still resolve to the caller-owned source
    # tree, which avoids silently duplicating a potentially multi-gigabyte game directory.
    count, skipped = build_gp5(
        root, project_path, volume, passcode, absolute_paths=False,
        keep_service_paths=keep_service_paths, converter_path=converter_path)

    toolchain = destination / "toolchain"
    toolchain_profile, runtime = copy_toolchain(publishing_tools, toolchain)
    scripts_dir = destination / "scripts"
    scripts_dir.mkdir()
    this_script = Path(__file__).resolve()
    shutil.copy2(this_script, scripts_dir / this_script.name)

    final_name = content_id(root / "sce_sys" / "param.json") + "-plaintext.pkg"
    final_package = destination / final_name
    partial_package = final_package.with_name(final_package.stem + ".partial.pkg")
    partial_metric = Path(str(partial_package) + ".naps_metric.json")
    final_metric = Path(str(final_package) + ".naps_metric.json")
    run_logged([
        str(toolchain / "prospero-pub-cmd.exe"), "img_create", "--oformat", "nwonly",
        "--no_progress_bar", str(project_path), str(partial_package),
    ], destination / "logs" / "img-create.log", destination)
    partial_package.replace(final_package)
    if partial_metric.is_file():
        partial_metric.replace(final_metric)
    write_rebuild_script(destination, final_name)

    output_files = [final_package, final_metric]
    output_records = {
        path.name: {"size": path.stat().st_size, "sha256": sha256_file(path)}
        for path in output_files if path.is_file()
    }
    result = {
        "profile": toolchain_profile.replace("-v3", "-bundle-v3"),
        "toolchain_profile": toolchain_profile,
        "source": str(root),
        "project": "project.gp5",
        "passcode": passcode,
        "volume": volume,
        "file_count": count,
        "excluded": skipped,
        "toolchain_source": str(publishing_tools),
        "toolchain_files": runtime,
        "outputs": output_records,
    }
    (destination / "build-manifest.json").write_text(
        json.dumps(result, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    readme = (
        "# Plaintext LibProsperoPkg build bundle\n\n"
        f"Source folder: `{root}`\n\n"
        f"Final package: `{final_name}`\n\n"
        "The bundle contains the version-locked patched SDK runtime, GP5, logs, "
        "hash manifest and `build.ps1`. The source payload is referenced by `project.gp5` and is "
        "not duplicated. The SDK writes the final plaintext/no-auth representation directly; "
        "the temporary filename is only atomically renamed. Run "
        "`powershell -ExecutionPolicy Bypass -File .\\build.ps1 -Force` to rebuild.\n"
    )
    (destination / "README.md").write_text(readme, encoding="utf-8")
    return final_package, count, skipped


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source", type=Path, help="prepared game/project folder")
    parser.add_argument(
        "output", type=Path,
        help="new .gp5 path, or a new build-bundle directory with --build")
    parser.add_argument("--build", action="store_true",
                        help="create a complete build bundle and produce the plaintext PKG")
    parser.add_argument("--publishing-tools", type=Path, default=DEFAULT_PUBLISHING_TOOLS,
                        help="patched Publishing Tools bin directory used by --build")
    parser.add_argument("--volume", choices=VOLUME_TYPES, default="app",
                        help="package type (default: app)")
    parser.add_argument("--passcode", default="0" * 32,
                        help="32-character package passcode (default: 32 zeroes)")
    parser.add_argument("--absolute-paths", action="store_true",
                        help="write absolute src_path values instead of paths relative to the GP5")
    parser.add_argument("--keep-sce-sys", action="append", default=[], metavar="PATH",
                        help="retain a normally regenerated sce_sys path; may be repeated")
    parser.add_argument("--keep-keystone", action="store_true",
                        help="include source sce_sys/keystone; requires the custom-keystone SDK patch")
    parser.add_argument(
        "--dds-converter", type=Path,
        help="path to the standalone prospero-dds2png executable")
    args = parser.parse_args()
    root, output = args.source.resolve(), args.output.resolve()
    if output == root:
        raise ValueError("output must be distinct from the source directory")
    keep = {path.replace("\\", "/").lstrip("/").casefold() for path in args.keep_sce_sys}
    # --build uses a custom-keystone-only SDK profile, so preserving the source
    # keystone is mandatory. GP5-only generation remains opt-in.
    keep_keystone = args.keep_keystone or args.build
    if keep_keystone:
        keystone = root / "sce_sys" / "keystone"
        if not keystone.is_file() or keystone.stat().st_size != 0x60:
            raise ValueError(
                "the custom-keystone build requires a 96-byte source file at "
                "sce_sys/keystone")
        keep.add("sce_sys/keystone")
    if args.build:
        if args.absolute_paths:
            raise ValueError("--absolute-paths is not used in --build mode")
        package, count, skipped = build_bundle(
            root, output, args.volume, args.passcode, keep,
            args.publishing_tools.resolve(),
            args.dds_converter.resolve() if args.dds_converter else None)
        print(f"Created build bundle {output} with {count} explicit file mapping(s).")
        print(f"Final LibProsperoPkg-compatible package: {package}")
    else:
        if not output.name.casefold().endswith(".gp5"):
            raise ValueError("output must be a .gp5 file unless --build is specified")
        count, skipped = build_gp5(
            root, output, args.volume, args.passcode, args.absolute_paths, keep,
            args.dds_converter.resolve() if args.dds_converter else None)
        print(f"Created {output} with {count} explicit file mapping(s).")
    if skipped:
        print("Excluded " + str(len(skipped)) + " service/project artifact(s):")
        for item in skipped:
            print("  " + item)


if __name__ == "__main__":
    try:
        main()
    except Exception as error:
        print(f"error: {error}", file=sys.stderr)
        raise SystemExit(2)
