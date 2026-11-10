# AMPR runtimes (ZSTD PS5 pages, beta)

The two files below are embedded byte-for-byte in `PsViethoa.FpkgBuilder.Core.dll`. The app only ever writes them into a PS5 game folder as `fakelib/libSceAmpr.sprx`; they run on the PS5, never on the computer.

| File | Build | Bytes | SHA-256 | Source |
|---|---|---:|---|---|
| `ampr-emu-ng-0.3.4/libSceAmpr.sprx` | ampr-emu-ng v0.3.4 (release) | 712144 | `7d1bb260f954b755ed687733e52ccfa38c31440a9b88a6fbba5b3b75e08bfa26` | [quer3q/ampr-emu-ng](https://github.com/quer3q/ampr-emu-ng) release v0.3.4 (same file as ps5-dump-forge v0.5.5 `vendor/ampr-emu-ng/`) |
| `ampr_emu-0.4.2.1/libSceAmpr.sprx` | ampr_emu 0.4.2.1 (upstream release) | 423350 | `69e6c4d5e4f5fb83c9e01815db5861c4c75734acbf4595cafa50d4c218116d1a` | [drakmor/ampr_emu](https://github.com/drakmor/ampr_emu) 0.4.2.1 (copy in ps5-dump-forge `vendor/ampr_emu/`) |

- **ampr-emu-ng** reads the ZSTD packs (`ampr_zstd.index` + `ampr_zstd-NNN.pak`) and LZ4 packs; it is installed by **Pack ZSTD**. v0.3.4 fixes black screens / freezes at launch and when loading saves (Crimson Desert, issues #13 / #30 upstream) and doubles the pack caches. v0.3.3 and v0.3.2 are still recognised in existing games.
- **ampr_emu 0.4.2.1** is put back by **Unpack ZSTD**, as ps5-dump-forge does.
- License: both are **GPL-3.0-or-later** (`GPL-3.0.txt`). Corresponding source: tag `v0.3.4` of quer3q/ampr-emu-ng and release 0.4.2.1 of drakmor/ampr_emu; ps5-dump-forge also keeps `ampr-emu-ng-0.3.4-src.tar.gz` and `ampr_emu-0.4.2.1-src.tar.gz` in its `vendor/`.
- The ZSTD pack format is reimplemented in C# (`src/PsViethoa.FpkgBuilder.Core/Zstd/`) from `docs/zstd.md` of [quer3q/ps5-dump-forge](https://github.com/quer3q/ps5-dump-forge) (GPL-3.0-or-later), ported from PSVIETHOA STUDIO 2.1.0. Files that always stay loose follow ps5-dump-forge 0.5.5: `eboot.bin`, `*.prx`, `*.sprx`, `*.elf`, `*.self`, `*.ini`, `sce_sys/`, `sce_module/`, `fakelib*/`.

Replace a runtime only together with its SHA-256 in `Zstd/AmprZstd.cs` (`NgSha256`, `NgVersion`).
