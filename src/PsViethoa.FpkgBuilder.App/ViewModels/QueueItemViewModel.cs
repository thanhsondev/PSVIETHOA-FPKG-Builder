using System.Collections.Concurrent;
using System.Diagnostics;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using PsViethoa.FpkgBuilder.App.Services;
using PsViethoa.FpkgBuilder.Core.Localization;
using PsViethoa.FpkgBuilder.Core.Models;
using PsViethoa.FpkgBuilder.Core.Services;

namespace PsViethoa.FpkgBuilder.App.ViewModels;

public enum QueueItemStatus
{
    Waiting,
    Running,
    Done,
    Failed,
    Canceled,

    /// <summary>Bị bỏ qua trước khi chạy (trùng mục khác, nguồn không hợp lệ…).</summary>
    Skipped,
}

/// <summary>
/// Một game trong hàng chờ: nguồn, thông tin đọc từ param.json, tiến trình và nhật ký riêng. Nhật ký/tiến trình từ engine đến
/// trên luồng nền được xếp vào hàng đợi, <see cref="Drain"/> đổ sang giao diện theo nhịp của <see cref="QueueViewModel"/>.
/// </summary>
public sealed partial class QueueItemViewModel : ObservableObject
{
    private readonly ConcurrentQueue<LogEntry> _pendingLogs = new();
    private BuildProgress? _pendingProgress;
    private BuildProgress? _lastProgress;
    private DateTime _lastProgressAt;

    public QueueItemViewModel(string sourcePath)
    {
        SourcePath = sourcePath;
        Title = Path.GetFileName(Path.TrimEndingDirectorySeparator(sourcePath));
        SizeText = Loc.T("Queue.MetaReading");
    }

    public string SourcePath { get; }

    /// <summary>Tệp/thư mục nguồn hiển thị gọn (tên cuối của đường dẫn).</summary>
    public string SourceName => Path.GetFileName(Path.TrimEndingDirectorySeparator(SourcePath));

    [ObservableProperty] private string _title = string.Empty;

    /// <summary>icon0.png của game (thu nhỏ), null khi nguồn không có.</summary>
    [ObservableProperty] private Bitmap? _icon;

    public bool HasIcon => Icon != null;

    partial void OnIconChanged(Bitmap? value) => OnPropertyChanged(nameof(HasIcon));

    [ObservableProperty] private string _contentId = string.Empty;

    [ObservableProperty] private string _version = string.Empty;

    [ObservableProperty] private string _sizeText = string.Empty;

    [ObservableProperty] private string _outputFolder = string.Empty;

    [ObservableProperty] private QueueItemStatus _status = QueueItemStatus.Waiting;

    [ObservableProperty] private double _percent;

    [ObservableProperty] private string _phaseText = string.Empty;

    [ObservableProperty] private string _elapsedText = string.Empty;

    [ObservableProperty] private string _etaText = string.Empty;

    [ObservableProperty] private string _resultPath = string.Empty;

    [ObservableProperty] private string _resultSize = string.Empty;

    [ObservableProperty] private string _errorText = string.Empty;

    [ObservableProperty] private int _warningCount;

    [ObservableProperty] private int _logCount;

    /// <summary>Không đọc được param.json / nguồn không hợp lệ — vẫn cho vào hàng chờ nhưng đánh dấu để người dùng thấy.</summary>
    [ObservableProperty] private bool _metadataWarning;

    public LogCollection LogEntries { get; } = new();

    internal Stopwatch Stopwatch { get; } = new();

    internal CancellationTokenSource? Cancellation { get; set; }

    internal SourceMetadata? Metadata { get; set; }

    internal CancellationTokenSource? MetadataCancellation { get; set; }

    public bool IsWaiting => Status == QueueItemStatus.Waiting;

    public bool IsRunning => Status == QueueItemStatus.Running;

    public bool IsDone => Status == QueueItemStatus.Done;

    public bool IsFailed => Status == QueueItemStatus.Failed;

    public bool IsCanceled => Status == QueueItemStatus.Canceled;

    public bool IsSkipped => Status == QueueItemStatus.Skipped;

    public bool IsFinished => Status is QueueItemStatus.Done or QueueItemStatus.Failed or QueueItemStatus.Canceled or QueueItemStatus.Skipped;

    public bool CanCancel => Status is QueueItemStatus.Running or QueueItemStatus.Waiting;

    public bool CanRetry => Status is QueueItemStatus.Failed or QueueItemStatus.Canceled or QueueItemStatus.Skipped;

