using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PsViethoa.FpkgBuilder.App.Services;
using PsViethoa.FpkgBuilder.Core.Ftp;
using PsViethoa.FpkgBuilder.Core.Localization;
using PsViethoa.FpkgBuilder.Core.Models;
using PsViethoa.FpkgBuilder.Core.Services;

namespace PsViethoa.FpkgBuilder.App.ViewModels;

/// <summary>Màu chấm trạng thái ở thanh dưới trang FPKG trên PS5.</summary>
public enum RemoteStatusKind
{
    Idle,
    Info,
    Ok,
    Warn,
    Danger,
}

/// <summary>
/// Kết nối PS5 · FPKG trên PS5 (port từ PSVIETHOA STUDIO 2.1.0): quét các FPKG PS5 đã cài (bộ nhớ trong, ổ mở rộng, USB) qua FTP,
/// hiện tên game + icon + phiên bản; mở gói ngay trên máy (chỉ tải phần cần đọc), chọn tệp và trích về PC vào
/// "&lt;Tên game&gt; &lt;PPSAxxxxx&gt;". Chỉ đọc — không ghi gì lên PS5.
/// </summary>
public sealed partial class RemotePkgViewModel : ObservableObject
{
    /// <summary>Số kết nối FTP song song khi quét và khi giải nén (mỗi kênh NAPS một kết nối).</summary>
    private const int Channels = 4;

    private const int LogCapacity = 400;

    private readonly AppSettings _settings;
    private readonly DialogService _dialogs;
    private readonly DispatcherTimer _filterDebounce;
    private List<RemoteGameRow> _games = new();
    private List<RemoteFileRow> _rows = new();
    private PackageReader? _reader;
    private RemoteGameRow? _readerGame;
    private CancellationTokenSource? _work;
    private CancellationTokenSource? _session;
    private string _lastOutput = string.Empty;
    private bool _summaryQueued;
    private bool _selfTestStarted;
    private (int Count, long Bytes, string Seconds, string Host)? _lastScan;

