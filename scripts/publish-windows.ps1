# Đóng gói bản Windows x64 ngay trên Windows (PowerShell 7+ hoặc Windows PowerShell 5.1).
# Cách dùng:  powershell -ExecutionPolicy Bypass -File scripts\publish-windows.ps1
$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$AppName = "PSVIETHOA FPKG Builder"
$Version = (Select-String -Path "$Root\Directory.Build.props" -Pattern "<Version>([^<]+)").Matches[0].Groups[1].Value
$Rid = "win-x64"
$Out = Join-Path $Root "dist\$Rid"

Write-Host "==> Publish $Rid -> $Out"
if (Test-Path $Out) { Remove-Item -Recurse -Force $Out }
New-Item -ItemType Directory -Force -Path "$Out\app", "$Out\fpkg-cli" | Out-Null

dotnet publish "$Root\src\PsViethoa.FpkgBuilder.App\PsViethoa.FpkgBuilder.App.csproj" `
  -c Release -r $Rid --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:PublishReadyToRun=true -p:DebugType=none -o "$Out\app"

dotnet publish "$Root\src\PsViethoa.FpkgBuilder.Cli\PsViethoa.FpkgBuilder.Cli.csproj" `
  -c Release -r $Rid --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:PublishReadyToRun=true -p:DebugType=none -o "$Out\fpkg-cli"

# libScePubTools.dll đã nhúng trong exe (PsViethoa.FpkgBuilder.Core), không cần chép tệp rời.
Get-ChildItem $Out -Recurse -Include *.pdb, LibProsperoPkg.xml | Remove-Item -Force
Rename-Item "$Out\app\PsViethoa.FpkgBuilder.App.exe" "$AppName.exe"

$Zip = Join-Path $Out "$AppName-$Version-$Rid.zip"
if (Test-Path $Zip) { Remove-Item $Zip }
Compress-Archive -Path "$Out\app", "$Out\fpkg-cli" -DestinationPath $Zip
Write-Host "   -> $Zip"
Write-Host "Xong."
