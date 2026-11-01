using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using Microsoft.Win32.SafeHandles;
using PsViethoa.FpkgBuilder.Core.Localization;
using PsViethoa.FpkgBuilder.Core.Services;

namespace PsViethoa.FpkgBuilder.Core.Ftp;

// Hàng đợi truyền kiểu FileZilla. Chuyển từ PSVIETHOA STUDIO (Ngô Phi Phương), dùng với sự đồng ý của tác giả.

public enum FtpDirection
{
    Upload,
    Download,
}

public enum FtpItemState
{
    Queued,
    Running,
    Done,
    Failed,
    Skipped,
    Cancelled,
}

/// <summary>Làm gì khi tệp đích đã tồn tại.</summary>
public enum FtpExistsPolicy
{
    /// <summary>Hỏi qua <see cref="FtpTransferQueue.AskExists"/>; không có hàm hỏi thì coi như Resume.</summary>
    Ask,

    Overwrite,

    /// <summary>Ghi tiếp tệp dở bằng REST; ghi đè khi đích đã lớn hơn nguồn.</summary>
    Resume,

    Skip,

    OverwriteIfNewer,

    OverwriteIfSizeDiffers,
}

public sealed record FtpConnectionInfo(string Host, int Port, string User, string Pass)
{
    public override string ToString() => $"{User}@{Host}:{Port}";
}

/// <summary>
/// Một lần kéo thả hoặc một lựa chọn (một thư mục, hoặc một nhóm tệp lẻ). Chỉ là mô tả bất biến: giao diện tự cộng dồn tiến độ,
/// tốc độ và thời gian còn lại của từng nhóm từ các <see cref="FtpTransferItem"/> có <see cref="FtpTransferItem.Job"/> trỏ về đây.
/// </summary>
public sealed class FtpTransferJob(FtpDirection direction, string name, string source, string target)
{
    public FtpDirection Direction { get; } = direction;

    public string Name { get; } = name;

    public string Source { get; } = source;

    public string Target { get; } = target;

    public DateTime Created { get; } = DateTime.Now;

    public bool IsFolder { get; init; }
}

/// <summary>
/// Một tệp trong hàng đợi. Không phát sự kiện đổi thuộc tính: luồng làm việc ghi, giao diện đọc theo nhịp hẹn giờ.
/// Size, Transferred, State và Speed được đọc/ghi nguyên tử nên đọc từ luồng giao diện lúc nào cũng an toàn.
/// </summary>
public sealed class FtpTransferItem
{
    private int state = (int)FtpItemState.Queued;
    private long transferred;
    private long size;
    private double speed;

    public FtpTransferItem(FtpDirection direction, string localPath, string remotePath, long size)
    {
        Direction = direction;
        LocalPath = localPath;
        RemotePath = remotePath;
        this.size = size;
        Name = direction == FtpDirection.Upload ? Path.GetFileName(localPath) : Ps5Ftp.FileName(remotePath);
    }

    public FtpDirection Direction { get; }

    public string LocalPath { get; }

    public string RemotePath { get; }

    /// <summary>Tên tệp: lấy từ đường dẫn máy tính khi tải lên, từ đường dẫn PS5 khi tải về.</summary>
    public string Name { get; }

    /// <summary>Dung lượng tệp; -1 khi chưa biết (tải về sẽ hỏi SIZE) hoặc với mục đánh dấu thư mục.</summary>
    public long Size
    {
        get => Interlocked.Read(ref size);
        set => Interlocked.Exchange(ref size, value);
    }

    public long Transferred
    {
        get => Interlocked.Read(ref transferred);
        set => Interlocked.Exchange(ref transferred, value);
    }

    public FtpItemState State
    {
        get => (FtpItemState)Volatile.Read(ref state);
        set => Volatile.Write(ref state, (int)value);
    }

    public string Error { get; set; } = "";

    /// <summary>Tốc độ đã làm mượt, byte/giây; 0 khi không chạy.</summary>
    public double Speed
    {
        get => Volatile.Read(ref speed);
        set => Volatile.Write(ref speed, value);
    }

    /// <summary>Số đoạn REST song song khi tải về tệp lớn (1 = một luồng).</summary>
    public int Segments { get; set; } = 1;

    /// <summary>Số giây ước tính tới khi tệp này xong, do giao diện tính từ <see cref="FtpEtaModel"/>; -1 = chưa biết.</summary>
    public double Estimate { get; set; } = -1;

    public int Attempts { get; set; }

    /// <summary>Chỉ tạo một thư mục trên PS5 (để thư mục rỗng cũng được chép).</summary>
    public bool IsDirectoryMarker { get; init; }

    /// <summary>Luôn thay đích (dùng khi tải lại tệp vừa sửa), bất kể chính sách tệp trùng của hàng đợi.</summary>
    public bool ForceOverwrite { get; init; }

    /// <summary>Thư mục hoặc lựa chọn mà tệp này được xếp hàng cùng.</summary>
    public FtpTransferJob? Job { get; init; }

    public DateTime? Started { get; set; }

    public DateTime? Finished { get; set; }

