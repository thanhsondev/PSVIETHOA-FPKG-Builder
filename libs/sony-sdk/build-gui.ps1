#requires -Version 5.1

param(
    [switch]$ValidateOnly
)

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

[System.Windows.Forms.Application]::EnableVisualStyles()

$repoRoot = Split-Path -Parent $PSScriptRoot
$builderCandidates = @(
    (Join-Path $PSScriptRoot 'build-from-folder.ps1'),
    (Join-Path $repoRoot 'dist\plaintext-build-tools-v5\sdk279\build-from-folder.ps1'),
    (Join-Path $repoRoot 'dist\plaintext-build-tools-v5\sdk313\build-from-folder.ps1'),
    (Join-Path $repoRoot 'dist\plaintext-build-tools\sdk279\build-from-folder.ps1'),
    (Join-Path $repoRoot 'dist\plaintext-build-tools\sdk313\build-from-folder.ps1')
)
$builderScript = $builderCandidates |
    Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
    Select-Object -First 1
if ([string]::IsNullOrWhiteSpace($builderScript)) {
    $builderScript = $builderCandidates[0]
}
$toolkitRoot = Split-Path -Parent $builderScript
$publisherExecutable = Join-Path $toolkitRoot 'toolchain\prospero-pub-cmd.exe'
$metadataRecoveryCandidates = @(
    (Join-Path $PSScriptRoot 'metadata-recovery.exe'),
    (Join-Path $toolkitRoot 'metadata-recovery.exe'),
    (Join-Path $repoRoot 'src\ProsperoMetadataRecoveryTool\bin\Release\net9.0\win-x64\publish\metadata-recovery.exe'),
    (Join-Path $repoRoot 'src\ProsperoMetadataRecoveryTool\bin\Debug\net9.0\metadata-recovery.exe')
)
$metadataRecoveryExecutable = $metadataRecoveryCandidates |
    Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
$script:metadataCancelFile = $null
$script:buildProcess = $null
$script:buildStartedAt = $null
$script:finalizedProcessId = $null
$script:operationName = 'Build'
$script:operationSuccessStatus = 'Build completed successfully'
$script:operationResultFolder = $null
$script:lastResultFolder = $null
$script:processOutputStreams = @()
$script:fallbackChunkCount = 100
$script:sourceChunkCount = $null
$script:sourceChunkError = $null
$script:updatingChunkCount = $false
$script:selectedPlayGoLanguages = $null
$playGoLanguageCodes = @(
    'ja-JP', 'en-US', 'fr-FR', 'es-ES', 'de-DE', 'it-IT', 'nl-NL', 'pt-PT',
    'ru-RU', 'ko-KR', 'zh-Hant', 'zh-Hans', 'fi-FI', 'sv-SE', 'da-DK', 'no-NO',
    'pl-PL', 'pt-BR', 'en-GB', 'tr-TR', 'es-419', 'ar-AE', 'fr-CA', 'cs-CZ',
    'hu-HU', 'el-GR', 'ro-RO', 'th-TH', 'vi-VN', 'id-ID', 'uk-UA'
)

function Show-Error([string]$message) {
    [void][System.Windows.Forms.MessageBox]::Show(
        $form,
        $message,
        'PKG Build GUI',
        [System.Windows.Forms.MessageBoxButtons]::OK,
        [System.Windows.Forms.MessageBoxIcon]::Error
    )
}

function Quote-Argument([string]$value) {
    if ($null -eq $value -or $value.Length -eq 0) { return '""' }
    if ($value -notmatch '[\s"]') { return $value }

    # CommandLineToArgvW quoting: double backslashes before quotes and before
    # the closing quote. This works for both powershell.exe and SDK tools.
    $escaped = [regex]::Replace($value, '(\\*)"', {
        param($match)
        return $match.Groups[1].Value + $match.Groups[1].Value + '\"'
    })
    $escaped = [regex]::Replace($escaped, '(\\+)$', '$1$1')
    return '"' + $escaped + '"'
}

$form = New-Object System.Windows.Forms.Form
$form.Text = 'PKG Build / Verify / Extract'
$form.StartPosition = 'CenterScreen'
$form.Size = New-Object System.Drawing.Size(1050, 760)
$form.MinimumSize = New-Object System.Drawing.Size(900, 700)
$form.AutoScaleMode = [System.Windows.Forms.AutoScaleMode]::Dpi

$root = New-Object System.Windows.Forms.TableLayoutPanel
$root.Dock = 'Fill'
$root.AutoScroll = $true
$root.Padding = New-Object System.Windows.Forms.Padding(12)
$root.ColumnCount = 1
$root.RowCount = 7
[void]$root.RowStyles.Add((New-Object System.Windows.Forms.RowStyle([System.Windows.Forms.SizeType]::AutoSize)))
[void]$root.RowStyles.Add((New-Object System.Windows.Forms.RowStyle([System.Windows.Forms.SizeType]::AutoSize)))
[void]$root.RowStyles.Add((New-Object System.Windows.Forms.RowStyle([System.Windows.Forms.SizeType]::AutoSize)))
[void]$root.RowStyles.Add((New-Object System.Windows.Forms.RowStyle([System.Windows.Forms.SizeType]::AutoSize)))
[void]$root.RowStyles.Add((New-Object System.Windows.Forms.RowStyle([System.Windows.Forms.SizeType]::AutoSize)))
[void]$root.RowStyles.Add((New-Object System.Windows.Forms.RowStyle([System.Windows.Forms.SizeType]::AutoSize)))
[void]$root.RowStyles.Add((New-Object System.Windows.Forms.RowStyle([System.Windows.Forms.SizeType]::Percent, 100)))
[void]$form.Controls.Add($root)

$paths = New-Object System.Windows.Forms.GroupBox
$paths.Text = 'Build paths'
$paths.Dock = 'Top'
$paths.AutoSize = $true
$paths.AutoSizeMode = [System.Windows.Forms.AutoSizeMode]::GrowAndShrink
$paths.Padding = New-Object System.Windows.Forms.Padding(10, 18, 10, 10)
$paths.Margin = New-Object System.Windows.Forms.Padding(0, 0, 0, 8)
[void]$root.Controls.Add($paths, 0, 0)

$pathGrid = New-Object System.Windows.Forms.TableLayoutPanel
$pathGrid.Dock = 'Top'
$pathGrid.AutoSize = $true
$pathGrid.ColumnCount = 4
$pathGrid.RowCount = 5
[void]$pathGrid.ColumnStyles.Add((New-Object System.Windows.Forms.ColumnStyle([System.Windows.Forms.SizeType]::Absolute, 205)))
[void]$pathGrid.ColumnStyles.Add((New-Object System.Windows.Forms.ColumnStyle([System.Windows.Forms.SizeType]::Percent, 100)))
[void]$pathGrid.ColumnStyles.Add((New-Object System.Windows.Forms.ColumnStyle([System.Windows.Forms.SizeType]::AutoSize)))
[void]$pathGrid.ColumnStyles.Add((New-Object System.Windows.Forms.ColumnStyle([System.Windows.Forms.SizeType]::AutoSize)))
[void]$paths.Controls.Add($pathGrid)

function Add-PathControls {
    param(
        [int]$Row,
        [string]$LabelText,
        [bool]$ShowClear = $false
    )

    $label = New-Object System.Windows.Forms.Label
    $label.Text = $LabelText
    $label.AutoSize = $true
    $label.Anchor = 'Left'
    $label.Margin = New-Object System.Windows.Forms.Padding(0, 8, 8, 6)

    $text = New-Object System.Windows.Forms.TextBox
    $text.Dock = 'Fill'
    $text.Margin = New-Object System.Windows.Forms.Padding(0, 4, 8, 4)

    $browse = New-Object System.Windows.Forms.Button
    $browse.Text = 'Browse...'
    $browse.AutoSize = $true
    $browse.MinimumSize = New-Object System.Drawing.Size(88, 27)
    $browse.Margin = New-Object System.Windows.Forms.Padding(0, 2, 6, 2)

    $clear = New-Object System.Windows.Forms.Button
    $clear.Text = 'Clear'
    $clear.AutoSize = $true
    $clear.MinimumSize = New-Object System.Drawing.Size(82, 27)
    $clear.Margin = New-Object System.Windows.Forms.Padding(0, 2, 0, 2)
    $clear.Visible = $ShowClear

    [void]$pathGrid.Controls.Add($label, 0, $Row)
    [void]$pathGrid.Controls.Add($text, 1, $Row)
    [void]$pathGrid.Controls.Add($browse, 2, $Row)
    [void]$pathGrid.Controls.Add($clear, 3, $Row)

    return [PSCustomObject]@{
        TextBox = $text
        BrowseButton = $browse
        ClearButton = $clear
    }
}

$sourceControls = Add-PathControls -Row 0 -LabelText 'Source folder:'
$outputControls = Add-PathControls -Row 1 -LabelText 'Output file (.pkg):'
$referenceControls = Add-PathControls -Row 2 -LabelText 'Reference PKG (patch, optional):' -ShowClear $true
$tempControls = Add-PathControls -Row 3 -LabelText 'Temporary folder (optional):' -ShowClear $true
$extractControls = Add-PathControls -Row 4 -LabelText 'Extraction folder:' -ShowClear $true

