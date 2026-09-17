<div align="center">

<img src="docs/banner.png" alt="PSVIETHOA FPKG Builder" width="100%" />

🇻🇳 Tiếng Việt: [docs/README.vi.md](docs/README.vi.md)

**Build PS5 FPKG (FIH debug) packages from an app folder, an `.exfat` / `.ffpfsc` disk image or a GP5 project — and extract existing packages — on macOS and Windows.**

Bilingual UI (Vietnamese / English) · Speed presets · PFS v2 / v3 · `.exfat` / `.ffpfsc` images · GP5 projects · Package extraction · Update check · Free‑space & junk checks · ETA & throughput · Built‑in verification · CLI

<a href="https://github.com/thanhsondev/PSVIETHOA-FPKG-Builder/releases/latest"><img alt="Download" src="https://img.shields.io/badge/Download-Releases-22C55E?style=for-the-badge&logo=github" /></a>

![Platform](https://img.shields.io/badge/macOS-Apple%20Silicon%20%2B%20Intel-0F172A?logo=apple)
![Platform](https://img.shields.io/badge/Windows-x64-0F172A?logo=windows)
![.NET](https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet&logoColor=white)
![UI](https://img.shields.io/badge/UI-Avalonia%2011-8B5CF6)
![Languages](https://img.shields.io/badge/UI-VI%20%2F%20EN-22C55E)
![Version](https://img.shields.io/github/v/release/thanhsondev/PSVIETHOA-FPKG-Builder?label=version&color=F59E0B)
![Tests](https://img.shields.io/badge/tests-170%20passing-22C55E)

</div>

<p align="center">
  <img src="docs/screenshots/en-source.png" width="32%" />
  <img src="docs/screenshots/en-extract.png" width="32%" />
  <img src="docs/screenshots/en-advanced.png" width="32%" />
</p>

<div align="center">

**Credits:** PSVIETHOA — Nguyễn Thanh Sơn & Ngô Phi Phương · **Main project:** [Drakmor](https://github.com/) (LibProsperoPkg)

</div>

---

## Why?

Existing FPKG tooling for PS5 (`LibProsperoPkg.Gui`) is **Windows‑only WPF**. PSVIETHOA FPKG Builder is a full rewrite in **C# / .NET 10 + Avalonia UI** that runs natively on **macOS (Apple Silicon & Intel) and Windows**, adds a **bilingual interface**, accepts **`.exfat` / `.ffpfsc` disk images and GP5 projects** as sources, can **extract existing packages**, checks GitHub for **updates**, and focuses on being **smooth and fast when building very large game folders** (tens of GB). The package engine is **LibProsperoPkg** by **Drakmor** (the September 2026 build shipped with fpkg‑gui 0.6.8), so the packages it produces are byte‑for‑byte correct.

> The result is a **debug FPKG** (FIH image, signed byte `0x00`) — it installs only on a **PS5 with debug mode enabled**.

## What's new in 2.2

- **2.2.0 — packages built by Sony's Publishing Tools, on macOS and Windows**: the new default build method runs the bundled `sdk-fpkg279-fixdss3` toolchain in three steps (it always sets DRM `standard`, drops the fake licence and the AMPR/PlayGo emulator modules, and recovers missing `pic*.png` splash screens from the `.dds` copies with the bundled `prospero-dds2png.exe`). It creates a flat GP5 project, then runs `prospero-pub-cmd img_create --oformat nwonly`, then converts the result to a `PLAINTEXT_NOAUTH` package; the conversion is a byte-identical C# port of the toolchain's Python script. On Windows the SDK runs natively (Visual C++ x64 is installed for you). On macOS it runs through a bundled, trimmed Wine (Apple Silicon needs Rosetta 2). A **Sony SDK** checkbox under the progress bar (on by default, CLI `--no-sony-sdk`) switches back to the built-in engine; while it is on, the engine-only options (presets, Kraken, PFS, PlayGo, SDK override…) are greyed out and the package type is fixed to Application + PLAINTEXT_NOAUTH, as in the toolkit. The GP5 the app writes is byte-identical to the toolkit script's (same file order — it decides the package layout), the `.gp5`, scenario and `-build-logs` stay next to the package like the toolkit leaves them, and no mirror folder is needed. The source needs a 96-byte `sce_sys/keystone`. The SDK stage has its own live progress bar.

## What's new in 2.1.x

- **2.1.9 — engine from fpkg‑gui 0.6.8, package verification, DLC templates, cross‑drive fix**: the engine now regenerates every PlayGo table itself (1–255 chunks, default 100; only the counts are read from a kept `playgo-chunk.dat`, and broken PlayGo files are left out instead of stopping the build), forces DRM `"standard"` in memory, and the tool lowers `requiredSystemSoftwareVersion` to the game's SDK (`--keep-required-fw`). Every build is checked by the engine: CNT signature, the complete PlayGo layout, NAPS and inodes. An optional **full check** test-decompresses every file (`--full-verify`). Extract PKG mode gains **Quick check / Full check** buttons (`fpkg-cli verify --full`) and **Export DLC template** for DLC packages with data (`sce_sys` + a `.gp5` project, `fpkg-cli pkg-dlc-template`). A temporary folder on a different drive from the source or output now works: the mirror falls back to the system temp folder when the drive cannot hold links (exFAT/FAT32 on Windows), extracted images are patched in place, a chosen temp folder is no longer reset at startup, and FAT32 drives get a 4 GB warning. On macOS, packages built through an exFAT/FAT drive no longer contain `._*` files, and temporary folders with accented names are removed again.
- **2.1.8 — `.ffpkg` sources and no more extraction on macOS**: a `.ffpkg` dump is a FreeBSD **UFS2** filesystem image whose root is the app folder; neither macOS nor Windows can mount UFS, so the tool parses the format itself (superblock, cylinder groups, inodes, indirect blocks) and supports it everywhere `.exfat` is supported — new **.ffpkg file** button, drag and drop, `fpkg-cli --source x.ffpkg`. On macOS an `.exfat` image is now **mounted and read in place** even when files must be dropped or `param.json` patched, because the mirror folder is built on top of the read-only mount instead of extracting the whole image. The source buttons wrap to a second line on narrow windows.
- **2.1.7 — "application error" at game start, fixed at packaging time**: an FPKG is one unified image while PlayGo expects chunk-based installs, so the dump's PlayGo data makes the PS5 look for chunks that do not exist (a splash-screen hang without an error is a different, kernel-side problem). Builds now apply the known fix by default for **folders, `.exfat`, `.ffpfsc` and GP5 projects**: the `sce_sys/playgo*` files are left out (the library regenerates a matching set), `versionFileUri` is cleared and `attribute3` is set to 0 — three toggles in the advanced options, CLI `--keep-playgo` / `--keep-version-uri` / `--keep-attribute3`. `fakelib/libScePlayGo.sprx` is never touched by the tool (the engine itself drops it, like `libSceAmpr.sprx`). The leftover `ampr_emu.index` is removed too (`--keep-ampr`). The engine itself always strips `fakelib/libScePlayGo.sprx` and `fakelib/libSceAmpr.sprx` and offers no option to keep them; this tool never touches those files. Drakmor's `dlc_emu` stays in the package, and when a dump carries one the source card offers to **build one DLC package per entry of `dlc_emu.ini`** (`fpkg-cli dlc-from-ini`), each with its own RIF licence. "DRM standard" and "Drop old playgo*" are checkboxes next to the Build button, plus a **Restore defaults** link. Building over a same-named package now asks whether to overwrite, keep the old one (`… (1).pkg`) or cancel. The source folder, image and `.gp5` project are strictly read-only: the build assembles a mirror of the dump in the temporary folder out of links, drops the skipped files there and writes the patched `param.json` there, so nothing in your own folder is ever opened for writing.
- **2.1.6 — Windows mounts images too**: a read‑only virtual drive (Dokan) served by the app's own exFAT reader exposes the app folder of an `.exfat` **or `.ffpfsc`** image, so the packager reads straight from the image like `hdiutil` on macOS — no 149 GB extraction, no extra disk space; junk files are hidden and the DRM fix is applied on the virtual drive. **Windows installer** (`Setup.exe`) installs the app and the bundled Dokan driver in one go; the portable zip enables the driver by itself on first launch (only the UAC prompt) — nothing to download either way. New **Components & plugins** box in the advanced options checks for real that the engine, keys, Kraken, native Oodle, mounting and the sleep guard work. Without the driver the extraction path is used as before.
- **2.1.5 — engine from fpkg‑gui 0.6.5 + DRM fix**: `applicationDrmType` is forced to `"standard"` while building (packages built with `"free"` DRM show a lock on the PS5 and refuse to start); the source `param.json` is restored byte‑for‑byte afterwards, read‑only images are extracted instead of mounted when needed (advanced toggle, CLI `--keep-drm`). **Disk‑full recovery**: when the temp or output disk fills up the build pauses and lets you free space and retry.
- **2.1.4 — `.ffpfsc` sources**: a PS5 PFS container holding a PFSC‑compressed exFAT dump of a game opens like an `.exfat` image (decompressed on the fly, no intermediate file; separate **.ffpfsc file** button, drag & drop, `fpkg-cli inspect / build --source x.ffpfsc`). **Check for updates**: green header badge when a newer release exists (checked at every start, ↻ button, `fpkg-cli check-update`).
- **2.1.1 – 2.1.3**: Sony‑layout extraction (rebuildable `sce_sys`), automatic output folders, "Standard" preset naming, version badge, clear history, UI safety net.

- **Updated LibProsperoPkg engine** — overlapping reads / compression / writes with fewer intermediate copies, block deduplication and an in‑build compression cache, improved built‑in Kraken (especially levels 8–9), automatic fallback to built‑in Kraken when Oodle is unavailable, file‑handling and cancellation fixes, and existing packages are preserved if a rebuild fails.
- **PFS v2 / v3 selection**, configurable **Kraken block size** (128–256 KiB), **pre‑compression shuffle patterns** and **automatic shuffle analysis** (PFS v3 texture optimisation through permutation selection — very slow, fully effective only at Kraken level 9), optional **physical layout optimisation**. PFS v3 packages need **PS5 firmware 7.00 or newer**; the app warns about it.
- **Presets re‑mapped to the new encoder**: the default is now **Standard** = Kraken 4, the very encoder the 2.0.0 engine used for its "Kraken 7" (same size, same speed, plus dedup/layout gains); **Smallest** = Kraken 7 *Optimal* is a new deeper mode (~3 % smaller, 5–6× slower); new **Maximum** preset (Kraken 9 + PFS v3 + shuffle analysis).
- Live **throughput** in the build panel, smoother progress on multi‑GB files, re‑tuned ETA.
- CLI: `--pfs`, `--block-size`, `--shuffle`, `--shuffle-analysis`, `--shuffle-prediction-level`, `--skip-pfs-input-check`, `--no-layout-optimization`, `--source-mode`, `--project`, `--preset sony|standard`; new `pkg-info`, `pkg-list`, `pkg-extract` commands.
- **Extract packages** — new **Extract PKG** mode (Build | Extract bar under the header): open a `.pkg` (or drop it onto the window) to see the FIH / CNT headers, every `param.json` field, the icon and the full file list of the inner PPR‑PFS image; tick files or folders and extract them (or the whole package), or export the `sce_sys` entries. PLAINTEXT_NOAUTH packages are read straight from the `.pkg` in 4 MiB chunks without rebuilding the image; Native (AES‑XTS) packages are decrypted to the temp folder first. CLI: `pkg-info`, `pkg-list`, `pkg-extract`.
- **GP5 project sources** (`.gp5`, like fpkg‑gui's Folder | GP5 selector): pick a Publishing Tools / fpkg‑gui project in step 1 or drop it onto the window — Normal and Flat layouts, exclude masks and relative paths are honoured, the passcode comes from the project; `fpkg-cli inspect` / `build --source x.gp5`; an advanced **Source reading mode** chooses between an automatic top‑level `.gp5` and folder‑only packaging.

See [CHANGELOG.md](CHANGELOG.md) for details.

## Features

| Area | Details |
|---|---|
| **Sources** | An app folder (containing `sce_sys`) **or an exFAT disk image (`.exfat`)** — bare volume, MBR, or GPT — **or a `.ffpfsc` container** (a PS5 PFS image holding a PFSC‑compressed exFAT image; decompressed on the fly through the library, no intermediate file). A pure‑.NET exFAT reader reads `param.json` / icon / size straight from the image. On macOS the image is **mounted read‑only via `hdiutil` (no copy)**; on Windows it is **mounted as a read‑only virtual drive (Dokan, bundled — one‑click install)** served by the app's exFAT reader, which also works for `.ffpfsc`, hides junk files and applies the DRM fix on the drive. Without the driver, or on macOS when the image contains junk files, it is **extracted to the temp folder** (skipping `.DS_Store`, `._*`, `Thumbs.db`…) and cleaned up afterwards. **Or a GP5 project (`.gp5`)** from Publishing Tools / fpkg‑gui — Normal layout (`rootdir` + exclude masks) or Flat layout (explicit file list); relative paths resolve from the project's folder, the metadata card shows the layout and project root, and only the files the project lists are counted. |
| **Languages** | Vietnamese / English, switch instantly in the header, choice is remembered. CLI takes `--lang vi\|en` or the `FPKG_LANG` variable. |
| **Packaging** | FIH debug image, PLAINTEXT_NOAUTH or Native AES‑XTS, APP / Homebrew / DLC, automatic PlayGo (1–64 chunks), SDK override (1–11), passcode, deterministic builds. |
| **Compression** | Built‑in **managed Kraken encoder** (runs everywhere, multi‑threaded) or **native Oodle** via `libScePubTools.dll` (Windows, automatic fallback to built‑in Kraken). **PFS v2** (default, broadest compatibility) or **PFS v3** (pre‑compression shuffle patterns, automatic per‑block shuffle analysis), Kraken block size 128–256 KiB, physical layout optimisation. |
| **Speed** | Presets **Fast · Standard · Smallest · Maximum**. **Standard** (default) = Kraken level 4, the very encoder the 2.0.0 engine used for its "Kraken 7": same size, same speed. **Smallest** = Kraken 7 *Optimal* — a new deep-compression mode, about 3 % smaller but 5–6× slower. **Maximum** = Kraken 9 + PFS v3 + shuffle analysis. Custom level `-4…9`, thread count, and an uncompressed mode for quick tests. |
| **Before building** | Reads metadata + icon, scans size in parallel, **checks free space** on the temp/output volumes, and **detects & cleans OS junk files**. |
| **While building** | Weighted overall progress bar with **ETA** and live **throughput**, byte‑weighted progress inside multi‑GB files, virtualized 20k‑line log, safe cancel, and **sleep prevention** (`caffeinate` / `SetThreadExecutionState`). |
| **After building** | Verifies the FIH / outer‑PFS structure, optional SHA‑256, result panel, copy/save the log, open the output folder. |
| **Extract packages** | **Extract PKG** mode (extracted app trees are in Sony layout — `sce_sys/param.json`, icon, playgo merged from the CNT — so they can be rebuilt directly): FIH / CNT headers, region map, every `param.json` field, icon and file list of a debug FPKG; filter and tick files/folders, extract a selection or everything with progress and cancel, export `sce_sys` (param.json, icon0.png, playgo…) and SI entries, optional SHA‑256. Plaintext packages are read in place (4 MiB chunk cache, no temp image); Native packages are decrypted to the temp folder first; retail packages are information‑only. |
| **CLI** | `fpkg-cli build / inspect / verify / clean-junk / info / pkg-info / pkg-list / pkg-extract` for batch builds, scripting, and CI. |

## Download & run

Grab the archive for your platform from the [**Releases**](https://github.com/thanhsondev/PSVIETHOA-FPKG-Builder/releases/latest) page. Each build is **self‑contained** — no .NET install required.

- **macOS** — unzip, then right‑click `PSVIETHOA FPKG Builder.app` → **Open** the first time (the app is ad‑hoc signed).
- **Windows** — unzip and run `PSVIETHOA FPKG Builder.exe`. SmartScreen may prompt → **Run anyway**.
- **Linux** (x64 / arm64) — extract the `.tar.gz` and run `app/PsViethoa.FpkgBuilder.App` (`./install-desktop-entry.sh` adds a menu entry). The Sony SDK build method needs your distribution's Wine (`sudo apt install wine64` / `sudo dnf install wine` / `sudo pacman -S wine`); without it the built-in engine is used. Disk images are extracted with the pure-.NET reader.

Each archive also contains the `fpkg-cli` command‑line tool.

## Quick start

1. Prepare an extracted PS5 app folder (with a `sce_sys` folder and `eboot.bin`), **or** point at a `.exfat` disk image, a `.ffpfsc` container or a `.gp5` project.
2. In **step 1**, click **Browse…** / **.exfat image** / **.ffpfsc file** / **.gp5 project**, or drop the folder / file onto the window. Content ID, title, version, size and junk files are detected automatically; the output folder is set for you (toggle "Auto from source" off to choose your own).
3. Pick a speed preset in **step 3** (Standard is the default and equals the old engine's "Kraken 7"; Smallest = level 7 Optimal, slow) or open the advanced options.
4. Click **Build PKG** (`Ctrl/⌘+B` or `F5`). Watch the progress, ETA, and log; the structure is verified automatically when it finishes.

## Command line

```bash
fpkg-cli info --lang en
fpkg-cli inspect "/path/PPSA12345"                 # folder
fpkg-cli inspect "/path/PPSA12345.exfat"           # exFAT image — read directly, no mount
fpkg-cli inspect "/path/PPSA12345.ffpfsc"          # .ffpfsc container — inner exFAT decompressed on the fly
fpkg-cli check-update                              # newer release on GitHub?
fpkg-cli build --source "/path/PPSA12345.exfat" --output "/path/out"           # default: Standard (level 4), exfat auto
fpkg-cli build --source "/path/PPSA12345.exfat" --output "/path/out" --exfat extract
fpkg-cli build --source "/path/PPSA12345" --output "/path/out" --preset fast --clean-junk
fpkg-cli build --source "/path/PPSA12345" --output "/path/out" --preset maximum       # Kraken 9 + PFS v3 + shuffle analysis
fpkg-cli build --source "/path/PPSA12345" --output "/path/out" --pfs v3 --shuffle PredictForBc3 --block-size 128
fpkg-cli verify "/path/out/UP9000-PPSA12345_00-XXXX-A0100-V0100.pkg" --sha256
fpkg-cli pkg-info "/path/out/UP9000-PPSA12345_00-XXXX-A0100-V0100.pkg"                # General + param.json
fpkg-cli pkg-list "/path/out/UP9000-PPSA12345_00-XXXX-A0100-V0100.pkg" --include "*.sprx"
fpkg-cli pkg-extract "/path/out/UP9000-PPSA12345_00-XXXX-A0100-V0100.pkg" --output "/path/unpacked" --include "sce_sys/**" --include "eboot.bin" --cnt
```

Exit codes: `0` success · `1` invalid arguments · `2` build failed · `3` cancelled.

## Disk images (.exfat / .ffpfsc)

- Detected automatically by the `EXFAT   ` signature at the volume start, or inside an MBR/GPT partition; common offsets (sector 63, 2048…) are probed too.
- The app folder is located up to 3 levels deep inside the image (root first, e.g. dumps with `sce_sys` at the root).
- **macOS:** `hdiutil attach -readonly -imagekey diskimage-class=CRawDiskImage` → build straight from the mount point, unmount when done. *Automatic* only mounts a clean image; if junk files are present it extracts so they can be skipped.
- **Windows:** the app's own exFAT reader is exposed as a read‑only virtual drive through the [Dokan](https://github.com/dokan-dev/dokany) driver (`Z:\<image name>\…`), so the packager reads straight from the image — `.exfat` and `.ffpfsc` alike, junk files hidden, `param.json` DRM fix applied on the drive, nothing written to the image. The unmodified `Dokan_x64.msi` (2.3.1.1000, LGPL/MIT) ships in `app/redist/`: the **Setup.exe** installs it together with the app, the portable zip installs it by itself on first launch (only the Windows UAC prompt; declining leaves **Enable direct mounting** in the advanced options and `fpkg-cli install-dokan`). Without the driver the image is extracted to the temp folder as before.
- **Windows / Linux:** extracted with the pure‑.NET reader (3 workers, 4 MB buffers) to `<temp>/exfat-<name>-<hash>/`, needing extra free space ≈ the data in the image; removed after the build (even on cancel).
- **Validated:** the same image built two ways — hdiutil mount vs. pure‑.NET extraction — produces **byte‑identical** packages (same SHA‑256), i.e. the reader matches the macOS driver exactly.
- **`.ffpfsc` containers:** a PS5 PFS image (superblock v2, 64 KiB blocks) that holds one PFSC‑compressed file — the exFAT dump of the game. The app detects it by its header (any extension) or the `.ffpfsc` extension, layers the exFAT reader on the library's PFSC decompression (~900 MB/s, no temporary image); on Windows with Dokan it is **mounted** like an `.exfat` image, on macOS (where `hdiutil` cannot open a container) the app folder is **extracted** to the temp folder before building. Measured: 1.2 GB container (4.29 GB exFAT, 2.9 GB of game data) → info in 0.15 s, full build in 31 s.

## Performance

Measured on an Apple‑Silicon Mac (15 logical cores), built‑in Kraken, LibProsperoPkg build shipped with 2.1.0:

| Test | Config | Time | Package |
|---|---|---|---|
| 400 MB synthetic | Fast (Kraken 2) / **Standard (4, default)** | 6.8 s / 7.5 s | 254.3 MB / 254.2 MB |
| 352 MB synthetic, **30,005 files** | Standard (4) | 14 s (scan 0.12 s) | 204 MB |
| 400 MB synthetic | Smallest (Kraken 7) | 16.4 s | 250.3 MB |
| 400 MB synthetic | Maximum (Kraken 9 + PFS v3 + shuffle analysis) | 47.6 s | 249.8 MB |
| 813 MB real game (`PPSA06438`, mounted .exfat) | Smallest / Fast | 23.4 s / 4.9 s | 257.2 MB / 262.8 MB |
| **21.3 GB real game** (`PPSA27625`, mounted .exfat) | Fast (Kraken 2) | 1 min 59 s | 8.86 GiB |
| **21.3 GB real game** | **Standard (Kraken 4, default)** | **2 min 13 s** | **8.86 GiB** |
| **21.3 GB real game** | Smallest (Kraken 7 Optimal) | 12 min 49 s | 8.54 GiB |
| 1.2 GB `.ffpfsc` container (4.29 GB exFAT inside, 2.9 GB of game data) | Standard (Kraken 4) | 31 s (info 0.15 s) | 1.14 GiB |
| 36 GB package (`PPSA21567`, 166,707 files) — **Extract PKG** | list / extract | 3.8 s / ~200 MB/s | — |

For comparison, the LibProsperoPkg build shipped with 2.0.0 built the same 21.3 GB game in 3 min 40 s to a 9.03 GiB package. Measured side by side on the same 400 MB set, the 2.0.0 engine produced **identical** packages at levels 4 and 7 (254,212,194 bytes) — its "level 7" was the Normal encoder — and the 2.1.0 engine at level 4 reproduces that result (254,211,794 bytes) at the same speed. So **Standard** (the default) gives you the old "Kraken 7" size at the old speed, and on the 21 GB game it is even faster and smaller than before (2 min 13 s, 8.86 GiB). **Smallest** is a genuinely new *Optimal* mode: another 3.5 % (8.54 GiB) for a 5–6× longer build on already‑compressed game data. Levels 4–6 produce identical output; level 7 and up switch to the optimal parser.

## Build from source

Requires the [.NET SDK 10](https://dotnet.microsoft.com/download) (`brew install dotnet` on macOS).

```bash
dotnet build PsViethoa.FpkgBuilder.slnx -c Release   # build everything
scripts/run-dev.sh                                   # run the GUI (macOS/Linux)
dotnet test                                          # run the test suite

scripts/publish-macos.sh osx-arm64 osx-x64           # dist/: .app + fpkg-cli + zip
scripts/publish-windows.sh                           # dist/win-x64: .exe + fpkg-cli + zip
scripts/publish-linux.sh linux-x64 linux-arm64       # dist/: app + fpkg-cli + sony-sdk tar.gz
```

<details>
<summary>Project layout</summary>

```
PsViethoa.FpkgBuilder.slnx
Directory.Build.props                 # net10.0, version (2.1.0), GC/PGO
CHANGELOG.md
libs/                                 # LibProsperoPkg.dll, libScePubTools.dll (Windows)
src/
  PsViethoa.FpkgBuilder.Core/         # engine, validation, exFAT reader, progress, localization
  PsViethoa.FpkgBuilder.App/          # Avalonia UI (MVVM), tokens/styles, views, assets
  PsViethoa.FpkgBuilder.Cli/          # fpkg-cli
tests/PsViethoa.FpkgBuilder.Tests/    # xUnit (170 tests)
scripts/                              # publish + dev scripts
```
</details>

## Screenshots

| Light theme | PFS v3 options | Vietnamese UI |
|---|---|---|
| ![](docs/screenshots/en-light.png) | ![](docs/screenshots/en-advanced-pfs.png) | ![](docs/screenshots/vi-advanced.png) |

| While building | Extract mode (Vietnamese) | Extraction finished |
|---|---|---|
| ![](docs/screenshots/en-building.png) | ![](docs/screenshots/vi-extract.png) | ![](docs/screenshots/vi-extract-done.png) |

| Extract mode, light theme | Help — Credits |
|---|---|
| ![](docs/screenshots/en-extract-light.png) | ![](docs/screenshots/en-credits.png) |

## Credits & license

- **PSVIETHOA — Nguyễn Thanh Sơn & Ngô Phi Phương** — cross‑platform application, bilingual UI, exFAT image support, `fpkg-cli`.
- **Main project: [Drakmor](https://github.com/)** — author of **LibProsperoPkg**, the FPKG core (PFS v2/v3, NAPS, Kraken, shuffle analysis, PlayGo, package verification).
- Fonts: JetBrains Mono & Inter (SIL OFL). Icons: Material Design Icons (Apache 2.0).
- Thanks to the PS5 homebrew community and everyone who contributed to LibProsperoPkg.

> This tool builds **debug** packages for homebrew and development on debug‑enabled consoles. It ships the third‑party `LibProsperoPkg.dll` / `libScePubTools.dll` unmodified. Use responsibly.

<div align="center">

Made with care by **PSVIETHOA** · 🇻🇳

</div>
