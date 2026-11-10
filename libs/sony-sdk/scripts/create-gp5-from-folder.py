"""Create a flat Prospero GP5, or build a complete plaintext-PKG bundle from a folder.

Unlike a rootdir GP5, the generated manifest names every package input explicitly.
That prevents artifacts from a prior extraction/build from leaking back into the next
package. In GP5-only mode, SDK-generated sce_sys files (keystone, pfs-version, PlayGo
tables, image digests, patch origin/target metadata, and about/right.sprx) are left for
the publisher to regenerate unless explicitly retained. Extracted `sce_suppl` and
`sce_sc` trees are also omitted because Publishing Tools reserves those directories.
The root decrypted/ tree is omitted because it contains host-side decryption copies.
Actual input metadata such as param.json, presentation media,
trophy/UCP and game payloads remain included; extracted license.info/license.dat files are
excluded by default. Missing or unreadable UDS UCP containers are replaced with empty,
well-formed stubs derived from NPWR ids in sce_sys/uds/npbind.dat. A stale UCP digest is repaired
without discarding its member data.

The source tree is never modified. A normalized param.json with
applicationDrmType=standard, repaired SELF headers, and PNG files recovered from
sce_sys/*.dds when a suitable PNG is missing are written below a .gp5-assets
directory beside the generated GP5. SDK presentation PNGs use RGB for icon0,
pic0, and pic1, and RGBA for pic2; only pic2 preserves the alpha channel.
For PSAL (`--volume al`), the project instead contains only its normalized metadata
(param.json and a validated 512x512 RGB icon0.png) and requires an entitlement key.
Application projects read the PlayGo chunk count from sce_sys/playgo-chunk.dat when present.
Without it, the fallback is 100 chunks, configurable from 1 through 800.
When the source also has playgo-hash-table.dat and playgo-ficm.dat, its chunk labels,
chunk languages and file-to-chunk assignments are kept.
Otherwise game files remain in chunk zero; each language declared by playgo-scenario.json
gets a small generated, uncompressed payload. Languages share a chunk when the configured
count is too small to assign each a separate chunk. Remaining chunks are empty placeholders.
Source scenarios and localized presentation strings are retained. When the source
descriptor has valid scenario tables, the generated GP5 keeps their labels, ordered
chunk lists and initial chunk counts; otherwise each scenario includes all chunks.

With --build, APP and AC projects produce a new directory containing the GP5, a verified copy of
the patched SDK runtime, build logs, the directly generated LibProsperoPkg-compatible
package, reproducibility scripts, and a SHA-256 manifest. This mode uses
the custom-keystone profile: APP requires an exact 96-byte source sce_sys/keystone.
AC requires a 16-byte entitlement key and omits application-only param.json fields.
"""
from __future__ import annotations

import argparse
import fnmatch
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import struct
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
    "lem_notification.dat",
    "playgo-chunk.dat",
    "playgo-hash-table.dat",
    "playgo-ficm.dat",
    "playgo-scenario.json",
    "playgo-manifest.xml",
    "playgo-chunk.crc",
    "pfsimage.xml",
    "pfs-region-hints.json",
    "param.sfo",
    "param_cp_values.json",
    "pronunciation.sig",
    "origin-deltainfo.dat",
    "target-deltainfo.dat",
    "origin-param.json",
    "target-param.json",
    "origin-relocinfo.dat",
    "target-relocinfo.dat",
})
GENERATED_SCE_SYS_DIRECTORIES = frozenset({"about"})
GENERATED_ROOT_DIRECTORIES = frozenset({"sce_suppl", "sce_sc", "playgo-languages"})
EXCLUDED_ROOT_DIRECTORIES = {"decrypted": "host decryption artifacts"}
RESERVED_SCE_SYS_SUFFIXES = frozenset({".dds", ".auth_info"})
PROJECT_SUFFIXES = frozenset({".gp4", ".gp5", ".esbak"})
EXCLUDED_PATH_PATTERNS = {"*/saved/config/*/*.ini": "runtime artifacts"}
EXCLUDED_ROOT_FILES = {
    "ampr_emu.index": "host emulator index",
    # pfsdump writes these beside the dumped content.
    "entitlements.txt": "dumper entitlement record",
    "entitlement_key.dat": "dumper entitlement record",
}
EXCLUDED_FAKE_LIBRARIES = frozenset({"libsceampr.sprx", "libsceplaygo.sprx"})
GENERATED_ASSET_DIRECTORY = ".gp5-assets"
NORMALIZED_SELF_DIRECTORY = "normalized-self"
PROSPERO_SELF_MAGIC = b"\x54\x14\xf5\xee"
LEGACY_SELF_MAGIC = b"\x4f\x15\x3d\x1d"
MAX_SELF_METADATA_SIZE = 64 * 1024 * 1024
DEFAULT_PLAYGO_CHUNK_COUNT = 100
MAX_PLAYGO_CHUNK_COUNT = 800
PLAYGO_LANGUAGE_PAYLOAD_SIZE = 1024 * 1024
PLAYGO_LANGUAGES = (
    "ja-JP", "en-US", "fr-FR", "es-ES", "de-DE", "it-IT", "nl-NL", "pt-PT",
    "ru-RU", "ko-KR", "zh-Hant", "zh-Hans", "fi-FI", "sv-SE", "da-DK", "no-NO",
    "pl-PL", "pt-BR", "en-GB", "tr-TR", "es-419", "ar-AE", "fr-CA", "cs-CZ",
    "hu-HU", "el-GR", "ro-RO", "th-TH", "vi-VN", "id-ID", "uk-UA",
)
# Runtime API limit in the supported Prospero SDK (SCE_PLAYGO_MAX_SCENARIO).
MAX_PLAYGO_SCENARIO_COUNT = 5
ALL_LANGUAGES_MASK = (1 << 64) - 1
UNEXPECTED_FILES_SHOWN = 25
VOLUME_TYPES = {
    "app": "prospero_app",
    "patch": "prospero_patch",
    "ac": "prospero_ac",
    "al": "prospero_al",
}
DEFAULT_PUBLISHING_TOOLS = Path(
    r"C:\SCE\Prospero\Tools\Publishing Tools_2.7.9-plaintext-custom-keystone-v3\bin")
SUPPORTED_PATCH_PROFILES = frozenset({
    "sdk279-plaintext-direct-v6",
    "sdk313-plaintext-direct-v6",
})
CONTENT_ID_PATTERN = re.compile(
    r"^[A-Z]{2}[0-9]{4}-[A-Z]{4}[0-9]{5}_[0-9]{2}-[A-Z0-9]{16}$")
ENTITLEMENT_KEY_PATTERN = re.compile(r"^[0-9A-Fa-f]{32}$")
PSAL_PARAM_KEYS = (
    "conceptId", "contentId", "localizedParameters", "masterVersion", "titleId",
)
MIB = 1024 * 1024
GIB = 1024 * MIB
STANDARD_PACKAGE_LIMIT = 162_514_599_936
LARGE_PACKAGE_LV1_LIMIT = 195_878_191_104
LARGE_PACKAGE_LV2_LIMIT = 260_436_918_272
ADDCONT_MOUNT_LV2_LIMIT = 97_945_387_008
LARGE_PACKAGE_LV2_FILE_LIMIT = 500_000  # patched Publishing Tools profiles


# sce_sys/uds UCP containers: 0x60-byte header, one 0x40-byte entry per member,
# member data 16-byte aligned. Each NPWR id in npbind.dat maps to uds<NN>.ucp
# by index.
UCP_MAGIC = 0xB228C60A
NPBIND_MAGIC = 0xD294A018
UCP_HEADER, UCP_ENTRY, UCP_ALIGN = 0x60, 0x40, 0x10
UCP_MEMBER_ORDER = ("enum_definition.json", "events_definition.json",
                    "stats_definition.json", "stats_extraction.json")
UCP_ID_MEMBERS = ("enum_definition.json", "stats_definition.json",
                  "stats_extraction.json")
UCP_CONFIGURATION_ID = 54474


