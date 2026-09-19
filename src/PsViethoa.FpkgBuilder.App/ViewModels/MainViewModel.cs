using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PsViethoa.FpkgBuilder.App.Services;
using PsViethoa.FpkgBuilder.Core.ExFat;
using PsViethoa.FpkgBuilder.Core.Localization;
using PsViethoa.FpkgBuilder.Core.Models;
using PsViethoa.FpkgBuilder.Core.Services;

namespace PsViethoa.FpkgBuilder.App.ViewModels;

public enum StatusKind
{
    Ready,
    Working,
    Success,
    Error,
    Canceled,
}

/// <summary>Toàn bộ trạng thái và hành vi của cửa sổ chính (song ngữ, nguồn thư mục hoặc ảnh exFAT).</summary>
public sealed partial class MainViewModel : ObservableObject
{
    private const int MaxRecentSources = 8;

    /// <summary>Tên trường giả để view đưa tiêu điểm về nút Hủy khi bắt đầu tạo gói.</summary>
    public const string FocusCancelButton = "CancelButton";

    private readonly DialogService _dialogs;
    private readonly AppSettings _settings;
    private readonly BuildEngine _engine = new();
    private readonly ConcurrentQueue<LogEntry> _pendingLogs = new();
    private readonly DispatcherTimer _drainTimer;
    private readonly DispatcherTimer _tickTimer;
    private readonly DispatcherTimer _sourceDebounce;
    private readonly Stopwatch _buildStopwatch = new();

    private CancellationTokenSource? _buildCancellation;
    private CancellationTokenSource? _metadataCancellation;
    private BuildProgress? _pendingProgress;
    private BuildProgress? _lastProgress;
    private DateTime _lastProgressAt;
    private string? _suggestedTemporary;

    private bool _attemptedBuild;
    private bool _syncingPreset;
    private bool _syncingLanguage;
    private string? _lastMetadataSource;
    private SourceMetadata? _lastMetadata;
    private FolderStats? _lastStats;
    private IReadOnlyList<JunkFile> _junkFiles = Array.Empty<JunkFile>();
    private long _sourceBytes;
    private string _statusKey = "Status.Ready";
    private object?[] _statusArgs = Array.Empty<object?>();
    private string _phaseKey = "Phase.NotStarted";
    private BuildOutcome? _outcome;