$txtSource = $sourceControls.TextBox
$btnSource = $sourceControls.BrowseButton
$txtOutput = $outputControls.TextBox
$btnOutput = $outputControls.BrowseButton
$txtReference = $referenceControls.TextBox
$btnReference = $referenceControls.BrowseButton
$btnReferenceClear = $referenceControls.ClearButton
$txtTemp = $tempControls.TextBox
$btnTemp = $tempControls.BrowseButton
$btnTempClear = $tempControls.ClearButton
$txtExtract = $extractControls.TextBox
$btnExtractFolder = $extractControls.BrowseButton
$btnExtractClear = $extractControls.ClearButton

$hint = New-Object System.Windows.Forms.Label
$hint.Text = 'Choose APP or AC; AC requires an entitlement key. Select a reference PKG to build a patch.'
$hint.AutoSize = $true
$hint.Margin = New-Object System.Windows.Forms.Padding(0, 0, 0, 8)
[void]$root.Controls.Add($hint, 0, 1)

$options = New-Object System.Windows.Forms.FlowLayoutPanel
$options.Dock = 'Top'
$options.AutoSize = $true
$options.AutoSizeMode = 'GrowAndShrink'
$options.FlowDirection = 'LeftToRight'
$options.WrapContents = $true
$options.Margin = New-Object System.Windows.Forms.Padding(0, 4, 0, 8)

$lblVolume = New-Object System.Windows.Forms.Label
$lblVolume.Text = 'Package type:'
$lblVolume.AutoSize = $true
$lblVolume.Margin = New-Object System.Windows.Forms.Padding(0, 7, 8, 0)

$cmbVolume = New-Object System.Windows.Forms.ComboBox
$cmbVolume.DropDownStyle = [System.Windows.Forms.ComboBoxStyle]::DropDownList
$cmbVolume.Width = 185
$cmbVolume.Margin = New-Object System.Windows.Forms.Padding(0, 3, 24, 0)
[void]$cmbVolume.Items.Add('APP (application)')
[void]$cmbVolume.Items.Add('AC (additional content)')
$cmbVolume.SelectedIndex = 0

$lblCompression = New-Object System.Windows.Forms.Label
$lblCompression.Text = 'Compression (-4..9):'
$lblCompression.AutoSize = $true
$lblCompression.Margin = New-Object System.Windows.Forms.Padding(0, 7, 8, 0)

$numCompression = New-Object System.Windows.Forms.NumericUpDown
$numCompression.Minimum = -4
$numCompression.Maximum = 9
$numCompression.Value = 7
$numCompression.Width = 60
$numCompression.Margin = New-Object System.Windows.Forms.Padding(0, 3, 24, 0)

$lblSdkVersion = New-Object System.Windows.Forms.Label
$lblSdkVersion.Text = 'SDK version override:'
$lblSdkVersion.AutoSize = $true
$lblSdkVersion.Margin = New-Object System.Windows.Forms.Padding(0, 7, 8, 0)

$cmbSdkVersion = New-Object System.Windows.Forms.ComboBox
$cmbSdkVersion.DropDownStyle = [System.Windows.Forms.ComboBoxStyle]::DropDownList
$cmbSdkVersion.Width = 130
$cmbSdkVersion.Margin = New-Object System.Windows.Forms.Padding(0, 3, 24, 0)
[void]$cmbSdkVersion.Items.Add('Auto')
for ($major = 1; $major -le 13; $major++) {
    [void]$cmbSdkVersion.Items.Add("SDK $major.00")
}
$cmbSdkVersion.SelectedIndex = 0

$lblChunkCount = New-Object System.Windows.Forms.Label
$lblChunkCount.Text = 'PlayGo chunks (1..800):'
$lblChunkCount.AutoSize = $true
$lblChunkCount.Margin = New-Object System.Windows.Forms.Padding(0, 7, 8, 0)

$numChunkCount = New-Object System.Windows.Forms.NumericUpDown
$numChunkCount.Minimum = 1
$numChunkCount.Maximum = 800
$numChunkCount.Value = 100
$numChunkCount.Width = 65
$numChunkCount.Margin = New-Object System.Windows.Forms.Padding(0, 3, 24, 0)

$chunkToolTip = New-Object System.Windows.Forms.ToolTip
$chunkToolTip.SetToolTip($cmbSdkVersion,
    'APP only. Auto uses the source or eboot.bin. The selected SDK version must match eboot.bin for img_verify.')

function Read-SourceChunkCount([string]$path) {
    $stream = [IO.File]::OpenRead($path)
    try {
        $reader = New-Object IO.BinaryReader($stream)
        $header = $reader.ReadBytes(0x18)
        if ($header.Length -ne 0x18 -or
            [Text.Encoding]::ASCII.GetString($header, 0, 4) -ne 'plgx' -or
            [BitConverter]::ToUInt16($header, 4) -ne 0x1000 -or
            [BitConverter]::ToUInt16($header, 6) -ne 0 -or
            $stream.Length -lt 0x100 -or
            [BitConverter]::ToUInt32($header, 0x10) -ne $stream.Length) {
            throw 'Invalid PS5 playgo-chunk.dat header.'
        }
        $chunks = [BitConverter]::ToUInt16($header, 0x0a)
        $scenarios = [BitConverter]::ToUInt16($header, 0x0e)
        if ($chunks -lt 1 -or $chunks -gt 800 -or $scenarios -lt 1 -or $scenarios -gt 5) {
            throw "Unsupported PlayGo counts: $chunks chunks, $scenarios scenarios."
        }
        return [int]$chunks
    } finally {
        $stream.Dispose()
    }
}

function Update-SourceChunkCount {
    $script:sourceChunkCount = $null
    $script:sourceChunkError = $null
    $source = $txtSource.Text.Trim()
    try {
        if ($cmbVolume.SelectedIndex -eq 0 -and
            -not [string]::IsNullOrWhiteSpace($source) -and
            (Test-Path -LiteralPath $source -PathType Container)) {
            $chunkPath = Join-Path $source 'sce_sys\playgo-chunk.dat'
            if (Test-Path -LiteralPath $chunkPath) {
                $script:sourceChunkCount = Read-SourceChunkCount $chunkPath
            }
        }
    } catch {
        $script:sourceChunkError = $_.Exception.Message
    }
    $script:updatingChunkCount = $true
    try {
        if ($null -ne $script:sourceChunkCount) {
            $numChunkCount.Value = $script:sourceChunkCount
            $lblChunkCount.Text = 'PlayGo chunks (from source):'
            $hint = 'Read from sce_sys\playgo-chunk.dat.'
        } else {
            $numChunkCount.Value = $script:fallbackChunkCount
            $lblChunkCount.Text = 'PlayGo chunks (1..800):'
            $hint = 'Fallback when sce_sys\playgo-chunk.dat is absent.'
        }
        if ($null -ne $script:sourceChunkError) {
            $lblChunkCount.Text = 'PlayGo chunks (invalid source):'
            $hint = $script:sourceChunkError
        }
        if ($cmbVolume.SelectedIndex -eq 1) {
            $lblChunkCount.Text = 'PlayGo chunks (APP only):'
            $hint = 'AC packages do not use APP chunk declarations.'
        }
        $lblChunkCount.ForeColor = if ($null -ne $script:sourceChunkError) {
            [Drawing.Color]::DarkRed
        } else { [Drawing.SystemColors]::ControlText }
        $chunkToolTip.SetToolTip($numChunkCount, $hint)
        $chunkToolTip.SetToolTip($lblChunkCount, $hint)
        $numChunkCount.Enabled = $txtSource.Enabled -and $cmbVolume.SelectedIndex -eq 0 -and
            $null -eq $script:sourceChunkCount -and $null -eq $script:sourceChunkError
    } finally {
        $script:updatingChunkCount = $false
    }
}

$numChunkCount.Add_ValueChanged({
    if (-not $script:updatingChunkCount) {
        $script:fallbackChunkCount = [int]$numChunkCount.Value
    }
})
$txtSource.Add_TextChanged({ Update-SourceChunkCount })

$chkForce = New-Object System.Windows.Forms.CheckBox
$chkForce.Text = 'Overwrite existing files (-Force)'
$chkForce.AutoSize = $true
$chkForce.Margin = New-Object System.Windows.Forms.Padding(0, 6, 0, 0)

function Add-OptionPair([Windows.Forms.Control]$captionControl, [Windows.Forms.Control]$editorControl) {
    $pair = New-Object Windows.Forms.FlowLayoutPanel
    $pair.AutoSize = $true
    $pair.AutoSizeMode = 'GrowAndShrink'
    $pair.WrapContents = $false
    $pair.Margin = New-Object Windows.Forms.Padding(0, 0, 20, 6)
    $captionControl.Margin = New-Object Windows.Forms.Padding(0, 6, 8, 0)
    $editorControl.Margin = New-Object Windows.Forms.Padding(0, 2, 0, 0)
    [void]$pair.Controls.Add($captionControl)
    [void]$pair.Controls.Add($editorControl)
    [void]$options.Controls.Add($pair)
}

Add-OptionPair $lblVolume $cmbVolume
Add-OptionPair $lblCompression $numCompression
Add-OptionPair $lblSdkVersion $cmbSdkVersion
Add-OptionPair $lblChunkCount $numChunkCount
[void]$options.Controls.Add($chkForce)