    /// <summary>Thời điểm sửa trên PS5 lấy từ danh sách thư mục, gán cho bản sao trên máy tính sau khi tải về.</summary>
    public DateTime? RemoteModified { get; set; }

    public double Percent
    {
        get
        {
            var total = Size;
            return total <= 0 ? (State == FtpItemState.Done ? 100 : 0) : Math.Min(100.0, Transferred * 100.0 / total);
        }
    }
}

/// <summary>
/// Học xem phần còn lại của hàng đợi sẽ mất bao lâu. Mỗi tệp tốn một khoản cố định trên luồng của nó (kết nối, SIZE, PASV,
/// STOR/RETR, MFMT: thứ làm hàng nghìn tệp nhỏ chậm), các luồng song song chồng được khoản này lên nhau; cộng với dung lượng
/// của nó trên đường truyền chung, khoản mà các luồng không chồng được. Mỗi giây được nạp số tệp vừa xong, số byte vừa chuyển và
/// số luồng bận trung bình, mô hình khớp giây ≈ tệp / luồng × SecondsPerFile + MB × SecondsPerMb bằng bình phương tối thiểu đệ
/// quy có quên (nhớ ~15 giây), kéo nhẹ về giá trị ban đầu hợp lý để mấy giây đầu và tải lệch một phía (chỉ toàn tệp rất nhỏ,
/// hoặc một tệp rất lớn) vẫn cho kết quả ổn định. Dự báo nhận số luồng sẽ chạy, nên hàng đợi tự tăng từ 2 lên 16 luồng cho
/// tệp nhỏ được ước tính với 16.
/// </summary>
public sealed class FtpEtaModel
{
    private const double Forget = 0.93;
    private const double Shrink = 0.04;
    private const double PriorPerFile = 0.05;
    private const double PriorPerMb = 1 / 100.0;
    private double s11;
    private double s12;
    private double s22;
    private double t1;
    private double t2;

    public double SecondsPerFile { get; private set; } = PriorPerFile;

    public double SecondsPerMb { get; private set; } = PriorPerMb;

    public int Samples { get; private set; }

    public bool Ready => Samples >= 3;

    public void Reset()
    {
        s11 = s12 = s22 = t1 = t2 = 0;
        Samples = 0;
        SecondsPerFile = PriorPerFile;
        SecondsPerMb = PriorPerMb;
    }

    /// <param name="seconds">Thời gian thực của mẫu (thường ~1 giây) trong lúc hàng đợi bận.</param>
    /// <param name="files">Số tệp (và mục đánh dấu thư mục) xong trong khoảng đó.</param>
    /// <param name="bytes">Số byte đã chuyển trong khoảng đó.</param>
    /// <param name="workers">Số luồng bận trung bình trong mẫu.</param>
    public void Add(double seconds, int files, long bytes, double workers = 1)
    {
        if (seconds <= 0 || (files <= 0 && bytes <= 0))
        {
            return;
        }

        double f = Math.Max(0, files) / Math.Max(1, workers);
        double m = Math.Max(0, bytes) / 1048576.0;
        s11 = s11 * Forget + f * f;
        s12 = s12 * Forget + f * m;
        s22 = s22 * Forget + m * m;
        t1 = t1 * Forget + f * seconds;
        t2 = t2 * Forget + m * seconds;
        Samples++;

        // Ridge kéo về giá trị trước, tỉ lệ theo dữ liệu nên không bao giờ lấn át khi đã có mẫu thật.
        var l1 = Shrink * s11 + 1e-3;
        var l2 = Shrink * s22 + 1e-3;
        double a = s11 + l1;
        double b = s12;
        double d = s22 + l2;
        double r1 = t1 + l1 * SecondsPerFile;
        double r2 = t2 + l2 * SecondsPerMb;
        var det = a * d - b * b;
        if (det <= 1e-12)
        {
            return;
        }

        var u = (r1 * d - b * r2) / det;
        var v = (a * r2 - b * r1) / det;
        // Hệ số âm là do các mẫu gần thẳng hàng: giữ hệ số kia và khớp lại riêng hệ số này.
        if (u < 0)
        {
            u = 0;
            v = s22 > 0 ? t2 / s22 : SecondsPerMb;
        }

        if (v < 0)
        {
            v = 0;
            u = s11 > 0 ? t1 / s11 : SecondsPerFile;
        }

        SecondsPerFile = Math.Clamp(u, 0, 10);
        SecondsPerMb = Math.Clamp(v, 1 / 1500.0, 120);
    }

    /// <summary>Số giây ước tính cho <paramref name="files"/> tệp nữa, chứa <paramref name="bytes"/> byte nữa, trên <paramref name="workers"/> luồng.</summary>
    public double Cost(double files, long bytes, double workers = 1) =>
        files / Math.Max(1, workers) * SecondsPerFile + Math.Max(0, bytes) / 1048576.0 * SecondsPerMb;
}

