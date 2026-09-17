"""Convert a patched SDK 2.79 plaintext PKG to LibProsperoPkg's diagnostic profile.

The patched publisher CLI intentionally leaves the outer PFS and protected CNT records
plaintext.  It still emits an ordinary outer-PFS seed, though, which correctly makes a
normal reader attempt AES-XTS.  This tool makes that representation explicit without
touching the SDK installation: it writes the package to a new path, applies the
``PPRPLAIN-NOAUTH!`` marker, removes the now-false CNT encryption flags, and reseals
the non-RSA hashes affected by those edits.

The output remains a debug/test package, not a retail-signable submission image. Its
deterministic public CNT-header RSA wrap is rebuilt exactly like LibProsperoPkg. The
PlayGo CRC table in the trailing SI archive is also repaired after every changed mount-image
block so the debug installer sees the same bytes that the table authenticates.
"""
from __future__ import annotations

import argparse
import hashlib
import io
from pathlib import Path
import struct
import sys
import zipfile
import zlib


BLOCK_SIZE = 0x10000
PLAINTEXT_SEED = b"PPRPLAIN-NOAUTH!"
FIH_MAGIC = b"\x7fFIH"
CNT_MAGIC = b"\x7fCNT"
ENTRY_META_SIZE = 0x20
ENTRY_FLAG_ENCRYPTED = 0x80000000
IMAGE_DIGESTS_ENTRY_ID = 0x040A
CNT_WRAP_MODULUS = bytes.fromhex(
    "ab1dbd4339493316a35c404e2c2297b833685c1ad354e8c5ba7888d1b0faf25a"
    "8f14aa06528fa465866ed42303d300910bd9d84101fe54c12bfc4f7f9c3a7ac9"
    "1333fd2cdccb1400761ade5c2ebca0116d8c304b8b47f33c413772849e9e1d18"
    "3b4d7bbc994c37ed7887d48694234b71accb4db95070336618976ed67b1c401a"
    "2113d439880340499f656b7aeeb386c06798c2d144ebb584b5657b28e2909449"
    "31799b0b09b271a1d9370bfe4f84bacc78ea3c917d300d53d5c56a340b2b075"
    "6080f28325363eb9bc84eb91d70468eef8bd4ab302f13f30041709579caa54e8"
    "bd7642356ec85230a1514e00667568423081d6439968833a51c5b2fc7b6ef006"
    "23fb725899a2967cbc14ceeaefe8747280295a31c908959b37eceb0064182c533"
    "664ded6355ff313cf82a891a42dc88655fddfe71e650e51b1490a888ce38d6fb"
    "850e20d12408cdb0f0efab2ff19f9a95802d437560c0c986c5f2cbb20e2b897"
    "f6bcb67a5657b4724dbda2cb38fe23d738cf26f8cc06e0f1221fe740d0e368171")


class ConversionProgress:
    def __init__(self) -> None:
        self.interactive = sys.stdout.isatty()
        self.last_percent = -1
        self.last_width = 0

    def update(self, percent: float, message: str) -> None:
        percent = max(0.0, min(100.0, percent))
        whole = int(percent)
        if whole == self.last_percent and percent < 100:
            return
        if not self.interactive and self.last_percent >= 0 and whole < self.last_percent + 5 and whole < 100:
            return
        self.last_percent = whole
        width = 50
        filled = min(width, int(percent * width / 100))
        bar = "=" * filled + ">" + " " * max(0, width - filled - 1)
        if percent >= 100:
            bar = "=" * width
        line = f"[{bar}] {percent:6.2f}%  {message}"
        if self.interactive:
            padding = " " * max(0, self.last_width - len(line))
            print("\r" + line + padding, end="\n" if percent >= 100 else "", flush=True)
            self.last_width = len(line)
        else:
            print(line, flush=True)

    def newline(self) -> None:
        if self.interactive and self.last_percent < 100:
            print(flush=True)


def copy_with_progress(source: Path, destination: Path, progress: ConversionProgress) -> None:
    total = source.stat().st_size
    copied = 0
    progress.update(0, f"Copying input PKG (0 / {total:,} bytes)")
    with source.open("rb") as input_stream, destination.open("xb") as output_stream:
        while True:
            block = input_stream.read(8 * 1024 * 1024)
            if not block:
                break
            output_stream.write(block)
            copied += len(block)
            progress.update(
                85 * copied / max(total, 1),
                f"Copying input PKG ({copied:,} / {total:,} bytes)")
    if copied != total:
        raise OSError(f"copied {copied} of {total} input bytes")