$buildOptions = New-Object System.Windows.Forms.TableLayoutPanel
$buildOptions.Dock = 'Top'
$buildOptions.AutoSize = $true
$buildOptions.ColumnCount = 1
$buildOptions.RowCount = 3
$buildOptions.Margin = New-Object System.Windows.Forms.Padding(0)
[void]$buildOptions.RowStyles.Add((New-Object System.Windows.Forms.RowStyle([System.Windows.Forms.SizeType]::AutoSize)))
[void]$buildOptions.RowStyles.Add((New-Object System.Windows.Forms.RowStyle([System.Windows.Forms.SizeType]::AutoSize)))
[void]$buildOptions.RowStyles.Add((New-Object System.Windows.Forms.RowStyle([System.Windows.Forms.SizeType]::AutoSize)))
[void]$buildOptions.Controls.Add($options, 0, 0)

$credentials = New-Object System.Windows.Forms.FlowLayoutPanel
$credentials.Dock = 'Top'
$credentials.AutoSize = $true
$credentials.WrapContents = $true
$credentials.Margin = New-Object System.Windows.Forms.Padding(0, 0, 0, 8)

$passcodePanel = New-Object System.Windows.Forms.FlowLayoutPanel
$passcodePanel.AutoSize = $true
$passcodePanel.AutoSizeMode = 'GrowAndShrink'
$passcodePanel.WrapContents = $false
$passcodePanel.Margin = New-Object System.Windows.Forms.Padding(0)
[void]$credentials.Controls.Add($passcodePanel)

$lblPasscode = New-Object System.Windows.Forms.Label
$lblPasscode.Text = 'Passcode:'
$lblPasscode.AutoSize = $true
$lblPasscode.Margin = New-Object System.Windows.Forms.Padding(0, 7, 8, 0)
[void]$passcodePanel.Controls.Add($lblPasscode)

$txtPasscode = New-Object System.Windows.Forms.TextBox
$txtPasscode.Text = '00000000000000000000000000000000'
$txtPasscode.Width = 250
$txtPasscode.MaxLength = 32
$txtPasscode.Font = New-Object System.Drawing.Font('Consolas', 9)
$txtPasscode.Margin = New-Object System.Windows.Forms.Padding(0, 3, 0, 0)
[void]$passcodePanel.Controls.Add($txtPasscode)

$entitlementPanel = New-Object System.Windows.Forms.FlowLayoutPanel
$entitlementPanel.AutoSize = $true
$entitlementPanel.AutoSizeMode = 'GrowAndShrink'
$entitlementPanel.WrapContents = $false
$entitlementPanel.Margin = New-Object System.Windows.Forms.Padding(24, 0, 0, 0)
[void]$credentials.Controls.Add($entitlementPanel)

$lblEntitlementKey = New-Object System.Windows.Forms.Label
$lblEntitlementKey.Text = 'Entitlement key (required):'
$lblEntitlementKey.AutoSize = $true
$lblEntitlementKey.Margin = New-Object System.Windows.Forms.Padding(0, 7, 8, 0)
[void]$entitlementPanel.Controls.Add($lblEntitlementKey)

$txtEntitlementKey = New-Object System.Windows.Forms.TextBox
$txtEntitlementKey.Width = 250
$txtEntitlementKey.MaxLength = 32
$txtEntitlementKey.Font = New-Object System.Drawing.Font('Consolas', 9)
$txtEntitlementKey.Margin = New-Object System.Windows.Forms.Padding(0, 3, 0, 0)
[void]$entitlementPanel.Controls.Add($txtEntitlementKey)
$chunkToolTip.SetToolTip($txtEntitlementKey, 'AC requires 32 hexadecimal characters (16 bytes).')
[void]$buildOptions.Controls.Add($credentials, 0, 1)

$languagePanel = New-Object System.Windows.Forms.FlowLayoutPanel
$languagePanel.Dock = 'Top'
$languagePanel.AutoSize = $true
$languagePanel.Margin = New-Object System.Windows.Forms.Padding(0, 0, 0, 8)
$lblLanguages = New-Object System.Windows.Forms.Label
$lblLanguages.Text = 'PlayGo languages:'
$lblLanguages.AutoSize = $true
$lblLanguages.Margin = New-Object System.Windows.Forms.Padding(0, 7, 8, 0)
$txtLanguages = New-Object System.Windows.Forms.TextBox
$txtLanguages.ReadOnly = $true
$txtLanguages.Text = 'From source (preserve PlayGo layout)'
$txtLanguages.Width = 370
$txtLanguages.Margin = New-Object System.Windows.Forms.Padding(0, 3, 8, 0)
$btnLanguages = New-Object System.Windows.Forms.Button
$btnLanguages.Text = 'Choose languages...'
$btnLanguages.AutoSize = $true
$btnLanguages.MinimumSize = New-Object System.Drawing.Size(130, 28)
$btnSourceLanguages = New-Object System.Windows.Forms.Button
$btnSourceLanguages.Text = 'From source'
$btnSourceLanguages.AutoSize = $true
$btnSourceLanguages.MinimumSize = New-Object System.Drawing.Size(100, 28)
$languageInput = New-Object Windows.Forms.FlowLayoutPanel
$languageInput.AutoSize = $true
$languageInput.AutoSizeMode = 'GrowAndShrink'
$languageInput.WrapContents = $false
$languageInput.Margin = New-Object Windows.Forms.Padding(0)
$languageInput.Controls.AddRange(@($lblLanguages, $txtLanguages))
$languageActions = New-Object Windows.Forms.FlowLayoutPanel
$languageActions.AutoSize = $true
$languageActions.AutoSizeMode = 'GrowAndShrink'
$languageActions.WrapContents = $false
$languageActions.Margin = New-Object Windows.Forms.Padding(0)
$languageActions.Controls.AddRange(@($btnLanguages, $btnSourceLanguages))
$languagePanel.Controls.AddRange(@($languageInput, $languageActions))
[void]$buildOptions.Controls.Add($languagePanel, 0, 2)
[void]$root.Controls.Add($buildOptions, 0, 2)

function Set-PlayGoLanguages([string[]]$languages) {
    if ($null -ne $languages) {
        if ($languages.Count -eq 0 -or @($languages | Where-Object { $_ -notin $playGoLanguageCodes }).Count -gt 0) {
            throw 'Select at least one supported PlayGo language.'
        }
        $languages = @($playGoLanguageCodes | Where-Object { $_ -in $languages })
    }
    $script:selectedPlayGoLanguages = $languages
    $txtLanguages.Text = if ($null -eq $languages) { 'From source (preserve PlayGo layout)' } else { $languages -join ', ' }
    $chunkToolTip.SetToolTip($txtLanguages, $txtLanguages.Text)
}

function Get-PlayGoLanguageArguments {
    if ($cmbVolume.SelectedIndex -eq 0 -and $null -ne $script:selectedPlayGoLanguages) {
        return @('-PlayGoLanguages', ($script:selectedPlayGoLanguages -join ','))
    }
    return @()
}

function Get-SourcePlayGoLanguages {
    $source = $txtSource.Text.Trim()
    if (-not [string]::IsNullOrWhiteSpace($source)) {
        # Use the generator's validated masks, rather than an incomplete scenario JSON list.
        # This preview only seeds the explicit language picker; failed source-map reads fall
        # through to presentation metadata so the user can replace a damaged source layout.
        $generator = Join-Path $PSScriptRoot 'create-gp5-from-folder.py'
        if (-not (Test-Path -LiteralPath $generator)) { $generator = Join-Path $toolkitRoot 'create-gp5-from-folder.py' }
        if ((Test-Path -LiteralPath $generator) -and
            (Test-Path -LiteralPath (Join-Path $source 'sce_sys\playgo-hash-table.dat')) -and
            (Test-Path -LiteralPath (Join-Path $source 'sce_sys\playgo-ficm.dat')) -and
            (Test-Path -LiteralPath (Join-Path $source 'sce_sys\playgo-chunk.dat'))) {
            $previewCode = 'import json,runpy,sys; from pathlib import Path; m=runpy.run_path(sys.argv[1]); layout=m[''read_source_playgo_layout''](Path(sys.argv[2])); print(json.dumps(layout.languages if layout else []))'
            try {
                $preview = @(& python -c $previewCode $generator $source 2>&1)
                if ($LASTEXITCODE -eq 0) {
                    $languages = ConvertFrom-Json -InputObject ($preview -join "`n")
                    if ($languages.Count -gt 0) { return $languages }
                }
            } catch { } # The builder validates source maps; explicit languages replace them.
        }
        $scenarioPath = Join-Path $source 'sce_sys\playgo-scenario.json'
        if (Test-Path -LiteralPath $scenarioPath -PathType Leaf) {
            $scenario = Get-Content -LiteralPath $scenarioPath -Raw -Encoding UTF8 | ConvertFrom-Json
            if ($scenario.chunkSupportedLanguages) { return @($scenario.chunkSupportedLanguages) }
            $codes = @($scenario.chunkDefaultLanguage, $scenario.scenarioDefaultLanguage)
            foreach ($entry in $scenario.scenarios) { $codes += @($entry.PSObject.Properties.Name) }
            return @($playGoLanguageCodes | Where-Object { $_ -in $codes })
        }
        $paramPath = Join-Path $source 'sce_sys\param.json'
        if (Test-Path -LiteralPath $paramPath -PathType Leaf) {
            $param = Get-Content -LiteralPath $paramPath -Raw -Encoding UTF8 | ConvertFrom-Json
            if ($param.localizedParameters.defaultLanguage -in $playGoLanguageCodes) {
                return @($param.localizedParameters.defaultLanguage)
            }
        }
    }
    return @('en-US')
}