/// <summary>
/// Hàng đợi truyền kiểu FileZilla. Các tệp chạy song song, mỗi luồng một kết nối FTP riêng; một tệp tải về lớn hơn
/// <see cref="SegmentThreshold"/> còn được chia thành <see cref="SegmentsPerFile"/> đoạn REST trên các kết nối riêng, nhờ vậy một
/// tệp lớn cũng lấp đầy được đường 1 Gbps.
/// Khi bật <see cref="AutoWorkers"/>, số luồng bám theo hàng đợi: tăng dần tới <see cref="MaxWorkers"/> khi nhiều tệp nhỏ đang chờ
/// (nút thắt là chi phí từng tệp, không phải đường truyền) và giảm còn <see cref="HeavyWorkers"/> khi một tệp từ
/// <see cref="HeavyFileBytes"/> trở lên đang chạy (nhiều luồng lớn song song chỉ làm đĩa PS5 phải nhảy đầu đọc). Kết nối bị PS5 từ
/// chối hạ một mức trần được ghi nhớ thay vì làm lỗi tệp.
/// </summary>
public sealed class FtpTransferQueue : IDisposable
{
    /// <summary>Giới hạn cứng số luồng truyền tệp song song.</summary>
    public const int MaxWorkersLimit = 64;

    private readonly LinkedList<FtpTransferItem> pending = new();
    private readonly object pendingLock = new();
    private readonly List<FtpTransferItem> all = [];
    private readonly object gate = new();
    private readonly object control = new();
    private readonly ConcurrentDictionary<string, byte> madeDirs = new(StringComparer.Ordinal);
    private CancellationTokenSource? run;
    private long bytesMoved;
    private int finishedCount;
    private int active;
    private int target = 1;
    private int heavyActive;
    private int ceiling = MaxWorkersLimit;
    private volatile string autoReason = "";

    public FtpTransferQueue(FtpConnectionInfo connection)
    {
        Connection = connection;
    }

    public FtpConnectionInfo Connection { get; set; }

    /// <summary>Số tệp song song; là mức cơ bản khi bật <see cref="AutoWorkers"/>.</summary>
    public int Workers { get; set; } = 2;

    public bool AutoWorkers { get; set; }

    /// <summary>Mức trần khi chế độ tự động thêm luồng cho nhiều tệp nhỏ.</summary>
    public int MaxWorkers { get; set; } = 16;

    /// <summary>Tệp từ cỡ này trở lên là "nặng": khi một tệp nặng đang chạy, hàng đợi chạy tối đa <see cref="HeavyWorkers"/> tệp.</summary>
    public long HeavyFileBytes { get; set; } = 2L * 1024 * 1024 * 1024;

    public int HeavyWorkers { get; set; } = 2;

    /// <summary>Tệp dưới cỡ này tính là "nhỏ" khi chế độ tự động quyết định thêm luồng.</summary>
    public long SmallFileBytes { get; set; } = 16L * 1024 * 1024;

    public int SegmentsPerFile { get; set; } = 4;

    public long SegmentThreshold { get; set; } = 64L * 1024 * 1024;

    public int MaxAttempts { get; set; } = 3;

    public bool PreserveTimestamps { get; set; } = true;

    public FtpExistsPolicy ExistsPolicy { get; set; } = FtpExistsPolicy.Resume;

    public int BufferSize { get; set; } = 1024 * 1024;

    public int SocketBufferBytes { get; set; } = 4 * 1024 * 1024;

    public int TimeoutSeconds { get; set; } = 20;

    /// <summary>Được gọi trên luồng làm việc khi <see cref="ExistsPolicy"/> là Ask; trả về quyết định cho tệp này (đối số thứ hai là dung lượng đích đang có).</summary>
    public Func<FtpTransferItem, long, FtpExistsPolicy>? AskExists { get; set; }

    public bool Running => run != null && !run.IsCancellationRequested;

    public IReadOnlyList<FtpTransferItem> Items
    {
        get
        {
            lock (gate)
            {
                return all.ToList();
            }
        }
    }

    /// <summary>Tổng số byte đã chuyển từ khi tạo hàng đợi, cho đồng hồ tốc độ chung.</summary>
    public long BytesMoved => Interlocked.Read(ref bytesMoved);

    /// <summary>Số tệp (và mục đánh dấu thư mục) đã xong ở trạng thái Done hoặc Skipped từ khi tạo hàng đợi.</summary>
    public int FinishedCount => Volatile.Read(ref finishedCount);

    public int ActiveWorkers => Volatile.Read(ref active);

    public int TargetWorkers => Volatile.Read(ref target);

    public int HeavyActive => Volatile.Read(ref heavyActive);

    /// <summary>Số kết nối PS5 đã nhận trước khi từ chối một kết nối (MaxWorkersLimit = chưa từ chối lần nào).</summary>
    public int WorkerCeiling => Volatile.Read(ref ceiling);

    /// <summary>Vì sao chế độ tự động chọn số luồng hiện tại (rỗng = dùng mức cơ bản).</summary>
    public string AutoReason => autoReason;

    public event Action<FtpTransferItem>? ItemAdded;

    public event Action<FtpTransferItem>? ItemFinished;

    public event Action? Idle;

    public event Action<FtpLogLine>? Log;

    public void Enqueue(FtpTransferItem item)
    {
        lock (gate)
        {
            all.Add(item);
        }

        lock (pendingLock)
        {
            pending.AddLast(item);
        }

        ItemAdded?.Invoke(item);
    }

