using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Threading.Channels;
using PsViethoa.FpkgBuilder.Core.Localization;

namespace PsViethoa.FpkgBuilder.Core.Ftp;

// Xoá hàng loạt trên PS5. Chuyển từ PSVIETHOA STUDIO (Ngô Phi Phương), dùng với sự đồng ý của tác giả.

public enum FtpDeletePhase
{
    Idle,
    Counting,
    WaitingConfirm,
    DeletingFiles,
    DeletingFolders,
    Done,
    Cancelled,
}

/// <summary>Một mục không xoá được (hoặc không đọc được khi đếm), kèm lý do.</summary>
public sealed record FtpDeleteFailure(string Path, bool IsDirectory, string Error);

/// <summary>Bộ đếm trực tiếp, được cập nhật từ các luồng làm việc; đọc từ hẹn giờ của giao diện.</summary>
public sealed class FtpDeleteStats
{
    internal int dirsListed;
    internal int filesFound;
    internal int dirsFound;
    internal int filesDeleted;
    internal int dirsDeleted;
    internal int failed;
    internal int retries;
    internal int activeWorkers;
    internal int poolSize;
    internal int requested;
    internal long bytesFound;
    internal long bytesDeleted;

    public int DirsListed => Volatile.Read(ref dirsListed);

    public int FilesFound => Volatile.Read(ref filesFound);

    public int DirsFound => Volatile.Read(ref dirsFound);

    public long BytesFound => Interlocked.Read(ref bytesFound);

    public int FilesDeleted => Volatile.Read(ref filesDeleted);

    public int DirsDeleted => Volatile.Read(ref dirsDeleted);

    public long BytesDeleted => Interlocked.Read(ref bytesDeleted);

    public int Failed => Volatile.Read(ref failed);

    public int Retries => Volatile.Read(ref retries);

    /// <summary>Số kết nối đang bận với một mục ngay lúc này.</summary>
    public int ActiveWorkers => Volatile.Read(ref activeWorkers);

    /// <summary>Số kết nối đang mở tới PS5.</summary>
    public int Connections => Volatile.Read(ref poolSize);

    /// <summary>Số luồng đã yêu cầu mà không giữ được, vì PS5 từ chối hoặc làm rớt các kết nối đó.</summary>
    public int DroppedWorkers => Math.Max(0, Volatile.Read(ref requested) - Volatile.Read(ref poolSize));

    public int TotalItems => FilesFound + DirsFound;

    public int ProcessedItems => FilesDeleted + DirsDeleted + Failed;

    public string Current { get; internal set; } = "";
}

/// <summary>
/// Xoá hàng loạt trên PS5, chịu được ftpsrv chậm hoặc chập chờn. FileZilla hay đứng với cây lớn vì nó duyệt và xoá trên một kết
/// nối và bỏ cuộc ở lệnh rớt đầu tiên. Ở đây:
/// 1. một nhóm kết nối được mở một lần (cùng lúc tối đa <see cref="ConnectConcurrency"/> lượt bắt tay) và dùng lại cho mọi giai
///    đoạn, nên số luồng cao không phải trả một lần kết nối lại cho mỗi tầng thư mục;
/// 2. cả nhóm cùng liệt kê cây theo chiều rộng;
/// 3. tệp được xoá song song, mỗi lệnh tự thử lại sau khi hết hạn chờ hoặc rớt kết nối;
/// 4. thư mục được xoá từ tầng sâu nhất lên.
/// Một 550 sau lần thử lại được kiểm tra với máy chủ: nếu mục đã mất (lần đầu đã xoá được trước khi kết nối rớt) thì tính là đã
/// xoá. Kết nối mà PS5 từ chối hoặc không nhận lại thì trả mục của nó cho các kết nối khác và rời nhóm; khi nhóm cạn, bộ xoá chờ
/// rồi kết nối lại theo nhịp giãn dần (khoảng nửa phút) trước khi bỏ cuộc.
/// </summary>
public sealed class FtpTreeDeleter : IDisposable
{
    /// <summary>Chặn giá trị gõ nhầm mở hàng nghìn socket; không phải giới hạn tinh chỉnh.</summary>
    public const int MaxWorkers = 256;

