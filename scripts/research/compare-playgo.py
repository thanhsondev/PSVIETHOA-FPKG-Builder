"""So PlayGo + param.json của gói mới với thư mục game gốc (CHỈ ĐỌC nguồn và gói).

    python scripts/research/compare-playgo.py --game "G:\\PPSA26344 Ghost of Yōtei (01.512.000)" --pkg C:\\fpkg-research\\out\\PPSA26344\\x.pkg \\
        --work C:\\fpkg-research\\tmp\\cmp-PPSA26344 --out docs/research/compare-PPSA26344.md

- Xuất các entry CNT của gói (param.json, playgo-*.dat, playgo-scenario.json…) vào --work bằng chính mã của ứng dụng
  (scripts/research/research.cs compare → PackageReader.ExportCntEntries) rồi so: số chunk/kịch bản, mask ngôn ngữ, nhãn,
  initial_chunk_count, thứ tự, ngôn ngữ mặc định, ánh xạ tệp → chunk theo hash đường dẫn.
- param.json: liệt kê mọi khoá thêm/mất/đổi, đánh dấu khoá mà SDK/ứng dụng được phép đổi.
- --work và --out bị từ chối nếu nằm trên ổ bảo vệ (--protect, mặc định G:\\).
"""
from __future__ import annotations

import argparse
import json
import os
import subprocess
from pathlib import Path

EXPECTED_PARAM_CHANGES = {
    "applicationDrmType": "SDK toolkit luôn đặt standard",
    "sdkVersion": "Publishing Tools 2.79 không đọc được SDK từ eboot (thiếu prospero-llvm-readelf) → 0",
    "requiredSystemSoftwareVersion": "Publishing Tools đặt theo chính nó",
    "pubtools": "Publishing Tools ghi lại (ngày tạo, phiên bản 2.79)",
    "originContentVersion": "Publishing Tools bỏ khoá của gói patch khi tạo gói app",
    "targetContentVersion": "Publishing Tools bỏ khoá của gói patch khi tạo gói app",
    "versionFileUri": "tuỳ chọn ứng dụng (mặc định bật) xoá URL kiểm tra phiên bản",
    "attribute3": "tuỳ chọn ứng dụng (mặc định bật) đặt 0",
}


