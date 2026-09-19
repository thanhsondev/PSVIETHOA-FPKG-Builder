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
$script:buildProcess = $null
$script:buildStartedAt = $null
$script:finalizedProcessId = $null
$script:operationName = 'Build'
$script:operationSuccessStatus = 'Build completed successfully'
$script:operationResultFolder = $null
$script:lastResultFolder = $null
$script:stdoutReadTask = $null
$script:stderrReadTask = $null
$script:stdoutEnded = $true
$script:stderrEnded = $true

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
$form.MinimumSize = New-Object System.Drawing.Size(800, 560)
$form.AutoScaleMode = [System.Windows.Forms.AutoScaleMode]::Dpi

$root = New-Object System.Windows.Forms.TableLayoutPanel
$root.Dock = 'Fill'
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
$hint.Text = 'Select a reference PKG to build a patch; the remastered companion is created automatically.'
$hint.AutoSize = $true
$hint.Margin = New-Object System.Windows.Forms.Padding(205, 0, 0, 8)
[void]$root.Controls.Add($hint, 0, 1)

$options = New-Object System.Windows.Forms.FlowLayoutPanel
$options.Dock = 'Top'
$options.AutoSize = $true
$options.FlowDirection = 'LeftToRight'
$options.WrapContents = $true
$options.Margin = New-Object System.Windows.Forms.Padding(0, 4, 0, 8)

$lblCompression = New-Object System.Windows.Forms.Label
$lblCompression.Text = 'Compression (-4..9):'
$lblCompression.AutoSize = $true
$lblCompression.Margin = New-Object System.Windows.Forms.Padding(0, 7, 8, 0)
[void]$options.Controls.Add($lblCompression)

$numCompression = New-Object System.Windows.Forms.NumericUpDown
$numCompression.Minimum = -4
$numCompression.Maximum = 9
$numCompression.Value = 7
$numCompression.Width = 60
$numCompression.Margin = New-Object System.Windows.Forms.Padding(0, 3, 24, 0)
[void]$options.Controls.Add($numCompression)

$chkForce = New-Object System.Windows.Forms.CheckBox
$chkForce.Text = 'Overwrite existing files (-Force)'
$chkForce.AutoSize = $true
$chkForce.Margin = New-Object System.Windows.Forms.Padding(0, 6, 0, 0)
[void]$options.Controls.Add($chkForce)

$lblPasscode = New-Object System.Windows.Forms.Label
$lblPasscode.Text = 'Passcode:'
$lblPasscode.AutoSize = $true
$lblPasscode.Margin = New-Object System.Windows.Forms.Padding(24, 7, 8, 0)
[void]$options.Controls.Add($lblPasscode)

$txtPasscode = New-Object System.Windows.Forms.TextBox
$txtPasscode.Text = '00000000000000000000000000000000'
$txtPasscode.Width = 250
$txtPasscode.MaxLength = 32
$txtPasscode.Font = New-Object System.Drawing.Font('Consolas', 9)
$txtPasscode.Margin = New-Object System.Windows.Forms.Padding(0, 3, 0, 0)
[void]$options.Controls.Add($txtPasscode)

$lblVerificationMode = New-Object System.Windows.Forms.Label
$lblVerificationMode.Text = 'Verification:'
$lblVerificationMode.AutoSize = $true
$lblVerificationMode.Margin = New-Object System.Windows.Forms.Padding(24, 7, 8, 0)
[void]$options.Controls.Add($lblVerificationMode)

$cmbVerificationMode = New-Object System.Windows.Forms.ComboBox
$cmbVerificationMode.DropDownStyle = [System.Windows.Forms.ComboBoxStyle]::DropDownList
$cmbVerificationMode.Width = 190
$cmbVerificationMode.Margin = New-Object System.Windows.Forms.Padding(0, 3, 0, 0)
[void]$cmbVerificationMode.Items.Add('Full (format + integrity)')
[void]$cmbVerificationMode.Items.Add('Format only')
$cmbVerificationMode.SelectedIndex = 0
[void]$options.Controls.Add($cmbVerificationMode)
[void]$root.Controls.Add($options, 0, 2)

