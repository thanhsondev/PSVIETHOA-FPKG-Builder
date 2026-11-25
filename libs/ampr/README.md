# AMPR runtimes (ZSTD PS5 pages, beta)

The two files below are embedded byte-for-byte in `PsViethoa.FpkgBuilder.Core.dll`. The app only ever writes them into a PS5 game folder as `fakelib/libSceAmpr.sprx`; they run on the PS5, never on the computer.

| File | Build | Bytes | SHA-256 | Source |
|---|---|---:|---|---|
| `ampr-emu-ng-0.5.0/libSceAmpr.sprx` | ampr-emu-ng v0.5.0 (release, 20 Nov 2026) | 715264 | `7379e49953d8f14f26b45ff06a863ecea56aa90a60ba3d5a375f61c52618a174` | [quer3q/ampr-emu-ng](https://github.com/quer3q/ampr-emu-ng) release v0.5.0 (matches the release `SHA256SUMS`) |
| `ampr_emu-0.4.2.1/libSceAmpr.sprx` | ampr_emu 0.4.2.1 (upstream release) | 423350 | `69e6c4d5e4f5fb83c9e01815db5861c4c75734acbf4595cafa50d4c218116d1a` | [drakmor/ampr_emu](https://github.com/drakmor/ampr_emu) 0.4.2.1 (copy in ps5-dump-forge `vendor/ampr_emu/`) |

- **ampr-emu-ng** reads the ZSTD packs (`ampr_zstd.index` + `ampr_zstd-NNN.pak`) and LZ4 packs; it is installed by **Pack ZSTD**. v0.5.0 (20 Nov 2026): up to 20% faster reads, a smaller module (150.89 MiB instead of 184.44 MiB) and a 30–40% faster index load; fixes Final Fantasy XVI, Marvel's Avengers, Demon Slayer Hinokami, GTA V and saves in games backported to FW 4.xx. The on-disk format is unchanged (`AMPRIDX3` / `AMPRZPK1` / `AMPRPAK4`, same path keys), so games packed with 0.3.x–0.4.x keep working and 0.3.0–0.4.1 are still recognised. One behaviour change: 0.5.0 no longer serves `mmap` of files inside a pack (0.4.1 decoded them to `/data/ampr_cache`; upstream found no game that does this) and deletes a leftover `/data/ampr_cache` at start.
- **ampr_emu 0.4.2.1** is put back by **Unpack ZSTD**, as ps5-dump-forge does.
- License: both are **GPL-3.0-or-later** (`GPL-3.0.txt`). Corresponding source: tag `v0.5.0` (commit d20210e) of quer3q/ampr-emu-ng (`ampr-emu-ng-0.5.0/LICENSE` is the tag's LICENSE) and release 0.4.2.1 of drakmor/ampr_emu; the v0.5.0 source archive is on the release tag; ps5-dump-forge keeps `ampr_emu-0.4.2.1-src.tar.gz` in its `vendor/`.
- The ZSTD pack format is reimplemented in C# (`src/PsViethoa.FpkgBuilder.Core/Zstd/`) from `docs/zstd.md` of [quer3q/ps5-dump-forge](https://github.com/quer3q/ps5-dump-forge) (GPL-3.0-or-later), ported from PSVIETHOA STUDIO 2.1.0. Files that always stay loose follow ps5-dump-forge 0.5.5: `eboot.bin`, `*.prx`, `*.sprx`, `*.elf`, `*.self`, `*.ini`, `sce_sys/`, `sce_module/`, `fakelib*/`.

Replace a runtime only together with its SHA-256 in `Zstd/AmprZstd.cs` (`NgSha256`, `NgVersion`).
