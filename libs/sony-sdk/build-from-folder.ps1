param(
    [Parameter(Mandatory=$true)][string]$SourceFolder,
    [Parameter(Mandatory=$true)][string]$OutputPackage,
    [string]$ReferencePackage,
    [string]$Passcode = "00000000000000000000000000000000",
    [string]$Python = "python",
    [string]$TemporaryDirectory = $env:LIBPROSPERO_TEMP_DIR,
    [ValidateRange(-4, 9)][int]$CompressionLevel = 7,
    [switch]$KeepKeystone,
    [switch]$KeepIntermediate,
    [switch]$Force
)
$ErrorActionPreference = "Stop"
$toolkit = [IO.Path]::GetFullPath($PSScriptRoot)
$source = [IO.Path]::GetFullPath($SourceFolder)
$final = [IO.Path]::GetFullPath($OutputPackage)
$reference = $null
if (-not [string]::IsNullOrWhiteSpace($ReferencePackage)) {
    $reference = [IO.Path]::GetFullPath($ReferencePackage)
}
if (-not (Test-Path -LiteralPath $source -PathType Container)) {
    throw "SourceFolder does not exist or is not a directory: $source"
}
$sourceKeystone = Join-Path $source "sce_sys\keystone"
if (-not (Test-Path -LiteralPath $sourceKeystone -PathType Leaf)) {
    throw "This custom-keystone toolchain requires: $sourceKeystone"
}
if ((Get-Item -LiteralPath $sourceKeystone).Length -ne 96) {
    throw "The source keystone must be exactly 96 bytes: $sourceKeystone"
}
if ([IO.Path]::GetExtension($final) -ine ".pkg") {
    throw "OutputPackage must be a .pkg path."
}
if ($reference) {
    if (-not (Test-Path -LiteralPath $reference -PathType Leaf)) {
        throw "ReferencePackage does not exist or is not a file: $reference"
    }
    if ([IO.Path]::GetExtension($reference) -ine ".pkg") {
        throw "ReferencePackage must be a .pkg path."
    }
    if ($reference -ieq $final) {
        throw "ReferencePackage and OutputPackage must be different files."
    }
}
$toolkitPrefix = $toolkit.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if ($final.StartsWith($toolkitPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Build output must be outside the tools-only directory."
}
$outputDirectory = Split-Path -Parent $final
if (-not $outputDirectory) { $outputDirectory = (Get-Location).Path }
if (-not (Test-Path -LiteralPath $outputDirectory -PathType Container)) {
    New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
}
$temporaryRoot = $null
$temporaryRootCreated = $false
$temporaryBuildDirectory = $null
if (-not [string]::IsNullOrWhiteSpace($TemporaryDirectory)) {
    $expandedTemporaryDirectory = [Environment]::ExpandEnvironmentVariables($TemporaryDirectory)
    $temporaryRoot = [IO.Path]::GetFullPath($expandedTemporaryDirectory)
    if (Test-Path -LiteralPath $temporaryRoot) {
        if (-not (Test-Path -LiteralPath $temporaryRoot -PathType Container)) {
            throw "TemporaryDirectory is not a directory: $temporaryRoot"
        }
    } else {
        New-Item -ItemType Directory -Force -Path $temporaryRoot | Out-Null
        $temporaryRootCreated = $true
    }
    $temporaryBuildDirectory = Join-Path $temporaryRoot (
        "libprospero-pkg-" + [Guid]::NewGuid().ToString("N"))
}
$stem = [IO.Path]::GetFileNameWithoutExtension($final)
$gp5 = Join-Path $outputDirectory ($stem + ".gp5")
$scenario = Join-Path $outputDirectory ($stem + ".playgo-scenario.json")
$assets = Join-Path (Join-Path $outputDirectory ".gp5-assets") $stem
$partial = Join-Path $outputDirectory ($stem + ".partial.pkg")
$partialRemastered = $partial + ".remastered.pkg"
$finalRemastered = $final + ".remastered.pkg"
$metric = $partial + ".naps_metric.json"
$finalMetric = $final + ".naps_metric.json"
$logDirectory = Join-Path $outputDirectory ($stem + "-build-logs")
if ($final -ieq $partial) { throw "OutputPackage name conflicts with the temporary package name." }
if ($reference) {
    foreach ($generatedPath in @(
            $gp5, $scenario, $partial, $partialRemastered, $metric,
            $final, $finalRemastered, $finalMetric)) {
        if ($reference -ieq [IO.Path]::GetFullPath($generatedPath)) {
            throw "ReferencePackage conflicts with a generated output: $generatedPath"
        }
    }
    $assetsPath = [IO.Path]::GetFullPath($assets)
    $assetsPrefix = $assetsPath.TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if ($reference -ieq $assetsPath -or
        $reference.StartsWith($assetsPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "ReferencePackage must be outside the generated GP5 assets directory."
    }
}

$outputs = @($gp5, $scenario, $partial, $metric, $final, $finalMetric)
if ($reference) { $outputs += @($partialRemastered, $finalRemastered) }
foreach ($path in $outputs) {
    if (Test-Path -LiteralPath $path) {
        if (-not $Force) { throw "Output already exists: $path (use -Force to replace it)" }
        Remove-Item -LiteralPath $path -Force
    }
}
if (Test-Path -LiteralPath $assets) {
    if (-not $Force) { throw "Generated GP5 assets already exist: $assets (use -Force to replace them)" }
    Remove-Item -LiteralPath $assets -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $logDirectory | Out-Null
if ($temporaryBuildDirectory) {
    New-Item -ItemType Directory -Path $temporaryBuildDirectory | Out-Null
    # Publishing Tools and GP5 generation inherit this private per-build workspace.
    $env:TEMP = $temporaryBuildDirectory
    $env:TMP = $temporaryBuildDirectory
    Write-Host "Temporary build directory: $temporaryBuildDirectory"
}

Write-Host "[1/2] Creating GP5: $gp5"
$gp5Args = @(
    (Join-Path $toolkit "scripts/create-gp5-from-folder.py"),
    $source, $gp5, "--passcode", $Passcode, "--absolute-paths", "--keep-keystone")
& $Python @gp5Args 2>&1 |
    Tee-Object -FilePath (Join-Path $logDirectory "01-create-gp5.log")
if ($LASTEXITCODE -ne 0) { throw "GP5 creation failed with exit code $LASTEXITCODE" }

if ($reference) {
    Write-Host "[2/2] Building plaintext/no-auth patch against: $reference"
} else {
    Write-Host "[2/2] Building final plaintext/no-auth PKG (compression level: $CompressionLevel)"
}
$publisher = Join-Path $toolkit "toolchain/prospero-pub-cmd.exe"
$sdkStarted = [DateTime]::UtcNow
# Do not pipe native stdout through Tee-Object: Publishing Tools detects the pipe and
# stops repainting its progress bar. A direct console invocation keeps live progress.
$publisherArgs = @("img_create", "--oformat", "nwonly", "--compression_level", $CompressionLevel)
if ($reference) { $publisherArgs += @("--ref_pkg_path", $reference) }
$publisherArgs += @($gp5, $partial)
& $publisher @publisherArgs
$sdkExitCode = $LASTEXITCODE
$sdkFinished = [DateTime]::UtcNow
$loggedCommand = ($publisherArgs | ForEach-Object {
    if ([string]$_ -match '[\s"]') { '"' + ([string]$_).Replace('"', '\"') + '"' } else { [string]$_ }
}) -join ' '
@(
    "command=$loggedCommand"
    "started_utc=$($sdkStarted.ToString('o'))"
    "finished_utc=$($sdkFinished.ToString('o'))"
    "elapsed=$($sdkFinished - $sdkStarted)"
    "exit_code=$sdkExitCode"
) | Set-Content -LiteralPath (Join-Path $logDirectory "02-img-create.log") -Encoding UTF8
if ($sdkExitCode -ne 0) { throw "PKG creation failed with exit code $sdkExitCode" }
if ($reference -and -not (Test-Path -LiteralPath $partialRemastered -PathType Leaf)) {
    throw "Publisher did not create the expected remastered package: $partialRemastered"
}
Move-Item -LiteralPath $partial -Destination $final
if ($reference) {
    Move-Item -LiteralPath $partialRemastered -Destination $finalRemastered
}
if ($KeepIntermediate -and (Test-Path -LiteralPath $metric)) {
    Move-Item -LiteralPath $metric -Destination $finalMetric
}

if (-not $KeepIntermediate) {
    if ($temporaryBuildDirectory) {
        $resolvedTemporaryBuildDirectory = [IO.Path]::GetFullPath($temporaryBuildDirectory)
        $temporaryPrefix = $temporaryRoot.TrimEnd(
            [IO.Path]::DirectorySeparatorChar,
            [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
        if (-not $resolvedTemporaryBuildDirectory.StartsWith(
                $temporaryPrefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to clean a temporary directory outside TemporaryDirectory."
        }
        Remove-Item -LiteralPath $resolvedTemporaryBuildDirectory -Recurse -Force
        if ($temporaryRootCreated -and
            (Test-Path -LiteralPath $temporaryRoot -PathType Container) -and
            -not (Get-ChildItem -LiteralPath $temporaryRoot -Force | Select-Object -First 1)) {
            Remove-Item -LiteralPath $temporaryRoot -Force
        }
    }
    if (Test-Path -LiteralPath $metric) { Remove-Item -LiteralPath $metric -Force }

    # The GP5, its scenario sidecar and generated assets are build inputs created by this
    # launcher. Remove them after a successful conversion; the final PKG and logs remain.
    foreach ($path in @($gp5, $scenario)) {
        if (Test-Path -LiteralPath $path) {
            Remove-Item -LiteralPath $path -Force
        }
    }
    if (Test-Path -LiteralPath $assets) {
        $generatedAssetsRoot = [IO.Path]::GetFullPath(
            (Join-Path $outputDirectory ".gp5-assets"))
        $resolvedAssets = [IO.Path]::GetFullPath($assets)
        $generatedAssetsPrefix = $generatedAssetsRoot.TrimEnd(
            [IO.Path]::DirectorySeparatorChar,
            [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
        if (-not $resolvedAssets.StartsWith(
                $generatedAssetsPrefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to clean generated assets outside .gp5-assets."
        }
        Remove-Item -LiteralPath $resolvedAssets -Recurse -Force
        if ((Test-Path -LiteralPath $generatedAssetsRoot -PathType Container) -and
            -not (Get-ChildItem -LiteralPath $generatedAssetsRoot -Force | Select-Object -First 1)) {
            Remove-Item -LiteralPath $generatedAssetsRoot -Force
        }
    }
    Write-Host "Temporary GP5 inputs removed."
} elseif ($temporaryBuildDirectory) {
    Write-Host "Temporary workspace retained in: $temporaryBuildDirectory"
}
if ($KeepIntermediate) {
    Write-Host "Created GP5: $gp5"
}
Write-Host "Created PKG: $final"
if ($reference) {
    Write-Host "Created remastered PKG: $finalRemastered"
}
