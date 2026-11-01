using System.Buffers;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using PsViethoa.FpkgBuilder.Core.Localization;

namespace PsViethoa.FpkgBuilder.Core.Ftp;

// Client FTP cho ftpsrv homebrew trên PS5. Chuyển từ PSVIETHOA STUDIO (Ngô Phi Phương), dùng với sự đồng ý của tác giả.

/// <summary>Một lệnh FTP thất bại. <see cref="Code"/> là mã phản hồi ba chữ số (0 khi đường truyền đứt hoặc hết hạn chờ).</summary>
public sealed class FtpException(int code, string message) : Exception(message)
{
    public int Code { get; } = code;

    /// <summary>5xx: máy chủ từ chối hẳn; 4xx là lỗi tạm thời, đáng thử lại.</summary>
    public bool Permanent => Code >= 500 && Code < 600;
}

/// <summary>Một phản hồi của máy chủ (có thể nhiều dòng, nối bằng '\n').</summary>
public sealed record FtpReply(int Code, string Text)
{
    public bool Ok => Code is >= 200 and < 300;

    public bool Intermediate => Code is >= 100 and < 200 or >= 300 and < 400;
}

public enum FtpEntryKind
{
    File,
    Directory,
    Link,
}

/// <summary>Một dòng trong danh sách thư mục trên máy chủ.</summary>
public sealed record FtpEntry(string Name, string FullPath, FtpEntryKind Kind, long Size, DateTime? Modified, string Permissions, string? LinkTarget)
{
    public bool IsDirectory => Kind == FtpEntryKind.Directory;

    /// <summary>Chuỗi quyền kiểu "drwxr-xr-x" đổi thành số bát phân mà SITE CHMOD cần; null khi không rõ.</summary>
    public int? OctalMode => Ps5Ftp.PermissionsToOctal(Permissions);
}

/// <summary>Một dòng giao thức thô, cho ô nhật ký lệnh.</summary>
public sealed record FtpLogLine(bool Outgoing, string Text, DateTime When)
{
    public override string ToString() => $"{When:HH:mm:ss}  {(Outgoing ? "→" : "←")}  {Text}";
}

/// <summary>
/// Client FTP gọn nhưng đủ dùng, nhắm tới ftpsrv homebrew trên PS5 (cổng mặc định 2121).
/// Mỗi đối tượng giữ một kết nối điều khiển và KHÔNG an toàn đa luồng: mỗi luồng truyền cần một client riêng,
/// cũng là cách FileZilla chạy nhiều lượt truyền cùng lúc.
/// </summary>
public sealed class Ps5Ftp : IDisposable
{
    public const int DefaultPort = 2121;

    private TcpClient? control;
    private NetworkStream? stream;
    private StreamReader? reader;
    private readonly Encoding encoding = Encoding.UTF8;
    private readonly List<string> features = [];

    public string Host { get; private set; } = "";

    public int Port { get; private set; } = DefaultPort;

    public string Welcome { get; private set; } = "";

    public bool Connected => control?.Connected == true;

    /// <summary>Máy chủ trả lời FEAT có UTF8 và nhận OPTS UTF8 ON; tên khi đó gửi dạng UTF-8.</summary>
    public bool Utf8 { get; private set; }

    public bool SupportsMlsd => Has("MLSD");

    public bool SupportsRest => Has("REST");

    public bool SupportsMfmt => Has("MFMT");

    public bool SupportsSize => Has("SIZE");

    public bool SupportsEpsv => Has("EPSV");

    public IReadOnlyList<string> Features => features;

    /// <summary>Mọi lệnh gửi đi và mọi phản hồi nhận về, theo đúng thứ tự. Gắn vào đây để hiện nhật ký lệnh.</summary>
    public event Action<FtpLogLine>? Log;

    public int TimeoutSeconds { get; set; } = 20;

    /// <summary>Bộ đệm sao chép của ứng dụng. Hai bộ đệm cỡ này luân phiên để đĩa và mạng chạy chồng lên nhau (xem <see cref="PumpAsync"/>).</summary>
    public int BufferSize { get; set; } = 1024 * 1024;

    /// <summary>Bộ đệm socket của nhân hệ điều hành cho kênh dữ liệu. Đặt trước khi kết nối để cửa sổ TCP mở đủ rộng cho 1 Gbps.</summary>
    public int SocketBufferBytes { get; set; } = 4 * 1024 * 1024;

    private bool Has(string feature) => features.Any(f => f.StartsWith(feature, StringComparison.OrdinalIgnoreCase));

    private void Emit(bool outgoing, string text) => Log?.Invoke(new FtpLogLine(outgoing, text, DateTime.Now));

