using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PsViethoa.FpkgBuilder.App.Services;
using PsViethoa.FpkgBuilder.Core.Ftp;
using PsViethoa.FpkgBuilder.Core.Localization;
using PsViethoa.FpkgBuilder.Core.Services;

namespace PsViethoa.FpkgBuilder.App.ViewModels;

/// <summary>Một dòng trong khung duyệt (máy tính hoặc PS5).</summary>
public sealed partial class FtpFileRow : ObservableObject
{
    public FtpFileRow(string name, string fullPath, bool isDirectory, long size, DateTime? modified, string kind, bool isParent = false, bool isLink = false)
    {
        Name = name;
        FullPath = fullPath;
        IsDirectory = isDirectory;
        Size = size;
        Modified = modified;
        Kind = kind;
        IsParent = isParent;
        IsLink = isLink;
    }

    public string Name { get; }

    public string FullPath { get; }

    public bool IsDirectory { get; }

    public bool IsParent { get; }

    public bool IsLink { get; }

    public long Size { get; }

    public DateTime? Modified { get; }

    /// <summary>Máy tính: đuôi tệp / "Thư mục"; PS5: quyền dạng drwxr-xr-x.</summary>
    public string Kind { get; }

    public string SizeText => IsDirectory ? string.Empty : Formatters.Size(Size);

    public string ModifiedText => Modified?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? string.Empty;

    public Geometry? Icon =>
        Avalonia.Application.Current?.TryGetResource(IsParent ? "IconArrowUp" : IsDirectory ? "IconFolder" : IsLink ? "IconLink" : "IconFile", null, out var value) == true
            ? value as Geometry
            : null;
}

/// <summary>Một tệp trong hàng đợi truyền; cập nhật từ <see cref="FtpTransferItem"/> theo nhịp hẹn giờ (luồng giao diện).</summary>
public sealed partial class FtpQueueRow : ObservableObject
{
    public FtpQueueRow(FtpTransferItem item)
    {
        Item = item;
        Refresh();
    }

    public FtpTransferItem Item { get; }

    public string Name => Item.IsDirectoryMarker ? Item.Name + "/" : Item.Name;

    public bool IsUpload => Item.Direction == FtpDirection.Upload;

    public string DirectionText => IsUpload ? "PC → PS5" : "PS5 → PC";

    public string Target => IsUpload ? Item.RemotePath : Item.LocalPath;

    [ObservableProperty] private double _percent;
    [ObservableProperty] private string _transferredText = string.Empty;
    [ObservableProperty] private string _speedText = string.Empty;
    [ObservableProperty] private string _etaText = string.Empty;
    [ObservableProperty] private string _stateText = string.Empty;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _isFailed;
    [ObservableProperty] private bool _isDone;
    [ObservableProperty] private string _error = string.Empty;

    public FtpItemState LastState { get; private set; } = (FtpItemState)(-1);

    public void Refresh()
    {
        var item = Item;
        var state = item.State;
        Percent = item.Percent;
        TransferredText = item.IsDirectoryMarker ? string.Empty : $"{Formatters.Size(Math.Max(0, item.Transferred))} / {Formatters.Size(Math.Max(0, item.Size))}";
        SpeedText = state == FtpItemState.Running && item.Speed > 0 ? Formatters.Rate(item.Speed) : string.Empty;
        EtaText = state switch
        {
            FtpItemState.Running when item.Speed > 1 && item.Size > item.Transferred => FtpViewModel.Duration((item.Size - item.Transferred) / item.Speed),
            FtpItemState.Queued when item.Estimate >= 0 => "~" + FtpViewModel.Duration(item.Estimate),
            _ => string.Empty,
        };
        if (state != LastState)
        {
            StateText = StateLabel(state, item.Segments);
            IsRunning = state == FtpItemState.Running;
            IsFailed = state is FtpItemState.Failed or FtpItemState.Cancelled;
            IsDone = state is FtpItemState.Done or FtpItemState.Skipped;
            LastState = state;
        }
        else if (state == FtpItemState.Running)
        {
            StateText = StateLabel(state, item.Segments);
        }

        // Lỗi "Thử lại (1/3)…" chỉ có ý nghĩa khi tệp đã thất bại hoặc bị dừng.
        Error = IsFailed ? item.Error : string.Empty;
    }

    public void RefreshTexts()
    {
        StateText = StateLabel(Item.State, Item.Segments);
    }

    private static string StateLabel(FtpItemState state, int segments) => state switch
    {
        FtpItemState.Queued => Loc.T("Ftp.State.Queued"),
        FtpItemState.Running => segments > 1 ? Loc.F("Ftp.State.RunningSegments", segments) : Loc.T("Ftp.State.Running"),
        FtpItemState.Done => Loc.T("Ftp.State.Done"),
        FtpItemState.Failed => Loc.T("Ftp.State.Failed"),
        FtpItemState.Skipped => Loc.T("Ftp.State.Skipped"),
        FtpItemState.Cancelled => Loc.T("Ftp.State.Cancelled"),
        _ => state.ToString(),
    };
}

/// <summary>Một máy PS5 đã kết nối (danh sách "Kết nối gần đây").</summary>
public sealed record FtpRecentHost(string Host, int Port, string User, DateTime LastUsed)
{
    public string Title => $"{Host}:{Port}";

    public string Subtitle => (User.Length > 0 ? User : "anonymous") + " · " + LastUsed.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
}

/// <summary>Chọn một cách xử lý khi tệp đích đã có.</summary>
public sealed record FtpExistsOption(FtpExistsPolicy Policy, string Label);

/// <summary>
/// Trang FTP PS5: trình FTP hai khung kiểu FileZilla cho ftpsrv trên PS5 (cổng 2121). Duyệt máy tính và PS5, tải lên / tải về đa luồng
/// (tự tăng luồng cho nhiều tệp nhỏ, chia đoạn tệp lớn khi tải về, tiếp tục phần dở), kéo thả qua lại giữa hai khung hoặc từ
/// Finder/Explorer, đổi tên, xoá (cả cây thư mục), tạo thư mục. Lõi FTP chuyển từ PSVIETHOA STUDIO (Ngô Phi Phương).
/// </summary>
public sealed partial class FtpViewModel : ObservableObject
{
    public const int VisiblePendingLimit = 1500;
    public const int VisibleFinishedLimit = 2000;