    private readonly FtpConnectionInfo connection;
    private readonly List<(string Path, long Size)> files = [];
    private readonly List<(string Path, int Depth)> folders = [];
    private readonly List<FtpDeleteFailure> failures = [];
    private readonly object gate = new();
    private readonly List<Worker> pool = [];
    private SemaphoreSlim? connectGate;
    // Số kết nối PS5 đã nhận ở lần gần nhất nó từ chối thêm.
    private int ceiling = int.MaxValue;
    private volatile FtpDeletePhase phase = FtpDeletePhase.Idle;

    public FtpTreeDeleter(FtpConnectionInfo connection)
    {
        this.connection = connection;
    }

    public int Workers { get; set; } = 4;

    public int TimeoutSeconds { get; set; } = 20;

    /// <summary>Số lần chạy mỗi lệnh (thử lại sau khi hết hạn chờ, rớt kết nối hoặc gặp 4xx).</summary>
    public int MaxAttempts { get; set; } = 4;

    /// <summary>Số lần thử mở một kết nối trước khi bỏ suất đó trong vòng này.</summary>
    public int ConnectAttempts { get; set; } = 2;

    /// <summary>Số lượt bắt tay kết nối được chạy cùng lúc khi lấp đầy nhóm.</summary>
    public int ConnectConcurrency { get; set; } = 8;

    /// <summary>Số giây chờ giữa các vòng không tiến triển gì, ví dụ khi PS5 từ chối mọi kết nối.</summary>
    public int[] BackoffSeconds { get; set; } = [1, 2, 4, 8, 8, 8];

    public FtpDeleteStats Stats { get; } = new();

    public FtpDeletePhase Phase => phase;

    public IReadOnlyList<FtpDeleteFailure> Failures
    {
        get
        {
            lock (gate)
            {
                return failures.ToList();
            }
        }
    }

    public event Action<FtpLogLine>? Log;

    private int Target => Math.Clamp(Workers, 1, MaxWorkers);

    // ── Nhóm kết nối ───────────────────────────────────────────────────────
    /// <summary>Mở kết nối cho tới khi nhóm đủ <see cref="Workers"/>; suất nào PS5 từ chối thì tạm để trống.</summary>
    private async Task FillPoolAsync(CancellationToken ct)
    {
        connectGate ??= new SemaphoreSlim(Math.Max(1, ConnectConcurrency));
        Volatile.Write(ref Stats.requested, Target);
        int missing;
        lock (gate)
        {
            // Khi PS5 đã cho thấy nó nhận bao nhiêu kết nối, chỉ bù nhóm tới mức trần đó thay vì thử lại mọi suất bị từ chối ở
            // mỗi tầng thư mục; nhóm rỗng thì thử lại đủ số.
            var goal = pool.Count == 0 ? Target : Math.Min(Target, ceiling);
            missing = goal - pool.Count;
        }

        if (missing <= 0)
        {
            return;
        }

        var fresh = await Task.WhenAll(Enumerable.Range(0, missing).Select(async _ =>
        {
            var w = new Worker(this);
            try
            {
                await w.EnsureConnectedAsync(ct).ConfigureAwait(false);
                return w;
            }
            catch (WorkerLostException)
            {
                w.Dispose();
                return null;
            }
        })).ConfigureAwait(false);
        lock (gate)
        {
            foreach (var w in fresh)
            {
                if (w != null)
                {
                    pool.Add(w);
                }
            }

            Volatile.Write(ref Stats.poolSize, pool.Count);
            if (fresh.Any(w => w == null))
            {
                // PS5 từ chối một số kết nối: nhớ giới hạn của nó.
                ceiling = Math.Max(1, pool.Count);
            }
            else
            {
                ceiling = Math.Max(ceiling, pool.Count);
            }
        }
    }

    private void Retire(Worker w)
    {
        lock (gate)
        {
            pool.Remove(w);
            Volatile.Write(ref Stats.poolSize, pool.Count);
        }

        w.Dispose();
    }

    private List<Worker> Snapshot()
    {
        lock (gate)
        {
            return pool.ToList();
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            foreach (var w in pool)
            {
                w.Dispose();
            }

            pool.Clear();
            Volatile.Write(ref Stats.poolSize, 0);
        }

        connectGate?.Dispose();
    }

