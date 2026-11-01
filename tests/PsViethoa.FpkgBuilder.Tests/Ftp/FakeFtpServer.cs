using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using PsViethoa.FpkgBuilder.Core.Ftp;

namespace PsViethoa.FpkgBuilder.Tests.Ftp;

/// <summary>Cách máy chủ giả trả lời FEAT.</summary>
public enum FakeFeat
{
    /// <summary>"502": như mini_ftpd của bộ thử gốc.</summary>
    NotImplemented,

    /// <summary>"211" không có dòng tính năng nào: như ftpsrv trên PS5.</summary>
    Empty,

    /// <summary>Liệt kê SIZE, REST STREAM, MDTM, MFMT, UTF8.</summary>
    Full,
}

/// <summary>
/// Máy chủ FTP chạy ngay trong tiến trình thử, trên 127.0.0.1 cổng ngẫu nhiên, gốc là một thư mục tạm. Bắt chước ftpsrv của PS5
/// đủ để thử client: LIST kiểu "ls -l", PASV, REST cho RETR/STOR, APPE, SIZE, MDTM, MFMT, RNFR/RNTO (đè im lặng như rename(2)).
/// Mỗi kết nối điều khiển chạy trên một task riêng. Có các nút để mô phỏng PS5 khó tính: giới hạn số kết nối, trễ mỗi lệnh,
/// từ chối REST, cắt kênh dữ liệu giữa chừng, rớt kết nối sau DELE.
/// </summary>
internal sealed class FakeFtpServer : IAsyncDisposable
{
    private readonly TcpListener listener;
    private readonly CancellationTokenSource stop = new();
    private readonly ConcurrentDictionary<int, TcpClient> clients = new();
    private readonly ConcurrentDictionary<int, Task> sessions = new();
    private readonly Task acceptLoop;
    private int active;
    private int peak;
    private int total;
    private int refused;
    private int nextId;
    private int dropRetr;
    private int dropStor;
    private int deleCount;

    public FakeFtpServer(string root)
    {
        Root = Path.GetFullPath(root);
        Directory.CreateDirectory(Root);
        listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(512);
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        acceptLoop = Task.Run(AcceptLoopAsync);
    }

    /// <summary>Thư mục thật trên máy ứng với "/" của máy chủ.</summary>
    public string Root { get; }

    public int Port { get; }

    public FakeFeat Feat { get; set; } = FakeFeat.NotImplemented;

    /// <summary>Mật khẩu bắt buộc; null = nhận mọi mật khẩu (như ftpsrv).</summary>
    public string? Password { get; set; }

    /// <summary>Số kết nối điều khiển tối đa cùng lúc (0 = không giới hạn); kết nối thừa bị từ chối.</summary>
    public int MaxConnections { get; set; }

    /// <summary>Từ chối kết nối thừa bằng cách đóng luôn (true) thay vì trả "421" (false).</summary>
    public bool RefuseByClosing { get; set; }

    /// <summary>Chờ trước mỗi phản hồi (trừ "150"), giống độ trễ mỗi lệnh của PS5.</summary>
    public TimeSpan CommandDelay { get; set; }

    /// <summary>Trả "502" cho REST.</summary>
    public bool RefuseRest { get; set; }

    /// <summary>Cứ mỗi N lệnh DELE: xoá tệp rồi cắt kết nối điều khiển mà không trả lời (0 = tắt).</summary>
    public int DropAfterDeleEvery { get; set; }

    /// <summary>Tệp mang tên này không bao giờ xoá được: DELE luôn trả "550 Permission denied" (như locked.bin của mini_ftpd).</summary>
    public string? LockedName { get; set; }

    /// <summary>Dòng LIST thêm vào cho một thư mục (khoá là đường dẫn trên máy chủ, ví dụ "/"), để thử liên kết và định dạng lạ.</summary>
    public ConcurrentDictionary<string, string[]> ExtraListLines { get; } = new(StringComparer.Ordinal);

    /// <summary>Mọi lệnh đã nhận, theo thứ tự (mật khẩu bị che).</summary>
    public ConcurrentQueue<string> Commands { get; } = new();

    public int ActiveConnections => Volatile.Read(ref active);