    /// <summary>Xếp hàng mọi tệp trong <paramref name="localDir"/>; thư mục trên PS5 được tạo khi cần (kể cả thư mục rỗng).</summary>
    public int EnqueueUploadTree(string localDir, string remoteDir, FtpTransferJob? job = null)
    {
        var count = 0;
        var root = new DirectoryInfo(localDir);
        var remoteRoot = Ps5Ftp.Combine(remoteDir, root.Name);
        job ??= new FtpTransferJob(FtpDirection.Upload, root.Name, root.FullName, remoteRoot) { IsFolder = true };
        madeDirs.TryRemove(remoteRoot, out _);
        var walk = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 };
        Enqueue(new FtpTransferItem(FtpDirection.Upload, root.FullName, remoteRoot, -1) { IsDirectoryMarker = true, Job = job });
        foreach (var sub in root.EnumerateDirectories("*", walk))
        {
            var relDir = Path.GetRelativePath(root.FullName, sub.FullName).Replace('\\', '/');
            Enqueue(new FtpTransferItem(FtpDirection.Upload, sub.FullName, Ps5Ftp.Combine(remoteRoot, relDir), -1) { IsDirectoryMarker = true, Job = job });
        }

        foreach (var file in root.EnumerateFiles("*", walk))
        {
            var relative = Path.GetRelativePath(root.FullName, file.FullName).Replace('\\', '/');
            Enqueue(new FtpTransferItem(FtpDirection.Upload, file.FullName, Ps5Ftp.Combine(remoteRoot, relative), file.Length) { Job = job });
            count++;
        }