    /// <summary>
    /// Cho mọi kết nối trong nhóm chạy một lượt trên nguồn việc chung cho tới khi cạn, lấp lại nhóm và chờ giãn dần khi mất kết
    /// nối; <paramref name="pass"/> trả false khi nó dừng vì mất đường truyền.
    /// </summary>
    private async Task RunRoundsAsync(Func<bool> done, Func<Func<int>> progressProbe, Func<Worker, Task<bool>> pass, string what, CancellationToken ct)
    {
        var probe = progressProbe();
        var stalls = 0;
        while (!done())
        {
            ct.ThrowIfCancellationRequested();
            await FillPoolAsync(ct).ConfigureAwait(false);
            var workers = Snapshot();
            var before = probe();
            if (workers.Count > 0)
            {
                await Task.WhenAll(workers.Select(w => Task.Run(async () =>
                {
                    if (!await pass(w).ConfigureAwait(false))
                    {
                        Retire(w);
                    }
                }, ct))).ConfigureAwait(false);
            }

            if (done())
            {
                break;
            }

            if (probe() > before)
            {
                stalls = 0;
                continue;
            }

            if (stalls >= BackoffSeconds.Length)
            {
                throw new FtpException(0, Loc.F("Ftp.Core.DeleteNoConnections", BackoffSeconds.Sum(), what));
            }

            Log?.Invoke(new FtpLogLine(false, Loc.F("Ftp.Core.DeleteWaiting", what, BackoffSeconds[stalls]), DateTime.Now));
            await Task.Delay(TimeSpan.FromSeconds(BackoffSeconds[stalls++]), ct).ConfigureAwait(false);
        }
    }