    /// <summary>Số kết nối điều khiển được nhận cùng lúc nhiều nhất.</summary>
    public int PeakConnections => Volatile.Read(ref peak);

    public int TotalConnections => Volatile.Read(ref total);

    public int RefusedConnections => Volatile.Read(ref refused);

    public FtpConnectionInfo Connection => new("127.0.0.1", Port, "anonymous", Password ?? "");

    /// <summary>N lệnh RETR kế tiếp chỉ gửi một phần dữ liệu rồi cắt kênh và trả "426".</summary>
    public void DropNextRetr(int count = 1) => Interlocked.Add(ref dropRetr, count);

    /// <summary>N lệnh STOR/APPE kế tiếp chỉ nhận một khối rồi cắt kênh (RST) và trả "426".</summary>
    public void DropNextStor(int count = 1) => Interlocked.Add(ref dropStor, count);

    /// <summary>Số lệnh đã nhận có động từ <paramref name="verb"/> (và, nếu có, đúng đối số <paramref name="argument"/>).</summary>
    public int CountCommands(string verb, string? argument = null) =>
        Commands.Count(c => argument == null
            ? c.Equals(verb, StringComparison.Ordinal) || c.StartsWith(verb + " ", StringComparison.Ordinal)
            : c.Equals(verb + " " + argument, StringComparison.Ordinal));

    /// <summary>Đường dẫn thật của một đường dẫn trên máy chủ.</summary>
    public string RealPath(string remote)
    {
        var parts = remote.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 0 ? Root : Path.Combine([Root, .. parts]);
    }

    public async ValueTask DisposeAsync()
    {
        await stop.CancelAsync();
        listener.Stop();
        foreach (var client in clients.Values)
        {
            client.Dispose();
        }

        try
        {
            await Task.WhenAll(sessions.Values.Append(acceptLoop)).WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception)
        {
            // Tắt máy chủ thử: lỗi của từng phiên đã không còn quan trọng.
        }

        stop.Dispose();
    }