class MersenneTwister:
    """Exact uint32 generator used by LibProsperoPkg's deterministic RSA padding."""

    N = 624
    M = 397
    MASK32 = 0xFFFFFFFF

    def __init__(self, seed_words: list[int]) -> None:
        self.mt = [0] * self.N
        self.mt[0] = 0x012BD6AA
        for index in range(1, self.N):
            previous = self.mt[index - 1]
            self.mt[index] = (index + 0x6C078965 * (previous ^ (previous >> 30))) & self.MASK32
        state_index, seed_index = 1, 0
        for _ in range(max(self.N, len(seed_words))):
            previous = self.mt[state_index - 1]
            mixed = ((previous ^ (previous >> 30)) * 0x0019660D) & self.MASK32
            self.mt[state_index] = (
                (self.mt[state_index] ^ mixed) + seed_words[seed_index] + seed_index
            ) & self.MASK32
            state_index += 1
            seed_index = (seed_index + 1) % len(seed_words)
            if state_index >= self.N:
                self.mt[0] = self.mt[self.N - 1]
                state_index = 1
        for _ in range(self.N - 1):
            previous = self.mt[state_index - 1]
            mixed = ((previous ^ (previous >> 30)) * 0x5D588B65) & self.MASK32
            self.mt[state_index] = ((self.mt[state_index] ^ mixed) - state_index) & self.MASK32
            state_index += 1
            if state_index >= self.N:
                self.mt[0] = self.mt[self.N - 1]
                state_index = 1
        self.mt[0] = 1 << 31
        # The C# seed constructor uses its mti field as the initialization loop
        # counter and leaves it at N; the first Int32 call therefore twists.
        self.index = self.N

    def int32(self) -> int:
        if self.index >= self.N:
            for index in range(self.N):
                value = (self.mt[index] & 0x80000000) | self.mt[(index + 1) % self.N] & 0x7FFFFFFF
                self.mt[index] = (
                    self.mt[(index + self.M) % self.N] ^ (value >> 1) ^
                    (0x9908B0DF if value & 1 else 0)
                ) & self.MASK32
            self.index = 0
        value = self.mt[self.index]
        self.index += 1
        value ^= value >> 11
        value ^= (value << 7) & 0x9D2C5680
        value ^= (value << 15) & 0xEFC60000
        value ^= value >> 18
        return value & self.MASK32


def build_cnt_header_wrap(cnt_header: bytes | bytearray) -> bytes:
    """Port of ProsperoPublisherRsa.BuildCntHeaderWrap from LibProsperoPkg."""
    if len(cnt_header) < 0x1000 or len(CNT_WRAP_MODULUS) != 384:
        raise ValueError("invalid CNT header or RSA-3072 modulus")
    message = sha3(cnt_header[:0x1000])
    seed = hashlib.sha256(hashlib.sha256(CNT_WRAP_MODULUS + message).digest()).digest()
    generator = MersenneTwister([
        int.from_bytes(seed[offset:offset + 4], "big")
        for offset in range(0, len(seed), 4)
    ])
    encoded = bytearray(len(CNT_WRAP_MODULUS))
    encoded[1] = 2
    padding_length = len(encoded) - len(message) - 3
    cursor = 2
    while cursor < 2 + padding_length:
        source = b"".join(generator.int32().to_bytes(4, "big") for _ in range(12))
        for value in hashlib.sha256(source).digest():
            if value == 0:
                continue
            encoded[cursor] = value
            cursor += 1
            if cursor == 2 + padding_length:
                break
    encoded[2 + padding_length] = 0
    encoded[3 + padding_length:] = message
    wrapped = pow(
        int.from_bytes(encoded, "big"), 65537,
        int.from_bytes(CNT_WRAP_MODULUS, "big"))
    return wrapped.to_bytes(len(CNT_WRAP_MODULUS), "big")


def sha3(data: bytes | bytearray | memoryview) -> bytes:
    return hashlib.sha3_256(data).digest()


def read_exact(stream, offset: int, size: int) -> bytes:
    stream.seek(offset)
    data = stream.read(size)
    if len(data) != size:
        raise ValueError(f"truncated package at 0x{offset:X}")
    return data