$btnSourceLanguages.Add_Click({ Set-PlayGoLanguages $null })
$btnLanguages.Add_Click({
    try {
        $selected = if ($null -eq $script:selectedPlayGoLanguages) { @(Get-SourcePlayGoLanguages) }
                    else { $script:selectedPlayGoLanguages }
    } catch { Show-Error ('Unable to read source PlayGo languages: ' + $_.Exception.Message); return }
    $dialog = New-Object System.Windows.Forms.Form
    $dialog.Text = 'PlayGo languages'
    $dialog.StartPosition = 'CenterParent'
    $dialog.Size = New-Object System.Drawing.Size(460, 500)
    $dialog.MinimumSize = New-Object System.Drawing.Size(400, 350)
    $dialog.AutoScaleMode = 'Dpi'
    $dialog.ShowInTaskbar = $false
    $dialog.MinimizeBox = $false
    $list = New-Object System.Windows.Forms.CheckedListBox
    $list.Dock = 'Fill'
    $list.CheckOnClick = $true
    $list.IntegralHeight = $false
    foreach ($code in $playGoLanguageCodes) {
        $name = [Globalization.CultureInfo]::GetCultureInfo($code).EnglishName
        if ($code -eq 'no-NO') { $name = 'Norwegian (Norway)' }
        [void]$list.Items.Add(($code + ' - ' + $name), ($code -in $selected))
    }
    $actions = New-Object System.Windows.Forms.FlowLayoutPanel
    $actions.Dock = 'Bottom'
    $actions.AutoSize = $true
    $all = New-Object System.Windows.Forms.Button
    $all.Text = 'Select all'
    $all.Add_Click({ for ($i = 0; $i -lt $list.Items.Count; $i++) { $list.SetItemChecked($i, $true) } })
    $none = New-Object System.Windows.Forms.Button
    $none.Text = 'Clear'
    $none.Add_Click({ for ($i = 0; $i -lt $list.Items.Count; $i++) { $list.SetItemChecked($i, $false) } })
    $ok = New-Object System.Windows.Forms.Button
    $ok.Text = 'OK'
    $ok.Enabled = $list.CheckedIndices.Count -gt 0
    $list.Add_ItemCheck({
        $count = $list.CheckedIndices.Count
        if ($_.NewValue -eq [Windows.Forms.CheckState]::Checked) { $count++ }
        if ($_.CurrentValue -eq [Windows.Forms.CheckState]::Checked) { $count-- }
        $ok.Enabled = $count -gt 0
    })
    $ok.Add_Click({
        if ($list.CheckedIndices.Count -eq 0) {
            [void][Windows.Forms.MessageBox]::Show($dialog, 'Select at least one language.', 'PlayGo languages')
            return
        }
        $dialog.DialogResult = [Windows.Forms.DialogResult]::OK
    })
    $cancel = New-Object System.Windows.Forms.Button
    $cancel.Text = 'Cancel'
    $cancel.DialogResult = [Windows.Forms.DialogResult]::Cancel
    foreach ($action in @($all, $none, $ok, $cancel)) { [void]$actions.Controls.Add($action) }
    [void]$dialog.Controls.Add($list)
    [void]$dialog.Controls.Add($actions)
    $dialog.AcceptButton = $ok
    $dialog.CancelButton = $cancel
    try {
        if ($dialog.ShowDialog($form) -eq [Windows.Forms.DialogResult]::OK) {
            Set-PlayGoLanguages @($list.CheckedIndices | ForEach-Object { $playGoLanguageCodes[$_] })
        }
    } finally { $dialog.Dispose() }
})

function Get-BuildVolumeArguments {
    if ($cmbVolume.SelectedIndex -eq 0) { return @('-Volume', 'app') }
    $key = $txtEntitlementKey.Text.Trim()
    if ($key -notmatch '\A[0-9A-Fa-f]{32}\z') {
        throw 'AC requires an entitlement key: exactly 32 hexadecimal characters (16 bytes).'
    }
    return @('-Volume', 'ac', '-EntitlementKey', $key.ToUpperInvariant())
}

function Get-SdkVersionOverrideArguments {
    if ($cmbVolume.SelectedIndex -ne 0) { return @() }
    $major = $cmbSdkVersion.SelectedIndex
    if ($major -eq 0) { return @() }
    if ($major -lt 1 -or $major -gt 13) {
        throw 'Select Auto or an SDK version from 1.00 through 13.00.'
    }
    return @('-SdkVersion', [string]$major)
}

$cmbVolume.Add_SelectedIndexChanged({
    Set-BuildControlsEnabled $txtSource.Enabled
    Update-SourceChunkCount
})

$buttons = New-Object System.Windows.Forms.FlowLayoutPanel
$buttons.Dock = 'Top'
$buttons.AutoSize = $true
$buttons.AutoSizeMode = 'GrowAndShrink'
$buttons.FlowDirection = 'LeftToRight'
$buttons.WrapContents = $true
$buttons.Margin = New-Object System.Windows.Forms.Padding(0, 0, 0, 8)

$btnBuild = New-Object System.Windows.Forms.Button
$btnBuild.Text = 'Build'
$btnBuild.AutoSize = $true
$btnBuild.MinimumSize = New-Object System.Drawing.Size(110, 32)

$btnRecoverMetadata = New-Object System.Windows.Forms.Button
$btnRecoverMetadata.Text = 'Restore metadata online'
$btnRecoverMetadata.AutoSize = $true
$btnRecoverMetadata.MinimumSize = New-Object System.Drawing.Size(165, 32)
$chunkToolTip.SetToolTip($btnRecoverMetadata,
    'Download matching-version *_sc.pkg from versionFileUri. Back up originals and restore plaintext metadata.')

$btnSelectPackage = New-Object System.Windows.Forms.Button
$btnSelectPackage.Text = 'Select existing PKG...'
$btnSelectPackage.AutoSize = $true
$btnSelectPackage.MinimumSize = New-Object System.Drawing.Size(155, 32)

$btnQuickVerify = New-Object System.Windows.Forms.Button
$btnQuickVerify.Text = 'Quick verify'
$btnQuickVerify.AutoSize = $true
$btnQuickVerify.MinimumSize = New-Object System.Drawing.Size(110, 32)

$btnFullVerify = New-Object System.Windows.Forms.Button
$btnFullVerify.Text = 'Full verify'
$btnFullVerify.AutoSize = $true
$btnFullVerify.MinimumSize = New-Object System.Drawing.Size(110, 32)
$chunkToolTip.SetToolTip($btnQuickVerify, 'Check the PKG format without the integrity pass.')
$chunkToolTip.SetToolTip($btnFullVerify, 'Check the PKG format and integrity.')

$btnExtract = New-Object System.Windows.Forms.Button
$btnExtract.Text = 'Extract PKG'
$btnExtract.AutoSize = $true
$btnExtract.MinimumSize = New-Object System.Drawing.Size(110, 32)

$btnOpenOutput = New-Object System.Windows.Forms.Button
$btnOpenOutput.Text = 'Open result folder'
$btnOpenOutput.AutoSize = $true
$btnOpenOutput.Enabled = $false
$btnOpenOutput.MinimumSize = New-Object System.Drawing.Size(180, 32)

$btnClearLog = New-Object System.Windows.Forms.Button
$btnClearLog.Text = 'Clear log'
$btnClearLog.AutoSize = $true
$btnClearLog.MinimumSize = New-Object System.Drawing.Size(110, 32)
function New-ActionGroup([Windows.Forms.Control[]]$controls) {
    $group = New-Object Windows.Forms.FlowLayoutPanel
    $group.AutoSize = $true
    $group.AutoSizeMode = 'GrowAndShrink'
    $group.WrapContents = $false
    $group.Margin = New-Object Windows.Forms.Padding(0, 0, 16, 0)
    $group.Controls.AddRange($controls)
    return $group
}

$buildActions = New-ActionGroup @($btnBuild, $btnRecoverMetadata)
$packageActions = New-ActionGroup @($btnSelectPackage, $btnQuickVerify, $btnFullVerify, $btnExtract)
$utilityActions = New-ActionGroup @($btnOpenOutput, $btnClearLog)
$buttons.Controls.AddRange(@($buildActions, $packageActions, $utilityActions))
$buttons.SetFlowBreak($packageActions, $true)
[void]$root.Controls.Add($buttons, 0, 3)

$statusPanel = New-Object System.Windows.Forms.FlowLayoutPanel
$statusPanel.Dock = 'Top'
$statusPanel.AutoSize = $true
$statusPanel.FlowDirection = 'LeftToRight'
$statusPanel.WrapContents = $false
$statusPanel.Margin = New-Object System.Windows.Forms.Padding(0, 0, 0, 4)

$lblStatusCaption = New-Object System.Windows.Forms.Label
$lblStatusCaption.Text = 'Status:'
$lblStatusCaption.AutoSize = $true
$lblStatusCaption.Margin = New-Object System.Windows.Forms.Padding(0, 4, 6, 0)
[void]$statusPanel.Controls.Add($lblStatusCaption)