def choose_package_size(unpacked_bytes: int, file_count: int,
                        sdk_profile: str, mount_level: int = 0
                        ) -> tuple[int, int | None, int, int]:
    """Choose the smallest SDK large-package level from uncompressed GP5 inputs.

    This is a preflight estimate, not an exact prediction of the SDK's PFS layout.
    Include a bounded safety allowance for metadata, alignment and generated files.
    """
    if sdk_profile not in {"sdk279", "sdk313"}:
        raise ValueError(f"unsupported auto-size SDK profile: {sdk_profile}")
    if unpacked_bytes < 0 or file_count < 0:
        raise ValueError("unpacked size and file count must not be negative")
    if type(mount_level) is not int or mount_level not in {0, 1, 2}:
        raise ValueError(f"invalid kernel.addcontMountLevel: {mount_level!r}")
    allowance = max(512 * MIB, (unpacked_bytes + 99) // 100) + file_count * 4096
    estimated = unpacked_bytes + allowance
    # Preserve the highest mount level that can accommodate the estimated image.
    # The SDK's lv2 attribute does not lift the level-1 limit, and lv3 requires 0.
    selected_mount_level = mount_level
    if selected_mount_level == 2 and estimated > ADDCONT_MOUNT_LV2_LIMIT:
        selected_mount_level = 1
    if selected_mount_level == 1 and estimated > LARGE_PACKAGE_LV1_LIMIT:
        selected_mount_level = 0
    if selected_mount_level == 2:
        return 0, None, estimated, selected_mount_level
    if estimated <= STANDARD_PACKAGE_LIMIT:
        return 0, None, estimated, selected_mount_level
    if estimated <= LARGE_PACKAGE_LV1_LIMIT:
        return 1, None, estimated, selected_mount_level
    if estimated <= LARGE_PACKAGE_LV2_LIMIT:
        if file_count > LARGE_PACKAGE_LV2_FILE_LIMIT:
            raise ValueError(
                f"lv2 needs at most {LARGE_PACKAGE_LV2_FILE_LIMIT} files in the "
                f"patched SDK, but the GP5 has {file_count}")
        return 2, None, estimated, selected_mount_level
    if sdk_profile == "sdk279":
        # The estimate uses unpacked input sizes plus a conservative allowance.
        # Keep the largest profile available to this SDK and let img_create make
        # the authoritative decision after compression.
        if file_count > LARGE_PACKAGE_LV2_FILE_LIMIT:
            raise ValueError(
                f"lv2 needs at most {LARGE_PACKAGE_LV2_FILE_LIMIT} files in the "
                f"patched SDK, but the GP5 has {file_count}")
        return 2, None, estimated, selected_mount_level
    app_size_gib = max(256, (estimated + GIB - 1) // GIB)
    if app_size_gib > 320:
        # appSizeInGib itself is capped at 320. The unpacked-size estimate may
        # exceed it while the compressed package still fits.
        app_size_gib = 320
    return 4, app_size_gib, estimated, selected_mount_level


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


def is_complete_sceversion(records: bytes) -> bool:
    if not records:
        return False
    offset = 0
    count = 0
    while offset < len(records):
        if len(records) - offset < 5 or records[offset:offset + 2] != b"\0\0":
            return False
        payload_size = struct.unpack_from("<H", records, offset + 2)[0]
        record_size = payload_size + 4
        if (payload_size < 18 or record_size > len(records) - offset or
                records[offset + 4] != 8):
            return False
        name_size = payload_size - 17
        name = records[offset + 5:offset + 5 + name_size]
        version_offset = offset + 5 + name_size
        if (not name.endswith(b":") or
                any(byte < 0x20 or byte > 0x7e for byte in name) or
                records[version_offset:version_offset + 8] !=
                records[version_offset + 8:version_offset + 16]):
            return False
        offset += record_size
        count += 1
    return count != 0


def executable_sdk_version(path: Path) -> int | None:
    """Read the highest package SDK major/minor from a SELF .sceversion trailer."""
    size = path.stat().st_size
    if size < 0x20:
        return None
    with path.open("rb") as stream:
        header = stream.read(0x20)
        if header[:4] not in {PROSPERO_SELF_MAGIC, LEGACY_SELF_MAGIC}:
            return None
        boundary = struct.unpack_from("<Q", header, 0x10)[0]
        if not 0x20 <= boundary <= size:
            return None
        start = max(0, boundary - 0x0f)
        if not 0 < size - start <= MAX_SELF_METADATA_SIZE:
            return None
        stream.seek(start)
        tail = stream.read()
    local_boundary = boundary - start
    for backtrack in range(min(0x0f, local_boundary) + 1):
        records = tail[local_boundary - backtrack:]
        if not is_complete_sceversion(records):
            continue
        versions = []
        offset = 0
        while offset < len(records):
            payload_size = struct.unpack_from("<H", records, offset + 2)[0]
            name_size = payload_size - 17
            version_offset = offset + 5 + name_size
            versions.append(int.from_bytes(
                records[version_offset:version_offset + 8], "big"))
            offset += payload_size + 4
        return max(versions) & 0xFFFF000000000000
    return None


def self_repair_plan(path: Path) -> tuple[bool, int, int] | None:
    """Return (rewrite_magic, trailer_offset, padding) for a repairable SELF."""
    size = path.stat().st_size
    if size < 0x20:
        return None
    with path.open("rb") as stream:
        header = stream.read(0x20)
        if header[:4] not in {PROSPERO_SELF_MAGIC, LEGACY_SELF_MAGIC}:
            return None
        rewrite_magic = header[:4] == LEGACY_SELF_MAGIC
        boundary = struct.unpack_from("<Q", header, 0x10)[0]
        if not 0x20 <= boundary <= size:
            return (rewrite_magic, size, 0) if rewrite_magic else None
        start = max(0, boundary - 0x0f)
        tail_length = size - start
        if not 0 < tail_length <= MAX_SELF_METADATA_SIZE:
            return (rewrite_magic, size, 0) if rewrite_magic else None
        stream.seek(start)
        tail = stream.read(tail_length)
    local_boundary = boundary - start
    for backtrack in range(min(0x0f, local_boundary) + 1):
        candidate_offset = local_boundary - backtrack
        if is_complete_sceversion(tail[candidate_offset:]):
            if rewrite_magic or backtrack:
                return rewrite_magic, boundary - backtrack, backtrack
            return None
    return (rewrite_magic, size, 0) if rewrite_magic else None


def write_repaired_self(
    source: Path, destination: Path, plan: tuple[bool, int, int],
) -> None:
    rewrite_magic, trailer_offset, padding = plan
    if destination.exists() or destination.is_symlink():
        raise FileExistsError(f"generated normalized SELF already exists: {destination}")
    destination.parent.mkdir(parents=True, exist_ok=True)
    with source.open("rb") as input_stream, destination.open("wb") as output_stream:
        remaining = trailer_offset
        position = 0
        while remaining:
            block = input_stream.read(min(1 << 20, remaining))
            if not block:
                raise EOFError(f"unexpected EOF while normalizing {source}")
            if rewrite_magic and position == 0:
                block = PROSPERO_SELF_MAGIC + block[4:]
            output_stream.write(block)
            position += len(block)
            remaining -= len(block)
        if padding:
            output_stream.write(bytes(padding))
        shutil.copyfileobj(input_stream, output_stream, length=1 << 20)
    shutil.copystat(source, destination)


def prepare_executable_inputs(
    root: Path, files: list[Path], generated_root: Path,
) -> dict[Path, Path]:
    replacements: dict[Path, Path] = {}
    for source in files:
        plan = self_repair_plan(source)
        if plan is None:
            continue
        relative = relative_posix(root, source)
        destination = generated_root / NORMALIZED_SELF_DIRECTORY / Path(relative)
        rewrite_magic, _, padding = plan
        repairs = []
        if rewrite_magic:
            repairs.append("Prospero magic")
        if padding:
            repairs.append(f".sceversion +{padding}-byte alignment")
        print(f"Repairing SELF {relative} ({', '.join(repairs)})...")
        write_repaired_self(source, destination, plan)
        replacements[source] = destination
    return replacements


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
                if item.parent == root and item.name.casefold() in EXCLUDED_ROOT_DIRECTORIES:
                    reason = EXCLUDED_ROOT_DIRECTORIES[item.name.casefold()]
                    skipped.append(relative + f"/ ({reason})")
                    continue
                if (item.parent == root and
                        item.name.casefold() == GENERATED_ASSET_DIRECTORY.casefold()):
                    skipped.append(relative + "/ (generated GP5 assets)")
                    continue
                if (item.parent == root and
                        item.name.casefold() in GENERATED_ROOT_DIRECTORIES):
                    skipped.append(relative + "/ (reserved/SDK-generated directory)")
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
            pattern = next((pattern for pattern in EXCLUDED_PATH_PATTERNS
                            if fnmatch.fnmatchcase(normalized, pattern)), None)
            if pattern is not None:
                skipped.append(relative + f" ({EXCLUDED_PATH_PATTERNS[pattern]})")
                continue
            if normalized in EXCLUDED_ROOT_FILES:
                skipped.append(relative + f" ({EXCLUDED_ROOT_FILES[normalized]})")
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


def addcont_mount_level(value: dict, source: Path) -> int:
    kernel = value.get("kernel", {})
    if not isinstance(kernel, dict):
        raise ValueError(f"{source} kernel must be a JSON object")
    level = kernel.get("addcontMountLevel", 0)
    if type(level) is not int or level not in {0, 1, 2}:
        raise ValueError(
            f"{source} kernel.addcontMountLevel must be 0, 1 or 2, "
            f"got {level!r}")
    return level


def read_addcont_mount_level(source: Path) -> int:
    try:
        value = json.loads(source.read_text(encoding="utf-8-sig"))
    except (OSError, UnicodeDecodeError, json.JSONDecodeError) as error:
        raise ValueError(f"cannot parse {source}: {error}") from error
    if not isinstance(value, dict):
        raise ValueError(f"{source} must contain a JSON object")
    return addcont_mount_level(value, source)


def normalize_entitlement_key(volume: str, value: str | None) -> str | None:
    if volume in {"ac", "al"} and value is None:
        raise ValueError(f"--entitlement-key is required for --volume {volume}")
    if value is None:
        return None
    if volume not in {"ac", "al"}:
        raise ValueError("--entitlement-key is valid only for --volume ac or al")
    if ENTITLEMENT_KEY_PATTERN.fullmatch(value) is None:
        raise ValueError("entitlement key must contain exactly 32 hexadecimal characters")
    return value.upper()


def parse_sdk_version_override(value: str | None) -> int | None:
    if value is None:
        return None
    if re.fullmatch(r"(?:[1-9][0-9]{0,2})(?:\.00)?", value):
        major = int(value.split(".", 1)[0])
        if major > 255:
            raise ValueError("SDK version major must be 1..255")
        return major << 56
    if re.fullmatch(r"0x[0-9a-fA-F]{16}", value):
        package_version = int(value, 16) & 0xFFFF000000000000
        if package_version:
            return package_version
    raise ValueError("SDK version must be 1..255, MAJOR.00, or 0x plus 16 hex digits")


def write_standard_param(
    source: Path, destination: Path, volume: str,
    size_selection: tuple[int, int | None, int, int] | None = None,
    inferred_sdk_version: int | None = None,
    sdk_version_override: int | None = None,
) -> None:
    try:
        value = json.loads(source.read_text(encoding="utf-8-sig"))
    except (OSError, UnicodeDecodeError, json.JSONDecodeError) as error:
        raise ValueError(f"cannot parse {source}: {error}") from error
    if not isinstance(value, dict):
        raise ValueError(f"{source} must contain a JSON object")
    effective_sdk_version = (sdk_version_override if sdk_version_override is not None
                             else inferred_sdk_version if volume == "app" else None)
    if effective_sdk_version:
        def parse_version(name: str) -> int:
            raw = value.get(name, "0x0000000000000000")
            if not isinstance(raw, str) or re.fullmatch(r"0x[0-9a-fA-F]{16}", raw) is None:
                raise ValueError(f"{source}: invalid {name}: {raw!r}")
            return int(raw, 16)

        current_sdk = (parse_version("sdkVersion") if sdk_version_override is None
                       else sdk_version_override)
        if sdk_version_override is not None or current_sdk == 0:
            current_sdk = effective_sdk_version
            value["sdkVersion"] = f"0x{current_sdk:016X}"
        required = parse_version("requiredSystemSoftwareVersion")
        if required < current_sdk:
            value["requiredSystemSoftwareVersion"] = f"0x{current_sdk:016X}"
    if volume == "al":
        missing = [key for key in PSAL_PARAM_KEYS if key not in value]
        if missing:
            raise ValueError(
                f"{source} is missing required PSAL field(s): {', '.join(missing)}")
        value = {key: value[key] for key in PSAL_PARAM_KEYS}
    elif volume == "ac":
        value.pop("applicationDrmType", None)
        value.pop("applicationCategoryType", None)
    else:
        value["applicationDrmType"] = "standard"
        if size_selection is not None:
            if value.get("applicationCategoryType") != 0:
                raise ValueError(
                    "automatic large-package selection requires a native game "
                    "(applicationCategoryType: 0)")
            attribute, app_size_gib, _, selected_mount_level = size_selection
            value["attributePub"] = attribute
            original_mount_level = addcont_mount_level(value, source)
            kernel = value.get("kernel")
            if selected_mount_level != original_mount_level:
                if kernel is None:
                    kernel = {}
                    value["kernel"] = kernel
                kernel["addcontMountLevel"] = selected_mount_level
            if app_size_gib is not None:
                if kernel is None:
                    kernel = {}
                    value["kernel"] = kernel
                if selected_mount_level != 0:
                    raise ValueError(
                        "lv3 requires kernel.addcontMountLevel to be absent or 0")
                kernel["appSizeInGib"] = app_size_gib
            elif kernel is not None:
                kernel.pop("appSizeInGib", None)
            if ((selected_mount_level == 1 and attribute not in {0, 1}) or
                    (selected_mount_level == 2 and attribute != 0)):
                raise ValueError(
                    f"attributePub={attribute} is incompatible with "
                    f"kernel.addcontMountLevel={selected_mount_level}")
    destination.parent.mkdir(parents=True, exist_ok=True)
    destination.write_text(
        json.dumps(value, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")


def validate_psal_icon(path: Path) -> None:
    data = path.read_bytes()[:33]
    if (len(data) < 33 or data[:8] != b"\x89PNG\r\n\x1a\n" or
            data[12:16] != b"IHDR"):
        raise ValueError(f"PSAL icon is not a valid PNG: {path}")
    width, height = struct.unpack_from(">II", data, 16)
    bit_depth, color_type = data[24], data[25]
    if (width, height, bit_depth, color_type) != (512, 512, 8, 2):
        raise ValueError(
            "PSAL sce_sys/icon0.png must be 512x512, 8-bit RGB without alpha "
            f"(got {width}x{height}, bit depth {bit_depth}, color type {color_type})")


def sce_sys_dds_images(root: Path) -> list[tuple[Path, str]]:
    system = root / "sce_sys"
    if not system.is_dir():
        return []
    images: list[tuple[Path, str]] = []
    destination_names: set[str] = set()
    for dds in sorted(system.iterdir(), key=lambda item: item.name.casefold()):
        if not dds.is_file() or dds.suffix.casefold() != ".dds":
            continue
        png_name = dds.with_suffix(".png").name
        if png_name.casefold() in destination_names:
            raise ValueError(f"multiple sce_sys DDS files map to {png_name}")
        destination_names.add(png_name.casefold())
        images.append((dds, png_name))
    return images


def required_png_color_type(name: str) -> int | None:
    stem = Path(name).stem.casefold()
    if re.fullmatch(r"(?:icon0|pic0|pic1)(?:_[0-9]{2})?", stem):
        return 2  # 8-bit RGB
    if re.fullmatch(r"pic2(?:_[0-9]{2})?", stem):
        return 6  # 8-bit RGBA
    return None


def png_color_type(path: Path) -> int | None:
    if not path.is_file():
        return None
    with path.open("rb") as source:
        header = source.read(26)
    if (len(header) < 26 or header[:8] != b"\x89PNG\r\n\x1a\n" or
            header[8:12] != b"\x00\x00\x00\x0d" or
            header[12:16] != b"IHDR" or header[24] != 8):
        return None
    return header[25]


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
        "sce_sys contains DDS images, but no prospero-dds2png converter was found; "
        "rebuild the toolkit or use --dds-converter")


def convert_dds_images(
    images: list[tuple[Path, str]], generated_system: Path,
    converter_path: Path | None,
) -> list[tuple[str, Path]]:
    if not images:
        return []
    converter = find_dds_converter(converter_path)
    converted: list[tuple[str, Path]] = []
    for source, png_name in images:
        destination = generated_system / png_name
        if destination.exists() or destination.is_symlink():
            raise FileExistsError(f"generated PNG already exists: {destination}")
        destination.parent.mkdir(parents=True, exist_ok=True)
        required_color = required_png_color_type(png_name)
        preserve_alpha = required_color is None or required_color == 6
        mode = "RGBA" if preserve_alpha else "RGB"
        print(f"Converting sce_sys/{source.name} to {png_name} ({mode})...")
        suffix = converter.suffix.casefold()
        if suffix == ".py":
            command = [sys.executable, str(converter)]
        elif suffix == ".exe" and os.name != "nt":
            command = ["wine", str(converter)]
        else:
            command = [str(converter)]
        command.extend([str(source), str(destination)])
        if preserve_alpha:
            command.append("--preserve-alpha")
        completed = subprocess.run(
            command, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
            text=True, encoding="utf-8", errors="replace", timeout=300)
        if completed.returncode != 0:
            raise RuntimeError(
                f"DDS converter failed for {source} with exit code "
                f"{completed.returncode}:\n{completed.stdout.rstrip()}")
        if png_color_type(destination) != (6 if preserve_alpha else 2):
            raise ValueError(
                f"DDS converter did not create an 8-bit {mode} PNG: {destination}")
        converted.append((f"sce_sys/{png_name}", destination))
    return converted


def ucp_member_documents(npwr: str) -> dict[str, dict[str, object]]:
    base = {"schemaVersion": "1.1", "npCommunicationId": npwr}
    return {
        "enum_definition.json": {**base, "enumDefinitionRevision": 1, "enumArray": []},
        "events_definition.json": {"configurationId": UCP_CONFIGURATION_ID, "events": [],
                                   "nextOffset": 0, "previousOffset": 0, "totalItemCount": 0},
        "stats_definition.json": {**base, "statsDefinitionRevision": 1, "statDefinitionArray": []},
        "stats_extraction.json": {**base, "statsExtractionRevision": 1,
                                  "statsExtractionRuleArray": []},
    }


def ucp_digest(data: bytes) -> bytes:
    zeroed = bytearray(data)
    zeroed[0x1C:0x30] = bytes(20)
    return hashlib.sha1(zeroed).digest()


def pack_ucp(members: list[tuple[str, bytes]]) -> bytes:
    """UCP bytes with a valid header digest from [(name, bytes)] members."""
    table_end = UCP_HEADER + UCP_ENTRY * len(members)
    offsets, at = [], table_end
    for _, body in members:
        offsets.append(at)
        # Real files add a full 16 bytes after members that are already aligned.
        at += len(body) + (UCP_ALIGN - len(body) % UCP_ALIGN)
    out = bytearray(at)
    struct.pack_into(">IIQIII", out, 0, UCP_MAGIC, 1, at, len(members), UCP_ENTRY, 0)
    for i, ((name, body), off) in enumerate(zip(members, offsets)):
        entry = UCP_HEADER + i * UCP_ENTRY
        out[entry:entry + len(name)] = name.encode()
        struct.pack_into(">QQ", out, entry + 0x20, off, len(body))
        out[off:off + len(body)] = body
    out[0x1C:0x30] = hashlib.sha1(out).digest()
    return bytes(out)


def unpack_ucp(data: bytes) -> list[tuple[str, bytes]]:
    """Parse the container, raising ValueError on structural problems."""
    if len(data) < UCP_HEADER:
        raise ValueError(f"{len(data)} bytes, shorter than the {UCP_HEADER:#x}-byte header")
    magic, version, size = struct.unpack_from(">IIQ", data, 0)
    count, entry_size = struct.unpack_from(">II", data, 0x10)
    if magic != UCP_MAGIC:
        raise ValueError(f"bad magic {magic:#010x}")
    if version != 1:
        raise ValueError(f"unsupported version {version}")
    if size != len(data):
        raise ValueError(f"size field {size} != file size {len(data)}")
    if entry_size != UCP_ENTRY:
        raise ValueError(f"entry size {entry_size:#x}, expected {UCP_ENTRY:#x}")
    table_end = UCP_HEADER + count * UCP_ENTRY
    if table_end > len(data):
        raise ValueError(f"{count} entries do not fit in the file")
    members = []
    seen: set[str] = set()
    for i in range(count):
        entry = UCP_HEADER + i * UCP_ENTRY
        name = data[entry:entry + 0x20].split(b"\0")[0].decode("latin1")
        if not name or name in seen or "/" in name or "\\" in name or name in {".", ".."}:
            raise ValueError(f"member {i} has an empty, duplicate or unsafe name: {name!r}")
        seen.add(name)
        off, length = struct.unpack_from(">QQ", data, entry + 0x20)
        if off < table_end or off > len(data) or length > len(data) - off:
            raise ValueError(f"member {name!r} runs past the end of the file")
        members.append((name, data[off:off + length]))
    return members


def ucp_problems(data: bytes, npwr: str) -> list[str]:
    """Return a list of problems; empty means the UCP is acceptable."""
    try:
        members = unpack_ucp(data)
    except ValueError as error:
        return [str(error)]
    problems = []
    if ucp_digest(data) != data[0x1C:0x30]:
        problems.append(f"digest is {data[0x1C:0x30].hex()}, should be {ucp_digest(data).hex()}")
    names = [name for name, _ in members]
    missing = [name for name in UCP_MEMBER_ORDER if name not in names]
    if missing:
        problems.append("missing members: " + ", ".join(missing))
    for name, body in members:
        if name not in UCP_ID_MEMBERS:
            continue
        try:
            found = json.loads(body).get("npCommunicationId")
        except (ValueError, AttributeError) as error:
            problems.append(f"{name} is not valid JSON ({error})")
            continue
        if found != npwr:
            problems.append(f"{name} npCommunicationId is {found!r}, npbind says {npwr!r}")
    return problems


def read_npbind_ids(path: Path) -> list[str]:
    """NPWR ids in npbind.dat, in index order."""
    data = path.read_bytes()
    if len(data) != 0x214 or struct.unpack_from(">I", data, 0)[0] != NPBIND_MAGIC:
        raise ValueError(f"{path}: not an npbind.dat")
    if (struct.unpack_from(">I", data, 4)[0] != 1 or
            struct.unpack_from(">I", data, 0x0C)[0] != len(data) or
            hashlib.sha1(data[:0x200]).digest() != data[0x200:0x214]):
        raise ValueError(f"{path}: invalid version, size or SHA-1 authentication")
    entry_size, = struct.unpack_from(">I", data, 0x14)
    count, = struct.unpack_from(">I", data, 0x1C)
    if entry_size < 4 or 0x80 + count * entry_size > 0x200:
        raise ValueError(f"{path}: {count} entries of {entry_size:#x} bytes do not fit")
    ids = []
    for i in range(count):
        base = 0x80 + i * entry_size
        at, end = base, base + entry_size
        found = False
        while at + 4 <= end:
            tag, length = struct.unpack_from(">HH", data, at)
            if tag == 0:
                break
            if at + 4 + length > end:
                raise ValueError(f"{path}: entry {i} has a truncated TLV record")
            if tag == 0x10:
                npwr = data[at + 4:at + 4 + length].split(b"\0")[0].decode("ascii")
                if not re.fullmatch(r"NPWR[0-9]{5}_[0-9]{2}", npwr):
                    raise ValueError(f"{path}: entry {i} has an invalid NPWR id: {npwr!r}")
                ids.append(npwr)
                found = True
                break
            at += 4 + length
        if not found:
            raise ValueError(f"{path}: entry {i} has no NPWR id")
    return ids


def prepare_ucp_stubs(
    root: Path, files: list[Path], generated_system: Path,
) -> tuple[dict[Path, Path], list[tuple[str, Path]]]:
    """Generated repairs or stubs for missing or invalid sce_sys/uds UCPs.

    Returns (replacements, additions): replacements maps a source UCP to its
    repaired file or stub; additions holds (dst_path, stub) pairs for missing
    UCPs. The source tree is never modified.
    """
    system = root / "sce_sys"
    npbind = system / "uds" / "npbind.dat"
    if not npbind.is_file():
        return {}, []
    try:
        ids = read_npbind_ids(npbind)
    except (OSError, ValueError, UnicodeDecodeError) as error:
        print(f"[Warn] {error}; UCP containers are packaged as they are")
        return {}, []
    sources = {relative_posix(root, file).casefold(): file for file in files}
    replacements: dict[Path, Path] = {}
    additions: list[tuple[str, Path]] = []
    for index, npwr in enumerate(ids):
        relative = f"sce_sys/uds/uds{index:02d}.ucp"
        source = sources.get(relative.casefold())
        if source is None:
            reason = "missing"
        else:
            original = source.read_bytes()
            problems = ucp_problems(original, npwr)
            if not problems:
                continue
            if len(problems) == 1 and problems[0].startswith("digest is "):
                repaired = bytearray(original)
                repaired[0x1C:0x30] = ucp_digest(original)
                destination = generated_system / "uds" / f"uds{index:02d}.ucp"
                if destination.exists() or destination.is_symlink():
                    raise FileExistsError(f"generated UCP repair already exists: {destination}")
                destination.parent.mkdir(parents=True, exist_ok=True)
                destination.write_bytes(repaired)
                replacements[source] = destination
                print(f"UCP {relative} has a stale digest; repaired the digest without changing members")
                continue
            reason = "invalid: " + "; ".join(problems)
        documents = ucp_member_documents(npwr)
        stub = pack_ucp([(name, json.dumps(documents[name], separators=(", ", ": ")).encode())
                         for name in UCP_MEMBER_ORDER])
        destination = generated_system / "uds" / f"uds{index:02d}.ucp"
        if destination.exists() or destination.is_symlink():
            raise FileExistsError(f"generated UCP stub already exists: {destination}")
        destination.parent.mkdir(parents=True, exist_ok=True)
        destination.write_bytes(stub)
        print(f"[Warn] UCP {relative} is {reason}; GP5 uses an empty stub for {npwr} "
              f"({len(stub)} bytes); source is unchanged")
        if source is not None:
            replacements[source] = destination
        else:
            additions.append((relative, destination))
    return replacements, additions


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


def read_source_chunk_count(root: Path, fallback: int, announce: bool = True) -> int:
    """Read only the chunk count from a validated PS5 PlayGo descriptor header."""
    source = root / "sce_sys" / "playgo-chunk.dat"
    if not source.exists():
        return fallback
    with source.open("rb") as stream:
        header = stream.read(0x18)
        size = os.fstat(stream.fileno()).st_size
    if (len(header) != 0x18 or header[:4] != b"plgx" or
            struct.unpack_from("<HH", header, 4) != (0x1000, 0) or
            size < 0x100 or struct.unpack_from("<I", header, 0x10)[0] != size):
        raise ValueError(f"{source}: invalid PS5 playgo-chunk.dat header")
    chunks = struct.unpack_from("<H", header, 0x0a)[0]
    scenarios = struct.unpack_from("<H", header, 0x0e)[0]
    if (not 1 <= chunks <= MAX_PLAYGO_CHUNK_COUNT or
            not 1 <= scenarios <= MAX_PLAYGO_SCENARIO_COUNT):
        raise ValueError(
            f"{source}: unsupported PlayGo counts: {chunks} chunks, {scenarios} scenarios")
    if announce:
        print(f"PlayGo: using {chunks} chunks from {source}")
    return chunks


PLAYGO_FLT_MAGIC = b"\x7fFLT"
# Known inputs intentionally excluded or regenerated by Publishing Tools.
PLAYGO_SDK_GENERATED_PATHS = (
    "sce_sys/about/right.sprx",
    *(f"sce_sys/{name}" for name in GENERATED_SCE_SYS_FILES
      if name != "playgo-scenario.json"),
    *EXCLUDED_ROOT_FILES,
    *(f"fakelib/{name}" for name in EXCLUDED_FAKE_LIBRARIES),
)
PLAYGO_HASH_MIX = 0x8000000080008081
MASK64 = (1 << 64) - 1


def _rol64(value: int, count: int) -> int:
    return ((value << count) | (value >> (64 - count))) & MASK64


def _playgo_mix(a: int, b: int, c: int) -> tuple[int, int, int]:
    x = _rol64(_rol64(c ^ b, 5) ^ a, 53)
    y = _rol64(_rol64(c ^ a, 17) ^ b, 11)
    z = _rol64(_rol64(b ^ a, 1) ^ c, 59)
    return (((~y & z) ^ x ^ PLAYGO_HASH_MIX) & MASK64,
            ((~z & x) ^ y) & MASK64, ((~x & y) ^ z) & MASK64)


def playgo_path_hash(path: str, seed: int, key: int) -> int:
    """Hash an image-relative path (no leading slash) as playgo-hash-table.dat stores it.

    seed and key are read from that file's header.
    """
    data = path.encode("utf-8").upper()
    a, b, c = seed, _rol64(seed, 11), _rol64(seed, 23)
    tail = 0
    if data:
        blocks, rest = divmod(len(data) - 1, 8)
        for index in range(blocks):
            a ^= struct.unpack_from("<Q", data, index * 8)[0]
            a, b, c = _playgo_mix(a, b, c)
        tail = int.from_bytes(data[blocks * 8:blocks * 8 + rest + 1], "little")
    return _playgo_mix(tail ^ a ^ key, b, c)[0]


class SourcePlayGoLayout:
    """Chunk labels and languages, and each file's chunk keyed by path hash."""

    def __init__(self, chunks: list[tuple[str, list[str]]], file_chunks: dict[int, int],
                 seed: int, key: int,
                 scenarios: list[tuple[str, int, list[int]]] | None = None):
        self.chunks = chunks
        self.file_chunks = file_chunks
        self.seed, self.key = seed, key
        self.scenarios = scenarios

    @property
    def languages(self) -> list[str]:
        present = {language for _label, languages in self.chunks for language in languages}
        return [language for language in PLAYGO_LANGUAGES if language in present]

    def hash_of(self, path: str) -> int:
        return playgo_path_hash(path, self.seed, self.key)

    def chunk_of(self, path: str) -> int | None:
        return self.file_chunks.get(self.hash_of(path))

    def missing_by_chunk(self, placed: set[int]) -> dict[int, tuple[int, int]]:
        """{chunk: (missing, expected)} for chunks whose mapped files are not all placed."""
        generated = {self.hash_of(path) for path in PLAYGO_SDK_GENERATED_PATHS}
        expected: dict[int, int] = {}
        missing: dict[int, int] = {}
        for path_hash, chunk_id in self.file_chunks.items():
            if path_hash in generated:
                continue
            expected[chunk_id] = expected.get(chunk_id, 0) + 1
            if path_hash not in placed:
                missing[chunk_id] = missing.get(chunk_id, 0) + 1
        return {chunk_id: (count, expected[chunk_id]) for chunk_id, count in missing.items()}


def read_source_playgo_layout(
    root: Path, chunk_count: int | None = None,
) -> SourcePlayGoLayout | None:
    """Read the layout from sce_sys/playgo-{chunk,hash-table,ficm}.dat.

    Returns None when any of the three is missing, or when several chunks exist
    but every file is in chunk 0. A single-chunk source still has a real layout.
    The ficm holds one chunk per entry of the sorted hash table.
    """
    system = root / "sce_sys"
    chunk_path = system / "playgo-chunk.dat"
    hash_path = system / "playgo-hash-table.dat"
    ficm_path = system / "playgo-ficm.dat"
    if not (chunk_path.is_file() and hash_path.is_file() and ficm_path.is_file()):
        return None
    if chunk_count is None:
        chunk_count = read_source_chunk_count(root, DEFAULT_PLAYGO_CHUNK_COUNT, announce=False)
    # Validate fixed headers and lengths before reading any variable-size tables.
    # Stream paired entries instead of materializing two large integer tuples.
    file_chunks: dict[int, int] = {}
    with hash_path.open("rb") as hash_stream, ficm_path.open("rb") as ficm_stream:
        hash_header = hash_stream.read(0x38)
        if (len(hash_header) != 0x38 or
                struct.unpack_from("<I", hash_header, 0)[0] != 1 or
                hash_header[0x18:0x1c] != PLAYGO_FLT_MAGIC or
                struct.unpack_from("<I", hash_header, 0x08)[0] != 0x38):
            raise ValueError(f"{hash_path}: unrecognized header")
        file_count = struct.unpack_from("<I", hash_header, 0x24)[0]
        seed, key = struct.unpack_from("<QQ", hash_header, 0x28)
        if (struct.unpack_from("<I", hash_header, 0x0c)[0] != file_count * 8 or
                os.fstat(hash_stream.fileno()).st_size != 0x38 + file_count * 8):
            raise ValueError(f"{hash_path}: size does not match {file_count} entries")
        ficm_header = ficm_stream.read(0x10)
        if (len(ficm_header) != 0x10 or
                struct.unpack_from("<I", ficm_header, 0)[0] != 1 or
                struct.unpack_from("<I", ficm_header, 0x08)[0] != 0x10 or
                struct.unpack_from("<I", ficm_header, 0x0c)[0] != file_count * 2 or
                os.fstat(ficm_stream.fileno()).st_size != 0x10 + file_count * 2):
            raise ValueError(f"{ficm_path}: does not describe {file_count} files")
        previous = -1
        for start in range(0, file_count, 8192):
            count = min(8192, file_count - start)
            hash_block, ficm_block = hash_stream.read(count * 8), ficm_stream.read(count * 2)
            if len(hash_block) != count * 8 or len(ficm_block) != count * 2:
                raise ValueError(f"{hash_path} / {ficm_path}: truncated file map")
            for (path_hash,), (chunk_id,) in zip(
                    struct.iter_unpack("<Q", hash_block), struct.iter_unpack("<H", ficm_block)):
                if path_hash <= previous:
                    raise ValueError(f"{hash_path}: hashes must be unique and strictly increasing")
                if chunk_id >= chunk_count:
                    raise ValueError(f"{ficm_path}: invalid chunk id/reserved byte: {chunk_id}")
                previous = path_hash
                file_chunks[path_hash] = chunk_id
    if chunk_count > 1 and not any(file_chunks.values()):
        return None

    # Only the chunk attributes and labels are needed, not the extent/scenario tables.
    with chunk_path.open("rb") as chunk_stream:
        header = chunk_stream.read(0x100)
        size = os.fstat(chunk_stream.fileno()).st_size
        if (len(header) != 0x100 or header[:4] != b"plgx" or
                struct.unpack_from("<HH", header, 4) != (0x1000, 0) or
                struct.unpack_from("<I", header, 0x10)[0] != size or
                struct.unpack_from("<H", header, 0x0a)[0] != chunk_count):
            raise ValueError(f"{chunk_path}: truncated or changed descriptor")
        attrs_offset, attrs_size = struct.unpack_from("<II", header, 0xc0)
        labels_offset, labels_size = struct.unpack_from("<II", header, 0xd0)
        if (attrs_offset < 0x100 or attrs_size != chunk_count * 0x20 or
                attrs_offset + attrs_size > size):
            raise ValueError(f"{chunk_path}: chunk attribute table does not match {chunk_count} chunks")
        if (labels_offset < 0x100 or labels_offset + labels_size > size or
                max(attrs_offset, labels_offset) < min(attrs_offset + attrs_size,
                                                       labels_offset + labels_size)):
            raise ValueError(f"{chunk_path}: invalid or overlapping chunk label table")
        chunk_stream.seek(attrs_offset)
        attributes = chunk_stream.read(attrs_size)
        chunk_stream.seek(labels_offset)
        labels = chunk_stream.read(labels_size)
        if len(attributes) != attrs_size or len(labels) != labels_size:
            raise ValueError(f"{chunk_path}: truncated chunk attributes or labels")
    chunks: list[tuple[str, list[str]]] = []
    for chunk_id in range(chunk_count):
        entry = chunk_id * 0x20
        # Bit 63 is ja-JP, then down through PLAYGO_LANGUAGES in order.
        mask = struct.unpack_from("<Q", attributes, entry + 0x10)[0]
        label_at = struct.unpack_from("<I", attributes, entry + 0x1c)[0]
        if label_at >= len(labels):
            raise ValueError(f"{chunk_path}: chunk {chunk_id} label offset is out of bounds")
        end = labels.find(b"\0", label_at)
        if end < 0:
            raise ValueError(f"{chunk_path}: chunk {chunk_id} label is not null-terminated")
        try:
            label = labels[label_at:end].decode("utf-8")
        except UnicodeDecodeError as error:
            raise ValueError(f"{chunk_path}: chunk {chunk_id} label is not UTF-8") from error
        if any((ord(char) < 0x20 and char not in "\t\n\r") or
               0xd800 <= ord(char) <= 0xdfff or ord(char) in {0xfffe, 0xffff} for char in label):
            raise ValueError(f"{chunk_path}: chunk {chunk_id} label contains invalid XML characters")
        languages = [language for bit, language in enumerate(PLAYGO_LANGUAGES)
                     if mask >> (63 - bit) & 1]
        known_mask = sum(1 << (63 - bit) for bit in range(len(PLAYGO_LANGUAGES)))
        if mask & ~known_mask and mask != ALL_LANGUAGES_MASK:
            print(f"[Warn] PlayGo chunk {chunk_id} in {chunk_path} sets unrecognized language bits "
                  f"(mask {mask:#x}); the chunk is emitted for its known languages only.")
        chunks.append((label or (f"Chunk #{chunk_id}" if languages else ""), languages))

    return SourcePlayGoLayout(chunks, file_chunks, seed, key,
                              read_source_scenarios(chunk_path, chunk_count))


def read_source_scenarios(
    chunk_path: Path, chunk_count: int,
) -> list[tuple[str, int, list[int]]] | None:
    """Read source scenario labels, initial counts and ordered chunk lists."""
    data = chunk_path.read_bytes()
    scenario_count = struct.unpack_from("<H", data, 0x0e)[0]
    table_at, table_size = struct.unpack_from("<II", data, 0xe0)
    lists_at, lists_size = struct.unpack_from("<II", data, 0xe8)
    names_at, names_size = struct.unpack_from("<II", data, 0xf0)
    try:
        if (table_size != scenario_count * 0x20 or
                min(table_at, lists_at, names_at) < 0x100 or
                max(table_at + table_size, lists_at + lists_size, names_at + names_size) > len(data)):
            raise ValueError("scenario sections do not match the header")
        lists = data[lists_at:lists_at + lists_size]
        names = data[names_at:names_at + names_size]
        scenarios = []
        for index in range(scenario_count):
            entry = table_at + index * 0x20
            initial, total = struct.unpack_from("<HH", data, entry + 0x14)
            list_at, name_at = struct.unpack_from("<II", data, entry + 0x18)
            if (list_at + total * 2 > len(lists) or initial > total or
                    name_at >= len(names) or b"\0" not in names[name_at:]):
                raise ValueError(f"scenario {index} points outside its sections")
            ids = list(struct.unpack_from(f"<{total}H", lists, list_at))
            if any(chunk_id >= chunk_count for chunk_id in ids) or len(set(ids)) != total:
                raise ValueError(f"scenario {index} has an invalid chunk list")
            label = names[name_at:names.index(b"\0", name_at)].decode("utf-8")
            if any((ord(char) < 0x20 and char not in "\t\n\r") or
                   0xd800 <= ord(char) <= 0xdfff or ord(char) in {0xfffe, 0xffff}
                   for char in label):
                raise ValueError(f"scenario {index} label contains invalid XML characters")
            scenarios.append((label, initial, ids))
    except (ValueError, struct.error, UnicodeDecodeError) as error:
        print(f"[Warn] PlayGo: ignoring the scenario tables in {chunk_path}: {error}; "
              "every scenario will list all chunks.")
        return None
    return scenarios


def chunk_ranges(ids: list[int]) -> str:
    """Collapse ascending runs of chunk IDs while keeping source order."""
    parts: list[str] = []
    start = previous = None
    for chunk_id in ids:
        if start is not None and chunk_id == previous + 1:
            previous = chunk_id
            continue
        if start is not None:
            parts.append(str(start) if start == previous else f"{start}-{previous}")
        start = previous = chunk_id
    if start is not None:
        parts.append(str(start) if start == previous else f"{start}-{previous}")
    return " ".join(parts)


def parse_playgo_languages(value: str) -> list[str]:
    codes = {code.casefold(): code for code in PLAYGO_LANGUAGES}
    requested = [item.strip().casefold() for item in value.split(",")]
    if not requested or any(item not in codes for item in requested):
        raise ValueError("PlayGo languages must be a non-empty comma-separated list of supported codes")
    if len(set(requested)) != len(requested):
        raise ValueError("PlayGo languages must not contain duplicates")
    return [codes[item] for item in requested]


def write_scenario(
    path: Path, source: Path, fallback_language: str,
    selected_languages: list[str] | None = None,
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

    if selected_languages is not None:
        original_default = value.get("chunkDefaultLanguage", language)
        canonical_default = (language_codes.get(original_default.strip().casefold())
                             if isinstance(original_default, str) else None)
        value["chunkSupportedLanguages"] = selected_languages
        value["chunkDefaultLanguage"] = (
            canonical_default if canonical_default in selected_languages else
            "en-US" if "en-US" in selected_languages else selected_languages[0])

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
    path.write_text(
        json.dumps(value, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    return (count, default_id, sorted(definitions),
            chunk_default_language, chunk_languages)


def write_language_payloads(
    generated_root: Path, languages: list[str], chunk_count: int,
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
        result.append((destination, payload, min(index, chunk_count - 1)))
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
    entitlement_key: str | None = None,
    auto_size_profile: str | None = None,
    chunk_count: int = DEFAULT_PLAYGO_CHUNK_COUNT,
    playgo_languages: str | None = None,
    sdk_version_override: int | None = None,
) -> tuple[int, list[str]]:
    if len(passcode) != 32:
        raise ValueError("passcode must contain exactly 32 characters")
    if volume != "app" and sdk_version_override is not None:
        raise ValueError("--sdk-version supports APP volumes only")
    if not root.is_dir():
        raise NotADirectoryError(root)
    if output.exists() or output.is_symlink():
        raise FileExistsError(f"output already exists: {output}")
    entitlement_key = normalize_entitlement_key(volume, entitlement_key)
    if playgo_languages is not None and volume != "app":
        raise ValueError("PlayGo language selection supports APP volumes only")
    selected_languages = (parse_playgo_languages(playgo_languages)
                          if playgo_languages is not None else None)
    if auto_size_profile is not None and volume != "app":
        raise ValueError("automatic package-size selection supports APP volumes only")
    if type(chunk_count) is not int or not 1 <= chunk_count <= MAX_PLAYGO_CHUNK_COUNT:
        raise ValueError(f"PlayGo chunk count must be 1..{MAX_PLAYGO_CHUNK_COUNT}")
    if volume != "app" and chunk_count != DEFAULT_PLAYGO_CHUNK_COUNT:
        raise ValueError("custom PlayGo chunk count supports APP volumes only")
    layout = None
    if volume == "app":
        chunk_count = read_source_chunk_count(root, chunk_count)
        if selected_languages is not None:
            print("PlayGo: --playgo-languages given; replacing the source chunk layout "
                  "with generated chunks")
        else:
            layout = read_source_playgo_layout(root, chunk_count)
        if layout is not None:
            chunk_count = len(layout.chunks)
            # The chunk masks can list more languages than playgo-scenario.json.
            selected_languages = layout.languages
    is_psal = volume == "al"
    has_scenario = volume not in {"ac", "al"}
    scenario_input = output.with_suffix(".playgo-scenario.json")
    if has_scenario and (scenario_input.exists() or scenario_input.is_symlink()):
        raise FileExistsError(f"generated PlayGo scenario already exists: {scenario_input}")
    output.parent.mkdir(parents=True, exist_ok=True)

    files, skipped = collect_files(root, output, keep_service_paths)
    param = root / "sce_sys" / "param.json"
    if param not in files:
        reason = "excluded as a service artifact" if param.exists() else "missing"
        raise ValueError(f"required sce_sys/param.json is {reason}")
    package_content_id = content_id(param)
    # PSAL permits only its original RGB icon0.png. Other volume types use a
    # DDS only when its PNG counterpart is absent or has the wrong color mode.
    dds_images = [] if is_psal else sce_sys_dds_images(root)
    source_pngs = {relative_posix(root, file).casefold(): file for file in files}
    needed_conversions: list[tuple[Path, str]] = []
    replaced_pngs: set[Path] = set()
    for dds, png_name in dds_images:
        original = source_pngs.get(f"sce_sys/{png_name}".casefold())
        required_color = required_png_color_type(png_name)
        if original is not None and (required_color is None or
                                     png_color_type(original) == required_color):
            continue
        if original is not None:
            replaced_pngs.add(original)
            skipped.append(relative_posix(root, original) +
                           " (invalid PNG color mode; replaced from DDS)")
        needed_conversions.append((dds, png_name))
    if replaced_pngs:
        files = [file for file in files if file not in replaced_pngs]
    for file in files:
        relative = relative_posix(root, file)
        if (relative.casefold().startswith("sce_sys/") and
                relative.count("/") == 1 and file.suffix.casefold() == ".png"):
            required_color = required_png_color_type(file.name)
            if required_color is not None and png_color_type(file) != required_color:
                mode = "RGB" if required_color == 2 else "RGBA"
                raise ValueError(f"{relative} must be an 8-bit {mode} PNG; "
                                 "no matching DDS is available to repair it")
    if is_psal:
        allowed = {"sce_sys/param.json", "sce_sys/icon0.png"}
        selected: list[Path] = []
        for file in files:
            relative = relative_posix(root, file)
            if relative.casefold() in allowed:
                selected.append(file)
            else:
                skipped.append(relative + " (PSAL contains metadata only)")
        files = selected
        icon = next(
            (file for file in files
             if relative_posix(root, file).casefold() == "sce_sys/icon0.png"), None)
        if icon is None:
            raise ValueError("--volume al requires sce_sys/icon0.png")
        validate_psal_icon(icon)
        scenario_count = scenario_default_id = 0
        scenario_definitions = []
        chunk_default_language = ""
        chunk_languages = []
    elif volume == "ac":
        scenario_count = scenario_default_id = 0
        scenario_definitions = []
        chunk_default_language = ""
        chunk_languages = []
    else:
        (scenario_count, scenario_default_id, scenario_definitions,
         chunk_default_language, chunk_languages) = write_scenario(
            scenario_input, root / "sce_sys" / "playgo-scenario.json",
            default_language(param), selected_languages)
    generated_root = output.parent / GENERATED_ASSET_DIRECTORY / output.stem
    generated_system = generated_root / "sce_sys"
    generated_param = generated_system / "param.json"
    if generated_param.exists() or generated_param.is_symlink():
        raise FileExistsError(f"generated param.json already exists: {generated_param}")
    converted_pngs = convert_dds_images(
        needed_conversions, generated_system, converter_path)
    executable_replacements = ({} if is_psal else
                               prepare_executable_inputs(root, files, generated_root))
    ucp_replacements: dict[Path, Path] = {}
    generated_ucps: list[tuple[str, Path]] = []
    if volume in {"app", "patch"}:
        ucp_replacements, generated_ucps = prepare_ucp_stubs(root, files, generated_system)
    language_payloads = (write_language_payloads(generated_root, chunk_languages, chunk_count)
                         if volume == "app" and layout is None else [])
    size_selection = None
    if auto_size_profile is not None:
        mapped_files = [ucp_replacements.get(file, executable_replacements.get(file, file))
                        for file in files if file != param]
        mapped_files.extend(path for _, path in converted_pngs)
        mapped_files.extend(path for _, path in generated_ucps)
        mapped_files.extend(path for _, path, _ in language_payloads)
        mapped_files.append(scenario_input)
        # The normalized param is metadata, not game payload. Its small size is
        # covered by the allowance; all other mapped files use their actual size.
        unpacked_bytes = sum(path.stat().st_size for path in mapped_files)
        file_count = len(mapped_files) + 1
        mount_level = read_addcont_mount_level(param)
        size_selection = choose_package_size(
            unpacked_bytes, file_count, auto_size_profile, mount_level)
        attribute, app_size_gib, estimated, selected_mount_level = size_selection
        detail = (f", appSizeInGib={app_size_gib}" if app_size_gib else "")
        if selected_mount_level != mount_level:
            print(f"[Warn] Adjusting kernel.addcontMountLevel from {mount_level} "
                  f"to {selected_mount_level} in the generated param.json; "
                  "the source file is unchanged.")
        profile_limit = (LARGE_PACKAGE_LV2_LIMIT if auto_size_profile == "sdk279"
                         else 320 * GIB)
        if estimated > profile_limit:
            maximum = ("attributePub=2 (lv2)" if auto_size_profile == "sdk279"
                       else "attributePub=4 (lv3), appSizeInGib=320")
            print(f"[Warn] Conservative estimate {estimated} bytes exceeds the "
                  f"maximum declared size for {auto_size_profile}: "
                  f"{profile_limit} bytes. Continuing with {maximum}; "
                  "img_create will apply the final limit after compression.")
        print(f"Unpacked GP5 inputs: {unpacked_bytes} bytes, {file_count} files; "
              f"conservative estimate: {estimated} bytes; "
              f"addcontMountLevel={selected_mount_level}; "
              f"attributePub={attribute}{detail}")
    eboot = next((file for file in files
                  if relative_posix(root, file).casefold() == "eboot.bin"), None)
    inferred_sdk = (executable_sdk_version(executable_replacements.get(eboot, eboot))
                    if eboot else None)
    write_standard_param(
        param, generated_param, volume, size_selection, inferred_sdk,
        sdk_version_override)

    project = ET.Element("psproject", {"fmt": "gp5"})
    volume_node = ET.SubElement(project, "volume")
    ET.SubElement(volume_node, "volume_type").text = VOLUME_TYPES[volume]
    package = ET.SubElement(volume_node, "package", {"passcode": passcode})
    if is_psal:
        package.set("entitlement_key", entitlement_key)
    else:
        package.set("content_id", package_content_id)
        if entitlement_key is not None:
            package.set("entitlement_key", entitlement_key)
    if volume == "app":
        chunk_info = ET.SubElement(volume_node, "chunk_info", {
            "chunk_count": str(chunk_count),
            "scenario_count": str(scenario_count)})
        chunks = ET.SubElement(chunk_info, "chunks", {
            "supported_languages": " ".join(chunk_languages),
            "default_language": chunk_default_language,
        })
        for chunk_id in range(chunk_count):
            if layout is not None:
                label, assigned_languages = layout.chunks[chunk_id]
            else:
                label = f"Chunk #{chunk_id}"
                assigned_languages = [language for index, language in
                                      enumerate(chunk_languages, start=1)
                                      if min(index, chunk_count - 1) == chunk_id]
            if layout is not None and not label and not assigned_languages:
                continue
            attributes = {
                "id": str(chunk_id),
                "label": label,
                "layer_no": "0",
                "languages": (" ".join(assigned_languages) if assigned_languages
                              else " ".join(chunk_languages)),
            }
            ET.SubElement(chunks, "chunk", attributes)
        scenarios = ET.SubElement(
            chunk_info, "scenarios", {"default_id": str(scenario_default_id)})
        source_scenarios = layout.scenarios if layout is not None else None
        if source_scenarios is not None and len(source_scenarios) != len(scenario_definitions):
            print(f"[Warn] PlayGo: the source has {len(source_scenarios)} scenarios but "
                  f"playgo-scenario.json declares {len(scenario_definitions)}; every scenario "
                  "will list all chunks.")
            source_scenarios = None
        for scenario_id, scenario_type, label in scenario_definitions:
            if source_scenarios is not None:
                label, initial, chunk_ids = source_scenarios[scenario_id]
                text = chunk_ranges(chunk_ids)
            else:
                initial = chunk_count
                text = f"0-{chunk_count - 1}" if chunk_count > 1 else "0"
            scenario = ET.SubElement(scenarios, "scenario", {
                "id": str(scenario_id),
                "type": scenario_type,
                "initial_chunk_count": str(initial),
                "label": label,
            })
            scenario.text = text

    files_node = ET.SubElement(project, "files")
    unmapped_count = 0
    unmapped_sample: list[str] = []
    unexpected_unmapped: list[str] = []
    placed: set[int] = set()

    def mapped_chunk(destination: str) -> str:
        nonlocal unmapped_count
        if layout is None:
            return "0"
        candidates = [destination]
        # The SDK turns presentation PNG inputs into DDS package nodes, so the
        # original file map commonly contains the DDS name rather than the PNG.
        normalized = destination.casefold()
        if normalized.startswith("sce_sys/") and normalized.endswith(".png"):
            candidates.append(destination[:-4] + ".dds")
        for candidate in candidates:
            path_hash = layout.hash_of(candidate)
            chunk_id = layout.file_chunks.get(path_hash)
            if chunk_id is not None:
                placed.add(path_hash)
                return str(chunk_id)
        unmapped_count += 1
        if len(unmapped_sample) < 5:
            unmapped_sample.append(destination)
        if not normalized.startswith("sce_sys/"):
            unexpected_unmapped.append(destination)
        return "0"

    if has_scenario:
        ET.SubElement(files_node, "file", {
            "dst_path": "sce_sys/playgo-scenario.json",
            "src_path": source_path(output.parent, scenario_input, absolute_paths),
            "chunk": mapped_chunk("sce_sys/playgo-scenario.json"),
        })
    for destination, converted in converted_pngs:
        ET.SubElement(files_node, "file", {
            "dst_path": destination,
            "src_path": source_path(output.parent, converted, absolute_paths),
            "chunk": mapped_chunk(destination),
        })
    for destination, stub in generated_ucps:
        ET.SubElement(files_node, "file", {
            "dst_path": destination,
            "src_path": source_path(output.parent, stub, absolute_paths),
            "chunk": mapped_chunk(destination),
        })
    for destination, payload, chunk_id in language_payloads:
        ET.SubElement(files_node, "file", {
            "dst_path": destination,
            "src_path": source_path(output.parent, payload, absolute_paths),
            "chunk": str(chunk_id),
            "pfs_compression": "disable",
        })
    for file in files:
        actual_source = (generated_param if file == param else
                         ucp_replacements.get(file, executable_replacements.get(file, file)))
        attributes = {
            "dst_path": relative_posix(root, file),
            "src_path": source_path(output.parent, actual_source, absolute_paths),
        }
        if not is_psal:
            attributes["chunk"] = mapped_chunk(attributes["dst_path"])
        ET.SubElement(files_node, "file", attributes)
    if layout is not None:
        print(f"PlayGo: kept the source layout ({chunk_count} chunks, languages "
              f"{' '.join(chunk_languages)}); {len(files_node) - unmapped_count} of "
              f"{len(files_node)} files placed from playgo-ficm.dat")
        if unmapped_count:
            shown = ", ".join(unmapped_sample) + (", ..." if unmapped_count > 5 else "")
            print(f"PlayGo: {unmapped_count} file(s) not in the source map went to chunk 0: {shown}")
        if unexpected_unmapped:
            print(f"[Warn] PlayGo: {len(unexpected_unmapped)} file(s) outside sce_sys are not in the "
                  "retail file map and were added to chunk 0:")
            for path in unexpected_unmapped[:UNEXPECTED_FILES_SHOWN]:
                print(f"  {path}")
            if len(unexpected_unmapped) > UNEXPECTED_FILES_SHOWN:
                print(f"  ... and {len(unexpected_unmapped) - UNEXPECTED_FILES_SHOWN} more")
        incomplete = layout.missing_by_chunk(placed)
        for chunk_id, (count, expected) in sorted(incomplete.items()):
            label, languages = layout.chunks[chunk_id]
            print(f"[Warn] PlayGo chunk {chunk_id} ({label}; {' '.join(languages)}) is missing "
                  f"{count} of its {expected} file(s) in the source")
        if incomplete:
            print("[Warn] The package still declares these chunks. Installing a language "
                  "that uses them gives the game an incomplete data set.")
    indent(project)
    ET.ElementTree(project).write(output, encoding="utf-8", xml_declaration=True)
    return (len(files) + len(converted_pngs) + len(generated_ucps) + len(language_payloads) +
            (1 if has_scenario else 0)), skipped


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
    chunk_count: int = DEFAULT_PLAYGO_CHUNK_COUNT,
    entitlement_key: str | None = None,
    playgo_languages: str | None = None,
    sdk_version_override: int | None = None,
) -> tuple[Path, int, list[str]]:
    if volume not in {"app", "ac"}:
        raise ValueError("--build supports debug APP/AC nwonly profiles")
    entitlement_key = normalize_entitlement_key(volume, entitlement_key)
    if playgo_languages is not None:
        if volume != "app":
            raise ValueError("PlayGo language selection supports APP volumes only")
        parse_playgo_languages(playgo_languages)
    if volume == "app" and "sce_sys/keystone" not in keep_service_paths:
        raise ValueError(
            "the custom-keystone build profile requires a 96-byte source "
            "sce_sys/keystone")
    if destination.exists() or destination.is_symlink():
        raise FileExistsError(f"build destination already exists: {destination}")
    manifest_path = publishing_tools / "patch-manifest.json"
    toolchain_profile = json.loads(manifest_path.read_text(encoding="utf-8")).get("profile")
    if toolchain_profile not in SUPPORTED_PATCH_PROFILES:
        raise ValueError(f"unsupported patched SDK profile in {manifest_path}")
    auto_size_profile = toolchain_profile[:6] if volume == "app" else None
    destination.mkdir(parents=True)
    project_path = destination / "project.gp5"
    # Bundle GP5 paths are relative to the bundle. They still resolve to the caller-owned source
    # tree, which avoids silently duplicating a potentially multi-gigabyte game directory.
    count, skipped = build_gp5(
        root, project_path, volume, passcode, absolute_paths=False,
        keep_service_paths=keep_service_paths, converter_path=converter_path,
        entitlement_key=entitlement_key, auto_size_profile=auto_size_profile,
        chunk_count=chunk_count, playgo_languages=playgo_languages,
        sdk_version_override=sdk_version_override)
    chunk_info = ET.parse(project_path).getroot().find("volume/chunk_info")
    chunk_count = int(chunk_info.attrib["chunk_count"]) if chunk_info is not None else 0

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
        "profile": toolchain_profile.replace("-v6", "-bundle-v6"),
        "toolchain_profile": toolchain_profile,
        "source": str(root),
        "project": "project.gp5",
        "passcode": passcode,
        "volume": volume,
        "sdk_version_override": (f"0x{sdk_version_override:016X}"
                                 if sdk_version_override is not None else None),
        "chunk_count": chunk_count,
        "playgo_languages": (chunk_info.find("chunks").attrib["supported_languages"].split()
                             if chunk_info is not None else []),
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
        "not duplicated. The patched SDK writes the final plaintext/no-auth representation "
        "with the GP5 SDK version directly; the temporary filename is only atomically "
        "renamed. Run "
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
    parser.add_argument("--auto-size-profile", choices=("sdk279", "sdk313"),
                        help="select attributePub from unpacked inputs for this SDK")
    parser.add_argument("--chunk-count", type=int, default=DEFAULT_PLAYGO_CHUNK_COUNT,
                        metavar="1..800",
                        help="fallback APP PlayGo chunk count without source "
                             "sce_sys/playgo-chunk.dat (default: 100)")
    parser.add_argument("--passcode", default="0" * 32,
                        help="32-character package passcode (default: 32 zeroes)")
    parser.add_argument("--sdk-version", metavar="MAJOR[.00]|0xHEX16",
                        help="override sdkVersion in generated param.json; also raises the "
                             "required system version when needed")
    parser.add_argument("--playgo-languages", metavar="CODE,CODE",
                        help="override APP PlayGo chunk languages, e.g. en-US,ru-RU; "
                             "otherwise retain source languages")
    parser.add_argument(
        "--entitlement-key",
        help="16-byte hex entitlement key (required for ac and al)")
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
    entitlement_key = normalize_entitlement_key(args.volume, args.entitlement_key)
    sdk_version_override = parse_sdk_version_override(args.sdk_version)
    keep = {path.replace("\\", "/").lstrip("/").casefold() for path in args.keep_sce_sys}
    # APP bundles preserve the source keystone. AC does not carry the APP helper;
    # GP5-only keystone retention remains opt-in.
    keep_keystone = args.keep_keystone or (args.build and args.volume == "app")
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
        if args.auto_size_profile is not None:
            raise ValueError("--build detects its SDK profile automatically")
        package, count, skipped = build_bundle(
            root, output, args.volume, args.passcode, keep,
            args.publishing_tools.resolve(),
            args.dds_converter.resolve() if args.dds_converter else None,
            args.chunk_count, entitlement_key, args.playgo_languages,
            sdk_version_override)
        print(f"Created build bundle {output} with {count} explicit file mapping(s).")
        print(f"Final LibProsperoPkg-compatible package: {package}")
    else:
        if not output.name.casefold().endswith(".gp5"):
            raise ValueError("output must be a .gp5 file unless --build is specified")
        count, skipped = build_gp5(
            root, output, args.volume, args.passcode, args.absolute_paths, keep,
            args.dds_converter.resolve() if args.dds_converter else None,
            entitlement_key, args.auto_size_profile, args.chunk_count,
            args.playgo_languages, sdk_version_override)
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