    // ── Giai đoạn 1: đếm ───────────────────────────────────────────────────
    /// <summary>Mở rộng lựa chọn thành mọi tệp và thư mục bên trong.</summary>
    public async Task CountAsync(IEnumerable<(string Path, bool IsDirectory, long Size)> roots, CancellationToken ct)
    {
        phase = FtpDeletePhase.Counting;
        var channel = Channel.CreateUnbounded<(string Path, int Depth)>();
        var pending = 0;
        foreach (var (path, isDir, size) in roots)
        {
            if (isDir)
            {
                lock (gate)
                {
                    folders.Add((path, 0));
                }

                Interlocked.Increment(ref Stats.dirsFound);
                Interlocked.Increment(ref pending);
                channel.Writer.TryWrite((path, 0));
            }
            else
            {
                AddFile(path, size);
            }
        }

        if (pending == 0)
        {
            phase = FtpDeletePhase.WaitingConfirm;
            return;
        }

        try
        {
            await RunRoundsAsync(() => Volatile.Read(ref pending) == 0, () => () => Stats.DirsListed + Stats.Failed, async w =>
            {
                Interlocked.Increment(ref Stats.activeWorkers);
                try
                {
                    await foreach (var (dir, depth) in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                    {
                        Stats.Current = dir;
                        IReadOnlyList<FtpEntry> entries = [];
                        try
                        {
                            entries = await w.RunAsync(c => c.ListAsync(dir, ct), ct).ConfigureAwait(false);
                            Interlocked.Increment(ref Stats.dirsListed);
                        }
                        catch (WorkerLostException)
                        {
                            // Vẫn đang chờ: một kết nối khác sẽ liệt kê nó.
                            channel.Writer.TryWrite((dir, depth));
                            return false;
                        }
                        catch (FtpException ex) when (!ct.IsCancellationRequested)
                        {
                            Fail(dir, true, Loc.F("Ftp.Core.ListFolderFailed", ex.Message));
                        }

                        foreach (var e in entries)
                        {
                            if (e.IsDirectory)
                            {
                                lock (gate)
                                {
                                    folders.Add((e.FullPath, depth + 1));
                                }

                                Interlocked.Increment(ref Stats.dirsFound);
                                Interlocked.Increment(ref pending);
                                channel.Writer.TryWrite((e.FullPath, depth + 1));
                            }
                            else
                            {
                                // Liên kết tính là tệp: DELE gỡ liên kết, không đụng tới đích của nó.
                                AddFile(e.FullPath, e.Size);
                            }
                        }

                        if (Interlocked.Decrement(ref pending) == 0)
                        {
                            channel.Writer.TryComplete();
                        }
                    }

                    return true;
                }
                finally
                {
                    Interlocked.Decrement(ref Stats.activeWorkers);
                }
            }, Loc.T("Ftp.Core.PhaseCounting"), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            phase = FtpDeletePhase.Cancelled;
            throw;
        }

        phase = FtpDeletePhase.WaitingConfirm;
    }

    // ── Giai đoạn 2 và 3: xoá ──────────────────────────────────────────────
    /// <summary>Xoá mọi tệp đã đếm, rồi các thư mục từ tầng sâu nhất lên. Đóng nhóm kết nối khi xong.</summary>
    public async Task DeleteAsync(CancellationToken ct)
    {
        try
        {
            phase = FtpDeletePhase.DeletingFiles;
            List<(string Path, long Size)> fileList;
            lock (gate)
            {
                fileList = files.ToList();
            }

            await DrainAsync(fileList, async (w, f) =>
            {
                Stats.Current = f.Path;
                if (await DeleteOneAsync(w, f.Path, isDirectory: false, ct).ConfigureAwait(false))
                {
                    Interlocked.Increment(ref Stats.filesDeleted);
                    Interlocked.Add(ref Stats.bytesDeleted, Math.Max(0, f.Size));
                }
            }, Loc.T("Ftp.Core.PhaseDeletingFiles"), ct).ConfigureAwait(false);

            phase = FtpDeletePhase.DeletingFolders;
            List<(string Path, int Depth)> folderList;
            lock (gate)
            {
                folderList = folders.ToList();
            }

            foreach (var level in folderList.GroupBy(f => f.Depth).OrderByDescending(g => g.Key))
            {
                await DrainAsync(level.ToList(), async (w, d) =>
                {
                    Stats.Current = d.Path;
                    if (await DeleteOneAsync(w, d.Path, isDirectory: true, ct).ConfigureAwait(false))
                    {
                        Interlocked.Increment(ref Stats.dirsDeleted);
                    }
                }, Loc.T("Ftp.Core.PhaseDeletingFolders"), ct).ConfigureAwait(false);
            }

            phase = FtpDeletePhase.Done;
            Stats.Current = "";
        }
        catch (OperationCanceledException)
        {
            phase = FtpDeletePhase.Cancelled;
            throw;
        }
        finally
        {
            Dispose();
        }
    }

    private Task DrainAsync<T>(List<T> items, Func<Worker, T, Task> work, string what, CancellationToken ct)
    {
        var queue = new ConcurrentQueue<T>(items);
        var taken = 0;
        return RunRoundsAsync(() => queue.IsEmpty, () => () => Volatile.Read(ref taken), async w =>
        {
            Interlocked.Increment(ref Stats.activeWorkers);
            try
            {
                while (queue.TryDequeue(out var item))
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        await work(w, item).ConfigureAwait(false);
                        Interlocked.Increment(ref taken);
                    }
                    catch (WorkerLostException)
                    {
                        queue.Enqueue(item);
                        return false;
                    }
                }

                return true;
            }
            finally
            {
                Interlocked.Decrement(ref Stats.activeWorkers);
            }
        }, what, ct);
    }

    private async Task<bool> DeleteOneAsync(Worker w, string path, bool isDirectory, CancellationToken ct)
    {
        try
        {
            var retried = false;
            await w.RunAsync(async c =>
            {
                var reply = await c.SendRawAsync((isDirectory ? "RMD " : "DELE ") + path, ct).ConfigureAwait(false);
                if (reply.Ok)
                {
                    return true;
                }

                // Một 4xx/5xx ngay sau khi kết nối lại có thể nghĩa là lần đầu đã xoá được rồi.
                if (reply.Code >= 500 || retried)
                {
                    var gone = isDirectory
                        ? !await c.DirectoryExistsAsync(path, ct).ConfigureAwait(false)
                        : await c.GetSizeAsync(path, ct).ConfigureAwait(false) < 0 && !await c.DirectoryExistsAsync(path, ct).ConfigureAwait(false);
                    if (gone)
                    {
                        return true;
                    }
                }

                throw new FtpException(reply.Code, reply.Text.Trim());
            }, ct, onRetry: () => retried = true).ConfigureAwait(false);
            return true;
        }
        catch (FtpException ex) when (!ct.IsCancellationRequested)
        {
            Fail(path, isDirectory, isDirectory && ex.Code == 550 ? Loc.F("Ftp.Core.FolderNotEmpty", ex.Message) : ex.Message);
            return false;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is IOException or SocketException or ObjectDisposedException)
        {
            Fail(path, isDirectory, Loc.F("Ftp.Core.LinkLostAfter", MaxAttempts, ex.Message));
            return false;
        }
    }

