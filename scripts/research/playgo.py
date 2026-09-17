"""Read-only parser for PS5 PlayGo tables (sce_sys/playgo-chunk.dat, playgo-ficm.dat, playgo-hash-table.dat).

Mirrors src/PsViethoa.FpkgBuilder.Core/Services/SonySdkPlayGo.cs (Parse) but never raises on odd data:
every problem is reported in the result so the survey can list it. Only opens files with 'rb'.
"""
from __future__ import annotations

import struct

LANGUAGE_CODES = [
    "ja-JP", "en-US", "fr-FR", "es-ES", "de-DE", "it-IT", "nl-NL", "pt-PT", "ru-RU", "ko-KR", "zh-Hant", "zh-Hans", "fi-FI", "sv-SE",
    "da-DK", "no-NO", "pl-PL", "pt-BR", "en-GB", "tr-TR", "es-419", "ar-AE", "fr-CA", "cs-CZ", "hu-HU", "el-GR", "ro-RO", "th-TH",
    "vi-VN", "id-ID", "uk-UA",
]


def codes(mask: int) -> list[str]:
    out = []
    for bit in range(64):
        if mask & (1 << (63 - bit)):
            out.append(LANGUAGE_CODES[bit] if bit < len(LANGUAGE_CODES) else f"lang#{bit}")
    return out


def _u16(b, o):
    return struct.unpack_from("<H", b, o)[0]


def _u32(b, o):
    return struct.unpack_from("<I", b, o)[0]


def _u64(b, o):
    return struct.unpack_from("<Q", b, o)[0]


def _label(b, sec, rel):
    off, size = sec
    if rel >= size:
        return None
    raw = b[off + rel:off + size]
    end = raw.find(b"\0")
    raw = raw if end < 0 else raw[:end]
    if not raw or any(c < 32 or c > 126 for c in raw):
        return None
    return raw.decode("ascii")


def parse_chunk(b: bytes) -> dict:
    r: dict = {"size": len(b), "problems": []}
    if len(b) < 256 or b[:4] != b"plgx":
        r["problems"].append("not plgx")
        return r
    r["version"] = _u16(b, 4)
    if r["version"] != 0x1000:
        r["problems"].append(f"version {r['version']:#x} != 0x1000")
    stored = _u32(b, 16)
    if stored != len(b):
        r["problems"].append(f"stored length {stored} != file length {len(b)}")
    r["chunk_count"] = _u16(b, 10)
    r["scenario_count"] = _u16(b, 14)
    r["default_scenario"] = _u16(b, 20)
    r["default_language_id"] = _u16(b, 36)
    r["default_language"] = LANGUAGE_CODES[r["default_language_id"]] if r["default_language_id"] < len(LANGUAGE_CODES) else None
    mask = _u64(b, 56)
    r["supported_mask"] = f"{mask:#018x}"
    r["supported_languages"] = codes(mask)
    if mask == 0 or r["default_language_id"] > 63 or not mask & (1 << (63 - min(r["default_language_id"], 63))):
        r["problems"].append("default language not in header mask")

    def section(ptr, name):
        off, size = _u32(b, ptr), _u32(b, ptr + 4)
        if off > len(b) or size > len(b) - off:
            r["problems"].append(f"section {name} outside file")
            return (0, 0)
        return (off, size)

    ca = section(192, "chunk attributes")
    cl = section(208, "chunk labels")
    sa = section(224, "scenario attributes")
    sr = section(232, "scenario chunk refs")
    sl = section(240, "scenario labels")
    chunks = []
    for i in range(r["chunk_count"]):
        rec = ca[0] + i * 32
        if rec + 32 > ca[0] + ca[1]:
            r["problems"].append("chunk attributes truncated")
            break
        cmask = _u64(b, rec + 16)
        c = {"id": i, "mask": f"{cmask:#018x}", "languages": codes(cmask), "label": _label(b, cl, _u32(b, rec + 28))}
        c["all_languages"] = cmask == mask
        if cmask == 0 or cmask & ~mask:
            r["problems"].append(f"chunk {i} invalid mask")
        chunks.append(c)
    r["chunks"] = chunks
    scen = []
    for i in range(r["scenario_count"]):
        rec = sa[0] + i * 32
        if rec + 32 > sa[0] + sa[1]:
            r["problems"].append("scenario attributes truncated")
            break
        initial, total, lo = _u16(b, rec + 20), _u16(b, rec + 22), _u32(b, rec + 24)
        order = []
        if lo + total * 2 <= sr[1]:
            order = [_u16(b, sr[0] + lo + k * 2) for k in range(total)]
        else:
            r["problems"].append(f"scenario {i} chunk list outside section")
        if total < 1 or total > r["chunk_count"] or initial < 1 or initial > total or len(set(order)) != len(order) or any(x >= r["chunk_count"] for x in order):
            r["problems"].append(f"scenario {i} invalid chunk list (initial {initial}, total {total}, order {order})")
        scen.append({"id": i, "type_byte": b[rec], "initial_chunk_count": initial, "total": total, "order": order, "label": _label(b, sl, _u32(b, rec + 28))})
    r["scenarios"] = scen
    lang_chunks = [c["id"] for c in chunks if not c["all_languages"]]
    r["language_chunks"] = lang_chunks
    if r["chunk_count"] <= 1 and r["scenario_count"] <= 1:
        r["class"] = "1-chunk"
    elif lang_chunks:
        r["class"] = "multi-chunk-language"
    else:
        r["class"] = "multi-chunk-no-language"
    return r


def parse_ficm_hash(ficm: bytes | None, table: bytes | None) -> dict:
    r: dict = {"problems": []}
    if ficm is None or table is None:
        r["problems"].append("missing ficm or hash-table")
        return r
    ok_ficm = len(ficm) >= 16 and _u32(ficm, 0) == 1 and _u32(ficm, 8) == 16
    ok_flt = len(table) >= 56 and _u32(table, 0) == 1 and table[24:28] == b"\x7fFLT" and _u32(table, 8) == 56
    r["ficm_ok"], r["flt_ok"] = ok_ficm, ok_flt
    if not ok_ficm:
        r["problems"].append("ficm header unexpected")
    if not ok_flt:
        r["problems"].append("hash-table header unexpected (\\x7fFLT@24?)")
    if not (ok_ficm and ok_flt):
        return r
    map_bytes = min(_u32(ficm, 12), len(ficm) - 16)
    hash_count = min(_u32(table, 36), (len(table) - 56) // 8)
    r["ficm_entries"] = map_bytes // 2
    r["hash_count"] = _u32(table, 36)
    if r["ficm_entries"] != r["hash_count"]:
        r["problems"].append(f"ficm entries {r['ficm_entries']} != hash count {r['hash_count']}")
    count = min(hash_count, map_bytes // 2)
    hist: dict[int, int] = {}
    mapping: dict[int, int] = {}
    for i in range(count):
        chunk = ficm[16 + i * 2]
        hist[chunk] = hist.get(chunk, 0) + 1
        mapping[_u64(table, 56 + i * 8)] = chunk
    r["files_per_chunk"] = {str(k): v for k, v in sorted(hist.items())}
    r["_mapping"] = mapping
    return r