$buttons = New-Object System.Windows.Forms.FlowLayoutPanel
$buttons.Dock = 'Top'
$buttons.AutoSize = $true
$buttons.FlowDirection = 'LeftToRight'
$buttons.WrapContents = $true
$buttons.Margin = New-Object System.Windows.Forms.Padding(0, 0, 0, 8)

$btnBuild = New-Object System.Windows.Forms.Button
$btnBuild.Text = 'Build'
$btnBuild.AutoSize = $true
$btnBuild.MinimumSize = New-Object System.Drawing.Size(110, 32)
[void]$buttons.Controls.Add($btnBuild)

$btnSelectPackage = New-Object System.Windows.Forms.Button
$btnSelectPackage.Text = 'Select existing PKG...'
$btnSelectPackage.AutoSize = $true
$btnSelectPackage.MinimumSize = New-Object System.Drawing.Size(155, 32)
[void]$buttons.Controls.Add($btnSelectPackage)

$btnVerify = New-Object System.Windows.Forms.Button
$btnVerify.Text = 'Verify PKG'
$btnVerify.AutoSize = $true
$btnVerify.MinimumSize = New-Object System.Drawing.Size(110, 32)
[void]$buttons.Controls.Add($btnVerify)

$btnExtract = New-Object System.Windows.Forms.Button
$btnExtract.Text = 'Extract PKG'
$btnExtract.AutoSize = $true
$btnExtract.MinimumSize = New-Object System.Drawing.Size(110, 32)
[void]$buttons.Controls.Add($btnExtract)

$btnOpenOutput = New-Object System.Windows.Forms.Button
$btnOpenOutput.Text = 'Open result folder'
$btnOpenOutput.AutoSize = $true
$btnOpenOutput.Enabled = $false
$btnOpenOutput.MinimumSize = New-Object System.Drawing.Size(180, 32)
[void]$buttons.Controls.Add($btnOpenOutput)

$btnClearLog = New-Object System.Windows.Forms.Button
$btnClearLog.Text = 'Clear log'
$btnClearLog.AutoSize = $true
$btnClearLog.MinimumSize = New-Object System.Drawing.Size(110, 32)
[void]$buttons.Controls.Add($btnClearLog)
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
$btnClearLog.Add_Click({ $log.Clear() })

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
    $chkForce.Enabled = $enabled
    $txtPasscode.Enabled = $enabled
    $cmbVerificationMode.Enabled = $enabled
    $btnBuild.Enabled = $enabled -and (Test-Path -LiteralPath $builderScript -PathType Leaf)
    $btnSelectPackage.Enabled = $enabled
    $btnVerify.Enabled = $enabled -and (Test-Path -LiteralPath $publisherExecutable -PathType Leaf)
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

function Pump-ProcessOutput {
    # Do not use OutputDataReceived/ErrorDataReceived callbacks here.
    # Windows PowerShell 5.1 may invoke those callbacks on a .NET worker thread
    # without a PowerShell runspace, which can terminate the GUI process.
    $maxLinesPerTick = 250

    $count = 0
    while (-not $script:stdoutEnded -and $null -ne $script:stdoutReadTask -and $script:stdoutReadTask.IsCompleted -and $count -lt $maxLinesPerTick) {
        if ($script:stdoutReadTask.IsFaulted) {
            throw $script:stdoutReadTask.Exception.GetBaseException()
        }
        $line = $script:stdoutReadTask.Result
        if ($null -eq $line) {
            $script:stdoutEnded = $true
            $script:stdoutReadTask = $null
            break
        }
        Append-LogLine $line
        $script:stdoutReadTask = $script:buildProcess.StandardOutput.ReadLineAsync()
        $count++
    }

    $count = 0
    while (-not $script:stderrEnded -and $null -ne $script:stderrReadTask -and $script:stderrReadTask.IsCompleted -and $count -lt $maxLinesPerTick) {
        if ($script:stderrReadTask.IsFaulted) {
            throw $script:stderrReadTask.Exception.GetBaseException()
        }
        $line = $script:stderrReadTask.Result
        if ($null -eq $line) {
            $script:stderrEnded = $true
            $script:stderrReadTask = $null
            break
        }
        Append-LogLine ('[stderr] ' + $line)
        $script:stderrReadTask = $script:buildProcess.StandardError.ReadLineAsync()
        $count++
    }
}