    // ── Kết nối ────────────────────────────────────────────────────────────
    public async Task ConnectAsync(string host, int port, string user, string pass, CancellationToken ct = default)
    {
        Disconnect();
        Host = host;
        Port = port;
        control = new TcpClient { NoDelay = true };
        using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            cts.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds));
            try
            {
                await control.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                control.Dispose();
                control = null;
                throw new FtpException(0, Loc.F("Ftp.Core.ConnectTimeout", host, port, TimeoutSeconds));
            }
        }

        control.ReceiveTimeout = control.SendTimeout = TimeoutSeconds * 1000;
        stream = control.GetStream();
        reader = new StreamReader(stream, encoding, false, 4096, true);

        var hello = await ReadReplyAsync(ct).ConfigureAwait(false);
        if (hello.Code != 220)
        {
            throw new FtpException(hello.Code, Loc.F("Ftp.Core.BadGreeting", hello.Text));
        }

        Welcome = hello.Text;

        var userReply = await SendAsync("USER " + (string.IsNullOrEmpty(user) ? "anonymous" : user), ct).ConfigureAwait(false);
        if (userReply.Code == 331)
        {
            var passReply = await SendAsync("PASS " + (string.IsNullOrEmpty(pass) ? "anonymous@" : pass), ct, mask: true).ConfigureAwait(false);
            if (!passReply.Ok)
            {
                throw new FtpException(passReply.Code, Loc.F("Ftp.Core.LoginRejected", passReply.Text));
            }
        }
        else if (!userReply.Ok)
        {
            throw new FtpException(userReply.Code, Loc.F("Ftp.Core.LoginRejected", userReply.Text));
        }

        await LoadFeaturesAsync(ct).ConfigureAwait(false);
        await TrySendAsync("TYPE I", ct).ConfigureAwait(false);
    }

    private async Task LoadFeaturesAsync(CancellationToken ct)
    {
        features.Clear();
        var reply = await TrySendAsync("FEAT", ct).ConfigureAwait(false);
        if (reply is { Code: 211 })
        {
            foreach (var line in reply.Text.Split('\n').Skip(1))
            {
                var t = line.Trim();
                if (t.Length == 0 || t.StartsWith("211", StringComparison.Ordinal))
                {
                    continue;
                }

                features.Add(t);
            }
        }

        if (Has("UTF8"))
        {
            var opts = await TrySendAsync("OPTS UTF8 ON", ct).ConfigureAwait(false);
            Utf8 = opts?.Ok == true;
        }

        // ftpsrv trên PS5 trả lời FEAT rất ít hoặc không gì cả; coi như có những lệnh cơ bản mà nó thực sự hỗ trợ.
        if (features.Count == 0)
        {
            features.AddRange(["SIZE", "REST STREAM"]);
        }
    }

    public void Disconnect()
    {
        if (control != null)
        {
            try
            {
                if (control.Connected)
                {
                    WriteLine("QUIT");
                }
            }
            catch (IOException)
            {
            }
            catch (SocketException)
            {
            }
            catch (ObjectDisposedException)
            {
            }

            try
            {
                reader?.Dispose();
            }
            catch (IOException)
            {
            }

            try
            {
                stream?.Dispose();
            }
            catch (IOException)
            {
            }

            try
            {
                control.Dispose();
            }
            catch (IOException)
            {
            }
        }

        control = null;
        stream = null;
        reader = null;
        features.Clear();
        Welcome = "";
        Utf8 = false;
    }

    public void Dispose() => Disconnect();

    // ── Gửi lệnh ───────────────────────────────────────────────────────────
    private void WriteLine(string command)
    {
        if (stream == null)
        {
            throw new FtpException(0, Loc.T("Ftp.Core.NotConnected"));
        }

        var bytes = encoding.GetBytes(command + "\r\n");
        stream.Write(bytes, 0, bytes.Length);
        stream.Flush();
    }

    /// <summary>Gửi một lệnh và trả phản hồi. Ném <see cref="FtpException"/> khi phản hồi là lỗi (4xx/5xx).</summary>
    public async Task<FtpReply> SendAsync(string command, CancellationToken ct = default, bool mask = false)
    {
        var reply = await SendRawAsync(command, ct, mask).ConfigureAwait(false);
        if (reply.Code >= 400)
        {
            throw new FtpException(reply.Code, reply.Text.Trim());
        }

        return reply;
    }

    /// <summary>Gửi một lệnh và trả phản hồi, không ném lỗi khi gặp 4xx/5xx.</summary>
    public async Task<FtpReply> SendRawAsync(string command, CancellationToken ct = default, bool mask = false)
    {
        Emit(true, mask ? command[..Math.Min(5, command.Length)] + " ********" : command);
        WriteLine(command);
        return await ReadReplyAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Gửi một lệnh; trả null thay vì ném lỗi khi máy chủ từ chối hoặc đường truyền đứt.</summary>
    private async Task<FtpReply?> TrySendAsync(string command, CancellationToken ct)
    {
        try
        {
            return await SendRawAsync(command, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or SocketException or FtpException)
        {
            return null;
        }
    }

    /// <summary>
    /// Đọc một phản hồi (có thể nhiều dòng). Đọc socket bất đồng bộ bỏ qua ReceiveTimeout, nên máy chủ im lặng sẽ làm treo mãi:
    /// phản hồi phải về trong <paramref name="timeoutSeconds"/> giây (mặc định <see cref="TimeoutSeconds"/>).
    /// Hết hạn chờ thì kênh điều khiển không dùng được nữa; bên gọi coi mã 0 là "kết nối lại".
    /// </summary>
    private async Task<FtpReply> ReadReplyAsync(CancellationToken ct, int? timeoutSeconds = null)
    {
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
        idle.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds ?? TimeoutSeconds));
        try
        {
            return await ReadReplyCoreAsync(idle.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new FtpException(0, Loc.F("Ftp.Core.ReplyTimeout", timeoutSeconds ?? TimeoutSeconds));
        }
    }

    private async Task<FtpReply> ReadReplyCoreAsync(CancellationToken ct)
    {
        if (reader == null)
        {
            throw new FtpException(0, Loc.T("Ftp.Core.NotConnected"));
        }

        var first = await reader.ReadLineAsync(ct).ConfigureAwait(false)
                    ?? throw new FtpException(0, Loc.T("Ftp.Core.ServerClosed"));
        Emit(false, first);
        var all = new StringBuilder(first);
        if (first.Length >= 4 && first[3] == '-')
        {
            var tag = first[..3];
            while (true)
            {
                var line = await reader.ReadLineAsync(ct).ConfigureAwait(false)
                           ?? throw new FtpException(0, Loc.T("Ftp.Core.ServerClosedMultiline"));
                Emit(false, line);
                all.Append('\n').Append(line);
                if (line.Length >= 4 && line.StartsWith(tag, StringComparison.Ordinal) && line[3] == ' ')
                {
                    break;
                }
            }
        }

        var text = all.ToString();
        var code = int.TryParse(text.AsSpan(0, Math.Min(3, text.Length)), out var c) ? c : 0;
        return new FtpReply(code, text);
    }

    // ── Duyệt thư mục ──────────────────────────────────────────────────────
    public async Task<string> GetWorkingDirectoryAsync(CancellationToken ct = default)
    {
        var reply = await SendAsync("PWD", ct).ConfigureAwait(false);
        var q1 = reply.Text.IndexOf('"');
        var q2 = q1 >= 0 ? reply.Text.IndexOf('"', q1 + 1) : -1;
        return q1 >= 0 && q2 > q1 ? reply.Text[(q1 + 1)..q2] : "/";
    }

    public async Task ChangeDirectoryAsync(string path, CancellationToken ct = default)
        => await SendAsync("CWD " + path, ct).ConfigureAwait(false);

    public async Task<bool> DirectoryExistsAsync(string path, CancellationToken ct = default)
    {
        var current = await GetWorkingDirectoryAsync(ct).ConfigureAwait(false);
        var reply = await SendRawAsync("CWD " + path, ct).ConfigureAwait(false);
        if (reply.Ok)
        {
            await SendRawAsync("CWD " + current, ct).ConfigureAwait(false);
        }

        return reply.Ok;
    }

    public async Task CreateDirectoryAsync(string path, CancellationToken ct = default)
        => await SendAsync("MKD " + path, ct).ConfigureAwait(false);

    public async Task RemoveDirectoryAsync(string path, CancellationToken ct = default)
        => await SendAsync("RMD " + path, ct).ConfigureAwait(false);

    public async Task DeleteFileAsync(string path, CancellationToken ct = default)
        => await SendAsync("DELE " + path, ct).ConfigureAwait(false);

    public async Task RenameAsync(string from, string to, CancellationToken ct = default)
    {
        await SendAsync("RNFR " + from, ct).ConfigureAwait(false);
        await SendAsync("RNTO " + to, ct).ConfigureAwait(false);
    }

    /// <summary>SITE CHMOD. Không phải bản ftpsrv nào cũng có; trả false khi bị từ chối.</summary>
    public async Task<bool> ChmodAsync(string path, int octalMode, CancellationToken ct = default)
    {
        var reply = await SendRawAsync($"SITE CHMOD {Convert.ToString(octalMode, 8).PadLeft(3, '0')} {path}", ct).ConfigureAwait(false);
        return reply.Ok;
    }

    /// <summary>Dung lượng tệp trên máy chủ (SIZE); -1 khi tệp không có hoặc lệnh bị từ chối.</summary>
    public async Task<long> GetSizeAsync(string path, CancellationToken ct = default)
    {
        var reply = await SendRawAsync("SIZE " + path, ct).ConfigureAwait(false);
        return reply.Code == 213 && long.TryParse(reply.Text[3..].Trim(), out var size) ? size : -1;
    }

    /// <summary>Thời điểm sửa tệp (MDTM), đổi sang giờ máy; null khi không có.</summary>
    public async Task<DateTime?> GetModifiedAsync(string path, CancellationToken ct = default)
    {
        var reply = await SendRawAsync("MDTM " + path, ct).ConfigureAwait(false);
        if (reply.Code != 213)
        {
            return null;
        }

        var text = reply.Text[3..].Trim();
        return DateTime.TryParseExact(text[..Math.Min(14, text.Length)], "yyyyMMddHHmmss",
            CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var when)
            ? when.ToLocalTime()
            : null;
    }

    /// <summary>Chép thời điểm sửa của tệp máy tính sang tệp vừa tải lên (MFMT). Máy chủ không hỗ trợ thì trả false, không báo lỗi.</summary>
    public async Task<bool> SetModifiedAsync(string path, DateTime localTime, CancellationToken ct = default)
    {
        var reply = await SendRawAsync($"MFMT {localTime.ToUniversalTime():yyyyMMddHHmmss} {path}", ct).ConfigureAwait(false);
        return reply.Ok;
    }

    public async Task NoopAsync(CancellationToken ct = default) => await SendRawAsync("NOOP", ct).ConfigureAwait(false);

    // ── Kênh dữ liệu ───────────────────────────────────────────────────────
    private async Task<TcpClient> OpenDataAsync(CancellationToken ct)
    {
        if (SupportsEpsv)
        {
            var epsv = await SendRawAsync("EPSV", ct).ConfigureAwait(false);
            if (epsv.Code == 229)
            {
                var open = epsv.Text.IndexOf('(');
                var close = epsv.Text.IndexOf(')', open + 1);
                if (open >= 0 && close > open)
                {
                    var parts = epsv.Text[(open + 1)..close].Split('|');
                    if (parts.Length >= 4 && int.TryParse(parts[3], out var eport))
                    {
                        return await ConnectDataAsync(Host, eport, ct).ConfigureAwait(false);
                    }
                }
            }
        }

        var pasv = await SendAsync("PASV", ct).ConfigureAwait(false);
        var lp = pasv.Text.IndexOf('(');
        var rp = pasv.Text.IndexOf(')', lp + 1);
        if (lp < 0 || rp < lp)
        {
            throw new FtpException(pasv.Code, Loc.F("Ftp.Core.PasvUnreadable", pasv.Text.Trim()));
        }

        var n = pasv.Text[(lp + 1)..rp].Split(',');
        if (n.Length < 6)
        {
            throw new FtpException(pasv.Code, Loc.F("Ftp.Core.PasvIncomplete", pasv.Text.Trim()));
        }

        var ip = string.Join('.', n[0].Trim(), n[1].Trim(), n[2].Trim(), n[3].Trim());
        var port = (int.Parse(n[4].Trim(), CultureInfo.InvariantCulture) << 8) | int.Parse(n[5].Trim(), CultureInfo.InvariantCulture);
        // Có máy chủ báo địa chỉ LAN không đi tới được; địa chỉ của kết nối điều khiển thì luôn dùng được.
        if (!IPAddress.TryParse(ip, out var parsed) || IPAddress.IsLoopback(parsed))
        {
            ip = Host;
        }

        return await ConnectDataAsync(ip, port, ct).ConfigureAwait(false);
    }

    private async Task<TcpClient> ConnectDataAsync(string host, int port, CancellationToken ct)
    {
        // Một loạt hàng nghìn tệp rất nhỏ có thể tạm hết cổng cục bộ (kênh tải lên đã đóng nằm ~2 phút ở TIME_WAIT): đó là lỗi
        // tạm thời, nên chờ một chút rồi thử lại thay vì báo lỗi tệp.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await ConnectDataOnceAsync(host, port, ct).ConfigureAwait(false);
            }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.AddressAlreadyInUse or SocketError.NoBufferSpaceAvailable && attempt < 25 && !ct.IsCancellationRequested)
            {
                await Task.Delay(Math.Min(1000, 40 * attempt), ct).ConfigureAwait(false);
            }
        }
    }

    private async Task<TcpClient> ConnectDataOnceAsync(string host, int port, CancellationToken ct)
    {
        // Socket IPv4 thuần khi PS5 được gọi bằng địa chỉ IPv4 (socket hai chế độ mặc định bỏ qua tuỳ chọn dùng lại cổng).
        var data = IPAddress.TryParse(host, out var address) && address.AddressFamily == AddressFamily.InterNetwork
            ? new TcpClient(AddressFamily.InterNetwork)
            : new TcpClient();
        data.NoDelay = true;
        data.ReceiveBufferSize = SocketBufferBytes;
        data.SendBufferSize = SocketBufferBytes;
        // Mỗi lượt tải lên đóng kênh dữ liệu trước, cổng cục bộ vì vậy nằm ở TIME_WAIT ~2 phút. Hàng chục nghìn tệp nhỏ làm Windows
        // hết cổng tạm ("Only one usage of each socket address"). SO_REUSE_UNICASTPORT cho phép dùng lại một cổng tới cổng máy chủ
        // khác (mỗi PASV là một cổng mới). Tuỳ chọn này chỉ có trên Windows.
        if (OperatingSystem.IsWindows())
        {
            try
            {
                data.Client.SetSocketOption((SocketOptionLevel)0xFFFF, (SocketOptionName)0x3007, 1);
            }
            catch (SocketException)
            {
            }
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds));
            await data.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            data.Dispose();
            throw new FtpException(0, Loc.F("Ftp.Core.DataConnectFailed", host, port));
        }
        catch
        {
            data.Dispose();
            throw;
        }

        data.ReceiveTimeout = data.SendTimeout = TimeoutSeconds * 1000;
        return data;
    }

    // ── Liệt kê ────────────────────────────────────────────────────────────
    public async Task<IReadOnlyList<FtpEntry>> ListAsync(string path, CancellationToken ct = default)
    {
        var dir = NormalizeDir(path);
        // LIST thường: có máy chủ đọc "LIST -a" thành tên tệp. ftpsrv đã liệt kê cả tệp bắt đầu bằng dấu chấm.
        var lines = await ReadLinesAsync(SupportsMlsd ? "MLSD " + dir : "LIST " + dir, ct).ConfigureAwait(false);

        var list = new List<FtpEntry>();
        foreach (var line in lines)
        {
            var entry = SupportsMlsd ? ParseMlsd(line, dir) : ParseUnix(line, dir);
            if (entry == null || entry.Name is "." or "..")
            {
                continue;
            }

            list.Add(entry);
        }

        return list;
    }

    private async Task<List<string>> ReadLinesAsync(string command, CancellationToken ct)
    {
        using var data = await OpenDataAsync(ct).ConfigureAwait(false);
        var started = await SendRawAsync(command, ct).ConfigureAwait(false);
        if (started.Code >= 400)
        {
            throw new FtpException(started.Code, started.Text.Trim());
        }

        var lines = new List<string>();
        using (var idle = CancellationTokenSource.CreateLinkedTokenSource(ct))
        using (var dataStream = data.GetStream())
        using (var dataReader = new StreamReader(dataStream, encoding))
        {
            try
            {
                while (true)
                {
                    // Đặt lại mỗi dòng: đo thời gian im lặng, không phải tổng thời gian.
                    idle.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds));
                    if (await dataReader.ReadLineAsync(idle.Token).ConfigureAwait(false) is not { } line)
                    {
                        break;
                    }

                    if (line.Trim().Length > 0)
                    {
                        lines.Add(line);
                    }
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new FtpException(0, Loc.F("Ftp.Core.ListStalled", TimeoutSeconds));
            }
        }

        var done = await ReadReplyAsync(ct).ConfigureAwait(false);
        if (done.Code >= 400)
        {
            throw new FtpException(done.Code, done.Text.Trim());
        }

        return lines;
    }

    // ── Truyền tệp ─────────────────────────────────────────────────────────
    /// <summary>Tải <paramref name="remote"/> về <paramref name="target"/>. <paramref name="resumeFrom"/> &gt; 0 thì gửi REST.</summary>
    public async Task DownloadAsync(string remote, Stream target, long resumeFrom, Action<long>? progress, CancellationToken ct = default)
    {
        using var data = await OpenDataAsync(ct).ConfigureAwait(false);
        if (resumeFrom > 0)
        {
            var rest = await SendRawAsync("REST " + resumeFrom.ToString(CultureInfo.InvariantCulture), ct).ConfigureAwait(false);
            if (rest.Code != 350)
            {
                // Máy chủ sẽ gửi cả tệp: làm lại đích từ đầu thay vì nối thêm một bản thứ hai.
                if (!target.CanSeek)
                {
                    throw new FtpException(rest.Code, Loc.F("Ftp.Core.RestUnsupported", rest.Text.Trim()));
                }

                target.SetLength(0);
                target.Position = 0;
                resumeFrom = 0;
            }
        }

        var started = await SendRawAsync("RETR " + remote, ct).ConfigureAwait(false);
        if (started.Code >= 400)
        {
            throw new FtpException(started.Code, started.Text.Trim());
        }

        using (var dataStream = data.GetStream())
        {
            await PumpAsync(dataStream, target, resumeFrom, -1, progress, ct).ConfigureAwait(false);
        }

        var done = await ReadReplyAsync(ct).ConfigureAwait(false);
        if (done.Code >= 400)
        {
            throw new FtpException(done.Code, done.Text.Trim());
        }

        // Máy chủ đã xác nhận đủ mọi byte (226): đóng hẳn kênh không qua TIME_WAIT để hàng nghìn tệp nhỏ không làm cạn cổng cục bộ.
        try
        {
            data.Client.LingerState = new LingerOption(true, 0);
        }
        catch (SocketException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>
    /// Tải đúng <paramref name="length"/> byte bắt đầu từ <paramref name="offset"/>, để chia một tệp lớn ra nhiều kết nối.
    /// Khi đoạn dừng trước cuối tệp, kênh dữ liệu bị cắt sớm và kênh điều khiển rơi vào trạng thái không xác định, nên bên gọi
    /// phải huỷ client này sau đó (giá trị trả về cho biết máy chủ có tự tới cuối tệp hay không).
    /// </summary>
    public async Task<bool> DownloadRangeAsync(string remote, Stream target, long offset, long length, Action<long>? progress, CancellationToken ct = default)
    {
        using var data = await OpenDataAsync(ct).ConfigureAwait(false);
        if (offset > 0)
        {
            var rest = await SendRawAsync("REST " + offset.ToString(CultureInfo.InvariantCulture), ct).ConfigureAwait(false);
            if (rest.Code != 350)
            {
                throw new FtpException(rest.Code, Loc.F("Ftp.Core.RestNoSegments", rest.Text.Trim()));
            }
        }

        var started = await SendRawAsync("RETR " + remote, ct).ConfigureAwait(false);
        if (started.Code >= 400)
        {
            throw new FtpException(started.Code, started.Text.Trim());
        }

        long got;
        using (var dataStream = data.GetStream())
        {
            got = await PumpAsync(dataStream, target, 0, length, progress, ct).ConfigureAwait(false);
        }

        if (got < length)
        {
            throw new FtpException(0, Loc.F("Ftp.Core.DataClosedEarly", got, length));
        }

        // Chỉ đọc phản hồi kết thúc khi máy chủ tự tới cuối tệp; nếu không thì kênh đã bị cắt.
        try
        {
            var done = await ReadReplyAsync(ct, 3).ConfigureAwait(false);
            return done.Code is >= 200 and < 300;
        }
        catch (Exception ex) when (ex is IOException or FtpException)
        {
            return false;
        }
    }

    /// <summary>
    /// Sao chép hai bộ đệm: khối sau được đọc trong lúc khối trước vẫn đang ghi, nên đĩa và mạng chạy chồng lên nhau thay vì
    /// lần lượt. <paramref name="limit"/> &lt; 0 thì chép tới hết luồng.
    /// Trả tổng đang chạy (tính từ <paramref name="start"/>) như giá trị đã đưa cho <paramref name="progress"/>.
    /// </summary>
    private async Task<long> PumpAsync(Stream from, Stream to, long start, long limit, Action<long>? progress, CancellationToken ct)
    {
        // Bộ đệm mượn từ kho chung, cỡ vừa lượt truyền: cấp mới hai mảng 1 MB cho mỗi tệp biến một thư mục 100 000 tệp nhỏ thành
        // ~200 GB rác trên vùng nhớ lớn, và các lần GC toàn phần nó gây ra làm đứng luồng giao diện.
        long hint = limit >= 0 ? limit : from.CanSeek ? Math.Max(0, from.Length - from.Position) : BufferSize;
        var size = (int)Math.Clamp(hint, 4096, BufferSize);
        var a = ArrayPool<byte>.Shared.Rent(size);
        var b = ArrayPool<byte>.Shared.Rent(size);
        var remaining = limit < 0 ? long.MaxValue : limit;
        var total = start;
        var pending = Task.CompletedTask;
        // Đọc/ghi socket bất đồng bộ bỏ qua ReceiveTimeout/SendTimeout: thay vào đó đặt lại hẹn giờ "im lặng" trước mỗi khối,
        // để PS5 đứng im làm lỗi tệp này sau TimeoutSeconds thay vì treo luồng mãi mãi.
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = idle.Token;
        var idleSpan = TimeSpan.FromSeconds(TimeoutSeconds);
        try
        {
            idle.CancelAfter(idleSpan);
            var want = (int)Math.Min(size, remaining);
            var read = want == 0 ? 0 : await from.ReadAsync(a.AsMemory(0, want), token).ConfigureAwait(false);
            while (read > 0)
            {
                // Bộ đệm 'b' rảnh lại khi lượt ghi trước đã xong.
                await pending.ConfigureAwait(false);
                idle.CancelAfter(idleSpan);
                pending = to.WriteAsync(a.AsMemory(0, read), token).AsTask();
                total += read;
                remaining -= read;
                progress?.Invoke(limit < 0 ? total : total - start);
                (a, b) = (b, a);
                if (remaining <= 0)
                {
                    break;
                }

                want = (int)Math.Min(size, remaining);
                read = await from.ReadAsync(a.AsMemory(0, want), token).ConfigureAwait(false);
            }

            await pending.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new FtpException(0, Loc.F("Ftp.Core.DataStalled", TimeoutSeconds));
        }
        finally
        {
            // Không bao giờ trả bộ đệm khi một lượt ghi có thể vẫn đang dùng nó.
            try
            {
                await pending.ConfigureAwait(false);
            }
            catch
            {
                // Lỗi của lượt ghi dở đã được báo ở trên (hoặc không còn quan trọng); ở đây chỉ chờ nó xong.
            }

            ArrayPool<byte>.Shared.Return(a);
            ArrayPool<byte>.Shared.Return(b);
        }

        return limit < 0 ? total : total - start;
    }

    /// <summary>Tải <paramref name="source"/> lên <paramref name="remote"/>. <paramref name="resumeFrom"/> &gt; 0 thì ghi tiếp bằng REST.</summary>
    public async Task UploadAsync(Stream source, string remote, long resumeFrom, Action<long>? progress, CancellationToken ct = default)
    {
        using var data = await OpenDataAsync(ct).ConfigureAwait(false);
        var command = "STOR " + remote;
        if (resumeFrom > 0)
        {
            // REST+STOR (ghi từ vị trí đó) hoặc, khi REST bị từ chối, APPE (nối vào cuối). Cả hai đều cần nguồn đặt ở cùng vị trí,
            // nếu không phần đầu tệp sẽ bị gửi hai lần.
            var rest = await SendRawAsync("REST " + resumeFrom.ToString(CultureInfo.InvariantCulture), ct).ConfigureAwait(false);
            if (rest.Code != 350)
            {
                command = "APPE " + remote;
            }

            source.Seek(resumeFrom, SeekOrigin.Begin);
        }

        var started = await SendRawAsync(command, ct).ConfigureAwait(false);
        if (started.Code >= 400)
        {
            throw new FtpException(started.Code, started.Text.Trim());
        }

        using (var dataStream = data.GetStream())
        {
            await PumpAsync(source, dataStream, resumeFrom, -1, progress, ct).ConfigureAwait(false);
            await dataStream.FlushAsync(ct).ConfigureAwait(false);
            data.Client.Shutdown(SocketShutdown.Send);
        }

        var done = await ReadReplyAsync(ct).ConfigureAwait(false);
        if (done.Code >= 400)
        {
            throw new FtpException(done.Code, done.Text.Trim());
        }
    }

    /// <summary>Đọc cả tệp trên máy chủ vào bộ nhớ (dùng cho trình sửa tích hợp).</summary>
    public async Task<byte[]> ReadAllBytesAsync(string remote, CancellationToken ct = default)
    {
        using var buffer = new MemoryStream();
        await DownloadAsync(remote, buffer, 0, null, ct).ConfigureAwait(false);
        return buffer.ToArray();
    }

    /// <summary>Ghi đè cả tệp trên máy chủ bằng <paramref name="content"/>.</summary>
    public async Task WriteAllBytesAsync(string remote, byte[] content, CancellationToken ct = default)
    {
        using var buffer = new MemoryStream(content, false);
        await UploadAsync(buffer, remote, 0, null, ct).ConfigureAwait(false);
    }

    // ── Đường dẫn và đọc danh sách ─────────────────────────────────────────
    /// <summary>Chuẩn hoá đường dẫn thư mục trên máy chủ: dấu '/', bắt đầu bằng '/', không có '/' ở cuối (trừ gốc).</summary>
    public static string NormalizeDir(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "/";
        }

        var p = path.Replace('\\', '/').Trim();
        while (p.Length > 1 && p.EndsWith('/'))
        {
            p = p[..^1];
        }

        return p.StartsWith('/') ? p : "/" + p;
    }

    public static string Combine(string dir, string name)
    {
        var d = NormalizeDir(dir);
        return d == "/" ? "/" + name.TrimStart('/') : d + "/" + name.TrimStart('/');
    }

    public static string Parent(string path)
    {
        var p = NormalizeDir(path);
        if (p == "/")
        {
            return "/";
        }

        var cut = p.LastIndexOf('/');
        return cut <= 0 ? "/" : p[..cut];
    }

    public static string FileName(string path)
    {
        var p = path.Replace('\\', '/').TrimEnd('/');
        var cut = p.LastIndexOf('/');
        return cut < 0 ? p : p[(cut + 1)..];
    }

    /// <summary>"drwxr-xr-x" → 755. Trả null với mọi chuỗi không phải chuỗi quyền 10 ký tự.</summary>
    public static int? PermissionsToOctal(string? permissions)
    {
        if (permissions is not { Length: >= 10 })
        {
            return null;
        }

        var value = 0;
        for (var group = 0; group < 3; group++)
        {
            var digit = 0;
            var at = 1 + group * 3;
            if (permissions[at] == 'r')
            {
                digit |= 4;
            }

            if (permissions[at + 1] == 'w')
            {
                digit |= 2;
            }

            if (permissions[at + 2] is 'x' or 's' or 't')
            {
                digit |= 1;
            }

            value = value * 8 + digit;
        }

        return value;
    }

    private static FtpEntry? ParseMlsd(string line, string dir)
    {
        var split = line.IndexOf(' ');
        if (split < 0)
        {
            return null;
        }

        var name = line[(split + 1)..].Trim();
        if (name.Length == 0)
        {
            return null;
        }

        var kind = FtpEntryKind.File;
        long size = 0;
        DateTime? modified = null;
        var permissions = "";
        foreach (var fact in line[..split].Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = fact.IndexOf('=');
            if (eq < 0)
            {
                continue;
            }

            var key = fact[..eq].Trim().ToLowerInvariant();
            var value = fact[(eq + 1)..].Trim();
            switch (key)
            {
                case "type":
                    kind = value.Equals("dir", StringComparison.OrdinalIgnoreCase) || value.Equals("cdir", StringComparison.OrdinalIgnoreCase)
                        || value.Equals("pdir", StringComparison.OrdinalIgnoreCase)
                        ? FtpEntryKind.Directory
                        : value.StartsWith("OS.unix=slink", StringComparison.OrdinalIgnoreCase) ? FtpEntryKind.Link : FtpEntryKind.File;
                    break;
                case "size":
                    long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out size);
                    break;
                case "unix.mode":
                    permissions = value;
                    break;
                case "modify":
                    if (DateTime.TryParseExact(value[..Math.Min(14, value.Length)], "yyyyMMddHHmmss", CultureInfo.InvariantCulture,
                            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var when))
                    {
                        modified = when.ToLocalTime();
                    }

                    break;
            }
        }

        return new FtpEntry(name, Combine(dir, name), kind, size, modified, permissions, null);
    }

    /// <summary>Đọc một dòng "ls -l" kiểu Unix, định dạng mà ftpsrv trên PS5 dùng.</summary>
    private static FtpEntry? ParseUnix(string line, string dir)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 9)
        {
            return null;
        }

        var mode = parts[0];
        if (mode.Length < 10)
        {
            return null;
        }

        var kind = mode[0] switch
        {
            'd' => FtpEntryKind.Directory,
            'l' => FtpEntryKind.Link,
            _ => FtpEntryKind.File,
        };
        long.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var size);

        // Trường 5..7 là "Mon DD HH:MM" hoặc "Mon DD  YYYY"; tên là toàn bộ phần sau chúng.
        var stamp = $"{parts[5]} {parts[6]} {parts[7]}";
        DateTime? modified = null;
        if (DateTime.TryParseExact(stamp, ["MMM d HH:mm", "MMM dd HH:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var sameYear))
        {
            modified = new DateTime(DateTime.Now.Year, sameYear.Month, sameYear.Day, sameYear.Hour, sameYear.Minute, 0);
            if (modified > DateTime.Now.AddDays(1))
            {
                modified = modified.Value.AddYears(-1);
            }
        }
        else if (DateTime.TryParseExact(stamp, ["MMM d yyyy", "MMM dd yyyy"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var otherYear))
        {
            modified = otherYear;
        }

        var nameStart = 0;
        for (var i = 0; i < 8; i++)
        {
            nameStart = line.IndexOf(parts[i], nameStart, StringComparison.Ordinal) + parts[i].Length;
        }

        var name = line[nameStart..].TrimStart();
        string? target = null;
        if (kind == FtpEntryKind.Link)
        {
            var arrow = name.IndexOf(" -> ", StringComparison.Ordinal);
            if (arrow > 0)
            {
                target = name[(arrow + 4)..];
                name = name[..arrow];
            }
        }

        if (name.Length == 0)
        {
            return null;
        }

        return new FtpEntry(name, Combine(dir, name), kind, size, modified, mode, target);
    }
}