$lblStatus = New-Object System.Windows.Forms.Label
$lblStatus.Text = 'Ready'
$lblStatus.AutoSize = $true
$lblStatus.Margin = New-Object System.Windows.Forms.Padding(0, 4, 0, 0)
[void]$statusPanel.Controls.Add($lblStatus)
[void]$root.Controls.Add($statusPanel, 0, 4)

$lblLog = New-Object System.Windows.Forms.Label
$lblLog.Text = 'Operation log:'
$lblLog.AutoSize = $true
$lblLog.Margin = New-Object System.Windows.Forms.Padding(0, 0, 0, 4)
[void]$root.Controls.Add($lblLog, 0, 5)

$log = New-Object System.Windows.Forms.RichTextBox
$log.Dock = 'Fill'
$log.MinimumSize = New-Object Drawing.Size(0, 120)
$log.ReadOnly = $true
$log.WordWrap = $false
$log.DetectUrls = $false
$log.Font = New-Object System.Drawing.Font('Consolas', 9)
$log.HideSelection = $false
[void]$root.Controls.Add($log, 0, 6)

$sourceFolderDialog = New-Object System.Windows.Forms.FolderBrowserDialog
$sourceFolderDialog.Description = 'Select the source project folder'
$sourceFolderDialog.ShowNewFolderButton = $false

$tempFolderDialog = New-Object System.Windows.Forms.FolderBrowserDialog
$tempFolderDialog.Description = 'Select the temporary folder'
$tempFolderDialog.ShowNewFolderButton = $true

$extractFolderDialog = New-Object System.Windows.Forms.FolderBrowserDialog
$extractFolderDialog.Description = 'Select an empty extraction folder'
$extractFolderDialog.ShowNewFolderButton = $true

$saveDialog = New-Object System.Windows.Forms.SaveFileDialog
$saveDialog.Title = 'Select the output PKG file'
$saveDialog.Filter = 'PKG package (*.pkg)|*.pkg|All files (*.*)|*.*'
$saveDialog.DefaultExt = 'pkg'
$saveDialog.AddExtension = $true
$saveDialog.OverwritePrompt = $false

$openPackageDialog = New-Object System.Windows.Forms.OpenFileDialog
$openPackageDialog.Title = 'Select a PKG image'
$openPackageDialog.Filter = 'PKG package (*.pkg)|*.pkg|All files (*.*)|*.*'
$openPackageDialog.CheckFileExists = $true
$openPackageDialog.Multiselect = $false

$openReferenceDialog = New-Object System.Windows.Forms.OpenFileDialog
$openReferenceDialog.Title = 'Select the reference PKG image'
$openReferenceDialog.Filter = 'PKG package (*.pkg)|*.pkg|All files (*.*)|*.*'
$openReferenceDialog.CheckFileExists = $true
$openReferenceDialog.Multiselect = $false

$btnSource.Add_Click({
    if ($txtSource.Text -and (Test-Path -LiteralPath $txtSource.Text -PathType Container)) {
        $sourceFolderDialog.SelectedPath = $txtSource.Text
    }
    if ($sourceFolderDialog.ShowDialog($form) -eq [System.Windows.Forms.DialogResult]::OK) {
        $txtSource.Text = $sourceFolderDialog.SelectedPath
        if ([string]::IsNullOrWhiteSpace($txtOutput.Text)) {
            $sourceInfo = Get-Item -LiteralPath $sourceFolderDialog.SelectedPath
            if ($null -ne $sourceInfo.Parent) {
                $txtOutput.Text = Join-Path $sourceInfo.Parent.FullName ($sourceInfo.Name + '.pkg')
                if ([string]::IsNullOrWhiteSpace($txtExtract.Text)) {
                    $txtExtract.Text = Join-Path $sourceInfo.Parent.FullName ($sourceInfo.Name + '-extracted')
                }
            }
        }
    }
})

$btnOutput.Add_Click({
    if (-not [string]::IsNullOrWhiteSpace($txtOutput.Text)) {
        try {
            $fullOutput = [IO.Path]::GetFullPath($txtOutput.Text)
            $initialDir = Split-Path -Parent $fullOutput
            if ($initialDir -and (Test-Path -LiteralPath $initialDir -PathType Container)) {
                $saveDialog.InitialDirectory = $initialDir
            }
            $saveDialog.FileName = Split-Path -Leaf $fullOutput
        } catch { }
    } elseif (-not [string]::IsNullOrWhiteSpace($txtSource.Text)) {
        try {
            $sourceItem = Get-Item -LiteralPath $txtSource.Text -ErrorAction Stop
            if ($null -ne $sourceItem.Parent) {
                $saveDialog.InitialDirectory = $sourceItem.Parent.FullName
                $saveDialog.FileName = $sourceItem.Name + '.pkg'
            }
        } catch { }
    }

    if ($saveDialog.ShowDialog($form) -eq [System.Windows.Forms.DialogResult]::OK) {
        $txtOutput.Text = $saveDialog.FileName
        if ([string]::IsNullOrWhiteSpace($txtExtract.Text)) {
            $txtExtract.Text = [IO.Path]::Combine(
                [IO.Path]::GetDirectoryName($saveDialog.FileName),
                [IO.Path]::GetFileNameWithoutExtension($saveDialog.FileName) + '-extracted')
        }
    }
})

$btnReference.Add_Click({
    if (-not [string]::IsNullOrWhiteSpace($txtReference.Text)) {
        try {
            $fullReference = [IO.Path]::GetFullPath($txtReference.Text)
            $referenceDirectory = Split-Path -Parent $fullReference
            if (Test-Path -LiteralPath $referenceDirectory -PathType Container) {
                $openReferenceDialog.InitialDirectory = $referenceDirectory
            }
            $openReferenceDialog.FileName = Split-Path -Leaf $fullReference
        } catch { }
    }
    if ($openReferenceDialog.ShowDialog($form) -eq [System.Windows.Forms.DialogResult]::OK) {
        $txtReference.Text = $openReferenceDialog.FileName
    }
})

$btnTemp.Add_Click({
    if ($txtTemp.Text -and (Test-Path -LiteralPath $txtTemp.Text -PathType Container)) {
        $tempFolderDialog.SelectedPath = $txtTemp.Text
    }
    if ($tempFolderDialog.ShowDialog($form) -eq [System.Windows.Forms.DialogResult]::OK) {
        $txtTemp.Text = $tempFolderDialog.SelectedPath
    }
})

$btnTempClear.Add_Click({ $txtTemp.Clear() })
$btnReferenceClear.Add_Click({ $txtReference.Clear() })
$btnExtractClear.Add_Click({ $txtExtract.Clear() })
$btnClearLog.Add_Click({
    $log.Clear()
    foreach ($stream in $script:processOutputStreams) { $stream.LogStart = -1; $stream.LogLength = 0 }
})

$btnExtractFolder.Add_Click({
    if ($txtExtract.Text -and (Test-Path -LiteralPath $txtExtract.Text -PathType Container)) {
        $extractFolderDialog.SelectedPath = $txtExtract.Text
    }
    if ($extractFolderDialog.ShowDialog($form) -eq [System.Windows.Forms.DialogResult]::OK) {
        $txtExtract.Text = $extractFolderDialog.SelectedPath
    }
})

$btnSelectPackage.Add_Click({
    if (-not [string]::IsNullOrWhiteSpace($txtOutput.Text)) {
        try {
            $currentPackage = [IO.Path]::GetFullPath($txtOutput.Text)
            $currentDirectory = Split-Path -Parent $currentPackage
            if (Test-Path -LiteralPath $currentDirectory -PathType Container) {
                $openPackageDialog.InitialDirectory = $currentDirectory
            }
            $openPackageDialog.FileName = Split-Path -Leaf $currentPackage
        } catch { }
    }
    if ($openPackageDialog.ShowDialog($form) -eq [System.Windows.Forms.DialogResult]::OK) {
        $txtOutput.Text = $openPackageDialog.FileName
        $txtExtract.Text = [IO.Path]::Combine(
            [IO.Path]::GetDirectoryName($openPackageDialog.FileName),
            [IO.Path]::GetFileNameWithoutExtension($openPackageDialog.FileName) + '-extracted')
    }
})

$btnOpenOutput.Add_Click({
    if (-not [string]::IsNullOrWhiteSpace($script:lastResultFolder)) {
        try {
            if (Test-Path -LiteralPath $script:lastResultFolder -PathType Container) {
                Start-Process explorer.exe -ArgumentList @($script:lastResultFolder)
            }
        } catch { }
    }
})

function Append-LogLine([string]$line) {
    if ($null -eq $line -or $form.IsDisposed) { return }
    if ($form.InvokeRequired) {
        $captured = $line
        [void]$form.BeginInvoke([Action]{ Append-LogLine $captured })
        return
    }
    $log.AppendText($line + [Environment]::NewLine)
    $log.SelectionStart = $log.TextLength
    $log.ScrollToCaret()
}