    private void AddFile(string path, long size)
    {
        lock (gate)
        {
            files.Add((path, size));
        }

        Interlocked.Increment(ref Stats.filesFound);
        Interlocked.Add(ref Stats.bytesFound, Math.Max(0, size));
    }

    private void Fail(string path, bool isDirectory, string error)
    {
        lock (gate)
        {
            failures.Add(new FtpDeleteFailure(path, isDirectory, error));
        }

        Interlocked.Increment(ref Stats.failed);
        Log?.Invoke(new FtpLogLine(false, Loc.F("Ftp.Core.DeleteFailedLog", path, error), DateTime.Now));
    }

    /// <summary>Không mở (lại) được kết nối: mục của nó trả về cho các kết nối khác và nó rời nhóm.</summary>
    private sealed class WorkerLostException(Exception inner) : Exception(Loc.F("Ftp.Core.WorkerLost", inner.Message), inner);

    /// <summary>Một kết nối FTP trong nhóm; tự kết nối lại và chạy lại lệnh khi đường truyền trục trặc.</summary>
    private sealed class Worker(FtpTreeDeleter owner) : IDisposable
    {
        private Ps5Ftp? client;

        public async Task EnsureConnectedAsync(CancellationToken ct)
        {
            if (client is { Connected: true })
            {
                return;
            }

            Exception? last = null;
            for (var attempt = 1; attempt <= Math.Max(1, owner.ConnectAttempts); attempt++)
            {
                ct.ThrowIfCancellationRequested();
                client?.Dispose();
                client = null;
                var c = new Ps5Ftp { TimeoutSeconds = owner.TimeoutSeconds };
                c.Log += line =>
                {
                    if (!line.Outgoing && line.Text.Length > 0 && line.Text[0] is '4' or '5')
                    {
                        owner.Log?.Invoke(line);
                    }
                };
                await owner.connectGate!.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    await c.ConnectAsync(owner.connection.Host, owner.connection.Port, owner.connection.User, owner.connection.Pass, ct).ConfigureAwait(false);
                    client = c;
                    return;
                }
                catch (Exception ex) when (!ct.IsCancellationRequested && ex is FtpException or IOException or SocketException or ObjectDisposedException)
                {
                    c.Dispose();
                    last = ex;
                }
                finally
                {
                    owner.connectGate.Release();
                }

                await Task.Delay(300 * attempt, ct).ConfigureAwait(false);
            }

            throw new WorkerLostException(last!);
        }

        public async Task<T> RunAsync<T>(Func<Ps5Ftp, Task<T>> op, CancellationToken ct, Action? onRetry = null)
        {
            for (var attempt = 1; ; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                // Ném WorkerLostException khi PS5 không nhận lại kết nối này.
                await EnsureConnectedAsync(ct).ConfigureAwait(false);
                try
                {
                    return await op(client!).ConfigureAwait(false);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested && attempt < owner.MaxAttempts && IsTransient(ex))
                {
                    // Kênh điều khiển giờ ở trạng thái không xác định.
                    client?.Dispose();
                    client = null;
                    Interlocked.Increment(ref owner.Stats.retries);
                    onRetry?.Invoke();
                    await Task.Delay(250 * attempt, ct).ConfigureAwait(false);
                }
            }
        }

        private static bool IsTransient(Exception ex) => ex switch
        {
            // 0 = hết hạn chờ / kết nối đóng, 4xx = thử lại sau.
            FtpException f => f.Code == 0 || f.Code is >= 400 and < 500,
            IOException or SocketException or ObjectDisposedException => true,
            _ => false,
        };

        public void Dispose()
        {
            client?.Dispose();
            client = null;
        }
    }
}
