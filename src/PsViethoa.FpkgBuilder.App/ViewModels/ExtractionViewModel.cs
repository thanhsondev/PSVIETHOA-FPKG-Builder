using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PsViethoa.FpkgBuilder.App.Services;
using PsViethoa.FpkgBuilder.Core.Localization;
using PsViethoa.FpkgBuilder.Core.Models;
using PsViethoa.FpkgBuilder.Core.Services;

namespace PsViethoa.FpkgBuilder.App.ViewModels;

/// <summary>Một hàng khoá/giá trị trong bảng thông tin gói.</summary>
public sealed record KeyValueRow(string Key, string Value);

/// <summary>ObservableCollection thay toàn bộ nội dung bằng một thông báo Reset (danh sách hàng chục nghìn tệp).</summary>
public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    public void ReplaceAll(IEnumerable<T> items)
    {
        Items.Clear();
        foreach (var item in items)
        {
            Items.Add(item);
        }

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}

/// <summary>Một tệp/thư mục trong ảnh trong, có thể tích chọn.</summary>
public sealed partial class PackageEntryItem : ObservableObject
{
    public PackageEntryItem(PackageEntry entry)
    {
        Entry = entry;
    }

    public PackageEntry Entry { get; }

    public string Path => Entry.Path;

    public string Name => Entry.Name;

    public bool IsDirectory => Entry.IsDirectory;

    public bool IsFile => !Entry.IsDirectory;

    public string SizeText => Entry.IsDirectory ? string.Empty : Formatters.Size(Entry.Size);

    public string DetailText => Entry.IsDirectory
        ? Loc.T("Extract.Folder")
        : Entry.IsPfsCompressed ? $"PFSC · {Formatters.Size(Entry.CompressedSize)}" : Entry.CompressionLabel;

    [ObservableProperty] private bool _isSelected;
}

/// <summary>Trạng thái và hành vi của chế độ "Giải nén gói": đọc thông tin, liệt kê tệp, xem trước, trích xuất có chọn lọc.</summary>
public sealed partial class ExtractionViewModel : ObservableObject
{
    private const int PreviewHeadBytes = PackagePreview.MaxTextBytes;

    private readonly AppSettings _settings;
    private readonly DialogService _dialogs;
    private readonly Action<LogLevel, string> _mainLog;
    private readonly DispatcherTimer _pathDebounce;
    private readonly DispatcherTimer _filterDebounce;
    private readonly DispatcherTimer _previewDebounce;
    private readonly DispatcherTimer _tickTimer;
    private readonly Stopwatch _stopwatch = new();
    private readonly string _previewTempRoot = Path.Combine(Path.GetTempPath(), "psviethoa-preview", Guid.NewGuid().ToString("N"));

    private PackageReader? _reader;
    private PackageInfo? _info;
    private CancellationTokenSource? _loadCancellation;
    private CancellationTokenSource? _workCancellation;
    private CancellationTokenSource? _previewCancellation;
    private List<PackageEntryItem> _allItems = new();
    private string? _loadedPath;
    private string? _decryptedTempHint;
    private bool _bulkSelecting;
    private bool _recountQueued;
    private bool _shutdown;
    private int _filterVersion;

    public ExtractionViewModel(AppSettings settings, DialogService dialogs, Action<LogLevel, string> log)
    {
        _settings = settings;
        _dialogs = dialogs;
        _mainLog = log;
        // Lưu ý: hàm dựng DispatcherTimer(interval, priority, callback) của Avalonia tự Start() ngay — dùng CreateTimer để
        // các bộ đếm chỉ chạy khi được yêu cầu (nếu không gói đã nhớ sẽ tự nạp 0,5 s sau khi mở ứng dụng, kể cả ở chế độ tạo gói).
        _pathDebounce = CreateTimer(500, () =>
        {
            _pathDebounce!.Stop();
            if (!string.Equals(PackagePath.Trim(), _loadedPath, StringComparison.Ordinal) && !IsLoading)
            {
                StartLoad();
            }
        });
        _filterDebounce = CreateTimer(150, () =>
        {
            _filterDebounce!.Stop();
            ApplyFilterNow();
        });
        _previewDebounce = CreateTimer(150, () =>
        {
            _previewDebounce!.Stop();
            _ = LoadPreviewAsync();
        });
        _tickTimer = CreateTimer(500, () => ElapsedText = Formatters.Clock(_stopwatch.Elapsed));

        _packagePath = settings.ExtractPackagePath ?? string.Empty;
        _outputFolder = settings.ExtractOutputFolder ?? string.Empty;
        _autoOutputFolder = settings.ExtractOutputAuto;
        Loc.Current.LanguageChanged += (_, _) => OnLanguageChanged();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Shutdown();
        ShowEmpty();
        RefreshFreeSpace();
    }

