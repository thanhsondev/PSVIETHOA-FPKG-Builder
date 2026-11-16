# AMPR runtimes (ZSTD PS5 pages, beta)

The two files below are embedded byte-for-byte in `PsViethoa.FpkgBuilder.Core.dll`. The app only ever writes them into a PS5 game folder as `fakelib/libSceAmpr.sprx`; they run on the PS5, never on the computer.

| File | Build | Bytes | SHA-256 | Source |
|---|---|---:|---|---|
| `ampr-emu-ng-0.4.1/libSceAmpr.sprx` | ampr-emu-ng v0.4.1 (release) | 733104 | `9c7c41b3216bc4f877d67e3c2e5c9c8f9ffabdd5065f4f3fe0dd98fa7228386f` | [quer3q/ampr-emu-ng](https://github.com/quer3q/ampr-emu-ng) release v0.4.1 (matches the release `SHA256SUMS`) |
| `ampr_emu-0.4.2.1/libSceAmpr.sprx` | ampr_emu 0.4.2.1 (upstream release) | 423350 | `69e6c4d5e4f5fb83c9e01815db5861c4c75734acbf4595cafa50d4c218116d1a` | [drakmor/ampr_emu](https://github.com/drakmor/ampr_emu) 0.4.2.1 (copy in ps5-dump-forge `vendor/ampr_emu/`) |

- **ampr-emu-ng** reads the ZSTD packs (`ampr_zstd.index` + `ampr_zstd-NNN.pak`) and LZ4 packs; it is installed by **Pack ZSTD**. v0.4.1 (15 Nov 2026): a compact in-memory file index with no 48 MiB cap (Final Fantasy XVI, Demon's Souls and Returnal start again), faster pack reads with a 64 MiB decoded cache, save and folder-handle fixes; the pack format is unchanged. v0.3.2–v0.4.0 are still recognised in existing games.
- **ampr_emu 0.4.2.1** is put back by **Unpack ZSTD**, as ps5-dump-forge does.
- License: both are **GPL-3.0-or-later** (`GPL-3.0.txt`). Corresponding source: tag `v0.4.1` (commit 8b61a06) of quer3q/ampr-emu-ng and release 0.4.2.1 of drakmor/ampr_emu; the v0.4.1 source archive is on the release tag; ps5-dump-forge keeps `ampr_emu-0.4.2.1-src.tar.gz` in its `vendor/`.
- The ZSTD pack format is reimplemented in C# (`src/PsViethoa.FpkgBuilder.Core/Zstd/`) from `docs/zstd.md` of [quer3q/ps5-dump-forge](https://github.com/quer3q/ps5-dump-forge) (GPL-3.0-or-later), ported from PSVIETHOA STUDIO 2.1.0. Files that always stay loose follow ps5-dump-forge 0.5.5: `eboot.bin`, `*.prx`, `*.sprx`, `*.elf`, `*.self`, `*.ini`, `sce_sys/`, `sce_module/`, `fakelib*/`.

Replace a runtime only together with its SHA-256 in `Zstd/AmprZstd.cs` (`NgSha256`, `NgVersion`).
