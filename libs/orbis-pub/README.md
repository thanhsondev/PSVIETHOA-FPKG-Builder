# PS4 Fake PKG Tools 3.87 (orbis-pub-cmd)

The PS4 PKG pages build packages (Build Base / Build Update / Build DLC / Merge Base + Update) with the publishing tool from **PS4 Fake PKG Tools 3.87, patched by CyB1K** (`readme.txt`, `CyB1K.txt`). The tool is a 32-bit Windows program:

- **Windows:** runs natively.
- **macOS:** runs through the bundled Wine. Wine must keep `lib/wine/i386-windows`, so `scripts/fetch-wine.sh` keeps it. Apple Silicon also needs Rosetta 2.
- **Linux:** runs through the distro's Wine with 32-bit support (`wine32` / `wine:i386`).

| File | SHA-256 |
|---|---|
| `orbis-pub-cmd.exe` (original, full digest + integrity) | `9d136a27a0739555329e46b61208b7105d4c5aea5644cbf8052bfed4250d0e1b` |
| `orbis-pub-cmd-fast.exe` (fast build, see `FAST_ENGINE_PATCH.json`) | `9ff56997d84f743dae1c4bcd8769ab9721bf40da471c1e99d6b9fd0f0c3d48fb` |
| `orbis-pub-prx.dll` | `114978ea429842f9893cecd66552341b83b645abfb67bb8f937b814776f15427` |
| `zlib1.dll` | `6411fad0f6e2567e4853cf1ba158bdd3ae503153c01f5dad58c1dce82afa6a71` |
| `ext/sc.exe` | `f630938d30a3dbf38352765f2f300e42d98d993154eb688ee0502adabdd9790d` |
| `ext/di.exe` | `730308e99bd500ce57304873fd6bdbcba1839fe5d00b3c1e9c75266c29202650` |
| `ext/libatrac9.dll` | `e1b1f3db147d7996a5b1fbddb2b7322e56f25bbf689f47c50638fd268fc1d3c7` |

The fast build is the original with two patches by Ngô Phi Phương (PSVIETHOA STUDIO): it skips the reference-package integrity precheck and the base delta scan. The app picks it when Full verify is off.

Replace this folder as a whole when upgrading; do not edit the binaries.
