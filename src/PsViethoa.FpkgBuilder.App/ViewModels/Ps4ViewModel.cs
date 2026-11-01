using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PsViethoa.FpkgBuilder.App.Services;
using PsViethoa.FpkgBuilder.Core.Localization;
using PsViethoa.FpkgBuilder.Core.Models;
using PsViethoa.FpkgBuilder.Core.Ps4;
using PsViethoa.FpkgBuilder.Core.Services;

namespace PsViethoa.FpkgBuilder.App.ViewModels;

/// <summary>Năm chức năng PS4 PKG, mỗi chức năng một mục trên thanh bên.</summary>
public enum Ps4Mode
{
    Extract,
    Base,
    Patch,
    Dlc,
    Merge,
}

/// <summary>Một trường param.sfo trong trình sửa SFO (Đóng Update / Gộp). Sửa vào bản sao dùng khi đóng; tệp nguồn không đổi.</summary>
public sealed partial class Ps4SfoRow : ObservableObject
{
    public Ps4SfoRow(string key, ushort format, int maxLength, string original, bool isNew, string? lockNote, string note)
    {
        Key = key;
        Format = format;
        MaxLength = maxLength;
        Original = original;
        IsNew = isNew;
        Locked = lockNote != null;
        Note = lockNote ?? note;
        _value = original;
    }

    public string Key { get; }

    public ushort Format { get; }

    public int MaxLength { get; }

    public string Original { get; }

    public bool IsNew { get; }

    public bool Locked { get; }

    public string Note { get; }

    public Action? OnChanged { get; set; }

    [ObservableProperty] private string _value;

    public string? Error { get; private set; }

    public bool HasError => Error != null;

    public bool IsModified => IsNew || Value != Original;

    public string TypeLabel => Loc.T(Format switch { Ps4SfoDocument.Utf8 => "Ps4.Sfo.TypeString", Ps4SfoDocument.Integer => "Ps4.Sfo.TypeNumber", _ => "Ps4.Sfo.TypeBinary" });

    public string MaxText
    {
        get
        {
            if (Format != Ps4SfoDocument.Utf8)
            {
                return MaxLength.ToString(CultureInfo.InvariantCulture);
            }

            var needed = Encoding.UTF8.GetByteCount(Value) + 1;
            return needed > MaxLength ? $"{MaxLength}→{Ps4SfoDocument.Align(needed)}" : MaxLength.ToString(CultureInfo.InvariantCulture);
        }
    }

    public string? Tip => Error ?? (Locked ? Note : IsModified ? Loc.F("Ps4.SfoEdit.Original", Original) : null);

    partial void OnValueChanged(string value)
    {
        Error = Ps4SfoDocument.Validate(Format, value, Key);
        OnPropertyChanged(nameof(Error));
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(IsModified));
        OnPropertyChanged(nameof(MaxText));
        OnPropertyChanged(nameof(Tip));
        OnChanged?.Invoke();
    }

    public void RefreshTexts()
    {
        OnPropertyChanged(nameof(TypeLabel));
        OnPropertyChanged(nameof(Tip));
    }
}

/// <summary>
/// Trang PS4 PKG: xả FPKG (toàn bộ hoặc từng tệp), đóng base, đóng update (kèm trình sửa param.sfo), đóng lại DLC đã xả và gộp base +
/// update thành một PKG base. Một màn hình dùng chung cho cả năm chức năng; ô và nhãn đổi theo <see cref="Mode"/>. Cơ chế theo
/// PSVIETHOA STUDIO 1.7.0 (Ngô Phi Phương): <see cref="Ps4JobRunner"/> + LibOrbisPkg + orbis-pub-cmd.
/// </summary>
public sealed partial class Ps4ViewModel : ObservableObject
{
    public const string DefaultPasscode = Ps4Defaults.Passcode;

    private static readonly Dictionary<string, string> SfoNoteKeys = new(StringComparer.Ordinal)
    {
        ["TITLE"] = "Ps4.SfoNote.Title",
        ["TITLE_ID"] = "Ps4.SfoNote.TitleId",
        ["ATTRIBUTE"] = "Ps4.SfoNote.Attribute",
        ["ATTRIBUTE2"] = "Ps4.SfoNote.Attribute2",
        ["SYSTEM_VER"] = "Ps4.SfoNote.SystemVer",
        ["PUBTOOLINFO"] = "Ps4.SfoNote.PubToolInfo",
        ["PARENTAL_LEVEL"] = "Ps4.SfoNote.Parental",
        ["APP_TYPE"] = "Ps4.SfoNote.AppType",
        ["DOWNLOAD_DATA_SIZE"] = "Ps4.SfoNote.DownloadData",
        ["REMOTE_PLAY_KEY_ASSIGN"] = "Ps4.SfoNote.RemotePlay",
        ["FORMAT"] = "Ps4.SfoNote.Format",
    };

    private readonly AppSettings _settings;
    private readonly DialogService _dialogs;
    private readonly Dictionary<Ps4Mode, string> _sources = new();
    private readonly System.Collections.Concurrent.ConcurrentQueue<LogEntry> _pendingLog = new();
    private readonly DispatcherTimer _tick;
    private readonly DispatcherTimer _inspectDelay;
    private readonly Stopwatch _elapsed = new();
    private readonly HashSet<string> _sfoRemoved = new(StringComparer.Ordinal);
    private CancellationTokenSource? _cancellation;
    private Ps4Progress? _pendingProgress;
    private string[]? _extractSelection;
    private string _selectionSource = string.Empty;
    private int _inspection;
    private bool _versionEdited;
    private bool _settingVersion;
    private bool _sfoSyncing;
    private byte[]? _sfoBaseBytes;
    private string _sfoLoadedKey = string.Empty;
    private bool _sfoFromFile;
    private string _sfoOriginRaw = string.Empty;
    private int _logCount;