def hash_range(stream, offset: int, size: int) -> bytes:
    digest = hashlib.sha3_256()
    stream.seek(offset)
    while size:
        chunk = stream.read(min(size, 1024 * 1024))
        if not chunk:
            raise ValueError(f"truncated package at 0x{offset:X}")
        digest.update(chunk)
        size -= len(chunk)
    return digest.digest()


def hash_ranges(stream, ranges: list[tuple[int, int]]) -> bytes:
    digest = hashlib.sha3_256()
    for offset, size in ranges:
        stream.seek(offset)
        while size:
            chunk = stream.read(min(size, 1024 * 1024))
            if not chunk:
                raise ValueError(f"truncated package at 0x{offset:X}")
            digest.update(chunk)
            size -= len(chunk)
    return digest.digest()


def parse_entries(header: bytes) -> list[tuple[int, int, int, int]]:
    count = struct.unpack_from(">I", header, 0x10)[0]
    table = struct.unpack_from(">I", header, 0x18)[0]
    if count == 0 or count > 0x10000 or table + count * ENTRY_META_SIZE > len(header):
        raise ValueError("invalid CNT entry table")
    entries = []
    for index in range(count):
        record = table + index * ENTRY_META_SIZE
        entry_id = struct.unpack_from(">I", header, record)[0]
        flags = struct.unpack_from(">I", header, record + 8)[0]
        data_offset, data_size = struct.unpack_from(">II", header, record + 0x10)
        entries.append((entry_id, flags, data_offset, data_size))
    return entries


def require_range(file_size: int, offset: int, size: int, name: str) -> None:
    if offset < 0 or size < 0 or offset > file_size or size > file_size - offset:
        raise ValueError(f"{name} is outside the package")


def crc32c(data: bytes) -> int:
    value = 0xFFFFFFFF
    for byte in data:
        value ^= byte
        for _ in range(8):
            value = (value >> 1) ^ (0x82F63B78 if value & 1 else 0)
    return value ^ 0xFFFFFFFF


def replace_stored_zip_member(archive: bytearray, name: str, payload: bytes) -> None:
    """Replace one same-sized STORED ZIP member and both of its ZIP CRC-32 fields."""
    with zipfile.ZipFile(io.BytesIO(archive), "r") as package:
        matches = [entry for entry in package.infolist() if entry.filename == name]
        if len(matches) != 1:
            raise ValueError(f"SI archive must contain exactly one {name}")
        entry = matches[0]
        if entry.compress_type != zipfile.ZIP_STORED or entry.flag_bits & 0x08:
            raise ValueError(f"SI member is not a directly patchable STORED entry: {name}")
        if entry.file_size != len(payload) or entry.compress_size != len(payload):
            raise ValueError(
                f"SI member size mismatch for {name}: {entry.file_size} != {len(payload)}")

    local = entry.header_offset
    if archive[local:local + 4] != b"PK\x03\x04":
        raise ValueError(f"invalid SI local header for {name}")
    name_size, extra_size = struct.unpack_from("<HH", archive, local + 26)
    data_offset = local + 30 + name_size + extra_size
    archive[data_offset:data_offset + len(payload)] = payload
    zip_crc = zlib.crc32(payload) & 0xFFFFFFFF
    struct.pack_into("<I", archive, local + 14, zip_crc)

    eocd = archive.rfind(b"PK\x05\x06", max(0, len(archive) - 0x10016))
    if eocd < 0 or eocd + 22 > len(archive):
        raise ValueError("SI end-of-central-directory record is missing")
    central_size, central_offset = struct.unpack_from("<II", archive, eocd + 12)
    central_end = central_offset + central_size
    if central_end > eocd:
        raise ValueError("SI central directory is outside the archive")
    cursor = central_offset
    found = False
    while cursor < central_end:
        if archive[cursor:cursor + 4] != b"PK\x01\x02":
            raise ValueError("invalid SI central-directory record")
        filename_size, extra_size, comment_size = struct.unpack_from(
            "<HHH", archive, cursor + 28)
        filename = bytes(archive[cursor + 46:cursor + 46 + filename_size]).decode("utf-8")
        if filename == name:
            if found:
                raise ValueError(f"duplicate SI central-directory member: {name}")
            struct.pack_into("<I", archive, cursor + 16, zip_crc)
            found = True
        cursor += 46 + filename_size + extra_size + comment_size
    if not found:
        raise ValueError(f"SI central-directory member is missing: {name}")