function Reset-ProcessState {
    $script:stdoutReadTask = $null
    $script:stderrReadTask = $null
    $script:stdoutEnded = $true
    $script:stderrEnded = $true
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
        $script:stdoutEnded = $false
        $script:stderrEnded = $false
        $script:stdoutReadTask = $proc.StandardOutput.ReadLineAsync()
        $script:stderrReadTask = $proc.StandardError.ReadLineAsync()
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
            # The process may have exited while a final asynchronous ReadLineAsync
            # is still completing. Keep pumping until both redirected streams reach EOF.
            $script:buildProcess.WaitForExit()
            Pump-ProcessOutput

            if (-not $script:stdoutEnded -or -not $script:stderrEnded) {
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
                    if (-not [string]::IsNullOrWhiteSpace($script:operationResultFolder) -and
                        (Test-Path -LiteralPath $script:operationResultFolder -PathType Container)) {
                        $script:lastResultFolder = $script:operationResultFolder
                        $btnOpenOutput.Enabled = $true
                    }
                } else {
                    Append-LogLine ('=== {0} FAILED: exit code {1} ({2:hh\:mm\:ss}) ===' -f $script:operationName.ToUpperInvariant(), $exitCode, $elapsed)
                    $lblStatus.Text = ('{0} failed (exit code {1})' -f $script:operationName, $exitCode)
                    $btnOpenOutput.Enabled = $false
                }

                Set-BuildControlsEnabled $true
                $script:buildProcess.Dispose()
                $script:buildProcess = $null
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

    $keystone = Join-Path $source 'sce_sys\keystone'
    if (-not (Test-Path -LiteralPath $keystone -PathType Leaf)) {
        Show-Error "Required file was not found in the project:`r`nsce_sys\keystone"
        return
    }
    if ((Get-Item -LiteralPath $keystone).Length -ne 96) {
        Show-Error 'sce_sys\keystone must be exactly 96 bytes.'
        return
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
    $compression = [int]$numCompression.Value

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
    if (-not [string]::IsNullOrWhiteSpace($temp)) {
        $arguments += @('-TemporaryDirectory', $temp)
    }
    if (-not [string]::IsNullOrWhiteSpace($reference)) {
        $arguments += @('-ReferencePackage', $reference)
    }
    if ($chkForce.Checked) { $arguments += '-Force' }

    $header = @(
        ('Source:      ' + $source),
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
        ('Force:       ' + $chkForce.Checked)
    )

    Start-Operation `
        -Executable $powershellExe `
        -Arguments $arguments `
        -Name 'Build' `
        -SuccessStatus 'Build completed successfully' `
        -WorkingDirectory $toolkitRoot `
        -ResultFolder (Split-Path -Parent $output) `
        -LogHeader $header
})

$btnVerify.Add_Click({
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
    $integrityCheck = if ($cmbVerificationMode.SelectedIndex -eq 1) { 'off' } else { 'on' }
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
        -Name 'Verify' `
        -SuccessStatus 'Verification completed successfully' `
        -WorkingDirectory $toolkitRoot `
        -LogHeader $verifyHeader
})

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

$form.Add_FormClosing({
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