    public Ps4ViewModel(AppSettings settings, DialogService dialogs)
    {
        _settings = settings;
        _dialogs = dialogs;
        _outputFolder = settings.Ps4OutputFolder ?? string.Empty;
        _fullVerify = settings.Ps4FullVerify;
        _statusText = Loc.T("Ps4.Status.Ready");
        _phaseText = Loc.T("Ps4.Status.Ready");
        _tick = new DispatcherTimer(TimeSpan.FromMilliseconds(120), DispatcherPriority.Background, (_, _) => Tick());
        _inspectDelay = new DispatcherTimer(TimeSpan.FromMilliseconds(450), DispatcherPriority.Background, async (sender, _) =>
        {
            (sender as DispatcherTimer)?.Stop();
            await InspectSourceAsync();
        });
        SfoRows.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasSfo));
        Loc.Current.LanguageChanged += (_, _) => OnLanguageChanged();
        _selectionSummary = Loc.T("Ps4.Selection.All");
        ResetInfo();
    }

    [ObservableProperty] private Ps4Mode _mode;

    // ── Ô nhập ──────────────────────────────────────────────────────────────────────────────────────────────────────
    [ObservableProperty] private string _sourcePath = string.Empty;
    [ObservableProperty] private string _basePath = string.Empty;
    [ObservableProperty] private string _basePasscode = string.Empty;
    [ObservableProperty] private string _outputFolder;
    [ObservableProperty] private string _passcode = DefaultPasscode;
    [ObservableProperty] private string _version = string.Empty;
    [ObservableProperty] private string _sourceVersion = string.Empty;
    [ObservableProperty] private string _versionHint = string.Empty;
    [ObservableProperty] private bool _fullVerify;
    [ObservableProperty] private bool _gp4Only;
    [ObservableProperty] private string _entitlementKey = string.Empty;
    [ObservableProperty] private string _enginePath = string.Empty;
    [ObservableProperty] private bool _showAdvanced;
    [ObservableProperty] private string _selectionSummary = string.Empty;
    [ObservableProperty] private bool _showSfoEditor;

    // ── Thông tin nguồn ─────────────────────────────────────────────────────────────────────────────────────────────
    [ObservableProperty] private string _infoTitle = string.Empty;
    [ObservableProperty] private string _infoSubtitle = string.Empty;
    [ObservableProperty] private string _infoContentId = "—";
    [ObservableProperty] private string _infoTitleId = "—";
    [ObservableProperty] private string _infoVersion = "—";
    [ObservableProperty] private string _infoBadge = string.Empty;
    [ObservableProperty] private bool _infoOk;
    [ObservableProperty] private bool _infoWarn;
    [ObservableProperty] private Bitmap? _infoIcon;
    [ObservableProperty] private bool _isInspecting;

    // ── param.sfo ───────────────────────────────────────────────────────────────────────────────────────────────────
    [ObservableProperty] private string _sfoOrigin = string.Empty;
    [ObservableProperty] private string _sfoQuickTitle = string.Empty;
    [ObservableProperty] private bool _sfoTitleAllLanguages;
    [ObservableProperty] private string _sfoNewKey = string.Empty;
    [ObservableProperty] private string _sfoNewValue = string.Empty;
    [ObservableProperty] private bool _sfoNewIsNumber;
    [ObservableProperty] private string _sfoStatus = string.Empty;
    [ObservableProperty] private bool _sfoStatusError;
    [ObservableProperty] private bool _sfoStatusWarn;
    [ObservableProperty] private string _sfoDirtyText = string.Empty;
    [ObservableProperty] private Ps4SfoRow? _selectedSfoRow;

    // ── Tiến độ ─────────────────────────────────────────────────────────────────────────────────────────────────────
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private Ps4Mode? _busyMode;
    [ObservableProperty] private string _statusText;
    [ObservableProperty] private string _phaseText;
    [ObservableProperty] private string _detailText = string.Empty;
    [ObservableProperty] private double _percent;
    [ObservableProperty] private string _speedText = "—";
    [ObservableProperty] private string _etaText = "—";
    [ObservableProperty] private string _elapsedText = "00:00";
    [ObservableProperty] private bool _estimated;
    [ObservableProperty] private bool _succeeded;
    [ObservableProperty] private bool _failed;
    [ObservableProperty] private string _lastFolder = string.Empty;
    [ObservableProperty] private string _lastLog = string.Empty;
    [ObservableProperty] private string _lastPackage = string.Empty;
    [ObservableProperty] private string _logFilter = string.Empty;

    public LogCollection Log { get; } = new();

    public ObservableCollection<LogEntry> VisibleLog { get; } = new();

    public ObservableCollection<Ps4SfoRow> SfoRows { get; } = new();

    public Ps4JobKind Kind => Mode switch
    {
        Ps4Mode.Base => Ps4JobKind.Base,
        Ps4Mode.Patch => Ps4JobKind.Patch,
        Ps4Mode.Dlc => Ps4JobKind.Dlc,
        Ps4Mode.Merge => Ps4JobKind.Merge,
        _ => Ps4JobKind.Extract,
    };

    public bool HasIcon => InfoIcon != null;

    public bool HasSfo => SfoRows.Count > 0;

    public bool IsSfoDirty => _sfoBaseBytes != null && (_sfoFromFile || _sfoRemoved.Count > 0 || SfoRows.Any(r => r.IsModified));

    public bool IsExtract => Mode == Ps4Mode.Extract;

    public bool ShowBase => Mode is Ps4Mode.Patch or Ps4Mode.Merge;

    public bool ShowBasePasscode => Mode == Ps4Mode.Patch;

    public bool ShowBaseFolderButton => Mode is Ps4Mode.Patch or Ps4Mode.Merge;

    public bool ShowVersion => Mode != Ps4Mode.Extract;

    public bool ShowRandomPass => Mode != Ps4Mode.Extract;

    public bool ShowEntitlement => Mode == Ps4Mode.Dlc;

    public bool ShowGp4Only => Mode != Ps4Mode.Extract;

    public bool ShowSfoTabs => Mode is Ps4Mode.Patch or Ps4Mode.Merge;

    public bool ShowSettingsForm => !ShowSfoTabs || !ShowSfoEditor;

    public bool CanToggleVerify => Mode != Ps4Mode.Dlc;

    /// <summary>Nguồn chọn bằng hộp chọn tệp (.pkg): Xả và Gộp (update có thể là PKG hoặc thư mục).</summary>
    public bool SourcePicksFile => Mode is Ps4Mode.Extract or Ps4Mode.Merge;

    public bool ShowSourceFolderButton => !SourcePicksFile || Mode == Ps4Mode.Merge;

    public string SourceLabel => Loc.T("Ps4.Source." + Mode);

    public string SourceWatermark => Loc.T("Ps4.Source." + Mode + ".Watermark");

    public string BaseLabel => Loc.T("Ps4.Base." + Mode);

    public string PasscodeLabel => Loc.T("Ps4.Passcode." + Mode);

    public string VersionLabel => Loc.T(Mode == Ps4Mode.Merge ? "Ps4.Version.Merge" : "Ps4.Version.Build");

    public string FullVerifyLabel => Loc.T(Mode switch { Ps4Mode.Extract => "Ps4.Verify.Extract", Ps4Mode.Dlc => "Ps4.Verify.Dlc", _ => "Ps4.Verify.Build" });

    public string FullVerifyTip => Loc.T(Mode == Ps4Mode.Extract ? "Ps4.Verify.Extract.Tip" : "Ps4.Verify.Build.Tip");

    public string Gp4OnlyLabel => Loc.T(Mode == Ps4Mode.Merge ? "Ps4.Gp4Only.Merge" : "Ps4.Gp4Only");

    public string OutputHint => Loc.T(Mode == Ps4Mode.Extract ? "Ps4.Output.HintExtract" : "Ps4.Output.HintBuild");

    public string StartText => Loc.T(Gp4Only && Mode != Ps4Mode.Extract ? "Ps4.Start.Gp4" : "Ps4.Start." + Mode);

    public string StepSourceTitle => Loc.T("Ps4.Step.Source." + Mode);

    public string ModeHint => Loc.T("Ps4.Hint." + Mode);

    public string SourceVersionText => SourceVersion.Length > 0 ? Loc.F("Ps4.Version.Current", SourceVersion) : Loc.T("Ps4.Version.Unknown");

    public bool PasscodeValid => Regex.IsMatch(Passcode, "^[0-9A-Za-z_-]{32}$");

    public bool CanStart => !IsBusy && SourcePath.Trim().Length > 0 && OutputFolder.Trim().Length > 0;

    public string LogCountText => Loc.F(_logCount == 1 ? "Ps4.LogCountOne" : "Ps4.LogCount", _logCount);

    public string BusyText => BusyMode is { } busy ? Loc.F("Ps4.BusyBanner", Loc.T("Nav.Ps4" + busy)) : string.Empty;

    public bool ShowBusyBanner => IsBusy && BusyMode != Mode;

    partial void OnModeChanged(Ps4Mode oldValue, Ps4Mode newValue)
    {
        _sources[oldValue] = SourcePath;
        _inspection++;
        if (newValue == Ps4Mode.Dlc)
        {
            FullVerify = true;
        }
        else if (oldValue == Ps4Mode.Dlc)
        {
            FullVerify = _settings.Ps4FullVerify;
        }

        ShowSfoEditor = false;
        SourcePath = _sources.GetValueOrDefault(newValue, string.Empty);
        ResetVersionState();
        ResetInfo();
        RefreshTexts();
        if (SourcePath.Trim().Length > 0)
        {
            _inspectDelay.Stop();
            _inspectDelay.Start();
        }
    }

    partial void OnSourcePathChanged(string value)
    {
        OnPropertyChanged(nameof(CanStart));
        StartCommand.NotifyCanExecuteChanged();
        if (IsBusy)
        {
            return;
        }

        if (Mode == Ps4Mode.Extract && !Clean(value).Equals(_selectionSource, StringComparison.OrdinalIgnoreCase))
        {
            _extractSelection = null;
            SelectionSummary = Loc.T("Ps4.Selection.All");
        }

        _inspection++;
        ResetVersionState();
        ResetInfo();
        AutoOutputFromSource();
        _inspectDelay.Stop();
        if (Clean(value).Length > 0)
        {
            _inspectDelay.Start();
        }
    }

    partial void OnOutputFolderChanged(string value)
    {
        _settings.Ps4OutputFolder = value;
        OnPropertyChanged(nameof(CanStart));
        StartCommand.NotifyCanExecuteChanged();
    }

    partial void OnFullVerifyChanged(bool value)
    {
        if (Mode != Ps4Mode.Dlc)
        {
            _settings.Ps4FullVerify = value;
        }
    }

    partial void OnGp4OnlyChanged(bool value) => OnPropertyChanged(nameof(StartText));

    partial void OnSourceVersionChanged(string value) => OnPropertyChanged(nameof(SourceVersionText));

    partial void OnVersionChanged(string value)
    {
        if (!_settingVersion)
        {
            _versionEdited = true;
        }
    }

    partial void OnPasscodeChanged(string value) => OnPropertyChanged(nameof(PasscodeValid));

    partial void OnInfoIconChanged(Bitmap? value) => OnPropertyChanged(nameof(HasIcon));

    partial void OnShowSfoEditorChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowSettingsForm));
        if (value && _sfoBaseBytes == null && Clean(SourcePath).Length > 0)
        {
            _ = LoadSfoFromSourceAsync(Clean(SourcePath), Kind, force: false);
        }
    }

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(ShowBusyBanner));
        StartCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
    }

    partial void OnBusyModeChanged(Ps4Mode? value)
    {
        OnPropertyChanged(nameof(BusyText));
        OnPropertyChanged(nameof(ShowBusyBanner));
    }

    partial void OnLogFilterChanged(string value) => RebuildVisibleLog();

    partial void OnSfoQuickTitleChanged(string value) => ApplyQuickTitle();

    partial void OnSfoTitleAllLanguagesChanged(bool value) => ApplyQuickTitle();

    private void RefreshTexts()
    {
        foreach (var name in new[]
                 {
                     nameof(IsExtract), nameof(ShowBase), nameof(ShowBasePasscode), nameof(ShowBaseFolderButton), nameof(ShowVersion), nameof(ShowRandomPass),
                     nameof(ShowEntitlement), nameof(ShowGp4Only), nameof(ShowSfoTabs), nameof(ShowSettingsForm), nameof(CanToggleVerify), nameof(SourcePicksFile),
                     nameof(ShowSourceFolderButton), nameof(SourceLabel), nameof(SourceWatermark), nameof(BaseLabel), nameof(PasscodeLabel), nameof(VersionLabel),
                     nameof(FullVerifyLabel), nameof(FullVerifyTip), nameof(Gp4OnlyLabel), nameof(OutputHint), nameof(StartText), nameof(StepSourceTitle), nameof(ModeHint),
                     nameof(SourceVersionText), nameof(LogCountText), nameof(BusyText), nameof(ShowBusyBanner), nameof(Kind),
                 })
        {
            OnPropertyChanged(name);
        }

        foreach (var row in SfoRows)
        {
            row.RefreshTexts();
        }

        if (!IsBusy && !Succeeded && !Failed)
        {
            StatusText = Loc.T("Ps4.Status.Ready");
            PhaseText = StatusText;
        }

        if (_extractSelection == null && Mode == Ps4Mode.Extract)
        {
            SelectionSummary = Loc.T("Ps4.Selection.All");
        }
        else if (_extractSelection != null)
        {
            SelectionSummary = Loc.F("Ps4.Selection.Some", _extractSelection.Length.ToString("N0", CultureInfo.InvariantCulture));
        }
    }

    /// <summary>Đổi ngôn ngữ: đọc lại thông tin nguồn (chữ mô tả, huy hiệu) và dựng lại bảng SFO nếu chưa sửa gì.</summary>
    private void OnLanguageChanged()
    {
        RefreshTexts();
        if (IsBusy)
        {
            return;
        }

        if (_sfoBaseBytes != null && !IsSfoDirty)
        {
            FillSfo(_sfoBaseBytes, _sfoOriginRaw, _sfoFromFile);
        }

        if (Clean(SourcePath).Length > 0)
        {
            _inspectDelay.Stop();
            _inspectDelay.Start();
        }
        else
        {
            ResetInfo();
        }
    }

    private static string Clean(string? path) => (path ?? string.Empty).Trim().Trim('"');

    // ── Thông tin nguồn ─────────────────────────────────────────────────────────────────────────────────────────────

    private void ResetInfo()
    {
        InfoTitle = Loc.T("Ps4.Info.None");
        InfoSubtitle = Loc.T("Ps4.Info.NoneHint");
        InfoContentId = "—";
        InfoTitleId = "—";
        InfoVersion = "—";
        InfoBadge = Loc.T("Ps4.Info.BadgeOffline");
        InfoOk = false;
        InfoWarn = false;
        InfoIcon = null;
    }

    private void ResetVersionState()
    {
        SourceVersion = string.Empty;
        _versionEdited = false;
        SetVersionText(string.Empty);
        VersionHint = string.Empty;
    }

    private void SetVersionText(string value)
    {
        _settingVersion = true;
        Version = value;
        _settingVersion = false;
    }

    /// <summary>Thư mục xuất tự đặt cạnh nguồn (sửa được), như bản Studio.</summary>
    private void AutoOutputFromSource()
    {
        var path = Clean(SourcePath);
        string? folder = null;
        try
        {
            if (File.Exists(path))
            {
                folder = Path.GetDirectoryName(Path.GetFullPath(path));
            }
            else if (Directory.Exists(path))
            {
                var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
                if (Path.GetFileName(full).Equals("image0", StringComparison.OrdinalIgnoreCase))
                {
                    full = Path.GetDirectoryName(full) ?? full;
                }

                folder = Path.GetDirectoryName(full);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            folder = null;
        }

        if (!string.IsNullOrEmpty(folder) && !string.Equals(Clean(OutputFolder), folder, StringComparison.OrdinalIgnoreCase))
        {
            OutputFolder = folder;
        }
    }

    [RelayCommand]
    private async Task InspectAsync()
    {
        _versionEdited = false;
        await InspectSourceAsync();
    }

    /// <summary>Đọc thông tin nguồn (tên, ảnh, Content ID, phiên bản) ở luồng nền; kết quả cũ bị bỏ khi người dùng đổi nguồn.</summary>
    public async Task InspectSourceAsync()
    {
        _inspectDelay.Stop();
        var path = Clean(SourcePath);
        if (path.Length == 0 || IsBusy)
        {
            return;
        }

        var pass = Passcode;
        var generation = ++_inspection;
        var mode = Mode;
        IsInspecting = true;
        InfoSubtitle = Loc.T("Ps4.Info.Reading");
        try
        {
            var result = await Task.Run(() => Inspect(path, pass, mode));
            if (generation != _inspection)
            {
                return;
            }

            InfoTitle = result.Title;
            InfoTitleId = result.TitleId.Length > 0 ? result.TitleId : "—";
            InfoContentId = result.ContentId.Length > 0 ? result.ContentId : "—";
            InfoVersion = result.Version.Length > 0 ? result.Version : "—";
            InfoSubtitle = Loc.T(File.Exists(path) ? "Ps4.Info.KindPkg" : "Ps4.Info.KindFolder") + "  ·  " + result.Category
                + (result.Bytes > 0 ? "  ·  " + Formatters.Size(result.Bytes) : string.Empty);
            InfoBadge = result.Badge;
            InfoOk = result.Ok;
            InfoWarn = !result.Ok;
            InfoIcon = result.Icon is { Length: > 0 } icon ? LoadBitmap(icon) : null;
            if (mode != Ps4Mode.Extract)
            {
                SourceVersion = result.Version;
                if (!_versionEdited)
                {
                    SetVersionText(SourceVersion);
                }

                VersionHint = result.VersionNote;
            }

            if (mode is Ps4Mode.Patch or Ps4Mode.Merge)
            {
                await LoadSfoFromSourceAsync(path, Kind, force: false);
            }
        }
        catch (Exception ex)
        {
            if (generation != _inspection)
            {
                return;
            }

            ResetInfo();
            InfoBadge = Loc.T("Ps4.Info.ReadFailed");
            InfoWarn = true;
            InfoSubtitle = ex.Message;
            if (mode != Ps4Mode.Extract)
            {
                VersionHint = ex.Message;
            }
        }
        finally
        {
            if (generation == _inspection)
            {
                IsInspecting = false;
            }
        }
    }

    private sealed record InspectResult(string Title, string TitleId, string ContentId, string Version, string Category, long Bytes, string Badge, bool Ok, byte[]? Icon, string VersionNote);

    private static InspectResult Inspect(string path, string passcode, Ps4Mode mode)
    {
        if (mode == Ps4Mode.Extract || (File.Exists(path) && mode != Ps4Mode.Merge))
        {
            var package = Ps4PackageReader.Inspect(path, passcode);
            var badge = package.PasscodeMatches ? "Ps4.Info.BadgePasscode" : package.RecoverableFakePackage ? "Ps4.Info.BadgeFakeKey" : "Ps4.Info.BadgeLocked";
            return new InspectResult(package.Title, package.TitleId, package.ContentId, package.Version, package.Category, package.Bytes, Loc.T(badge),
                package.PasscodeMatches || package.RecoverableFakePackage, Ps4PackageReader.ReadIcon(path), string.Empty);
        }

        if (mode == Ps4Mode.Merge)
        {
            var info = Ps4MergeBuilder.InspectInput(path, true, passcode);
            var isPackage = File.Exists(path);
            var iconPath = isPackage ? string.Empty : Ps4ProjectBuilder.LocateMetadata(info.Path, "icon0.png");
            return new InspectResult(info.Title, info.TitleId, info.ContentId, info.Version, info.Category, info.Bytes,
                Loc.T(info.Category == "gp" ? "Ps4.Info.BadgeUpdateReady" : "Ps4.Info.BadgeNotUpdate"), info.Category == "gp",
                isPackage ? Ps4PackageReader.ReadIcon(path) : File.Exists(iconPath) ? File.ReadAllBytes(iconPath) : null,
                Loc.T("Ps4.Info.MergeVersionNote"));
        }

        var kind = mode switch { Ps4Mode.Patch => Ps4JobKind.Patch, Ps4Mode.Dlc => Ps4JobKind.Dlc, _ => Ps4JobKind.Base };
        var folder = Ps4ProjectBuilder.InspectFolder(path, kind);
        var icon = Ps4ProjectBuilder.LocateMetadata(Ps4ProjectBuilder.ResolveImage(path), "icon0.png");
        return new InspectResult(folder["TITLE"], folder["TITLE_ID"], folder["CONTENT_ID"], folder["APP_VER"], folder["CATEGORY"], 0,
            Loc.T("Ps4.Info.BadgeSfoRead"), true, File.Exists(icon) ? File.ReadAllBytes(icon) : null, folder["VERSION_NOTE"]);
    }

    private static Bitmap? LoadBitmap(byte[] data)
    {
        try
        {
            using var stream = new MemoryStream(data);
            return new Bitmap(stream);
        }
        catch (Exception)
        {
            return null;
        }
    }

    // ── Chọn đường dẫn ──────────────────────────────────────────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task BrowseSourceAsync()
    {
        var path = await _dialogs.PickFileAsync(SourceLabel, null, DialogService.PkgFiles);
        if (path != null)
        {
            SourcePath = path;
        }
    }

    [RelayCommand]
    private async Task BrowseSourceFolderAsync()
    {
        var path = await _dialogs.PickFolderAsync(SourceLabel, null);
        if (path != null)
        {
            SourcePath = path;
        }
    }

    [RelayCommand]
    private async Task BrowseBaseAsync()
    {
        var path = await _dialogs.PickFileAsync(BaseLabel, null, DialogService.PkgFiles);
        if (path != null)
        {
            BasePath = path;
        }
    }

    [RelayCommand]
    private async Task BrowseBaseFolderAsync()
    {
        var path = await _dialogs.PickFolderAsync(BaseLabel, null);
        if (path != null)
        {
            BasePath = path;
        }
    }

    [RelayCommand]
    private async Task BrowseOutputAsync()
    {
        var path = await _dialogs.PickFolderAsync(Loc.T("Ps4.Output"), Clean(OutputFolder).Length > 0 && Directory.Exists(Clean(OutputFolder)) ? Clean(OutputFolder) : null);
        if (path != null)
        {
            OutputFolder = path;
        }
    }

    [RelayCommand]
    private async Task BrowseEngineAsync()
    {
        var path = await _dialogs.PickFileAsync(Loc.T("Ps4.Engine.Pick"), null);
        if (path != null)
        {
            EnginePath = path;
        }
    }

    /// <summary>Chọn từng tệp để xả (mặc định xả toàn bộ PKG).</summary>
    [RelayCommand]
    private async Task PickFilesAsync()
    {
        var path = Clean(SourcePath);
        if (!File.Exists(path))
        {
            StatusText = Loc.T("Ps4.Selection.PickPkgFirst");
            return;
        }

        StatusText = Loc.T("Ps4.Selection.Reading");
        try
        {
            var passcode = Passcode;
            var files = await Task.Run(() => Ps4PackageReader.ListFiles(path, passcode));
            var result = await Views.Ps4FilePickerWindow.PickAsync(_dialogs.Owner, files, _extractSelection);
            if (result.Accepted)
            {
                _extractSelection = result.Paths;
                _selectionSource = path;
                SelectionSummary = _extractSelection == null ? Loc.T("Ps4.Selection.All") : Loc.F("Ps4.Selection.Some", _extractSelection.Length.ToString("N0", CultureInfo.InvariantCulture));
            }

            StatusText = Loc.F("Ps4.Selection.Read", files.Count.ToString("N0", CultureInfo.InvariantCulture));
        }
        catch (Exception ex)
        {
            StatusText = Loc.F("Ps4.Selection.Failed", ex.Message);
            AddLog(new LogEntry(LogLevel.Error, ex.Message));
        }
    }

    /// <summary>Passcode ngẫu nhiên 32 ký tự cho gói mới (bảng chữ không có ký tự dễ nhầm, như bản Studio).</summary>
    [RelayCommand]
    private void RandomPasscode()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";
        Passcode = new string(Enumerable.Range(0, 32).Select(_ => alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)]).ToArray());
        StatusText = Loc.T("Ps4.Passcode.Generated");
    }

    [RelayCommand]
    private void VersionUp() => ChangeVersion(1);

    [RelayCommand]
    private void VersionDown() => ChangeVersion(-1);

    private void ChangeVersion(int delta)
    {
        try
        {
            Version = Ps4AppVersion.Step(Version, delta);
        }
        catch (ArgumentException ex)
        {
            StatusText = ex.Message;
        }
    }

    /// <summary>Chuẩn hoá ô phiên bản khi rời ô (1.5 → 01.50 không đổi nghĩa: chỉ dạng NN.NN hợp lệ mới được định dạng lại).</summary>
    public void NormalizeVersion()
    {
        if (Ps4AppVersion.TryParse(Version, out var value))
        {
            SetVersionText(Ps4AppVersion.Format(value));
        }
    }

    [RelayCommand]
    private async Task ResetVersionAsync()
    {
        _versionEdited = false;
        await InspectSourceAsync();
    }

    /// <summary>Phiên bản PS4 dạng "NN.NN": tăng/giảm 0.01, giữ trong 00.00–99.99.</summary>
    public static string StepVersion(string version, int delta)
    {
        var parts = (version ?? string.Empty).Split('.');
        var value = parts.Length == 2 && int.TryParse(parts[0], out var major) && int.TryParse(parts[1], out var minor) && minor is >= 0 and < 100
            ? major * 100 + minor
            : 100;
        value = Math.Clamp(value + delta, 0, 9999);
        return $"{value / 100:00}.{value % 100:00}";
    }

    // ── Trình sửa param.sfo (Đóng Update / Gộp) ─────────────────────────────────────────────────────────────────────

    [RelayCommand]
    private void ShowSettings() => ShowSfoEditor = false;

    [RelayCommand]
    private void ShowSfo() => ShowSfoEditor = true;

    private static bool IsLocalizedTitle(string key) => Regex.IsMatch(key, "^TITLE_[0-9]{2}$");

    private string? SfoLockNote(string key) => key switch
    {
        "CONTENT_ID" => Loc.T("Ps4.SfoLock.ContentId"),
        "CATEGORY" => Loc.T("Ps4.SfoLock.Category"),
        "APP_VER" => Loc.T(Mode == Ps4Mode.Merge ? "Ps4.SfoLock.AppVerMerge" : "Ps4.SfoLock.AppVer"),
        "VERSION" when Mode == Ps4Mode.Merge => Loc.T("Ps4.SfoLock.VersionMerge"),
        _ => null,
    };

    private void ApplyQuickTitle()
    {
        if (_sfoSyncing)
        {
            return;
        }

        _sfoSyncing = true;
        try
        {
            foreach (var row in SfoRows.Where(r => !r.Locked && (r.Key == "TITLE" || SfoTitleAllLanguages && IsLocalizedTitle(r.Key))))
            {
                row.Value = SfoQuickTitle;
            }
        }
        finally
        {
            _sfoSyncing = false;
        }
    }

    private void SetSfoStatus(string text, bool error = false, bool warn = false)
    {
        SfoStatus = text;
        SfoStatusError = error;
        SfoStatusWarn = warn;
    }

    private void UpdateSfoDirty()
    {
        if (!_sfoSyncing && SfoRows.FirstOrDefault(r => r.Key == "TITLE")?.Value is { } title && title != SfoQuickTitle)
        {
            _sfoSyncing = true;
            SfoQuickTitle = title;
            _sfoSyncing = false;
        }

        var count = SfoRows.Count(r => r.IsModified) + _sfoRemoved.Count;
        SfoDirtyText = !IsSfoDirty ? string.Empty
            : _sfoFromFile ? (count > 0 ? Loc.F("Ps4.SfoEdit.FromFileEdited", count) : Loc.T("Ps4.SfoEdit.FromFile"))
            : Loc.F("Ps4.SfoEdit.Edited", count);
        OnPropertyChanged(nameof(IsSfoDirty));
        var error = SfoRows.FirstOrDefault(r => r.Error != null)?.Error;
        if (error != null)
        {
            SetSfoStatus(error, error: true);
        }
        else if (IsSfoDirty)
        {
            SetSfoStatus(Loc.T("Ps4.SfoEdit.DirtyHint"), warn: true);
        }
        else
        {
            SetSfoStatus(Loc.T("Ps4.SfoEdit.Hint"));
        }
    }

    private void FillSfo(byte[] bytes, string origin, bool fromFile)
    {
        var document = Ps4SfoDocument.Parse(bytes);
        _sfoBaseBytes = bytes;
        _sfoFromFile = fromFile;
        _sfoRemoved.Clear();
        SfoRows.Clear();
        foreach (var entry in document.Entries.OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            var note = SfoNoteKeys.TryGetValue(entry.Key, out var key) ? Loc.T(key)
                : entry.Key.StartsWith("TITLE_", StringComparison.Ordinal) ? Loc.F("Ps4.SfoNote.TitleLang", entry.Key[6..]) : string.Empty;
            SfoRows.Add(new Ps4SfoRow(entry.Key, entry.Format, entry.MaxLength, entry.Display, false, SfoLockNote(entry.Key), note) { OnChanged = UpdateSfoDirty });
        }

        _sfoOriginRaw = origin;
        SfoOrigin = Loc.F(fromFile ? "Ps4.SfoEdit.OriginFile" : "Ps4.SfoEdit.OriginSource", origin);
        UpdateSfoDirty();
    }

    private async Task LoadSfoFromSourceAsync(string path, Ps4JobKind kind, bool force)
    {
        var key = kind + "|" + path;
        if (!force && key == _sfoLoadedKey && _sfoBaseBytes != null)
        {
            return;
        }

        if (!force && _sfoLoadedKey.Length > 0 && key != _sfoLoadedKey && IsSfoDirty)
        {
            AddLog(new LogEntry(LogLevel.Info, Loc.T("Ps4.SfoEdit.SourceChanged")));
        }

        try
        {
            var (bytes, origin) = await Task.Run(() => Ps4SfoDocument.ReadSource(path, kind));
            if (Clean(SourcePath) != path || Kind != kind)
            {
                return;
            }

            FillSfo(bytes, origin, fromFile: false);
            _sfoLoadedKey = key;
        }
        catch (Exception ex)
        {
            SfoRows.Clear();
            _sfoRemoved.Clear();
            _sfoBaseBytes = null;
            _sfoFromFile = false;
            _sfoLoadedKey = string.Empty;
            SfoOrigin = Loc.T("Ps4.SfoEdit.ReadFailed");
            SfoDirtyText = string.Empty;
            SetSfoStatus(ex.Message, error: true);
        }
    }

    private byte[] BuildEditedSfo()
    {
        if (_sfoBaseBytes == null)
        {
            throw new InvalidOperationException(Loc.T("Ps4.SfoEdit.NotRead"));
        }

        var document = Ps4SfoDocument.Parse(_sfoBaseBytes);
        foreach (var key in _sfoRemoved)
        {
            document.Remove(key);
        }

        foreach (var row in SfoRows)
        {
            if (row.Error != null)
            {
                throw new FormatException(row.Error);
            }

            if (row.IsNew)
            {
                document.Remove(row.Key);
                document.Add(row.Key, row.Format, row.Value);
            }
            else if (row.IsModified)
            {
                document.SetText(row.Key, row.Value);
            }
        }

        return document.ToBytes();
    }

    /// <summary>Ghi param.sfo đã sửa cho lượt đóng gói; null khi nguồn hiện tại không có sửa đổi nào.</summary>
    private string? SaveEditedSfo()
    {
        if (!IsSfoDirty)
        {
            return null;
        }

        if (!_sfoFromFile && _sfoLoadedKey != Kind + "|" + Clean(SourcePath))
        {
            AddLog(new LogEntry(LogLevel.Warning, Loc.T("Ps4.SfoEdit.OtherSource")));
            return null;
        }

        var bytes = BuildEditedSfo();
        var title = Regex.Replace(SfoRows.FirstOrDefault(r => r.Key == "TITLE_ID")?.Value ?? "custom", "[^A-Za-z0-9_-]", string.Empty);
        var folder = Path.Combine(Path.GetDirectoryName(SettingsService.Path)!, "ps4-sfo");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, $"{title}_{DateTime.Now:yyyyMMdd_HHmmss}_param.sfo");
        File.WriteAllBytes(file, bytes);
        var changed = SfoRows.Where(r => r.IsModified).Select(r => r.Key).Concat(_sfoRemoved.Select(k => "-" + k)).ToList();
        AddLog(new LogEntry(LogLevel.Info, Loc.F("Ps4.SfoEdit.Using", changed.Count > 0 ? string.Join(", ", changed) : Loc.T("Ps4.SfoEdit.LoadedFile"), file)));
        return file;
    }

    [RelayCommand]
    private async Task SfoReloadAsync()
    {
        var path = Clean(SourcePath);
        if (path.Length == 0)
        {
            SetSfoStatus(Loc.T("Ps4.SfoEdit.PickSourceFirst"), warn: true);
            return;
        }

        if (IsSfoDirty && !await _dialogs.ConfirmAsync(Loc.T("Ps4.SfoEdit.Title"), Loc.T("Ps4.SfoEdit.ReloadConfirm"), Loc.T("Ps4.SfoEdit.Reload"), Loc.T("Common.Cancel")))
        {
            return;
        }

        await LoadSfoFromSourceAsync(path, Kind, force: true);
    }

    [RelayCommand]
    private async Task SfoOpenAsync()
    {
        var file = await _dialogs.PickFileAsync(Loc.T("Ps4.SfoEdit.OpenTitle"), null, DialogService.SfoFiles);
        if (file == null)
        {
            return;
        }

        try
        {
            var bytes = await File.ReadAllBytesAsync(file);
            var current = SfoRows.FirstOrDefault(r => r.Key == "CONTENT_ID")?.Original;
            FillSfo(bytes, file, fromFile: true);
            var loaded = SfoRows.FirstOrDefault(r => r.Key == "CONTENT_ID")?.Original;
            if (current != null && loaded != current)
            {
                SetSfoStatus(Loc.F("Ps4.SfoEdit.OtherContentId", loaded, current), error: true);
            }
        }
        catch (Exception ex)
        {
            SetSfoStatus(Loc.F("Ps4.SfoEdit.OpenFailed", ex.Message), error: true);
        }
    }

    [RelayCommand]
    private async Task SfoSaveAsync()
    {
        if (_sfoBaseBytes == null)
        {
            SetSfoStatus(Loc.T("Ps4.SfoEdit.NothingToSave"), warn: true);
            return;
        }

        byte[] bytes;
        try
        {
            bytes = BuildEditedSfo();
        }
        catch (Exception ex)
        {
            SetSfoStatus(ex.Message, error: true);
            return;
        }

        var target = await _dialogs.SaveFileAsync(Loc.T("Ps4.SfoEdit.SaveTitle"), "param.sfo", "sfo", DialogService.SfoFiles);
        if (target == null)
        {
            return;
        }

        try
        {
            await File.WriteAllBytesAsync(target, bytes);
            SetSfoStatus(Loc.F("Ps4.SfoEdit.Saved", target));
        }
        catch (Exception ex)
        {
            SetSfoStatus(ex.Message, error: true);
        }
    }

    [RelayCommand]
    private void SfoAdd()
    {
        if (_sfoBaseBytes == null)
        {
            SetSfoStatus(Loc.T("Ps4.SfoEdit.ReadFirst"), warn: true);
            return;
        }

        var key = SfoNewKey.Trim().ToUpperInvariant();
        if (!Regex.IsMatch(key, "^[A-Z0-9_]{1,64}$"))
        {
            SetSfoStatus(Loc.T("Ps4.SfoEdit.KeyFormat"), error: true);
            return;
        }

        if (SfoRows.Any(r => r.Key == key))
        {
            SetSfoStatus(Loc.F("Ps4.SfoEdit.KeyExists", key), error: true);
            return;
        }

        var format = SfoNewIsNumber ? Ps4SfoDocument.Integer : Ps4SfoDocument.Utf8;
        if (Ps4SfoDocument.Validate(format, SfoNewValue, key) is { } error)
        {
            SetSfoStatus(error, error: true);
            return;
        }

        var max = format == Ps4SfoDocument.Integer ? 4 : Math.Max(Ps4SfoDocument.Align(Encoding.UTF8.GetByteCount(SfoNewValue) + 1), 128);
        var row = new Ps4SfoRow(key, format, max, SfoNewValue, true, null, Loc.T("Ps4.SfoEdit.NewField")) { OnChanged = UpdateSfoDirty };
        var index = SfoRows.TakeWhile(r => string.CompareOrdinal(r.Key, key) < 0).Count();
        SfoRows.Insert(index, row);
        _sfoRemoved.Remove(key);
        SelectedSfoRow = row;
        SfoNewKey = string.Empty;
        SfoNewValue = string.Empty;
        UpdateSfoDirty();
    }

    [RelayCommand]
    private void SfoRemove()
    {
        if (SelectedSfoRow is not { } row)
        {
            SetSfoStatus(Loc.T("Ps4.SfoEdit.PickRow"), warn: true);
            return;
        }

        if (row.Locked)
        {
            SetSfoStatus(Loc.F("Ps4.SfoEdit.Locked", row.Key), warn: true);
            return;
        }

        SfoRows.Remove(row);
        if (!row.IsNew)
        {
            _sfoRemoved.Add(row.Key);
        }

        UpdateSfoDirty();
    }

    // ── Chạy tác vụ ─────────────────────────────────────────────────────────────────────────────────────────────────

    private bool CanRunStart() => CanStart;

    [RelayCommand(CanExecute = nameof(CanRunStart))]
    private async Task StartAsync()
    {
        if (IsBusy)
        {
            return;
        }

        var mode = Mode;
        var kind = Kind;
        var request = new Ps4JobRequest(
            kind,
            Clean(SourcePath),
            Clean(OutputFolder),
            Clean(BasePath),
            Passcode,
            mode == Ps4Mode.Extract ? string.Empty : Version.Trim(),
            FullVerify,
            mode != Ps4Mode.Extract && Gp4Only,
            Clean(EnginePath),
            ExtractOnly: mode == Ps4Mode.Extract ? _extractSelection : null,
            EntitlementKey: Clean(EntitlementKey),
            BasePasscode: Clean(BasePasscode));
        if (mode is Ps4Mode.Patch or Ps4Mode.Merge)
        {
            try
            {
                if (SaveEditedSfo() is { } edited)
                {
                    request = request with { CustomParamSfo = edited };
                }
            }
            catch (Exception ex)
            {
                ShowSfoEditor = true;
                SetSfoStatus(ex.Message, error: true);
                return;
            }
        }

        SettingsService.Save(_settings);
        _cancellation = new CancellationTokenSource();
        IsBusy = true;
        BusyMode = mode;
        Succeeded = false;
        Failed = false;
        LastFolder = string.Empty;
        LastLog = string.Empty;
        LastPackage = string.Empty;
        Percent = 0;
        SpeedText = "—";
        EtaText = "—";
        DetailText = string.Empty;
        Estimated = false;
        PhaseText = Loc.T("Ps4.Status.Starting");
        StatusText = Loc.T("Ps4.Status.Running");
        _elapsed.Restart();
        _tick.Start();
        AddLog(new LogEntry(LogLevel.Info, Loc.F("Ps4.Log.Start", Loc.T("Nav.Ps4" + mode), Loc.T(request.Verify ? "Ps4.Log.ModeFull" : "Ps4.Log.ModeFast"))));
        try
        {
            var result = await Ps4JobRunner.RunAsync(request, OnJobLog, progress => Interlocked.Exchange(ref _pendingProgress, progress), _cancellation.Token);
            LastFolder = result.Directory;
            LastLog = result.Log;
            LastPackage = result.Package ?? string.Empty;
            Tick();
            Percent = 100;
            PhaseText = Loc.T(result.Status == "project_ready" ? "Ps4.Phase.Gp4Ready" : "Ps4.Phase.Done");
            DetailText = result.Package ?? result.Project ?? result.Directory;
            SpeedText = "—";
            EtaText = "00:00";
            Succeeded = true;
            StatusText = Loc.T(result.Verified ? "Ps4.Status.DoneVerified" : result.Status == "project_ready" ? "Ps4.Status.DoneGp4" : mode == Ps4Mode.Extract ? "Ps4.Status.DoneExtract" : "Ps4.Status.Done");
            AddLog(new LogEntry(LogLevel.Success, Loc.F("Ps4.Log.Result", DetailText)));
        }
        catch (Exception ex)
        {
            if (ex is Ps4JobFailureException failure)
            {
                LastFolder = failure.Folder;
                LastLog = Path.Combine(failure.Folder, "job.log");
            }

            Tick();
            var stopped = _cancellation.IsCancellationRequested;
            Failed = true;
            PhaseText = Loc.T(stopped ? "Ps4.Status.Stopped" : "Ps4.Status.Failed");
            DetailText = ex.Message;
            StatusText = Loc.T(stopped ? "Ps4.Status.StoppedShort" : "Ps4.Status.FailedShort");
            SpeedText = "—";
            EtaText = "—";
            AddLog(new LogEntry(stopped ? LogLevel.Warning : LogLevel.Error, Loc.F("Ps4.Log.Result", ex.Message)));
        }
        finally
        {
            _elapsed.Stop();
            Tick();
            _tick.Stop();
            _cancellation.Dispose();
            _cancellation = null;
            IsBusy = false;
            BusyMode = null;
        }
    }

    private bool CanStop() => IsBusy;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop()
    {
        _cancellation?.Cancel();
        StatusText = Loc.T("Ps4.Status.Stopping");
        StopCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Dòng nhật ký từ luồng tác vụ: "[HH:mm:ss] [MỨC] nội dung" → LogEntry (gom lại, đổ vào giao diện theo nhịp 120 ms).</summary>
    private void OnJobLog(string line, string level)
    {
        var message = line;
        var close = message.IndexOf("] [", StringComparison.Ordinal);
        if (message.StartsWith('[') && close > 0)
        {
            var end = message.IndexOf(']', close + 3);
            if (end > close)
            {
                message = message[(end + 1)..].TrimStart();
            }
        }

        if (level == Ps4LogLevel.Engine)
        {
            message = "› " + message;
        }

        _pendingLog.Enqueue(new LogEntry(level switch
        {
            Ps4LogLevel.Ok => LogLevel.Success,
            Ps4LogLevel.Warn or Ps4LogLevel.Stop => LogLevel.Warning,
            Ps4LogLevel.Error => LogLevel.Error,
            _ => LogLevel.Info,
        }, message));
    }

    private void Tick()
    {
        var batch = new List<LogEntry>();
        while (_pendingLog.TryDequeue(out var entry))
        {
            batch.Add(entry);
        }

        foreach (var entry in batch)
        {
            AddLog(entry);
        }

        if (Interlocked.Exchange(ref _pendingProgress, null) is { } progress)
        {
            PhaseText = progress.Phase;
            DetailText = progress.Detail;
            Percent = Math.Max(Percent, Math.Clamp(progress.Percent, 0, 100));
            Estimated = progress.Estimated;
            SpeedText = progress.BytesPerSecond > 0 ? Formatters.Rate(progress.BytesPerSecond) : "—";
            EtaText = progress.SecondsRemaining is > 0 and < 864000 ? Duration(progress.SecondsRemaining.Value) : "—";
        }

        ElapsedText = Duration(_elapsed.Elapsed.TotalSeconds);
    }

    private static string Duration(double seconds) =>
        seconds >= 3600 ? TimeSpan.FromSeconds(seconds).ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture) : TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(@"mm\:ss", CultureInfo.InvariantCulture);

    private void AddLog(LogEntry entry)
    {
        Log.AddBatch([entry]);
        _logCount++;
        OnPropertyChanged(nameof(LogCountText));
        if (MatchesFilter(entry))
        {
            VisibleLog.Add(entry);
            if (VisibleLog.Count > LogCollection.Capacity)
            {
                RebuildVisibleLog();
            }
        }
    }

    private bool MatchesFilter(LogEntry entry) =>
        string.IsNullOrWhiteSpace(LogFilter) || entry.Message.Contains(LogFilter.Trim(), StringComparison.OrdinalIgnoreCase);

    private void RebuildVisibleLog()
    {
        VisibleLog.Clear();
        foreach (var entry in Log.Where(MatchesFilter))
        {
            VisibleLog.Add(entry);
        }
    }

    [RelayCommand]
    private void ClearLog()
    {
        Log.Clear();
        VisibleLog.Clear();
        _logCount = 0;
        OnPropertyChanged(nameof(LogCountText));
    }

    [RelayCommand]
    private async Task CopyLogAsync() =>
        await _dialogs.SetClipboardAsync(string.Join(Environment.NewLine, VisibleLog.Select(entry => $"{entry.TimeText} {entry.Message}")));

    [RelayCommand]
    private async Task OpenResultAsync()
    {
        var target = LastPackage.Length > 0 ? Path.GetDirectoryName(LastPackage)! : LastFolder.Length > 0 ? LastFolder : Clean(OutputFolder);
        if (target.Length > 0 && Directory.Exists(target))
        {
            await _dialogs.OpenFolderAsync(target);
        }
    }

    [RelayCommand]
    private async Task OpenLogAsync()
    {
        // Mở thẳng tệp nhật ký bằng ứng dụng mặc định; chỉ khi hệ thống không mở được mới mở thư mục chứa nó.
        if (LastLog.Length > 0 && File.Exists(LastLog) && !await _dialogs.OpenFileAsync(LastLog))
        {
            await _dialogs.OpenFolderAsync(Path.GetDirectoryName(LastLog)!);
        }
    }

    /// <summary>Kéo thả: tệp .pkg / thư mục vào ô nguồn (hoặc ô base khi <paramref name="toBase"/>).</summary>
    public void DropPath(string path, bool toBase)
    {
        if (IsBusy)
        {
            return;
        }

        if (toBase && ShowBase)
        {
            BasePath = path;
        }
        else if (!SourcePicksFile && File.Exists(path))
        {
            // Đóng base / update / DLC cần thư mục đã xả; tệp PKG chỉ là nguồn của trang Xả và trang Gộp.
            StatusText = Loc.T("Ps4.Drop.NeedFolder");
        }
        else
        {
            SourcePath = path;
        }
    }
}