        return count;
    }

    /// <summary>Duyệt một thư mục trên PS5 bằng <paramref name="browser"/> và xếp hàng tải mọi tệp về <paramref name="localDir"/>.</summary>
    public async Task<int> EnqueueDownloadTreeAsync(Ps5Ftp browser, string remoteDir, string localDir, CancellationToken ct = default, FtpTransferJob? job = null)
    {
        var count = 0;
        var targetDir = Path.Combine(localDir, Ps5Ftp.FileName(remoteDir));
        job ??= new FtpTransferJob(FtpDirection.Download, Ps5Ftp.FileName(remoteDir), remoteDir, targetDir) { IsFolder = true };
        Directory.CreateDirectory(targetDir);
        var stack = new Stack<(string Remote, string Local)>();
        stack.Push((Ps5Ftp.NormalizeDir(remoteDir), targetDir));
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (remote, local) = stack.Pop();
            Directory.CreateDirectory(local);
            foreach (var entry in await browser.ListAsync(remote, ct).ConfigureAwait(false))
            {
                var localPath = Path.Combine(local, entry.Name);
                if (entry.IsDirectory)
                {
                    stack.Push((entry.FullPath, localPath));
                }
                else
                {
                    Enqueue(new FtpTransferItem(FtpDirection.Download, localPath, entry.FullPath, entry.Size) { RemoteModified = entry.Modified, Job = job });
                    count++;
                }
            }
        }

        return count;
    }

    public void Start()
    {
        if (Running)
        {
            return;
        }

        run = new CancellationTokenSource();
        var token = run.Token;
        Volatile.Write(ref ceiling, MaxWorkersLimit);
        _ = Task.Run(() => ControlAsync(token), CancellationToken.None);
    }

    /// <summary>Dừng mọi luồng. Tệp đang chạy chuyển sang Cancelled và giữ phần đã có để tiếp tục (resume) sau.</summary>
    public void Stop() => run?.Cancel();

    /// <summary>Đưa các tệp lỗi và đã huỷ trở lại hàng đợi.</summary>
    public int RetryFailed()
    {
        var n = 0;
        foreach (var item in Items.Where(i => i.State is FtpItemState.Failed or FtpItemState.Cancelled))
        {
            item.State = FtpItemState.Queued;
            item.Error = "";
            item.Attempts = 0;
            item.Speed = 0;
            lock (pendingLock)
            {
                pending.AddLast(item);
            }

            n++;
        }

        return n;
    }

    public void ClearFinished()
    {
        lock (gate)
        {
            all.RemoveAll(i => i.State is FtpItemState.Done or FtpItemState.Skipped);
        }
    }

    public void Remove(FtpTransferItem item)
    {
        if (item.State == FtpItemState.Running)
        {
            return;
        }

        item.State = FtpItemState.Cancelled;
        lock (gate)
        {
            all.Remove(item);
        }
    }

    public void Dispose()
    {
        Stop();
    }

    // ── Điều phối luồng ────────────────────────────────────────────────────
    private bool HasQueued()
    {
        lock (pendingLock)
        {
            for (var n = pending.First; n != null; n = n.Next)
            {
                if (n.Value.State == FtpItemState.Queued)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private bool IsHeavy(FtpTransferItem item) => !item.IsDirectoryMarker && item.Size >= HeavyFileBytes;

    /// <summary>Số luồng hàng đợi sẽ dùng cho phần đang chờ (không tính tệp nặng đang chạy lúc này).</summary>
    public int PlannedWorkers()
    {
        var baseWorkers = Math.Clamp(Workers, 1, MaxWorkersLimit);
        var value = baseWorkers;
        if (AutoWorkers)
        {
            var small = CountSmallQueued();
            if (small >= 8)
            {
                value = Math.Clamp(baseWorkers + small / 4, baseWorkers, Math.Clamp(MaxWorkers, baseWorkers, MaxWorkersLimit));
            }
        }

        return Math.Max(1, Math.Min(value, Volatile.Read(ref ceiling)));
    }

    /// <summary>Đếm tệp nhỏ (và mục thư mục) trong tối đa 512 mục đang chờ đầu hàng.</summary>
    private int CountSmallQueued()
    {
        int small = 0;
        int scanned = 0;
        lock (pendingLock)
        {
            for (var n = pending.First; n != null && scanned < 512; n = n.Next)
            {
                var it = n.Value;
                if (it.State != FtpItemState.Queued)
                {
                    continue;
                }

                scanned++;
                if (it.IsDirectoryMarker || it.Size is >= 0 && it.Size < SmallFileBytes)
                {
                    small++;
                }
            }
        }

        return small;
    }

    /// <summary>Tính lại số luồng nên chạy ngay lúc này. Rẻ: chỉ xem vài trăm mục đang chờ đầu hàng.</summary>
    private int UpdateTarget()
    {
        var baseWorkers = Math.Clamp(Workers, 1, MaxWorkersLimit);
        int value;
        var why = "";
        if (!AutoWorkers)
        {
            value = baseWorkers;
        }
        else
        {
            var small = CountSmallQueued();
            var cap = Math.Clamp(MaxWorkers, baseWorkers, MaxWorkersLimit);
            value = baseWorkers;
            if (small >= 8)
            {
                value = Math.Clamp(baseWorkers + small / 4, baseWorkers, cap);
                if (value > baseWorkers)
                {
                    why = Loc.F("Ftp.Core.AutoSmallFiles", small >= 512 ? "512+" : small.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
            }

            if (Volatile.Read(ref heavyActive) > 0)
            {
                var heavyCap = Math.Clamp(HeavyWorkers, 1, MaxWorkersLimit);
                if (value > heavyCap)
                {
                    value = heavyCap;
                }

                why = Loc.F("Ftp.Core.AutoHeavyFile", Formatters.Size(HeavyFileBytes));
            }
        }

        var limit = Volatile.Read(ref ceiling);
        if (value > limit)
        {
            value = limit;
            why = (why.Length > 0 ? why + " · " : "") + Loc.F("Ftp.Core.AutoCeiling", limit);
        }

        lock (control)
        {
            target = Math.Max(1, value);
        }

        autoReason = why;
        return target;
    }

    private async Task ControlAsync(CancellationToken ct)
    {
        var live = new List<Task>();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                live.RemoveAll(t => t.IsCompleted);
                var want = UpdateTarget();
                var work = HasQueued();
                if (!work && live.Count == 0)
                {
                    break;
                }

                if (work)
                {
                    int spawn;
                    lock (control)
                    {
                        // Tăng từ từ (4 kết nối mới mỗi nhịp) để ftpsrv không bị dồn một loạt bắt tay cùng lúc.
                        spawn = Math.Max(0, Math.Min(want - active, live.Count == 0 ? want : 4));
                        active += spawn;
                    }

                    for (var i = 0; i < spawn; i++)
                    {
                        live.Add(Task.Run(() => WorkerAsync(ct), CancellationToken.None));
                    }
                }

                try
                {
                    await Task.Delay(250, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
        finally
        {
            try
            {
                await Task.WhenAll(live).ConfigureAwait(false);
            }
            catch
            {
                // Lỗi của từng luồng đã được ghi vào mục tương ứng; ở đây chỉ chờ chúng dừng.
            }

            run = null;
            Idle?.Invoke();
        }
    }

    /// <summary>Cho luồng này nghỉ khi đang có nhiều luồng hơn mức cần. Quyết định và đếm trong cùng một khoá.</summary>
    private bool TryRetire()
    {
        lock (control)
        {
            if (active <= target)
            {
                return false;
            }

            active--;
            return true;
        }
    }

    /// <summary>Lấy tệp kế tiếp đang chờ. Tệp nặng bị bỏ qua khi các suất tệp nặng đã đầy (tệp nhỏ vẫn chạy tiếp).</summary>
    private bool TryTake(out FtpTransferItem item, out bool blocked)
    {
        blocked = false;
        var heavyFull = AutoWorkers && Volatile.Read(ref heavyActive) >= Math.Clamp(HeavyWorkers, 1, MaxWorkersLimit);
        lock (pendingLock)
        {
            var n = pending.First;
            while (n != null)
            {
                var next = n.Next;
                var it = n.Value;
                if (it.State != FtpItemState.Queued)
                {
                    pending.Remove(n);
                    n = next;
                    continue;
                }

                if (heavyFull && IsHeavy(it))
                {
                    blocked = true;
                    n = next;
                    continue;
                }

                pending.Remove(n);
                item = it;
                return true;
            }
        }

        item = null!;
        return false;
    }

    private void GiveBack(FtpTransferItem item)
    {
        lock (pendingLock)
        {
            pending.AddFirst(item);
        }
    }

    private static bool IsRefusal(Exception ex) => ex switch
    {
        // Thiếu cổng cục bộ không phải là PS5 từ chối.
        SocketException s => s.SocketErrorCode is not (SocketError.AddressAlreadyInUse or SocketError.NoBufferSpaceAvailable),
        FtpException f => f.Code is 0 or 421,
        IOException => true,
        _ => false,
    };

    private Ps5Ftp NewClient()
    {
        var c = new Ps5Ftp { BufferSize = BufferSize, SocketBufferBytes = SocketBufferBytes, TimeoutSeconds = TimeoutSeconds };
        c.Log += line => Log?.Invoke(line);
        return c;
    }

    private async Task<Ps5Ftp> ConnectAsync(CancellationToken ct)
    {
        var c = NewClient();
        try
        {
            await c.ConnectAsync(Connection.Host, Connection.Port, Connection.User, Connection.Pass, ct).ConfigureAwait(false);
        }
        catch
        {
            c.Dispose();
            throw;
        }

        return c;
    }

    private async Task WorkerAsync(CancellationToken ct)
    {
        Ps5Ftp? client = null;
        // Luồng này vẫn còn được tính trong `active`.
        var counted = true;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (TryRetire())
                {
                    counted = false;
                    return;
                }

                if (!TryTake(out var item, out var blocked))
                {
                    if (!blocked)
                    {
                        return;
                    }

                    // Chỉ còn tệp nặng mà các suất tệp nặng đang bận.
                    await Task.Delay(200, ct).ConfigureAwait(false);
                    continue;
                }

                if (client == null)
                {
                    try
                    {
                        client = await ConnectAsync(ct).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (!ct.IsCancellationRequested && IsRefusal(ex) && ActiveWorkers > 1)
                    {
                        // PS5 không nhận thêm một kết nối: trả tệp lại hàng đợi và cho luồng này nghỉ.
                        GiveBack(item);
                        int now;
                        lock (control)
                        {
                            now = Math.Max(1, active - 1);
                        }

                        if (now < Volatile.Read(ref ceiling))
                        {
                            Volatile.Write(ref ceiling, now);
                            Log?.Invoke(new FtpLogLine(false, Loc.F("Ftp.Core.RefusedKeepWorkers", ex.Message, now), DateTime.Now));
                        }

                        return;
                    }
                    catch (Exception) when (!ct.IsCancellationRequested)
                    {
                        // Chỉ còn một luồng mà không kết nối được: để nhánh xử lý từng tệp bên dưới báo lỗi và thử lại.
                    }
                }

                var heavy = IsHeavy(item);
                if (heavy)
                {
                    Interlocked.Increment(ref heavyActive);
                    UpdateTarget();
                }

                item.State = FtpItemState.Running;
                item.Started = DateTime.Now;
                item.Attempts++;
                try
                {
                    client ??= await ConnectAsync(ct).ConfigureAwait(false);
                    if (item.IsDirectoryMarker)
                    {
                        await EnsureRemoteDirAsync(client, item.RemotePath, ct).ConfigureAwait(false);
                    }
                    else if (item.Direction == FtpDirection.Upload)
                    {
                        await UploadAsync(client, item, ct).ConfigureAwait(false);
                    }
                    else
                    {
                        client = await DownloadAsync(client, item, ct).ConfigureAwait(false);
                    }

                    if (item.State == FtpItemState.Running)
                    {
                        item.State = FtpItemState.Done;
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    item.State = FtpItemState.Cancelled;
                    item.Error = Loc.T("Ftp.Core.StoppedResumable");
                }
                catch (Exception ex)
                {
                    // Sau bất kỳ lỗi nào đường truyền cũng đáng ngờ: kết nối lại cho tệp sau.
                    client?.Dispose();
                    client = null;
                    var permanent = ex is FtpException { Permanent: true } or UnauthorizedAccessException or DirectoryNotFoundException;
                    if (!permanent && item.Attempts < MaxAttempts)
                    {
                        item.State = FtpItemState.Queued;
                        item.Error = Loc.F("Ftp.Core.Retrying", item.Attempts, MaxAttempts, ex.Message);
                        lock (pendingLock)
                        {
                            pending.AddLast(item);
                        }

                        if (heavy)
                        {
                            Interlocked.Decrement(ref heavyActive);
                            heavy = false;
                        }

                        await Task.Delay(500, ct).ConfigureAwait(false);
                        continue;
                    }

                    item.State = FtpItemState.Failed;
                    item.Error = ex.Message;
                }
                finally
                {
                    if (heavy)
                    {
                        Interlocked.Decrement(ref heavyActive);
                        UpdateTarget();
                    }
                }

                item.Speed = 0;
                item.Finished = DateTime.Now;
                if (item.State is FtpItemState.Done or FtpItemState.Skipped)
                {
                    Interlocked.Increment(ref finishedCount);
                }

                ItemFinished?.Invoke(item);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            client?.Dispose();
            if (counted)
            {
                lock (control)
                {
                    active--;
                }
            }
        }
    }

    // ── Thư mục ────────────────────────────────────────────────────────────
    private async Task EnsureRemoteDirAsync(Ps5Ftp client, string dir, CancellationToken ct)
    {
        var path = Ps5Ftp.NormalizeDir(dir);
        if (path == "/" || madeDirs.ContainsKey(path))
        {
            return;
        }

        var parent = Ps5Ftp.Parent(path);
        if (parent != path)
        {
            await EnsureRemoteDirAsync(client, parent, ct).ConfigureAwait(false);
        }

        var reply = await client.SendRawAsync("MKD " + path, ct).ConfigureAwait(false);
        // 550 thường nghĩa là "đã có"; kiểm tra lại thay vì làm lỗi cả cây.
        if (!reply.Ok && !await client.DirectoryExistsAsync(path, ct).ConfigureAwait(false))
        {
            throw new FtpException(reply.Code, Loc.F("Ftp.Core.MakeDirFailed", path, reply.Text.Trim()));
        }

        madeDirs[path] = 0;
    }

    // ── Tải lên ────────────────────────────────────────────────────────────
    private async Task UploadAsync(Ps5Ftp client, FtpTransferItem item, CancellationToken ct)
    {
        var info = new FileInfo(item.LocalPath);
        if (!info.Exists)
        {
            throw new FileNotFoundException(Loc.T("Ftp.Core.SourceMissing"), item.LocalPath);
        }

        item.Size = info.Length;
        await EnsureRemoteDirAsync(client, Ps5Ftp.Parent(item.RemotePath), ct).ConfigureAwait(false);

        long resume = 0;
        var remoteSize = await client.GetSizeAsync(item.RemotePath, ct).ConfigureAwait(false);
        if (remoteSize >= 0)
        {
            var decision = Decide(item, remoteSize, remoteSize < info.Length);
            if (decision == FtpExistsPolicy.Skip)
            {
                MarkSkipped(item);
                return;
            }

            if (decision == FtpExistsPolicy.OverwriteIfNewer)
            {
                var remoteTime = await client.GetModifiedAsync(item.RemotePath, ct).ConfigureAwait(false);
                if (remoteTime != null && remoteTime >= info.LastWriteTime)
                {
                    MarkSkipped(item);
                    return;
                }
            }

            if (decision == FtpExistsPolicy.OverwriteIfSizeDiffers && remoteSize == info.Length)
            {
                MarkSkipped(item);
                return;
            }

            if (decision == FtpExistsPolicy.Resume && remoteSize < info.Length)
            {
                resume = remoteSize;
            }

            if (decision == FtpExistsPolicy.Resume && remoteSize == info.Length)
            {
                MarkSkipped(item);
                return;
            }
        }

        var meter = new SpeedMeter(item, resume);
        // bufferSize 0: PumpAsync đã đọc theo khối lớn; bộ đệm FileStream 4 MB cho mỗi tệp chỉ là rác với tệp nhỏ.
        await using (var source = new FileStream(item.LocalPath, FileMode.Open, FileAccess.Read, FileShare.Read, 0,
                         FileOptions.SequentialScan | FileOptions.Asynchronous))
        {
            await client.UploadAsync(source, item.RemotePath, resume, done => meter.Report(done, this), ct).ConfigureAwait(false);
        }

        meter.Report(item.Size, this, force: true);
        if (PreserveTimestamps && client.SupportsMfmt)
        {
            await client.SetModifiedAsync(item.RemotePath, info.LastWriteTime, ct).ConfigureAwait(false);
        }
    }

    // ── Tải về ─────────────────────────────────────────────────────────────
    /// <returns>Client dùng tiếp; tải chia đoạn có thể thay client khác.</returns>
    private async Task<Ps5Ftp> DownloadAsync(Ps5Ftp client, FtpTransferItem item, CancellationToken ct)
    {
        if (item.Size < 0)
        {
            item.Size = await client.GetSizeAsync(item.RemotePath, ct).ConfigureAwait(false);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(item.LocalPath)!);

        long resume = 0;
        var local = new FileInfo(item.LocalPath);
        if (local.Exists)
        {
            var decision = Decide(item, local.Length, local.Length < item.Size);
            if (decision == FtpExistsPolicy.Skip)
            {
                MarkSkipped(item);
                return client;
            }

            if (decision == FtpExistsPolicy.OverwriteIfNewer && item.RemoteModified != null && local.LastWriteTime >= item.RemoteModified)
            {
                MarkSkipped(item);
                return client;
            }

            if (decision == FtpExistsPolicy.OverwriteIfSizeDiffers && local.Length == item.Size)
            {
                MarkSkipped(item);
                return client;
            }

            if (decision == FtpExistsPolicy.Resume && item.Size > 0 && local.Length == item.Size)
            {
                MarkSkipped(item);
                return client;
            }

            if (decision == FtpExistsPolicy.Resume && local.Length < item.Size)
            {
                resume = local.Length;
            }
        }

        var segments = Math.Clamp(SegmentsPerFile, 1, 8);
        if (resume == 0 && segments > 1 && item.Size >= SegmentThreshold && client.SupportsRest)
        {
            await SegmentedDownloadAsync(item, segments, ct).ConfigureAwait(false);
        }
        else
        {
            var meter = new SpeedMeter(item, resume);
            // OpenOrCreate + đặt vị trí (không dùng Append) để tải lại được từ 0 khi máy chủ từ chối REST.
            await using var target = new FileStream(item.LocalPath, resume > 0 ? FileMode.OpenOrCreate : FileMode.Create, FileAccess.Write,
                FileShare.Read, 0, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (resume > 0)
            {
                target.SetLength(resume);
                target.Position = resume;
            }

            await client.DownloadAsync(item.RemotePath, target, resume, done => meter.Report(done, this), ct).ConfigureAwait(false);
            meter.Report(item.Size, this, force: true);
        }

        if (PreserveTimestamps && item.RemoteModified is { } when)
        {
            File.SetLastWriteTime(item.LocalPath, when);
        }

        return client;
    }

    private async Task SegmentedDownloadAsync(FtpTransferItem item, int segments, CancellationToken ct)
    {
        item.Segments = segments;
        using var handle = File.OpenHandle(item.LocalPath, FileMode.Create, FileAccess.Write, FileShare.Read, FileOptions.Asynchronous);
        RandomAccess.SetLength(handle, item.Size);

        var part = item.Size / segments;
        var done = new long[segments];
        var meter = new SpeedMeter(item, 0);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var tasks = new Task[segments];
        for (var i = 0; i < segments; i++)
        {
            var index = i;
            var start = part * index;
            var length = index == segments - 1 ? item.Size - start : part;
            tasks[i] = Task.Run(async () =>
            {
                for (var attempt = 1; ; attempt++)
                {
                    var already = Interlocked.Read(ref done[index]);
                    using var segClient = await ConnectAsync(linked.Token).ConfigureAwait(false);
                    try
                    {
                        await using var sink = new OffsetWriteStream(handle, start + already);
                        await segClient.DownloadRangeAsync(item.RemotePath, sink, start + already, length - already, got =>
                        {
                            Interlocked.Exchange(ref done[index], already + got);
                            meter.Report(done.Sum(), this);
                        }, linked.Token).ConfigureAwait(false);
                        return;
                    }
                    catch (Exception) when (!linked.IsCancellationRequested && attempt < MaxAttempts)
                    {
                        // Kết nối lại và tải tiếp đoạn này từ chỗ đã dừng.
                        await Task.Delay(400, linked.Token).ConfigureAwait(false);
                    }
                }
            }, linked.Token);
        }

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch
        {
            linked.Cancel();
            throw;
        }

        meter.Report(item.Size, this, force: true);
    }

    private static void MarkSkipped(FtpTransferItem item)
    {
        item.State = FtpItemState.Skipped;
        item.Transferred = item.Size;
    }

    private FtpExistsPolicy Decide(FtpTransferItem item, long existingSize, bool resumable)
    {
        if (item.ForceOverwrite)
        {
            return FtpExistsPolicy.Overwrite;
        }

        var policy = ExistsPolicy;
        if (policy == FtpExistsPolicy.Ask)
        {
            policy = AskExists?.Invoke(item, existingSize) ?? FtpExistsPolicy.Resume;
        }

        if (policy == FtpExistsPolicy.Resume && !resumable)
        {
            policy = existingSize == item.Size ? FtpExistsPolicy.Resume : FtpExistsPolicy.Overwrite;
        }

        return policy;
    }

    internal void AddMoved(long delta) => Interlocked.Add(ref bytesMoved, delta);

    /// <summary>Giới hạn cập nhật tiến độ ~7 lần/giây cho mỗi tệp và tính tốc độ đã làm mượt.</summary>
    private sealed class SpeedMeter(FtpTransferItem item, long start)
    {
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly object sync = new();
        private long last = start;
        private long lastReported = start;
        private double lastTime;

        public void Report(long done, FtpTransferQueue owner, bool force = false)
        {
            lock (sync)
            {
                var delta = done - lastReported;
                if (delta > 0)
                {
                    owner.AddMoved(delta);
                    lastReported = done;
                }

                var now = clock.Elapsed.TotalSeconds;
                if (!force && now - lastTime < 0.15)
                {
                    return;
                }

                var instant = now > lastTime ? (done - last) / (now - lastTime) : 0;
                item.Speed = item.Speed <= 0 ? instant : item.Speed * 0.7 + instant * 0.3;
                last = done;
                lastTime = now;
                item.Transferred = done;
            }
        }
    }
}

/// <summary>Luồng chỉ ghi, ghi tại một vị trí tăng dần trong tệp, để nhiều đoạn cùng điền một tệp song song.</summary>
internal sealed class OffsetWriteStream(SafeFileHandle handle, long offset) : Stream
{
    private long position = offset;

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => position;
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count)
    {
        RandomAccess.Write(handle, buffer.AsSpan(offset, count), position);
        position += count;
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await RandomAccess.WriteAsync(handle, buffer, position, cancellationToken).ConfigureAwait(false);
        position += buffer.Length;
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
}
