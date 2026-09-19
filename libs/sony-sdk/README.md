# Plaintext PKG build toolkit

Profile: `sdk279-plaintext-direct-v3`

## Graphical interface

Run:

```bat
build-gui.bat
```

The GUI can build a PKG, verify an existing PKG, or extract it to an empty folder.
Full format and integrity verification is selected by default. Use `Format only`
when the integrity pass is not needed. When a compact patch is selected, verify
and extract automatically use its adjacent `.remastered.pkg` companion.

## Command line

Build with the default compression level (`7`):

```bat
build.bat "C:\path\to\project" "C:\path\to\output\game.pkg"
```

Optional arguments:

- compression level from `-4` through `9`;
- `force` to replace existing output files;
- `keep` to retain intermediate build files;
- `reference=C:\path\to\base.pkg` to build a patch. This also creates
  `<output>.remastered.pkg`.

```bat
build.bat "C:\path\to\project" "C:\path\to\output\game.pkg" 9 force keep
```

PowerShell equivalent:

```powershell
powershell -ExecutionPolicy Bypass -File .\build-from-folder.ps1 `
  -SourceFolder C:\path\to\project `
  -OutputPackage C:\path\to\output\game.pkg `
  -ReferencePackage C:\path\to\output\base.pkg `
  -CompressionLevel 9
```

The source project must contain an exact 96-byte `sce_sys\keystone`. The source
directory is not modified. Build logs are written next to the output package in a
`<package-name>-build-logs` directory.

Use the GUI's temporary-folder field or PowerShell's `-TemporaryDirectory` option
when temporary data must be placed on another drive.

The profile is diagnostic plaintext/unsigned output, not an installable console image.
