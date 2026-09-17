"""Khảo sát CHỈ ĐỌC mọi thư mục game PS5 trên một ổ (mặc định G:\\) cho PSVIETHOA FPKG Builder 2.2.0.

    python scripts/research/survey-games.py --root G:\\ --out docs/research [--chunkmap-dir C:\\fpkg-research\\json] [--only PPSA26344]

- Chỉ mở tệp nguồn ở chế độ 'rb', chỉ liệt kê thư mục (os.scandir), không đổi thuộc tính/thời gian.
- Mọi đường dẫn GHI (--out, --chunkmap-dir) bị từ chối nếu nằm trên ổ được bảo vệ (--protect, mặc định G:\\).
- Bảng PlayGo đọc bằng scripts/research/playgo.py (cùng bố cục với SonySdkPlayGo.cs); với game nhiều chunk, ánh xạ tệp → chunk
  (hash đường dẫn PS5) tính bằng công cụ C# scripts/research/research.cs (dùng đúng mã của ứng dụng) nếu có --chunkmap-dir.
"""
from __future__ import annotations

import argparse
import json
import os
import re
import subprocess
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import playgo  # noqa: E402

SKIP_TOP = {"data", "dump", "homebrew", "itemzflow", "elf-arsenal_config", "fpkg-temp", "system volume information", "$recycle.bin",
            "ps5", "ps5_autoloader", "voidshell_config", ".spotlight-v100", ".trashes", ".temporaryitems", ".fseventsd"}
SPECIAL_CHARS = set("&'\"<>#%![];,=")
JUNK_NAMES = {".ds_store", "thumbs.db", "desktop.ini"}
KNOWN_SCE_SYS = {
    "param.json", "keystone", "pfs-version.dat", "license.dat", "license.info", "imagedigs.dat", "about", "icon0.png", "icon0.dds",
    "pic0.png", "pic0.dds", "pic1.png", "pic1.dds", "pic2.png", "pic2.dds", "nptitle.dat", "npbind.dat", "playgo-chunk.dat",
    "playgo-ficm.dat", "playgo-hash-table.dat", "playgo-scenario.json", "playgo-manifest.xml", "trophy2", "uds", "cp", "snd0.at9",
    "save_data.png", "disc_info.dat", "lem_notification.dat", "shareparam.json", "changeinfo", "ext_info.dat", "trophy",
}
PARAM_KEYS = ["titleId", "contentId", "contentVersion", "masterVersion", "applicationCategoryType", "applicationDrmType", "attribute",
              "attribute2", "attribute3", "sdkVersion", "requiredSystemSoftwareVersion", "versionFileUri", "originContentVersion",
              "targetContentVersion", "downloadDataSize", "pubtools", "kernel", "addcont", "userDefinedParam1"]
CONTENT_ID = re.compile(r"^[A-Z]{2}[0-9]{4}-[A-Z]{4}[0-9]{5}_[0-9]{2}-[A-Z0-9]{16}$")


def guard(path: str, protected: list[str]) -> str:
    full = os.path.abspath(path)
    for root in protected:
        if full.lower().startswith(os.path.abspath(root).lower()):
            raise SystemExit(f"REFUSED: write path {full} is on protected drive {root}")
    return full


def read_bytes(path: str, limit: int | None = None) -> bytes | None:
    try:
        with open(path, "rb") as stream:
            return stream.read() if limit is None else stream.read(limit)
    except OSError:
        return None


def find_app_root(folder: str) -> str | None:
    if os.path.isfile(os.path.join(folder, "sce_sys", "param.json")):
        return folder
    try:
        for entry in os.scandir(folder):
            if entry.is_dir(follow_symlinks=False) and os.path.isfile(os.path.join(entry.path, "sce_sys", "param.json")):
                return entry.path
    except OSError:
        pass
    return None


