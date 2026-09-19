using System.Collections.ObjectModel;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PsViethoa.FpkgBuilder.App.Services;
using PsViethoa.FpkgBuilder.Core.Localization;
using PsViethoa.FpkgBuilder.Core.Models;
using PsViethoa.FpkgBuilder.Core.Services;

namespace PsViethoa.FpkgBuilder.App.ViewModels;

/// <summary>
/// Hàng chờ tạo gói: nhiều game xếp hàng, chạy song song tối đa N lượt (mỗi lượt là một <see cref="BuildEngine.BuildAsync"/> độc lập
/// với nhật ký, tiến trình và huỷ riêng). Thiết lập tạo gói (SDK Sony, DRM, nén…) lấy từ tab Tạo gói tại lúc lượt bắt đầu qua
/// <c>requestFactory</c>; thư mục xuất theo quy tắc "tự đặt theo nguồn" của tab đó.
/// Xung đột khi chạy song song đã tính: WINEPREFIX tạo một lần (SonySdkRunner), huỷ một lượt không tắt wineserver của lượt khác,
/// bí danh/gương/tệp tạm mang tên ngẫu nhiên riêng, hai mục cùng Content ID + cùng thư mục xuất không được chạy cùng lúc.
/// </summary>
public sealed partial class QueueViewModel : ObservableObject
{
    public const int MaxConcurrency = 4;

    private readonly AppSettings _settings;
    private readonly DialogService _dialogs;
    private readonly Func<QueueItemViewModel, BuildRequest> _requestFactory;
    private readonly Func<string, string> _outputFolderFor;
    private readonly Action<LogLevel, string> _mainLog;
    private readonly BuildEngine _engine = new();
    private readonly DispatcherTimer _timer;