function Set-BuildControlsEnabled([bool]$enabled) {
    $txtSource.Enabled = $enabled
    $btnSource.Enabled = $enabled
    $txtOutput.Enabled = $enabled
    $btnOutput.Enabled = $enabled
    $txtReference.Enabled = $enabled
    $btnReference.Enabled = $enabled
    $btnReferenceClear.Enabled = $enabled
    $txtTemp.Enabled = $enabled
    $btnTemp.Enabled = $enabled
    $btnTempClear.Enabled = $enabled
    $txtExtract.Enabled = $enabled
    $btnExtractFolder.Enabled = $enabled
    $btnExtractClear.Enabled = $enabled
    $numCompression.Enabled = $enabled
    $cmbSdkVersion.Enabled = $enabled -and $cmbVolume.SelectedIndex -eq 0
    $cmbVolume.Enabled = $enabled
    $usesEntitlement = $cmbVolume.SelectedIndex -eq 1
    $entitlementPanel.Visible = $usesEntitlement
    $lblEntitlementKey.Visible = $usesEntitlement
    $txtEntitlementKey.Visible = $usesEntitlement
    $txtEntitlementKey.Enabled = $enabled -and $usesEntitlement
    $languagePanel.Enabled = $enabled -and -not $usesEntitlement
    $numChunkCount.Enabled = $enabled -and -not $usesEntitlement -and $null -eq $script:sourceChunkCount -and
        $null -eq $script:sourceChunkError
    $chkForce.Enabled = $enabled
    $txtPasscode.Enabled = $enabled
    $btnBuild.Enabled = $enabled -and (Test-Path -LiteralPath $builderScript -PathType Leaf)
    $btnRecoverMetadata.Enabled = $enabled -and -not [string]::IsNullOrWhiteSpace($metadataRecoveryExecutable)
    $btnSelectPackage.Enabled = $enabled
    $btnQuickVerify.Enabled = $enabled -and (Test-Path -LiteralPath $publisherExecutable -PathType Leaf)
    $btnFullVerify.Enabled = $btnQuickVerify.Enabled
    $btnExtract.Enabled = $enabled -and (Test-Path -LiteralPath $publisherExecutable -PathType Leaf)
}

function Get-ValidatedPasscode {
    $passcode = $txtPasscode.Text
    if ($passcode.Length -ne 32) {
        Show-Error 'The passcode must contain exactly 32 characters.'
        return $null
    }
    return $passcode
}

function Update-ProcessLogLine($stream) {
    $text = $stream.Prefix + $stream.Text
    if ($stream.LogStart -lt 0) {
        $stream.LogStart = $log.TextLength
        $log.AppendText($text + [Environment]::NewLine)
    } else {
        $log.Select($stream.LogStart, $stream.LogLength)
        $log.SelectedText = $text
        $delta = $text.Length - $stream.LogLength
        foreach ($other in $script:processOutputStreams) {
            if ($other.LogStart -gt $stream.LogStart) { $other.LogStart += $delta }
        }
    }
    $stream.LogLength = $text.Length
    $log.SelectionStart = $log.TextLength
    $log.SelectionLength = 0
    $log.ScrollToCaret()
}

function Write-ProcessOutput($stream, [string]$text) {
    # A CR may be either a progress-line repaint or half of a CRLF split
    # across reads. Keep the current line until the next character decides.
    foreach ($part in [regex]::Matches($text, '[^\r\n]+|[\r\n]')) {
        $value = $part.Value
        if ($stream.PendingCR -and $value -ne "`n") { $stream.Text = '' }
        $stream.PendingCR = $false
        if ($value -eq "`r") {
            $stream.PendingCR = $true
        } elseif ($value -eq "`n") {
            if ($stream.LogStart -lt 0) { Update-ProcessLogLine $stream }
            $stream.Text = ''
            $stream.LogStart = -1
            $stream.LogLength = 0
        } else {
            $stream.Text += $value
            Update-ProcessLogLine $stream
        }
    }
}

function New-ProcessOutputStream($reader, [string]$prefix) {
    $buffer = New-Object char[] 4096
    return @{
        Reader = $reader; Buffer = $buffer; Prefix = $prefix
        Task = $reader.ReadAsync($buffer, 0, $buffer.Length); Ended = $false
        Text = ''; PendingCR = $false; LogStart = -1; LogLength = 0
    }
}

function Pump-ProcessOutput {
    # Do not use OutputDataReceived/ErrorDataReceived callbacks here.
    # Windows PowerShell 5.1 may invoke those callbacks on a .NET worker thread
    # without a PowerShell runspace, which can terminate the GUI process.
    foreach ($stream in $script:processOutputStreams) {
        $count = 0
        while (-not $stream.Ended -and $stream.Task.IsCompleted -and $count -lt 8) {
            if ($stream.Task.IsFaulted) { throw $stream.Task.Exception.GetBaseException() }
            $length = $stream.Task.Result
            if ($length -eq 0) { $stream.Ended = $true; $stream.Task = $null; break }
            Write-ProcessOutput $stream ([string]::new($stream.Buffer, 0, $length))
            $stream.Task = $stream.Reader.ReadAsync($stream.Buffer, 0, $stream.Buffer.Length)
            $count++
        }
    }
}

function Reset-ProcessState {
    $script:processOutputStreams = @()
}

function Start-Operation {
    param(
        [Parameter(Mandatory = $true)][string]$Executable,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$SuccessStatus,
        [Parameter(Mandatory = $true)][string]$WorkingDirectory,
        [string]$ResultFolder,
        [string[]]$LogHeader = @()
    )

    if ($null -ne $script:buildProcess) { return }
    if (-not (Test-Path -LiteralPath $Executable -PathType Leaf)) {
        Show-Error "Required executable was not found:`r`n$Executable"
        return
    }

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $Executable
    $psi.Arguments = (($Arguments | ForEach-Object { Quote-Argument ([string]$_) }) -join ' ')
    $psi.WorkingDirectory = $WorkingDirectory
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true

    $proc = New-Object System.Diagnostics.Process
    $proc.StartInfo = $psi
    $proc.EnableRaisingEvents = $false

    $log.Clear()
    foreach ($line in $LogHeader) { Append-LogLine $line }
    if ($LogHeader.Count -gt 0) { Append-LogLine '' }

    try {
        Set-BuildControlsEnabled $false
        $btnOpenOutput.Enabled = $false
        $script:lastResultFolder = $null
        $lblStatus.Text = ($Name + '...')
        $script:buildStartedAt = Get-Date
        $script:finalizedProcessId = $null
        $script:operationName = $Name
        $script:operationSuccessStatus = $SuccessStatus
        $script:operationResultFolder = $ResultFolder
        Reset-ProcessState

        $started = $proc.Start()
        if (-not $started) { throw "Failed to start $Executable" }
        $script:buildProcess = $proc
        $script:processOutputStreams = @(
            (New-ProcessOutputStream $proc.StandardOutput ''),
            (New-ProcessOutputStream $proc.StandardError '[stderr] ')
        )
    } catch {
        Set-BuildControlsEnabled $true
        $lblStatus.Text = 'Launch error'
        Append-LogLine ('GUI launch error: ' + $_.Exception.ToString())
        try { $proc.Dispose() } catch { }
        $script:buildProcess = $null
        Reset-ProcessState
        Show-Error $_.Exception.Message
    }
}

$pollTimer = New-Object System.Windows.Forms.Timer
$pollTimer.Interval = 100
$pollTimer.Add_Tick({
    if ($null -eq $script:buildProcess) { return }

    try {
        Pump-ProcessOutput

        if ($script:buildProcess.HasExited) {
            # The process may have exited while a final asynchronous ReadAsync
            # is still completing. Keep pumping until both redirected streams reach EOF.
            $script:buildProcess.WaitForExit()
            Pump-ProcessOutput

            if (@($script:processOutputStreams | Where-Object { -not $_.Ended }).Count -gt 0) {
                return
            }

            if ($script:finalizedProcessId -ne $script:buildProcess.Id) {
                $script:finalizedProcessId = $script:buildProcess.Id
                $exitCode = $script:buildProcess.ExitCode
                $elapsed = (Get-Date) - $script:buildStartedAt

                Append-LogLine ''
                if ($exitCode -eq 0) {
                    Append-LogLine ('=== {0} SUCCEEDED ({1:hh\:mm\:ss}) ===' -f $script:operationName.ToUpperInvariant(), $elapsed)
                    $lblStatus.Text = $script:operationSuccessStatus
                    if ($script:operationName -eq 'Metadata recovery') { Update-SourceChunkCount }
                    if (-not [string]::IsNullOrWhiteSpace($script:operationResultFolder) -and
                        (Test-Path -LiteralPath $script:operationResultFolder -PathType Container)) {
                        $script:lastResultFolder = $script:operationResultFolder
                        $btnOpenOutput.Enabled = $true
                    }
                } elseif ($exitCode -eq 130 -and $script:operationName -eq 'Metadata recovery') {
                    Append-LogLine 'Metadata recovery canceled; applied changes were rolled back.'
                    $lblStatus.Text = 'Metadata recovery canceled'
                } else {
                    Append-LogLine ('=== {0} FAILED: exit code {1} ({2:hh\:mm\:ss}) ===' -f $script:operationName.ToUpperInvariant(), $exitCode, $elapsed)
                    $lblStatus.Text = ('{0} failed (exit code {1})' -f $script:operationName, $exitCode)
                    $btnOpenOutput.Enabled = $false
                }

                Set-BuildControlsEnabled $true
                $script:buildProcess.Dispose()
                $script:buildProcess = $null
                if ($null -ne $script:metadataCancelFile) {
                    if (Test-Path -LiteralPath $script:metadataCancelFile) {
                        Remove-Item -LiteralPath $script:metadataCancelFile
                    }
                    $script:metadataCancelFile = $null
                }
                Reset-ProcessState
            }
        }
    } catch {
        Append-LogLine ('GUI process error: ' + $_.Exception.Message)
        $lblStatus.Text = 'GUI error'
        Set-BuildControlsEnabled $true
        if ($null -ne $script:buildProcess) {
            try { $script:buildProcess.Dispose() } catch { }
            $script:buildProcess = $null
        }
        Reset-ProcessState
    }
})
$pollTimer.Start()