def repair_playgo_crc(
    stream, file_size: int, supplement_offset: int, touched_blocks: set[int], content_id: str,
) -> int:
    require_range(file_size, supplement_offset, file_size - supplement_offset, "SI archive")
    archive = bytearray(read_exact(stream, supplement_offset, file_size - supplement_offset))
    crc_name = f"config/{content_id}/playgo-chunk.crc"
    with zipfile.ZipFile(io.BytesIO(archive), "r") as package:
        matches = [entry for entry in package.infolist() if entry.filename == crc_name]
        if len(matches) != 1:
            raise ValueError(f"SI archive must contain exactly one {crc_name}")
        current = bytearray(package.read(matches[0]))
    block_count = (supplement_offset + BLOCK_SIZE - 1) // BLOCK_SIZE
    if len(current) != block_count * 4:
        raise ValueError(
            f"SI PlayGo CRC table has {len(current)} bytes; expected {block_count * 4}")
    repaired = 0
    for block_index in sorted(touched_blocks):
        if block_index >= block_count:
            continue
        block_offset = block_index * BLOCK_SIZE
        block_size = min(BLOCK_SIZE, supplement_offset - block_offset)
        block = read_exact(stream, block_offset, block_size)
        struct.pack_into("<I", current, block_index * 4, crc32c(block))
        repaired += 1
    replace_stored_zip_member(archive, crc_name, bytes(current))
    stream.seek(supplement_offset)
    stream.write(archive)
    return repaired