    public QueueViewModel(AppSettings settings, DialogService dialogs, Func<QueueItemViewModel, BuildRequest> requestFactory, Func<string, string> outputFolderFor, Action<LogLevel, string> mainLog)
    {
        _settings = settings;
        _dialogs = dialogs;
        _requestFactory = requestFactory;
        _outputFolderFor = outputFolderFor;
        _mainLog = mainLog;
        ConcurrencyIndex = Math.Clamp(settings.QueueConcurrency, 1, MaxConcurrency) - 1;
        RefreshConcurrencyOptions();
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(200), DispatcherPriority.Background, (_, _) => Tick());
        Loc.Current.PropertyChanged += (_, _) => OnLanguageChanged();
    }

    public ObservableCollection<QueueItemViewModel> Items { get; } = new();

    [ObservableProperty] private QueueItemViewModel? _selectedItem;

    [ObservableProperty] private IReadOnlyList<string> _concurrencyOptions = Array.Empty<string>();

    /// <summary>0..3 → 1..4 lượt chạy cùng lúc.</summary>
    [ObservableProperty] private int _concurrencyIndex;

    /// <summary>Đang nhận việc mới từ hàng chờ (bấm Bắt đầu). Tạm dừng = để các lượt đang chạy xong nhưng không bắt đầu lượt mới.</summary>
    [ObservableProperty] private bool _isDispatching;

    [ObservableProperty] private int _waitingCount;

    [ObservableProperty] private int _runningCount;

    [ObservableProperty] private int _doneCount;

    [ObservableProperty] private int _failedCount;

    [ObservableProperty] private double _overallPercent;

    [ObservableProperty] private string _summaryText = string.Empty;

    [ObservableProperty] private string _headlineText = string.Empty;

    [ObservableProperty] private string _elapsedText = string.Empty;

    private DateTime? _startedAt;

    public int Concurrency => ConcurrencyIndex + 1;

    public bool IsRunning => RunningCount > 0;

    public bool IsBusy => IsRunning || IsDispatching;

    public bool HasItems => Items.Count > 0;

    public bool IsEmpty => Items.Count == 0;

    public bool HasSelection => SelectedItem != null;

    public bool HasFinished => Items.Any(item => item.IsFinished);

    public bool CanStart => !IsDispatching && Items.Any(item => item.IsWaiting);

    public bool CanPause => IsDispatching;

    public bool CanCancelAll => Items.Any(item => item.CanCancel);

    public string SelectedLogTitle => SelectedItem == null ? Loc.T("Queue.LogEmpty") : Loc.F("Queue.LogTitle", SelectedItem.Title);

    partial void OnConcurrencyIndexChanged(int value)
    {
        OnPropertyChanged(nameof(Concurrency));
        _settings.QueueConcurrency = Concurrency;
        Pump();
    }

    partial void OnSelectedItemChanged(QueueItemViewModel? value)
    {
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectedLogTitle));
    }

    partial void OnIsDispatchingChanged(bool value) => RefreshCommandStates();

    private void OnLanguageChanged()
    {
        RefreshConcurrencyOptions();
        foreach (var item in Items)
        {
            item.RefreshLanguage();
        }

        RefreshSummary();
        OnPropertyChanged(nameof(SelectedLogTitle));
    }

    private void RefreshConcurrencyOptions()
    {
        var index = ConcurrencyIndex;
        ConcurrencyOptions = Enumerable.Range(1, MaxConcurrency).Select(n => Loc.F("Queue.ConcurrencyOption", n)).ToList();
        ConcurrencyIndex = index;
    }

    // ===================== Thêm mục =====================

    [RelayCommand]
    private async Task AddFolderAsync()
    {
        var folder = await _dialogs.PickFolderAsync(Loc.T("Queue.AddFolderTitle"), _settings.SourcePath);
        if (folder != null)
        {
            await AddPathsAsync(new[] { folder });
        }
    }

    [RelayCommand]
    private async Task AddImageAsync()
    {
        var file = await _dialogs.PickFileAsync(
            Loc.T("Queue.AddImageTitle"),
            _settings.SourcePath,
            new FilePickerFileType(Loc.T("Queue.ImageFilter")) { Patterns = new[] { "*.exfat", "*.ffpfsc", "*.ffpkg", "*.gp5" } });
        if (file != null)
        {
            await AddPathsAsync(new[] { file });
        }
    }

    /// <summary>
    /// Thêm nguồn từ đường dẫn (nút chọn hoặc kéo-thả). Một thư mục không phải nguồn nhưng chứa nhiều thư mục game (ổ dump)
    /// thì thêm từng thư mục con là nguồn hợp lệ.
    /// </summary>
    public async Task<int> AddPathsAsync(IEnumerable<string> paths)
    {
        var candidates = new List<string>();
        foreach (var raw in paths)
        {
            var path = Path.TrimEndingDirectorySeparator(raw.Trim());
            if (string.IsNullOrEmpty(path))
            {
                continue;
            }

            if (IsGameSource(path))
            {
                candidates.Add(path);
                continue;
            }

            if (Directory.Exists(path))
            {
                // Thư mục không phải game (ổ dump, thư mục gom nhiều game): thêm từng thư mục con là game.
                var children = await Task.Run(() => EnumerateGameFolders(path));
                candidates.AddRange(children);
            }
        }

        var added = 0;
        foreach (var candidate in candidates)
        {
            if (Items.Any(item => !item.IsFinished && PathsEqual(item.SourcePath, candidate)))
            {
                _mainLog(LogLevel.Warning, Loc.F("Queue.Duplicate", candidate));
                continue;
            }

            var item = new QueueItemViewModel(candidate);
            try
            {
                item.OutputFolder = _outputFolderFor(candidate);
            }
            catch (Exception)
            {
                item.OutputFolder = string.Empty;
            }

            Items.Add(item);
            added++;
            _ = LoadMetadataAsync(item);
        }

        if (added > 0)
        {
            _mainLog(LogLevel.Info, Loc.F("Queue.Added", added));
            SelectedItem ??= Items[0];
        }
        else if (candidates.Count == 0)
        {
            _mainLog(LogLevel.Warning, Loc.T("Queue.AddedNone"));
        }

        RefreshSummary();
        Pump();
        return added;
    }

    private static IEnumerable<string> EnumerateGameFolders(string root)
    {
        var result = new List<string>();
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(root))
            {
                var name = Path.GetFileName(directory);
                if (name.StartsWith('.') || name.StartsWith('$') || string.Equals(name, "System Volume Information", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (IsGameSource(directory))
                {
                    result.Add(directory);
                }
            }
        }
        catch (Exception)
        {
        }

        result.Sort(StringComparer.OrdinalIgnoreCase);
        return result;
    }

    /// <summary>Thư mục game (có sce_sys) hoặc tệp ảnh đĩa / dự án GP5. Thư mục thường không tính, để còn duyệt các thư mục con.</summary>
    private static bool IsGameSource(string path)
    {
        if (Directory.Exists(path))
        {
            return Directory.Exists(Path.Combine(path, "sce_sys"));
        }

        return File.Exists(path) && SourceLocator.Detect(path) != SourceKind.None;
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

    private async Task LoadMetadataAsync(QueueItemViewModel item)
    {
        item.MetadataCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        item.MetadataCancellation = cancellation;
        var token = cancellation.Token;
        try
        {
            var metadata = await Task.Run(() => MetadataReader.Read(item.SourcePath, token), token);
            if (token.IsCancellationRequested)
            {
                return;
            }

            item.Metadata = metadata;
            item.Title = string.IsNullOrWhiteSpace(metadata.Title) ? item.SourceName : metadata.Title!;
            item.ContentId = metadata.ContentId ?? string.Empty;
            item.Version = metadata.Version ?? string.Empty;
            item.MetadataWarning = !metadata.HasParamJson;
            OnPropertyChanged(nameof(SelectedLogTitle));
            item.Icon = await Task.Run(() => IconLoader.Load(metadata, 128), token);

            var stats = await Task.Run(() => FolderScanner.Scan(item.SourcePath, token), token);
            if (!token.IsCancellationRequested)
            {
                item.SizeText = Loc.F("Queue.SizeFiles", Formatters.Size(stats.TotalBytes), Formatters.Count(stats.FileCount));
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            item.MetadataWarning = true;
            item.SizeText = string.Empty;
            item.ErrorText = ex.Message;
        }
    }

    // ===================== Điều phối =====================

    [RelayCommand]
    private void Start()
    {
        IsDispatching = true;
        _startedAt ??= DateTime.UtcNow;
        _timer.Start();
        Pump();
    }

    [RelayCommand]
    private void Pause()
    {
        IsDispatching = false;
        RefreshSummary();
    }

    [RelayCommand]
    private void CancelAll()
    {
        IsDispatching = false;
        foreach (var item in Items.ToList())
        {
            if (item.IsWaiting)
            {
                item.Status = QueueItemStatus.Canceled;
                item.ErrorText = Loc.T("Queue.CanceledBeforeStart");
            }
            else if (item.IsRunning)
            {
                item.Cancellation?.Cancel();
            }
        }

        RefreshSummary();
    }

    [RelayCommand]
    private void CancelItem(QueueItemViewModel? item)
    {
        if (item == null)
        {
            return;
        }

        if (item.IsWaiting)
        {
            item.Status = QueueItemStatus.Canceled;
            item.ErrorText = Loc.T("Queue.CanceledBeforeStart");
        }
        else if (item.IsRunning)
        {
            item.Cancellation?.Cancel();
        }

        RefreshSummary();
    }

    [RelayCommand]
    private void Retry(QueueItemViewModel? item)
    {
        if (item is not { CanRetry: true })
        {
            return;
        }

        item.Status = QueueItemStatus.Waiting;
        item.ErrorText = string.Empty;
        item.PhaseText = string.Empty;
        item.Percent = 0;
        RefreshSummary();
        Pump();
    }

    [RelayCommand]
    private void Remove(QueueItemViewModel? item)
    {
        if (item is not { CanRemove: true })
        {
            return;
        }

        item.MetadataCancellation?.Cancel();
        var index = Items.IndexOf(item);
        Items.Remove(item);
        if (SelectedItem == item)
        {
            SelectedItem = Items.Count == 0 ? null : Items[Math.Clamp(index, 0, Items.Count - 1)];
        }

        RefreshSummary();
    }

    [RelayCommand]
    private void ClearFinished()
    {
        foreach (var item in Items.Where(item => item.IsFinished).ToList())
        {
            Items.Remove(item);
        }

        if (SelectedItem != null && !Items.Contains(SelectedItem))
        {
            SelectedItem = Items.FirstOrDefault();
        }

        RefreshSummary();
    }

    [RelayCommand]
    private void MoveUp(QueueItemViewModel? item) => Move(item, -1);

    [RelayCommand]
    private void MoveDown(QueueItemViewModel? item) => Move(item, +1);

    private void Move(QueueItemViewModel? item, int delta)
    {
        if (item == null)
        {
            return;
        }

        var index = Items.IndexOf(item);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= Items.Count)
        {
            return;
        }

        Items.Move(index, target);
    }

    [RelayCommand]
    private async Task OpenOutputAsync(QueueItemViewModel? item)
    {
        if (item == null)
        {
            return;
        }

        var target = item.HasResult && File.Exists(item.ResultPath) ? Path.GetDirectoryName(item.ResultPath) ?? item.OutputFolder : item.OutputFolder;
        if (!string.IsNullOrEmpty(target) && Directory.Exists(target))
        {
            await _dialogs.OpenFolderAsync(target);
        }
    }

    [RelayCommand]
    private async Task CopyLogAsync()
    {
        var item = SelectedItem;
        if (item == null || item.LogEntries.Count == 0)
        {
            return;
        }

        var text = string.Join(Environment.NewLine, item.LogEntries.Select(entry => $"[{entry.TimeText}] {entry.Message}"));
        await _dialogs.SetClipboardAsync(text);
    }

    /// <summary>Bắt đầu các mục đang chờ cho tới khi đủ số lượt song song.</summary>
    private void Pump()
    {
        if (!IsDispatching)
        {
            RefreshSummary();
            return;
        }

        while (Items.Count(item => item.IsRunning) < Concurrency)
        {
            var next = Items.FirstOrDefault(item => item.IsWaiting);
            if (next == null)
            {
                break;
            }

            // Hai mục cùng Content ID vào cùng thư mục xuất mà chạy song song sẽ ghi đè nhau: bỏ qua mục sau, người dùng xếp lại nếu cần.
            var clash = Items.FirstOrDefault(other => other != next && other.IsRunning
                && !string.IsNullOrEmpty(next.ContentId) && string.Equals(other.ContentId, next.ContentId, StringComparison.OrdinalIgnoreCase)
                && PathsEqual(other.OutputFolder, next.OutputFolder));
            if (clash != null)
            {
                next.Status = QueueItemStatus.Skipped;
                next.ErrorText = Loc.F("Queue.SameOutput", clash.Title);
                continue;
            }

            _ = RunItemAsync(next);
        }

        if (!Items.Any(item => item.IsWaiting) && !Items.Any(item => item.IsRunning))
        {
            IsDispatching = false;
            _timer.Stop();
            _startedAt = null;
        }

        RefreshSummary();
    }

    private async Task RunItemAsync(QueueItemViewModel item)
    {
        item.Status = QueueItemStatus.Running;
        item.ResetForRun();
        item.Cancellation = new CancellationTokenSource();
        var token = item.Cancellation.Token;
        RefreshSummary();

        try
        {
            if (item.Metadata == null && item.MetadataCancellation is { IsCancellationRequested: false })
            {
                // Chưa đọc xong param.json: chờ tối đa vài giây rồi cứ chạy (engine tự đọc lại).
                for (var i = 0; i < 50 && item.Metadata == null; i++)
                {
                    await Task.Delay(100, token);
                }
            }

            var request = _requestFactory(item);
            item.OutputFolder = request.OutputFolder;
            var outcome = await _engine.BuildAsync(
                request,
                item.EnqueueLog,
                new Progress<BuildProgress>(item.ReportProgress),
                token,
                _ => false,
                _ =>
                {
                    item.EnqueueLog(new LogEntry(LogLevel.Warning, Loc.T("Queue.ConflictKept")));
                    return OutputConflictChoice.KeepExisting;
                });

            item.Drain();
            foreach (var warning in outcome.Warnings)
            {
                item.EnqueueLog(new LogEntry(LogLevel.Warning, Loc.F("Build.Warning", warning)));
            }

            item.WarningCount = outcome.Warnings.Count;
            item.ResultPath = outcome.OutputPath;
            item.ResultSize = Formatters.Size(outcome.Verification.Length);
            item.EnqueueLog(new LogEntry(LogLevel.Success, Loc.F("Build.Success", Formatters.Duration(outcome.Elapsed), outcome.OutputPath)));
            item.Percent = 100;
            item.PhaseText = Loc.T("Phase.Done");
            item.EtaText = string.Empty;
            item.Status = QueueItemStatus.Done;
            _mainLog(LogLevel.Success, Loc.F("Queue.ItemDone", item.Title, Formatters.Duration(outcome.Elapsed), outcome.OutputPath));
        }
        catch (OperationCanceledException)
        {
            item.EnqueueLog(new LogEntry(LogLevel.Warning, Loc.T("Build.UserCanceled")));
            item.PhaseText = Loc.T("Phase.Canceled");
            item.EtaText = string.Empty;
            item.Status = QueueItemStatus.Canceled;
            _mainLog(LogLevel.Warning, Loc.F("Queue.ItemCanceled", item.Title));
        }
        catch (Exception ex)
        {
            item.EnqueueLog(new LogEntry(LogLevel.Error, Loc.F("Build.Error", ex.Message)));
            item.ErrorText = ex.Message;
            item.PhaseText = Loc.T("Phase.Stopped");
            item.EtaText = string.Empty;
            item.Status = QueueItemStatus.Failed;
            _mainLog(LogLevel.Error, Loc.F("Queue.ItemFailed", item.Title, ex.Message));
        }
        finally
        {
            item.Stopwatch.Stop();
            item.ElapsedText = Formatters.Clock(item.Stopwatch.Elapsed);
            item.Cancellation?.Dispose();
            item.Cancellation = null;
            item.Drain();
            Pump();
        }
    }

    private void Tick()
    {
        foreach (var item in Items)
        {
            if (item.IsRunning)
            {
                item.Drain();
            }
        }

        if (_startedAt is { } started)
        {
            ElapsedText = Formatters.Clock(DateTime.UtcNow - started);
        }

        RefreshProgress();
    }

    private void RefreshProgress()
    {
        var total = Items.Count(item => !item.IsSkipped);
        if (total == 0)
        {
            OverallPercent = 0;
            return;
        }

        double sum = 0;
        foreach (var item in Items)
        {
            if (item.IsSkipped)
            {
                continue;
            }

            sum += item.IsDone ? 100 : item.IsRunning ? item.Percent : item.IsFinished ? 100 : 0;
        }

        OverallPercent = sum / total;
    }

    private void RefreshSummary()
    {
        WaitingCount = Items.Count(item => item.IsWaiting);
        RunningCount = Items.Count(item => item.IsRunning);
        DoneCount = Items.Count(item => item.IsDone);
        FailedCount = Items.Count(item => item.IsFailed || item.IsCanceled || item.IsSkipped);
        SummaryText = Loc.F("Queue.Summary", WaitingCount, RunningCount, DoneCount, FailedCount);
        HeadlineText = Items.Count == 0
            ? Loc.T("Queue.HeadlineEmpty")
            : IsBusy
                ? Loc.F("Queue.HeadlineRunning", DoneCount, Items.Count, RunningCount)
                : Loc.F("Queue.HeadlineIdle", Items.Count, DoneCount, FailedCount);
        RefreshProgress();
        RefreshCommandStates();
    }

    private void RefreshCommandStates()
    {
        foreach (var name in new[] { nameof(IsRunning), nameof(IsBusy), nameof(HasItems), nameof(IsEmpty), nameof(HasFinished), nameof(CanStart), nameof(CanPause), nameof(CanCancelAll) })
        {
            OnPropertyChanged(name);
        }
    }

    /// <summary>Đóng ứng dụng: huỷ mọi lượt đang chạy.</summary>
    public void Shutdown()
    {
        IsDispatching = false;
        foreach (var item in Items)
        {
            item.MetadataCancellation?.Cancel();
            if (item.IsRunning)
            {
                item.Cancellation?.Cancel();
            }
        }

        _timer.Stop();
    }
}