    /// <summary>Tạo DispatcherTimer ở trạng thái dừng (chỉ Start() khi cần).</summary>
    private static DispatcherTimer CreateTimer(int milliseconds, Action tick)
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => tick();
        return timer;
    }

    // ===================== Bước 1: gói nguồn & thư mục xuất =====================

    [ObservableProperty] private string _packagePath = string.Empty;
    [ObservableProperty] private string _containerSummary = string.Empty;
    [ObservableProperty] private string _outputFolder = string.Empty;
    [ObservableProperty] private string _freeSpaceText = string.Empty;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _loadingText = string.Empty;
    [ObservableProperty] private string? _loadError;

    public bool HasLoadError => !string.IsNullOrEmpty(LoadError);
    public bool HasContainerSummary => !string.IsNullOrEmpty(ContainerSummary);

    partial void OnPackagePathChanged(string value)
    {
        _pathDebounce.Stop();
        if (!string.Equals(value.Trim(), _loadedPath, StringComparison.Ordinal))
        {
            _pathDebounce.Start();
        }
    }

    partial void OnOutputFolderChanged(string value)
    {
        _settings.ExtractOutputFolder = value.Trim();
        RefreshFreeSpace();
    }

    partial void OnLoadErrorChanged(string? value) => OnPropertyChanged(nameof(HasLoadError));
    partial void OnContainerSummaryChanged(string value) => OnPropertyChanged(nameof(HasContainerSummary));

    // ===================== Bước 2: nội dung cần trích =====================

    [ObservableProperty] private bool _extractAppTree = true;
    [ObservableProperty] private bool _extractOuter;
    [ObservableProperty] private bool _extractCnt;
    [ObservableProperty] private bool _extractSi;
    [ObservableProperty] private bool _decompressPfsc = true;
    [ObservableProperty] private bool _hasSupplement;
    [ObservableProperty] private string _passcode = new('0', BuildRequest.PasscodeLength);
    [ObservableProperty] private bool _showPasscode;

    partial void OnExtractAppTreeChanged(bool value) => ExtractCommand.NotifyCanExecuteChanged();
    partial void OnExtractOuterChanged(bool value) => ExtractCommand.NotifyCanExecuteChanged();
    partial void OnExtractCntChanged(bool value) => ExtractCommand.NotifyCanExecuteChanged();
    partial void OnExtractSiChanged(bool value) => ExtractCommand.NotifyCanExecuteChanged();

    // ===================== Thông tin gói =====================

    [ObservableProperty] private bool _hasPackage;
    [ObservableProperty] private bool _canExtract;
    [ObservableProperty] private bool _canExport;
    [ObservableProperty] private string? _blockedReason;
    [ObservableProperty] private bool _metadataWarning;
    [ObservableProperty] private string _metaTitle = string.Empty;
    [ObservableProperty] private string _metaSubtitle = string.Empty;
    [ObservableProperty] private string _kindChip = string.Empty;
    [ObservableProperty] private string _modeChip = string.Empty;
    [ObservableProperty] private string _versionChip = string.Empty;
    [ObservableProperty] private string _sdkChip = string.Empty;
    [ObservableProperty] private string _categoryChip = string.Empty;
    [ObservableProperty] private string _filesChip = string.Empty;
    [ObservableProperty] private Bitmap? _iconImage;
    [ObservableProperty] private IReadOnlyList<KeyValueRow> _generalRows = Array.Empty<KeyValueRow>();
    [ObservableProperty] private IReadOnlyList<KeyValueRow> _paramRows = Array.Empty<KeyValueRow>();
    [ObservableProperty] private string? _paramError;
    [ObservableProperty] private string? _sha256;
    [ObservableProperty] private bool _isHashing;
    [ObservableProperty] private double _hashPercent;

    /// <summary>Đang kiểm tra nội dung gói bằng engine (nhanh hoặc đầy đủ).</summary>
    [ObservableProperty] private bool _isVerifying;
    [ObservableProperty] private bool _isVerifyingFull;
    [ObservableProperty] private double _verifyPercent;

    /// <summary>Gói đang mở là DLC có dữ liệu: hiện nút "Xuất mẫu DLC".</summary>
    [ObservableProperty] private bool _isDlcWithData;

    /// <summary>Kết quả lần kiểm tra gần nhất (null = chưa kiểm tra gói này).</summary>
    [ObservableProperty] private ContentVerification? _verifyResult;

    public bool HasIcon => IconImage != null;
    public bool HasBlockedReason => !string.IsNullOrEmpty(BlockedReason);
    public bool HasParams => ParamRows.Count > 0;
    public bool HasParamError => !string.IsNullOrEmpty(ParamError);
    public bool HasSha => !string.IsNullOrEmpty(Sha256);
    public bool CanComputeSha => HasPackage && !HasSha && !IsBusy;
    public bool CanVerify => HasPackage && !IsBusy;
    public bool CanExportDlcTemplate => HasPackage && IsDlcWithData && !IsBusy;
    public bool IsBusy => IsLoading || IsExtracting || IsHashing || IsVerifying;
    public bool HasVerifyResult => VerifyResult != null && !IsVerifying;
    public bool VerifyPassed => VerifyResult is { IsValid: true };
    public bool VerifyFailed => VerifyResult is { IsValid: false };

    public string VerifyingText => IsVerifyingFull
        ? Loc.F("Extract.VerifyingFull", VerifyPercent.ToString("0"))
        : Loc.T("Extract.VerifyingQuick");

    public string VerifyResultText => VerifyResult switch
    {
        null => string.Empty,
        { IsValid: true } result => Loc.F(result.IsFull ? "Plan.VerifiedFull" : "Plan.VerifiedQuick", result.Checks.Count, Formatters.Duration(result.Elapsed)),
        { } result => Loc.F("Extract.VerifyFailed", result.Issues.Count) + "\n" + string.Join("\n", result.Issues.Select(issue => "• " + issue)),
    };
    public bool CanEdit => !IsBusy;
    public string HashingText => Loc.F("Extract.Hashing", HashPercent.ToString("0"));

    partial void OnIconImageChanged(Bitmap? oldValue, Bitmap? newValue)
    {
        oldValue?.Dispose();
        OnPropertyChanged(nameof(HasIcon));
    }

    partial void OnBlockedReasonChanged(string? value) => OnPropertyChanged(nameof(HasBlockedReason));
    partial void OnParamRowsChanged(IReadOnlyList<KeyValueRow> value) => OnPropertyChanged(nameof(HasParams));
    partial void OnParamErrorChanged(string? value) => OnPropertyChanged(nameof(HasParamError));
    partial void OnHashPercentChanged(double value) => OnPropertyChanged(nameof(HashingText));
    partial void OnVerifyPercentChanged(double value) => OnPropertyChanged(nameof(VerifyingText));
    partial void OnIsVerifyingFullChanged(bool value) => OnPropertyChanged(nameof(VerifyingText));

    partial void OnVerifyResultChanged(ContentVerification? value)
    {
        OnPropertyChanged(nameof(HasVerifyResult));
        OnPropertyChanged(nameof(VerifyPassed));
        OnPropertyChanged(nameof(VerifyFailed));
        OnPropertyChanged(nameof(VerifyResultText));
    }

    partial void OnIsVerifyingChanged(bool value)
    {
        OnPropertyChanged(nameof(HasVerifyResult));
        NotifyBusy();
    }

    partial void OnSha256Changed(string? value)
    {
        OnPropertyChanged(nameof(HasSha));
        OnPropertyChanged(nameof(CanComputeSha));
        ComputeSha256Command.NotifyCanExecuteChanged();
    }

    partial void OnIsLoadingChanged(bool value) => NotifyBusy();
    partial void OnIsHashingChanged(bool value) => NotifyBusy();
    partial void OnHasPackageChanged(bool value) => NotifyBusy();
    partial void OnCanExtractChanged(bool value) => NotifyBusy();
    partial void OnCanExportChanged(bool value) => NotifyBusy();

    private void NotifyBusy()
    {
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(CanEditOutput));
        OnPropertyChanged(nameof(CanComputeSha));
        OnPropertyChanged(nameof(CanVerify));
        OnPropertyChanged(nameof(CanExportDlcTemplate));
        ExportDlcTemplateCommand.NotifyCanExecuteChanged();
        ComputeSha256Command.NotifyCanExecuteChanged();
        VerifyQuickCommand.NotifyCanExecuteChanged();
        VerifyFullCommand.NotifyCanExecuteChanged();
        ExtractCommand.NotifyCanExecuteChanged();
        CopyInfoCommand.NotifyCanExecuteChanged();
        ReloadCommand.NotifyCanExecuteChanged();
        BrowsePackageCommand.NotifyCanExecuteChanged();
        BrowseOutputCommand.NotifyCanExecuteChanged();
        ExtractThisFileCommand.NotifyCanExecuteChanged();
        OpenExternalCommand.NotifyCanExecuteChanged();
    }

    // ===================== Thẻ trạng thái =====================

    [ObservableProperty] private string _headlineText = string.Empty;
    [ObservableProperty] private string _sublineText = string.Empty;
    [ObservableProperty] private StatusKind _statusKind = StatusKind.Ready;
    [ObservableProperty] private bool _isExtracting;
    [ObservableProperty] private bool _isCanceling;
    [ObservableProperty] private bool _isIndeterminate;
    [ObservableProperty] private double _progressPercent;
    [ObservableProperty] private string _percentText = "0%";
    [ObservableProperty] private string _progressText = string.Empty;
    [ObservableProperty] private string _currentFile = string.Empty;
    [ObservableProperty] private string _elapsedText = string.Empty;
    [ObservableProperty] private bool _hasResult;
    [ObservableProperty] private string _resultTitle = string.Empty;
    [ObservableProperty] private string _resultFolder = string.Empty;
    [ObservableProperty] private string? _errorBanner;
    [ObservableProperty] private string? _noticeBanner;

    public bool IsStatusWorking => StatusKind == StatusKind.Working;
    public bool IsStatusSuccess => StatusKind == StatusKind.Success;
    public bool IsStatusError => StatusKind == StatusKind.Error;
    public bool IsStatusCanceled => StatusKind == StatusKind.Canceled;
    public bool HasErrorBanner => !string.IsNullOrEmpty(ErrorBanner);
    public bool HasNoticeBanner => !string.IsNullOrEmpty(NoticeBanner);
    public bool CanCancel => IsExtracting && !IsCanceling;
    public bool CanOpenOutput => (Directory.Exists(OutputFolder.Trim()) || Directory.Exists(ResultFolder)) && !IsExtracting;

    partial void OnStatusKindChanged(StatusKind value)
    {
        OnPropertyChanged(nameof(IsStatusWorking));
        OnPropertyChanged(nameof(IsStatusSuccess));
        OnPropertyChanged(nameof(IsStatusError));
        OnPropertyChanged(nameof(IsStatusCanceled));
    }

    partial void OnErrorBannerChanged(string? value) => OnPropertyChanged(nameof(HasErrorBanner));
    partial void OnNoticeBannerChanged(string? value) => OnPropertyChanged(nameof(HasNoticeBanner));

    partial void OnIsExtractingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(CanOpenOutput));
        CancelWorkCommand.NotifyCanExecuteChanged();
        OpenOutputFolderCommand.NotifyCanExecuteChanged();
        NotifyBusy();
    }

    partial void OnIsCancelingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanCancel));
        CancelWorkCommand.NotifyCanExecuteChanged();
    }

    // ===================== Danh sách tệp & xem trước =====================

    public BulkObservableCollection<PackageEntryItem> VisibleItems { get; } = new();

    [ObservableProperty] private string _filter = string.Empty;
    [ObservableProperty] private string _selectionSummary = string.Empty;
    [ObservableProperty] private int _selectedCount;
    [ObservableProperty] private long _selectedBytes;
    [ObservableProperty] private int _selectedTab;
    [ObservableProperty] private PackageEntryItem? _previewItem;
    [ObservableProperty] private string _previewTitle = string.Empty;
    [ObservableProperty] private string _previewInfo = string.Empty;
    [ObservableProperty] private PreviewKind _previewKind = PreviewKind.None;
    [ObservableProperty] private string _previewText = string.Empty;
    [ObservableProperty] private Bitmap? _previewImage;
    [ObservableProperty] private bool _isPreviewLoading;

    public bool HasEntries => _allItems.Count > 0;
    public bool HasSelection => SelectedCount > 0;
    public bool HasPreviewItem => PreviewItem is { IsFile: true };
    public bool IsPreviewText => PreviewKind == PreviewKind.Text;
    public bool IsPreviewImage => PreviewKind == PreviewKind.Image;
    public bool IsPreviewHex => PreviewKind == PreviewKind.Hex;
    public bool IsPreviewPlaceholder => !IsPreviewLoading && PreviewKind is PreviewKind.None or PreviewKind.Empty;
    public string PreviewPlaceholder => PreviewItem == null
        ? Loc.T("Extract.PreviewEmpty")
        : PreviewItem.IsDirectory ? Loc.T("Extract.PreviewDir") : PreviewKind == PreviewKind.Empty ? Loc.T("Extract.PreviewEmptyFile") : Loc.T("Extract.PreviewEmpty");

    public LogCollection LogEntries { get; } = new();

    [ObservableProperty] private int _logCount;

    partial void OnFilterChanged(string value)
    {
        _filterDebounce.Stop();
        _filterDebounce.Start();
    }

    partial void OnSelectedCountChanged(int value)
    {
        OnPropertyChanged(nameof(HasSelection));
        ExtractCommand.NotifyCanExecuteChanged();
    }

    partial void OnPreviewItemChanged(PackageEntryItem? value)
    {
        OnPropertyChanged(nameof(HasPreviewItem));
        ExtractThisFileCommand.NotifyCanExecuteChanged();
        OpenExternalCommand.NotifyCanExecuteChanged();
        _previewDebounce.Stop();
        _previewDebounce.Start();
    }

    partial void OnPreviewKindChanged(PreviewKind value) => NotifyPreview();
    partial void OnIsPreviewLoadingChanged(bool value) => NotifyPreview();

    partial void OnPreviewImageChanged(Bitmap? oldValue, Bitmap? newValue) => oldValue?.Dispose();

    private void NotifyPreview()
    {
        OnPropertyChanged(nameof(IsPreviewText));
        OnPropertyChanged(nameof(IsPreviewImage));
        OnPropertyChanged(nameof(IsPreviewHex));
        OnPropertyChanged(nameof(IsPreviewPlaceholder));
        OnPropertyChanged(nameof(PreviewPlaceholder));
    }

    /// <summary>Hàng đang được làm nổi (nhấn chuột / di chuyển bằng bàn phím) → xem trước.</summary>
    public void Highlight(PackageEntryItem? item)
    {
        if (!ReferenceEquals(PreviewItem, item))
        {
            PreviewItem = item;
        }
    }

    // ===================== Nạp gói =====================

    /// <summary>Nạp gói ngay (kéo–thả, dev hook, nút Chọn…).</summary>
    public void LoadPackage(string path)
    {
        PackagePath = path;
        _pathDebounce.Stop();
        StartLoad();
    }

    /// <summary>Gọi khi chuyển sang chế độ giải nén: nạp gói đã nhớ nếu chưa nạp.</summary>
    public void EnsureLoaded()
    {
        var path = PackagePath.Trim();
        if (!IsLoading && path.Length > 0 && !string.Equals(path, _loadedPath, StringComparison.Ordinal) && File.Exists(path))
        {
            StartLoad();
        }
    }

    public void SaveSettings()
    {
        _settings.ExtractPackagePath = PackagePath.Trim();
        _settings.ExtractOutputFolder = OutputFolder.Trim();
    }

    /// <summary>Huỷ mọi việc đang chạy, giải phóng tệp tạm và thư mục xem trước (gọi khi đóng ứng dụng).</summary>
    public void Shutdown()
    {
        if (_shutdown)
        {
            return;
        }

        _shutdown = true;
        _loadCancellation?.Cancel();
        _workCancellation?.Cancel();
        _previewCancellation?.Cancel();
        var reader = _reader;
        _reader = null;
        reader?.Dispose();
        BuildEngine.TryDeleteDirectory(_previewTempRoot);
    }

    private void StartLoad()
    {
        if (IsExtracting || IsHashing || IsVerifying)
        {
            // Reader đang được dùng để trích xuất/băm: không thay gói giữa chừng (ô đường dẫn bị khoá, kéo–thả bị chặn ở View).
            return;
        }

        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _loadCancellation = null;

        // Lượt nạp trước (nếu có) đã bị huỷ và sẽ không tự tắt cờ bận nữa — tắt ở đây để giao diện không bị khoá
        // khi đường dẫn mới rỗng/không tồn tại.
        IsLoading = false;
        LoadingText = string.Empty;

        var path = PackagePath.Trim();
        if (path.Length == 0)
        {
            ResetPackage();
            ShowEmpty();
            return;
        }

        if (!File.Exists(path))
        {
            ResetPackage();
            ShowEmpty();
            LoadError = Loc.T("Extract.FileMissing");
            return;
        }

        var cancellation = new CancellationTokenSource();
        _loadCancellation = cancellation;
        _ = LoadAsync(path, cancellation.Token);
    }

    private async Task LoadAsync(string path, CancellationToken cancellationToken)
    {
        ResetPackage();
        ShowEmpty();
        IsLoading = true;
        LoadingText = Loc.T("Extract.Loading");
        HeadlineText = Loc.T("Extract.HeadLoading");
        SublineText = path;
        MetaTitle = Path.GetFileName(path);
        MetaSubtitle = path;
        var passcode = Passcode;
        try
        {
            var info = await Task.Run(() => PackageInspector.Inspect(path, passcode, cancellationToken), cancellationToken);
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            _info = info;
            _loadedPath = path;
            _settings.ExtractPackagePath = path;
            SuggestOutputFolder(path);
            RenderInfo();
            IconImage = await Task.Run(() => LoadBitmap(info.IconBytes), cancellationToken);
            Log(LogLevel.Info, Loc.F("Extract.Inspected", path));

            if (info.CanExtract)
            {
                LoadingText = Loc.T(info.ImageMode == PackageImageMode.Native ? "Extract.OpeningNative" : "Extract.Opening");
                SublineText = Loc.T(info.ImageMode == PackageImageMode.Native ? "Extract.SubNativeWorking" : "Extract.SubPlain");
                var temporary = string.IsNullOrWhiteSpace(_settings.TemporaryFolder) ? null : _settings.TemporaryFolder;
                var reader = await Task.Run(() => PackageReader.Open(path, passcode, temporary, cancellationToken, message =>
                {
                    Log(LogLevel.Info, message);
                    if (info.ImageMode == PackageImageMode.Native)
                    {
                        _decryptedTempHint = temporary ?? Path.GetTempPath();
                    }
                }), cancellationToken);
                if (cancellationToken.IsCancellationRequested || _shutdown)
                {
                    reader.Dispose();
                    return;
                }

                _reader = reader;
                var items = await Task.Run(() => reader.Entries.Select(e => new PackageEntryItem(e)).ToList(), cancellationToken);
                if (cancellationToken.IsCancellationRequested)
                {
                    // Đã chuyển sang gói khác trong lúc chờ: reader này đã bị ResetPackage() của lượt mới giải phóng.
                    return;
                }

                foreach (var item in items)
                {
                    item.PropertyChanged += OnItemPropertyChanged;
                }

                _allItems = items;
                OnPropertyChanged(nameof(HasEntries));
                await ApplyFilterAsync();
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                Recount();
                FilesChip = Loc.F("Meta.Files", Formatters.Count(reader.FileCount), Formatters.Size(reader.TotalBytes));
                Log(LogLevel.Success, Loc.F("Extract.Opened", path, Formatters.Count(reader.FileCount), Formatters.Size(reader.TotalBytes)));
                CanExtract = true;

                // Dev hook cho ảnh chụp tài liệu: PSVIETHOA_EXTRACT_PREVIEW=<đường dẫn trong gói> → tích chọn + xem trước tệp đó.
                var previewHook = Environment.GetEnvironmentVariable("PSVIETHOA_EXTRACT_PREVIEW");
                if (!string.IsNullOrWhiteSpace(previewHook) &&
                    items.FirstOrDefault(i => i.IsFile && (string.Equals(i.Path, previewHook.Trim(), StringComparison.OrdinalIgnoreCase) || string.Equals(i.Name, previewHook.Trim(), StringComparison.OrdinalIgnoreCase))) is { } hooked)
                {
                    hooked.IsSelected = true;
                    Highlight(hooked);
                }

                // Dev hook: PSVIETHOA_AUTOEXTRACT=1 → chạy trích xuất ngay sau khi nạp (kiểm thử luồng giao diện / chụp màn hình "xong").
                if (Environment.GetEnvironmentVariable("PSVIETHOA_AUTOEXTRACT") == "1")
                {
                    Dispatcher.UIThread.Post(() => _ = ExtractCommand.ExecuteAsync(null), DispatcherPriority.Background);
                }
            }
            else
            {
                BlockedReason = info.ExtractBlockedReason;
                ExtractAppTree = false;
                Log(LogLevel.Warning, info.ExtractBlockedReason ?? string.Empty);
            }

            CanExport = info.Cnt != null;
            IsDlcWithData = info.IsDlcWithData;
            HasSupplement = info.HasSupplement;
            if (!HasSupplement)
            {
                ExtractSi = false;
            }

            RefreshHeadline();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            LoadError = ex.Message;
            MetaTitle = Loc.T("Extract.ReadFailedTitle");
            MetaSubtitle = ex.Message;
            MetadataWarning = true;
            HeadlineText = Loc.T("Extract.ReadFailedTitle");
            SublineText = ex.Message;
            Log(LogLevel.Error, Loc.F("Extract.Failed", ex.Message));
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                IsLoading = false;
                LoadingText = string.Empty;
                NotifyBusy();
            }
        }
    }

    /// <summary>Gợi ý thư mục xuất mặc định cho một gói: "&lt;tên gói&gt;-extract" cạnh tệp .pkg (rỗng nếu không xác định được).</summary>
    private static string DefaultOutputFor(string packagePath)
    {
        try
        {
            var directory = Path.GetDirectoryName(packagePath);
            return string.IsNullOrEmpty(directory) ? string.Empty : Path.Combine(directory, Path.GetFileNameWithoutExtension(packagePath) + "-extract");
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    /// <summary>Thư mục xuất luôn tự đặt theo gói ("&lt;gói&gt;-extract" cạnh tệp .pkg); mặc định bật.</summary>
    [ObservableProperty] private bool _autoOutputFolder = true;

    /// <summary>Ô thư mục xuất chỉ sửa tay được khi tắt "Tự đặt theo gói" (và không bận).</summary>
    public bool CanEditOutput => CanEdit && !AutoOutputFolder;

    partial void OnAutoOutputFolderChanged(bool value)
    {
        _settings.ExtractOutputAuto = value;
        OnPropertyChanged(nameof(CanEditOutput));
        if (value)
        {
            SuggestOutputFolder(_loadedPath ?? PackagePath.Trim());
        }
    }

    /// <summary>Khi "Tự đặt theo gói" đang bật, thư mục xuất luôn là "&lt;gói&gt;-extract" cạnh tệp .pkg hiện tại.</summary>
    private void SuggestOutputFolder(string packagePath)
    {
        if (!AutoOutputFolder)
        {
            return;
        }

        var suggestion = DefaultOutputFor(packagePath);
        if (suggestion.Length > 0)
        {
            OutputFolder = suggestion;
        }
    }

    private void RefreshFreeSpace()
    {
        var folder = OutputFolder.Trim();
        if (folder.Length == 0)
        {
            FreeSpaceText = string.Empty;
            OnPropertyChanged(nameof(CanOpenOutput));
            OpenOutputFolderCommand.NotifyCanExecuteChanged();
            return;
        }

        _ = Task.Run(() =>
        {
            // Thư mục có thể chưa tồn tại: dò lên thư mục cha gần nhất.
            var probe = folder;
            while (!string.IsNullOrEmpty(probe) && !Directory.Exists(probe))
            {
                probe = Path.GetDirectoryName(probe);
            }

            return string.IsNullOrEmpty(probe) ? null : DiskSpaceAdvisor.Probe(probe);
        }).ContinueWith(task =>
        {
            var info = task.IsCompletedSuccessfully ? task.Result : null;
            FreeSpaceText = info == null
                ? Loc.T("Extract.FreeSpaceUnknown")
                : Loc.F("Extract.FreeSpace", Formatters.Size(info.FreeBytes), info.MountPoint);
            OnPropertyChanged(nameof(CanOpenOutput));
            OpenOutputFolderCommand.NotifyCanExecuteChanged();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void ResetPackage()
    {
        _previewCancellation?.Cancel();
        var reader = _reader;
        _reader = null;
        reader?.Dispose();
        _info = null;
        _loadedPath = null;
        _decryptedTempHint = null;
        foreach (var item in _allItems)
        {
            item.PropertyChanged -= OnItemPropertyChanged;
        }

        _allItems = new List<PackageEntryItem>();
        VisibleItems.ReplaceAll(Array.Empty<PackageEntryItem>());
        OnPropertyChanged(nameof(HasEntries));
        PreviewItem = null;
        ClearPreview();
        SelectedCount = 0;
        SelectedBytes = 0;
        Sha256 = null;
        HashPercent = 0;
        VerifyResult = null;
        VerifyPercent = 0;
        HasResult = false;
        ErrorBanner = null;
        NoticeBanner = null;
        StatusKind = StatusKind.Ready;
    }

    private void ShowEmpty()
    {
        HasPackage = false;
        CanExtract = false;
        CanExport = false;
        IsDlcWithData = false;
        HasSupplement = false;
        BlockedReason = null;
        LoadError = null;
        MetadataWarning = false;
        ContainerSummary = string.Empty;
        MetaTitle = Loc.T("Extract.NoPackage");
        MetaSubtitle = Loc.T("Extract.NoPackageHint");
        KindChip = ModeChip = VersionChip = SdkChip = CategoryChip = FilesChip = string.Empty;
        IconImage = null;
        GeneralRows = Array.Empty<KeyValueRow>();
        ParamRows = Array.Empty<KeyValueRow>();
        ParamError = null;
        SelectionSummary = string.Empty;
        HeadlineText = Loc.T("Extract.HeadEmpty");
        SublineText = Loc.T("Extract.SubEmpty");
        ProgressPercent = 0;
        PercentText = "0%";
        ProgressText = string.Empty;
        CurrentFile = string.Empty;
        ElapsedText = string.Empty;
    }

    private void RenderInfo()
    {
        if (_info is not { } info)
        {
            return;
        }

        HasPackage = true;
        MetadataWarning = !info.CanExtract;
        MetaTitle = string.IsNullOrWhiteSpace(info.Title) ? (info.ContentId ?? Path.GetFileName(info.Path)) : info.Title!;
        MetaSubtitle = string.IsNullOrWhiteSpace(info.ContentId) ? info.Path : info.ContentId!;
        KindChip = info.Kind switch
        {
            PackageContainerKind.FullDebug => "FIH debug",
            PackageContainerKind.FullRetail => "FIH retail",
            PackageContainerKind.Meta => "CNT",
            _ => "?",
        };
        ModeChip = info.ImageMode switch
        {
            PackageImageMode.PlaintextNoAuth => "PLAINTEXT_NOAUTH",
            PackageImageMode.Native => "AES-XTS",
            _ => string.Empty,
        };
        VersionChip = info.Params?.ContentVersion is { } version ? Loc.F("Meta.Version", version) : string.Empty;
        SdkChip = info.Params?.SdkMajor is { } sdk ? Loc.F("Meta.Sdk", sdk) : string.Empty;
        CategoryChip = info.Params?.CategoryLabel ?? string.Empty;
        BlockedReason = info.ExtractBlockedReason;
        ContainerSummary = Loc.F(
            "Extract.ContainerLine",
            info.Kind,
            info.Map != null ? Formatters.Size(info.Map.OuterPfsSize) : "—",
            info.Map != null ? Formatters.Size(info.Map.CntSize) : Formatters.Size(info.FileSize),
            info.Map is { SupplementSize: > 0 } ? Formatters.Size(info.Map.SupplementSize) : "—");
        GeneralRows = PackageInspector.GeneralRows(info, Sha256).Select(r => new KeyValueRow(r.Key, r.Value)).ToArray();
        ParamRows = info.Params?.Fields.Select(f => new KeyValueRow(f.Key, f.Value)).ToArray() ?? Array.Empty<KeyValueRow>();
        ParamError = info.ParamJsonError;
        if (_reader is { } reader)
        {
            FilesChip = Loc.F("Meta.Files", Formatters.Count(reader.FileCount), Formatters.Size(reader.TotalBytes));
        }
    }

    private void RefreshHeadline()
    {
        if (_info is not { } info)
        {
            HeadlineText = Loc.T("Extract.HeadEmpty");
            SublineText = Loc.T("Extract.SubEmpty");
            return;
        }

        if (IsExtracting)
        {
            return;
        }

        switch (info.Kind)
        {
            case PackageContainerKind.FullDebug when _reader != null:
                HeadlineText = Loc.T("Extract.HeadOpened");
                SublineText = _reader.ImageMode == PackageImageMode.Native
                    ? Loc.F("Extract.SubNative", _decryptedTempHint ?? Path.GetTempPath())
                    : Loc.T("Extract.SubPlain");
                break;
            case PackageContainerKind.FullRetail:
                HeadlineText = Loc.T("Extract.HeadRetail");
                SublineText = info.ExtractBlockedReason ?? string.Empty;
                break;
            case PackageContainerKind.Meta:
                HeadlineText = Loc.T("Extract.HeadMeta");
                SublineText = info.ExtractBlockedReason ?? string.Empty;
                break;
            default:
                HeadlineText = Loc.T("Extract.HeadOpened");
                SublineText = string.Empty;
                break;
        }
    }

    private static Bitmap? LoadBitmap(byte[]? bytes)
    {
        try
        {
            if (bytes is { Length: > 0 })
            {
                using var memory = new MemoryStream(bytes);
                return Bitmap.DecodeToWidth(memory, 256);
            }
        }
        catch (Exception)
        {
        }

        return null;
    }

    private void OnLanguageChanged()
    {
        if (_info != null)
        {
            RenderInfo();
            RefreshHeadline();
        }
        else
        {
            ShowEmpty();
        }

        Recount();
        RefreshFreeSpace();
        NotifyPreview();
    }

    // ===================== Lọc & chọn =====================

    /// <summary>Áp dụng bộ lọc ngay (Enter trong ô lọc).</summary>
    public void ApplyFilterNow()
    {
        _filterDebounce.Stop();
        _ = ApplyFilterAsync();
    }

    private async Task ApplyFilterAsync()
    {
        var version = ++_filterVersion;
        var filter = Filter.Trim();
        var source = _allItems;
        var visible = await Task.Run(() => FilterItems(source, filter));
        if (version != _filterVersion)
        {
            return;
        }

        VisibleItems.ReplaceAll(visible);
        Recount();
    }

    private static List<PackageEntryItem> FilterItems(List<PackageEntryItem> source, string filter)
    {
        if (filter.Length == 0)
        {
            return source.ToList();
        }

        var matched = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in source)
        {
            if (item.IsFile && item.Path.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                matched.Add(item.Path);
                var slash = item.Path.LastIndexOf('/');
                while (slash > 0)
                {
                    matched.Add(item.Path[..slash]);
                    slash = item.Path.LastIndexOf('/', slash - 1);
                }
            }
        }

        return source.Where(i => matched.Contains(i.Path) || (i.IsDirectory && i.Path.Contains(filter, StringComparison.OrdinalIgnoreCase))).ToList();
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(PackageEntryItem.IsSelected) || _bulkSelecting || sender is not PackageEntryItem item)
        {
            return;
        }

        if (item.IsDirectory)
        {
            // Tích một thư mục = tích mọi tệp bên trong.
            var prefix = item.Path + "/";
            _bulkSelecting = true;
            try
            {
                foreach (var child in _allItems)
                {
                    if (child.Path.StartsWith(prefix, StringComparison.Ordinal))
                    {
                        child.IsSelected = item.IsSelected;
                    }
                }
            }
            finally
            {
                _bulkSelecting = false;
            }
        }

        QueueRecount();
    }

    private void QueueRecount()
    {
        if (_recountQueued)
        {
            return;
        }

        _recountQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _recountQueued = false;
            Recount();
        }, DispatcherPriority.Background);
    }

    private void Recount()
    {
        var count = 0;
        long bytes = 0;
        var files = 0;
        foreach (var item in _allItems)
        {
            if (item.IsDirectory)
            {
                continue;
            }

            files++;
            if (item.IsSelected)
            {
                count++;
                bytes += item.Entry.Size;
            }
        }

        SelectedCount = count;
        SelectedBytes = bytes;
        var showing = VisibleItems.Count(i => i.IsFile);
        SelectionSummary = _allItems.Count == 0
            ? string.Empty
            : Loc.F("Extract.SelectionSummary", Formatters.Count(count), Formatters.Count(files), Formatters.Count(showing), Formatters.Size(bytes));
    }

    private void SetSelection(Func<PackageEntryItem, bool> selector)
    {
        _bulkSelecting = true;
        try
        {
            foreach (var item in VisibleItems)
            {
                item.IsSelected = selector(item);
            }
        }
        finally
        {
            _bulkSelecting = false;
        }

        Recount();
    }

    [RelayCommand]
    private void SelectAll() => SetSelection(_ => true);

    [RelayCommand]
    private void SelectNone() => SetSelection(_ => false);

    [RelayCommand]
    private void InvertSelection() => SetSelection(item => !item.IsSelected);

    // ===================== Xem trước =====================

    private void ClearPreview()
    {
        PreviewKind = PreviewKind.None;
        PreviewText = string.Empty;
        PreviewImage = null;
        PreviewTitle = string.Empty;
        PreviewInfo = string.Empty;
        IsPreviewLoading = false;
    }

    private async Task LoadPreviewAsync()
    {
        _previewCancellation?.Cancel();
        _previewCancellation?.Dispose();
        _previewCancellation = null;

        var item = PreviewItem;
        var reader = _reader;
        if (item == null || reader == null || item.IsDirectory)
        {
            ClearPreview();
            PreviewTitle = item?.Path ?? string.Empty;
            return;
        }

        var cancellation = new CancellationTokenSource();
        _previewCancellation = cancellation;
        var token = cancellation.Token;
        PreviewTitle = item.Path;
        PreviewInfo = Loc.T("Extract.PreviewLoading");
        IsPreviewLoading = true;
        PreviewImage = null;
        PreviewText = string.Empty;
        try
        {
            var entry = item.Entry;
            var result = await Task.Run(() =>
            {
                if (entry.Size == 0)
                {
                    return new PreviewResult(PreviewKind.Empty, string.Empty, null, Formatters.Size(0));
                }

                var head = new byte[(int)Math.Min(entry.Size, PreviewHeadBytes)];
                var read = reader.ReadRange(entry, 0, head, head.Length);
                token.ThrowIfCancellationRequested();
                var span = head.AsSpan(0, read);
                var kind = PackagePreview.Detect(entry.Path, span, entry.Size);
                switch (kind)
                {
                    case PreviewKind.Image:
                    {
                        // Ảnh cần đủ byte để giải mã; giới hạn theo PackagePreview.MaxImageBytes.
                        var full = read >= entry.Size ? head : ReadWhole(reader, entry);
                        token.ThrowIfCancellationRequested();
                        using var memory = new MemoryStream(full, 0, (int)Math.Min(full.Length, entry.Size));
                        Bitmap bitmap;
                        try
                        {
                            bitmap = Bitmap.DecodeToWidth(memory, 512);
                        }
                        catch (Exception)
                        {
                            return new PreviewResult(PreviewKind.Hex, PackagePreview.HexDump(span), null, Formatters.Size(entry.Size) + " · " + Loc.T("Extract.PreviewHex"));
                        }

                        return new PreviewResult(PreviewKind.Image, string.Empty, bitmap, Formatters.Size(entry.Size) + " · " + Loc.F("Extract.PreviewImage", bitmap.PixelSize.Width, bitmap.PixelSize.Height));
                    }

                    case PreviewKind.Text:
                    {
                        var text = PackagePreview.DecodeText(span);
                        var truncated = entry.Size > read ? Loc.F("Extract.PreviewTruncated", Formatters.Size(read)) : string.Empty;
                        return new PreviewResult(PreviewKind.Text, text, null, Formatters.Size(entry.Size) + " · " + Loc.T("Extract.PreviewText") + truncated);
                    }

                    case PreviewKind.Empty:
                        return new PreviewResult(PreviewKind.Empty, string.Empty, null, Formatters.Size(0));
                    default:
                    {
                        var hex = PackagePreview.HexDump(span[..Math.Min(span.Length, PackagePreview.MaxHexBytes)]);
                        var truncated = entry.Size > PackagePreview.MaxHexBytes ? Loc.F("Extract.PreviewTruncated", Formatters.Size(Math.Min(entry.Size, PackagePreview.MaxHexBytes))) : string.Empty;
                        return new PreviewResult(PreviewKind.Hex, hex, null, Formatters.Size(entry.Size) + " · " + Loc.T("Extract.PreviewHex") + truncated);
                    }
                }
            }, token);

            if (token.IsCancellationRequested || !ReferenceEquals(PreviewItem, item))
            {
                result.Image?.Dispose();
                return;
            }

            PreviewText = result.Text;
            PreviewImage = result.Image;
            PreviewInfo = result.Info;
            PreviewKind = result.Kind;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested)
            {
                PreviewKind = PreviewKind.None;
                PreviewInfo = ex.Message;
            }
        }
        finally
        {
            if (!token.IsCancellationRequested)
            {
                IsPreviewLoading = false;
            }
        }
    }

    /// <summary>Kết quả xem trước được tính ở luồng nền.</summary>
    private sealed record PreviewResult(PreviewKind Kind, string Text, Bitmap? Image, string Info);

    private static byte[] ReadWhole(PackageReader reader, PackageEntry entry)
    {
        var buffer = new byte[(int)Math.Min(entry.Size, PackagePreview.MaxImageBytes)];
        var done = 0;
        while (done < buffer.Length)
        {
            var chunk = new byte[Math.Min(1 << 20, buffer.Length - done)];
            var read = reader.ReadRange(entry, done, chunk, chunk.Length);
            if (read <= 0)
            {
                break;
            }

            Buffer.BlockCopy(chunk, 0, buffer, done, read);
            done += read;
        }

        return buffer;
    }

    private bool CanUsePreviewItem => HasPreviewItem && _reader != null && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanUsePreviewItem))]
    private async Task ExtractThisFileAsync()
    {
        if (PreviewItem is not { IsFile: true } item || _reader is not { } reader)
        {
            return;
        }

        var folder = await _dialogs.PickFolderAsync(Loc.T("Extract.PickOutput"), Directory.Exists(OutputFolder.Trim()) ? OutputFolder.Trim() : Path.GetDirectoryName(PackagePath.Trim()));
        if (folder == null)
        {
            return;
        }

        try
        {
            var entry = item.Entry;
            var decompress = DecompressPfsc;
            await Task.Run(() => reader.Extract([entry], folder, null, CancellationToken.None, 1, decompress));
            var target = Path.Combine(folder, entry.Path.Replace('/', Path.DirectorySeparatorChar));
            NoticeBanner = null;
            ResultTitle = Loc.F("Extract.ExtractedOne", entry.Path, folder);
            ResultFolder = folder;
            HasResult = true;
            Log(LogLevel.Success, Loc.F("Extract.ExtractedOne", entry.Path, target));
        }
        catch (Exception ex)
        {
            ErrorBanner = ex.Message;
            Log(LogLevel.Error, Loc.F("Extract.Failed", ex.Message));
        }
    }

    [RelayCommand(CanExecute = nameof(CanUsePreviewItem))]
    private async Task OpenExternalAsync()
    {
        if (PreviewItem is not { IsFile: true } item || _reader is not { } reader)
        {
            return;
        }

        try
        {
            var entry = item.Entry;
            var decompress = DecompressPfsc;
            var folder = Path.Combine(_previewTempRoot, Guid.NewGuid().ToString("N")[..8]);
            await Task.Run(() => reader.Extract([entry], folder, null, CancellationToken.None, 1, decompress));
            var target = Path.Combine(folder, entry.Path.Replace('/', Path.DirectorySeparatorChar));
            if (!await _dialogs.OpenFileAsync(target))
            {
                NoticeBanner = Loc.T("Extract.OpenExternalFailed");
            }
        }
        catch (Exception ex)
        {
            ErrorBanner = ex.Message;
        }
    }

    // ===================== Lệnh =====================

    private bool CanBrowse => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanBrowse))]
    private async Task BrowsePackageAsync()
    {
        var current = PackagePath.Trim();
        var initial = File.Exists(current) ? Path.GetDirectoryName(current) : Directory.Exists(current) ? current : null;
        var file = await _dialogs.PickFileAsync(
            Loc.T("Extract.PickPackage"),
            initial,
            new FilePickerFileType(Loc.T("Extract.PkgFilter")) { Patterns = ["*.pkg"] },
            new FilePickerFileType(Loc.T("Pick.AllFiles")) { Patterns = ["*"] });
        if (file != null)
        {
            LoadPackage(file);
        }
    }

    [RelayCommand(CanExecute = nameof(CanBrowse))]
    private async Task BrowseOutputAsync()
    {
        var current = OutputFolder.Trim();
        var initial = Directory.Exists(current) ? current : Path.GetDirectoryName(current);
        var folder = await _dialogs.PickFolderAsync(Loc.T("Extract.PickOutput"), initial);
        if (folder != null)
        {
            // Người dùng tự chọn thư mục → tắt "Tự đặt theo gói" để không bị ghi đè khi mở gói khác.
            AutoOutputFolder = false;
            OutputFolder = folder;
        }
    }

    [RelayCommand(CanExecute = nameof(CanBrowse))]
    private void Reload()
    {
        _loadedPath = null;
        StartLoad();
    }

    private bool CanRunExtract => HasPackage && !IsBusy && ((ExtractAppTree && CanExtract) || (ExtractCnt && CanExport) || (ExtractSi && HasSupplement) || ExtractOuter);

    [RelayCommand(CanExecute = nameof(CanRunExtract))]
    private async Task ExtractAsync()
    {
        if (_info is not { } info || IsBusy)
        {
            return;
        }

        var folder = OutputFolder.Trim();
        if (folder.Length == 0)
        {
            NoticeBanner = Loc.T("Extract.NoOutput");
            return;
        }

        var doApp = ExtractAppTree && CanExtract && _reader != null;
        var doOuter = ExtractOuter;
        var doCnt = ExtractCnt && CanExport;
        var doSi = ExtractSi && HasSupplement;
        if (!doApp && !doOuter && !doCnt && !doSi)
        {
            NoticeBanner = Loc.T("Extract.NothingToDo");
            return;
        }

        try
        {
            Directory.CreateDirectory(folder);
            if (Directory.EnumerateFileSystemEntries(folder).Any())
            {
                var proceed = await _dialogs.ConfirmAsync(
                    Loc.T("Extract.OverwriteTitle"),
                    Loc.F("Extract.OverwriteBody", folder),
                    Loc.T("Extract.OverwriteYes"),
                    Loc.T("Common.Cancel"));
                if (!proceed)
                {
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            ErrorBanner = ex.Message;
            return;
        }

        var reader = _reader;
        var selection = doApp ? _allItems.Where(i => i.IsSelected).Select(i => i.Entry).ToList() : new List<PackageEntry>();
        var entries = doApp ? (selection.Count > 0 ? selection : reader!.Entries) : Array.Empty<PackageEntry>();
        var appFiles = entries.Count(e => !e.IsDirectory);
        var appBytes = entries.Where(e => !e.IsDirectory).Sum(e => e.Size);
        var passcode = Passcode;
        var decompress = DecompressPfsc;
        var packagePath = info.Path;

        ErrorBanner = null;
        NoticeBanner = null;
        HasResult = false;
        IsExtracting = true;
        IsCanceling = false;
        IsIndeterminate = false;
        ProgressPercent = 0;
        PercentText = "0%";
        ProgressText = doApp ? Loc.F("Extract.Progress", 0, Formatters.Count(appFiles), Formatters.Size(0), Formatters.Size(appBytes)) : string.Empty;
        CurrentFile = string.Empty;
        StatusKind = StatusKind.Working;
        HeadlineText = Loc.T("Extract.HeadExtracting");
        SublineText = folder;
        _stopwatch.Restart();
        ElapsedText = "00:00";
        _tickTimer.Start();
        _workCancellation = new CancellationTokenSource();
        var token = _workCancellation.Token;
        SelectedTab = 1;
        Log(LogLevel.Success, Loc.F("Extract.LogStart", Formatters.Count(appFiles), Formatters.Size(appBytes), folder));

        var progress = new Progress<ExtractProgress>(p =>
        {
            var percent = p.TotalBytes <= 0 ? 100 : p.DoneBytes * 100.0 / p.TotalBytes;
            ProgressPercent = Math.Clamp(percent, 0, 100);
            PercentText = $"{ProgressPercent:0}%";
            ProgressText = Loc.F("Extract.Progress", Formatters.Count(p.DoneFiles), Formatters.Count(p.TotalFiles), Formatters.Size(p.DoneBytes), Formatters.Size(p.TotalBytes));
            CurrentFile = p.CurrentFile ?? string.Empty;
        });

        var parts = new List<string>();
        try
        {
            if (doApp)
            {
                CurrentFile = Loc.T("Extract.StepApp");
                var result = await Task.Run(() => reader!.Extract(entries, folder, progress, token, 0, decompress), token);
                foreach (var warning in result.Warnings)
                {
                    Log(LogLevel.Warning, warning);
                }

                parts.Add(Loc.F("Extract.PartApp", Formatters.Count(result.FileCount), Formatters.Size(result.TotalBytes)));
                ProgressPercent = 100;
                PercentText = "100%";

                // Bố cục Sony: ghép param.json / icon0.png / playgo… từ CNT vào sce_sys của cây ứng dụng để dùng lại làm nguồn tạo gói.
                if (CanExport)
                {
                    await RunStepAsync(Loc.T("Extract.StepSceSys"), () =>
                    {
                        var files = PackageReader.ExportSceSys(packagePath, folder, passcode, token);
                        parts.Add(Loc.F("Extract.PartSceSys", files.Count));
                        Log(LogLevel.Info, Loc.F("Extract.LogSceSys", files.Count, Path.Combine(folder, "sce_sys")));
                    }, token);
                }
            }

            if (doOuter)
            {
                await RunStepAsync(Loc.T("Extract.StepOuter"), () =>
                {
                    var files = PackageReader.ExportOuterFiles(packagePath, Path.Combine(folder, "outer"), passcode, decompress, token);
                    parts.Add(Loc.F("Extract.PartOuter", files.Count));
                    Log(LogLevel.Info, Loc.F("Extract.LogExport", files.Count, Path.Combine(folder, "outer")));
                }, token);
            }

            if (doCnt)
            {
                await RunStepAsync(Loc.T("Extract.StepCnt"), () =>
                {
                    var files = PackageReader.ExportCntEntries(packagePath, Path.Combine(folder, "cnt"), passcode, token);
                    parts.Add(Loc.F("Extract.PartCnt", files.Count));
                    Log(LogLevel.Info, Loc.F("Extract.LogExport", files.Count, Path.Combine(folder, "cnt")));
                }, token);
            }

            if (doSi)
            {
                await RunStepAsync(Loc.T("Extract.StepSi"), () =>
                {
                    var files = PackageReader.ExportSiEntries(packagePath, Path.Combine(folder, "si"), token);
                    parts.Add(Loc.F("Extract.PartSi", files.Count));
                    Log(LogLevel.Info, Loc.F("Extract.LogSi", files.Count, Path.Combine(folder, "si")));
                }, token);
            }

            _stopwatch.Stop();
            IsIndeterminate = false;
            ProgressPercent = 100;
            PercentText = "100%";
            CurrentFile = string.Empty;
            ResultTitle = Loc.F("Extract.ResultSummary", string.Join(" · ", parts), Formatters.Duration(_stopwatch.Elapsed));
            ResultFolder = folder;
            HasResult = true;
            StatusKind = StatusKind.Success;
            HeadlineText = Loc.T("Extract.HeadDone");
            SublineText = folder;
            Log(LogLevel.Success, Loc.F("Extract.LogDone", Formatters.Duration(_stopwatch.Elapsed), folder));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            NoticeBanner = Loc.T("Extract.Canceled");
            StatusKind = StatusKind.Canceled;
            HeadlineText = Loc.T("Extract.HeadCanceled");
            SublineText = folder;
            Log(LogLevel.Warning, Loc.T("Extract.Canceled"));
        }
        catch (Exception ex)
        {
            ErrorBanner = ex.Message;
            StatusKind = StatusKind.Error;
            HeadlineText = Loc.T("Extract.HeadFailed");
            SublineText = ex.Message;
            Log(LogLevel.Error, Loc.F("Extract.Failed", ex.Message));
        }
        finally
        {
            _stopwatch.Stop();
            _tickTimer.Stop();
            ElapsedText = Formatters.Clock(_stopwatch.Elapsed);
            IsIndeterminate = false;
            _workCancellation?.Dispose();
            _workCancellation = null;
            IsExtracting = false;
            IsCanceling = false;
            RefreshFreeSpace();
        }
    }

    private async Task RunStepAsync(string name, Action work, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        IsIndeterminate = true;
        CurrentFile = name;
        Log(LogLevel.Info, Loc.F("Extract.LogStep", name));
        await Task.Run(work, token);
        IsIndeterminate = false;
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void CancelWork()
    {
        if (_workCancellation is { IsCancellationRequested: false } cancellation)
        {
            IsCanceling = true;
            HeadlineText = Loc.T("Extract.StatusCanceling");
            cancellation.Cancel();
        }
    }

    [RelayCommand(CanExecute = nameof(CanOpenOutput))]
    private async Task OpenOutputFolderAsync()
    {
        var folder = string.IsNullOrEmpty(ResultFolder) || !Directory.Exists(ResultFolder) ? OutputFolder.Trim() : ResultFolder;
        if (!string.IsNullOrEmpty(folder) && !await _dialogs.OpenFolderAsync(folder))
        {
            await _dialogs.ShowErrorAsync(Loc.T("Build.OpenFailed"), folder);
        }
    }

    private bool CanCopyInfo => HasPackage;

    [RelayCommand(CanExecute = nameof(CanCopyInfo))]
    private async Task CopyInfoAsync()
    {
        if (_info is not { } info)
        {
            return;
        }

        try
        {
            await _dialogs.SetClipboardAsync(PackageInspector.Describe(info, Sha256));
            NoticeBanner = null;
            Log(LogLevel.Info, Loc.T("Extract.CopiedInfo"));
        }
        catch (Exception ex)
        {
            ErrorBanner = Loc.F("Build.CopyFailed", ex.Message);
        }
    }

    [RelayCommand(CanExecute = nameof(CanComputeSha))]
    private async Task ComputeSha256Async()
    {
        if (_info is not { } info || IsBusy)
        {
            return;
        }

        IsHashing = true;
        HashPercent = 0;
        _workCancellation = new CancellationTokenSource();
        var token = _workCancellation.Token;
        try
        {
            var sha = await Task.Run(() => PackageInspector.ComputeSha256(info.Path, token, percent => Dispatcher.UIThread.Post(() => HashPercent = percent)), token);
            Sha256 = sha;
            RenderInfo();
            Log(LogLevel.Info, Loc.F("Build.Sha", sha));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ErrorBanner = ex.Message;
        }
        finally
        {
            _workCancellation?.Dispose();
            _workCancellation = null;
            IsHashing = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanVerify))]
    private Task VerifyQuickAsync() => VerifyAsync(full: false);

    [RelayCommand(CanExecute = nameof(CanVerify))]
    private Task VerifyFullAsync() => VerifyAsync(full: true);

    /// <summary>
    /// Kiểm tra nội dung gói bằng engine 0.6.8: bản nhanh đọc metadata (chữ ký CNT, bố cục PlayGo, NAPS, inode, SI); bản đầy đủ
    /// còn giải mã, đối chiếu từng khối với imagedigs.dat và giải nén thử mọi tệp trong bộ nhớ. Không ghi gì ra đĩa.
    /// </summary>
    private async Task VerifyAsync(bool full)
    {
        if (_info is not { } info || IsBusy)
        {
            return;
        }

        IsVerifyingFull = full;
        VerifyPercent = 0;
        VerifyResult = null;
        IsVerifying = true;
        _workCancellation = new CancellationTokenSource();
        var token = _workCancellation.Token;
        var passcode = Passcode;
        Log(LogLevel.Info, Loc.T(full ? "Plan.VerifyingFull" : "Extract.VerifyingQuick"));
        try
        {
            var result = await Task.Run(
                () => PackageVerifier.VerifyContents(
                    info.Path,
                    passcode,
                    full,
                    token,
                    percent => Dispatcher.UIThread.Post(() => VerifyPercent = percent)),
                token);
            foreach (var check in result.Checks)
            {
                Log(LogLevel.Info, Loc.F("Plan.VerifyCheck", check));
            }

            foreach (var issue in result.Issues)
            {
                Log(LogLevel.Error, Loc.F("Plan.VerifyIssue", issue.Stage, issue.Message));
            }

            VerifyResult = result;
            Log(result.IsValid ? LogLevel.Success : LogLevel.Error, result.IsValid
                ? Loc.F(result.IsFull ? "Plan.VerifiedFull" : "Plan.VerifiedQuick", result.Checks.Count, Formatters.Duration(result.Elapsed))
                : Loc.F("Verify.ContentFailed", string.Join("; ", result.Issues)));
        }
        catch (OperationCanceledException)
        {
            Log(LogLevel.Warning, Loc.T("Extract.VerifyCanceled"));
        }
        catch (Exception ex)
        {
            ErrorBanner = ex.Message;
            Log(LogLevel.Error, ex.Message);
        }
        finally
        {
            _workCancellation?.Dispose();
            _workCancellation = null;
            IsVerifying = false;
        }
    }

    [RelayCommand]
    private void CancelVerify() => _workCancellation?.Cancel();

    partial void OnIsDlcWithDataChanged(bool value) => NotifyBusy();

    /// <summary>
    /// Xuất mẫu DLC (fpkg-gui 0.6.8 "Export DLC template") ra "&lt;gói&gt;-dlc-template" cạnh tệp .pkg: tệp sce_sys cần để đóng gói
    /// lại cùng tệp dự án .gp5. Bỏ dữ liệu DLC vào thư mục đó rồi chọn tệp .gp5 làm nguồn ở chế độ Tạo gói.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanExportDlcTemplate))]
    private async Task ExportDlcTemplateAsync()
    {
        if (_info is not { } info || IsBusy)
        {
            return;
        }

        var folder = PackageReader.SuggestDlcTemplateFolder(info.Path);
        var passcode = Passcode;
        IsVerifyingFull = true;
        VerifyPercent = 0;
        IsVerifying = true;
        _workCancellation = new CancellationTokenSource();
        var token = _workCancellation.Token;
        Log(LogLevel.Info, Loc.F("DlcTemplate.Exporting", folder));
        try
        {
            var files = await Task.Run(
                () => PackageReader.ExportDlcTemplate(info.Path, folder, passcode, token, percent => Dispatcher.UIThread.Post(() => VerifyPercent = percent)),
                token);
            foreach (var file in files)
            {
                Log(LogLevel.Info, "  " + file);
            }

            ResultTitle = Loc.F("DlcTemplate.Done", files.Count, info.ContentId + ".gp5");
            ResultFolder = folder;
            HasResult = true;
            Log(LogLevel.Success, Loc.F("DlcTemplate.Done", files.Count, info.ContentId + ".gp5") + " " + folder);
        }
        catch (OperationCanceledException)
        {
            Log(LogLevel.Warning, Loc.T("DlcTemplate.Canceled"));
            BuildEngine.TryDeleteDirectory(folder);
        }
        catch (Exception ex)
        {
            ErrorBanner = ex.Message;
            Log(LogLevel.Error, ex.Message);
        }
        finally
        {
            _workCancellation?.Dispose();
            _workCancellation = null;
            IsVerifying = false;
            IsVerifyingFull = false;
            OnPropertyChanged(nameof(CanOpenOutput));
            OpenOutputFolderCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand]
    private void DismissError() => ErrorBanner = null;

    [RelayCommand]
    private void DismissNotice() => NoticeBanner = null;

    [RelayCommand]
    private void DismissResult() => HasResult = false;

    [RelayCommand]
    private void ClearLog()
    {
        LogEntries.Clear();
        LogCount = 0;
    }

    // ===================== Nhật ký =====================

    /// <summary>Ghi vào nhật ký của chế độ giải nén và nhật ký chung của cửa sổ.</summary>
    private void Log(LogLevel level, string message)
    {
        var entry = new LogEntry(level, message);
        if (Dispatcher.UIThread.CheckAccess())
        {
            Append(entry);
        }
        else
        {
            Dispatcher.UIThread.Post(() => Append(entry));
        }

        _mainLog(level, message);
    }

    private void Append(LogEntry entry)
    {
        LogEntries.AddBatch([entry]);
        LogCount = LogEntries.Count;
    }
}