def walk(root: str) -> dict:
    stats = {"files": 0, "dirs": 0, "bytes": 0, "zero_byte": 0, "over_4gib": 0, "largest": [0, ""], "max_depth": 0,
             "max_rel_len": [0, ""], "reparse": [], "non_ascii": [], "special": [], "case_dupes": [], "trailing": [],
             "junk": [], "gp5_assets": False, "project_files": [], "errors": []}
    stack = [("", 0)]
    while stack:
        rel_dir, depth = stack.pop()
        path = os.path.join(root, rel_dir) if rel_dir else root
        try:
            entries = list(os.scandir(path))
        except OSError as error:
            stats["errors"].append(f"{rel_dir}: {error}")
            continue
        folded: dict[str, str] = {}
        for entry in entries:
            rel = f"{rel_dir}/{entry.name}" if rel_dir else entry.name
            key = entry.name.casefold()
            if key in folded:
                stats["case_dupes"].append(f"{rel} ~ {folded[key]}")
            folded[key] = rel
            if len(rel) > stats["max_rel_len"][0]:
                stats["max_rel_len"] = [len(rel), rel]
            if any(ord(c) > 127 for c in entry.name):
                stats["non_ascii"].append(rel)
            if SPECIAL_CHARS & set(entry.name):
                stats["special"].append(rel)
            if entry.name != entry.name.strip() or entry.name.endswith("."):
                stats["trailing"].append(rel)
            if key in JUNK_NAMES or entry.name.startswith("._"):
                stats["junk"].append(rel)
            try:
                if entry.is_symlink() or (hasattr(entry, "is_junction") and entry.is_junction()):
                    stats["reparse"].append(rel)
                    continue
                if entry.is_dir(follow_symlinks=False):
                    stats["dirs"] += 1
                    if not rel_dir and key == ".gp5-assets":
                        stats["gp5_assets"] = True
                    stack.append((rel, depth + 1))
                    stats["max_depth"] = max(stats["max_depth"], depth + 1)
                    continue
                size = entry.stat(follow_symlinks=False).st_size
            except OSError as error:
                stats["errors"].append(f"{rel}: {error}")
                continue
            stats["files"] += 1
            stats["bytes"] += size
            if size == 0:
                stats["zero_byte"] += 1
            if size >= 4 * 1024 ** 3:
                stats["over_4gib"] += 1
            if size > stats["largest"][0]:
                stats["largest"] = [size, rel]
            if key.endswith((".gp4", ".gp5", ".esbak")) or key.endswith(".playgo-scenario.json"):
                stats["project_files"].append(rel)
    for name in ("reparse", "non_ascii", "special", "case_dupes", "trailing", "junk", "project_files", "errors"):
        stats[name + "_count"] = len(stats[name])
        stats[name] = stats[name][:25]
    return stats


def sce_sys_info(app: str) -> dict:
    sce = os.path.join(app, "sce_sys")
    info: dict = {"entries": [], "unknown": []}
    try:
        for entry in os.scandir(sce):
            info["entries"].append(entry.name + ("/" if entry.is_dir() else ""))
            if entry.name.lower() not in KNOWN_SCE_SYS:
                info["unknown"].append(entry.name)
    except OSError as error:
        info["error"] = str(error)
    names = {n.lower().rstrip("/") for n in info["entries"]}
    key = os.path.join(sce, "keystone")
    info["keystone_size"] = os.path.getsize(key) if os.path.isfile(key) else None
    info["keystone_ok"] = info["keystone_size"] == 96
    for name in ("pfs-version.dat", "license.dat", "license.info", "imagedigs.dat", "nptitle.dat", "playgo-manifest.xml", "icon0.png",
                 "pic0.png", "pic1.png", "pic2.png", "playgo-scenario.json", "disc_info.dat"):
        info[name] = name in names
    info["dds"] = sorted(n for n in names if n.endswith(".dds"))
    info["missing_png_with_dds"] = sorted(f"{n[:-4]}.png" for n in info["dds"] if n.startswith("pic") and f"{n[:-4]}.png" not in names)
    info["missing_png_without_dds"] = sorted(p for p in ("pic0.png", "pic1.png", "pic2.png") if p not in names and p[:-4] + ".dds" not in names)
    info["about_right_sprx"] = os.path.isfile(os.path.join(sce, "about", "right.sprx"))
    info["npbind"] = [d for d in ("trophy2", "uds", "trophy") if os.path.isfile(os.path.join(sce, d, "npbind.dat"))]
    for folder in ("trophy2", "uds", "cp", "changeinfo"):
        info[folder + "_dir"] = os.path.isdir(os.path.join(sce, folder))
    return info


