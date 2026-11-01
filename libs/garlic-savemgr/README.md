# garlic-savemgr (PS5 payload)

`garlic-savemgr.elf` is Garlic SaveMgr by **earthonion**: a PS5 payload that decrypts, re-encrypts and installs saves and serves an HTTP API on port 8082. The app sends it to the console's ELF loader (port 9021) only when you click **Load ELF** on the Resign Save page.

This build comes from PSVIETHOA STUDIO 1.4.3 (Ngô Phi Phương). It adds the PS5PARAM ownership fix: `/api/status` reports `ps5param_version: 1`, and the app refuses to install a PS5 save through an older build.

- SHA-256: `cda182f04acf7ca7f3be506f363c189a5801975cdd944e7788aa82fcd4b5009e`
- `src/`: the sources it was built from (`main.c`, `ps5param.h`, `ui.html`). The build also needs:
  - the SQLite amalgamation (`sqlite3.c` / `sqlite3.h`, public domain);
  - `ui.h`, generated from `ui.html` with `__VERSION__` replaced by `v1.82b-studio-1.4.3`;
  - the PS5 payload SDK (clang + ld.lld).

Replace this folder as a whole when upgrading; do not edit the ELF.