$btnBuild.Add_Click({
    if ($null -ne $script:buildProcess) { return }

    if (-not (Test-Path -LiteralPath $builderScript -PathType Leaf)) {
        Show-Error "build-from-folder.ps1 was not found:`r`n$builderScript"
        return
    }

    $source = $txtSource.Text.Trim()
    $output = $txtOutput.Text.Trim()
    $reference = $txtReference.Text.Trim()
    $temp = $txtTemp.Text.Trim()
    $passcode = Get-ValidatedPasscode
    if ($null -eq $passcode) { return }

    if ([string]::IsNullOrWhiteSpace($source) -or -not (Test-Path -LiteralPath $source -PathType Container)) {
        Show-Error 'Select an existing source folder.'
        return
    }

    try {
        $volumeArguments = @(Get-BuildVolumeArguments)
        $sdkVersionArguments = @(Get-SdkVersionOverrideArguments)
    } catch {
        Show-Error $_.Exception.Message
        return
    }
    $volume = if ($cmbVolume.SelectedIndex -eq 1) { 'ac' } else { 'app' }
    if ($volume -eq 'app') {
        $keystone = Join-Path $source 'sce_sys\keystone'
        if (-not (Test-Path -LiteralPath $keystone -PathType Leaf)) {
            Show-Error "Required file was not found in the project:`r`nsce_sys\keystone"
            return
        }
        if ((Get-Item -LiteralPath $keystone).Length -ne 96) {
            Show-Error 'sce_sys\keystone must be exactly 96 bytes.'
            return
        }
    }

    if ([string]::IsNullOrWhiteSpace($output)) {
        Show-Error 'Select an output PKG file.'
        return
    }

    try {
        $source = [IO.Path]::GetFullPath($source)
        $output = [IO.Path]::GetFullPath($output)
        if (-not [string]::IsNullOrWhiteSpace($reference)) {
            $reference = [IO.Path]::GetFullPath($reference)
        }
    } catch {
        Show-Error 'A source, output, or reference path is invalid.'
        return
    }

    if ([IO.Path]::GetExtension($output) -ine '.pkg') {
        Show-Error 'The output file must have the .pkg extension.'
        return
    }
    if (-not [string]::IsNullOrWhiteSpace($reference)) {
        if (-not (Test-Path -LiteralPath $reference -PathType Leaf)) {
            Show-Error "The reference PKG was not found:`r`n$reference"
            return
        }
        if ([IO.Path]::GetExtension($reference) -ine '.pkg') {
            Show-Error 'The reference file must have the .pkg extension.'
            return
        }
        if ($reference -ieq $output) {
            Show-Error 'The reference and output PKG must be different files.'
            return
        }
    }

    if (-not [string]::IsNullOrWhiteSpace($temp)) {
        try {
            $temp = [Environment]::ExpandEnvironmentVariables($temp)
            $temp = [IO.Path]::GetFullPath($temp)
            if (Test-Path -LiteralPath $temp) {
                if (-not (Test-Path -LiteralPath $temp -PathType Container)) {
                    Show-Error 'The specified temporary path exists but is not a folder.'
                    return
                }
            } else {
                [void](New-Item -ItemType Directory -Force -Path $temp)
            }
        } catch {
            Show-Error ('Unable to use the temporary folder: ' + $_.Exception.Message)
            return
        }
        $txtTemp.Text = $temp
    }

    $txtSource.Text = $source
    $txtOutput.Text = $output
    $txtReference.Text = $reference
    Update-SourceChunkCount
    if ($null -ne $script:sourceChunkError) {
        Show-Error ('Unable to read source PlayGo metadata: ' + $script:sourceChunkError)
        return
    }
    $compression = [int]$numCompression.Value
    $chunkCount = [int]$numChunkCount.Value

    $powershellExe = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    if (-not (Test-Path -LiteralPath $powershellExe -PathType Leaf)) {
        $powershellExe = 'powershell.exe'
    }

    $arguments = @(
        '-NoLogo',
        '-NoProfile',
        '-NonInteractive',
        '-ExecutionPolicy', 'Bypass',
        '-File', $builderScript,
        '-SourceFolder', $source,
        '-OutputPackage', $output,
        '-Passcode', $passcode,
        '-CompressionLevel', $compression.ToString()
    )
    $arguments += $volumeArguments
    $arguments += $sdkVersionArguments
    if ($volume -eq 'app') { $arguments += @('-ChunkCount', $chunkCount.ToString()) }
    $arguments += @(Get-PlayGoLanguageArguments)
    if (-not [string]::IsNullOrWhiteSpace($temp)) {
        $arguments += @('-TemporaryDirectory', $temp)
    }
    if (-not [string]::IsNullOrWhiteSpace($reference)) {
        $arguments += @('-ReferencePackage', $reference)
    }
    if ($chkForce.Checked) { $arguments += '-Force' }

    $header = @(
        ('Source:      ' + $source),
        ('Volume:      ' + $volume.ToUpperInvariant()),
        ('Output:      ' + $output),
        ('Reference:   ' + $(if ($reference) { $reference } else { '<none; full package>' }))
    )
    if ([string]::IsNullOrWhiteSpace($temp)) {
        $header += 'Temp:        <not specified>'
    } else {
        $header += ('Temp:        ' + $temp)
    }
    $header += @(
        ('Compression: ' + $compression),
        ('SDK version: ' + $(if ($sdkVersionArguments.Count) { $sdkVersionArguments[1] } else { '<source / eboot>' })),
        ('Force:       ' + $chkForce.Checked)
    )
    if ($volume -eq 'app') {
        $header += ('Chunks:      ' + $chunkCount)
        $header += ('Languages:   ' + $txtLanguages.Text)
    }
    else { $header += 'Entitlement: supplied (16 bytes)' }

    Start-Operation `
        -Executable $powershellExe `
        -Arguments $arguments `
        -Name 'Build' `
        -SuccessStatus 'Build completed successfully' `
        -WorkingDirectory $toolkitRoot `
        -ResultFolder (Split-Path -Parent $output) `
        -LogHeader $header
})

function Start-PackageVerification([bool]$full) {
    if ($null -ne $script:buildProcess) { return }

    $package = $txtOutput.Text.Trim()
    if ([string]::IsNullOrWhiteSpace($package)) {
        Show-Error 'Select an existing PKG image.'
        return
    }
    try { $package = [IO.Path]::GetFullPath($package) } catch {
        Show-Error 'The PKG path is invalid.'
        return
    }
    if (-not (Test-Path -LiteralPath $package -PathType Leaf)) {
        Show-Error "The PKG image was not found:`r`n$package"
        return
    }
    if ([IO.Path]::GetExtension($package) -ine '.pkg') {
        Show-Error 'The selected image must have the .pkg extension.'
        return
    }
    $selectedPackage = $package
    $remasteredCompanion = $package + '.remastered.pkg'
    if (Test-Path -LiteralPath $remasteredCompanion -PathType Leaf) {
        # SDK img_verify cannot parse a compact patch with a passcode. Its
        # remastered companion contains the same resulting image and supports
        # the normal format/integrity verification path.
        $package = $remasteredCompanion
    }
    $passcode = Get-ValidatedPasscode
    if ($null -eq $passcode) { return }

    $txtOutput.Text = $package
    $integrityCheck = if ($full) { 'on' } else { 'off' }
    $checksDescription = if ($integrityCheck -eq 'on') { 'format + integrity' } else { 'format only' }
    $arguments = @(
        'img_verify', '--passcode', $passcode,
        '--format_check', 'on', '--integrity_check', $integrityCheck,
        '--no_progress_bar', $package
    )
    $verifyHeader = @('Package:     ' + $package, 'Checks:      ' + $checksDescription)
    if ($package -ine $selectedPackage) {
        $verifyHeader += ('Patch input: ' + $selectedPackage)
    }
    Start-Operation `
        -Executable $publisherExecutable `
        -Arguments $arguments `
        -Name $(if ($full) { 'Full verify' } else { 'Quick verify' }) `
        -SuccessStatus 'Verification completed successfully' `
        -WorkingDirectory $toolkitRoot `
        -LogHeader $verifyHeader
}

$btnQuickVerify.Add_Click({ Start-PackageVerification $false })
$btnFullVerify.Add_Click({ Start-PackageVerification $true })

$btnExtract.Add_Click({
    if ($null -ne $script:buildProcess) { return }

    $package = $txtOutput.Text.Trim()
    $destination = $txtExtract.Text.Trim()
    if ([string]::IsNullOrWhiteSpace($package)) {
        Show-Error 'Select an existing PKG image.'
        return
    }
    if ([string]::IsNullOrWhiteSpace($destination)) {
        Show-Error 'Select an extraction folder.'
        return
    }
    try {
        $package = [IO.Path]::GetFullPath($package)
        $destination = [IO.Path]::GetFullPath($destination)
    } catch {
        Show-Error 'The PKG path or extraction folder is invalid.'
        return
    }
    if (-not (Test-Path -LiteralPath $package -PathType Leaf)) {
        Show-Error "The PKG image was not found:`r`n$package"
        return
    }
    if ([IO.Path]::GetExtension($package) -ine '.pkg') {
        Show-Error 'The selected image must have the .pkg extension.'
        return
    }
    $selectedPackage = $package
    $remasteredCompanion = $package + '.remastered.pkg'
    if (Test-Path -LiteralPath $remasteredCompanion -PathType Leaf) {
        # Compact patches require their reference image to be materialized.
        # img_create already emitted that form as the remastered companion.
        $package = $remasteredCompanion
    }
    $passcode = Get-ValidatedPasscode
    if ($null -eq $passcode) { return }

    try {
        if (Test-Path -LiteralPath $destination) {
            if (-not (Test-Path -LiteralPath $destination -PathType Container)) {
                Show-Error 'The extraction path exists but is not a folder.'
                return
            }
            if (Get-ChildItem -LiteralPath $destination -Force | Select-Object -First 1) {
                Show-Error 'The extraction folder must be empty.'
                return
            }
        } else {
            [void](New-Item -ItemType Directory -Path $destination -Force)
        }
    } catch {
        Show-Error ('Unable to prepare the extraction folder: ' + $_.Exception.Message)
        return
    }

    $txtOutput.Text = $package
    $txtExtract.Text = $destination
    $arguments = @(
        'img_extract', '--passcode', $passcode,
        '--no_progress_bar', $package, $destination
    )
    $extractHeader = @('Package:     ' + $package, 'Destination: ' + $destination)
    if ($package -ine $selectedPackage) {
        $extractHeader += ('Patch input: ' + $selectedPackage)
    }
    Start-Operation `
        -Executable $publisherExecutable `
        -Arguments $arguments `
        -Name 'Extract' `
        -SuccessStatus 'Extraction completed successfully' `
        -WorkingDirectory $toolkitRoot `
        -ResultFolder $destination `
        -LogHeader $extractHeader
})