def guard(path: str, protected: list[str]) -> str:
    full = os.path.abspath(path)
    for root in protected:
        if full.lower().startswith(os.path.abspath(root).lower()):
            raise SystemExit(f"REFUSED: write path {full} is on protected drive {root}")
    return full


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--game", required=True)
    parser.add_argument("--pkg", required=True)
    parser.add_argument("--work", required=True)
    parser.add_argument("--out", required=True)
    parser.add_argument("--protect", default="G:\\")
    args = parser.parse_args()
    protected = [p for p in args.protect.split(";") if p]
    work = guard(args.work, protected)
    out = guard(args.out, protected)
    repo = Path(__file__).resolve().parents[2]
    report_json = os.path.join(work, "compare.json")
    command = ["dotnet", "run", str(repo / "scripts" / "research" / "research.cs"), "--", "compare", args.game, args.pkg, os.path.join(work, "cnt"), report_json]
    completed = subprocess.run(command, cwd=repo, capture_output=True, text=True, encoding="utf-8", errors="replace")
    if completed.returncode != 0:
        print(completed.stdout[-3000:], completed.stderr[-3000:])
        return completed.returncode
    report = json.load(open(report_json, encoding="utf-8"))

    original_param = json.loads(open(os.path.join(args.game, "sce_sys", "param.json"), "rb").read().decode("utf-8-sig"))
    package_param = report["package"].get("param") or {}
    lines = [f"# So sánh PlayGo / param.json — {os.path.basename(args.game.rstrip(os.sep))}", "",
             f"- Nguồn: `{args.game}`", f"- Gói: `{args.pkg}` ({os.path.getsize(args.pkg) / 1e9:.2f} GB)",
             f"- Kết luận cấu trúc PlayGo: **{report['verdict']}**", ""]
    o, n = report["original"], report["package"].get("structure") or {}
    lines += ["## PlayGo", "", "| | Gốc | Gói mới |", "|---|---|---|",
              f"| Số chunk | {o['chunkCount']} | {n.get('chunkCount')} |",
              f"| Số kịch bản | {o['scenarioCount']} | {n.get('scenarioCount')} |",
              f"| Ngôn ngữ hỗ trợ | {o['supportedLanguages']} | {n.get('supportedLanguages')} |",
              f"| Ngôn ngữ mặc định (id) | {o['defaultLanguageId']} | {n.get('defaultLanguageId')} |",
              f"| Kịch bản mặc định | {o['defaultScenarioId']} | {n.get('defaultScenarioId')} |",
              f"| Mục bảng tệp | {o['fileTableEntries']} | {n.get('fileTableEntries')} |", ""]
    fm = report.get("fileMap", {})
    lines += [f"Ánh xạ tệp → chunk (theo hash đường dẫn): cùng chunk {fm.get('sameChunk')}, khác chunk **{fm.get('differentChunk')}**, "
              f"có trong gốc nhưng không có trong gói {fm.get('notInPackage')}, chỉ có trong gói {fm.get('onlyInPackage')}.", ""]
    if report.get("differences"):
        lines += ["Khác biệt cấu trúc:", ""] + [f"- {d}" for d in report["differences"]] + [""]
    if fm.get("mismatchSample"):
        lines += ["Mẫu tệp khác chunk:", ""] + [f"- {d}" for d in fm["mismatchSample"]] + [""]
    lines += ["| Chunk | Nhãn gốc | Ngôn ngữ gốc | Nhãn mới | Ngôn ngữ mới |", "|---:|---|---|---|---|"]
    new_chunks = {c["id"]: c for c in n.get("chunks", [])}
    for c in o["chunks"]:
        m = new_chunks.get(c["id"], {})
        lines.append(f"| {c['id']} | {c['label']} | {c['languages']} | {m.get('label', '—')} | {m.get('languages', '—')} |")
    lines += ["", "| Kịch bản | Gốc (initial / thứ tự) | Mới (initial / thứ tự) |", "|---:|---|---|"]
    new_scen = {s["id"]: s for s in n.get("scenarios", [])}
    for s in o["scenarios"]:
        m = new_scen.get(s["id"], {})
        lines.append(f"| {s['id']} `{s['label']}` | {s['initialChunkCount']} / {s['order']} | {m.get('initialChunkCount', '—')} / {m.get('order', '—')} |")

    lines += ["", "## param.json (gốc → trong gói)", "", "| Khoá | Gốc | Trong gói | Đánh giá |", "|---|---|---|---|"]
    unexpected = 0
    for key in sorted(set(original_param) | set(package_param)):
        a, b = original_param.get(key, "∅"), package_param.get(key, "∅")
        if a == b:
            continue
        verdict = EXPECTED_PARAM_CHANGES.get(key)
        if verdict is None:
            unexpected += 1
            verdict = "**CẦN XEM**"
        lines.append(f"| {key} | `{json.dumps(a, ensure_ascii=False)[:80]}` | `{json.dumps(b, ensure_ascii=False)[:80]}` | {verdict} |")
    lines += ["", f"Khoá đổi ngoài dự kiến: **{unexpected}**.", ""]
    scenario_text = report["package"].get("playgoScenarioJson")
    original_scenario = os.path.join(args.game, "sce_sys", "playgo-scenario.json")
    if scenario_text is not None and os.path.isfile(original_scenario):
        same = open(original_scenario, "rb").read() == scenario_text.encode("utf-8")
        lines.append(f"playgo-scenario.json trong gói {'giống từng byte' if same else 'KHÁC'} tệp gốc.")
    os.makedirs(os.path.dirname(out), exist_ok=True)
    open(out, "w", encoding="utf-8").write("\n".join(lines) + "\n")
    print(f"{report['verdict']} · differentChunk={fm.get('differentChunk')} · unexpected param changes={unexpected} → {out}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