def param_info(app: str) -> dict:
    raw = read_bytes(os.path.join(app, "sce_sys", "param.json"))
    if raw is None:
        return {"error": "missing"}
    try:
        data = json.loads(raw.decode("utf-8-sig"))
    except (UnicodeDecodeError, json.JSONDecodeError) as error:
        return {"error": f"invalid: {error}"}
    out: dict = {"keys": sorted(data.keys())}
    for key in PARAM_KEYS:
        if key in data:
            value = data[key]
            if key == "versionFileUri":
                value = "(empty)" if not str(value).strip() else "(set)"
            out[key] = value
    localized = data.get("localizedParameters") or {}
    default = localized.get("defaultLanguage")
    out["defaultLanguage"] = default
    out["languages"] = sorted(k for k in localized if k != "defaultLanguage")
    title = (localized.get(default) or {}).get("titleName") if isinstance(localized.get(default), dict) else None
    out["titleName"] = title
    out["contentId_valid"] = bool(isinstance(data.get("contentId"), str) and CONTENT_ID.match(data["contentId"]))
    out["unusual_keys"] = sorted(k for k in data if k not in PARAM_KEYS and k not in ("localizedParameters",))
    return out


def playgo_info(app: str) -> dict:
    sce = os.path.join(app, "sce_sys")
    chunk = read_bytes(os.path.join(sce, "playgo-chunk.dat"))
    if chunk is None:
        return {"present": False, "class": "no-playgo"}
    parsed = playgo.parse_chunk(chunk)
    table = playgo.parse_ficm_hash(read_bytes(os.path.join(sce, "playgo-ficm.dat")), read_bytes(os.path.join(sce, "playgo-hash-table.dat")))
    table.pop("_mapping", None)
    parsed["present"] = True
    parsed["file_table"] = table
    for c in parsed.get("chunks", []):
        if c["all_languages"]:
            c["languages"] = "*"
    scenario = read_bytes(os.path.join(sce, "playgo-scenario.json"))
    if scenario is not None:
        try:
            parsed["scenario_json"] = json.loads(scenario.decode("utf-8-sig"))
        except (UnicodeDecodeError, json.JSONDecodeError) as error:
            parsed["scenario_json_error"] = str(error)
    return parsed


def emu_info(app: str) -> dict:
    out: dict = {}
    out["ampr_emu_index"] = os.path.isfile(os.path.join(app, "ampr_emu.index"))
    for folder in ("fakelib", "fakelib2", "sce_module"):
        path = os.path.join(app, folder)
        out[folder] = sorted(os.listdir(path)) if os.path.isdir(path) else None
    out["dlc_emu"] = sorted(n for n in os.listdir(app) if n.lower().startswith("dlc_emu"))
    out["root_files"] = sorted(e.name for e in os.scandir(app) if e.is_file())
    readme = next((os.path.join(app, n) for n in out["root_files"] if n.lower() == "readme.txt"), None)
    if readme:
        text = (read_bytes(readme, 2000) or b"").decode("utf-8", "replace")
        out["readme_head"] = text.strip().splitlines()[:6]
    return out


def chunkmap(app: str, out_dir: str, game_id: str, repo: Path) -> dict | None:
    target = os.path.join(out_dir, f"chunkmap-{game_id}.json")
    if not os.path.isfile(target):
        command = ["dotnet", "run", str(repo / "scripts" / "research" / "research.cs"), "--", "chunkmap", app, target]
        completed = subprocess.run(command, cwd=repo, capture_output=True, text=True, encoding="utf-8", errors="replace")
        if completed.returncode != 0 or not os.path.isfile(target):
            return {"error": (completed.stderr or completed.stdout)[-2000:]}
    data = json.load(open(target, encoding="utf-8"))
    return {k: data.get(k) for k in ("filesOnDisk", "filesMatchedInTable", "filesNotInTable", "tableEntriesWithoutFile",
                                      "tableEntriesWithoutFilePerChunk", "perChunk")}


def game_id(name: str, param: dict) -> str:
    title = param.get("titleId") if isinstance(param.get("titleId"), str) else None
    if title:
        return title
    match = re.search(r"(PPSA|CUSA)\d{5}", name)
    return match.group(0) if match else re.sub(r"[^A-Za-z0-9]+", "_", name)[:40]


