param(
    [Parameter(Mandatory = $true)][string]$QueueFile,
    [string]$Cli = "$PSScriptRoot\..\..\src\PsViethoa.FpkgBuilder.Cli\bin\Release\net10.0\fpkg-cli.exe",
    [string]$ResultsCsv = 'C:\fpkg-research\logs\matrix.csv',
    [string]$Protected = 'G:\'
)
# Chạy tuần tự các lượt tạo gói trong QueueFile (mỗi dòng: tên|thư mục nguồn|thư mục xuất|tham số thêm|giữ gói yes/no).
# Nguồn CHỈ ĐỌC; mọi đường dẫn ghi (xuất, tạm, log) phải nằm ngoài ổ được bảo vệ — vi phạm thì dừng hẳn.
$ErrorActionPreference = 'Stop'
function Assert-Writable([string]$path) {
    $full = [IO.Path]::GetFullPath($path)
    if ($full.StartsWith([IO.Path]::GetFullPath($Protected), [StringComparison]::OrdinalIgnoreCase)) { throw "REFUSED: write path $full is on protected drive $Protected" }
    return $full
}
Assert-Writable $ResultsCsv | Out-Null
if (-not (Test-Path $ResultsCsv)) { 'name,started,finished,wall_seconds,exit,package_bytes,gp5_elapsed,img_elapsed,convert_elapsed,args' | Out-File $ResultsCsv -Encoding utf8 }
$env:FPKG_LANG = 'en'
foreach ($line in Get-Content $QueueFile -Encoding utf8) {
    if ([string]::IsNullOrWhiteSpace($line) -or $line.StartsWith('#')) { continue }
    $parts = $line.Split('|')
    $name = $parts[0].Trim(); $source = $parts[1].Trim(); $output = Assert-Writable $parts[2].Trim(); $extra = if ($parts.Count -gt 3) { $parts[3].Trim() } else { '' }; $keep = ($parts.Count -gt 4 -and $parts[4].Trim() -eq 'yes')
    $temp = Assert-Writable (Join-Path 'C:\fpkg-research\tmp' $name)
    $log = Assert-Writable (Join-Path 'C:\fpkg-research\logs' ($name + '.log'))
    New-Item -ItemType Directory -Force $output, $temp | Out-Null
    $free = (Get-PSDrive -Name ($output.Substring(0,1))).Free
    "=== $name $(Get-Date -Format o) free=$([math]::Round($free/1GB)) GB args=$extra" | Out-File $log -Encoding utf8
    $args = @('build', '--source', $source, '--output', $output, '--temp', $temp, '--overwrite') + ($extra -split ' ' | Where-Object { $_ -ne '' })
    $started = Get-Date
    & $Cli @args 2>&1 | Out-File $log -Append -Encoding utf8 -Width 500
    $exit = $LASTEXITCODE
    $finished = Get-Date
    $pkg = Get-ChildItem -LiteralPath $output -Filter '*.pkg' -File -ErrorAction SilentlyContinue | Where-Object { $_.Name -notlike '*.sdk-plaintext.pkg' } | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    $size = if ($pkg) { $pkg.Length } else { '' }
    $elapsed = @{}
    foreach ($stage in '01-create-gp5', '02-img-create', '03-postprocess') {
        $f = Get-ChildItem -LiteralPath $output -Recurse -Filter "$stage.log" -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
        $elapsed[$stage] = if ($f) { ((Get-Content $f.FullName | Where-Object { $_ -like 'elapsed=*' }) -replace 'elapsed=', '') } else { '' }
    }
    '{0},{1},{2},{3},{4},{5},{6},{7},{8},"{9}"' -f $name, $started.ToString('s'), $finished.ToString('s'), [math]::Round(($finished - $started).TotalSeconds), $exit, $size, $elapsed['01-create-gp5'], $elapsed['02-img-create'], $elapsed['03-postprocess'], $extra | Out-File $ResultsCsv -Append -Encoding utf8
    "=== done exit=$exit size=$size $(Get-Date -Format o)" | Out-File $log -Append -Encoding utf8
    if ($pkg -and -not $keep -and $exit -eq 0) {
        # Chỉ xoá gói trong thư mục xuất trên ổ nghiên cứu (đã kiểm tra không phải ổ bảo vệ) để lấy chỗ cho lượt sau.
        Remove-Item -LiteralPath (Assert-Writable $pkg.FullName) -Force
    }
    Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue
}
