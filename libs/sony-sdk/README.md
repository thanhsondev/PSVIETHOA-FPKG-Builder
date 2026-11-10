# Plaintext PKG build toolkit

Profile: `sdk279-plaintext-direct-v6`

## Graphical interface

Run:

```bat
build-gui.bat
```

The GUI can build a PKG, verify an existing PKG, or extract it to an empty folder.
Choose APP or AC in the package-type selector. AC requires an entitlement key
(32 hexadecimal characters / 16 bytes); the GUI validates it before starting a build.
For APP, select `Auto` in `SDK version override` to use the source version or infer it
from `eboot.bin`. The list offers SDK 1.00 through SDK 13.00. A selection overrides
the generated `param.json`. The source stays unchanged;
the required system version is raised to the selected SDK version when necessary.
The selected version must match `eboot.bin` for SDK `img_verify`.
The PowerShell launcher also accepts `-SdkVersion 5` or a packed hexadecimal version.
The GUI displays the chunk count from source `sce_sys\playgo-chunk.dat` when present.
Without that file, the PlayGo chunk count is editable (1 through 800; default 100).
Use `Choose languages...` to select APP PlayGo languages. `From source` restores
automatic use of the source scenario's language list (or param.json default).
An explicit selection overrides chunk languages while retaining source scenario presentation.
The PowerShell launcher accepts `-PlayGoLanguages 'en-US,ru-RU'` for the same selection.
For APP and patch projects, a valid `sce_sys\uds\npbind.dat` identifies UDS archives.
The generator repairs a stale UCP digest while preserving all members. Missing or
unreadable `udsNN.ucp` inputs use an empty UCP stub and print a warning; this drops
the original UDS data from the package, while the source file remains unchanged.
Use `Quick verify` to check the PKG format, or `Full verify` to check both format
and integrity. When a compact patch is selected, verify
and extract automatically use its adjacent `.remastered.pkg` companion.

## Command line

Use `Restore metadata online` to recover plaintext `sce_sys` resources from the
matching-version metadata piece referenced by `versionFileUri`. Only `*_sc.pkg`
is downloaded and its manifest SHA-256 is checked. The helper saves backups in
a sibling `<source>.metadata-backups` folder and keeps local SDK/firmware/DRM
settings by default. Protected records are skipped; retain their decrypted dump
copies. This operation does not upgrade game payloads or recover `keystone`.
The GUI also accepts an exact-version manifest or metadata URL when the version
XML only offers a newer update. TLS uses normal validation plus Sony's scoped
public root CA. `metadata-recovery.exe --help` describes the standalone command.

Build with the default compression level (`7`):

```bat
build.bat "C:\path\to\project" "C:\path\to\output\game.pkg"
```

Optional arguments:

- compression level from `-4` through `9`;
- `chunks=COUNT` for the fallback of 1 through 800 PlayGo chunks when the source
  has no `sce_sys\playgo-chunk.dat` (default `100`);
- `force` to replace existing output files;
- `keep` to retain intermediate build files;
- `reference=C:\path\to\base.pkg` to build a patch. This also creates
  `<output>.remastered.pkg`.

```bat
build.bat "C:\path\to\project" "C:\path\to\output\game.pkg" 9 chunks=32 force keep
```

PowerShell equivalent:

```powershell
powershell -ExecutionPolicy Bypass -File .\build-from-folder.ps1 `
  -SourceFolder C:\path\to\project `
  -OutputPackage C:\path\to\output\game.pkg `
  -ReferencePackage C:\path\to\output\base.pkg `
  -SdkVersion 5 `
  -CompressionLevel 9 `
  -ChunkCount 32
```

APP source projects must contain an exact 96-byte `sce_sys\keystone`.
For AC, use `-Volume ac -EntitlementKey <32 hexadecimal characters>` with the
PowerShell launcher, or select AC and enter the key in the GUI. AC does not require
an APP keystone or chunk-count setting. APP-only category and DRM fields are removed
from its generated param.json. The source directory is not modified.
Build logs are written next to the output package in a
`<package-name>-build-logs` directory.

For APP builds, existing `sce_sys` presentation PNGs are used when their color
mode matches the SDK requirement. A missing or wrong-mode PNG is recovered
from its DDS into a generated folder; the source PNG is never overwritten.
`icon0*`, `pic0*`, and `pic1*` require 8-bit RGB; `pic2*` requires 8-bit RGBA
with the alpha channel preserved. The GP5 maps PNGs only; Publishing Tools
generates the reserved DDS package entries itself. PSAL builds retain their
required RGB `icon0.png` instead.

For a full APP build, the launcher chooses the smallest `attributePub` level
from the uncompressed size of files actually mapped into the GP5, plus a
metadata/alignment allowance. It writes the selection to a generated copy of
`sce_sys/param.json`; the source file is untouched. SDK 2.79 supports levels
0/1/2, and SDK 3.13 also supports level 3 with `kernel.appSizeInGib`.
`kernel.addcontMountLevel` affects the selected limit: value 1 can use lv1
but not lv2's larger limit; value 2 is capped at 97,945,387,008 bytes. If
the estimated package does not fit, the generated param.json lowers the mount
level only as far as needed (2 to 1 or 0; 1 to 0) and logs a warning. This
changes an application setting in the output package, but not the source file.
The estimate is a preflight choice, not a guarantee of final PKG size. Builds
against a reference PKG retain the source `attributePub` because patch size
alone does not determine the remastered package size.

Use the GUI's temporary-folder field or PowerShell's `-TemporaryDirectory` option
when temporary data must be placed on another drive.

The profile uses plaintext outer PFS with stock development RIF signatures and protected CNT entries.