def voice_languages(pg: dict, cm: dict | None) -> list[str]:
    """Chunk ngôn ngữ có dữ liệu thật (không phải tệp giả 1 tệp) theo chunkmap."""
    if not cm or not cm.get("perChunk"):
        return []
    labels = {c["id"]: c for c in pg.get("chunks", [])}
    result = []
    for entry in cm["perChunk"]:
        chunk = labels.get(entry["chunk"])
        if chunk and chunk["languages"] != "*" and entry["bytes"] > 1024 * 1024:
            result.append(f"{entry['label']} [{chunk['languages'] if isinstance(chunk['languages'], str) else ' '.join(chunk['languages'])}] {entry['files']} tệp {entry['bytes'] / 1e9:.2f} GB")
        elif chunk and chunk["languages"] == "*" and entry["chunk"] != 0 and entry["bytes"] > 1024 * 1024:
            result.append(f"{entry['label']} [mọi ngôn ngữ] {entry['files']} tệp {entry['bytes'] / 1e9:.2f} GB")
    return result


def anomalies(g: dict) -> list[str]:
    notes = []
    w, s, p, pg, e = g["walk"], g["sce_sys"], g["param"], g["playgo"], g["emu"]
    if not s.get("keystone_ok"):
        notes.append(f"keystone không hợp lệ (kích thước {s.get('keystone_size')}) — SDK không build được")
    if p.get("error"):
        notes.append("param.json: " + p["error"])
    elif not p.get("contentId_valid"):
        notes.append(f"contentId không hợp lệ: {p.get('contentId')}")
    if g["name_non_ascii"]:
        notes.append("tên thư mục có ký tự ngoài ASCII → cần bí danh ASCII (junction)")
    if w["non_ascii_count"]:
        notes.append(f"{w['non_ascii_count']} tên tệp/thư mục ngoài ASCII (ví dụ {w['non_ascii'][:2]})")
    if w["special_count"]:
        notes.append(f"{w['special_count']} tên có ký tự đặc biệt XML/shell (ví dụ {w['special'][:2]})")
    if w["trailing_count"]:
        notes.append(f"{w['trailing_count']} tên có khoảng trắng đầu/cuối hoặc dấu chấm cuối: {w['trailing'][:3]}")
    if w["reparse_count"]:
        notes.append(f"{w['reparse_count']} symlink/junction (SDK mode từ chối): {w['reparse'][:3]}")
    if w["case_dupes_count"]:
        notes.append(f"{w['case_dupes_count']} tên trùng khi bỏ hoa/thường")
    if w["over_4gib"]:
        notes.append(f"{w['over_4gib']} tệp ≥ 4 GiB (lớn nhất {w['largest'][0] / 1024 ** 3:.1f} GiB: {w['largest'][1]})")
    if w["max_rel_len"][0] > 180:
        notes.append(f"đường dẫn tương đối dài {w['max_rel_len'][0]} ký tự")
    if w["files"] > 150_000:
        notes.append(f"{w['files']} tệp (> 150 000)")
    if w["zero_byte"]:
        notes.append(f"{w['zero_byte']} tệp 0 byte")
    if w["junk_count"]:
        notes.append(f"{w['junk_count']} tệp rác hệ điều hành ({w['junk'][:3]})")
    if w["gp5_assets"] or w["project_files_count"]:
        notes.append(f"tàn dư lượt build cũ trong nguồn: .gp5-assets={w['gp5_assets']}, dự án {w['project_files'][:3]}")
    if w["errors_count"]:
        notes.append(f"{w['errors_count']} lỗi đọc: {w['errors'][:2]}")
    if s.get("missing_png_without_dds"):
        notes.append(f"thiếu {s['missing_png_without_dds']} và không có .dds để khôi phục")
    if s.get("missing_png_with_dds"):
        notes.append(f"thiếu {s['missing_png_with_dds']} (khôi phục được từ .dds)")
    if s.get("unknown"):
        notes.append(f"sce_sys có mục lạ: {s['unknown']}")
    if s.get("playgo-manifest.xml"):
        notes.append("có sce_sys/playgo-manifest.xml")
    if pg.get("problems"):
        notes.append(f"playgo-chunk.dat: {pg['problems']}")
    if pg.get("file_table", {}).get("problems") and pg.get("present"):
        notes.append(f"bảng ficm/hash: {pg['file_table']['problems']}")
    cm = g.get("chunkmap") or {}
    if cm.get("tableEntriesWithoutFile"):
        notes.append(f"DUMP THIẾU: {cm['tableEntriesWithoutFile']} tệp có trong bảng PlayGo gốc nhưng không có trên đĩa (theo chunk: {cm.get('tableEntriesWithoutFilePerChunk')})")
    if e.get("dlc_emu"):
        notes.append(f"bộ giả lập DLC: {e['dlc_emu']}")
    if p.get("addcont"):
        notes.append("param.json có addcont (DLC/serviceIdForSharing)")
    if s.get("npbind"):
        pass
    return notes


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--root", default="G:\\")
    parser.add_argument("--out", default="docs/research")
    parser.add_argument("--chunkmap-dir", default=None)
    parser.add_argument("--protect", default="G:\\")
    parser.add_argument("--only", default=None)
    args = parser.parse_args()
    protected = [p for p in args.protect.split(";") if p]
    out_dir = guard(args.out, protected)
    chunk_dir = guard(args.chunkmap_dir, protected) if args.chunkmap_dir else None
    repo = Path(__file__).resolve().parents[2]

    games = []
    for entry in sorted(os.scandir(args.root), key=lambda e: e.name.casefold()):
        if not entry.is_dir(follow_symlinks=False):
            continue
        low = entry.name.lower()
        if low in SKIP_TOP or low.endswith("-pkg") or low.startswith("."):
            continue
        if args.only and args.only.lower() not in low:
            continue
        app = find_app_root(entry.path)
        if app is None:
            continue
        started = time.time()
        print(f"[survey] {entry.name}", flush=True)
        param = param_info(app)
        g = {"folder": entry.name, "app_root": app, "name_non_ascii": any(ord(c) > 127 for c in app),
             "dump_origin": {"dlpsgame": "[DLPSGAME.COM]" in entry.name.upper(), "suffix_app": bool(re.search(r"-app0?\b", entry.name, re.I))}}
        g["param"] = param
        g["id"] = game_id(entry.name, param)
        g["walk"] = walk(app)
        g["sce_sys"] = sce_sys_info(app)
        g["playgo"] = playgo_info(app)
        g["emu"] = emu_info(app)
        if chunk_dir and g["playgo"].get("class") in ("multi-chunk-language", "multi-chunk-no-language"):
            g["chunkmap"] = chunkmap(app, chunk_dir, g["id"], repo)
        g["voice_chunks"] = voice_languages(g["playgo"], g.get("chunkmap"))
        g["anomalies"] = anomalies(g)
        g["survey_seconds"] = round(time.time() - started, 1)
        games.append(g)

    os.makedirs(out_dir, exist_ok=True)
    suffix = f"-{args.only}" if args.only else ""
    with open(os.path.join(out_dir, f"game-survey{suffix}.json"), "w", encoding="utf-8") as stream:
        json.dump({"root": args.root, "generated": time.strftime("%Y-%m-%d %H:%M:%S"), "games": games}, stream, ensure_ascii=False, indent=1)
    with open(os.path.join(out_dir, f"game-survey{suffix}.md"), "w", encoding="utf-8") as stream:
        stream.write(render_markdown(games, args.root))
    print(f"[survey] {len(games)} game → {out_dir}")
    return 0