    public static readonly string[] RemoteQuickPaths = ["/", "/data", "/mnt/usb0", "/mnt/usb1", "/mnt/ext0", "/mnt/ext1", "/user/app", "/user/home", "/user/download"];

    private readonly AppSettings _settings;
    private readonly DialogService _dialogs;
    private readonly SemaphoreSlim _browserGate = new(1, 1);
    private readonly ConcurrentQueue<FtpLogLine> _pendingLog = new();
    private readonly ConcurrentQueue<FtpTransferItem> _added = new();
    private readonly List<FtpQueueRow> _overflow = new();
    private readonly List<FtpLocalRowSource> _localAll = new();
    private readonly List<FtpFileRow> _remoteAll = new();
    private readonly DispatcherTimer _tick;
    private readonly DispatcherTimer _keepAlive;
    private readonly FtpEtaModel _eta = new();
    private readonly Stopwatch _runClock = new();
    private Ps5Ftp? _browser;
    private FtpTransferQueue? _queue;
    private FtpConnectionInfo? _connection;
    private bool _loaded;
    private int _tickCount;
    private long _lastBytes;
    private int _lastFinished;
    private double _lastSample;
    private double _smoothedSpeed;
    private double _peakSpeed;
    private double _workerSum;
    private int _workerSamples;

    private sealed record FtpLocalRowSource(FtpFileRow Row);

