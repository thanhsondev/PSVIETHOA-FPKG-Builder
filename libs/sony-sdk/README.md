# Independent plaintext build toolkit

Profile: `sdk279-plaintext-unsigned-v2`

This directory contains tools only. It intentionally contains no GP5 or PKG files.
The source project and all build outputs stay outside this directory.

Simple CMD invocation (the source `sce_sys/keystone` is included automatically):

```bat
build.bat "C:\path\to\project" "C:\path\to\output\game.pkg"
```

Optional words `force` and `keep` replace existing outputs and retain the raw
intermediate package. The `keystone` word remains accepted for compatibility but is
no longer required:

```bat
build.bat "C:\path\to\project" "C:\path\to\output\game.pkg" force keep
build.bat "C:\path\to\project" "C:\path\to\output\game.pkg" force keystone
```

Equivalent PowerShell invocation:

```powershell
powershell -ExecutionPolicy Bypass -File .\build-from-folder.ps1 `
  -SourceFolder C:\path\to\project `
  -OutputPackage C:\path\to\output\game.pkg
```

The command performs three explicit stages: creates `game.gp5` plus its generated
default `game.playgo-scenario.json`, builds a raw SDK package from that GP5, then
post-processes it into `game.pkg`. Logs are written to
`game-build-logs`. The intermediate raw package is removed after success; use
`-KeepIntermediate` to retain it and `-Force` to replace existing named outputs.
Every build requires an exact 96-byte source file at `sce_sys/keystone`; it is always
written into the GP5. The patched SDK accepts that file and skips its normal
passcode-based regeneration. GP5 generation uses a normalized sidecar `param.json`
with `applicationDrmType` set to `standard`. If `sce_sys/pic*.dds` has no matching PNG,
the bundled converter writes it below `.gp5-assets` beside the GP5 and maps that PNG.
A single compact standalone C++ win-x64 converter is included as
`prospero-dds2png.exe`. It does not load `LibProsperoPkg`, require .NET or use Pillow.
The source directory is not modified. During the second stage Publishing Tools displays its
native PKG creation progress directly in the console. The conversion stage displays a
second byte-based progress bar for copying and resealing the package. To preserve live
repainting, `02-img-create.log` and `03-postprocess.log` record command/timing/exit summaries;
the detailed SDK and converter messages remain visible in the console.
The profile is diagnostic plaintext/unsigned output, not an installable console image.