def render_markdown(games: list[dict], root: str) -> str:
    lines = [f"# Khảo sát game trên `{root}` (chỉ đọc)", "", f"Tạo lúc {time.strftime('%Y-%m-%d %H:%M')} bằng `scripts/research/survey-games.py`. "
             "PlayGo: `1-chunk` = script gốc đã đúng cấu trúc; `multi-chunk-language` = có chunk theo ngôn ngữ (ứng viên lỗi mất thoại khi build 1 chunk); "
             "`multi-chunk-no-language` = nhiều chunk nhưng mọi chunk cùng ngôn ngữ.", "",
             "| # | Thư mục | Title ID | Phiên bản | Tệp | Dung lượng | Keystone | PlayGo (chunk/kịch bản) | Lớp PlayGo | Chunk ngôn ngữ có dữ liệu | Ghi chú |",
             "|---|---|---|---|---:|---:|---|---|---|---|---|"]
    total_bytes = 0
    for index, g in enumerate(games, 1):
        p, w, pg = g["param"], g["walk"], g["playgo"]
        total_bytes += w["bytes"]
        chunks = f"{pg.get('chunk_count', '—')}/{pg.get('scenario_count', '—')}" if pg.get("present") else "—"
        voice = len(g["voice_chunks"])
        lines.append(f"| {index} | {g['folder'].strip()} | {p.get('titleId', '—')} | {p.get('contentVersion', '—')} | {w['files']:,} | {w['bytes'] / 1e9:.1f} GB | "
                     f"{'OK' if g['sce_sys'].get('keystone_ok') else '**THIẾU/SAI**'} | {chunks} | {pg.get('class')} | {voice or '—'} | {len(g['anomalies'])} |")
    lines += ["", f"Tổng: {len(games)} game, {total_bytes / 1e9:.0f} GB.", ""]
    classes: dict[str, list[str]] = {}
    for g in games:
        classes.setdefault(g["playgo"].get("class", "?"), []).append(f"{g['id']} ({g['folder'].strip()})")
    lines.append("## Phân loại PlayGo")
    for name, items in sorted(classes.items()):
        lines.append(f"- **{name}** ({len(items)}): " + ", ".join(items))
    lines += ["", "## Chi tiết từng game", ""]
    for g in games:
        p, pg, s, e, w = g["param"], g["playgo"], g["sce_sys"], g["emu"], g["walk"]
        lines.append(f"### {g['id']} — {p.get('titleName') or g['folder'].strip()}")
        lines.append(f"- Thư mục: `{g['app_root']}` · {w['files']:,} tệp · {w['dirs']:,} thư mục · {w['bytes'] / 1e9:.2f} GB · sâu {w['max_depth']} · "
                     f"đường dẫn tương đối dài nhất {w['max_rel_len'][0]} ký tự · tệp lớn nhất {w['largest'][0] / 1024 ** 3:.2f} GiB")
        lines.append(f"- param.json: contentId `{p.get('contentId')}`, version {p.get('contentVersion')}, master {p.get('masterVersion')}, DRM `{p.get('applicationDrmType')}`, "
                     f"category {p.get('applicationCategoryType')}, attribute {p.get('attribute')}/{p.get('attribute2')}/{p.get('attribute3')}, SDK {p.get('sdkVersion')}, "
                     f"FW {p.get('requiredSystemSoftwareVersion')}, versionFileUri {p.get('versionFileUri', '—')}, ngôn ngữ tiêu đề: {', '.join(p.get('languages', []))}")
        if p.get("unusual_keys"):
            lines.append(f"- param.json khoá khác: {', '.join(p['unusual_keys'])}")
        if pg.get("present"):
            lines.append(f"- PlayGo: **{pg['class']}** · {pg.get('chunk_count')} chunk · {pg.get('scenario_count')} kịch bản · mặc định {pg.get('default_language')} · "
                         f"hỗ trợ {len(pg.get('supported_languages', []))} ngôn ngữ · bảng tệp {pg.get('file_table', {}).get('hash_count')} mục")
            if pg["class"] != "1-chunk":
                for c in pg.get("chunks", []):
                    langs = c["languages"] if isinstance(c["languages"], str) else " ".join(c["languages"])
                    lines.append(f"  - chunk {c['id']} `{c['label']}` [{langs}]")
                for sc in pg.get("scenarios", []):
                    lines.append(f"  - kịch bản {sc['id']} `{sc['label']}` initial={sc['initial_chunk_count']} thứ tự {' '.join(map(str, sc['order']))}")
        else:
            lines.append("- PlayGo: không có playgo-chunk.dat")
        if g["voice_chunks"]:
            lines.append("- Chunk ngôn ngữ/phụ có dữ liệu thật: " + "; ".join(g["voice_chunks"]))
        lines.append(f"- sce_sys: {', '.join(sorted(s.get('entries', [])))}")
        lines.append(f"- Giả lập/dump: ampr_emu.index={e['ampr_emu_index']}, fakelib={e['fakelib']}, dlc_emu={e['dlc_emu'] or '—'}, "
                     f"tệp gốc: {', '.join(e['root_files'][:12])}")
        if g["anomalies"]:
            lines.append("- **Bất thường:**")
            lines += [f"  - {note}" for note in g["anomalies"]]
        lines.append("")
    return "\n".join(lines) + "\n"


if __name__ == "__main__":
    raise SystemExit(main())