$btnRecoverMetadata.Add_Click({
    if ($null -ne $script:buildProcess) { return }
    $source = $txtSource.Text.Trim()
    if ([string]::IsNullOrWhiteSpace($source) -or -not (Test-Path -LiteralPath $source -PathType Container)) {
        Show-Error 'Select the existing game source folder.'
        return
    }
    if ([string]::IsNullOrWhiteSpace($metadataRecoveryExecutable)) {
        Show-Error 'metadata-recovery.exe is missing. Build ProsperoMetadataRecoveryTool or refresh the toolkit.'
        return
    }
    $dialog = New-Object Windows.Forms.Form
    $dialog.Text = 'Restore metadata online'
    $dialog.StartPosition = 'CenterParent'
    $dialog.ClientSize = New-Object Drawing.Size(740, 240)
    $dialog.FormBorderStyle = 'FixedDialog'
    $dialog.MinimizeBox = $false
    $dialog.MaximizeBox = $false
    $description = New-Object Windows.Forms.Label
    $description.Text = 'Downloads only *_sc.pkg matching the dump version. Originals are backed up beside the game folder. Protected records are skipped and listed in the log.'
    $description.SetBounds(12, 12, 710, 44)
    $urlLabel = New-Object Windows.Forms.Label
    $urlLabel.Text = 'URL (optional):'
    $urlLabel.SetBounds(12, 67, 165, 22)
    $url = New-Object Windows.Forms.TextBox
    $url.SetBounds(180, 64, 545, 24)
    $caLabel = New-Object Windows.Forms.Label
    $caLabel.Text = 'Root CA (optional):'
    $caLabel.SetBounds(12, 102, 165, 22)
    $ca = New-Object Windows.Forms.TextBox
    $ca.SetBounds(180, 99, 495, 24)
    $browseCa = New-Object Windows.Forms.Button
    $browseCa.Text = '...'
    $browseCa.SetBounds(682, 98, 43, 26)
    $browseCa.Add_Click({
        $picker = New-Object Windows.Forms.OpenFileDialog
        $picker.Filter = 'CA certificates (*.cer;*.crt;*.pem)|*.cer;*.crt;*.pem|All files (*.*)|*.*'
        try { if ($picker.ShowDialog($dialog) -eq 'OK') { $ca.Text = $picker.FileName } }
        finally { $picker.Dispose() }
    })
    $preserve = New-Object Windows.Forms.CheckBox
    $preserve.Text = 'Keep local SDK, firmware and DRM settings'
    $preserve.Checked = $true
    $preserve.SetBounds(12, 141, 700, 25)
    $restore = New-Object Windows.Forms.Button
    $restore.Text = 'Download and restore'
    $restore.DialogResult = 'OK'
    $restore.SetBounds(450, 192, 175, 30)
    $cancel = New-Object Windows.Forms.Button
    $cancel.Text = 'Cancel'
    $cancel.DialogResult = 'Cancel'
    $cancel.SetBounds(635, 192, 90, 30)
    $dialog.Controls.AddRange(@($description, $urlLabel, $url, $caLabel, $ca, $browseCa, $preserve, $restore, $cancel))
    $dialog.AcceptButton = $restore
    $dialog.CancelButton = $cancel
    $tip = New-Object Windows.Forms.ToolTip
    $tip.SetToolTip($url, 'Blank uses versionFileUri from param.json. Accepts a version XML, manifest JSON or matching *_sc.pkg URL.')
    try {
        if ($dialog.ShowDialog($form) -ne 'OK') { return }
        $source = [IO.Path]::GetFullPath($source)
        $script:metadataCancelFile = Join-Path ([IO.Path]::GetTempPath()) ('prospero-metadata-cancel-' + [Guid]::NewGuid().ToString('N') + '.flag')
        $arguments = @($source, '--cancel-file', $script:metadataCancelFile)
        if (-not [string]::IsNullOrWhiteSpace($url.Text)) { $arguments += @('--url', $url.Text.Trim()) }
        if (-not [string]::IsNullOrWhiteSpace($ca.Text)) { $arguments += @('--ca-cert', $ca.Text.Trim()) }
        if (-not $preserve.Checked) { $arguments += '--original-runtime' }
        $metadataSourceUrl = $url.Text.Trim()
        if ([string]::IsNullOrWhiteSpace($metadataSourceUrl)) {
            $metadataParam = Get-Content -LiteralPath (Join-Path $source 'sce_sys\param.json') -Raw -Encoding UTF8 | ConvertFrom-Json
            $metadataSourceUrl = ([string]$metadataParam.versionFileUri).Trim()
        }
        $metadataHeader = @(
            ('Source: ' + $source)
            ('Source URL: ' + $metadataSourceUrl)
        )
        Start-Operation -Executable $metadataRecoveryExecutable -Arguments $arguments `
            -Name 'Metadata recovery' -SuccessStatus 'Metadata recovery completed; see skipped records in the log' `
            -WorkingDirectory (Split-Path -Parent $metadataRecoveryExecutable) -ResultFolder $source `
            -LogHeader $metadataHeader
    } catch { Show-Error $_.Exception.Message }
    finally { $tip.Dispose(); $dialog.Dispose() }
})

$form.Add_FormClosing({
    if ($null -ne $script:buildProcess -and $script:operationName -eq 'Metadata recovery') {
        # Let the helper roll back an in-progress transaction before the GUI can close.
        if ($null -ne $script:metadataCancelFile) {
            [IO.File]::WriteAllText($script:metadataCancelFile, 'cancel')
        }
        $lblStatus.Text = 'Canceling metadata recovery; waiting for rollback...'
        $_.Cancel = $true
        return
    }
    if ($null -ne $script:buildProcess) {
        $answer = [System.Windows.Forms.MessageBox]::Show(
            $form,
            'An operation is still running. Close the GUI and terminate the process?',
            'PKG Build GUI',
            [System.Windows.Forms.MessageBoxButtons]::YesNo,
            [System.Windows.Forms.MessageBoxIcon]::Warning
        )
        if ($answer -ne [System.Windows.Forms.DialogResult]::Yes) {
            $_.Cancel = $true
            return
        }
        try {
            & taskkill.exe /PID $script:buildProcess.Id /T /F | Out-Null
        } catch { }
    }
})

Update-SourceChunkCount
Set-BuildControlsEnabled $true
if (-not (Test-Path -LiteralPath $builderScript -PathType Leaf) -and
    -not (Test-Path -LiteralPath $publisherExecutable -PathType Leaf)) {
    $lblStatus.Text = 'Build toolkit not found. Run prepare-plaintext-build-tools.py first.'
} elseif (-not (Test-Path -LiteralPath $builderScript -PathType Leaf)) {
    $lblStatus.Text = 'Builder not found; verify and extract are available.'
} elseif (-not (Test-Path -LiteralPath $publisherExecutable -PathType Leaf)) {
    $lblStatus.Text = 'Publisher not found; only build is available.'
}

if ($ValidateOnly) {
    Write-Output ('Builder:   ' + $builderScript)
    Write-Output ('Publisher: ' + $publisherExecutable)
    Write-Output ('Build available:   ' + (Test-Path -LiteralPath $builderScript -PathType Leaf))
    Write-Output ('Verify available:  ' + (Test-Path -LiteralPath $publisherExecutable -PathType Leaf))
    Write-Output ('Extract available: ' + (Test-Path -LiteralPath $publisherExecutable -PathType Leaf))
} else {
    [void]$form.ShowDialog()
}