def convert(path: Path, progress: ConversionProgress) -> dict[str, int]:
    progress.update(86, "Reading FIH, outer-PFS and CNT metadata")
    with path.open("r+b") as stream:
        file_size = path.stat().st_size
        fih = bytearray(read_exact(stream, 0, BLOCK_SIZE))
        if fih[:4] != FIH_MAGIC:
            raise ValueError("only finalized FIH packages are supported")
        outer_offset, outer_size = struct.unpack_from("<QQ", fih, 0x10)
        superblock_offset = struct.unpack_from("<Q", fih, 0x20)[0]
        cnt_offset = struct.unpack_from("<Q", fih, 0x58)[0]
        require_range(file_size, outer_offset, outer_size, "outer PFS")
        require_range(file_size, superblock_offset, BLOCK_SIZE, "outer superblock")
        require_range(file_size, cnt_offset, BLOCK_SIZE, "CNT header")
        if not outer_offset <= superblock_offset < outer_offset + outer_size:
            raise ValueError("FIH superblock does not lie inside the outer PFS")

        superblock = bytearray(read_exact(stream, superblock_offset, BLOCK_SIZE))
        if struct.unpack_from("<Q", superblock, 0)[0] != 2 or superblock[8:12] != b"\x0b\x2a\x33\x01":
            raise ValueError("FIH does not point to a PS5 outer-PFS superblock")
        mode = struct.unpack_from("<H", superblock, 0x1C)[0]
        if mode != 0x000D:
            raise ValueError(f"unsupported outer-PFS mode 0x{mode:04X}; expected 0x000D")

        cnt = bytearray(read_exact(stream, cnt_offset, BLOCK_SIZE))
        if cnt[:4] != CNT_MAGIC:
            raise ValueError("FIH does not point to a CNT header")
        entries = parse_entries(cnt)
        touched_blocks: set[int] = set()

        def mark_touched(offset: int, size: int) -> None:
            if size <= 0:
                return
            first = offset // BLOCK_SIZE
            last = (offset + size - 1) // BLOCK_SIZE
            touched_blocks.update(range(first, last + 1))

        # The first 64 KiB of CNT contains both header fields and the compact entry payloads
        # for normal nwonly packages.  Keep that in-memory window coherent with every targeted
        # write, so the final header flush cannot restore an old digest-table record.
        def write_cnt(relative_offset: int, data: bytes) -> None:
            require_range(file_size - cnt_offset, relative_offset, len(data), "CNT update")
            stream.seek(cnt_offset + relative_offset)
            stream.write(data)
            mark_touched(cnt_offset + relative_offset, len(data))
            first = max(relative_offset, 0)
            last = min(relative_offset + len(data), BLOCK_SIZE)
            if first < last:
                cnt[first:last] = data[first - relative_offset:last - relative_offset]

        # Change the representation marker first, then re-create every hash that directly
        # covers it.  The superblock is deliberately plaintext in a normal nwonly image too.
        superblock[0x370:0x380] = PLAINTEXT_SEED
        superblock[0x380:0x3A0] = bytes(32)
        superblock[0x380:0x3A0] = sha3(superblock[:0x5A0])
        stream.seek(superblock_offset)
        stream.write(superblock)
        mark_touched(superblock_offset, len(superblock))
        superblock_digest = sha3(superblock)
        progress.update(89, "Updating plaintext outer-PFS marker and superblock digest")

        # The SDK's identity-CBC patch leaves these payloads clear but leaves their flags set.
        # Clearing the bit makes generic CNT readers use the real logical size, rather than
        # trying to decrypt clear bytes.  Keep every other policy/key-index bit verbatim.
        cleared_flags = 0
        cleared_entry_indexes = []
        table = struct.unpack_from(">I", cnt, 0x18)[0]
        for index, (_, flags, _, _) in enumerate(entries):
            if flags & ENTRY_FLAG_ENCRYPTED:
                record = table + index * ENTRY_META_SIZE
                struct.pack_into(">I", cnt, record + 8, flags & ~ENTRY_FLAG_ENCRYPTED)
                cleared_flags += 1
                cleared_entry_indexes.append(index)
        entries = parse_entries(cnt)

        # imagedigs.dat is a reversed SHA3 record per outer 64-KiB block.  Only the changed
        # plaintext superblock record needs replacing; stream the whole entry afterward when
        # refreshing its ordinary CNT entry digest.
        image_entry_index = next(
            (i for i, entry in enumerate(entries) if entry[0] == IMAGE_DIGESTS_ENTRY_ID), None)
        if image_entry_index is None:
            raise ValueError("CNT has no imagedigs.dat entry")
        _, _, image_offset, image_size = entries[image_entry_index]
        superblock_index = (superblock_offset - outer_offset) // BLOCK_SIZE
        image_record_offset = superblock_index * 32
        if outer_size % BLOCK_SIZE or image_record_offset + 32 > image_size:
            raise ValueError("imagedigs.dat does not cover the outer-PFS superblock")
        stream.seek(cnt_offset)
        stream.write(cnt)
        mark_touched(cnt_offset, len(cnt))
        write_cnt(image_offset + image_record_offset, superblock_digest[::-1])

        # FIH/CNT cross references.  Update fixed-info after writing the new game digest to FIH.
        fih[0x30:0x50] = superblock_digest
        stream.seek(0)
        stream.write(fih)
        mark_touched(0, len(fih))
        fixed_info_digest = sha3(fih)
        cnt[0x440:0x460] = superblock_digest
        cnt[0x460:0x480] = fixed_info_digest
        cnt[0x4A0:0x4B0] = PLAINTEXT_SEED

        # Update the changed image-digest and entry-meta records in CNT's per-entry digest table.
        # This is not recursive: the table's own slot is specified as zero.
        digest_entry_index = next(
            (i for i, entry in enumerate(entries) if entry[0] == 0x0001), None)
        if digest_entry_index is None:
            raise ValueError("CNT has no per-entry digest table")
        _, _, digest_offset, digest_size = entries[digest_entry_index]
        if digest_size < len(entries) * 32:
            raise ValueError("CNT per-entry digest table is truncated")
        metas_entry_index = next(
            (i for i, entry in enumerate(entries) if entry[0] == 0x0100), None)
        if metas_entry_index is None:
            raise ValueError("CNT has no entry-meta table")
        for index in sorted({metas_entry_index, image_entry_index, *cleared_entry_indexes}):
            _, _, data_offset, data_size = entries[index]
            write_cnt(
                digest_offset + index * 32,
                hash_range(stream, cnt_offset + data_offset, data_size))
        progress.update(92, "Rebuilding FIH/CNT cross-references and entry digests")

        # CNT's four normal body seals.  The first two cover the semantic SC-entry sequence;
        # the first is also the FIH reader's historical "rollup" hash.  Their preimages do not
        # include these four stored fields, so they can be written in any order after the updates.
        rollup_offset = struct.unpack_from(">Q", cnt, 0x20)[0]
        rollup_size = struct.unpack_from(">I", cnt, 0x1C)[0]
        require_range(file_size - cnt_offset, rollup_offset, rollup_size, "CNT rollup")
        write_cnt(0x100, hash_range(stream, cnt_offset + rollup_offset, rollup_size))
        sc_count = struct.unpack_from(">H", cnt, 0x14)[0]
        sc_entries = [entry for entry in entries if entry[0] in (0x0010, 0x0020, 0x0080, 0x0100)]
        if len(sc_entries) != 4:
            raise ValueError("CNT does not contain the expected SC entries")
        short_ranges = [(cnt_offset + offset, size) for _, _, offset, size in sc_entries]
        meta_offset, meta_size = sc_entries[-1][2], sc_entries[-1][3]
        short_ranges[-1] = (cnt_offset + meta_offset, min(meta_size, sc_count * ENTRY_META_SIZE))
        write_cnt(0x120, hash_ranges(stream, short_ranges))
        write_cnt(0x140, hash_range(stream, cnt_offset + digest_offset, digest_size))
        body_offset = struct.unpack_from(">Q", cnt, 0x20)[0]
        body_size = struct.unpack_from(">Q", cnt, 0x28)[0]
        require_range(file_size - cnt_offset, body_offset, body_size, "CNT body")
        write_cnt(0x160, hash_range(stream, cnt_offset + body_offset, body_size))
        image_key_offset, image_key_size, mandatory_offset, mandatory_size = struct.unpack_from(
            ">4I", cnt, 0x510)
        require_range(file_size - cnt_offset, image_key_offset, image_key_size, "CNT image-key descriptor")
        require_range(file_size - cnt_offset, mandatory_offset, mandatory_size, "CNT mandatory descriptor")
        write_cnt(
            0x520,
            hash_range(stream, cnt_offset + image_key_offset, image_key_size) +
            hash_range(stream, cnt_offset + mandatory_offset, mandatory_size))
        cnt[0xFE0:0x1000] = bytes(32)
        cnt[0xFE0:0x1000] = sha3(cnt[:0xFE0])
        stream.seek(cnt_offset)
        stream.write(cnt)
        mark_touched(cnt_offset, len(cnt))
        stream.seek(cnt_offset + 0x1000)
        cnt_wrap = build_cnt_header_wrap(cnt)
        stream.write(cnt_wrap)
        mark_touched(cnt_offset + 0x1000, len(cnt_wrap))
        stream.flush()
        progress.update(96, "Rebuilding CNT rollups and RSA-3072 header wrap")

        table = struct.unpack_from(">I", cnt, 0x18)[0]
        body_offset = struct.unpack_from(">Q", cnt, 0x20)[0]
        body_size = struct.unpack_from(">Q", cnt, 0x28)[0]
        cnt_end = max(
            0x5A0,
            table + len(entries) * ENTRY_META_SIZE,
            body_offset + body_size,
            *(offset + size for _, _, offset, size in entries),
        )
        cnt_end = (cnt_end + 15) & ~15
        supplement_offset = cnt_offset + cnt_end
        require_range(file_size, supplement_offset, file_size - supplement_offset, "SI archive")
        content_id = bytes(cnt[0x40:0x70]).split(b"\0", 1)[0].decode("ascii")
        repaired_crc_blocks = repair_playgo_crc(
            stream, file_size, supplement_offset, touched_blocks, content_id)
        stream.flush()
        progress.update(99, "Repairing SI PlayGo CRC32C and ZIP metadata")
    return {"cleared_encryption_flags": cleared_flags, "outer_blocks": outer_size // BLOCK_SIZE,
            "cnt_header_wrap_bytes": len(CNT_WRAP_MODULUS),
            "playgo_crc_blocks": repaired_crc_blocks}


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("input", type=Path, help="PKG created by the patched SDK CLI")
    parser.add_argument("output", type=Path, help="new LibProsperoPkg-compatible diagnostic PKG")
    args = parser.parse_args()
    source, destination = args.input.resolve(), args.output.resolve()
    if source == destination:
        raise ValueError("input and output must be different files")
    if not source.is_file():
        raise FileNotFoundError(source)
    if destination.exists():
        raise FileExistsError(f"output already exists: {destination}")
    destination.parent.mkdir(parents=True, exist_ok=True)
    progress = ConversionProgress()
    try:
        copy_with_progress(source, destination, progress)
        report = convert(destination, progress)
        progress.update(100, "Conversion complete")
    except Exception:
        progress.newline()
        destination.unlink(missing_ok=True)
        raise
    print(f"Created {destination}")
    print(f"Marked {report['outer_blocks']} outer-PFS blocks plaintext/no-auth; "
          f"cleared {report['cleared_encryption_flags']} CNT encryption flag(s).")
    print(f"Rebuilt {report['cnt_header_wrap_bytes']}-byte deterministic CNT RSA-3072 header wrap.")
    print(f"Repaired {report['playgo_crc_blocks']} changed PlayGo mount-image CRC block(s).")


if __name__ == "__main__":
    main()
