param(
    [Parameter(Mandatory=$true)][string]$SourceFolder,
    [Parameter(Mandatory=$true)][string]$OutputPackage,
    [string]$Passcode = "00000000000000000000000000000000",
    [string]$Python = "python",
    [switch]$KeepKeystone,
    [switch]$KeepIntermediate,
    [switch]$Force
)
$ErrorActionPreference = "Stop"
$toolkit = [IO.Path]::GetFullPath($PSScriptRoot)
$source = [IO.Path]::GetFullPath($SourceFolder)
$final = [IO.Path]::GetFullPath($OutputPackage)
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
$toolkitPrefix = $toolkit.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if ($final.StartsWith($toolkitPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Build output must be outside the tools-only directory."
}
$outputDirectory = Split-Path -Parent $final
if (-not $outputDirectory) { $outputDirectory = (Get-Location).Path }
New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
$stem = [IO.Path]::GetFileNameWithoutExtension($final)
$gp5 = Join-Path $outputDirectory ($stem + ".gp5")
$scenario = Join-Path $outputDirectory ($stem + ".playgo-scenario.json")
$assets = Join-Path (Join-Path $outputDirectory ".gp5-assets") $stem
$raw = Join-Path $outputDirectory ($stem + ".sdk-plaintext.pkg")
$metric = $raw + ".naps_metric.json"
$logDirectory = Join-Path $outputDirectory ($stem + "-build-logs")
if ($final -ieq $raw) { throw "OutputPackage name conflicts with the intermediate package name." }

foreach ($path in @($gp5, $scenario, $raw, $metric, $final)) {
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

Write-Host "[1/3] Creating GP5: $gp5"
$gp5Args = @(
    (Join-Path $toolkit "scripts/create-gp5-from-folder.py"),
    $source, $gp5, "--passcode", $Passcode, "--absolute-paths", "--keep-keystone")
& $Python @gp5Args 2>&1 |
    Tee-Object -FilePath (Join-Path $logDirectory "01-create-gp5.log")
if ($LASTEXITCODE -ne 0) { throw "GP5 creation failed with exit code $LASTEXITCODE" }

Write-Host "[2/3] Building raw PKG from GP5"
$publisher = Join-Path $toolkit "toolchain/prospero-pub-cmd.exe"
$sdkStarted = [DateTime]::UtcNow
# Do not pipe native stdout through Tee-Object: Publishing Tools detects the pipe and
# stops repainting its progress bar. A direct console invocation keeps live progress.
& $publisher img_create --oformat nwonly $gp5 $raw
$sdkExitCode = $LASTEXITCODE
$sdkFinished = [DateTime]::UtcNow
@(
    "command=img_create --oformat nwonly `"$gp5`" `"$raw`""
    "started_utc=$($sdkStarted.ToString('o'))"
    "finished_utc=$($sdkFinished.ToString('o'))"
    "elapsed=$($sdkFinished - $sdkStarted)"
    "exit_code=$sdkExitCode"
) | Set-Content -LiteralPath (Join-Path $logDirectory "02-img-create.log") -Encoding UTF8
if ($sdkExitCode -ne 0) { throw "PKG creation failed with exit code $sdkExitCode" }

Write-Host "[3/3] Converting to LibProsperoPkg-compatible PKG: $final"
$postprocess = Join-Path $toolkit "scripts/postprocess-sdk279-plaintext.py"
$convertStarted = [DateTime]::UtcNow
# Keep stdout attached to the console so the Python converter can repaint its own
# byte-based progress bar while copying and resealing a large package.
& $Python $postprocess $raw $final
$convertExitCode = $LASTEXITCODE
$convertFinished = [DateTime]::UtcNow
@(
    "command=`"$Python`" `"$postprocess`" `"$raw`" `"$final`""
    "started_utc=$($convertStarted.ToString('o'))"
    "finished_utc=$($convertFinished.ToString('o'))"
    "elapsed=$($convertFinished - $convertStarted)"
    "exit_code=$convertExitCode"
) | Set-Content -LiteralPath (Join-Path $logDirectory "03-postprocess.log") -Encoding UTF8
if ($convertExitCode -ne 0) { throw "PKG postprocess failed with exit code $convertExitCode" }

if (-not $KeepIntermediate) {
    Remove-Item -LiteralPath $raw -Force
    if (Test-Path -LiteralPath $metric) { Remove-Item -LiteralPath $metric -Force }
}
Write-Host "Created GP5: $gp5"
Write-Host "Created PKG: $final"