    public FtpViewModel(AppSettings settings, DialogService dialogs)
    {
        _settings = settings;
        _dialogs = dialogs;
        var last = settings.FtpHosts.OrderByDescending(h => h.LastUsed).FirstOrDefault();
        _host = last?.Host ?? string.Empty;
        _port = (last?.Port ?? Ps5Ftp.DefaultPort).ToString(CultureInfo.InvariantCulture);
        _user = last?.User ?? string.Empty;
        _parallelFiles = Math.Clamp(settings.FtpParallelFiles, 1, 8);
        _threadsPerFile = Math.Clamp(settings.FtpThreadsPerFile, 1, 8);
        _autoThreads = settings.FtpAutoThreads;
        _statusText = Loc.T("Ftp.Status.Disconnected");
        _remoteStatus = Loc.T("Ftp.Status.Disconnected");
        _tick = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => Tick());
        _keepAlive = new DispatcherTimer(TimeSpan.FromSeconds(30), DispatcherPriority.Background, async (_, _) => await KeepAliveAsync());
        BuildExistsOptions();
        SelectedExistsOption = ExistsOptions.FirstOrDefault(o => o.Policy.ToString() == settings.FtpExistsPolicy) ?? ExistsOptions[0];
        LoadRecent();
        Loc.Current.LanguageChanged += (_, _) => RefreshTexts();
    }

    // ── Kết nối ─────────────────────────────────────────────────────────────────────────────────────────────────────
    [ObservableProperty] private string _host;
    [ObservableProperty] private string _port;
    [ObservableProperty] private string _user;
    [ObservableProperty] private string _password = string.Empty;
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private bool _isConnecting;
    [ObservableProperty] private string _statusText;

    // ── Khung máy tính ──────────────────────────────────────────────────────────────────────────────────────────────
    [ObservableProperty] private string _localPath = string.Empty;
    [ObservableProperty] private string _localFilter = string.Empty;
    [ObservableProperty] private string _localStatus = string.Empty;

    // ── Khung PS5 ───────────────────────────────────────────────────────────────────────────────────────────────────
    [ObservableProperty] private string _remotePath = "/";
    [ObservableProperty] private string _remoteFilter = string.Empty;
    [ObservableProperty] private string _remoteStatus;
    [ObservableProperty] private bool _remoteLoading;

    // ── Hàng đợi ────────────────────────────────────────────────────────────────────────────────────────────────────
    [ObservableProperty] private int _parallelFiles;
    [ObservableProperty] private int _threadsPerFile;
    [ObservableProperty] private bool _autoThreads;
    [ObservableProperty] private FtpExistsOption? _selectedExistsOption;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private int _queueTab;
    [ObservableProperty] private string _speedText = "0 KB/s";
    [ObservableProperty] private string _peakText = string.Empty;
    [ObservableProperty] private string _remainingText = "—";
    [ObservableProperty] private string _finishText = string.Empty;
    [ObservableProperty] private string _filesText = "0 / 0";
    [ObservableProperty] private string _bytesText = "0 B / 0 B";
    [ObservableProperty] private string _threadsText = "0";
    [ObservableProperty] private string _threadsReason = string.Empty;
    [ObservableProperty] private double _totalPercent;
    [ObservableProperty] private string _overflowText = string.Empty;
    [ObservableProperty] private bool _showLog;

    public ObservableCollection<FtpFileRow> LocalRows { get; } = new();

    public ObservableCollection<FtpFileRow> RemoteRows { get; } = new();

    public ObservableCollection<FtpFileRow> SelectedLocal { get; } = new();

    public ObservableCollection<FtpFileRow> SelectedRemote { get; } = new();

    public ObservableCollection<FtpQueueRow> PendingRows { get; } = new();

    public ObservableCollection<FtpQueueRow> FailedRows { get; } = new();

    public ObservableCollection<FtpQueueRow> DoneRows { get; } = new();

    public ObservableCollection<FtpRecentHost> RecentHosts { get; } = new();

    public ObservableCollection<string> LogLines { get; } = new();

    public ObservableCollection<FtpExistsOption> ExistsOptions { get; } = new();

    public ObservableCollection<string> LocalQuickPaths { get; } = new();

    public IReadOnlyList<string> RemoteQuick => RemoteQuickPaths;

    public bool ShowRecent => !IsConnected;

    public bool HasRecent => RecentHosts.Count > 0;

    public bool ShowPending => QueueTab == 0;

    public bool ShowFailed => QueueTab == 1;

    public bool ShowDone => QueueTab == 2;

    public string PendingTabText => Loc.F("Ftp.Tab.Pending", PendingRows.Count + _overflow.Count);

    public string FailedTabText => Loc.F("Ftp.Tab.Failed", FailedRows.Count);

    public string DoneTabText => Loc.F("Ftp.Tab.Done", DoneRows.Count);

    public string ConnectionTitle => IsConnected && _connection != null ? $"{_connection.Host}:{_connection.Port}" : Loc.T("Ftp.NotConnected");

    partial void OnIsConnectedChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowRecent));
        OnPropertyChanged(nameof(ConnectionTitle));
        ConnectCommand.NotifyCanExecuteChanged();
        DisconnectCommand.NotifyCanExecuteChanged();
    }

    partial void OnQueueTabChanged(int value)
    {
        OnPropertyChanged(nameof(ShowPending));
        OnPropertyChanged(nameof(ShowFailed));
        OnPropertyChanged(nameof(ShowDone));
    }

    partial void OnLocalFilterChanged(string value) => ApplyLocalFilter();

    partial void OnRemoteFilterChanged(string value) => ApplyRemoteFilter();

    partial void OnParallelFilesChanged(int value) => _settings.FtpParallelFiles = Math.Clamp(value, 1, 8);

    partial void OnThreadsPerFileChanged(int value) => _settings.FtpThreadsPerFile = Math.Clamp(value, 1, 8);

    partial void OnAutoThreadsChanged(bool value) => _settings.FtpAutoThreads = value;

    partial void OnSelectedExistsOptionChanged(FtpExistsOption? value)
    {
        if (value != null)
        {
            _settings.FtpExistsPolicy = value.Policy.ToString();
        }
    }

    private void BuildExistsOptions()
    {
        var selected = SelectedExistsOption?.Policy;
        ExistsOptions.Clear();
        foreach (var policy in new[] { FtpExistsPolicy.Resume, FtpExistsPolicy.Overwrite, FtpExistsPolicy.OverwriteIfNewer, FtpExistsPolicy.OverwriteIfSizeDiffers, FtpExistsPolicy.Skip })
        {
            ExistsOptions.Add(new FtpExistsOption(policy, Loc.T("Ftp.Exists." + policy)));
        }

        if (selected != null)
        {
            SelectedExistsOption = ExistsOptions.First(o => o.Policy == selected);
        }
    }

    private void RefreshTexts()
    {
        BuildExistsOptions();
        foreach (var row in PendingRows.Concat(FailedRows).Concat(DoneRows))
        {
            row.RefreshTexts();
        }

        OnPropertyChanged(nameof(PendingTabText));
        OnPropertyChanged(nameof(FailedTabText));
        OnPropertyChanged(nameof(DoneTabText));
        OnPropertyChanged(nameof(ConnectionTitle));
        if (!IsConnected)
        {
            StatusText = Loc.T("Ftp.Status.Disconnected");
            RemoteStatus = StatusText;
        }

        UpdateLocalStatus();
    }

    /// <summary>Lần đầu mở trang: liệt kê thư mục máy tính mở lần trước (hoặc thư mục Tải về).</summary>
    public void EnsureLoaded()
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        BuildLocalQuickPaths();
        var start = _settings.FtpLocalFolder is { Length: > 0 } saved && Directory.Exists(saved)
            ? saved
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        LoadLocal(Directory.Exists(start) ? start : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
    }

    // ── Máy đã nhớ ──────────────────────────────────────────────────────────────────────────────────────────────────

    private void LoadRecent()
    {
        RecentHosts.Clear();
        foreach (var host in _settings.FtpHosts.OrderByDescending(h => h.LastUsed).Take(12))
        {
            RecentHosts.Add(new FtpRecentHost(host.Host, host.Port, host.User, host.LastUsed));
        }

        OnPropertyChanged(nameof(HasRecent));
    }

    private void RememberHost(string host, int port, string user)
    {
        var existing = _settings.FtpHosts.FirstOrDefault(h => h.Host.Equals(host, StringComparison.OrdinalIgnoreCase) && h.Port == port);
        if (existing == null)
        {
            existing = new FtpHostSetting { Host = host, Port = port };
            _settings.FtpHosts.Add(existing);
        }

        existing.User = user;
        existing.LastUsed = DateTime.Now;
        if (_settings.FtpHosts.Count > 20)
        {
            _settings.FtpHosts.RemoveAll(h => h != existing && _settings.FtpHosts.OrderByDescending(x => x.LastUsed).Skip(20).Contains(h));
        }

        SettingsService.Save(_settings);
        LoadRecent();
    }

    [RelayCommand]
    private async Task ConnectRecentAsync(FtpRecentHost? recent)
    {
        if (recent == null)
        {
            return;
        }

        Host = recent.Host;
        Port = recent.Port.ToString(CultureInfo.InvariantCulture);
        User = recent.User;
        await ConnectAsync();
    }

    [RelayCommand]
    private void ForgetHost(FtpRecentHost? recent)
    {
        if (recent == null)
        {
            return;
        }

        _settings.FtpHosts.RemoveAll(h => h.Host.Equals(recent.Host, StringComparison.OrdinalIgnoreCase) && h.Port == recent.Port);
        SettingsService.Save(_settings);
        LoadRecent();
    }

    // ── Kết nối ─────────────────────────────────────────────────────────────────────────────────────────────────────

    private bool CanConnect() => !IsConnecting;

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectAsync()
    {
        var host = Host.Trim();
        if (host.Length == 0)
        {
            StatusText = Loc.T("Ftp.Error.Host");
            return;
        }

        if (!int.TryParse(Port.Trim(), out var port) || port is < 1 or > 65535)
        {
            StatusText = Loc.T("Ftp.Error.Port");
            return;
        }

        if (IsBusy)
        {
            StatusText = Loc.T("Ftp.Status.BusyReconnect");
            return;
        }

        DisconnectCore();
        IsConnecting = true;
        ConnectCommand.NotifyCanExecuteChanged();
        StatusText = Loc.F("Ftp.Status.Connecting", host, port);
        var info = new FtpConnectionInfo(host, port, User.Trim(), Password);
        var client = NewBrowser();
        try
        {
            await client.ConnectAsync(info.Host, info.Port, info.User, info.Pass);
            _browser = client;
            _connection = info;
            _queue = NewQueue(info);
            IsConnected = true;
            _keepAlive.Start();
            _tick.Start();
            RememberHost(host, port, info.User);
            StatusText = Loc.F("Ftp.Status.Connected", host, port);
            await LoadRemoteAsync("/");
        }
        catch (Exception ex) when (ex is FtpException or IOException or SocketException or OperationCanceledException)
        {
            client.Dispose();
            StatusText = Loc.F("Ftp.Status.ConnectFailed", ex.Message);
            RemoteStatus = StatusText;
            Log(false, StatusText);
        }
        finally
        {
            IsConnecting = false;
            ConnectCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanDisconnect() => IsConnected;

    [RelayCommand(CanExecute = nameof(CanDisconnect))]
    private void Disconnect()
    {
        _queue?.Stop();
        DisconnectCore();
        StatusText = Loc.T("Ftp.Status.Disconnected");
        RemoteStatus = StatusText;
    }

    private void DisconnectCore()
    {
        _keepAlive.Stop();
        _browser?.Dispose();
        _browser = null;
        if (_queue is { Running: false })
        {
            _queue.Dispose();
            _queue = null;
        }

        _connection = null;
        IsConnected = false;
        _remoteAll.Clear();
        RemoteRows.Clear();
    }

    private Ps5Ftp NewBrowser()
    {
        var client = new Ps5Ftp();
        client.Log += line => _pendingLog.Enqueue(line);
        return client;
    }

    private FtpTransferQueue NewQueue(FtpConnectionInfo info)
    {
        var queue = new FtpTransferQueue(info);
        queue.ItemAdded += item => _added.Enqueue(item);
        queue.Log += line =>
        {
            // Bỏ các lệnh lặp lại của từng tệp, chỉ giữ phản hồi lỗi và thông báo của hàng đợi.
            if (!line.Outgoing && (line.Text.Length == 0 || line.Text[0] is '4' or '5' || !char.IsDigit(line.Text[0])))
            {
                _pendingLog.Enqueue(line);
            }
        };
        queue.Idle += () => Dispatcher.UIThread.Post(OnQueueIdle);
        return queue;
    }

    private void Log(bool outgoing, string text) => _pendingLog.Enqueue(new FtpLogLine(outgoing, text, DateTime.Now));

    /// <summary>Chạy một thao tác trên kết nối duyệt (một lệnh một lúc); rớt kết nối thì nối lại một lần rồi thử lại.</summary>
    private async Task<T> Browse<T>(Func<Ps5Ftp, Task<T>> operation)
    {
        await _browserGate.WaitAsync();
        try
        {
            for (var attempt = 1; ; attempt++)
            {
                if (_connection == null)
                {
                    throw new FtpException(0, Loc.T("Ftp.NotConnected"));
                }

                if (_browser is not { Connected: true })
                {
                    _browser?.Dispose();
                    var client = NewBrowser();
                    try
                    {
                        await client.ConnectAsync(_connection.Host, _connection.Port, _connection.User, _connection.Pass);
                    }
                    catch
                    {
                        client.Dispose();
                        throw;
                    }

                    _browser = client;
                }

                try
                {
                    return await operation(_browser);
                }
                catch (Exception ex) when (attempt == 1 && ex is IOException or SocketException or FtpException { Code: 0 or 421 })
                {
                    _browser?.Dispose();
                    _browser = null;
                }
            }
        }
        finally
        {
            _browserGate.Release();
        }
    }

    private async Task KeepAliveAsync()
    {
        if (!IsConnected || _browser == null || !await _browserGate.WaitAsync(0))
        {
            return;
        }

        try
        {
            await _browser.NoopAsync();
        }
        catch (Exception)
        {
            _browser?.Dispose();
            _browser = null;
        }
        finally
        {
            _browserGate.Release();
        }
    }

    // ── Khung máy tính ──────────────────────────────────────────────────────────────────────────────────────────────

    private void BuildLocalQuickPaths()
    {
        LocalQuickPaths.Clear();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var path in new[] { home, Path.Combine(home, "Desktop"), Path.Combine(home, "Downloads"), Path.Combine(home, "Documents") })
        {
            if (Directory.Exists(path))
            {
                LocalQuickPaths.Add(path);
            }
        }

        try
        {
            foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady))
            {
                var root = drive.RootDirectory.FullName;
                var interesting = OperatingSystem.IsWindows()
                    || root.StartsWith("/Volumes/", StringComparison.Ordinal)
                    || root.StartsWith("/media/", StringComparison.Ordinal)
                    || root.StartsWith("/run/media/", StringComparison.Ordinal)
                    || root.StartsWith("/mnt/", StringComparison.Ordinal);
                if (interesting && !LocalQuickPaths.Contains(root))
                {
                    LocalQuickPaths.Add(root);
                }
            }
        }
        catch (Exception)
        {
        }
    }

    /// <summary>Liệt kê thư mục máy tính: thư mục trước, rồi tệp; bỏ tệp ẩn và rác hệ điều hành.</summary>
    public void LoadLocal(string path)
    {
        string full;
        try
        {
            full = Path.GetFullPath(path.Trim().Trim('"'));
        }
        catch (Exception)
        {
            LocalStatus = Loc.F("Ftp.Local.Missing", path);
            return;
        }

        if (!Directory.Exists(full))
        {
            LocalStatus = Loc.F("Ftp.Local.Missing", full);
            return;
        }

        var rows = new List<FtpFileRow>();
        try
        {
            var info = new DirectoryInfo(full);
            if (info.Parent != null)
            {
                rows.Add(new FtpFileRow("..", info.Parent.FullName, true, 0, null, string.Empty, isParent: true));
            }

            var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System };
            foreach (var directory in info.EnumerateDirectories("*", options).Where(d => !IsHidden(d)).OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
            {
                rows.Add(new FtpFileRow(directory.Name, directory.FullName, true, 0, directory.LastWriteTime, Loc.T("Ftp.Kind.Folder")));
            }

            foreach (var file in info.EnumerateFiles("*", options).Where(f => !IsHidden(f) && !JunkFileFinder.IsJunkFileName(f.Name)).OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
            {
                rows.Add(new FtpFileRow(file.Name, file.FullName, false, file.Length, file.LastWriteTime, file.Extension.TrimStart('.').ToUpperInvariant()));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LocalStatus = ex.Message;
            return;
        }

        LocalPath = full;
        _settings.FtpLocalFolder = full;
        _localAll.Clear();
        _localAll.AddRange(rows.Select(r => new FtpLocalRowSource(r)));
        ApplyLocalFilter();
    }

    private static bool IsHidden(FileSystemInfo info) =>
        info.Name.StartsWith('.') || (OperatingSystem.IsWindows() && info.Attributes.HasFlag(FileAttributes.Hidden));

    private void ApplyLocalFilter()
    {
        var filter = LocalFilter.Trim();
        LocalRows.Clear();
        foreach (var row in _localAll.Select(s => s.Row).Where(r => r.IsParent || filter.Length == 0 || r.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)))
        {
            LocalRows.Add(row);
        }

        UpdateLocalStatus();
    }

    private void UpdateLocalStatus()
    {
        var items = _localAll.Select(s => s.Row).Where(r => !r.IsParent).ToList();
        LocalStatus = Loc.F("Ftp.PaneStatus", items.Count(r => !r.IsDirectory), items.Count(r => r.IsDirectory), Formatters.Size(items.Where(r => !r.IsDirectory).Sum(r => r.Size)));
    }

    [RelayCommand]
    private void LocalGo() => LoadLocal(LocalPath);

    [RelayCommand]
    private void LocalUp()
    {
        var parent = Directory.GetParent(LocalPath);
        if (parent != null)
        {
            LoadLocal(parent.FullName);
        }
    }

    [RelayCommand]
    private void LocalRefresh() => LoadLocal(LocalPath);

    [RelayCommand]
    private void LocalQuick(string? path)
    {
        if (path != null)
        {
            LoadLocal(path);
        }
    }

    [RelayCommand]
    private async Task LocalBrowseAsync()
    {
        var path = await _dialogs.PickFolderAsync(Loc.T("Ftp.Local.Pick"), Directory.Exists(LocalPath) ? LocalPath : null);
        if (path != null)
        {
            LoadLocal(path);
        }
    }

    [RelayCommand]
    private async Task LocalNewFolderAsync()
    {
        var name = await InputDialogAsync(Loc.T("Ftp.NewFolder"), string.Empty, Loc.T("Ftp.Create"));
        if (name == null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.Combine(LocalPath, name));
            LoadLocal(LocalPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LocalStatus = ex.Message;
        }
    }

    [RelayCommand]
    private async Task LocalRenameAsync()
    {
        if (SelectedLocal.FirstOrDefault(r => !r.IsParent) is not { } row)
        {
            return;
        }

        var name = await InputDialogAsync(Loc.T("Ftp.Rename"), row.Name, Loc.T("Ftp.Rename"));
        if (name == null || name == row.Name)
        {
            return;
        }

        try
        {
            var target = Path.Combine(Path.GetDirectoryName(row.FullPath)!, name);
            if (row.IsDirectory)
            {
                Directory.Move(row.FullPath, target);
            }
            else
            {
                File.Move(row.FullPath, target);
            }

            LoadLocal(LocalPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LocalStatus = ex.Message;
        }
    }

    [RelayCommand]
    private async Task LocalDeleteAsync()
    {
        var rows = SelectedLocal.Where(r => !r.IsParent).ToList();
        if (rows.Count == 0)
        {
            return;
        }

        var names = string.Join(", ", rows.Take(5).Select(r => r.Name)) + (rows.Count > 5 ? " …" : string.Empty);
        if (!await _dialogs.ConfirmAsync(Loc.T("Ftp.Delete.LocalTitle"), Loc.F("Ftp.Delete.LocalMessage", rows.Count, names), Loc.T("Ftp.Delete"), Loc.T("Common.Cancel"), destructive: true))
        {
            return;
        }

        foreach (var row in rows)
        {
            try
            {
                if (row.IsDirectory)
                {
                    Directory.Delete(row.FullPath, recursive: true);
                }
                else
                {
                    File.Delete(row.FullPath);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log(false, ex.Message);
            }
        }

        LoadLocal(LocalPath);
    }

    [RelayCommand]
    private async Task LocalCopyPathAsync()
    {
        if (SelectedLocal.FirstOrDefault() is { } row)
        {
            await _dialogs.SetClipboardAsync(row.FullPath);
        }
    }

    [RelayCommand]
    private async Task LocalRevealAsync()
    {
        await _dialogs.OpenFolderAsync(LocalPath);
    }

    /// <summary>Nhấp đúp ở khung máy tính: vào thư mục, hoặc tải tệp lên thư mục PS5 đang mở.</summary>
    public void LocalActivate(FtpFileRow row)
    {
        if (row.IsDirectory)
        {
            LoadLocal(row.FullPath);
        }
        else
        {
            UploadPaths([row.FullPath], null);
        }
    }

    // ── Khung PS5 ───────────────────────────────────────────────────────────────────────────────────────────────────

    public async Task LoadRemoteAsync(string path)
    {
        if (!IsConnected)
        {
            return;
        }

        var dir = Ps5Ftp.NormalizeDir(path);
        RemoteLoading = true;
        RemoteStatus = Loc.F("Ftp.Remote.Listing", dir);
        try
        {
            var entries = await Browse(client => client.ListAsync(dir));
            var rows = new List<FtpFileRow>();
            if (dir != "/")
            {
                rows.Add(new FtpFileRow("..", Ps5Ftp.Parent(dir), true, 0, null, string.Empty, isParent: true));
            }

            rows.AddRange(entries
                .OrderByDescending(e => e.IsDirectory)
                .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .Select(e => new FtpFileRow(e.Name, e.FullPath, e.IsDirectory, e.Size, e.Modified, e.Permissions, isLink: e.Kind == FtpEntryKind.Link)));
            RemotePath = dir;
            _remoteAll.Clear();
            _remoteAll.AddRange(rows);
            ApplyRemoteFilter();
        }
        catch (Exception ex) when (ex is FtpException or IOException or SocketException)
        {
            RemoteStatus = Loc.F("Ftp.Remote.ListFailed", dir, ex.Message);
            Log(false, RemoteStatus);
        }
        finally
        {
            RemoteLoading = false;
        }
    }

    private void ApplyRemoteFilter()
    {
        var filter = RemoteFilter.Trim();
        RemoteRows.Clear();
        foreach (var row in _remoteAll.Where(r => r.IsParent || filter.Length == 0 || r.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)))
        {
            RemoteRows.Add(row);
        }

        var items = _remoteAll.Where(r => !r.IsParent).ToList();
        RemoteStatus = Loc.F("Ftp.PaneStatus", items.Count(r => !r.IsDirectory), items.Count(r => r.IsDirectory), Formatters.Size(items.Where(r => !r.IsDirectory).Sum(r => r.Size)));
    }

    [RelayCommand]
    private async Task RemoteGoAsync() => await LoadRemoteAsync(RemotePath);

    [RelayCommand]
    private async Task RemoteUpAsync() => await LoadRemoteAsync(Ps5Ftp.Parent(RemotePath));

    [RelayCommand]
    private async Task RemoteRefreshAsync() => await LoadRemoteAsync(RemotePath);

    [RelayCommand]
    private async Task RemoteQuickAsync(string? path)
    {
        if (path != null)
        {
            await LoadRemoteAsync(path);
        }
    }

    [RelayCommand]
    private async Task RemoteNewFolderAsync()
    {
        if (!IsConnected)
        {
            return;
        }

        var name = await InputDialogAsync(Loc.T("Ftp.NewFolder"), string.Empty, Loc.T("Ftp.Create"));
        if (name == null)
        {
            return;
        }

        try
        {
            await Browse(async client =>
            {
                await client.CreateDirectoryAsync(Ps5Ftp.Combine(RemotePath, name));
                return true;
            });
            await LoadRemoteAsync(RemotePath);
        }
        catch (Exception ex) when (ex is FtpException or IOException or SocketException)
        {
            RemoteStatus = ex.Message;
        }
    }

    [RelayCommand]
    private async Task RemoteRenameAsync()
    {
        if (SelectedRemote.FirstOrDefault(r => !r.IsParent) is not { } row)
        {
            return;
        }

        var name = await InputDialogAsync(Loc.T("Ftp.Rename"), row.Name, Loc.T("Ftp.Rename"));
        if (name == null || name == row.Name)
        {
            return;
        }

        try
        {
            await Browse(async client =>
            {
                await client.RenameAsync(row.FullPath, Ps5Ftp.Combine(Ps5Ftp.Parent(row.FullPath), name));
                return true;
            });
            await LoadRemoteAsync(RemotePath);
        }
        catch (Exception ex) when (ex is FtpException or IOException or SocketException)
        {
            RemoteStatus = ex.Message;
        }
    }

    /// <summary>Kéo tệp PS5 thả lên một thư mục PS5 khác: di chuyển trên PS5 (RNFR/RNTO).</summary>
    public async Task MoveRemoteAsync(IReadOnlyList<FtpFileRow> rows, string targetDir)
    {
        var moving = rows.Where(r => !r.IsParent && Ps5Ftp.Parent(r.FullPath) != Ps5Ftp.NormalizeDir(targetDir) && r.FullPath != Ps5Ftp.NormalizeDir(targetDir)).ToList();
        if (moving.Count == 0 || !IsConnected)
        {
            return;
        }

        try
        {
            await Browse(async client =>
            {
                foreach (var row in moving)
                {
                    await client.RenameAsync(row.FullPath, Ps5Ftp.Combine(targetDir, row.Name));
                }

                return true;
            });
        }
        catch (Exception ex) when (ex is FtpException or IOException or SocketException)
        {
            RemoteStatus = ex.Message;
        }

        await LoadRemoteAsync(RemotePath);
    }

    [RelayCommand]
    private async Task RemoteDeleteAsync()
    {
        var rows = SelectedRemote.Where(r => !r.IsParent).ToList();
        if (rows.Count == 0 || _connection == null)
        {
            return;
        }

        var names = string.Join(", ", rows.Take(5).Select(r => r.Name)) + (rows.Count > 5 ? " …" : string.Empty);
        if (!await _dialogs.ConfirmAsync(Loc.T("Ftp.Delete.RemoteTitle"), Loc.F("Ftp.Delete.RemoteMessage", rows.Count, names), Loc.T("Ftp.Delete"), Loc.T("Common.Cancel"), destructive: true))
        {
            return;
        }

        using var deleter = new FtpTreeDeleter(_connection) { Workers = Math.Clamp(ParallelFiles * 2, 2, 16) };
        deleter.Log += line => _pendingLog.Enqueue(line);
        var progress = new DispatcherTimer(TimeSpan.FromMilliseconds(300), DispatcherPriority.Background, (_, _) =>
            RemoteStatus = Loc.F("Ftp.Delete.Progress", deleter.Stats.ProcessedItems, Math.Max(deleter.Stats.TotalItems, deleter.Stats.ProcessedItems), deleter.Stats.Current));
        progress.Start();
        RemoteLoading = true;
        try
        {
            await Task.Run(async () =>
            {
                await deleter.CountAsync(rows.Select(r => (r.FullPath, r.IsDirectory && !r.IsLink, r.Size)), CancellationToken.None);
                await deleter.DeleteAsync(CancellationToken.None);
            });
            var failures = deleter.Failures;
            Log(false, Loc.F("Ftp.Delete.Done", deleter.Stats.FilesDeleted, deleter.Stats.DirsDeleted, failures.Count));
            foreach (var failure in failures.Take(20))
            {
                Log(false, $"{failure.Path}: {failure.Error}");
            }
        }
        catch (Exception ex) when (ex is FtpException or IOException or SocketException)
        {
            Log(false, ex.Message);
        }
        finally
        {
            progress.Stop();
            RemoteLoading = false;
        }

        await LoadRemoteAsync(RemotePath);
    }

    [RelayCommand]
    private async Task RemoteCopyPathAsync()
    {
        if (SelectedRemote.FirstOrDefault() is { } row)
        {
            await _dialogs.SetClipboardAsync(row.FullPath);
        }
    }

    /// <summary>Nhấp đúp ở khung PS5: vào thư mục, hoặc tải tệp về thư mục máy tính đang mở.</summary>
    public async Task RemoteActivateAsync(FtpFileRow row)
    {
        if (row.IsDirectory)
        {
            await LoadRemoteAsync(row.FullPath);
        }
        else
        {
            await DownloadRowsAsync([row], null);
        }
    }

    // ── Truyền tệp ──────────────────────────────────────────────────────────────────────────────────────────────────

    [RelayCommand]
    private void UploadSelected() => UploadPaths(SelectedLocal.Where(r => !r.IsParent).Select(r => r.FullPath).ToList(), null);

    [RelayCommand]
    private async Task DownloadSelectedAsync() => await DownloadRowsAsync(SelectedRemote.Where(r => !r.IsParent).ToList(), null);

    /// <summary>Tải tệp / thư mục máy tính lên <paramref name="remoteDir"/> (mặc định thư mục PS5 đang mở). Quét thư mục ở luồng nền.</summary>
    public void UploadPaths(IReadOnlyList<string> paths, string? remoteDir)
    {
        if (_queue == null || paths.Count == 0)
        {
            StatusText = Loc.T(_queue == null ? "Ftp.Status.ConnectFirst" : "Ftp.Status.NothingSelected");
            return;
        }

        var queue = _queue;
        var target = Ps5Ftp.NormalizeDir(remoteDir ?? RemotePath);
        ApplyQueueOptions(queue);
        _ = Task.Run(() =>
        {
            var loose = new FtpTransferJob(FtpDirection.Upload, Loc.F("Ftp.Job.Files", paths.Count), string.Empty, target);
            foreach (var path in paths)
            {
                try
                {
                    if (Directory.Exists(path))
                    {
                        queue.EnqueueUploadTree(path, target);
                    }
                    else if (File.Exists(path))
                    {
                        queue.Enqueue(new FtpTransferItem(FtpDirection.Upload, path, Ps5Ftp.Combine(target, Path.GetFileName(path)), new FileInfo(path).Length) { Job = loose });
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Log(false, ex.Message);
                }

                Dispatcher.UIThread.Post(() => StartQueue(queue));
            }
        });
        StatusText = Loc.F("Ftp.Status.QueuedUpload", paths.Count, target);
    }

    /// <summary>Tải tệp / thư mục PS5 về <paramref name="localDir"/> (mặc định thư mục máy tính đang mở).</summary>
    public async Task DownloadRowsAsync(IReadOnlyList<FtpFileRow> rows, string? localDir)
    {
        if (_queue == null || rows.Count == 0)
        {
            StatusText = Loc.T(_queue == null ? "Ftp.Status.ConnectFirst" : "Ftp.Status.NothingSelected");
            return;
        }

        var queue = _queue;
        var target = localDir ?? LocalPath;
        if (!Directory.Exists(target))
        {
            StatusText = Loc.F("Ftp.Local.Missing", target);
            return;
        }

        ApplyQueueOptions(queue);
        var loose = new FtpTransferJob(FtpDirection.Download, Loc.F("Ftp.Job.Files", rows.Count), string.Empty, target);
        foreach (var row in rows.Where(r => !r.IsDirectory || r.IsLink))
        {
            queue.Enqueue(new FtpTransferItem(FtpDirection.Download, Path.Combine(target, row.Name), row.FullPath, row.IsLink ? -1 : row.Size) { RemoteModified = row.Modified, Job = loose });
        }

        StartQueue(queue);
        foreach (var folder in rows.Where(r => r.IsDirectory && !r.IsLink))
        {
            StatusText = Loc.F("Ftp.Status.Scanning", folder.FullPath);
            try
            {
                await Browse(client => queue.EnqueueDownloadTreeAsync(client, folder.FullPath, target));
                StartQueue(queue);
            }
            catch (Exception ex) when (ex is FtpException or IOException or SocketException)
            {
                Log(false, Loc.F("Ftp.Remote.ListFailed", folder.FullPath, ex.Message));
            }
        }

        StatusText = Loc.F("Ftp.Status.QueuedDownload", rows.Count, target);
    }

    private void ApplyQueueOptions(FtpTransferQueue queue)
    {
        queue.Workers = Math.Clamp(ParallelFiles, 1, 8);
        queue.SegmentsPerFile = Math.Clamp(ThreadsPerFile, 1, 8);
        queue.AutoWorkers = AutoThreads;
        queue.ExistsPolicy = SelectedExistsOption?.Policy ?? FtpExistsPolicy.Resume;
    }

    private void StartQueue(FtpTransferQueue queue)
    {
        if (queue.Running)
        {
            return;
        }

        if (!_runClock.IsRunning)
        {
            _runClock.Restart();
            _eta.Reset();
            _lastSample = 0;
            _lastBytes = queue.BytesMoved;
            _lastFinished = queue.FinishedCount;
            _peakSpeed = 0;
            _workerSum = 0;
            _workerSamples = 0;
        }

        queue.Start();
        IsBusy = true;
        _tick.Start();
    }

    [RelayCommand]
    private void StartTransfers()
    {
        if (_queue != null)
        {
            ApplyQueueOptions(_queue);
            StartQueue(_queue);
        }
    }

    [RelayCommand]
    private void StopTransfers()
    {
        _queue?.Stop();
        StatusText = Loc.T("Ftp.Status.Stopping");
    }

    [RelayCommand]
    private void RetryFailed()
    {
        if (_queue == null)
        {
            return;
        }

        var count = _queue.RetryFailed();
        foreach (var row in FailedRows.ToList())
        {
            FailedRows.Remove(row);
            row.Refresh();
            PendingRows.Add(row);
        }

        StatusText = Loc.F("Ftp.Status.Retrying", count);
        StartTransfers();
        RaiseTabs();
    }

    [RelayCommand]
    private void ClearDone()
    {
        _queue?.ClearFinished();
        DoneRows.Clear();
        RaiseTabs();
    }

    private void OnQueueIdle()
    {
        Tick();
        IsBusy = false;
        _runClock.Stop();
        StatusText = Loc.F("Ftp.Status.Idle", DoneRows.Count, FailedRows.Count);
        LoadLocal(LocalPath);
        _ = LoadRemoteAsync(RemotePath);
    }

    private void RaiseTabs()
    {
        OnPropertyChanged(nameof(PendingTabText));
        OnPropertyChanged(nameof(FailedTabText));
        OnPropertyChanged(nameof(DoneTabText));
    }

    /// <summary>Nhịp 250 ms: đưa dòng nhật ký, tệp mới thêm và trạng thái tệp vào giao diện; mỗi giây tính tốc độ / thời gian còn lại.</summary>
    private void Tick()
    {
        var logCount = 0;
        while (_pendingLog.TryDequeue(out var line) && logCount++ < 400)
        {
            LogLines.Add(line.ToString());
        }

        while (LogLines.Count > 3000)
        {
            LogLines.RemoveAt(0);
        }

        var changed = false;
        while (_added.TryDequeue(out var item))
        {
            var row = new FtpQueueRow(item);
            if (PendingRows.Count < VisiblePendingLimit)
            {
                PendingRows.Add(row);
            }
            else
            {
                _overflow.Add(row);
            }

            changed = true;
        }

        foreach (var row in PendingRows.ToList())
        {
            row.Refresh();
            if (row.IsDone)
            {
                PendingRows.Remove(row);
                DoneRows.Insert(0, row);
                changed = true;
            }
            else if (row.IsFailed)
            {
                PendingRows.Remove(row);
                FailedRows.Insert(0, row);
                changed = true;
            }
        }

        while (DoneRows.Count > VisibleFinishedLimit)
        {
            DoneRows.RemoveAt(DoneRows.Count - 1);
        }

        while (PendingRows.Count < VisiblePendingLimit && _overflow.Count > 0)
        {
            var row = _overflow[0];
            _overflow.RemoveAt(0);
            row.Refresh();
            (row.IsDone ? DoneRows : row.IsFailed ? FailedRows : PendingRows).Add(row);
            changed = true;
        }

        OverflowText = _overflow.Count > 0 ? Loc.F("Ftp.Queue.Overflow", _overflow.Count) : string.Empty;
        if (changed)
        {
            RaiseTabs();
        }

        if (++_tickCount % 4 == 0)
        {
            UpdateStats();
        }
    }

    private void UpdateStats()
    {
        var queue = _queue;
        if (queue == null)
        {
            return;
        }

        var items = queue.Items;
        var files = items.Where(i => !i.IsDirectoryMarker).ToList();
        var done = files.Count(i => i.State is FtpItemState.Done or FtpItemState.Skipped);
        var bytesTotal = files.Sum(i => Math.Max(0, i.Size));
        var bytesDone = files.Sum(i => i.State is FtpItemState.Done or FtpItemState.Skipped ? Math.Max(0, i.Size) : Math.Max(0, i.Transferred));
        FilesText = $"{done:N0} / {files.Count:N0}";
        BytesText = $"{Formatters.Size(bytesDone)} / {Formatters.Size(bytesTotal)}";
        TotalPercent = bytesTotal > 0 ? Math.Min(100, bytesDone * 100.0 / bytesTotal) : files.Count > 0 ? done * 100.0 / files.Count : 0;
        ThreadsText = queue.Running ? $"{queue.ActiveWorkers} / {queue.TargetWorkers}" : "0";
        ThreadsReason = queue.AutoReason.Length > 0 ? Loc.F("Ftp.Threads.Auto", queue.AutoReason) : string.Empty;

        var now = _runClock.Elapsed.TotalSeconds;
        var dt = now - _lastSample;
        if (queue.Running && dt > 0.5)
        {
            var moved = queue.BytesMoved;
            var finished = queue.FinishedCount;
            var instant = Math.Max(0, moved - _lastBytes) / dt;
            _smoothedSpeed = _smoothedSpeed <= 0 ? instant : _smoothedSpeed * 0.6 + instant * 0.4;
            _peakSpeed = Math.Max(_peakSpeed, _smoothedSpeed);
            _workerSum += queue.ActiveWorkers;
            _workerSamples++;
            _eta.Add(dt, finished - _lastFinished, moved - _lastBytes, Math.Max(1, queue.ActiveWorkers));
            _lastBytes = moved;
            _lastFinished = finished;
            _lastSample = now;
            var remainingFiles = files.Count(i => i.State is FtpItemState.Queued or FtpItemState.Running);
            var remainingBytes = Math.Max(0, bytesTotal - bytesDone);
            var seconds = _eta.Ready ? _eta.Cost(remainingFiles, remainingBytes, queue.PlannedWorkers()) : _smoothedSpeed > 1 ? remainingBytes / _smoothedSpeed : -1;
            RemainingText = seconds >= 0 ? Duration(seconds) : "—";
            FinishText = seconds >= 0 ? Loc.F("Ftp.FinishAt", DateTime.Now.AddSeconds(seconds).ToString("HH:mm:ss", CultureInfo.InvariantCulture)) : string.Empty;
            var average = now > 0 ? (queue.BytesMoved) / Math.Max(1, now) : 0;
            PeakText = Loc.F("Ftp.Peak", Formatters.Rate(_peakSpeed), Formatters.Rate(average));
        }

        SpeedText = queue.Running ? Formatters.Rate(_smoothedSpeed) : Formatters.Rate(0);
        if (!queue.Running)
        {
            _smoothedSpeed = 0;
            RemainingText = "—";
            FinishText = string.Empty;
        }
    }

    public static string Duration(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0)
        {
            return string.Empty;
        }

        var span = TimeSpan.FromSeconds(Math.Min(seconds, 359_999));
        return span.TotalHours >= 1 ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}" : $"{span.Minutes:00}:{span.Seconds:00}";
    }

    [RelayCommand]
    private void ToggleLog() => ShowLog = !ShowLog;

    [RelayCommand]
    private void ClearLog() => LogLines.Clear();

    [RelayCommand]
    private void SetQueueTab(string? tab)
    {
        if (int.TryParse(tab, out var index))
        {
            QueueTab = index;
        }
    }

    private Task<string?> InputDialogAsync(string title, string value, string okLabel) =>
        Views.InputDialog.ShowAsync(_dialogs.Owner, title, string.Empty, value, okLabel, name =>
            name.IndexOfAny(['/', '\\', ':', '*', '?', '"', '<', '>', '|']) >= 0 || name is "." or ".." ? Loc.T("Ftp.Error.Name") : null);
}