    private static bool TryConsume(ref int counter)
    {
        while (true)
        {
            var current = Volatile.Read(ref counter);
            if (current <= 0)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref counter, current - 1, current) == current)
            {
                return true;
            }
        }
    }

    private async Task AcceptLoopAsync()
    {
        while (!stop.IsCancellationRequested)
        {
            TcpClient tcp;
            try
            {
                tcp = await listener.AcceptTcpClientAsync(stop.Token);
            }
            catch (Exception)
            {
                return;
            }

            var id = Interlocked.Increment(ref nextId);
            clients[id] = tcp;
            sessions[id] = Task.Run(() => SessionAsync(id, tcp, stop.Token));
        }
    }

    private async Task SessionAsync(int id, TcpClient tcp, CancellationToken ct)
    {
        var counted = false;
        try
        {
            tcp.NoDelay = true;
            var stream = tcp.GetStream();
            var now = Interlocked.Increment(ref active);
            counted = true;
            if (MaxConnections > 0 && now > MaxConnections)
            {
                Interlocked.Increment(ref refused);
                if (!RefuseByClosing)
                {
                    var bytes = Encoding.UTF8.GetBytes("421 Too many connections, try later.\r\n");
                    await stream.WriteAsync(bytes, ct);
                }

                return;
            }

            Interlocked.Increment(ref total);
            int seen;
            while ((seen = Volatile.Read(ref peak)) < now && Interlocked.CompareExchange(ref peak, now, seen) != seen)
            {
            }

            await new Session(this, tcp, stream).RunAsync(ct);
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
            // Client đóng kết nối hoặc máy chủ đang tắt.
        }
        finally
        {
            if (counted)
            {
                Interlocked.Decrement(ref active);
            }

            clients.TryRemove(id, out _);
            sessions.TryRemove(id, out _);
            tcp.Dispose();
        }
    }

    /// <summary>Trạng thái của một kết nối điều khiển.</summary>
    private sealed class Session(FakeFtpServer server, TcpClient tcp, NetworkStream stream)
    {
        private static readonly UTF8Encoding Utf8 = new(false);
        private string cwd = "/";
        private long rest;
        private string? renameFrom;
        private TcpListener? pasv;
        private bool loggedIn;

        public async Task RunAsync(CancellationToken ct)
        {
            using var reader = new StreamReader(stream, Utf8, false, 4096, leaveOpen: true);
            try
            {
                await ReplyAsync("220 PSVIETHOA fake ftpsrv ready.", ct);
                while (!ct.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(ct);
                    if (line == null)
                    {
                        return;
                    }

                    var space = line.IndexOf(' ');
                    var verb = (space < 0 ? line : line[..space]).ToUpperInvariant();
                    var argument = space < 0 ? "" : line[(space + 1)..];
                    server.Commands.Enqueue(verb == "PASS" ? "PASS ****" : verb + (space < 0 ? "" : " " + argument));
                    try
                    {
                        if (!await HandleAsync(verb, argument, ct))
                        {
                            return;
                        }
                    }
                    catch (Exception ex) when (ex is UnauthorizedAccessException or ArgumentException or FormatException or IOException)
                    {
                        // Lỗi tệp phía máy chủ thành "550"; nếu chính kênh điều khiển đã hỏng thì lần trả lời này ném tiếp và kết thúc phiên.
                        await ReplyAsync("550 " + ex.Message.ReplaceLineEndings(" "), ct);
                    }
                }
            }
            finally
            {
                pasv?.Stop();
            }
        }

        private async Task ReplyAsync(string line, CancellationToken ct)
        {
            if (server.CommandDelay > TimeSpan.Zero && !line.StartsWith("150", StringComparison.Ordinal))
            {
                await Task.Delay(server.CommandDelay, ct);
            }

            var bytes = Utf8.GetBytes(line + "\r\n");
            await stream.WriteAsync(bytes, ct);
            await stream.FlushAsync(ct);
        }

        /// <summary>Xử lý một lệnh; false = đóng phiên.</summary>
        private async Task<bool> HandleAsync(string verb, string argument, CancellationToken ct)
        {
            if (!loggedIn && verb is not ("USER" or "PASS" or "QUIT" or "FEAT"))
            {
                await ReplyAsync("530 Please login with USER and PASS.", ct);
                return true;
            }

            switch (verb)
            {
                case "USER":
                    await ReplyAsync("331 User name okay, need password.", ct);
                    return true;
                case "PASS":
                    if (server.Password != null && argument != server.Password)
                    {
                        await ReplyAsync("530 Login incorrect.", ct);
                        return true;
                    }

                    loggedIn = true;
                    await ReplyAsync("230 User logged in.", ct);
                    return true;
                case "FEAT":
                    await ReplyAsync(server.Feat switch
                    {
                        FakeFeat.Empty => "211-Features:\r\n211 End",
                        FakeFeat.Full => "211-Features:\r\n SIZE\r\n REST STREAM\r\n MDTM\r\n MFMT\r\n UTF8\r\n211 End",
                        _ => "502 Command not implemented.",
                    }, ct);
                    return true;
                case "OPTS":
                case "TYPE":
                case "MODE":
                case "STRU":
                    await ReplyAsync("200 Command okay.", ct);
                    return true;
                case "NOOP":
                    await ReplyAsync("200 NOOP okay.", ct);
                    return true;
                case "SYST":
                    await ReplyAsync("215 UNIX Type: L8", ct);
                    return true;
                case "SITE":
                    await ReplyAsync("200 SITE command okay.", ct);
                    return true;
                case "QUIT":
                    await ReplyAsync("221 Goodbye.", ct);
                    return false;
                case "PWD":
                    await ReplyAsync($"257 \"{cwd}\" is the current directory.", ct);
                    return true;
                case "CWD":
                    {
                        var (virtualPath, real) = Resolve(argument);
                        if (Directory.Exists(real))
                        {
                            cwd = virtualPath;
                            await ReplyAsync("250 Directory changed.", ct);
                        }
                        else
                        {
                            await ReplyAsync("550 No such directory.", ct);
                        }

                        return true;
                    }

                case "CDUP":
                    cwd = Resolve("..").Virtual;
                    await ReplyAsync("250 Directory changed.", ct);
                    return true;
                case "MKD":
                    {
                        var (virtualPath, real) = Resolve(argument);
                        if (Directory.Exists(real) || File.Exists(real))
                        {
                            await ReplyAsync("550 File exists.", ct);
                        }
                        else if (!Directory.Exists(Path.GetDirectoryName(real)))
                        {
                            await ReplyAsync("550 No such directory.", ct);
                        }
                        else
                        {
                            Directory.CreateDirectory(real);
                            await ReplyAsync($"257 \"{virtualPath}\" created.", ct);
                        }

                        return true;
                    }

                case "RMD":
                    {
                        var real = Resolve(argument).Real;
                        if (!Directory.Exists(real))
                        {
                            await ReplyAsync("550 No such directory.", ct);
                        }
                        else if (Directory.EnumerateFileSystemEntries(real).Any())
                        {
                            await ReplyAsync("550 Directory not empty.", ct);
                        }
                        else
                        {
                            Directory.Delete(real);
                            await ReplyAsync("250 Directory removed.", ct);
                        }

                        return true;
                    }

                case "DELE":
                    {
                        var real = Resolve(argument).Real;
                        if (!File.Exists(real))
                        {
                            await ReplyAsync("550 No such file.", ct);
                            return true;
                        }

                        if (server.LockedName != null && Path.GetFileName(real) == server.LockedName)
                        {
                            await ReplyAsync("550 Permission denied.", ct);
                            return true;
                        }

                        File.Delete(real);
                        var n = Interlocked.Increment(ref server.deleCount);
                        if (server.DropAfterDeleEvery > 0 && n % server.DropAfterDeleEvery == 0)
                        {
                            // Đã xoá, nhưng phản hồi không bao giờ tới.
                            tcp.Client.LingerState = new LingerOption(true, 0);
                            return false;
                        }

                        await ReplyAsync("250 File deleted.", ct);
                        return true;
                    }

                case "RNFR":
                    {
                        var real = Resolve(argument).Real;
                        if (File.Exists(real) || Directory.Exists(real))
                        {
                            renameFrom = real;
                            await ReplyAsync("350 Ready for RNTO.", ct);
                        }
                        else
                        {
                            renameFrom = null;
                            await ReplyAsync("550 No such file.", ct);
                        }

                        return true;
                    }

                case "RNTO":
                    {
                        if (renameFrom == null)
                        {
                            await ReplyAsync("503 Bad sequence of commands.", ct);
                            return true;
                        }

                        var real = Resolve(argument).Real;
                        if (Directory.Exists(renameFrom))
                        {
                            Directory.Move(renameFrom, real);
                        }
                        else
                        {
                            File.Move(renameFrom, real, overwrite: true);
                        }

                        renameFrom = null;
                        await ReplyAsync("250 Rename successful.", ct);
                        return true;
                    }

                case "SIZE":
                    {
                        var real = Resolve(argument).Real;
                        await ReplyAsync(File.Exists(real) ? "213 " + new FileInfo(real).Length.ToString(CultureInfo.InvariantCulture) : "550 Could not get file size.", ct);
                        return true;
                    }

                case "MDTM":
                    {
                        var real = Resolve(argument).Real;
                        await ReplyAsync(File.Exists(real) || Directory.Exists(real)
                            ? "213 " + File.GetLastWriteTimeUtc(real).ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture)
                            : "550 No such file.", ct);
                        return true;
                    }

                case "MFMT":
                    {
                        var cut = argument.IndexOf(' ');
                        var real = cut < 0 ? "" : Resolve(argument[(cut + 1)..]).Real;
                        if (cut < 0 || !File.Exists(real))
                        {
                            await ReplyAsync("550 No such file.", ct);
                            return true;
                        }

                        var when = DateTime.ParseExact(argument[..cut], "yyyyMMddHHmmss", CultureInfo.InvariantCulture,
                            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
                        File.SetLastWriteTimeUtc(real, when);
                        await ReplyAsync($"213 Modify={argument[..cut]}; {argument[(cut + 1)..]}", ct);
                        return true;
                    }

                case "REST":
                    if (server.RefuseRest)
                    {
                        await ReplyAsync("502 REST not implemented.", ct);
                        return true;
                    }

                    rest = long.Parse(argument.Trim(), CultureInfo.InvariantCulture);
                    await ReplyAsync($"350 Restarting at {rest}.", ct);
                    return true;
                case "PASV":
                    {
                        pasv?.Stop();
                        pasv = new TcpListener(IPAddress.Loopback, 0);
                        pasv.Start(4);
                        var port = ((IPEndPoint)pasv.LocalEndpoint).Port;
                        await ReplyAsync($"227 Entering Passive Mode (127,0,0,1,{port >> 8},{port & 255}).", ct);
                        return true;
                    }

                case "LIST":
                case "NLST":
                    await ListAsync(argument, ct);
                    return true;
                case "RETR":
                    await RetrieveAsync(argument, ct);
                    return true;
                case "STOR":
                case "APPE":
                    await StoreAsync(argument, verb == "APPE", ct);
                    return true;
                default:
                    await ReplyAsync("502 Command not implemented.", ct);
                    return true;
            }
        }

        private (string Virtual, string Real) Resolve(string argument)
        {
            var p = string.IsNullOrEmpty(argument) ? cwd : argument.StartsWith('/') ? argument : cwd.TrimEnd('/') + "/" + argument;
            var parts = new List<string>();
            foreach (var segment in p.Split('/'))
            {
                if (segment is "" or ".")
                {
                    continue;
                }

                if (segment == "..")
                {
                    if (parts.Count > 0)
                    {
                        parts.RemoveAt(parts.Count - 1);
                    }

                    continue;
                }

                parts.Add(segment);
            }

            var real = parts.Count == 0 ? server.Root : Path.Combine([server.Root, .. parts]);
            return ("/" + string.Join('/', parts), real);
        }

        private void ClosePasv()
        {
            pasv?.Stop();
            pasv = null;
        }

        private async Task<TcpClient?> AcceptDataAsync(CancellationToken ct)
        {
            var listening = pasv;
            pasv = null;
            if (listening == null)
            {
                return null;
            }

            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                var data = await listening.AcceptTcpClientAsync(timeout.Token);
                data.NoDelay = true;
                return data;
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            finally
            {
                listening.Stop();
            }
        }

        private static string ListDate(DateTime when) =>
            Math.Abs((DateTime.Now - when).TotalDays) < 180
                ? when.ToString("MMM dd HH:mm", CultureInfo.InvariantCulture)
                : when.ToString("MMM dd  yyyy", CultureInfo.InvariantCulture);

        private async Task ListAsync(string argument, CancellationToken ct)
        {
            // Bỏ qua tuỳ chọn kiểu "-a" / "-la" mà một số client gửi trước đường dẫn.
            var target = argument.Trim();
            while (target.StartsWith('-'))
            {
                var cut = target.IndexOf(' ');
                target = cut < 0 ? "" : target[(cut + 1)..].TrimStart();
            }

            var (virtualPath, real) = Resolve(target);
            if (!Directory.Exists(real))
            {
                ClosePasv();
                await ReplyAsync("550 No such directory.", ct);
                return;
            }

            using var data = await AcceptDataAsync(ct);
            if (data == null)
            {
                await ReplyAsync("425 Can't open data connection.", ct);
                return;
            }

            await ReplyAsync("150 Opening data connection for directory list.", ct);
            var rows = new List<string>
            {
                $"drwxr-xr-x 1 0 0 4096 {ListDate(Directory.GetLastWriteTime(real))} .",
                $"drwxr-xr-x 1 0 0 4096 {ListDate(Directory.GetLastWriteTime(real))} ..",
            };
            foreach (var entry in Directory.EnumerateFileSystemEntries(real).Order(StringComparer.Ordinal))
            {
                var name = Path.GetFileName(entry);
                rows.Add(Directory.Exists(entry)
                    ? $"drwxr-xr-x 1 0 0 4096 {ListDate(Directory.GetLastWriteTime(entry))} {name}"
                    : $"-rw-r--r-- 1 0 0 {new FileInfo(entry).Length.ToString(CultureInfo.InvariantCulture)} {ListDate(File.GetLastWriteTime(entry))} {name}");
            }

            if (server.ExtraListLines.TryGetValue(virtualPath, out var extra))
            {
                rows.AddRange(extra);
            }

            var dataStream = data.GetStream();
            await dataStream.WriteAsync(Utf8.GetBytes(string.Concat(rows.Select(r => r + "\r\n"))), ct);
            CloseGracefully(data);
            await ReplyAsync("226 Transfer complete.", ct);
        }

        /// <summary>Đóng kênh dữ liệu êm (FIN) để client nhận đủ mọi byte đã gửi.</summary>
        private static void CloseGracefully(TcpClient data)
        {
            try
            {
                data.Client.Shutdown(SocketShutdown.Send);
            }
            catch (SocketException)
            {
            }
            catch (ObjectDisposedException)
            {
            }

            data.Dispose();
        }

        private async Task RetrieveAsync(string argument, CancellationToken ct)
        {
            var offset = rest;
            rest = 0;
            var real = Resolve(argument).Real;
            if (!File.Exists(real))
            {
                ClosePasv();
                await ReplyAsync("550 No such file.", ct);
                return;
            }

            using var data = await AcceptDataAsync(ct);
            if (data == null)
            {
                await ReplyAsync("425 Can't open data connection.", ct);
                return;
            }

            await ReplyAsync("150 Opening BINARY mode data connection.", ct);
            var drop = TryConsume(ref server.dropRetr);
            var aborted = false;
            try
            {
                await using var file = new FileStream(real, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.Asynchronous);
                file.Seek(offset, SeekOrigin.Begin);
                // Khi cắt giữa chừng: gửi tối đa một nửa phần còn lại (không quá 64 KB) rồi đóng kênh.
                var limit = drop ? Math.Max(1, Math.Min((file.Length - offset) / 2, 64 * 1024)) : long.MaxValue;
                var dataStream = data.GetStream();
                var buffer = new byte[64 * 1024];
                long sent = 0;
                while (sent < limit)
                {
                    var read = await file.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, limit - sent)), ct);
                    if (read == 0)
                    {
                        break;
                    }

                    await dataStream.WriteAsync(buffer.AsMemory(0, read), ct);
                    sent += read;
                }
            }
            catch (Exception ex) when (ex is IOException or SocketException)
            {
                // Client đóng kênh trước (tải chia đoạn dừng ở cuối đoạn của nó).
                aborted = true;
            }

            CloseGracefully(data);
            await ReplyAsync(aborted || drop ? "426 Connection closed; transfer aborted." : "226 Transfer complete.", ct);
        }

        private async Task StoreAsync(string argument, bool append, CancellationToken ct)
        {
            var offset = rest;
            rest = 0;
            var real = Resolve(argument).Real;
            if (Directory.Exists(real) || !Directory.Exists(Path.GetDirectoryName(real)))
            {
                ClosePasv();
                await ReplyAsync("553 Could not create file.", ct);
                return;
            }

            using var data = await AcceptDataAsync(ct);
            if (data == null)
            {
                await ReplyAsync("425 Can't open data connection.", ct);
                return;
            }

            await ReplyAsync("150 Ok to send data.", ct);
            var drop = TryConsume(ref server.dropStor);
            var aborted = false;
            try
            {
                var mode = append ? FileMode.Append : offset > 0 && File.Exists(real) ? FileMode.Open : FileMode.Create;
                await using (var file = new FileStream(real, mode, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.Asynchronous))
                {
                    if (mode == FileMode.Open)
                    {
                        file.SetLength(offset);
                        file.Seek(offset, SeekOrigin.Begin);
                    }

                    var dataStream = data.GetStream();
                    var buffer = new byte[64 * 1024];
                    while (true)
                    {
                        var read = await dataStream.ReadAsync(buffer, ct);
                        if (read == 0)
                        {
                            break;
                        }

                        await file.WriteAsync(buffer.AsMemory(0, read), ct);
                        if (drop)
                        {
                            break;
                        }
                    }
                }

                if (drop)
                {
                    // Cắt ngang bằng RST, như PS5 làm rớt kênh dữ liệu.
                    data.Client.LingerState = new LingerOption(true, 0);
                }
            }
            catch (Exception ex) when (ex is IOException or SocketException)
            {
                aborted = true;
            }

            data.Dispose();
            await ReplyAsync(aborted || drop ? "426 Connection closed; transfer aborted." : "226 Transfer complete.", ct);
        }
    }
}