    public RemotePkgViewModel(AppSettings settings, DialogService dialogs)
    {
        _settings = settings;
        _dialogs = dialogs;
        var last = settings.FtpHosts.OrderByDescending(h => h.LastUsed).FirstOrDefault();
        _host = last?.Host ?? string.Empty;
        _port = (last?.Port ?? Ps5Ftp.DefaultPort).ToString(CultureInfo.InvariantCulture);
        _autoFolder = settings.RemotePkgAutoFolder;
        _baseFolder = settings.RemotePkgBaseFolder ?? string.Empty;
        _outputFolder = settings.RemotePkgOutputFolder ?? string.Empty;
        _filterDebounce = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(300) };
        _filterDebounce.Tick += (_, _) =>
        {
            _filterDebounce.Stop();
            ApplyFileFilter();
        };
        Loc.Current.LanguageChanged += (_, _) => RefreshTexts();
        _statusText = Loc.T("Remote.Status.Idle");
        _scanInfo = Loc.T("Remote.Scan.Hint");
        RefreshOutput();
        _ = LoadDrivesAsync();
    }

    // ═══════════════════════════════ Kết nối + quét ═══════════════════════════════

    [ObservableProperty] private string _host;
    [ObservableProperty] private string _port;
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private string _scanInfo;

    public ObservableCollection<RemoteGameRow> VisibleGames { get; } = new();

    public bool HasGames => VisibleGames.Count > 0;

    partial void OnSearchTextChanged(string value) => RefreshGames();

    private FtpConnectionInfo? Connection()
    {
        var host = Host.Trim();
        if (host.Length == 0 || !int.TryParse(Port.Trim(), out var port) || port is <= 0 or > 65535)
        {
            SetStatus(Loc.T("Remote.Error.Host"), RemoteStatusKind.Danger);
            return null;
        }

        // Dùng tài khoản đã nhớ ở trang FTP PS5 cho đúng máy đó; ftpsrv mặc định nhận anonymous.
        var saved = _settings.FtpHosts.FirstOrDefault(h => h.Host.Equals(host, StringComparison.OrdinalIgnoreCase) && h.Port == port);
        var user = saved?.User is { Length: > 0 } u ? u : "anonymous";
        return new FtpConnectionInfo(host, port, user, string.Empty);
    }

    [RelayCommand(CanExecute = nameof(CanScan))]
    private async Task ScanAsync()
    {
        if (IsBusy || Connection() is not { } info)
        {
            return;
        }

        CloseReader();
        _work = new CancellationTokenSource();
        var ct = _work.Token;
        SetBusy(true);
        ScanInfo = Loc.F("Remote.Scan.Running", info.Host, info.Port);
        SetStatus(Loc.T("Remote.Status.Scanning"), RemoteStatusKind.Info);
        var clock = Stopwatch.StartNew();
        try
        {
            var report = new Progress<string>(m => ScanInfo = m);
            var found = await RemotePkgScanner.ScanAsync(info, report, ct, Channels);
            _games = found.Select(g => new RemoteGameRow(g)).ToList();
            var total = found.Sum(g => g.PkgSize);
            var seconds = clock.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.CurrentCulture);
            _lastScan = (found.Count, total, seconds, info.Host);
            ScanInfo = ScanSummary();
            Log(LogLevel.Success, Loc.F("Remote.Log.Scanned", info.Host, found.Count, seconds));
            SetStatus(found.Count == 0 ? Loc.T("Remote.Status.NoGames") : Loc.T("Remote.Status.PickGame"), found.Count == 0 ? RemoteStatusKind.Warn : RemoteStatusKind.Ok);
            RememberHost(info);
            RefreshGames();
            _ = LoadIconsAsync(info, _games.ToList());
        }
        catch (OperationCanceledException)
        {
            ScanInfo = Loc.T("Remote.Scan.Stopped");
            SetStatus(Loc.T("Remote.Status.Stopped"), RemoteStatusKind.Idle);
        }
        catch (Exception ex)
        {
            ScanInfo = Loc.F("Remote.Scan.Failed", ex.Message);
            SetStatus(Loc.F("Remote.Status.ScanFailed", ex.Message), RemoteStatusKind.Danger);
            Log(LogLevel.Error, Loc.F("Remote.Log.ScanFailed", ex.Message));
        }
        finally
        {
            _work?.Dispose();
            _work = null;
            SetBusy(false);
        }
    }

    private bool CanScan => !IsBusy;

    private string ScanSummary() => _lastScan is { } s ? Loc.F("Remote.Scan.Done", s.Count, Formatters.Size(s.Bytes), s.Seconds, s.Host) : Loc.T("Remote.Scan.Hint");

    /// <summary>Quét được thì nhớ máy này vào danh sách "Kết nối gần đây" chung với trang FTP PS5.</summary>
    private void RememberHost(FtpConnectionInfo info)
    {
        var existing = _settings.FtpHosts.FirstOrDefault(h => h.Host.Equals(info.Host, StringComparison.OrdinalIgnoreCase) && h.Port == info.Port);
        if (existing == null)
        {
            existing = new FtpHostSetting { Host = info.Host, Port = info.Port, User = info.User == "anonymous" ? string.Empty : info.User };
            _settings.FtpHosts.Add(existing);
        }

        existing.LastUsed = DateTime.Now;
        SettingsService.Save(_settings);
    }

    private static string IconCachePath(string titleId) => Path.Combine(SettingsService.Directory, "ps5icons", titleId + ".png");

    /// <summary>Icon tải dần sau khi đã có danh sách (một kết nối, đệm ở &lt;thư mục dữ liệu&gt;/ps5icons, giữ 30 ngày).</summary>
    private async Task LoadIconsAsync(FtpConnectionInfo info, List<RemoteGameRow> list)
    {
        Ps5Ftp? ftp = null;
        try
        {
            foreach (var row in list)
            {
                if (row.Game.IconPath.Length == 0 || row.Icon != null)
                {
                    continue;
                }

                var cache = IconCachePath(row.TitleId);
                byte[]? bytes = null;
                try
                {
                    if (File.Exists(cache) && (DateTime.Now - File.GetLastWriteTime(cache)).TotalDays < 30)
                    {
                        bytes = await File.ReadAllBytesAsync(cache);
                    }
                }
                catch (Exception)
                {
                }

                if (bytes == null)
                {
                    try
                    {
                        if (ftp == null)
                        {
                            ftp = new Ps5Ftp();
                            await ftp.ConnectAsync(info.Host, info.Port, info.User, info.Pass);
                        }

                        bytes = await ftp.ReadAllBytesAsync(row.Game.IconPath);
                        Directory.CreateDirectory(Path.GetDirectoryName(cache)!);
                        await File.WriteAllBytesAsync(cache, bytes);
                    }
                    catch (Exception)
                    {
                        ftp?.Dispose();
                        ftp = null;
                        continue;
                    }
                }

                var data = bytes;
                var bitmap = await Task.Run(() => Decode(data, 152));
                if (bitmap != null)
                {
                    row.Icon = bitmap;
                }
            }
        }
        finally
        {
            ftp?.Dispose();
        }
    }

    private static Bitmap? Decode(byte[] bytes, int width)
    {
        try
        {
            using var memory = new MemoryStream(bytes);
            return Bitmap.DecodeToWidth(memory, width);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void RefreshGames()
    {
        var q = SearchText.Trim();
        var keep = SelectedGame;
        VisibleGames.Clear();
        foreach (var row in _games)
        {
            var g = row.Game;
            if (q.Length == 0 || g.DisplayTitle.Contains(q, StringComparison.CurrentCultureIgnoreCase) || g.TitleId.Contains(q, StringComparison.OrdinalIgnoreCase)
                || g.OriginalName.Contains(q, StringComparison.OrdinalIgnoreCase) || g.ContentId.Contains(q, StringComparison.OrdinalIgnoreCase))
            {
                VisibleGames.Add(row);
            }
        }

        OnPropertyChanged(nameof(HasGames));
        if (keep != null && VisibleGames.Contains(keep))
        {
            SelectedGame = keep;
        }
    }

    // ═══════════════════════════════ Chi tiết + mở gói ═══════════════════════════════

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(HeroTitle), nameof(HeroSub), nameof(HeroTip), nameof(HeroVersion), nameof(HasHeroVersion),
        nameof(HeroSize), nameof(HeroLocation), nameof(HeroKind), nameof(HeroIsRetail))]
    private RemoteGameRow? _selectedGame;

    public bool HasSelection => SelectedGame != null;

    public string HeroTitle => SelectedGame?.Title ?? Loc.T("Remote.Hero.None");

    public string HeroSub => SelectedGame is { } row
        ? (row.Game.ContentId.Length > 0 ? row.Game.ContentId : row.TitleId) + "   ·   " + row.Game.PkgPath
        : Loc.T("Remote.Hero.NoneHint");

    public string HeroTip => SelectedGame is { } row
        ? row.Game.ContentId + "\n" + row.Game.PkgPath + (row.Game.OriginalName.Length > 0 ? "\n" + Loc.F("Remote.OriginalName", row.Game.OriginalName) : string.Empty)
        : string.Empty;

    public string HeroVersion => SelectedGame is { Game.Version.Length: > 0 } row ? "v" + row.Game.Version : string.Empty;

    public bool HasHeroVersion => HeroVersion.Length > 0;

    public string HeroSize => SelectedGame is { } row ? Formatters.Size(row.Game.PkgSize) : string.Empty;

    public string HeroLocation => SelectedGame?.LocationText ?? string.Empty;

    public string HeroKind => SelectedGame is { IsRetail: true } ? "Retail" : "FPKG";

    public bool HeroIsRetail => SelectedGame?.IsRetail == true;

    partial void OnSelectedGameChanging(RemoteGameRow? oldValue, RemoteGameRow? newValue)
    {
        if (newValue != _readerGame)
        {
            CloseReader();
        }
    }

    partial void OnSelectedGameChanged(RemoteGameRow? value)
    {
        RefreshOutput();
        RefreshFilesEmpty();
        OpenCommand.NotifyCanExecuteChanged();
    }

    public bool CanOpen => !IsBusy && SelectedGame is { IsRetail: false } && _reader == null;

    [RelayCommand(CanExecute = nameof(CanOpen))]
    private async Task OpenAsync()
    {
        if (!CanOpen || SelectedGame is not { } row || Connection() is not { } info)
        {
            return;
        }

        CloseReader();
        _work = new CancellationTokenSource();
        _session = new CancellationTokenSource();
        SetBusy(true);
        IsIndeterminate = true;
        SetStatus(Loc.F("Remote.Status.Opening", row.Game.PkgPath), RemoteStatusKind.Info);
        FilesEmptyText = Loc.T("Remote.Files.Reading");
        var clock = Stopwatch.StartNew();
        var before = FtpRangeStream.BytesDownloaded;
        try
        {
            var token = _session.Token;
            var game = row.Game;
            var opened = await Task.Run(() => RemotePkgScanner.OpenPackage(info, game, Channels, token), _work.Token);
            if (SelectedGame != row)
            {
                opened.Dispose();
                return;
            }

            _reader = opened;
            _readerGame = row;
            var list = await Task.Run(() =>
            {
                var files = opened.Entries.Where(e => !e.IsDirectory);
                var sorted = SortBySize ? files.OrderByDescending(e => e.Size) : files.OrderBy(e => e.Path, StringComparer.OrdinalIgnoreCase);
                return sorted.Select(e => new RemoteFileRow(e, this)).ToList();
            });
            _rows = list;
            OnPropertyChanged(nameof(HasReader));
            ApplyFileFilter();
            var mb = ((FtpRangeStream.BytesDownloaded - before) / 1e6).ToString("0.#", CultureInfo.CurrentCulture);
            var seconds = clock.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.CurrentCulture);
            Log(LogLevel.Success, Loc.F("Remote.Log.Opened", row.Title, Formatters.Count(opened.FileCount), Formatters.Size(opened.TotalBytes), seconds, mb));
            SetStatus(Loc.F("Remote.Status.Opened", Formatters.Count(opened.FileCount), Formatters.Size(opened.TotalBytes)), RemoteStatusKind.Ok);
        }
        catch (OperationCanceledException)
        {
            CloseReader();
            SetStatus(Loc.T("Remote.Status.Stopped"), RemoteStatusKind.Idle);
        }
        catch (Exception ex)
        {
            CloseReader();
            SetStatus(Loc.F("Remote.Status.OpenFailed", ex.Message), RemoteStatusKind.Danger);
            Log(LogLevel.Error, Loc.F("Remote.Log.OpenFailed", ex.Message));
        }
        finally
        {
            IsIndeterminate = false;
            _work?.Dispose();
            _work = null;
            SetBusy(false);
        }
    }

    private void CloseReader()
    {
        _session?.Cancel();
        _session?.Dispose();
        _session = null;
        var reader = _reader;
        _reader = null;
        _readerGame = null;
        try
        {
            reader?.Dispose();
        }
        catch (Exception)
        {
        }

        _rows = new List<RemoteFileRow>();
        VisibleFiles = Array.Empty<RemoteFileRow>();
        OnPropertyChanged(nameof(HasReader));
        RefreshFilesEmpty();
        QueueSummary();
        OpenCommand.NotifyCanExecuteChanged();
        ExtractCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Gọi khi đóng ứng dụng: dừng việc đang chạy và đóng các kết nối FTP của gói đang mở.</summary>
    public void Shutdown()
    {
        _work?.Cancel();
        CloseReader();
    }

    // ── Mở rộng (ẩn cột game) ──

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExpandText))]
    private bool _isExpanded;

    public string ExpandText => Loc.T(IsExpanded ? "Remote.Collapse" : "Remote.Expand");

    [RelayCommand]
    private void ToggleExpand() => IsExpanded = !IsExpanded;

    // ═══════════════════════════════ Danh sách tệp ═══════════════════════════════

    [ObservableProperty] private IReadOnlyList<RemoteFileRow> _visibleFiles = Array.Empty<RemoteFileRow>();
    [ObservableProperty] private string _fileFilter = string.Empty;
    [ObservableProperty] private string _filesEmptyText = string.Empty;
    [ObservableProperty] private string _fileSummary = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SortText))]
    private bool _sortBySize;

    public bool HasReader => _reader != null;

    public bool HasVisibleFiles => VisibleFiles.Count > 0;

    public string SortText => Loc.T(SortBySize ? "Remote.Sort.Size" : "Remote.Sort.Name");

    partial void OnVisibleFilesChanged(IReadOnlyList<RemoteFileRow> value) => OnPropertyChanged(nameof(HasVisibleFiles));

    partial void OnFileFilterChanged(string value)
    {
        _filterDebounce.Stop();
        _filterDebounce.Start();
    }

    /// <summary>Enter trong ô lọc: lọc ngay, không chờ.</summary>
    public void ApplyFileFilter()
    {
        _filterDebounce.Stop();
        if (_reader == null)
        {
            return;
        }

        var shown = Filtered().ToList();
        VisibleFiles = shown;
        FilesEmptyText = Loc.T("Remote.Files.NoMatch");
        QueueSummary();
    }

    private IEnumerable<RemoteFileRow> Filtered()
    {
        var q = FileFilter.Trim();
        return q.Length == 0 ? _rows : _rows.Where(r => r.Path.Contains(q, StringComparison.OrdinalIgnoreCase));
    }

    private void RefreshFilesEmpty()
    {
        if (_reader != null)
        {
            return;
        }

        FilesEmptyText = SelectedGame == null ? Loc.T("Remote.Files.NoGame")
            : SelectedGame.IsRetail ? Loc.T("Remote.Files.Retail")
            : Loc.T("Remote.Files.PressOpen");
    }

    [RelayCommand]
    private async Task CheckFilteredAsync()
    {
        var shown = Filtered().ToList();
        if (shown.Count > 5000)
        {
            var bytes = Formatters.Size(shown.Sum(r => r.Entry.Size));
            if (!await _dialogs.ConfirmAsync(Loc.T("Remote.CheckMany.Title"), Loc.F("Remote.CheckMany.Body", Formatters.Count(shown.Count), bytes), Loc.T("Remote.CheckMany.Yes"), Loc.T("Common.Cancel")))
            {
                return;
            }
        }

        foreach (var row in shown)
        {
            row.SetCheckedQuiet(true);
        }

        QueueSummary();
    }

    [RelayCommand]
    private void UncheckAll()
    {
        foreach (var row in _rows)
        {
            row.SetCheckedQuiet(false);
        }

        QueueSummary();
    }

    [RelayCommand]
    private void ToggleSort()
    {
        SortBySize = !SortBySize;
        _rows = SortBySize
            ? _rows.OrderByDescending(r => r.Entry.Size).ToList()
            : _rows.OrderBy(r => r.Path, StringComparer.OrdinalIgnoreCase).ToList();
        ApplyFileFilter();
    }

    /// <summary>Space / nháy đúp: đảo ô tích của các dòng đang chọn (có dòng chưa tích thì tích hết).</summary>
    public void ToggleChecked(IReadOnlyList<RemoteFileRow> rows)
    {
        if (rows.Count == 0)
        {
            return;
        }

        var on = rows.Any(r => !r.IsChecked);
        foreach (var row in rows)
        {
            row.SetCheckedQuiet(on);
        }

        QueueSummary();
    }

    internal void OnFileCheckedChanged() => QueueSummary();

    /// <summary>Đếm lại số tệp đã chọn một lần cho cả loạt thay đổi (tích 240 000 dòng không đếm 240 000 lần).</summary>
    private void QueueSummary()
    {
        if (_summaryQueued)
        {
            return;
        }

        _summaryQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _summaryQueued = false;
            if (_reader == null)
            {
                FileSummary = string.Empty;
            }
            else
            {
                long bytes = 0;
                var count = 0;
                foreach (var row in _rows)
                {
                    if (row.IsChecked)
                    {
                        count++;
                        bytes += row.Entry.Size;
                    }
                }

                FileSummary = Loc.F("Remote.Files.Summary", Formatters.Count(count), Formatters.Size(bytes), Formatters.Count(VisibleFiles.Count), Formatters.Count(_rows.Count), Formatters.Size(_reader.TotalBytes));
            }

            ExtractCommand.NotifyCanExecuteChanged();
        }, DispatcherPriority.Background);
    }

    // ═══════════════════════════════ Thư mục xuất ═══════════════════════════════

    private string _baseFolder;

    public ObservableCollection<DriveChoice> Drives { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditOutput))]
    private bool _autoFolder;

    [ObservableProperty] private string _outputFolder;
    [ObservableProperty] private string _outputPlanText = string.Empty;

    public bool CanEditOutput => !AutoFolder && !IsBusy;

    /// <summary>Ổ / thư mục gốc; chưa chọn thì Màn hình nền.</summary>
    public string BaseFolder => _baseFolder.Length > 0 ? _baseFolder : Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

    private string GameFolder(RemoteGameRow row) => GameFolderName.For(row.Game.Title.Length > 0 ? row.Game.Title : row.Title, row.TitleId);

    partial void OnAutoFolderChanged(bool value)
    {
        _settings.RemotePkgAutoFolder = value;
        RefreshOutput();
    }

    partial void OnOutputFolderChanged(string value)
    {
        if (!AutoFolder)
        {
            _settings.RemotePkgOutputFolder = value.Trim();
        }
    }

    [RelayCommand]
    private void PickDrive(DriveChoice? drive)
    {
        if (drive != null && !IsBusy)
        {
            UseBase(drive.Root);
        }
    }

    [RelayCommand]
    private Task RefreshDrivesAsync() => LoadDrivesAsync();

    private async Task LoadDrivesAsync()
    {
        var found = await DriveChoice.ListAsync();
        Drives.Clear();
        foreach (var drive in found)
        {
            Drives.Add(drive);
        }

        SyncDrives();
    }

    private void SyncDrives()
    {
        foreach (var drive in Drives)
        {
            drive.IsSelected = AutoFolder && drive.Matches(_baseFolder);
        }
    }

    /// <summary>Đặt gốc và bật lại tự tạo thư mục "&lt;Tên game&gt; &lt;ID&gt;".</summary>
    public void UseBase(string folder)
    {
        _baseFolder = folder.Trim();
        _settings.RemotePkgBaseFolder = _baseFolder;
        if (!AutoFolder)
        {
            AutoFolder = true; // OnAutoFolderChanged → RefreshOutput
        }
        else
        {
            RefreshOutput();
        }

        SettingsService.Save(_settings);
    }

    private void RefreshOutput()
    {
        if (AutoFolder)
        {
            OutputFolder = SelectedGame == null ? BaseFolder : Path.Combine(BaseFolder, GameFolder(SelectedGame));
            OutputPlanText = SelectedGame == null
                ? Loc.F("Remote.Out.PlanNoGame", BaseFolder)
                : Loc.F("Remote.Out.Plan", GameFolder(SelectedGame), BaseFolder);
        }
        else
        {
            if (OutputFolder.Trim().Length == 0)
            {
                OutputFolder = _settings.RemotePkgOutputFolder is { Length: > 0 } manual ? manual : BaseFolder;
            }

            OutputPlanText = Loc.T("Remote.Out.PlanDirect");
        }

        SyncDrives();
    }

    [RelayCommand]
    private async Task BrowseOutputAsync()
    {
        if (IsBusy)
        {
            return;
        }

        var current = OutputFolder.Trim();
        var folder = await _dialogs.PickFolderAsync(Loc.T("Remote.Out.Pick"), Directory.Exists(current) ? current : BaseFolder);
        if (folder == null)
        {
            return;
        }

        if (AutoFolder)
        {
            // Chọn trúng đúng thư mục game đó thì lấy thư mục cha làm gốc (không lồng hai lần).
            var picked = folder.Length > 1 ? folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : folder;
            if (SelectedGame != null && string.Equals(Path.GetFileName(picked), GameFolder(SelectedGame), StringComparison.OrdinalIgnoreCase))
            {
                picked = Path.GetDirectoryName(picked) ?? picked;
            }

            if (OperatingSystem.IsWindows() && picked.Length == 2 && picked[1] == ':')
            {
                picked += Path.DirectorySeparatorChar;
            }

            UseBase(picked);
        }
        else
        {
            OutputFolder = folder;
        }
    }

    [RelayCommand]
    private async Task OpenOutputAsync()
    {
        var target = Directory.Exists(_lastOutput) ? _lastOutput : Directory.Exists(OutputFolder.Trim()) ? OutputFolder.Trim() : BaseFolder;
        if (!await _dialogs.OpenFolderAsync(target))
        {
            await _dialogs.ShowErrorAsync(Loc.T("Build.OpenFailed"), target);
        }
    }

    // ═══════════════════════════════ Trích ═══════════════════════════════

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ListOpacity), nameof(CanEditOutput), nameof(IsIdle))]
    private bool _isBusy;

    [ObservableProperty] private bool _isIndeterminate;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _statusText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStatusOk), nameof(IsStatusInfo), nameof(IsStatusWarn), nameof(IsStatusDanger))]
    private RemoteStatusKind _statusKind = RemoteStatusKind.Idle;

    public bool IsIdle => !IsBusy;

    /// <summary>Đang bận: danh sách mờ đi (không dùng IsEnabled để nền danh sách không đổi màu).</summary>
    public double ListOpacity => IsBusy ? 0.6 : 1;

    public bool IsStatusOk => StatusKind == RemoteStatusKind.Ok;

    public bool IsStatusInfo => StatusKind == RemoteStatusKind.Info;

    public bool IsStatusWarn => StatusKind == RemoteStatusKind.Warn;

    public bool IsStatusDanger => StatusKind == RemoteStatusKind.Danger;

    public LogCollection LogEntries { get; } = new();

    private bool CanExtract => !IsBusy && _reader != null && _rows.Any(r => r.IsChecked);

    [RelayCommand(CanExecute = nameof(CanExtract))]
    private async Task ExtractAsync()
    {
        if (IsBusy || _reader == null || SelectedGame is not { } row)
        {
            return;
        }

        var picked = _rows.Where(r => r.IsChecked).Select(r => r.Entry).ToList();
        if (picked.Count == 0)
        {
            SetStatus(Loc.T("Remote.Status.NothingChecked"), RemoteStatusKind.Warn);
            return;
        }

        var folder = OutputFolder.Trim();
        if (folder.Length == 0)
        {
            SetStatus(Loc.T("Remote.Status.NoOutput"), RemoteStatusKind.Warn);
            return;
        }

        var bytes = picked.Sum(e => e.Size);
        try
        {
            Directory.CreateDirectory(folder);
            if (DiskSpaceAdvisor.Probe(folder) is { } volume && volume.FreeBytes < bytes + (256L << 20))
            {
                SetStatus(Loc.F("Remote.Status.NoSpace", volume.MountPoint, Formatters.Size(volume.FreeBytes), Formatters.Size(bytes)), RemoteStatusKind.Danger);
                return;
            }
        }
        catch (Exception ex)
        {
            SetStatus(Loc.F("Remote.Status.FolderFailed", ex.Message), RemoteStatusKind.Danger);
            return;
        }

        _work = new CancellationTokenSource();
        var ct = _work.Token;
        SetBusy(true);
        Progress = 0;
        var clock = Stopwatch.StartNew();
        var before = FtpRangeStream.BytesDownloaded;
        Log(LogLevel.Info, Loc.F("Remote.Log.Extracting", Formatters.Count(picked.Count), Formatters.Size(bytes), row.Title, folder));
        var report = new Progress<ExtractProgress>(p =>
        {
            var pct = p.TotalBytes <= 0 ? 100 : p.DoneBytes * 100.0 / p.TotalBytes;
            Progress = Math.Clamp(pct, 0, 100);
            var sec = Math.Max(clock.Elapsed.TotalSeconds, 0.001);
            var net = (FtpRangeStream.BytesDownloaded - before) / sec;
            SetStatus(Loc.F("Remote.Status.Extracting", pct.ToString("0", CultureInfo.CurrentCulture), Formatters.Count(p.DoneFiles), Formatters.Count(p.TotalFiles),
                Formatters.Size(p.DoneBytes), Formatters.Size(p.TotalBytes), Formatters.Rate(net), p.CurrentFile ?? string.Empty), RemoteStatusKind.Info);
        });
        var reader = _reader;
        try
        {
            var result = await Task.Run(() => reader.Extract(picked, folder, report, ct, 0, true), ct);
            foreach (var warning in result.Warnings)
            {
                Log(LogLevel.Warning, warning);
            }

            _lastOutput = folder;
            Progress = 100;
            var sec = Math.Max(clock.Elapsed.TotalSeconds, 0.001);
            var mb = ((FtpRangeStream.BytesDownloaded - before) / 1e6).ToString("0.#", CultureInfo.CurrentCulture);
            SetStatus(Loc.F("Remote.Status.Done", Formatters.Count(result.FileCount), Formatters.Size(result.TotalBytes), Formatters.Duration(clock.Elapsed),
                Formatters.Rate(result.TotalBytes / sec), folder), RemoteStatusKind.Ok);
            Log(LogLevel.Success, Loc.F("Remote.Log.Done", Formatters.Count(result.FileCount), Formatters.Size(result.TotalBytes),
                clock.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.CurrentCulture), mb));
        }
        catch (OperationCanceledException)
        {
            SetStatus(Loc.T("Remote.Status.ExtractStopped"), RemoteStatusKind.Warn);
            Log(LogLevel.Warning, Loc.T("Remote.Status.ExtractStopped"));
        }
        catch (Exception ex)
        {
            SetStatus(Loc.F("Remote.Status.ExtractFailed", ex.Message), RemoteStatusKind.Danger);
            Log(LogLevel.Error, Loc.F("Remote.Log.ExtractFailed", ex.Message));
        }
        finally
        {
            _work?.Dispose();
            _work = null;
            SetBusy(false);
        }
    }

    [RelayCommand(CanExecute = nameof(IsBusy))]
    private void Cancel() => _work?.Cancel();

    // ═══════════════════════════════ Trạng thái ═══════════════════════════════

    private void SetBusy(bool busy)
    {
        IsBusy = busy;
        ScanCommand.NotifyCanExecuteChanged();
        OpenCommand.NotifyCanExecuteChanged();
        ExtractCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
    }

    private void SetStatus(string text, RemoteStatusKind kind)
    {
        StatusText = text;
        StatusKind = kind;
    }

    private void Log(LogLevel level, string message)
    {
        LogEntries.Add(new LogEntry(level, message));
        if (LogEntries.Count > LogCapacity)
        {
            LogEntries.RemoveAt(0);
        }
    }

    private void RefreshTexts()
    {
        foreach (var row in _games)
        {
            row.RefreshTexts();
        }

        OnPropertyChanged(nameof(HeroTitle));
        OnPropertyChanged(nameof(HeroSub));
        OnPropertyChanged(nameof(HeroTip));
        OnPropertyChanged(nameof(HeroLocation));
        OnPropertyChanged(nameof(ExpandText));
        OnPropertyChanged(nameof(SortText));
        RefreshOutput();
        RefreshFilesEmpty();
        QueueSummary();
        if (!IsBusy)
        {
            ScanInfo = ScanSummary();
            if (_games.Count == 0)
            {
                StatusText = Loc.T("Remote.Status.Idle");
            }
        }

        _ = LoadDrivesAsync();
    }

    // ═══════════════════════════════ Kiểm thử ═══════════════════════════════

    /// <summary>
    /// Kiểm thử tự chạy: PSVIETHOA_REMOTEPKG_TEST="TID|đường dẫn tệp trong gói|thư mục gốc xuất|tệp kết quả[|host[:port]]" →
    /// quét → chọn game → mở gói → tích tệp → trích, ghi một dòng kết quả (OK/MISSING/FAIL + số liệu) vào tệp kết quả.
    /// </summary>
    public async Task RunSelfTestIfRequestedAsync()
    {
        if (_selfTestStarted || Environment.GetEnvironmentVariable("PSVIETHOA_REMOTEPKG_TEST") is not { Length: > 0 } test)
        {
            return;
        }

        var parts = test.Split('|');
        if (parts.Length < 4)
        {
            return;
        }

        _selfTestStarted = true;
        string outcome;
        var timing = new List<string>();
        try
        {
            if (parts.Length >= 5 && parts[4].Length > 0)
            {
                var hostPort = parts[4].Split(':');
                Host = hostPort[0];
                Port = hostPort.Length > 1 ? hostPort[1] : Ps5Ftp.DefaultPort.ToString(CultureInfo.InvariantCulture);
            }

            var clock = Stopwatch.StartNew();
            await ScanAsync();
            timing.Add($"scan={clock.Elapsed.TotalSeconds:0.00}s");
            var row = _games.FirstOrDefault(g => g.TitleId.Equals(parts[0], StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidOperationException("không thấy " + parts[0]);
            SelectedGame = row;
            UseBase(parts[2]);
            clock.Restart();
            await OpenAsync();
            timing.Add($"open={clock.Elapsed.TotalSeconds:0.00}s");
            if (_reader == null)
            {
                throw new InvalidOperationException("mở gói lỗi: " + StatusText);
            }

            var file = parts[1].TrimStart('/');
            FileFilter = file;
            ApplyFileFilter();
            var target = _rows.FirstOrDefault(r => r.Path.TrimStart('/').Equals(file, StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidOperationException("không thấy tệp " + file);
            target.IsChecked = true;
            await Task.Delay(800);
            clock.Restart();
            await ExtractAsync();
            timing.Add($"extract={clock.Elapsed.TotalSeconds:0.00}s");
            var path = Path.Combine(OutputFolder, file.Replace('/', Path.DirectorySeparatorChar));
            FileFilter = string.Empty;
            ApplyFileFilter();
            var head = File.Exists(path) ? Convert.ToHexString(File.ReadAllBytes(path).AsSpan(0, 4)) : string.Empty;
            outcome = File.Exists(path)
                ? $"OK {path} {new FileInfo(path).Length} head={head} | games={_games.Count} files={_rows.Count} | {string.Join(' ', timing)} | {StatusText}"
                : $"MISSING {path} | {StatusText}";
        }
        catch (Exception ex)
        {
            outcome = "FAIL " + ex.Message;
        }

        try
        {
            await File.WriteAllTextAsync(parts[3], outcome);
        }
        catch (Exception)
        {
        }
    }
}