    public MainViewModel(AppSettings settings, DialogService dialogs)
    {
        _settings = settings;
        _dialogs = dialogs;
        Extraction = new ExtractionViewModel(settings, dialogs, Log);
        Extraction.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ExtractionViewModel.HeadlineText))
            {
                OnPropertyChanged(nameof(FooterStatusText));
            }
        };
        Queue = new QueueViewModel(settings, dialogs, CreateQueueRequest, OutputFolderForQueue, Log);
        Queue.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(QueueViewModel.HeadlineText) or nameof(QueueViewModel.IsBusy))
            {
                OnPropertyChanged(nameof(FooterStatusText));
            }
        };

        KeysAvailable = BuildEngine.KeysAvailable;
        LibraryVersion = BuildEngine.LibraryVersion;
        IsWindows = OperatingSystem.IsWindows();
        ProcessorCount = Environment.ProcessorCount;

        _drainTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(80), DispatcherPriority.Background, (_, _) => Drain());
        _tickTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) => Tick());
        _sourceDebounce = new DispatcherTimer(TimeSpan.FromMilliseconds(450), DispatcherPriority.Background, OnSourceDebounceTick);

        BuildOptionLists();
        LoadSettings();
        Loc.Current.LanguageChanged += (_, _) => OnLanguageChanged();
        _drainTimer.Start();
        RefreshValidation();
        UpdateHints();
        RefreshStatusText();
        PhaseText = Loc.T(_phaseKey);
        StartupUpdateCheck();
        StartupDokanInstall();
        _ = RefreshComponentsAsync();
    }

    private void OnSourceDebounceTick(object? sender, EventArgs e)
    {
        _sourceDebounce.Stop();
        ReloadMetadata();
    }

    // ===================== Thông tin tĩnh =====================

    public bool KeysAvailable { get; }

    public string LibraryVersion { get; }

    public bool IsWindows { get; }

    public int ProcessorCount { get; }

    public string AppVersionText => Loc.F("App.VersionLabel", AppInfo.Version, AppInfo.PlatformLabel);

    // ===================== Kiểm tra cập nhật =====================

    [ObservableProperty] private bool _updateAvailable;
    [ObservableProperty] private string _updateLabel = string.Empty;
    [ObservableProperty] private bool _isCheckingUpdate;
    [ObservableProperty] private bool _checkUpdatesOnStartup = true;
    private string? _updateUrl;

    /// <summary>Chữ trên huy hiệu "Có bản mới vX.Y.Z" ở header.</summary>
    public string UpdateBannerText => Loc.F("Update.Banner", UpdateLabel);

    partial void OnUpdateLabelChanged(string value) => OnPropertyChanged(nameof(UpdateBannerText));

    partial void OnCheckUpdatesOnStartupChanged(bool value) => _settings.CheckUpdatesOnStartup = value;

    /// <summary>Nút kiểm tra cập nhật ở header: hỏi GitHub Releases và báo kết quả bằng hộp thoại.</summary>
    [RelayCommand]
    private Task CheckUpdateAsync() => CheckUpdatesCoreAsync(manual: true);

    /// <summary>Mở trang tải bản mới (tệp zip đúng nền tảng nếu có, không thì trang release).</summary>
    [RelayCommand]
    private async Task OpenUpdateAsync()
    {
        var url = _updateUrl ?? UpdateChecker.ReleasesPage;
        if (!await _dialogs.OpenUriAsync(url))
        {
            await _dialogs.ShowInfoAsync(Loc.T("Update.Title"), url);
        }
    }

    private async Task CheckUpdatesCoreAsync(bool manual)
    {
        if (IsCheckingUpdate)
        {
            return;
        }

        IsCheckingUpdate = true;
        try
        {
            var info = await UpdateChecker.CheckAsync(AppInfo.Version, UpdateChecker.PlatformAssetHint(), CancellationToken.None, UpdateChecker.PreferredAssetToken());
            _settings.LastUpdateCheckUtc = DateTime.UtcNow;
            if (info.IsNewer)
            {
                UpdateLabel = "v" + info.LatestVersion;
                _updateUrl = info.AssetUrl ?? info.ReleaseUrl;
                UpdateAvailable = true;
                Log(LogLevel.Info, Loc.F("Update.LogAvailable", info.LatestVersion, AppInfo.Version, info.ReleaseUrl));

                // Luôn hỏi "có muốn tải không" mỗi lần mở ứng dụng khi còn bản mới (theo yêu cầu của chủ dự án); "Để sau" chỉ đóng hộp thoại.
                var open = await _dialogs.ConfirmAsync(
                    Loc.T("Update.Title"),
                    Loc.F("Update.AvailableBody", info.LatestVersion, AppInfo.Version, info.AssetName ?? info.ReleaseUrl),
                    Loc.T("Update.Download"),
                    Loc.T("Update.Later"));
                if (open)
                {
                    await OpenUpdateAsync();
                }
            }
            else
            {
                UpdateAvailable = false;
                if (manual)
                {
                    await _dialogs.ShowInfoAsync(Loc.T("Update.Title"), Loc.F("Update.UpToDate", AppInfo.Version, info.LatestVersion));
                }
            }
        }
        catch (Exception ex)
        {
            if (manual)
            {
                await _dialogs.ShowErrorAsync(Loc.T("Update.Title"), Loc.F("Update.Failed", ex.Message));
            }
            else
            {
                DebugLog.Write("Update check failed: " + ex.Message);
            }
        }
        finally
        {
            IsCheckingUpdate = false;
        }
    }

    /// <summary>Kiểm tra mỗi lần khởi động (nếu bật): có bản mới thì hiện huy hiệu, chấm báo trên nút ↻ và hỏi có muốn tải không.</summary>
    private void StartupUpdateCheck()
    {
        if (!CheckUpdatesOnStartup)
        {
            return;
        }

        _ = CheckUpdatesCoreAsync(manual: false);
    }

    /// <summary>Phiên bản ngắn hiện cạnh tên ứng dụng ở header ("v2.1.3").</summary>
    public string HeaderVersion => "v" + AppInfo.Version;

    /// <summary>Tiêu đề cửa sổ kèm phiên bản.</summary>
    public string WindowTitle => "PSVIETHOA FPKG Builder " + AppInfo.Version;

    public string CreditsText => Loc.F("App.Credits", LibraryVersion);

    public string ThreadsHint => Loc.F("Advanced.ThreadsHint", ProcessorCount);

    public string ThemeTooltip => Loc.T(IsDarkTheme ? "Header.ThemeToLight" : "Header.ThemeToDark");

    [ObservableProperty] private IReadOnlyList<string> _kindOptions = Array.Empty<string>();
    [ObservableProperty] private IReadOnlyList<string> _imageModeOptions = Array.Empty<string>();
    [ObservableProperty] private IReadOnlyList<string> _backendOptions = Array.Empty<string>();
    [ObservableProperty] private IReadOnlyList<string> _sdkOptions = Array.Empty<string>();
    [ObservableProperty] private IReadOnlyList<string> _exFatOptions = Array.Empty<string>();
    [ObservableProperty] private IReadOnlyList<string> _sourceModeOptions = Array.Empty<string>();
    [ObservableProperty] private IReadOnlyList<string> _pfsOptions = Array.Empty<string>();
    [ObservableProperty] private IReadOnlyList<string> _blockSizeOptions = Array.Empty<string>();
    [ObservableProperty] private IReadOnlyList<string> _shuffleOptions = Array.Empty<string>();

    private static readonly int[] BlockSizesKiB = [128, 192, 256];

    private static readonly ShufflePatternKind[] ShufflePatterns = Enum.GetValues<ShufflePatternKind>();

    private void BuildOptionLists()
    {
        KindOptions = [Loc.T("Kind.App"), Loc.T("Kind.Homebrew"), Loc.T("Kind.Dlc")];
        ImageModeOptions = [Loc.T("ImageMode.Plain"), Loc.T("ImageMode.Native")];
        BackendOptions = [Loc.T("Backend.Auto"), Loc.T("Backend.BuiltIn"), Loc.T("Backend.PubTools"), Loc.T("Backend.None")];
        SdkOptions = SdkVersions.All.Select(g => g.Label).ToList();
        ExFatOptions = [Loc.T("ExFat.Auto"), Loc.T("ExFat.Mount"), Loc.T("ExFat.Extract")];
        SourceModeOptions = [Loc.T("SourceMode.Auto"), Loc.T("SourceMode.Folder")];
        PfsOptions = [Loc.T("Pfs.V2"), Loc.T("Pfs.V3")];
        BlockSizeOptions = BlockSizesKiB.Select(k => $"{k} KiB").ToList();
        ShuffleOptions = ShufflePatterns.Select(BuildPresets.ShufflePatternLabel).ToList();
        SdkCompressionOptions = [Loc.T("SdkLevel.Default"), .. SdkCompressionLevels.Select(level => Loc.F("SdkLevel.Value", level))];
    }

    /// <summary>Các mức <c>--compression_level</c> cho SDK Sony ngoài mặc định (7): mục 0 của hộp chọn = không truyền (như bộ gốc).</summary>
    private static readonly int[] SdkCompressionLevels = [9, 8, 6, 5, 4, 3, 2, 1, 0, -1, -2, -3, -4];

    [ObservableProperty] private IReadOnlyList<string> _sdkCompressionOptions = Array.Empty<string>();

    /// <summary>SDK Sony: 0 = mặc định của Publishing Tools (7, như bộ công cụ gốc); còn lại = <see cref="SdkCompressionLevels"/>.</summary>
    [ObservableProperty] private int _sdkCompressionIndex;

    /// <summary>SDK Sony trên Windows: quét trước song song tệp nguồn để Windows Defender không làm chậm pha kiểm tra tệp (mặc định bật).</summary>
    [ObservableProperty] private bool _sdkPrescan = true;

    /// <summary>SDK Sony: giữ .gp5, scenario và .gp5-assets cạnh gói sau khi tạo xong (mặc định xoá như bộ công cụ fix6).</summary>
    [ObservableProperty] private bool _sdkKeepIntermediate;

    /// <summary>SDK Sony: PlayGo dự phòng (N chunk + số kịch bản gốc) khi nguồn không còn playgo-chunk.dat (mặc định bật: hầu hết dump đã mất bảng gốc).</summary>
    [ObservableProperty] private bool _sdkPlayGoFallback = true;

    /// <summary>Mức --compression_level mà mỗi preset ở bước 3 chọn khi SDK Sony bật: null = không truyền (7, như bộ gốc).</summary>
    private static int? SdkLevelForPreset(BuildPreset preset) =>
        preset.Id == BuildPresets.Fast.Id ? 2 : preset.Id == BuildPresets.Balanced.Id ? 4 : preset.Id == BuildPresets.Maximum.Id ? 9 : null;

    private static BuildPreset? SdkPresetForLevel(int? level) => level switch
    {
        null or 7 => BuildPresets.Smallest,
        2 => BuildPresets.Fast,
        4 => BuildPresets.Balanced,
        9 => BuildPresets.Maximum,
        _ => null,
    };

    public string PresetFastShort => Loc.T(SdkActive ? "Preset.fast.SdkShort" : "Preset.fast.Short");

    public string PresetBalancedShort => Loc.T(SdkActive ? "Preset.balanced.SdkShort" : "Preset.balanced.Short");

    public string PresetSmallestShort => Loc.T(SdkActive ? "Preset.smallest.SdkShort" : "Preset.smallest.Short");

    public string PresetMaximumShort => Loc.T(SdkActive ? "Preset.maximum.SdkShort" : "Preset.maximum.Short");

    partial void OnSdkCompressionIndexChanged(int value)
    {
        if (SdkActive)
        {
            SyncPresetFromSdk();
        }
    }

    /// <summary>SDK Sony bật: các ô preset phản ánh mức --compression_level đang chọn (mặc định 7 = "Nhỏ nhất", như bộ gốc).</summary>
    private void SyncPresetFromSdk()
    {
        if (_syncingPreset)
        {
            return;
        }

        _syncingPreset = true;
        try
        {
            var level = SdkLevelFromIndex(SdkCompressionIndex);
            var preset = SdkPresetForLevel(level);
            PresetFast = preset?.Id == BuildPresets.Fast.Id;
            PresetBalanced = preset?.Id == BuildPresets.Balanced.Id;
            PresetSmallest = preset?.Id == BuildPresets.Smallest.Id;
            PresetMaximum = preset?.Id == BuildPresets.Maximum.Id;
            PresetCustom = preset == null;
            PresetSummary = level is { } chosen ? Loc.F("Preset.SdkSummary", chosen) : Loc.T("Preset.SdkSummaryDefault");
        }
        finally
        {
            _syncingPreset = false;
        }
    }

    private static int? SdkLevelFromIndex(int index) => index <= 0 || index > SdkCompressionLevels.Length ? null : SdkCompressionLevels[index - 1];

    private static int SdkIndexFromLevel(int? level) => level is { } value && Array.IndexOf(SdkCompressionLevels, value) is var position && position >= 0 ? position + 1 : 0;

    /// <summary>Số khối PlayGo sửa được: engine tích hợp, hoặc SDK Sony khi bật PlayGo dự phòng.</summary>
    public bool PlayGoChunksEnabled => EngineOptionsEnabled || SdkPlayGoFallback;

    partial void OnSdkPlayGoFallbackChanged(bool value) => OnPropertyChanged(nameof(PlayGoChunksEnabled));

    public IReadOnlyList<string> RecentSources =>
        _settings.RecentSources.Where(p => Directory.Exists(p) || File.Exists(p)).ToArray();

    public bool HasRecent => RecentSources.Count > 0;

    public LogCollection LogEntries { get; } = new();

    /// <summary>View lắng nghe để tự cuộn nhật ký.</summary>
    public event EventHandler? LogAppended;

    /// <summary>View lắng nghe để đưa con trỏ về trường lỗi.</summary>
    public event EventHandler<string>? FocusFieldRequested;

    // ===================== Chế độ: tạo gói / giải nén gói =====================

    /// <summary>Trạng thái của chế độ "Giải nén gói" (cột thông tin + danh sách tệp trong gói).</summary>
    public ExtractionViewModel Extraction { get; }

    /// <summary>Hàng chờ tạo nhiều gói (chế độ thứ ba, không lưu vào cài đặt).</summary>
    public QueueViewModel Queue { get; }

    [ObservableProperty] private bool _isQueueMode;

    partial void OnIsQueueModeChanged(bool value)
    {
        OnPropertyChanged(nameof(FooterStatusText));
        SelectMode(value, nameof(IsQueueMode));
    }

    /// <summary>Tab "Tạo gói Update": cùng màn hình với Tạo gói nhưng có khối gói gốc / thư mục game gốc và 3 kiểu tạo (update, base, cả hai).</summary>
    [ObservableProperty] private bool _isUpdateMode;

    partial void OnIsUpdateModeChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowBuildView));
        OnPropertyChanged(nameof(BuildButtonText));
        if (PatchEnabled != value)
        {
            PatchEnabled = value;
        }

        SelectMode(value, nameof(IsUpdateMode));
    }

    /// <summary>Màn hình tạo gói dùng chung cho tab Tạo gói và tab Tạo gói Update.</summary>
    public bool ShowBuildView => IsBuildMode || IsUpdateMode;

    /// <summary>
    /// Các nút chế độ là RadioButton cùng nhóm: chỉ phản ứng khi một nút ĐƯỢC bật (tắt các nút kia); không suy ra chế độ từ một nút bị
    /// tắt — nhóm radio tắt các nút kia theo thứ tự riêng, suy diễn từ "false" khiến các handler đá nhau vô hạn.
    /// </summary>
    private void SelectMode(bool value, string mode)
    {
        if (_syncingMode || !value)
        {
            return;
        }

        _syncingMode = true;
        try
        {
            IsBuildMode = mode == nameof(IsBuildMode);
            IsUpdateMode = mode == nameof(IsUpdateMode);
            IsExtractMode = mode == nameof(IsExtractMode);
            IsQueueMode = mode == nameof(IsQueueMode);
        }
        finally
        {
            _syncingMode = false;
        }

        ApplyModeChanged();
    }

    /// <summary>Yêu cầu tạo gói cho một mục hàng chờ: thiết lập của tab Tạo gói, nguồn/đích/thông tin gói của mục.</summary>
    private BuildRequest CreateQueueRequest(QueueItemViewModel item)
    {
        var metadata = item.Metadata;
        var request = CreateRequest();
        request.SourcePath = item.SourcePath;
        request.OutputFolder = string.IsNullOrWhiteSpace(item.OutputFolder) ? OutputFolderForQueue(item.SourcePath) : item.OutputFolder;
        request.ContentId = metadata?.ContentId ?? string.Empty;
        request.Title = string.IsNullOrWhiteSpace(metadata?.Title) ? item.SourceName : metadata!.Title!;
        request.Version = metadata?.Version ?? string.Empty;
        request.SdkMajorOverride = null;
        // Hàng chờ: tên/phiên bản giữ nguyên của từng nguồn.
        request.SdkApplyPackageDetails = false;
        request.SdkPatchBaseFolder = null;
        // Hàng chờ luôn tạo gói đầy đủ: gói gốc của bản vá là của riêng một game.
        request.SdkReferencePackage = null;
        return request;
    }

    /// <summary>Thư mục xuất cho một nguồn trong hàng chờ: "&lt;nguồn&gt;-pkg" khi tab Tạo gói đang "tự đặt theo nguồn", nếu không thì thư mục xuất đã chọn.</summary>
    private string OutputFolderForQueue(string source) =>
        AutoOutputFolder || string.IsNullOrWhiteSpace(OutputFolder) ? BuildPreparer.SuggestOutputFolder(source) : OutputFolder.Trim();

    [ObservableProperty] private bool _isExtractMode;
    [ObservableProperty] private bool _isBuildMode = true;
    private bool _syncingMode;

    partial void OnIsExtractModeChanged(bool value)
    {
        OnPropertyChanged(nameof(FooterStatusText));
        SelectMode(value, nameof(IsExtractMode));
    }

    partial void OnIsBuildModeChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowBuildView));
        SelectMode(value, nameof(IsBuildMode));
    }

    private void ApplyModeChanged()
    {
        DebugLog.Write($"Mode: build={IsBuildMode} update={IsUpdateMode} extract={IsExtractMode} queue={IsQueueMode}");
        BuildCommand.NotifyCanExecuteChanged();
        _settings.ExtractMode = IsExtractMode;
        if (IsExtractMode)
        {
            Extraction.EnsureLoaded();
        }
    }

    // ===================== Ngôn ngữ =====================

    [ObservableProperty] private bool _languageVi = true;
    [ObservableProperty] private bool _languageEn;

    partial void OnLanguageViChanged(bool value)
    {
        if (value)
        {
            SetLanguage(Loc.Vietnamese);
        }
    }

    partial void OnLanguageEnChanged(bool value)
    {
        if (value)
        {
            SetLanguage(Loc.English);
        }
    }

    private void SetLanguage(string code)
    {
        if (_syncingLanguage)
        {
            return;
        }

        _settings.Language = code;
        Loc.Current.SetLanguage(code);
        SettingsService.Save(_settings);
    }

    private void OnLanguageChanged()
    {
        _syncingLanguage = true;
        try
        {
            LanguageVi = Loc.Current.Language == Loc.Vietnamese;
            LanguageEn = Loc.Current.Language == Loc.English;
        }
        finally
        {
            _syncingLanguage = false;
        }

        NotifyMountCapabilities();
        _ = RefreshComponentsAsync();
        var kind = KindIndex;
        var imageMode = ImageModeIndex;
        var backend = BackendIndex;
        var sdk = SdkIndex;
        var exFat = ExFatIndex;
        var sourceMode = SourceModeIndex;
        var pfs = PfsIndex;
        var block = BlockSizeIndex;
        var shuffle = ShuffleIndex;
        BuildOptionLists();
        Dispatcher.UIThread.Post(() =>
        {
            KindIndex = kind;
            ImageModeIndex = imageMode;
            BackendIndex = backend;
            SdkIndex = sdk;
            ExFatIndex = exFat;
            SourceModeIndex = sourceMode;
            PfsIndex = pfs;
            BlockSizeIndex = block;
            ShuffleIndex = shuffle;
        }, DispatcherPriority.Background);

        OnPropertyChanged(nameof(AppVersionText));
        OnPropertyChanged(nameof(CreditsText));
        OnPropertyChanged(nameof(ThreadsHint));
        OnPropertyChanged(nameof(ThemeTooltip));

        UpdateHints();
        SyncPresetFromSettings();
        RefreshStatusText();
        RefreshValidation();
        if (!IsBuilding)
        {
            PhaseText = Loc.T(_phaseKey);
        }

        if (_lastMetadata != null)
        {
            // Giữ nguyên những ô người dùng tự nhập: đổi ngôn ngữ không được lặng lẽ trả chúng về giá trị đọc từ nguồn.
            var contentId = ContentId;
            var title = Title;
            var version = Version;
            ApplyMetadata(SourcePath.Trim(), _lastMetadata);
            ContentId = contentId;
            Title = title;
            Version = version;
            if (_lastStats != null)
            {
                MetaFiles = Loc.F("Meta.Files", Formatters.Count(_lastStats.FileCount), Formatters.Size(_lastStats.TotalBytes));
            }
        }
        else
        {
            ShowEmptyMetadata();
        }

        RefreshJunkSummary();
        RefreshDiskInfo();
        if (_outcome != null)
        {
            ShowResult(_outcome);
        }
    }

    // ===================== Đường dẫn =====================

    [ObservableProperty] private string _sourcePath = string.Empty;
    [ObservableProperty] private string _outputFolder = string.Empty;
    [ObservableProperty] private string _temporaryFolder = string.Empty;

    // ===================== Thông tin gói =====================

    [ObservableProperty] private string _contentId = string.Empty;
    [ObservableProperty] private string _title = string.Empty;
    [ObservableProperty] private string _version = VersionHelper.Default;
    [ObservableProperty] private int _kindIndex;
    [ObservableProperty] private int _imageModeIndex;
    [ObservableProperty] private string _imageModeHint = string.Empty;

    // ===================== Nâng cao =====================

    [ObservableProperty] private string _passcode = new('0', BuildRequest.PasscodeLength);
    [ObservableProperty] private bool _showPasscode;
    [ObservableProperty] private bool _overrideSdk;
    [ObservableProperty] private int _sdkIndex;
    [ObservableProperty] private int _backendIndex;
    [ObservableProperty] private string _backendHint = string.Empty;
    [ObservableProperty] private double _krakenLevel = BuildRequest.DefaultKrakenLevel;
    [ObservableProperty] private string _krakenLevelText = string.Empty;
    [ObservableProperty] private decimal? _threads = 0;
    [ObservableProperty] private decimal? _playGoChunks = BuildRequest.DefaultPlayGoChunks;
    [ObservableProperty] private bool _deterministic = true;
    [ObservableProperty] private bool _computeSha256;

    /// <summary>Kiểm tra đầy đủ gói sau khi tạo: giải mã và giải nén thử mọi tệp trong bộ nhớ (chậm hơn, không ghi gì ra đĩa).</summary>
    [ObservableProperty] private bool _fullVerify;

    /// <summary>Hạ requiredSystemSoftwareVersion về SDK của game (fpkg-gui 0.6.8) — chỉ có tác dụng khi giữ SDK của game.</summary>
    [ObservableProperty] private bool _lowerRequiredFirmware = true;
    [ObservableProperty] private bool _preventSleep = true;
    [ObservableProperty] private string _publishingToolsPath = string.Empty;
    [ObservableProperty] private bool _advancedExpanded;
    [ObservableProperty] private int _exFatIndex;
    [ObservableProperty] private int _sourceModeIndex;
    [ObservableProperty] private int _pfsIndex;
    [ObservableProperty] private int _blockSizeIndex = 2;
    [ObservableProperty] private int _shuffleIndex;
    [ObservableProperty] private bool _shuffleAnalysis;
    [ObservableProperty] private bool _skipPfsCheck;
    [ObservableProperty] private bool _layoutOptimization = true;

    /// <summary>Ép DRM "standard" trong gói để game không bị khoá trên PS5 (engine làm trong bộ nhớ, nguồn không đổi).</summary>
    [ObservableProperty] private bool _forceStandardDrm = true;

    /// <summary>Tạo gói bằng SDK Sony (chuẩn mới, mặc định bật); bỏ tích để dùng engine tích hợp.</summary>
    [ObservableProperty] private bool _useSonySdk = true;

    /// <summary>Máy này chạy được SDK Sony (có bộ công cụ, Wine/Rosetta trên macOS) — do RefreshComponentsAsync xác định.</summary>
    [ObservableProperty] private bool _sdkAvailable = true;

    /// <summary>Lý do SDK Sony không dùng được (đã dịch), rỗng khi dùng được.</summary>
    [ObservableProperty] private string _sdkUnavailableReason = string.Empty;

    /// <summary>Lượt tạo gói sẽ đi qua SDK Sony: ô tích bật, máy chạy được SDK và nguồn không phải dự án .gp5 có sẵn.</summary>
    public bool SdkActive => UseSonySdk && SdkAvailable && !IsGp5Source;

    /// <summary>Các tuỳ chọn chỉ dành cho engine tích hợp (loại gói, chế độ ảnh, mức nén, PFS, PlayGo, SDK, bản dựng xác định…) — mờ khi SDK Sony hoạt động, như bộ gốc không có các lựa chọn này.</summary>
    public bool EngineOptionsEnabled => !SdkActive;

    public bool SdkCheckBoxEnabled => !IsBuilding && !IsGp5Source;

    public bool CanEditPlayGoDrop => !IsBuilding && !SdkActive;

    public bool CanEditDrm => !IsBuilding && !SdkActive;

    /// <summary>Ô "DRM standard": bộ công cụ SDK luôn chuẩn hoá applicationDrmType = standard nên hiện tích và khoá khi SDK hoạt động.</summary>
    public bool ForceStandardDrmUi
    {
        get => SdkActive || ForceStandardDrm;
        set
        {
            if (!SdkActive)
            {
                ForceStandardDrm = value;
            }
        }
    }

    partial void OnForceStandardDrmChanged(bool value) => OnPropertyChanged(nameof(ForceStandardDrmUi));

    public bool ShowSdkUnavailable => UseSonySdk && !SdkAvailable;

    public bool ShowSdkGp5Note => UseSonySdk && SdkAvailable && IsGp5Source;

    public string SdkUnavailableText => Loc.F("Build.SonySdkUnavailable", SdkUnavailableReason);

    /// <summary>Ô "Bỏ sce_sys/playgo*": SDK Sony luôn bỏ nên hiện tích và khoá; giá trị người dùng chọn chỉ đổi khi engine tích hợp.</summary>
    public bool RemovePlayGoFilesUi
    {
        get => SdkActive || RemovePlayGoFiles;
        set
        {
            if (!SdkActive)
            {
                RemovePlayGoFiles = value;
            }
        }
    }

    partial void OnUseSonySdkChanged(bool value) => ApplySdkMode();

    partial void OnSdkAvailableChanged(bool value) => ApplySdkMode();

    partial void OnIsGp5SourceChanged(bool value) => ApplySdkMode();

    partial void OnSdkUnavailableReasonChanged(string value) => OnPropertyChanged(nameof(SdkUnavailableText));

    /// <summary>SDK Sony chỉ tạo gói ứng dụng lớp ngoài không mã hoá: ép hai lựa chọn đó và làm mới mọi thuộc tính phụ thuộc.</summary>
    private void ApplySdkMode()
    {
        if (SdkActive)
        {
            KindIndex = 0;
            ImageModeIndex = 0;
        }

        foreach (var name in new[] { nameof(SdkActive), nameof(EngineOptionsEnabled), nameof(CompressionEnabled), nameof(SdkCheckBoxEnabled), nameof(CanEditPlayGoDrop), nameof(CanEditDrm), nameof(ShowSdkUnavailable), nameof(ShowSdkGp5Note), nameof(RemovePlayGoFilesUi), nameof(ForceStandardDrmUi), nameof(PlayGoChunksEnabled), nameof(PresetFastShort), nameof(PresetBalancedShort), nameof(PresetSmallestShort), nameof(PresetMaximumShort), nameof(PatchAvailable), nameof(ShowPatchNeedsSdk) })
        {
            OnPropertyChanged(name);
        }

        if (SdkActive)
        {
            SyncPresetFromSdk();
        }
        else
        {
            SyncPresetFromSettings();
        }

        if (_lastMetadata != null)
        {
            PlayGoText = DescribePlayGo(_lastMetadata);
        }

        RefreshDiskInfo();
    }

    /// <summary>Dòng PlayGo trên thẻ nguồn: SDK Sony luôn tạo mới 1 kịch bản / 1 khối (bộ gốc), engine tích hợp theo tuỳ chọn.</summary>
    private string DescribePlayGo(SourceMetadata metadata)
    {
        if (SdkActive)
        {
            var count = metadata.PlayGoFileCount + (metadata.HasPlayGoScenario ? 1 : 0);
            return count > 0 ? Loc.F("PlayGo.SonySdk", count) : Loc.T("PlayGo.SonySdkNone");
        }

        return MetadataReader.DescribePlayGo(metadata, (int)(PlayGoChunks ?? BuildRequest.DefaultPlayGoChunks), RemovePlayGoFiles);
    }

    /// <summary>Dọn tàn dư AMPR emu (ampr_emu.index) khỏi gói — engine đã luôn bỏ module giả lập.</summary>
    [ObservableProperty] private bool _removeAmprLeftovers = true;

    /// <summary>Bỏ sce_sys/playgo* của bản dump khỏi gói để thư viện tạo bộ PlayGo mới (mặc định bật, theo Drakmor).</summary>
    [ObservableProperty] private bool _removePlayGoFiles = true;

    /// <summary>Nguồn có bộ giả lập DLC — báo và mời tạo gói DLC riêng.</summary>
    [ObservableProperty] private bool _hasDlcEmuWarning;

    [ObservableProperty] private string _dlcBuildLabel = string.Empty;

    [ObservableProperty] private bool _canBuildDlc;

    [ObservableProperty] private bool _isBuildingDlc;

    /// <summary>Giữ bộ giả lập DLC (dlc_emu.ini + module fakelib) trong gói — mặc định bật; tắt để dọn khỏi gói.</summary>
    [ObservableProperty] private bool _keepDlcEmu = true;

    /// <summary>Xoá versionFileUri trong param.json khi tạo gói (bước 2 của hướng dẫn sửa lỗi PlayGo).</summary>
    [ObservableProperty] private bool _clearVersionFileUri = true;

    /// <summary>Đặt attribute3 trong param.json về 0 khi tạo gói (mặc định tắt từ 2.2.1: attribute3 giữ cờ PS5 Pro / 120 Hz / VRR).</summary>
    [ObservableProperty] private bool _clearPlayGoAttributes;


    public bool IsPfsV3 => PfsIndex == 1;

    partial void OnPfsIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsPfsV3));
        SyncPresetFromSettings();
        RefreshValidation();
    }

    /// <summary>Tự phân tích shuffle chỉ phát huy đầy đủ ở Kraken mức 9 (thông tin từ tác giả thư viện).</summary>
    public bool ShowShuffleLevelWarning => ShuffleAnalysis && KrakenLevel < BuildRequest.MaxKrakenLevel;

    partial void OnShuffleAnalysisChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowShuffleLevelWarning));
        SyncPresetFromSettings();
    }

    partial void OnShuffleIndexChanged(int value) => SyncPresetFromSettings();

    // ===================== Preset =====================

    [ObservableProperty] private bool _presetFast;
    [ObservableProperty] private bool _presetBalanced;
    [ObservableProperty] private bool _presetSmallest;
    [ObservableProperty] private bool _presetMaximum;
    [ObservableProperty] private bool _presetCustom;
    [ObservableProperty] private string _presetSummary = string.Empty;

    // ===================== Metadata nguồn =====================

    [ObservableProperty] private bool _hasSource;
    [ObservableProperty] private bool _hasParamJson;
    [ObservableProperty] private bool _metadataWarning;
    [ObservableProperty] private bool _isExFatSource;

    /// <summary>Nguồn là ảnh UFS2 (.ffpkg) — luôn phải trích ra thư mục tạm vì không hệ điều hành nào gắn được UFS.</summary>
    [ObservableProperty] private bool _isUfsSource;

    /// <summary>Ảnh exFAT nằm trong container .ffpfsc (chỉ giải nén được, không gắn).</summary>
    [ObservableProperty] private bool _isPfsContainerSource;

    /// <summary>Ảnh .exfat thuần (không phải container .ffpfsc).</summary>
    public bool IsPlainExFatSource => IsExFatSource && !IsPfsContainerSource;

    /// <summary>Hệ thống gắn được nguồn hiện tại không (hdiutil: chỉ .exfat thuần; Dokan: cả .ffpfsc).</summary>
    public bool CanMountCurrentSource => IsExFatSource && (IsPfsContainerSource ? ImageMounter.CanMountContainers : ImageMounter.IsAvailable);

    /// <summary>Hiện hộp chọn cách xử lý ảnh: ảnh .exfat thuần, hoặc container khi hệ thống gắn được container (Dokan).</summary>
    public bool ShowExFatStrategy => IsExFatSource && (!IsPfsContainerSource || ImageMounter.CanMountContainers);

    /// <summary>Container .ffpfsc trên hệ thống không gắn được container: chỉ giải thích là sẽ giải nén.</summary>
    public bool ShowPfsContainerHint => IsPfsContainerSource && !ImageMounter.CanMountContainers;

    /// <summary>Windows chưa cài Dokan: gợi ý cài để gắn ảnh như macOS thay vì giải nén.</summary>
    public bool ShowDokanHint => IsExFatSource && ImageMounter.DokanMissingOnWindows;

    /// <summary>Gói kèm bộ cài Dokan: cài ngay từ ứng dụng (một nút, hỏi quyền quản trị một lần).</summary>
    public bool CanInstallDokan => ShowDokanHint && DokanInstaller.IsBundled;

    /// <summary>Bản build không kèm bộ cài: chỉ còn cách tải từ trang Dokan.</summary>
    public bool ShowDokanDownload => ShowDokanHint && !DokanInstaller.IsBundled;

    public string DokanCalloutText => DokanInstaller.IsBundled
        ? Loc.F("Advanced.DokanCalloutBundled", DokanInstaller.BundledVersion)
        : Loc.T("Advanced.DokanCallout");

    [ObservableProperty] private bool _isInstallingDokan;

    /// <summary>Giải thích cách xử lý ảnh theo backend gắn có trên máy.</summary>
    public string ExFatHintText => ImageMounter.Backend switch
    {
        MountBackend.Hdiutil => Loc.T("Advanced.ExFatHint.Hdiutil"),
        MountBackend.Dokan => Loc.F("Advanced.ExFatHint.Dokan", DokanImageMounter.DriverVersion ?? "2.x"),
        _ => Loc.T("Advanced.ExFatHint.NoMount"),
    };

    partial void OnIsPfsContainerSourceChanged(bool value) => NotifyMountCapabilities();

    partial void OnIsExFatSourceChanged(bool value) => NotifyMountCapabilities();

    private void NotifyMountCapabilities()
    {
        OnPropertyChanged(nameof(IsPlainExFatSource));
        OnPropertyChanged(nameof(CanMountCurrentSource));
        OnPropertyChanged(nameof(ShowExFatStrategy));
        OnPropertyChanged(nameof(ShowPfsContainerHint));
        OnPropertyChanged(nameof(ShowDokanHint));
        OnPropertyChanged(nameof(CanInstallDokan));
        OnPropertyChanged(nameof(ShowDokanDownload));
        OnPropertyChanged(nameof(DokanCalloutText));
        OnPropertyChanged(nameof(ExFatHintText));
    }

    /// <summary>Cài driver Dokan kèm sẵn (msiexec im lặng, Windows hỏi quyền quản trị một lần) rồi kiểm tra lại khả năng gắn ảnh.</summary>
    [RelayCommand]
    private async Task InstallDokanAsync()
    {
        if (IsInstallingDokan || !DokanInstaller.CanInstall)
        {
            return;
        }

        var proceed = await _dialogs.ConfirmAsync(
            Loc.T("Dokan.ConfirmTitle"),
            Loc.F("Dokan.ConfirmBody", DokanInstaller.BundledVersion),
            Loc.T("Dokan.ConfirmYes"),
            Loc.T("Common.Cancel"));
        if (proceed)
        {
            await InstallDokanCoreAsync(quiet: false);
        }
    }

    /// <summary>
    /// Chạy bộ cài kèm theo. <paramref name="quiet"/> = lúc khởi động: thành công chỉ ghi nhật ký (UAC là tương tác duy nhất),
    /// thất bại chỉ ghi cảnh báo và để lại nút trong tuỳ chọn nâng cao; cần khởi động lại thì luôn báo.
    /// </summary>
    private async Task InstallDokanCoreAsync(bool quiet)
    {
        IsInstallingDokan = true;
        try
        {
            var result = await Task.Run(() => DokanInstaller.Install(TimeSpan.FromMinutes(10)));
            switch (result.Outcome)
            {
                case DokanInstallOutcome.Installed:
                case DokanInstallOutcome.AlreadyInstalled:
                    var installed = Loc.F("Dokan.Installed", result.Detail ?? DokanInstaller.BundledVersion);
                    Log(LogLevel.Info, installed);
                    if (!quiet)
                    {
                        await _dialogs.ShowInfoAsync(Loc.T("Dokan.DoneTitle"), installed);
                    }

                    break;
                case DokanInstallOutcome.RebootRequired:
                    Log(LogLevel.Warning, Loc.T("Dokan.RebootRequired"));
                    await _dialogs.ShowInfoAsync(Loc.T("Dokan.DoneTitle"), Loc.T("Dokan.RebootRequired"));
                    break;
                case DokanInstallOutcome.Cancelled:
                    Log(LogLevel.Info, Loc.T("Dokan.CancelledLog"));
                    break;
                case DokanInstallOutcome.NotBundled:
                    Log(LogLevel.Warning, Loc.T("Dokan.NotBundled"));
                    if (!quiet)
                    {
                        await _dialogs.ShowErrorAsync(Loc.T("Dokan.FailedTitle"), Loc.T("Dokan.NotBundled"));
                    }

                    break;
                default:
                    var failed = Loc.F("Dokan.Failed", result.ExitCode, result.Detail ?? string.Empty);
                    Log(LogLevel.Warning, failed);
                    if (!quiet)
                    {
                        await _dialogs.ShowErrorAsync(Loc.T("Dokan.FailedTitle"), failed);
                    }

                    break;
            }
        }
        finally
        {
            IsInstallingDokan = false;
            RecheckDokan();
        }
    }

    /// <summary>
    /// Windows, gói kèm bộ cài, chưa có driver: bật gắn ảnh trực tiếp ngay lần mở đầu — người dùng chỉ thấy hộp UAC.
    /// Từ chối thì không làm lại ở các lần mở sau (nút "Bật gắn ảnh trực tiếp" vẫn còn trong tuỳ chọn nâng cao).
    /// </summary>
    private void StartupDokanInstall()
    {
        if (!IsWindows || _settings.DokanPromptShown || !DokanInstaller.CanInstall)
        {
            return;
        }

        _settings.DokanPromptShown = true;
        SettingsService.Save(_settings);
        Log(LogLevel.Info, Loc.F("Dokan.AutoInstallLog", DokanInstaller.BundledVersion));
        _ = InstallDokanCoreAsync(quiet: true);
    }

    // ===================== Thành phần & plugin =====================

    public System.Collections.ObjectModel.ObservableCollection<ComponentRow> Components { get; } = new();

    [ObservableProperty] private bool _isProbingComponents;

    [ObservableProperty] private string _componentsSummary = string.Empty;

    /// <summary>Kiểm tra thật từng thành phần (engine, khoá, Kraken, Oodle, gắn ảnh, chống ngủ) trên luồng nền rồi hiển thị.</summary>
    // ===================== Bản vá (SDK Sony fix8: img_create --ref_pkg_path) =====================

    /// <summary>Tạo bản vá so với gói gốc đã cài thay vì gói đầy đủ (không lưu vào cài đặt: gói gốc khác nhau theo từng game).</summary>
    [ObservableProperty] private bool _patchEnabled;

    [ObservableProperty] private string _referencePackagePath = string.Empty;

    /// <summary>Thông tin gói gốc đã đọc (Content ID · phiên bản · dung lượng).</summary>
    [ObservableProperty] private string _referenceInfoText = string.Empty;

    /// <summary>Lỗi của ô gói gốc (hiện ngay dưới ô), rỗng khi hợp lệ.</summary>
    [ObservableProperty] private string _referenceError = string.Empty;

    [ObservableProperty] private bool _isReadingReference;

    /// <summary>Thư mục game gốc đầy đủ (tuỳ chọn): tệp nguồn không có được đọc từ đây; trống = giải nén riêng các tệp đó từ gói gốc.</summary>
    [ObservableProperty] private string _patchBaseFolder = string.Empty;

    public bool HasPatchBaseFolder => !string.IsNullOrWhiteSpace(PatchBaseFolder);

    /// <summary>
    /// Thư mục update không có param.json: Content ID phải là của gói gốc và phiên bản phải cao hơn nó — điền sẵn cả hai (người dùng
    /// vẫn sửa được). Nguồn có param.json thì giữ nguyên giá trị đọc từ nguồn.
    /// </summary>
    private void AdoptReferenceDetails()
    {
        if (_referenceInfo is not { } reference || !PatchEnabled || HasParamJson)
        {
            return;
        }

        if (!string.IsNullOrEmpty(reference.ContentId))
        {
            ContentId = reference.ContentId;
        }

        if (SuggestedPatchVersion is { } next)
        {
            Version = next;
        }
    }

    partial void OnPatchBaseFolderChanged(string value)
    {
        OnPropertyChanged(nameof(HasPatchBaseFolder));
        RefreshBaseFolder();
    }

    // ----- Tab Update: đầu vào luôn là gói BASE .pkg; 3 lựa chọn là tệp muốn XUẤT RA — 0 = chỉ gói Update, 1 = chỉ gói game đầy đủ đã kèm update, 2 = cả hai -----

    [ObservableProperty] private int _updateKind;

    public bool UpdateKindUpdateOnly
    {
        get => UpdateKind == 0;
        set { if (value) { UpdateKind = 0; } }
    }

    public bool UpdateKindFullOnly
    {
        get => UpdateKind == 1;
        set { if (value) { UpdateKind = 1; } }
    }

    public bool UpdateKindBoth
    {
        get => UpdateKind == 2;
        set { if (value) { UpdateKind = 2; } }
    }

    public string UpdateKindHint => Loc.T(UpdateKind switch { 1 => "Update.KindFullHint", 2 => "Update.KindBothHint", _ => "Update.KindUpdateHint" });

    public string BuildButtonText => Loc.T(!IsUpdateMode ? "Build.Button" : UpdateKind switch { 1 => "Update.ButtonFull", 2 => "Update.ButtonBoth", _ => "Update.ButtonUpdate" });

    partial void OnUpdateKindChanged(int value)
    {
        foreach (var name in new[] { nameof(UpdateKindUpdateOnly), nameof(UpdateKindFullOnly), nameof(UpdateKindBoth), nameof(UpdateKindHint), nameof(BuildButtonText) })
        {
            OnPropertyChanged(name);
        }
    }

    // ----- Thư mục game gốc: đọc param.json ở nền, hiện Content ID · phiên bản ngay dưới ô -----

    [ObservableProperty] private string _baseFolderInfoText = string.Empty;

    [ObservableProperty] private string _baseFolderError = string.Empty;

    public bool HasBaseFolderInfo => !string.IsNullOrEmpty(BaseFolderInfoText) && string.IsNullOrEmpty(BaseFolderError);

    public bool HasBaseFolderError => !string.IsNullOrEmpty(BaseFolderError);

    partial void OnBaseFolderInfoTextChanged(string value) => OnPropertyChanged(nameof(HasBaseFolderInfo));

    partial void OnBaseFolderErrorChanged(string value)
    {
        OnPropertyChanged(nameof(HasBaseFolderError));
        OnPropertyChanged(nameof(HasBaseFolderInfo));
    }

    private SourceMetadata? _baseFolderMetadata;
    private CancellationTokenSource? _baseFolderCancellation;

    private void RefreshBaseFolder()
    {
        _baseFolderCancellation?.Cancel();
        _baseFolderCancellation?.Dispose();
        _baseFolderCancellation = null;
        _baseFolderMetadata = null;
        BaseFolderInfoText = string.Empty;
        BaseFolderError = string.Empty;
        if (!HasPatchBaseFolder)
        {
            ApplyReferenceMismatch();
            return;
        }

        var cancellation = new CancellationTokenSource();
        _baseFolderCancellation = cancellation;
        _ = ReadBaseFolderAsync(PatchBaseFolder.Trim(), cancellation.Token);
    }

    private async Task ReadBaseFolderAsync(string folder, CancellationToken cancellationToken)
    {
        try
        {
            var metadata = await Task.Run(() => Directory.Exists(folder) ? MetadataReader.Read(folder, cancellationToken) : null, cancellationToken);
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            if (metadata is not { HasParamJson: true } || string.IsNullOrWhiteSpace(metadata.ContentId))
            {
                BaseFolderError = Loc.F("Patch.BaseFolderInvalid", folder);
                return;
            }

            _baseFolderMetadata = metadata;
            BaseFolderInfoText = Loc.F("Update.BaseFolderInfo", metadata.ContentId, metadata.Version ?? "—", string.IsNullOrWhiteSpace(metadata.Title) ? "—" : metadata.Title);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                BaseFolderError = ex.Message;
            }
        }
    }

    [RelayCommand]
    private async Task BrowsePatchBaseFolderAsync()
    {
        var folder = await _dialogs.PickFolderAsync(Loc.T("Patch.PickBaseFolder"), HasPatchBaseFolder ? PatchBaseFolder.Trim() : null);
        if (folder != null)
        {
            PatchBaseFolder = folder;
        }
    }

    [RelayCommand]
    private void ClearPatchBaseFolder() => PatchBaseFolder = string.Empty;

    /// <summary>Phiên bản gợi ý (gói gốc + 1) khi ô "Phiên bản" chưa cao hơn gói gốc; null thì ẩn nút.</summary>
    private string? SuggestedPatchVersion =>
        _referenceInfo?.ContentVersion is { } baseVersion &&
        (!VersionHelper.TryCanonicalize(Version, out var current) || SonySdkPatchReference.CompareVersions(current, baseVersion) <= 0)
            ? SonySdkPatchReference.NextVersion(baseVersion)
            : null;

    public bool CanBumpVersion => PatchEnabled && SuggestedPatchVersion != null;

    public string BumpVersionText => Loc.F("Patch.Bump", SuggestedPatchVersion ?? string.Empty);

    [RelayCommand]
    private void BumpVersion()
    {
        if (SuggestedPatchVersion is { } next)
        {
            Version = next;
        }
    }

    private void RefreshBumpVersion()
    {
        OnPropertyChanged(nameof(CanBumpVersion));
        OnPropertyChanged(nameof(BumpVersionText));
    }

    private CancellationTokenSource? _referenceCancellation;
    private SonySdkReferenceInfo? _referenceInfo;

    public bool HasReferenceInfo => !string.IsNullOrEmpty(ReferenceInfoText) && string.IsNullOrEmpty(ReferenceError);

    public bool HasReferenceError => !string.IsNullOrEmpty(ReferenceError);

    public bool HasReferencePath => !string.IsNullOrWhiteSpace(ReferencePackagePath);

    /// <summary>Bản vá chỉ làm được bằng SDK Sony: bật khối chọn gói gốc khi SDK đang hoạt động.</summary>
    public bool PatchAvailable => SdkActive;

    public bool ShowPatchNeedsSdk => PatchEnabled && !SdkActive;

    /// <summary>Gói gốc dùng cho lượt tạo gói này; null = gói đầy đủ.</summary>
    private string? ActiveReferencePackage => PatchEnabled && SdkActive && HasReferencePath ? ReferencePackagePath.Trim() : null;

    /// <summary>Thẻ nguồn của thư mục không có sce_sys đổi giữa "thiếu sce_sys" và "thư mục update"; nguồn có param.json thì không đụng (giữ ô đã sửa).</summary>
    private void RefreshUpdateFolderCard()
    {
        if (_lastMetadata is { HasSceSys: false } metadata && HasSource)
        {
            ApplyMetadata(SourcePath.Trim(), metadata);
        }
    }

    partial void OnPatchEnabledChanged(bool value)
    {
        RefreshUpdateFolderCard();
        OnPropertyChanged(nameof(ShowPatchNeedsSdk));
        RefreshReference();
        RefreshValidation();
    }

    partial void OnReferencePackagePathChanged(string value)
    {
        OnPropertyChanged(nameof(HasReferencePath));
        RefreshReference();
    }

    partial void OnReferenceInfoTextChanged(string value) => OnPropertyChanged(nameof(HasReferenceInfo));

    partial void OnReferenceErrorChanged(string value)
    {
        OnPropertyChanged(nameof(HasReferenceError));
        OnPropertyChanged(nameof(HasReferenceInfo));
    }

    [RelayCommand]
    private async Task BrowseReferenceAsync()
    {
        var initial = HasReferencePath ? Path.GetDirectoryName(ReferencePackagePath) : OutputFolder;
        var file = await _dialogs.PickFileAsync(Loc.T("Patch.PickTitle"), initial, new FilePickerFileType(Loc.T("Patch.PickFilter")) { Patterns = ["*.pkg"] });
        if (file != null)
        {
            ReferencePackagePath = file;
        }
    }

    [RelayCommand]
    private void ClearReference() => ReferencePackagePath = string.Empty;

    /// <summary>Đọc gói gốc ở nền (header/CNT) và đối chiếu Content ID + contentVersion với nguồn hiện tại; kết quả hiện dưới ô.</summary>
    private void RefreshReference()
    {
        _referenceCancellation?.Cancel();
        _referenceCancellation?.Dispose();
        _referenceCancellation = null;
        _referenceInfo = null;
        ReferenceInfoText = string.Empty;
        ReferenceError = string.Empty;
        IsReadingReference = false;
        RefreshBumpVersion();
        if (!PatchEnabled || !HasReferencePath)
        {
            return;
        }

        var cancellation = new CancellationTokenSource();
        _referenceCancellation = cancellation;
        _ = ReadReferenceAsync(ReferencePackagePath.Trim(), Passcode, cancellation.Token);
    }

    private async Task ReadReferenceAsync(string path, string passcode, CancellationToken cancellationToken)
    {
        IsReadingReference = true;
        try
        {
            var info = await Task.Run(() => SonySdkPatchReference.Inspect(path, passcode, cancellationToken), cancellationToken);
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            _referenceInfo = info;
            ReferenceInfoText = Loc.F("Patch.Info", info.ContentId, info.ContentVersion ?? "—", Formatters.Size(info.Length));
            if (string.IsNullOrWhiteSpace(ContentId) && !string.IsNullOrEmpty(info.ContentId))
            {
                ContentId = info.ContentId;
            }

            AdoptReferenceDetails();

            ApplyReferenceMismatch();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                ReferenceError = ex.Message;
            }
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                IsReadingReference = false;
            }
        }
    }

    /// <summary>So gói gốc với nguồn đang chọn (Content ID, contentVersion của param.json nguồn khi là thư mục).</summary>
    private void ApplyReferenceMismatch()
    {
        RefreshBumpVersion();
        if (_referenceInfo == null)
        {
            return;
        }

        // Phiên bản của bản mới = ô "Phiên bản" (công cụ ghi nó vào contentVersion của gói, không cần sửa param.json bằng tay).
        var newVersion = VersionHelper.TryCanonicalize(Version, out var canonical) ? canonical : null;
        ReferenceError = SonySdkPatchReference.Mismatch(_referenceInfo, ContentId.Trim(), newVersion) ?? string.Empty;
    }

    /// <summary>macOS Apple Silicon thiếu Rosetta 2 (thường gặp sau khi nâng cấp macOS lớn): hiện nút cài ngay trong cảnh báo SDK.</summary>
    public bool ShowInstallRosetta => UseSonySdk && !SdkAvailable && !IsInstallingRosetta && SonySdkToolchain.RosettaMissing;

    [ObservableProperty] private bool _isInstallingRosetta;

    partial void OnIsInstallingRosettaChanged(bool value) => OnPropertyChanged(nameof(ShowInstallRosetta));

    [RelayCommand]
    private async Task InstallRosettaAsync()
    {
        if (IsInstallingRosetta)
        {
            return;
        }

        var proceed = await _dialogs.ConfirmAsync(Loc.T("Rosetta.Title"), Loc.T("Rosetta.Body"), Loc.T("Rosetta.Yes"), Loc.T("Common.Cancel"));
        if (!proceed)
        {
            return;
        }

        IsInstallingRosetta = true;
        Log(LogLevel.Info, Loc.T("Rosetta.Installing"));
        try
        {
            var installed = await Task.Run(() => SonySdkToolchain.InstallRosetta(line => Log(LogLevel.Info, "softwareupdate: " + line), TimeSpan.FromMinutes(15)));
            Log(installed ? LogLevel.Success : LogLevel.Error, Loc.T(installed ? "Rosetta.Done" : "Rosetta.Failed"));
        }
        finally
        {
            IsInstallingRosetta = false;
        }

        await RefreshComponentsAsync();
    }

    [RelayCommand]
    private async Task RefreshComponentsAsync()
    {
        if (IsProbingComponents)
        {
            return;
        }

        IsProbingComponents = true;
        try
        {
            var publishingTools = PublishingToolsPath;
            var rows = await Task.Run(() => ComponentProbe.Run(publishingTools));
            var sdkProblem = await Task.Run(() => SonySdkToolchain.Resolve(out var problem) != null ? null : problem ?? "?");
            SdkUnavailableReason = sdkProblem ?? string.Empty;
            SdkAvailable = sdkProblem == null;
            OnPropertyChanged(nameof(ShowInstallRosetta));
            Components.Clear();
            foreach (var row in rows)
            {
                Components.Add(new ComponentRow(row));
            }

            var ready = rows.Count(r => r.State == ComponentState.Ok);
            var relevant = rows.Count(r => r.State != ComponentState.NotApplicable);
            ComponentsSummary = Loc.F("Comp.Summary", ready, relevant);
        }
        catch (Exception ex)
        {
            ComponentsSummary = ex.Message;
        }
        finally
        {
            IsProbingComponents = false;
        }
    }

    /// <summary>Sao chép báo cáo thành phần (kèm phiên bản ứng dụng, hệ điều hành) để gửi khi cần hỗ trợ.</summary>
    [RelayCommand]
    private async Task CopyComponentReportAsync()
    {
        var header = $"{AppInfo.Name} {AppInfo.Version} · {AppInfo.PlatformLabel} · .NET {Environment.Version} · {Loc.F("Cli.Library", LibraryVersion)}";
        await _dialogs.SetClipboardAsync(ComponentProbe.Report(Components.Select(c => c.Status), header));
    }

    /// <summary>Mở trang tải Dokan (Windows) — driver miễn phí để gắn ảnh như trên macOS.</summary>
    [RelayCommand]
    private async Task OpenDokanDownloadAsync()
    {
        if (!await _dialogs.OpenUriAsync(DokanImageMounter.DownloadUrl))
        {
            await _dialogs.ShowInfoAsync(Loc.T("Advanced.DokanDownload"), DokanImageMounter.DownloadUrl);
        }
    }

    /// <summary>Kiểm tra lại driver Dokan sau khi cài (không cần mở lại ứng dụng).</summary>
    [RelayCommand]
    private void RecheckDokan()
    {
        NotifyMountCapabilities();
        RefreshValidation();
        RefreshDiskInfo();
        _ = RefreshComponentsAsync();
    }
    [ObservableProperty] private string _metaExFatChip = string.Empty;
    [ObservableProperty] private string _metaExFatRoot = string.Empty;
    [ObservableProperty] private bool _isGp5Source;
    [ObservableProperty] private bool _isFolderSource;
    [ObservableProperty] private string _metaGp5Chip = string.Empty;
    [ObservableProperty] private string _metaGp5Root = string.Empty;
    [ObservableProperty] private string _metaTitle = string.Empty;
    [ObservableProperty] private string _metaSubtitle = string.Empty;
    [ObservableProperty] private string _metaVersion = string.Empty;
    [ObservableProperty] private string _metaSdk = string.Empty;
    [ObservableProperty] private string _metaFiles = string.Empty;
    [ObservableProperty] private string _metaTitleId = string.Empty;
    [ObservableProperty] private string _playGoText = string.Empty;
    [ObservableProperty] private bool _hasEboot;
    [ObservableProperty] private Bitmap? _iconImage;
    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private string _diskSummary = string.Empty;
    [ObservableProperty] private bool _diskWarning;
    [ObservableProperty] private bool _hasDiskInfo;
    [ObservableProperty] private int _junkCount;
    [ObservableProperty] private bool _junkReadOnly;
    [ObservableProperty] private string _junkSummary = string.Empty;

    // ===================== Trạng thái tạo gói =====================

    [ObservableProperty] private bool _isBuilding;
    [ObservableProperty] private bool _isCanceling;
    [ObservableProperty] private double _overallPercent;
    [ObservableProperty] private double _phasePercent;
    [ObservableProperty] private string _phaseText = string.Empty;
    [ObservableProperty] private string _percentText = "0%";
    [ObservableProperty] private string _etaText = string.Empty;
    [ObservableProperty] private string _throughputText = string.Empty;
    [ObservableProperty] private string _elapsedText = string.Empty;
    /// <summary>Thư mục xuất luôn tự đặt theo nguồn (mặc định bật).</summary>
    [ObservableProperty] private bool _autoOutputFolder = true;

    [ObservableProperty] private string _statusText = string.Empty;

    /// <summary>Dòng trạng thái ở chân cửa sổ: theo chế độ đang dùng (tạo gói hoặc giải nén gói).</summary>
    public string FooterStatusText =>
        IsQueueMode && !string.IsNullOrWhiteSpace(Queue.HeadlineText) ? Queue.HeadlineText
        : IsExtractMode && !string.IsNullOrWhiteSpace(Extraction.HeadlineText) ? Extraction.HeadlineText
        : StatusText;

    partial void OnStatusTextChanged(string value) => OnPropertyChanged(nameof(FooterStatusText));

    /// <summary>Lỗi không bắt được trên luồng giao diện: ghi vào nhật ký và thanh trạng thái thay vì để ứng dụng sập.</summary>
    public void ReportUnhandledException(Exception exception)
    {
        var message = Loc.F("App.UnhandledError", exception.Message);
        Log(LogLevel.Error, message + Environment.NewLine + exception);
        StatusText = message;
    }

    /// <summary>
    /// Thư viện gọi từ luồng tạo gói khi đĩa đầy: hiện hộp thoại trên luồng giao diện và chờ — "Thử lại" sau khi người dùng
    /// giải phóng dung lượng, "Huỷ" để dừng. Luồng gọi không phải luồng UI nên chờ đồng bộ ở đây là an toàn.
    /// </summary>
    private bool AskDiskFullRetry(string path)
    {
        try
        {
            return Dispatcher.UIThread.InvokeAsync(() => _dialogs.ConfirmAsync(
                Loc.T("Build.DiskFullTitle"),
                Loc.F("Build.DiskFullBody", path),
                Loc.T("Build.DiskFullRetry"),
                Loc.T("Common.Cancel"),
                destructive: true)).GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Thư mục xuất đã có gói cùng tên: hỏi ghi đè, giữ bản cũ (đổi tên) hay huỷ. Gọi từ luồng nền nên chờ đồng bộ trên luồng UI.
    /// </summary>
    private OutputConflictChoice AskOutputConflict(IReadOnlyList<string> existing)
    {
        try
        {
            var names = string.Join("\n", existing.Select(Path.GetFileName));
            return Dispatcher.UIThread.InvokeAsync(() => _dialogs.AskOutputConflictAsync(
                Loc.T("Conflict.Title"),
                Loc.F("Conflict.Body", names),
                Loc.T("Conflict.Overwrite"),
                Loc.T("Conflict.Keep"),
                Loc.T("Common.Cancel"))).GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            // Không hỏi được thì huỷ: ghi đè im lặng là phá đúng tệp mà người dùng có thể muốn giữ.
            return OutputConflictChoice.Cancel;
        }
    }

    /// <summary>Esc: huỷ tác vụ đang chạy của chế độ hiện tại (tạo gói hoặc giải nén).</summary>
    [RelayCommand]
    private void CancelActive()
    {
        if (IsExtractMode)
        {
            if (Extraction.CancelWorkCommand.CanExecute(null))
            {
                Extraction.CancelWorkCommand.Execute(null);
            }
        }
        else if (CancelCommand.CanExecute(null))
        {
            CancelCommand.Execute(null);
        }
    }
    [ObservableProperty] private StatusKind _statusKind = StatusKind.Ready;
    [ObservableProperty] private string? _errorBanner;
    [ObservableProperty] private string? _noticeBanner;

    // ===================== Kết quả =====================

    [ObservableProperty] private bool _hasResult;
    [ObservableProperty] private string _resultPath = string.Empty;
    [ObservableProperty] private string _resultType = string.Empty;
    [ObservableProperty] private string _resultSize = string.Empty;
    [ObservableProperty] private string _resultContentId = string.Empty;
    [ObservableProperty] private string _resultSha = string.Empty;
    [ObservableProperty] private string _resultElapsed = string.Empty;
    [ObservableProperty] private string _resultRatio = string.Empty;

    // ===================== Nhật ký & giao diện =====================

    [ObservableProperty] private bool _autoScrollLog = true;
    [ObservableProperty] private bool _isDarkTheme = true;
    [ObservableProperty] private int _logCount;

    // ===================== Lỗi nhập liệu =====================

    [ObservableProperty] private string? _sourceError;
    [ObservableProperty] private string? _outputError;
    [ObservableProperty] private string? _temporaryError;
    [ObservableProperty] private string? _contentIdError;
    [ObservableProperty] private string? _passcodeError;
    [ObservableProperty] private string? _versionError;
    [ObservableProperty] private string? _threadsError;
    [ObservableProperty] private string? _playGoError;
    [ObservableProperty] private string? _sdkError;
    [ObservableProperty] private string? _publishingToolsError;
    [ObservableProperty] private string? _exFatError;
    [ObservableProperty] private bool _hasErrors;

    public bool IsStatusReady => StatusKind == StatusKind.Ready;
    public bool IsStatusWorking => StatusKind == StatusKind.Working;
    public bool IsStatusSuccess => StatusKind == StatusKind.Success;
    public bool IsStatusError => StatusKind == StatusKind.Error;
    public bool IsStatusCanceled => StatusKind == StatusKind.Canceled;
    public bool CanEdit => !IsBuilding;
    public bool HasErrorBanner => !string.IsNullOrEmpty(ErrorBanner);
    public bool HasNoticeBanner => !string.IsNullOrEmpty(NoticeBanner);
    public bool HasJunk => JunkCount > 0;
    public bool HasIcon => IconImage != null;
    public bool UsesPublishingTools => BackendIndex is 0 or 2;
    public bool CompressionEnabled => BackendIndex != 3 && !SdkActive;
    public bool CanCleanJunk => HasJunk && !IsBuilding && !JunkReadOnly;
    public bool CanOpenOutput => HasResult && !string.IsNullOrEmpty(ResultPath) && File.Exists(ResultPath);

    partial void OnStatusKindChanged(StatusKind value)
    {
        OnPropertyChanged(nameof(IsStatusReady));
        OnPropertyChanged(nameof(IsStatusWorking));
        OnPropertyChanged(nameof(IsStatusSuccess));
        OnPropertyChanged(nameof(IsStatusError));
        OnPropertyChanged(nameof(IsStatusCanceled));
    }

    partial void OnIsBuildingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(CanCleanJunk));
        OnPropertyChanged(nameof(SdkCheckBoxEnabled));
        OnPropertyChanged(nameof(CanEditPlayGoDrop));
        OnPropertyChanged(nameof(CanEditDrm));
        BuildCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
        CleanJunkCommand.NotifyCanExecuteChanged();
    }

    partial void OnErrorBannerChanged(string? value) => OnPropertyChanged(nameof(HasErrorBanner));
    partial void OnNoticeBannerChanged(string? value) => OnPropertyChanged(nameof(HasNoticeBanner));

    partial void OnIconImageChanged(Bitmap? oldValue, Bitmap? newValue)
    {
        oldValue?.Dispose();
        OnPropertyChanged(nameof(HasIcon));
    }

    partial void OnJunkCountChanged(int value)
    {
        OnPropertyChanged(nameof(HasJunk));
        OnPropertyChanged(nameof(CanCleanJunk));
        CleanJunkCommand.NotifyCanExecuteChanged();
    }

    partial void OnJunkReadOnlyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanCleanJunk));
        CleanJunkCommand.NotifyCanExecuteChanged();
    }

    partial void OnHasResultChanged(bool value)
    {
        OnPropertyChanged(nameof(CanOpenOutput));
        OpenOutputCommand.NotifyCanExecuteChanged();
        CopyResultCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsDarkThemeChanged(bool value)
    {
        App.ApplyTheme(value);
        OnPropertyChanged(nameof(ThemeTooltip));
    }

    partial void OnSourcePathChanged(string value)
    {
        _sourceDebounce.Stop();
        _sourceDebounce.Start();
        RefreshValidation();
    }

    partial void OnOutputFolderChanged(string value)
    {
        UpdateTemporaryForOutput(value);
        RefreshValidation();
        RefreshDiskInfo();
    }

    partial void OnTemporaryFolderChanged(string value)
    {
        RefreshValidation();
        RefreshDiskInfo();
    }

    partial void OnContentIdChanged(string value)
    {
        RefreshValidation();
        ApplyReferenceMismatch();
    }

    partial void OnVersionChanged(string value)
    {
        RefreshValidation();
        // Bản vá so phiên bản trong ô này (được ghi vào param.json của gói) với gói gốc.
        ApplyReferenceMismatch();
    }
    partial void OnPasscodeChanged(string value) => RefreshValidation();
    partial void OnThreadsChanged(decimal? value) => RefreshValidation();
    partial void OnPublishingToolsPathChanged(string value) => RefreshValidation();
    partial void OnOverrideSdkChanged(bool value) => RefreshValidation();

    partial void OnExFatIndexChanged(int value)
    {
        RefreshValidation();
        RefreshDiskInfo();
    }

    partial void OnSourceModeIndexChanged(int value) => RefreshValidation();

    partial void OnPlayGoChunksChanged(decimal? value)
    {
        RefreshValidation();
        if (_lastMetadata != null)
        {
            PlayGoText = DescribePlayGo(_lastMetadata);
        }
    }

    partial void OnRemovePlayGoFilesChanged(bool value)
    {
        OnPropertyChanged(nameof(RemovePlayGoFilesUi));
        if (_lastMetadata != null)
        {
            PlayGoText = DescribePlayGo(_lastMetadata);
        }
    }

    partial void OnImageModeIndexChanged(int value) => UpdateHints();

    partial void OnBackendIndexChanged(int value)
    {
        OnPropertyChanged(nameof(UsesPublishingTools));
        OnPropertyChanged(nameof(CompressionEnabled));
        UpdateHints();
        SyncPresetFromSettings();
        RefreshValidation();
    }

    partial void OnKrakenLevelChanged(double value)
    {
        UpdateHints();
        OnPropertyChanged(nameof(ShowShuffleLevelWarning));
        SyncPresetFromSettings();
    }

    partial void OnPresetFastChanged(bool value)
    {
        if (value)
        {
            ApplyPreset(BuildPresets.Fast);
        }
    }

    partial void OnPresetBalancedChanged(bool value)
    {
        if (value)
        {
            ApplyPreset(BuildPresets.Balanced);
        }
    }

    partial void OnPresetSmallestChanged(bool value)
    {
        if (value)
        {
            ApplyPreset(BuildPresets.Smallest);
        }
    }

    partial void OnPresetMaximumChanged(bool value)
    {
        if (value)
        {
            ApplyPreset(BuildPresets.Maximum);
        }
    }

    private void ApplyPreset(BuildPreset preset)
    {
        if (_syncingPreset)
        {
            return;
        }

        if (SdkActive)
        {
            // SDK Sony: preset chỉ đổi --compression_level của img_create, không đụng các tuỳ chọn engine tích hợp đang bị khoá.
            _syncingPreset = true;
            try
            {
                SdkCompressionIndex = SdkIndexFromLevel(SdkLevelForPreset(preset));
            }
            finally
            {
                _syncingPreset = false;
            }

            SyncPresetFromSdk();
            return;
        }

        _syncingPreset = true;
        try
        {
            KrakenLevel = preset.KrakenLevel;
            if (BackendIndex == 3)
            {
                BackendIndex = 0;
            }

            PfsIndex = preset.PfsFormat == PfsFormat.V3 ? 1 : 0;
            ShuffleAnalysis = preset.ShuffleAnalysis;
            // Mẫu shuffle cố định là cấu hình "tuỳ chỉnh": preset nào cũng đặt về None (với "Tối đa" thư viện tự chọn mẫu
            // qua phân tích), nếu không SyncPresetFromSettings sẽ coi là tuỳ chỉnh ngay sau khi chọn preset.
            ShuffleIndex = 0;

            PresetCustom = false;
            PresetSummary = preset.Detail;
        }
        finally
        {
            _syncingPreset = false;
        }

        UpdateHints();
    }

    private void SyncPresetFromSettings()
    {
        if (_syncingPreset)
        {
            return;
        }

        _syncingPreset = true;
        try
        {
            var level = (int)Math.Round(KrakenLevel);
            var compressing = BackendIndex != 3;
            var preset = compressing
                ? BuildPresets.Match(KrakenBackendKind.Auto, level, PfsIndex == 1 ? PfsFormat.V3 : PfsFormat.V2, ShuffleAnalysis)
                : null;
            if (preset != null && ShuffleAnalysis && ShuffleIndex != 0)
            {
                preset = null;
            }

            PresetFast = preset?.Id == BuildPresets.Fast.Id;
            PresetBalanced = preset?.Id == BuildPresets.Balanced.Id;
            PresetSmallest = preset?.Id == BuildPresets.Smallest.Id;
            PresetMaximum = preset?.Id == BuildPresets.Maximum.Id;
            PresetCustom = preset == null;
            PresetSummary = preset?.Detail ?? (compressing
                ? Loc.F("Preset.CustomLevel", level, BuildPresets.KrakenLevelName(level), BuildPresets.KrakenLevelHint(level))
                : Loc.T("Preset.CustomUncompressed"));
        }
        finally
        {
            _syncingPreset = false;
        }
    }

    private void UpdateHints()
    {
        ImageModeHint = Loc.T(ImageModeIndex == 0 ? "ImageMode.PlainHint" : "ImageMode.NativeHint");

        var level = (int)Math.Round(KrakenLevel);
        KrakenLevelText = Loc.F("Advanced.LevelText", level, BuildPresets.KrakenLevelName(level), BuildPresets.KrakenLevelHint(level));

        BackendHint = BackendIndex switch
        {
            0 => IsWindows ? Loc.T("Backend.AutoHintWindows") : Loc.F("Backend.AutoHintOther", AppInfo.PlatformLabel),
            1 => Loc.T("Backend.BuiltInHint"),
            2 => Loc.T(IsWindows ? "Backend.PubToolsHintWindows" : "Backend.PubToolsHintOther"),
            _ => Loc.T("Backend.NoneHint"),
        };
    }

    // ===================== Cấu hình =====================

    private void LoadSettings()
    {
        var s = _settings;
        _syncingPreset = true;
        _syncingLanguage = true;
        try
        {
            LanguageVi = Loc.Normalize(s.Language) == Loc.Vietnamese;
            LanguageEn = !LanguageVi;

            SourcePath = s.SourcePath;
            OutputFolder = s.OutputFolder;
            AutoOutputFolder = s.OutputFolderAuto;
            CheckUpdatesOnStartup = s.CheckUpdatesOnStartup;
            // Thư mục tạm người dùng tự chọn được giữ nguyên kể cả khi khác ổ với thư mục xuất (trước đây bị đặt lại mỗi lần mở
            // ứng dụng, nên chọn tạm ở ổ này, xuất ở ổ kia không bao giờ "ăn"). Chỉ giá trị gợi ý tự động mới đi theo thư mục xuất.
            var defaultTemporary = string.IsNullOrWhiteSpace(s.OutputFolder) ? string.Empty : BuildPreparer.SuggestTemporaryFolder(s.OutputFolder);
            var savedTemporary = s.TemporaryFolder?.Trim() ?? string.Empty;
            var savedIsSuggestion = string.Equals(savedTemporary, defaultTemporary, StringComparison.OrdinalIgnoreCase);
            if (savedTemporary.Length > 0 && !savedIsSuggestion)
            {
                TemporaryFolder = savedTemporary;
                _suggestedTemporary = null;
            }
            else
            {
                TemporaryFolder = defaultTemporary;
                _suggestedTemporary = defaultTemporary;
            }

            ContentId = s.ContentId;
            Title = s.Title;
            Version = string.IsNullOrWhiteSpace(s.Version) ? VersionHelper.Default : s.Version;
            KindIndex = s.Kind switch { PackageKind.Homebrew => 1, PackageKind.DlcWithData => 2, _ => 0 };
            ImageModeIndex = s.ImageMode == OuterImageMode.Native ? 1 : 0;
            BackendIndex = s.KrakenBackend switch
            {
                KrakenBackendKind.BuiltIn => 1,
                KrakenBackendKind.PublishingTools => 2,
                KrakenBackendKind.Uncompressed => 3,
                _ => 0,
            };
            ExFatIndex = s.ExFat switch { ExFatStrategy.Mount => 1, ExFatStrategy.Extract => 2, _ => 0 };
            SourceModeIndex = s.SourceMode == SourceMode.Folder ? 1 : 0;
            PfsIndex = s.PfsFormat == PfsFormat.V3 ? 1 : 0;
            BlockSizeIndex = Math.Max(0, Array.IndexOf(BlockSizesKiB, s.KrakenBlockKiB) is var bi && bi >= 0 ? bi : 2);
            ShuffleIndex = Math.Max(0, Array.IndexOf(ShufflePatterns, s.ShufflePattern));
            ShuffleAnalysis = s.ShuffleAnalysis;
            SkipPfsCheck = s.SkipPfsInputCheck;
            LayoutOptimization = s.LayoutOptimization;
            ForceStandardDrm = s.ForceStandardDrm;
            UseSonySdk = s.UseSonySdk;
            RemoveAmprLeftovers = s.RemoveAmprLeftovers;
            RemovePlayGoFiles = s.RemovePlayGoFiles;
            KeepDlcEmu = s.KeepDlcEmu;
            ClearVersionFileUri = s.ClearVersionFileUri;
            ClearPlayGoAttributes = s.ClearPlayGoAttributes;
            KrakenLevel = Math.Clamp(s.KrakenLevel, BuildRequest.MinKrakenLevel, BuildRequest.MaxKrakenLevel);
            Threads = Math.Clamp(s.Threads, 0, BuildRequest.MaxThreads);
            if (s.PlayGoDefaultsRevision < 1)
            {
                // Cấu hình từ trước engine 0.6.8: 64 là mặc định (và tối đa) cũ, không phải giá trị người dùng chủ động chọn.
                if (s.PlayGoChunks == 64)
                {
                    s.PlayGoChunks = BuildRequest.DefaultPlayGoChunks;
                }

                s.PlayGoDefaultsRevision = 2;
            }

            if (s.PlayGoDefaultsRevision < 2)
            {
                // 2.2.1: mặc định "xoá attribute3" đổi thành tắt — cấu hình cũ lưu "bật" là mặc định cũ, không phải lựa chọn chủ động.
                s.ClearPlayGoAttributes = false;
                ClearPlayGoAttributes = false;
                s.PlayGoDefaultsRevision = 2;
            }

            PlayGoChunks = Math.Clamp(s.PlayGoChunks, BuildRequest.MinPlayGoChunks, BuildRequest.MaxPlayGoChunks);
            Deterministic = s.Deterministic;
            ComputeSha256 = s.ComputeSha256;
            FullVerify = s.FullVerify;
            LowerRequiredFirmware = s.LowerRequiredFirmware;
            SdkPrescan = s.SdkPrescan;
            SdkKeepIntermediate = s.SdkKeepIntermediate;
            SdkPlayGoFallback = s.SdkPlayGoFallback;
            SdkCompressionIndex = SdkIndexFromLevel(s.SdkCompressionLevel);
            PreventSleep = s.PreventSleep;
            OverrideSdk = s.OverrideSdk;
            SdkIndex = Math.Clamp(s.SdkMajor - 1, 0, SdkOptions.Count - 1);
            PublishingToolsPath = IsWindows ? (PublishingToolsLocator.Find(s.PublishingToolsPath) ?? s.PublishingToolsPath) : s.PublishingToolsPath;
            AdvancedExpanded = s.AdvancedExpanded;
            AutoScrollLog = s.AutoScrollLog;
            IsDarkTheme = !string.Equals(s.Theme, "Light", StringComparison.OrdinalIgnoreCase);
            IsExtractMode = s.ExtractMode;
        }
        finally
        {
            _syncingPreset = false;
            _syncingLanguage = false;
        }

        SyncPresetFromSettings();
        ApplySdkMode();
    }

    /// <summary>
    /// Khôi phục mọi tuỳ chọn tạo gói về mặc định (preset, mức nén, PFS, khối, shuffle, DRM "standard", bỏ playgo*, dọn AMPR,
    /// số khối PlayGo, SDK, tạo gói xác định, SHA-256, chống ngủ máy, kiểm tra cập nhật, tự cuộn nhật ký). Giữ nguyên đường dẫn
    /// nguồn / xuất / tạm, thông tin gói đọc từ nguồn, ngôn ngữ, giao diện, lịch sử và kích thước cửa sổ.
    /// </summary>
    [RelayCommand]
    private async Task ResetSettingsAsync()
    {
        if (IsBuilding)
        {
            return;
        }

        var proceed = await _dialogs.ConfirmAsync(Loc.T("Reset.Title"), Loc.T("Reset.Body"), Loc.T("Reset.Confirm"), Loc.T("Common.Cancel"));
        if (!proceed)
        {
            return;
        }

        // Đồng bộ giao diện -> cấu hình trước, để LoadSettings không ghi đè đường dẫn / thông tin gói vừa nhập bằng bản cũ.
        SaveSettings();

        // SDK bám theo nguồn: lấy từ metadata nếu đọc được, nếu không thì giữ đúng giá trị đang hiển thị (khi Override tắt,
        // giá trị đó chính là SDK của game). Phải chụp TRƯỚC khi LoadSettings đưa mọi thứ về mặc định.
        var sourceSdkIndex = _lastMetadata?.SdkMajor is { } major && major >= SdkVersions.MinMajor && major <= SdkVersions.MaxMajor
            ? major - 1
            : OverrideSdk ? (int?)null : SdkIndex;
        var d = new AppSettings();
        var s = _settings;
        s.ExFat = d.ExFat;
        s.SourceMode = d.SourceMode;
        s.Kind = d.Kind;
        s.ImageMode = d.ImageMode;
        s.KrakenBackend = d.KrakenBackend;
        s.KrakenLevel = d.KrakenLevel;
        s.Threads = d.Threads;
        s.PfsFormat = d.PfsFormat;
        s.KrakenBlockKiB = d.KrakenBlockKiB;
        s.ShufflePattern = d.ShufflePattern;
        s.ShuffleAnalysis = d.ShuffleAnalysis;
        s.SkipPfsInputCheck = d.SkipPfsInputCheck;
        s.LayoutOptimization = d.LayoutOptimization;
        s.ForceStandardDrm = d.ForceStandardDrm;
        s.UseSonySdk = d.UseSonySdk;
        s.RemovePlayGoFiles = d.RemovePlayGoFiles;
        s.RemoveAmprLeftovers = d.RemoveAmprLeftovers;
        s.KeepDlcEmu = d.KeepDlcEmu;
        s.ClearVersionFileUri = d.ClearVersionFileUri;
        s.ClearPlayGoAttributes = d.ClearPlayGoAttributes;
        s.PlayGoChunks = d.PlayGoChunks;
        s.Deterministic = d.Deterministic;
        s.ComputeSha256 = d.ComputeSha256;
        s.FullVerify = d.FullVerify;
        s.LowerRequiredFirmware = d.LowerRequiredFirmware;
        s.SdkPrescan = d.SdkPrescan;
        s.SdkKeepIntermediate = d.SdkKeepIntermediate;
        s.SdkPlayGoFallback = d.SdkPlayGoFallback;
        s.SdkCompressionLevel = d.SdkCompressionLevel;
        s.PreventSleep = d.PreventSleep;
        s.OverrideSdk = d.OverrideSdk;
        s.SdkMajor = d.SdkMajor;
        s.CheckUpdatesOnStartup = d.CheckUpdatesOnStartup;
        s.AutoScrollLog = d.AutoScrollLog;
        LoadSettings();

        // Passcode không nằm trong cấu hình lưu, phải tự đưa về mặc định; dự án GP5 thì lấy lại passcode ghi trong dự án.
        Passcode = new string('0', BuildRequest.PasscodeLength);

        // SDK và passcode của dự án bám theo nguồn đang mở, không phải giá trị mặc định của ứng dụng.
        if (_lastMetadata is { } metadata)
        {
            ApplyProjectPasscode(metadata);
        }

        if (sourceSdkIndex is { } keep && keep >= 0 && keep < SdkOptions.Count)
        {
            SdkIndex = keep;
        }

        UpdateHints();
        SaveSettings();
        Log(LogLevel.Info, Loc.F("Reset.DoneSdk", SdkIndex + 1));
    }

    public void SaveSettings()
    {
        var s = _settings;
        s.SourcePath = SourcePath.Trim();
        s.OutputFolder = OutputFolder.Trim();
        s.OutputFolderAuto = AutoOutputFolder;
        s.CheckUpdatesOnStartup = CheckUpdatesOnStartup;
        s.TemporaryFolder = TemporaryFolder.Trim();
        s.ContentId = ContentId.Trim();
        s.Title = Title.Trim();
        s.Version = Version.Trim();
        s.Kind = KindFromIndex(KindIndex);
        s.ImageMode = ImageModeIndex == 1 ? OuterImageMode.Native : OuterImageMode.PlaintextNoAuth;
        s.KrakenBackend = BackendFromIndex(BackendIndex);
        s.ExFat = ExFatFromIndex(ExFatIndex);
        s.SourceMode = SourceModeFromIndex(SourceModeIndex);
        s.PfsFormat = PfsIndex == 1 ? PfsFormat.V3 : PfsFormat.V2;
        s.KrakenBlockKiB = BlockSizesKiB[Math.Clamp(BlockSizeIndex, 0, BlockSizesKiB.Length - 1)];
        s.ShufflePattern = ShufflePatterns[Math.Clamp(ShuffleIndex, 0, ShufflePatterns.Length - 1)];
        s.ShuffleAnalysis = ShuffleAnalysis;
        s.SkipPfsInputCheck = SkipPfsCheck;
        s.LayoutOptimization = LayoutOptimization;
        s.ForceStandardDrm = ForceStandardDrm;
        s.UseSonySdk = UseSonySdk;
        s.RemoveAmprLeftovers = RemoveAmprLeftovers;
        s.RemovePlayGoFiles = RemovePlayGoFiles;
        s.KeepDlcEmu = KeepDlcEmu;
        s.ClearVersionFileUri = ClearVersionFileUri;
        s.ClearPlayGoAttributes = ClearPlayGoAttributes;
        s.KrakenLevel = (int)Math.Round(KrakenLevel);
        s.Threads = (int)(Threads ?? 0);
        s.PlayGoChunks = (int)(PlayGoChunks ?? BuildRequest.DefaultPlayGoChunks);
        s.PlayGoDefaultsRevision = 1;
        s.Deterministic = Deterministic;
        s.ComputeSha256 = ComputeSha256;
        s.FullVerify = FullVerify;
        s.LowerRequiredFirmware = LowerRequiredFirmware;
        s.SdkPrescan = SdkPrescan;
        s.SdkKeepIntermediate = SdkKeepIntermediate;
        s.SdkPlayGoFallback = SdkPlayGoFallback;
        s.SdkCompressionLevel = SdkLevelFromIndex(SdkCompressionIndex);
        s.PreventSleep = PreventSleep;
        s.OverrideSdk = OverrideSdk;
        s.SdkMajor = SdkIndex + 1;
        s.PublishingToolsPath = PublishingToolsPath.Trim();
        s.AdvancedExpanded = AdvancedExpanded;
        s.AutoScrollLog = AutoScrollLog;
        s.Theme = IsDarkTheme ? "Dark" : "Light";
        s.Language = Loc.Current.Language;
        s.ExtractMode = IsExtractMode;
        Extraction.SaveSettings();
        SettingsService.Save(s);
    }

    public void RememberWindowSize(double width, double height)
    {
        if (width > 400 && height > 300)
        {
            _settings.WindowWidth = width;
            _settings.WindowHeight = height;
        }
    }

    public (double Width, double Height) SavedWindowSize => (_settings.WindowWidth, _settings.WindowHeight);

    private static PackageKind KindFromIndex(int index) => index switch
    {
        1 => PackageKind.Homebrew,
        2 => PackageKind.DlcWithData,
        _ => PackageKind.Application,
    };

    private static KrakenBackendKind BackendFromIndex(int index) => index switch
    {
        1 => KrakenBackendKind.BuiltIn,
        2 => KrakenBackendKind.PublishingTools,
        3 => KrakenBackendKind.Uncompressed,
        _ => KrakenBackendKind.Auto,
    };

    private static ExFatStrategy ExFatFromIndex(int index) => index switch
    {
        1 => ExFatStrategy.Mount,
        2 => ExFatStrategy.Extract,
        _ => ExFatStrategy.Auto,
    };

    /// <summary>Chỉ áp dụng cho nguồn thư mục; nguồn .gp5 được BuildPreparer.Normalize ép về Gp5Project.</summary>
    private static SourceMode SourceModeFromIndex(int index) => index == 1 ? SourceMode.Folder : SourceMode.Auto;

    // ===================== Chọn nguồn =====================

    [RelayCommand]
    private async Task BrowseSourceAsync()
    {
        var initial = Directory.Exists(SourcePath.Trim()) ? SourcePath.Trim() : Path.GetDirectoryName(SourcePath.Trim());
        var folder = await _dialogs.PickFolderAsync(Loc.T("Pick.Source"), initial);
        if (folder != null)
        {
            SetSource(folder);
        }
    }

    /// <summary>Nút "Tệp .ffpfsc": chọn container PFS chứa ảnh exFAT nén.</summary>
    [RelayCommand]
    private async Task BrowseFfpfscAsync()
    {
        var current = SourcePath.Trim();
        var initial = File.Exists(current) ? Path.GetDirectoryName(current) : Directory.Exists(current) ? current : null;
        var file = await _dialogs.PickFileAsync(
            Loc.T("Pick.Ffpfsc"),
            initial,
            new FilePickerFileType(Loc.T("Pick.FfpfscFilter")) { Patterns = ["*.ffpfsc"] },
            new FilePickerFileType(Loc.T("Pick.AllFiles")) { Patterns = ["*"] });
        if (file != null)
        {
            SetSource(file);
        }
    }

    /// <summary>Nút "Tệp .ffpkg": chọn ảnh UFS2 (hệ tệp FreeBSD, thư mục gốc chính là thư mục ứng dụng).</summary>
    [RelayCommand]
    private async Task BrowseFfpkgAsync()
    {
        var current = SourcePath.Trim();
        var initial = File.Exists(current) ? Path.GetDirectoryName(current) : Directory.Exists(current) ? current : null;
        var file = await _dialogs.PickFileAsync(
            Loc.T("Pick.Ffpkg"),
            initial,
            new FilePickerFileType(Loc.T("Pick.FfpkgFilter")) { Patterns = ["*.ffpkg"] },
            new FilePickerFileType(Loc.T("Pick.AllFiles")) { Patterns = ["*"] });
        if (file != null)
        {
            SetSource(file);
        }
    }

    [RelayCommand]
    private async Task BrowseExFatAsync()
    {
        var current = SourcePath.Trim();
        var initial = File.Exists(current) ? Path.GetDirectoryName(current) : Directory.Exists(current) ? current : null;
        var file = await _dialogs.PickFileAsync(
            Loc.T("Pick.ExFat"),
            initial,
            new FilePickerFileType(Loc.T("Pick.ExFatFilter")) { Patterns = ["*.exfat"] },
            new FilePickerFileType(Loc.T("Pick.AllFiles")) { Patterns = ["*"] });
        if (file != null)
        {
            SetSource(file);
        }
    }

    [RelayCommand]
    private async Task BrowseGp5Async()
    {
        var current = SourcePath.Trim();
        var initial = File.Exists(current) ? Path.GetDirectoryName(current) : Directory.Exists(current) ? current : null;
        var file = await _dialogs.PickFileAsync(
            Loc.T("Pick.Gp5"),
            initial,
            new FilePickerFileType(Loc.T("Pick.Gp5Filter")) { Patterns = ["*.gp5"] },
            new FilePickerFileType(Loc.T("Pick.AllFiles")) { Patterns = ["*"] });
        if (file != null)
        {
            SetSource(file);
        }
    }

    public void SetSource(string path)
    {
        SourcePath = path;
        ApplyOutputSuggestion(path);
        _sourceDebounce.Stop();
        ReloadMetadata();
    }

    /// <summary>
    /// Khi "Tự đặt theo nguồn" đang bật, thư mục xuất luôn là "&lt;nguồn&gt;-pkg" cạnh nguồn hiện tại
    /// (đổi nguồn là đổi theo); tắt đi thì người dùng tự chọn và ứng dụng không đụng vào nữa.
    /// </summary>
    private void ApplyOutputSuggestion(string source)
    {
        if (!AutoOutputFolder || SourceLocator.Detect(source) == SourceKind.None)
        {
            return;
        }

        try
        {
            OutputFolder = BuildPreparer.SuggestOutputFolder(source);
        }
        catch (Exception)
        {
        }
    }

    /// <summary>Ô thư mục xuất chỉ sửa tay được khi tắt "Tự đặt theo nguồn".</summary>
    public bool CanEditOutput => !AutoOutputFolder;

    partial void OnAutoOutputFolderChanged(bool value)
    {
        OnPropertyChanged(nameof(CanEditOutput));
        if (value)
        {
            ApplyOutputSuggestion(SourcePath.Trim());
        }
    }

    [RelayCommand]
    private void SelectRecent(string? path)
    {
        if (!string.IsNullOrWhiteSpace(path) && (Directory.Exists(path) || File.Exists(path)))
        {
            SetSource(path);
        }
    }

    [RelayCommand]
    private async Task BrowseOutputAsync()
    {
        var folder = await _dialogs.PickFolderAsync(Loc.T("Pick.Output"), OutputFolder.Trim());
        if (folder != null)
        {
            OutputFolder = folder;
        }
    }

    [RelayCommand]
    private async Task BrowseTemporaryAsync()
    {
        var folder = await _dialogs.PickFolderAsync(Loc.T("Pick.Temp"), TemporaryFolder.Trim());
        if (folder != null)
        {
            TemporaryFolder = folder;
            _suggestedTemporary = null;
        }
    }

    [RelayCommand]
    private async Task BrowsePublishingToolsAsync()
    {
        var initial = File.Exists(PublishingToolsPath) ? Path.GetDirectoryName(PublishingToolsPath) : null;
        var file = await _dialogs.PickFileAsync(
            Loc.T("Pick.Dll"),
            initial,
            new FilePickerFileType("libScePubTools.dll") { Patterns = ["libScePubTools.dll", "*.dll"] });
        if (file != null)
        {
            PublishingToolsPath = file;
        }
    }

    [RelayCommand]
    private void UseSuggestedContentId() => ContentId = ContentIdHelper.Suggest(MetaTitleId, Title);

    [RelayCommand]
    private void ResetTemporary()
    {
        if (!string.IsNullOrWhiteSpace(OutputFolder))
        {
            var suggestion = BuildPreparer.SuggestTemporaryFolder(OutputFolder);
            TemporaryFolder = suggestion;
            _suggestedTemporary = suggestion;
        }
    }

    private void UpdateTemporaryForOutput(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return;
        }

        var current = TemporaryFolder.Trim();
        var untouched = string.IsNullOrWhiteSpace(current) ||
                        string.Equals(current, _suggestedTemporary, StringComparison.OrdinalIgnoreCase);
        if (!untouched)
        {
            return;
        }

        try
        {
            var suggestion = BuildPreparer.SuggestTemporaryFolder(output);
            _suggestedTemporary = suggestion;
            TemporaryFolder = suggestion;
        }
        catch (Exception)
        {
        }
    }

    // ===================== Metadata =====================

    [RelayCommand]
    private void ReloadMetadata()
    {
        _metadataCancellation?.Cancel();
        _metadataCancellation?.Dispose();
        _metadataCancellation = null;

        var source = SourcePath.Trim();
        if (SourceLocator.Detect(source) == SourceKind.None)
        {
            _lastMetadata = null;
            _lastStats = null;
            ShowEmptyMetadata();
            return;
        }

        // Gõ tay hoặc dán đường dẫn nguồn hợp lệ cũng tự đặt thư mục xuất như khi chọn bằng nút.
        ApplyOutputSuggestion(source);

        var cancellation = new CancellationTokenSource();
        _metadataCancellation = cancellation;
        _ = LoadMetadataAsync(source, cancellation.Token);
    }

    private async Task LoadMetadataAsync(string source, CancellationToken cancellationToken)
    {
        IsScanning = true;
        try
        {
            var metadata = await Task.Run(() => MetadataReader.Read(source, cancellationToken), cancellationToken);
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            _lastMetadata = metadata;
            _lastStats = null;
            ApplyMetadata(source, metadata);
            ApplyProjectPasscode(metadata);

            if (metadata.HasParamJson && !string.Equals(_lastMetadataSource, source, StringComparison.OrdinalIgnoreCase))
            {
                _lastMetadataSource = source;
                Log(LogLevel.Info, Loc.F("Meta.ReadParam", metadata.ParamJsonPath));
            }

            var iconTask = Task.Run(() => LoadBitmap(metadata), cancellationToken);
            var statsTask = Task.Run(() => FolderScanner.Scan(source, cancellationToken), cancellationToken);
            var junkTask = Task.Run(() => JunkFileFinder.Find(source, cancellationToken), cancellationToken);

            IconImage = await iconTask;
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            var stats = await statsTask;
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            _lastStats = stats;
            _sourceBytes = stats.TotalBytes;
            MetaFiles = Loc.F("Meta.Files", Formatters.Count(stats.FileCount), Formatters.Size(stats.TotalBytes));
            RememberRecent(source);

            _junkFiles = await junkTask;
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            JunkReadOnly = _junkFiles.Any(j => j.IsReadOnly);
            JunkCount = _junkFiles.Count;
            RefreshJunkSummary();
            RefreshDiskInfo();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _lastMetadata = null;
            MetaTitle = Loc.T("Meta.ReadFailed");
            MetaSubtitle = ex.Message;
            MetadataWarning = true;
            HasSource = true;
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                IsScanning = false;
            }
        }
    }

    private static Bitmap? LoadBitmap(SourceMetadata metadata) => IconLoader.Load(metadata);

    /// <summary>Dự án GP5 mang passcode 32 ký tự ASCII: điền vào trường Passcode ngay khi vừa đọc metadata (không đụng tới khi chỉ đổi ngôn ngữ).</summary>
    private void ApplyProjectPasscode(SourceMetadata metadata)
    {
        if (metadata.IsGp5 && metadata.Gp5Passcode is { Length: BuildRequest.PasscodeLength } passcode &&
            passcode.All(c => c < 128 && !char.IsControl(c)))
        {
            Passcode = passcode;
        }
    }

    private void ShowEmptyMetadata()
    {
        HasSource = false;
        HasParamJson = false;
        MetadataWarning = false;
        IsExFatSource = false;
        IsUfsSource = false;
        IsPfsContainerSource = false;
        MetaExFatChip = string.Empty;
        MetaExFatRoot = string.Empty;
        IsGp5Source = false;
        IsFolderSource = false;
        MetaGp5Chip = string.Empty;
        MetaGp5Root = string.Empty;
        MetaTitle = Loc.T("Meta.NoSource");
        MetaSubtitle = Loc.T("Meta.NoSourceHint");
        MetaVersion = string.Empty;
        MetaSdk = string.Empty;
        MetaFiles = string.Empty;
        MetaTitleId = string.Empty;
        PlayGoText = string.Empty;
        Passcode = new string('0', BuildRequest.PasscodeLength);
        HasDlcEmuWarning = false;
        CanBuildDlc = false;
        HasEboot = false;
        IconImage = null;
        IsScanning = false;
        HasDiskInfo = false;
        DiskWarning = false;
        DiskSummary = string.Empty;
        JunkCount = 0;
        JunkReadOnly = false;
        JunkSummary = string.Empty;
        _junkFiles = Array.Empty<JunkFile>();
        _sourceBytes = 0;
    }

    private void ApplyMetadata(string source, SourceMetadata metadata)
    {
        HasSource = true;
        HasEboot = metadata.HasEboot;
        ApplyAmprInfo(metadata.Ampr);
        ApplyDlcEmuInfo(metadata.DlcEmu);
        IsExFatSource = metadata.IsExFat;
        IsUfsSource = metadata.IsUfs;
        IsPfsContainerSource = metadata.IsPfsContainer;
        var volumeLabel = string.IsNullOrWhiteSpace(metadata.VolumeLabel) ? "—" : metadata.VolumeLabel;
        MetaExFatChip = metadata.IsPfsContainer
            ? Loc.F("Meta.PfsChip", volumeLabel, metadata.ContainerStoredLength is { } stored ? Formatters.Size(stored) : "—")
            : metadata.IsExFat ? Loc.F("Meta.ExFatChip", volumeLabel) : string.Empty;
        MetaExFatRoot = metadata.IsExFat
            ? (string.IsNullOrEmpty(metadata.AppRootInImage) ? Loc.T("Meta.ExFatRootTop") : Loc.F("Meta.ExFatRoot", metadata.AppRootInImage))
            : string.Empty;
        IsGp5Source = metadata.IsGp5;
        IsFolderSource = !metadata.IsImage && !metadata.IsGp5;
        MetaGp5Chip = metadata.IsGp5 ? Loc.F("Meta.Gp5Chip", metadata.Gp5Layout ?? "—") : string.Empty;
        MetaGp5Root = metadata.IsGp5 ? Loc.F("Meta.Gp5Root", metadata.Gp5RootFolder ?? "—") : string.Empty;
        MetaTitleId = metadata.TitleId ?? string.Empty;
        if (_lastStats == null)
        {
            MetaFiles = Loc.T("Meta.Scanning");
        }

        PlayGoText = DescribePlayGo(metadata);

        if (!metadata.HasSceSys)
        {
            HasParamJson = false;
            // Thư mục update của bản vá không cần sce_sys: thông tin game lấy từ gói gốc.
            var updateFolder = PatchEnabled && !metadata.IsImage && !metadata.IsGp5;
            MetadataWarning = !updateFolder;
            MetaTitle = Loc.T(updateFolder ? "Meta.UpdateFolder" : metadata.IsExFat ? "Val.ExFatNoApp" : metadata.IsGp5 ? "Val.Gp5NoParam" : "Meta.NoSceSys");
            MetaSubtitle = Loc.T(updateFolder ? "Meta.UpdateFolderHint" : "Meta.NoSceSysHint");
            MetaVersion = string.Empty;
            MetaSdk = string.Empty;
            return;
        }

        if (!metadata.HasParamJson)
        {
            HasParamJson = false;
            MetadataWarning = true;
            MetaTitle = Loc.T("Meta.NoParam");
            MetaSubtitle = metadata.ParamJsonError ?? Loc.T("Meta.NoParamHint");
            MetaVersion = string.Empty;
            MetaSdk = string.Empty;
            if (string.IsNullOrWhiteSpace(ContentId))
            {
                ContentId = ContentIdHelper.Suggest(null, Title.Length > 0 ? Title : Path.GetFileNameWithoutExtension(source));
            }

            return;
        }

        HasParamJson = true;
        MetadataWarning = false;
        MetaTitle = string.IsNullOrWhiteSpace(metadata.Title) ? Loc.T("Meta.NoTitle") : metadata.Title;
        MetaSubtitle = string.IsNullOrWhiteSpace(metadata.ContentId) ? Loc.T("Meta.NoContentId") : metadata.ContentId;
        MetaVersion = Loc.F("Meta.Version", string.IsNullOrWhiteSpace(metadata.Version) ? "—" : metadata.Version);
        MetaSdk = Loc.F("Meta.Sdk", metadata.SdkMajor?.ToString() ?? "—");

        if (!string.IsNullOrWhiteSpace(metadata.ContentId))
        {
            ContentId = metadata.ContentId;
        }

        if (VersionHelper.TryCanonicalize(metadata.Version, out var canonical))
        {
            Version = canonical;
        }

        if (!string.IsNullOrWhiteSpace(metadata.Title))
        {
            Title = metadata.Title;
        }

        if (!OverrideSdk && metadata.SdkMajor is { } sdk and >= SdkVersions.MinMajor and <= SdkVersions.MaxMajor)
        {
            SdkIndex = sdk - 1;
        }
    }

    private void RefreshJunkSummary()
    {
        JunkSummary = JunkCount == 0
            ? string.Empty
            : Loc.F(JunkReadOnly ? "Junk.SummaryReadOnly" : "Junk.Summary", JunkCount);
    }

    /// <summary>Ghi vào nhật ký dấu vết AMPR emu trong nguồn (tàn dư sẽ được bỏ khi tạo gói).</summary>
    private void ApplyAmprInfo(AmprInfo ampr)
    {
        if (ampr.Relevant)
        {
            Log(LogLevel.Info, Loc.F("Cli.Ampr", AmprInspector.Describe(ampr)));
        }
    }

    /// <summary>Báo "phát hiện DLC" và bật nút tạo gói DLC khi đọc được dlc_emu.ini (bộ giả lập vẫn được giữ trong gói theo mặc định).</summary>
    private void ApplyDlcEmuInfo(DlcEmuInfo info)
    {
        HasDlcEmuWarning = info.Present;
        CanBuildDlc = false;
        DlcBuildLabel = string.Empty;
        _dlcEntries = Array.Empty<DlcEmuEntry>();
        if (!info.Present)
        {
            return;
        }

        Log(LogLevel.Info, Loc.F("Cli.DlcEmu", DlcEmuInspector.Describe(info)));
        var source = SourcePath.Trim();
        _ = Task.Run(() => DlcEmuIni.Read(source, CancellationToken.None)).ContinueWith(task =>
        {
            if (!task.IsCompletedSuccessfully || !string.Equals(SourcePath.Trim(), source, StringComparison.Ordinal))
            {
                return;
            }

            _dlcEntries = task.Result;
            CanBuildDlc = _dlcEntries.Count > 0;
            DlcBuildLabel = Loc.F("Dlc.BuildButton", _dlcEntries.Count);
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private IReadOnlyList<DlcEmuEntry> _dlcEntries = Array.Empty<DlcEmuEntry>();

    /// <summary>Tạo một gói DLC riêng cho từng mục trong dlc_emu.ini của game, đặt cạnh gói game.</summary>
    [RelayCommand]
    private async Task BuildDlcPackagesAsync()
    {
        if (IsBuildingDlc || _dlcEntries.Count == 0)
        {
            return;
        }

        var output = OutputFolder.Trim();
        if (string.IsNullOrWhiteSpace(output))
        {
            await _dialogs.ShowErrorAsync(Loc.T("Dlc.NoneTitle"), Loc.T("Val.OutputRequired"));
            return;
        }

        IsBuildingDlc = true;
        try
        {
            var temporary = string.IsNullOrWhiteSpace(TemporaryFolder) ? BuildPreparer.SuggestTemporaryFolder(output) : TemporaryFolder.Trim();
            var entries = _dlcEntries;
            var title = Title;
            var dlcSource = Directory.Exists(SourcePath.Trim()) ? SourcePath.Trim() : null;
            var results = await Task.Run(() => DlcPackageBuilder.BuildAll(entries, output, temporary, title, e => Log(e.Level, e.Message), CancellationToken.None, AskOutputConflict, dlcSource));
            var ok = results.Count(r => r.Success);
            await _dialogs.ShowInfoAsync(Loc.T("Dlc.DoneTitle"), Loc.F("Dlc.DoneBody", ok, results.Count, output));
        }
        catch (Exception ex)
        {
            await _dialogs.ShowErrorAsync(Loc.T("Dlc.NoneTitle"), ex.Message);
        }
        finally
        {
            IsBuildingDlc = false;
        }
    }

    private bool WillExtractExFat =>
        IsUfsSource
        || (IsExFatSource && (ExFatIndex == 2 || !CanMountCurrentSource || (ExFatIndex == 0 && JunkCount > 0 && !ImageMounter.CanHideJunk)));

    private void RefreshDiskInfo()
    {
        if (!HasSource || _sourceBytes <= 0 || string.IsNullOrWhiteSpace(OutputFolder))
        {
            HasDiskInfo = false;
            DiskWarning = false;
            DiskSummary = string.Empty;
            return;
        }

        var output = OutputFolder.Trim();
        var temporary = string.IsNullOrWhiteSpace(TemporaryFolder) ? BuildPreparer.SuggestTemporaryFolder(output) : TemporaryFolder.Trim();
        var bytes = _sourceBytes;
        var staging = WillExtractExFat ? _sourceBytes : 0;
        var sonySdk = SdkActive;
        _ = Task.Run(() => DiskSpaceAdvisor.Check(output, temporary, bytes, staging, sonySdk)).ContinueWith(task =>
        {
            if (task.IsCompletedSuccessfully)
            {
                var report = task.Result;
                HasDiskInfo = true;
                DiskWarning = !report.Sufficient;
                DiskSummary = report.Summary;
            }
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>Xoá danh sách nguồn dùng gần đây (chỉ quên đường dẫn, không đụng vào tệp).</summary>
    [RelayCommand]
    private void ClearRecent()
    {
        _settings.RecentSources.Clear();
        OnPropertyChanged(nameof(RecentSources));
        OnPropertyChanged(nameof(HasRecent));
    }

    private void RememberRecent(string source)
    {
        var list = _settings.RecentSources;
        list.RemoveAll(item => string.Equals(item, source, StringComparison.OrdinalIgnoreCase));
        list.Insert(0, source);
        while (list.Count > MaxRecentSources)
        {
            list.RemoveAt(list.Count - 1);
        }

        OnPropertyChanged(nameof(RecentSources));
        OnPropertyChanged(nameof(HasRecent));
    }

    [RelayCommand(CanExecute = nameof(CanCleanJunk))]
    private async Task CleanJunkAsync()
    {
        if (_junkFiles.Count == 0 || JunkReadOnly)
        {
            return;
        }

        var root = SourcePath.Trim();
        var preview = string.Join("\n", _junkFiles.Take(8).Select(j => "• " + Path.GetRelativePath(root, j.Path)));
        if (_junkFiles.Count > 8)
        {
            preview += "\n" + Loc.F("Junk.More", _junkFiles.Count - 8);
        }

        var confirmed = await _dialogs.ConfirmAsync(
            Loc.T("Junk.ConfirmTitle"),
            Loc.F("Junk.ConfirmBody", _junkFiles.Count, preview),
            Loc.T("Junk.Delete"),
            Loc.T("Common.Cancel"),
            destructive: true);
        if (!confirmed)
        {
            return;
        }

        var files = _junkFiles;
        var (deleted, errors) = await Task.Run(() => JunkFileFinder.Delete(files));
        Log(deleted > 0 ? LogLevel.Success : LogLevel.Warning, Loc.F("Junk.Result", deleted, files.Count));
        foreach (var error in errors)
        {
            Log(LogLevel.Warning, Loc.F("Junk.DeleteFailed", error));
        }

        ReloadMetadata();
    }

    // ===================== Kiểm tra dữ liệu nhập =====================

    private BuildRequest CreateRequest() => new()
    {
        SourcePath = SourcePath.Trim(),
        OutputFolder = OutputFolder.Trim(),
        TemporaryFolder = TemporaryFolder.Trim(),
        ContentId = ContentId.Trim(),
        Title = Title.Trim(),
        Version = Version.Trim(),
        Passcode = Passcode,
        Kind = KindFromIndex(KindIndex),
        ImageMode = ImageModeIndex == 1 ? OuterImageMode.Native : OuterImageMode.PlaintextNoAuth,
        KrakenBackend = BackendFromIndex(BackendIndex),
        ExFat = ExFatFromIndex(ExFatIndex),
        SourceMode = SourceModeFromIndex(SourceModeIndex),
        PfsFormat = PfsIndex == 1 ? PfsFormat.V3 : PfsFormat.V2,
        KrakenBlockKiB = BlockSizesKiB[Math.Clamp(BlockSizeIndex, 0, BlockSizesKiB.Length - 1)],
        ShufflePattern = ShufflePatterns[Math.Clamp(ShuffleIndex, 0, ShufflePatterns.Length - 1)],
        ShuffleAnalysis = ShuffleAnalysis,
        SkipPfsInputCheck = SkipPfsCheck,
        LayoutOptimization = LayoutOptimization,
        ForceStandardDrm = ForceStandardDrm,
        UseSonySdk = UseSonySdk,
        SdkPrescan = SdkPrescan,
        SdkKeepIntermediate = SdkKeepIntermediate,
        SdkReferencePackage = ActiveReferencePackage,
        SdkPatchBaseFolder = ActiveReferencePackage != null && HasPatchBaseFolder ? PatchBaseFolder.Trim() : null,
        SdkPatchOutput = UpdateKind switch { 1 => SdkPatchOutput.FullOnly, 2 => SdkPatchOutput.Both, _ => SdkPatchOutput.UpdateOnly },
        SdkApplyPackageDetails = true,
        SdkPlayGoFallback = SdkPlayGoFallback,
        SdkCompressionLevel = SdkLevelFromIndex(SdkCompressionIndex),
        RemoveAmprLeftovers = RemoveAmprLeftovers,
        RemovePlayGoFiles = RemovePlayGoFiles,
        KeepDlcEmu = KeepDlcEmu,
        ClearVersionFileUri = ClearVersionFileUri,
        ClearPlayGoAttributes = ClearPlayGoAttributes,
        KrakenLevel = (int)Math.Round(KrakenLevel),
        Threads = (int)(Threads ?? 0),
        PlayGoChunks = (int)(PlayGoChunks ?? BuildRequest.DefaultPlayGoChunks),
        Deterministic = Deterministic,
        ComputeSha256 = ComputeSha256,
        FullVerify = FullVerify,
        LowerRequiredFirmware = LowerRequiredFirmware,
        SdkMajorOverride = OverrideSdk ? SdkIndex + 1 : null,
        PublishingToolsPath = string.IsNullOrWhiteSpace(PublishingToolsPath) ? null : PublishingToolsPath.Trim(),
        PreventSleep = PreventSleep,
    };

    private IReadOnlyList<ValidationError> RefreshValidation()
    {
        IReadOnlyList<ValidationError> errors;
        try
        {
            errors = BuildPreparer.Validate(CreateRequest());
        }
        catch (Exception ex)
        {
            errors = [new ValidationError(BuildPreparer.FieldSource, ex.Message)];
        }

        string? Pick(string field, bool hideWhenEmpty, string value)
        {
            var error = errors.FirstOrDefault(e => e.Field == field)?.Message;
            if (error != null && hideWhenEmpty && !_attemptedBuild && string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            return error;
        }

        SourceError = Pick(BuildPreparer.FieldSource, true, SourcePath);
        OutputError = Pick(BuildPreparer.FieldOutput, true, OutputFolder);
        TemporaryError = Pick(BuildPreparer.FieldTemporary, true, TemporaryFolder);
        ContentIdError = Pick(BuildPreparer.FieldContentId, true, ContentId);
        PasscodeError = Pick(BuildPreparer.FieldPasscode, false, Passcode);
        VersionError = Pick(BuildPreparer.FieldVersion, false, Version);
        ThreadsError = Pick(BuildPreparer.FieldThreads, false, "x");
        PlayGoError = Pick(BuildPreparer.FieldPlayGo, false, "x");
        SdkError = Pick(BuildPreparer.FieldSdk, false, "x");
        PublishingToolsError = Pick(BuildPreparer.FieldPublishingTools, false, "x");
        ExFatError = Pick(BuildPreparer.FieldExFat, false, "x");
        HasErrors = errors.Count > 0;
        return errors;
    }

    // ===================== Tạo gói =====================

    private bool CanBuild => !IsBuilding && !IsExtractMode;

    [RelayCommand(CanExecute = nameof(CanBuild))]
    private async Task BuildAsync()
    {
        if (IsBuilding)
        {
            return;
        }

        _attemptedBuild = true;
        var errors = RefreshValidation();
        if (errors.Count > 0)
        {
            var first = errors[0];
            SetStatus(StatusKind.Error, "Status.Invalid");
            ErrorBanner = first.Message;
            Log(LogLevel.Warning, first.Message);
            FocusFieldRequested?.Invoke(this, first.Field);
            return;
        }

        if (!KeysAvailable)
        {
            ErrorBanner = Loc.T("Build.NoKeysBanner");
            SetStatus(StatusKind.Error, "Status.NoKeys");
            return;
        }

        // Bản vá: lỗi của ô gói gốc chặn lượt tạo gói ngay tại đây (SDK chỉ báo sau khi nén xong).
        if (PatchEnabled)
        {
            ApplyReferenceMismatch();
            var patchProblem = !SdkActive ? Loc.T("Patch.NeedsSdk") : !HasReferencePath ? Loc.T("Patch.NeedsFile") : HasReferenceError ? ReferenceError : HasPatchBaseFolder && HasBaseFolderError ? BaseFolderError : null;
            if (patchProblem != null)
            {
                SetStatus(StatusKind.Error, "Status.Invalid");
                ErrorBanner = patchProblem;
                Log(LogLevel.Warning, patchProblem);
                return;
            }
        }

        if (DiskWarning)
        {
            var proceed = await _dialogs.ConfirmAsync(
                Loc.T("Build.DiskTitle"),
                Loc.F("Build.DiskBody", DiskSummary),
                Loc.T("Build.DiskYes"),
                Loc.T("Common.Cancel"));
            if (!proceed)
            {
                return;
            }
        }

        if (JunkCount > 0 && !JunkReadOnly)
        {
            DebugLog.Write($"Build: asking about {JunkCount} junk files");
            var clean = await _dialogs.ConfirmAsync(
                Loc.T("Junk.BuildTitle"),
                Loc.F("Junk.BuildBody", JunkSummary, JunkCount),
                Loc.T("Junk.BuildYes"),
                Loc.T("Junk.BuildNo"));
            if (clean)
            {
                var files = _junkFiles;
                var (deleted, deleteErrors) = await Task.Run(() => JunkFileFinder.Delete(files));
                Log(LogLevel.Info, Loc.F("Junk.Result", deleted, files.Count));
                foreach (var error in deleteErrors)
                {
                    Log(LogLevel.Warning, Loc.F("Junk.DeleteFailed", error));
                }

                JunkCount = 0;
                JunkSummary = string.Empty;
                _junkFiles = Array.Empty<JunkFile>();
            }
        }

        DebugLog.Write("Build: starting engine");
        var request = CreateRequest();

        ErrorBanner = null;
        NoticeBanner = null;
        ClearResult();
        LogEntries.Clear();
        LogCount = 0;
        _pendingLogs.Clear();
        Interlocked.Exchange(ref _pendingProgress, null);
        _lastProgress = null;

        IsBuilding = true;
        IsCanceling = false;
        FocusFieldRequested?.Invoke(this, FocusCancelButton);
        _buildCancellation = new CancellationTokenSource();
        var token = _buildCancellation.Token;

        SetStatus(StatusKind.Working, "Status.Building");
        ThroughputText = string.Empty;
        _phaseKey = "Phase.Preparing";
        PhaseText = Loc.T(_phaseKey) + "…";
        PercentText = "0%";
        OverallPercent = 0;
        PhasePercent = 0;
        EtaText = string.Empty;
        _buildStopwatch.Restart();
        ElapsedText = "00:00";
        _tickTimer.Start();
        SaveSettings();

        try
        {
            var outcome = await _engine.BuildAsync(
                request,
                entry => _pendingLogs.Enqueue(entry),
                new Progress<BuildProgress>(snapshot => Interlocked.Exchange(ref _pendingProgress, snapshot)),
                token,
                AskDiskFullRetry,
                AskOutputConflict);

            Drain();
            foreach (var warning in outcome.Warnings)
            {
                Log(LogLevel.Warning, Loc.F("Build.Warning", warning));
            }

            ShowResult(outcome);
            Log(LogLevel.Info, Loc.F("Build.Container", outcome.Verification.ContainerLabel));
            Log(LogLevel.Info, Loc.F("Build.Size", Formatters.SizeWithBytes(outcome.Verification.Length)));
            Log(LogLevel.Info, Loc.F("Build.Fih", outcome.Verification.SignedByte.ToString("X2"), outcome.Verification.OuterMode.ToString("X4")));
            if (outcome.Verification.SeedMarker != null)
            {
                Log(LogLevel.Info, Loc.F("Build.Marker", outcome.Verification.SeedMarker));
            }

            if (outcome.Verification.Sha256 != null)
            {
                Log(LogLevel.Info, Loc.F("Build.Sha", outcome.Verification.Sha256));
            }

            Log(LogLevel.Success, Loc.F("Build.Success", Formatters.Duration(outcome.Elapsed), outcome.OutputPath));
            SetStatus(StatusKind.Success, "Status.Success");
            _phaseKey = "Phase.Done";
            PhaseText = Loc.T(_phaseKey);
            PercentText = "100%";
            OverallPercent = 100;
            PhasePercent = 100;
            EtaText = string.Empty;
            if (outcome.Warnings.Count > 0)
            {
                NoticeBanner = Loc.F("Build.WarningsNotice", outcome.Warnings.Count);
            }
        }
        catch (OperationCanceledException)
        {
            Drain();
            Log(LogLevel.Warning, Loc.T("Build.UserCanceled"));
            SetStatus(StatusKind.Canceled, "Status.Canceled");
            _phaseKey = "Phase.Canceled";
            PhaseText = Loc.T(_phaseKey);
            EtaText = string.Empty;
        }
        catch (BuildValidationException ex)
        {
            Drain();
            ErrorBanner = ex.Message;
            SetStatus(StatusKind.Error, "Status.Invalid");
            _phaseKey = "Phase.Stopped";
            PhaseText = Loc.T(_phaseKey);
            RefreshValidation();
        }
        catch (Exception ex)
        {
            Drain();
            Log(LogLevel.Error, Loc.F("Build.Error", ex.Message));
            if (!string.IsNullOrWhiteSpace(ex.StackTrace))
            {
                Log(LogLevel.Error, ex.StackTrace!);
            }

            SetStatus(StatusKind.Error, "Status.Failed");
            ErrorBanner = ex.Message;
            _phaseKey = "Phase.Stopped";
            PhaseText = Loc.T(_phaseKey);
            EtaText = string.Empty;
        }
        finally
        {
            _buildStopwatch.Stop();
            _tickTimer.Stop();
            _buildCancellation?.Dispose();
            _buildCancellation = null;
            IsBuilding = false;
            IsCanceling = false;
            ElapsedText = Formatters.Clock(_buildStopwatch.Elapsed);
            Drain();
        }
    }

    private bool CanCancel => IsBuilding && !IsCanceling;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        if (_buildCancellation == null || _buildCancellation.IsCancellationRequested)
        {
            return;
        }

        IsCanceling = true;
        CancelCommand.NotifyCanExecuteChanged();
        SetStatus(StatusKind.Working, "Status.Canceling");
        Log(LogLevel.Warning, Loc.T("Build.CancelRequested"));
        _buildCancellation.Cancel();
    }

    /// <summary>Gọi khi cửa sổ đóng; trả về true nếu được phép đóng.</summary>
    public async Task<bool> ConfirmCloseAsync()
    {
        if (!IsBuilding && !Queue.IsRunning)
        {
            _metadataCancellation?.Cancel();
            SaveSettings();
            Extraction.Shutdown();
            Queue.Shutdown();
            return true;
        }

        var close = await _dialogs.ConfirmAsync(
            Loc.T("Close.Title"),
            Queue.IsRunning ? Loc.F("Queue.CloseBody", Queue.RunningCount) : Loc.T("Close.Body"),
            Loc.T("Close.Yes"),
            Loc.T("Close.No"),
            destructive: true);
        if (!close)
        {
            return false;
        }

        _buildCancellation?.Cancel();
        _metadataCancellation?.Cancel();
        SaveSettings();
        Extraction.Shutdown();
        Queue.Shutdown();
        return true;
    }

    private void SetStatus(StatusKind kind, string key, params object?[] args)
    {
        StatusKind = kind;
        _statusKey = key;
        _statusArgs = args;
        RefreshStatusText();
    }

    private void RefreshStatusText() => StatusText = Loc.F(_statusKey, _statusArgs);

    private void ShowResult(BuildOutcome outcome)
    {
        _outcome = outcome;
        var v = outcome.Verification;
        ResultPath = outcome.OutputPath;
        ResultType = v.ContainerLabel;
        ResultSize = Formatters.SizeWithBytes(v.Length);
        ResultContentId = string.IsNullOrWhiteSpace(v.ContentId) ? "—" : v.ContentId;
        ResultSha = v.Sha256 ?? Loc.T("Result.NoSha");
        ResultElapsed = Formatters.Duration(outcome.Elapsed);
        ResultRatio = _sourceBytes > 0
            ? Loc.F("Result.Ratio", Formatters.Size(_sourceBytes), Formatters.Size(v.Length), (v.Length * 100.0 / _sourceBytes).ToString("0.#"))
            : string.Empty;
        HasResult = true;
    }

    private void ClearResult()
    {
        _outcome = null;
        HasResult = false;
        ResultPath = string.Empty;
        ResultType = string.Empty;
        ResultSize = string.Empty;
        ResultContentId = string.Empty;
        ResultSha = string.Empty;
        ResultElapsed = string.Empty;
        ResultRatio = string.Empty;
    }

    [RelayCommand(CanExecute = nameof(CanOpenOutput))]
    private async Task OpenOutputAsync()
    {
        var folder = Path.GetDirectoryName(ResultPath);
        if (folder != null && !await _dialogs.OpenFolderAsync(folder))
        {
            await _dialogs.ShowErrorAsync(Loc.T("Build.OpenFailed"), folder);
        }
    }

    [RelayCommand(CanExecute = nameof(HasResult))]
    private async Task CopyResultAsync()
    {
        if (_outcome == null)
        {
            return;
        }

        var v = _outcome.Verification;
        var text = new StringBuilder()
            .AppendLine(Loc.F("Result.ClipboardHeader", AppInfo.Name))
            .AppendLine(Loc.T("Result.File") + ": " + _outcome.OutputPath)
            .AppendLine(Loc.T("Result.Type") + ": " + v.ContainerLabel)
            .AppendLine(Loc.T("Result.Size") + ": " + Formatters.SizeWithBytes(v.Length))
            .AppendLine(Loc.F("Build.Fih", v.SignedByte.ToString("X2"), v.OuterMode.ToString("X4")))
            .AppendLine("Content ID: " + (v.ContentId ?? "—"))
            .AppendLine(Loc.F("Result.Entries", v.EntryCount))
            .AppendLine("SHA-256: " + (v.Sha256 ?? "—"))
            .AppendLine(v.Contents is { } contents
                ? Loc.F(contents.IsFull ? "Plan.VerifiedFull" : "Plan.VerifiedQuick", contents.Checks.Count, Formatters.Duration(contents.Elapsed))
                : "—")
            .AppendLine(Loc.F("Result.Elapsed", Formatters.Duration(_outcome.Elapsed)))
            .ToString();

        try
        {
            await _dialogs.SetClipboardAsync(text);
            SetStatus(StatusKind.Ready, "Status.CopiedResult");
        }
        catch (Exception ex)
        {
            ErrorBanner = Loc.F("Build.CopyFailed", ex.Message);
        }
    }

    // ===================== Nhật ký =====================

    private void Log(LogLevel level, string message) => _pendingLogs.Enqueue(new LogEntry(level, message));

    private void Drain()
    {
        var progress = Interlocked.Exchange(ref _pendingProgress, null);
        if (progress != null)
        {
            ApplyProgress(progress);
        }

        if (_pendingLogs.IsEmpty)
        {
            return;
        }

        var batch = new List<LogEntry>();
        while (batch.Count < 2000 && _pendingLogs.TryDequeue(out var entry))
        {
            batch.Add(entry);
        }

        LogEntries.AddBatch(batch);
        LogCount = LogEntries.Count;
        LogAppended?.Invoke(this, EventArgs.Empty);
    }

    private void ApplyProgress(BuildProgress progress)
    {
        _lastProgress = progress;
        _lastProgressAt = DateTime.UtcNow;
        OverallPercent = Math.Clamp(progress.OverallPercent, 0, 100);
        PhasePercent = Math.Clamp(progress.PhasePercent, 0, 100);
        PercentText = $"{OverallPercent:0}%";
        PhaseText = progress.IsComplete
            ? Loc.T("Phase.Done")
            : $"{progress.Phase} · {progress.PhasePercent:0}%";
        ThroughputText = progress.IsComplete || string.IsNullOrEmpty(progress.Throughput) ? string.Empty : progress.Throughput!;
        UpdateEta();
    }

    private void Tick()
    {
        if (!IsBuilding)
        {
            return;
        }

        ElapsedText = Formatters.Clock(_buildStopwatch.Elapsed);
        UpdateEta();
    }

    private void UpdateEta()
    {
        if (_lastProgress?.Eta is not { } eta || _lastProgress.IsComplete)
        {
            EtaText = IsBuilding && _lastProgress != null && _lastProgress.OverallPercent > 0 ? Loc.T("Eta.Estimating") : string.Empty;
            return;
        }

        var remaining = eta - (DateTime.UtcNow - _lastProgressAt);
        if (remaining < TimeSpan.FromSeconds(1))
        {
            remaining = TimeSpan.FromSeconds(1);
        }

        EtaText = Loc.F("Eta.Remaining", Formatters.Duration(remaining));
    }

    private string BuildLogText()
    {
        var builder = new StringBuilder(LogEntries.Count * 80);
        foreach (var entry in LogEntries)
        {
            builder.Append('[').Append(entry.TimeText).Append("] ").AppendLine(entry.Message);
        }

        return builder.ToString();
    }

    [RelayCommand]
    private async Task CopyLogAsync()
    {
        if (LogEntries.Count == 0)
        {
            return;
        }

        try
        {
            await _dialogs.SetClipboardAsync(BuildLogText());
            SetStatus(IsBuilding ? StatusKind.Working : StatusKind.Ready, "Status.CopiedLog");
        }
        catch (Exception ex)
        {
            ErrorBanner = Loc.F("Log.CopyFailed", ex.Message);
        }
    }

    [RelayCommand]
    private async Task SaveLogAsync()
    {
        if (LogEntries.Count == 0)
        {
            return;
        }

        var path = await _dialogs.SaveFileAsync(
            Loc.T("Log.SaveTitle"),
            $"fpkg-build-log-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
            "txt",
            new FilePickerFileType(Loc.T("Log.TextFiles")) { Patterns = ["*.txt"] });
        if (path == null)
        {
            return;
        }

        try
        {
            await File.WriteAllTextAsync(path, BuildLogText(), Encoding.UTF8);
            SetStatus(IsBuilding ? StatusKind.Working : StatusKind.Ready, "Status.SavedLog", path);
        }
        catch (Exception ex)
        {
            ErrorBanner = Loc.F("Log.SaveFailed", ex.Message);
        }
    }

    [RelayCommand]
    private void ClearLog()
    {
        LogEntries.Clear();
        LogCount = 0;
    }

    [RelayCommand]
    private void DismissError() => ErrorBanner = null;

    [RelayCommand]
    private void DismissNotice() => NoticeBanner = null;

    [RelayCommand]
    private void ToggleTheme() => IsDarkTheme = !IsDarkTheme;

    [RelayCommand]
    private Task OpenAuthorAsync() => _dialogs.OpenUriAsync(AppInfo.AuthorUrl);
}