    public bool CanRemove => Status != QueueItemStatus.Running;

    public bool HasResult => !string.IsNullOrEmpty(ResultPath);

    public bool HasError => !string.IsNullOrEmpty(ErrorText);

    public bool HasWarnings => WarningCount > 0;

    public string StatusText => Loc.T("Queue.Status." + Status);

    /// <summary>Dòng phụ dưới tên game: Content ID · phiên bản · dung lượng.</summary>
    public string Subtitle
    {
        get
        {
            var parts = new List<string>(3);
            if (!string.IsNullOrEmpty(ContentId))
            {
                parts.Add(ContentId);
            }

            if (!string.IsNullOrEmpty(Version))
            {
                parts.Add("v" + Version);
            }

            if (!string.IsNullOrEmpty(SizeText))
            {
                parts.Add(SizeText);
            }

            return parts.Count == 0 ? SourcePath : string.Join(" · ", parts);
        }
    }

    partial void OnStatusChanged(QueueItemStatus value)
    {
        foreach (var name in new[] { nameof(IsWaiting), nameof(IsRunning), nameof(IsDone), nameof(IsFailed), nameof(IsCanceled), nameof(IsSkipped), nameof(IsFinished), nameof(CanCancel), nameof(CanRetry), nameof(CanRemove), nameof(StatusText) })
        {
            OnPropertyChanged(name);
        }
    }

    partial void OnContentIdChanged(string value) => OnPropertyChanged(nameof(Subtitle));

    partial void OnVersionChanged(string value) => OnPropertyChanged(nameof(Subtitle));

    partial void OnSizeTextChanged(string value) => OnPropertyChanged(nameof(Subtitle));

    partial void OnResultPathChanged(string value) => OnPropertyChanged(nameof(HasResult));

    partial void OnErrorTextChanged(string value) => OnPropertyChanged(nameof(HasError));

    partial void OnWarningCountChanged(int value) => OnPropertyChanged(nameof(HasWarnings));

    /// <summary>Đổi ngôn ngữ giao diện: các chuỗi dịch sẵn cần tính lại.</summary>
    public void RefreshLanguage() => OnPropertyChanged(nameof(StatusText));

    /// <summary>Gọi từ luồng bất kỳ (engine).</summary>
    internal void EnqueueLog(LogEntry entry) => _pendingLogs.Enqueue(entry);

    /// <summary>Gọi từ luồng bất kỳ (engine).</summary>
    internal void ReportProgress(BuildProgress progress) => Interlocked.Exchange(ref _pendingProgress, progress);

    /// <summary>Chuẩn bị chạy (lại): xoá kết quả/nhật ký cũ.</summary>
    internal void ResetForRun()
    {
        LogEntries.Clear();
        LogCount = 0;
        while (_pendingLogs.TryDequeue(out _))
        {
        }

        Interlocked.Exchange(ref _pendingProgress, null);
        _lastProgress = null;
        Percent = 0;
        PhaseText = Loc.T("Phase.Preparing") + "…";
        ElapsedText = "00:00";
        EtaText = string.Empty;
        ResultPath = string.Empty;
        ResultSize = string.Empty;
        ErrorText = string.Empty;
        WarningCount = 0;
        Stopwatch.Restart();
    }

    /// <summary>Đổ nhật ký và tiến trình đang chờ sang giao diện (luồng giao diện).</summary>
    internal void Drain()
    {
        var progress = Interlocked.Exchange(ref _pendingProgress, null);
        if (progress != null)
        {
            _lastProgress = progress;
            _lastProgressAt = DateTime.UtcNow;
            Percent = Math.Clamp(progress.OverallPercent, 0, 100);
            PhaseText = progress.IsComplete ? Loc.T("Phase.Done") : $"{progress.Phase} · {progress.PhasePercent:0}%";
        }

        if (IsRunning)
        {
            ElapsedText = Formatters.Clock(Stopwatch.Elapsed);
            UpdateEta();
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
    }

    private void UpdateEta()
    {
        if (_lastProgress?.Eta is not { } eta || _lastProgress.IsComplete)
        {
            EtaText = _lastProgress != null && _lastProgress.OverallPercent > 0 ? Loc.T("Eta.Estimating") : string.Empty;
            return;
        }

        var remaining = eta - (DateTime.UtcNow - _lastProgressAt);
        if (remaining < TimeSpan.FromSeconds(1))
        {
            remaining = TimeSpan.FromSeconds(1);
        }

        EtaText = Loc.F("Eta.Remaining", Formatters.Duration(remaining));
    }
}
