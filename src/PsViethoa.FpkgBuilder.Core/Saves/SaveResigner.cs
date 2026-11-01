using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using PsViethoa.FpkgBuilder.Core.Localization;

namespace PsViethoa.FpkgBuilder.Core.Saves;

/// <summary>garlic-savemgr / PS5 daemon từ chối hoặc kết quả không đúng như mong đợi.</summary>
public sealed class GarlicException(string message) : Exception(message);

/// <summary>Loại mục trong hàng đợi resign, nhận theo nội dung tệp.</summary>
public enum SaveKind
{
    Folder,
    Zip,
    Ps5Encrypted,
    Ps4Key,
    Ps4Encrypted,
    ParamSfo,
    Other,
}

/// <summary>Thiết lập chung của một lượt resign. UserId = 0 nghĩa là chưa nhập.</summary>
public sealed record ResignOptions(ulong AccountId, uint UserId, string Host, int Port, bool Install, string? TitleId)
{
    public string AccountHex => AccountId.ToString("x16");

    public string UserHex => UserId.ToString("x8");
}

/// <summary>Một người dùng trên PS5.</summary>
public sealed record Ps5User(string Id, string Name)
{
    public string Label => $"{Name} · {Id}";
}

/// <summary>Một save trên PS5 do garlic-savemgr liệt kê.</summary>
public sealed record GarlicSave(int Index, string Uid, string TitleId, string SaveName, string Type, bool Backup);

/// <summary>Số mục resign thành công / thất bại.</summary>
public sealed record ResignCount(int Ok, int Fail);

/// <summary>
/// Resign save PS4 / PS5 — chuyển từ PSVIETHOA Save Resigner (Ngô Phi Phương), dùng Garlic SaveMgr của earthonion chạy trên PS5.
/// <list type="bullet">
/// <item>PS5 daemon v2 (little-endian): REQ &lt;B op&gt;&lt;Q account&gt;&lt;I user&gt;&lt;Q size&gt;&lt;data&gt;; op 1 = giải mã + resign, op 3 = cài, op 0xFE = ping.</item>
/// <item>garlic-savemgr HTTP cổng 8082: save PS4 mã hoá, nhập save PS5 đã giải mã, liệt kê người dùng / save.</item>
/// </list>
/// Save đã giải mã trên máy tính được sửa tại chỗ (param.sfo, có bản .bak); save mã hoá được giải mã ra thư mục / ZIP mới cạnh tệp
/// nguồn — tệp mã hoá gốc không bị đổi.
/// </summary>
public static class SaveResigner
{
    public const int GarlicPort = 8082;
    public const int ElfLoaderPort = 9021;
    private const int Chunk = 1 << 20;
    private const int Ps4KeySize = 0x60;

    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        UseProxy = false,
        AutomaticDecompression = DecompressionMethods.None,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        PooledConnectionLifetime = TimeSpan.Zero,
    })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    private static ReadOnlySpan<byte> SkMagic => "pfsSKKey"u8;

    /// <summary>garlic-savemgr.elf nhúng trong ứng dụng (libs/garlic-savemgr), null khi bản dựng thiếu tệp.</summary>
    public static byte[]? BundledElf()
    {
        using var stream = typeof(SaveResigner).Assembly.GetManifestResourceStream("PsViethoa.FpkgBuilder.Core.GarlicSaveMgr.elf");
        if (stream == null)
        {
            return null;
        }

        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    /// <summary>Thư mục sao lưu save trên máy trước khi ghi đè (ứng dụng đặt theo thư mục dữ liệu của người dùng).</summary>
    public static string BackupRoot { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PSVIETHOA", "SaveBackups");

    // ── Nhận loại ──────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Chuỗi hex đúng <paramref name="bytes"/> byte (bỏ "0x", chữ thường), null khi sai.</summary>
    public static string? NormalizeHex(string? value, int bytes)
    {
        var s = (value ?? string.Empty).Trim().ToLowerInvariant();
        if (s.StartsWith("0x", StringComparison.Ordinal))
        {
            s = s[2..];
        }

        return s.Length == bytes * 2 && s.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f') ? s : null;
    }

    private static bool ReadAt(string path, long offset, Span<byte> buffer)
    {
        try
        {
            using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (f.Length < offset + buffer.Length)
            {
                return false;
            }

            f.Position = offset;
            f.ReadExactly(buffer);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static long SizeOf(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return -1;
        }
    }

    /// <summary>Ảnh save PS5 mã hoá: "pfsSKKey" ở 0x800.</summary>
    public static bool IsPs5Save(string path)
    {
        Span<byte> b = stackalloc byte[8];
        return File.Exists(path) && ReadAt(path, 0x800, b) && b.SequenceEqual(SkMagic);
    }

    /// <summary>Sealed key của save PS4: tệp 0x60 byte bắt đầu bằng "pfsSKKey".</summary>
    public static bool IsPs4SealedKey(string path)
    {
        Span<byte> b = stackalloc byte[8];
        return File.Exists(path) && SizeOf(path) == Ps4KeySize && ReadAt(path, 0, b) && b.SequenceEqual(SkMagic);
    }

    /// <summary>Ảnh save PS4 mã hoá (đi kèm tệp .bin sealed key).</summary>
    public static bool IsPs4EncryptedImage(string path)
    {
        if (!File.Exists(path) || path.EndsWith(".bin", StringComparison.OrdinalIgnoreCase) || SizeOf(path) < 0x1000)
        {
            return false;
        }

        Span<byte> first = stackalloc byte[1];
        return ReadAt(path, 0, first) && first[0] == 1 && !IsPs5Save(path);
    }

    /// <summary>Tệp .bin sealed key đi kèm ảnh save PS4 (tên ảnh + ".bin", hoặc bỏ tiền tố "sdimg_").</summary>
    public static string? FindPs4KeyForImage(string image)
    {
        var folder = Path.GetDirectoryName(image) ?? ".";
        var name = Path.GetFileName(image);
        var candidates = new List<string> { name + ".bin" };
        if (name.StartsWith("sdimg_", StringComparison.Ordinal))
        {
            candidates.Add(name[6..] + ".bin");
        }

        return candidates.Select(c => Path.Combine(folder, c)).FirstOrDefault(IsPs4SealedKey);
    }

    public static SaveKind Classify(string path)
    {
        if (Directory.Exists(path))
        {
            return SaveKind.Folder;
        }

        var name = Path.GetFileName(path).ToLowerInvariant();
        if (name.EndsWith(".zip", StringComparison.Ordinal))
        {
            return SaveKind.Zip;
        }

        if (IsPs5Save(path))
        {
            return SaveKind.Ps5Encrypted;
        }

        if (IsPs4SealedKey(path))
        {
            return SaveKind.Ps4Key;
        }

        if (IsPs4EncryptedImage(path))
        {
            return SaveKind.Ps4Encrypted;
        }

        return name == "param.sfo" ? SaveKind.ParamSfo : SaveKind.Other;
    }

    private sealed record Job(SaveKind Kind, string Path, string? Key = null);

    private static bool BackupHasPrimary(string path)
    {
        var name = Path.GetFileName(path);
        return name.StartsWith("sdimg_sce_bu_", StringComparison.OrdinalIgnoreCase) && IsPs5Save(Path.Combine(Path.GetDirectoryName(path)!, "sdimg_" + name[13..]));
    }

    private static List<Job> DiscoverJobs(string root)
    {
        IEnumerable<string> files = File.Exists(root)
            ? [root]
            : Directory.Exists(root)
                ? SaveFiles.Walk(root, d =>
                {
                    var n = Path.GetFileName(d);
                    return !n.StartsWith('.') && !n.EndsWith("_dec", StringComparison.Ordinal);
                })
                : Array.Empty<string>();
        var jobs = new List<Job>();
        foreach (var file in files)
        {
            var name = Path.GetFileName(file).ToLowerInvariant();
            if (name.EndsWith(".bin", StringComparison.Ordinal) && IsPs4SealedKey(file))
            {
                continue;
            }

            if (IsPs5Save(file) && !BackupHasPrimary(file))
            {
                jobs.Add(new Job(SaveKind.Ps5Encrypted, file));
            }
            else if (IsPs4EncryptedImage(file))
            {
                jobs.Add(new Job(SaveKind.Ps4Encrypted, file, FindPs4KeyForImage(file)));
            }
            else if (name == "param.sfo")
            {
                jobs.Add(new Job(SaveKind.ParamSfo, file));
            }
        }

        return jobs;
    }

    /// <summary>Mục cần tới PS5: save mã hoá, hoặc save PS5 đã giải mã sẽ được chép vào máy.</summary>
    public static bool NeedsConsole(string path, bool install)
    {
        var kind = Classify(path);
        if (kind is SaveKind.Ps4Encrypted or SaveKind.Ps5Encrypted)
        {
            return true;
        }

        if (kind is not (SaveKind.Folder or SaveKind.ParamSfo or SaveKind.Zip))
        {
            return false;
        }

        try
        {
            if (kind == SaveKind.Zip)
            {
                return install || ZipHasEncrypted(path);
            }

            return DiscoverJobs(path).Any(j => j.Kind is SaveKind.Ps4Encrypted or SaveKind.Ps5Encrypted || (install && j.Kind == SaveKind.ParamSfo && IsPs5PlainSfo(j.Path)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>ZIP có ảnh save mã hoá (đọc vài byte đầu của từng mục, không giải nén ra đĩa).</summary>
    private static bool ZipHasEncrypted(string zipPath)
    {
        try
        {
            using var archive = ZipFile.OpenRead(zipPath);
            foreach (var entry in archive.Entries)
            {
                if (entry.Length < 0x808 || entry.FullName.EndsWith('/'))
                {
                    continue;
                }

                using var stream = entry.Open();
                var head = new byte[0x808];
                stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
                if (head.AsSpan(0x800, 8).SequenceEqual(SkMagic) || (head[0] == 1 && !entry.Name.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
        }

        return false;
    }

    // ── ZIP ────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Giải nén ZIP an toàn: bỏ mục có đường dẫn tuyệt đối hoặc "..", không ghi ra ngoài thư mục đích.</summary>
    public static bool SafeExtractZip(string zipPath, string destination)
    {
        ZipArchive archive;
        try
        {
            archive = ZipFile.OpenRead(zipPath);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            return false;
        }

        using (archive)
        {
            var root = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            foreach (var entry in archive.Entries)
            {
                var name = entry.FullName.Replace('\\', '/');
                if (name.StartsWith('/') || name.Split('/').Contains(".."))
                {
                    continue;
                }

                try
                {
                    var target = Path.GetFullPath(Path.Combine(root, name));
                    if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (name.EndsWith('/'))
                    {
                        Directory.CreateDirectory(target);
                        continue;
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    entry.ExtractToFile(target, true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException or ArgumentException)
                {
                }
            }
        }

        return true;
    }

    private static void ZipDirectory(string source, string zipPath, CompressionLevel level, Func<string, bool>? include = null)
    {
        using var output = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var zip = new ZipArchive(output, ZipArchiveMode.Create);
        foreach (var file in SaveFiles.Walk(source))
        {
            if (include == null || include(file))
            {
                zip.CreateEntryFromFile(file, Path.GetRelativePath(source, file).Replace('\\', '/'), level);
            }
        }
    }

    /// <summary>Bản .bak cạnh param.sfo chỉ để hoàn tác trên máy tính — không bao giờ tải vào save trên máy.</summary>
    private static bool IsUploadable(string root, string file) =>
        !Path.GetRelativePath(root, file).Replace('\\', '/').Equals("sce_sys/param.sfo.bak", StringComparison.OrdinalIgnoreCase);

    private static string NewTempDirectory(string prefix) => Directory.CreateTempSubdirectory("psviethoa_" + prefix).FullName;

    private static void TryDelete(string? path)
    {
        try
        {
            if (path == null)
            {
                return;
            }

            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
            else if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    // ── PS5 daemon (TCP) ───────────────────────────────────────────────────────────────────────────────────────────

    private static TcpClient Connect(string host, int port, int connectSeconds, int ioSeconds)
    {
        var client = new TcpClient { NoDelay = true };
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(connectSeconds));
            client.ConnectAsync(host, port, cts.Token).AsTask().GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            client.Dispose();
            throw new TimeoutException(Loc.F("Resign.ConnectTimeout", host, port, connectSeconds));
        }
        catch
        {
            client.Dispose();
            throw;
        }

        client.ReceiveTimeout = client.SendTimeout = ioSeconds * 1000;
        return client;
    }

    private static void ReceiveExact(Stream stream, Span<byte> buffer)
    {
        var got = 0;
        while (got < buffer.Length)
        {
            var n = stream.Read(buffer[got..]);
            if (n == 0)
            {
                throw new IOException(Loc.T("Resign.DaemonCut"));
            }

            got += n;
        }
    }

    private static string ReceiveMessage(Stream stream)
    {
        Span<byte> len = stackalloc byte[4];
        ReceiveExact(stream, len);
        var bytes = new byte[Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(len), 4096u)];
        ReceiveExact(stream, bytes);
        return Encoding.UTF8.GetString(bytes);
    }

    private static byte[] DaemonHeader(byte op, ResignOptions o, long size)
    {
        var h = new byte[21];
        h[0] = op;
        BinaryPrimitives.WriteUInt64LittleEndian(h.AsSpan(1), o.AccountId);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(9), o.UserId);
        BinaryPrimitives.WriteUInt64LittleEndian(h.AsSpan(13), (ulong)size);
        return h;
    }

    private static void SendFile(Stream stream, string path)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, Chunk);
        input.CopyTo(stream, Chunk);
    }

    /// <summary>op 0xFE: (true, phiên bản) hoặc (false, lỗi).</summary>
    public static (bool Ok, string Message) PingDaemon(string host, int port, int timeoutSeconds = 5)
    {
        using var client = Connect(host, port, timeoutSeconds, timeoutSeconds);
        var stream = client.GetStream();
        var request = new byte[21];
        request[0] = 0xFE;
        stream.Write(request);
        Span<byte> status = stackalloc byte[1];
        ReceiveExact(stream, status);
        var message = ReceiveMessage(stream);
        return (status[0] == 1, message);
    }

    /// <summary>op 1: daemon gắn save mã hoá, đổi tài khoản và trả về ZIP nội dung đã giải mã.</summary>
    public static void DecryptResignPs5(ResignOptions o, string path, string outZip, Action<string> log)
    {
        var backend = ResolveInstaller(o, log);
        if (backend.Garlic)
        {
            GarlicDecryptResignPs5(o with { Port = backend.Port }, path, outZip, log);
            return;
        }

        var size = SizeOf(path);
        log("    [*] " + Loc.F("Resign.DecryptSend", Path.GetFileName(path), size.ToString("N0")));
        var watch = Stopwatch.StartNew();
        using (var client = Connect(o.Host, backend.Port, 10, 600))
        {
            var stream = client.GetStream();
            stream.Write(DaemonHeader(1, o, size));
            SendFile(stream, path);
            Span<byte> head = stackalloc byte[8];
            ReceiveExact(stream, head[..1]);
            if (head[0] != 1)
            {
                throw new IOException(Loc.F("Resign.Ps5Error", ReceiveMessage(stream)));
            }

            ReceiveExact(stream, head);
            var remaining = (long)BinaryPrimitives.ReadUInt64LittleEndian(head);
            using var output = new FileStream(outZip, FileMode.Create, FileAccess.Write, FileShare.None, Chunk);
            var buffer = new byte[Chunk];
            while (remaining > 0)
            {
                var part = buffer.AsSpan(0, (int)Math.Min(buffer.Length, remaining));
                ReceiveExact(stream, part);
                output.Write(part);
                remaining -= part.Length;
            }
        }

        log("    [OK] " + Loc.F("Resign.DecryptDone", watch.Elapsed.TotalSeconds.ToString("F1"), outZip));
    }

    /// <summary>op 3: nén (không nén dữ liệu) nội dung đã giải mã; daemon mã hoá lại và chép vào máy.</summary>
    public static string SendInstall(ResignOptions o, string contentDir, Action<string> log)
    {
        var temp = Path.Combine(Path.GetTempPath(), $"psviethoa_inst_{Environment.CurrentManagedThreadId}_{Path.GetFileName(contentDir)}.zip");
        try
        {
            ZipDirectory(contentDir, temp, CompressionLevel.NoCompression, f => IsUploadable(contentDir, f));
            var size = SizeOf(temp);
            log("    [*] " + Loc.F("Resign.InstallSend", Path.GetFileName(contentDir), size.ToString("N0")));
            using var client = Connect(o.Host, o.Port, 10, 600);
            var stream = client.GetStream();
            stream.Write(DaemonHeader(3, o, size));
            SendFile(stream, temp);
            Span<byte> status = stackalloc byte[1];
            ReceiveExact(stream, status);
            var message = ReceiveMessage(stream);
            if (status[0] != 1)
            {
                throw new IOException(Loc.F("Resign.Ps5Error", message));
            }

            log("    [OK] " + message);
            return message;
        }
        finally
        {
            TryDelete(temp);
        }
    }

    /// <summary>Gửi một ELF thẳng tới ELF loader (cổng 9021).</summary>
    public static long SendElf(string host, byte[] elf, Action<string>? log = null, Action<long, long>? progress = null, int port = ElfLoaderPort)
    {
        log?.Invoke("[*] " + Loc.F("Resign.ElfConnect", host, port));
        long sent = 0;
        using (var client = Connect(host, port, 10, 60))
        {
            var stream = client.GetStream();
            for (var offset = 0; offset < elf.Length; offset += Chunk)
            {
                var count = Math.Min(Chunk, elf.Length - offset);
                stream.Write(elf, offset, count);
                sent += count;
                progress?.Invoke(sent, elf.Length);
            }

            try
            {
                client.Client.Shutdown(SocketShutdown.Send);
            }
            catch (SocketException)
            {
            }
        }

        log?.Invoke("[OK] " + Loc.F("Resign.ElfSent", sent.ToString("N0")));
        return sent;
    }

    // ── garlic-savemgr HTTP ────────────────────────────────────────────────────────────────────────────────────────

    private sealed class FileUploadContent(string path, Action<string>? log) : HttpContent
    {
        private readonly long _size = new FileInfo(path).Length;

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, Chunk, true);
            var buffer = new byte[Chunk];
            long sent = 0;
            var lastPct = -1L;
            int n;
            while ((n = await input.ReadAsync(buffer)) > 0)
            {
                await stream.WriteAsync(buffer.AsMemory(0, n));
                sent += n;
                if (log == null || _size == 0)
                {
                    continue;
                }

                var pct = sent * 100 / _size;
                if (pct >= lastPct + 25 || sent == _size)
                {
                    lastPct = pct;
                    log("    [*] " + Loc.F("Resign.Upload", pct, sent.ToString("N0"), _size.ToString("N0")));
                }
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = _size;
            return true;
        }
    }

    private static byte[] Send(string host, int port, HttpMethod method, string path, int timeoutSeconds, HttpContent? content = null)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        using var request = new HttpRequestMessage(method, $"http://{host}:{port}{path}") { Content = content };
        request.Headers.ConnectionClose = true;
        if (content != null)
        {
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        }

        try
        {
            using var response = Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).GetAwaiter().GetResult();
            var body = response.Content.ReadAsByteArrayAsync(cts.Token).GetAwaiter().GetResult();
            if (response.StatusCode != HttpStatusCode.OK)
            {
                var text = Encoding.UTF8.GetString(body, 0, Math.Min(body.Length, 300));
                throw new GarlicException(text.Length > 0 ? text : $"HTTP {(int)response.StatusCode}");
            }

            return body;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            throw new TimeoutException(Loc.F("Resign.HttpTimeout", host, port, timeoutSeconds, path.Split('?')[0]));
        }
    }

    private static bool Truthy(JsonNode? node) => node switch
    {
        null => false,
        JsonObject o => o.Count > 0,
        JsonArray a => a.Count > 0,
        JsonValue v when v.TryGetValue<bool>(out var b) => b,
        JsonValue v when v.TryGetValue<string>(out var s) => s.Length > 0,
        JsonValue v when v.TryGetValue<double>(out var d) => d != 0,
        _ => true,
    };

    private static JsonNode? ParseGarlic(byte[] body)
    {
        var text = Encoding.UTF8.GetString(body);
        if (!text.StartsWith('{') && !text.StartsWith('['))
        {
            return JsonValue.Create(text);
        }

        var node = JsonNode.Parse(text);
        if (node is JsonObject o && Truthy(o["error"]))
        {
            throw new GarlicException(o["error"]!.ToString());
        }

        return node;
    }

    private static JsonNode? Garlic(string host, int port, string path, int timeoutSeconds) =>
        ParseGarlic(Send(host, port, HttpMethod.Get, path, timeoutSeconds));

    private static JsonNode? GarlicUpload(string host, int port, string path, string file, int timeoutSeconds, Action<string>? log = null) =>
        ParseGarlic(Send(host, port, HttpMethod.Post, path, timeoutSeconds, new FileUploadContent(file, log)));

    private static string Text(JsonNode? node, string key) => (node as JsonObject)?[key]?.ToString() ?? string.Empty;

    private static string Q(string value) => Uri.EscapeDataString(value);

    private static List<JsonNode?> Rows(JsonNode? listing, string key) =>
        (listing as JsonObject)?[key] is JsonArray array ? array.ToList() : new List<JsonNode?>();

    /// <summary>garlic-savemgr có trả lời /api/status không.</summary>
    public static (bool Ok, string Message) PingGarlic(string host, int port = GarlicPort, int timeoutSeconds = 5)
    {
        try
        {
            return Garlic(host, port, "/api/status", timeoutSeconds) is JsonObject o && o.ContainsKey("mounted")
                ? (true, "garlic-savemgr")
                : (false, Loc.T("Resign.NotGarlic"));
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static int ResolveGarlic(ResignOptions o)
    {
        var tried = new List<int>();
        foreach (var port in new[] { o.Port, GarlicPort })
        {
            if (port <= 0 || tried.Contains(port))
            {
                continue;
            }

            tried.Add(port);
            if (PingGarlic(o.Host, port, 4).Ok)
            {
                return port;
            }
        }

        throw new GarlicException(Loc.F("Resign.NoGarlic", string.Join(",", tried)));
    }

    private static void GarlicUnmount(string host, int port)
    {
        try
        {
            if (Garlic(host, port, "/api/status", 8) is JsonObject s && Truthy(s["mounted"]))
            {
                Garlic(host, port, "/api/unmount", 30);
            }
        }
        catch (Exception)
        {
            // Gỡ gắn là bước dọn dẹp: lỗi ở đây không được che lỗi chính của lượt resign.
        }
    }

    private static string? Ps5TitleFromPath(string path, string? configured)
    {
        static bool Valid(string? id) => id is { Length: 9 } && id.StartsWith("PPSA", StringComparison.OrdinalIgnoreCase) && id[4..].All(char.IsAsciiDigit);
        if (Valid(configured))
        {
            return configured!.ToUpperInvariant();
        }

        for (var folder = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(path))!); folder != null; folder = folder.Parent)
        {
            if (Valid(folder.Name))
            {
                return folder.Name.ToUpperInvariant();
            }
        }

        return null;
    }

    private sealed record NativePs5Save(byte[] Sfo, byte[] Image, byte[] Keystone, string Directory);

    private static string Ps5DirectoryFromImage(string path)
    {
        var dir = Path.GetFileNameWithoutExtension(path);
        if (dir.StartsWith("sdimg_", StringComparison.Ordinal))
        {
            dir = dir[6..];
        }

        if (dir.StartsWith("sce_bu_", StringComparison.Ordinal))
        {
            dir = dir[7..];
        }

        return dir;
    }

    private static string ParamsString(byte[] sfo, SaveSfoEntry entry, int offset) =>
        Encoding.ASCII.GetString(sfo, entry.DataOffset + offset, 16).Split('\0')[0];

    private static List<SaveSfoEntry> ValidateNativePs5Sfo(byte[] sfo, string title, ResignOptions o)
    {
        var entries = SaveSfo.Parse(sfo);
        if (!SaveSfo.IsPs5Format(sfo, entries) || SaveSfo.ReadString(sfo, entries, "FORMAT") != "ppr" || SaveSfo.ReadString(sfo, entries, "TITLE_ID") != title)
        {
            throw new GarlicException(Loc.T("Resign.NativeMismatch"));
        }

        var p = SaveSfo.Find(entries, "PARAMS");
        if (p is not { Format: 4, Length: >= 1024, MaxLength: >= 1024 } || p.DataOffset != 0x65c || SaveSfo.Find(entries, "ACCOUNT_ID")?.DataOffset != 0x1b8)
        {
            throw new GarlicException(Loc.T("Resign.NativeNoParams"));
        }

        if (!sfo.AsSpan(p.DataOffset + 8, 32).ContainsAnyExcept((byte)0))
        {
            throw new GarlicException(Loc.T("Resign.NativeNoHmac"));
        }

        var gameTitle = ParamsString(sfo, p, 0x3c);
        if (ParamsString(sfo, p, 0x2c) != title || gameTitle.Length != 9 || !gameTitle.StartsWith("PPSA", StringComparison.Ordinal) || !gameTitle[4..].All(char.IsAsciiDigit))
        {
            throw new GarlicException(Loc.T("Resign.NativeTitle"));
        }

        if (o.UserId != 0 && BinaryPrimitives.ReadUInt32LittleEndian(sfo.AsSpan(p.DataOffset + 4)) != o.UserId)
        {
            throw new GarlicException(Loc.T("Resign.NativeUser"));
        }

        if (o.AccountId != 0 && SaveSfo.ReadAccountId(sfo, entries) != o.AccountId)
        {
            throw new GarlicException(Loc.T("Resign.NativeAccount"));
        }

        var revision = SaveSfo.Find(entries, "SAVEDATA_REVISION");
        var blocks = SaveSfo.Find(entries, "SAVEDATA_BLOCKS");
        if (revision is not { Length: >= 16 } || !sfo.AsSpan(revision.DataOffset, 16).ContainsAnyExcept((byte)0) || blocks is not { Length: >= 8 })
        {
            throw new GarlicException(Loc.T("Resign.NativeRevision"));
        }

        return entries;
    }

    private static void MountPs5Copy(ResignOptions o, string image, Action<string> log)
    {
        GarlicUnmount(o.Host, o.Port);
        var result = GarlicUpload(o.Host, o.Port, "/api/decrypt_upload", image, 600, log);
        if (!Truthy((result as JsonObject)?["ok"]))
        {
            throw new GarlicException(Loc.T("Resign.MountNotConfirmed"));
        }
    }

    /// <summary>Đọc một bản sao mã hoá thay vì gắn thẳng save thật của người dùng.</summary>
    private static NativePs5Save ReadNativePs5Save(ResignOptions o, string title, string dir, Action<string> log)
    {
        if (o.UserId == 0)
        {
            throw new GarlicException(Loc.T("Resign.NeedUserForNative"));
        }

        var rows = Rows(Garlic(o.Host, o.Port, "/api/saves", 30), "saves");
        var candidates = rows.Select((row, index) => (Row: row, Index: index))
            .Where(x => Text(x.Row, "uid").Equals(o.UserHex, StringComparison.OrdinalIgnoreCase) && Text(x.Row, "title_id") == title &&
                        !Truthy((x.Row as JsonObject)?["backup"]) && !Text(x.Row, "save_name").StartsWith("sdimg_sce_bu_", StringComparison.Ordinal))
            .OrderByDescending(x => Text(x.Row, "save_name") == "sdimg_" + dir)
            .ToList();
        var temp = NewTempDirectory("native_ps5_");
        var errors = new List<string>();
        try
        {
            foreach (var candidate in candidates)
            {
                try
                {
                    var image = Send(o.Host, o.Port, HttpMethod.Get, "/api/download_raw?idx=" + candidate.Index, 300);
                    var copy = Path.Combine(temp, "native_image");
                    File.WriteAllBytes(copy, image);
                    if (!IsPs5Save(copy))
                    {
                        throw new GarlicException(Loc.T("Resign.NativeNotPfs"));
                    }

                    MountPs5Copy(o, copy, log);
                    var sfo = Send(o.Host, o.Port, HttpMethod.Get, "/api/download_file?name=sce_sys%2Fparam.sfo", 60);
                    var entries = ValidateNativePs5Sfo(sfo, title, o);
                    var keystone = Send(o.Host, o.Port, HttpMethod.Get, "/api/download_file?name=sce_sys%2Fkeystone", 60);
                    if (keystone.Length != 96)
                    {
                        throw new GarlicException(Loc.T("Resign.NativeNoKeystone"));
                    }

                    var nativeDir = SaveSfo.ReadString(sfo, entries, "SAVEDATA_DIRECTORY") ?? throw new GarlicException(Loc.T("Resign.NativeNoSlot"));
                    log("    [OK] " + Loc.F("Resign.NativeOk", title, nativeDir));
                    return new NativePs5Save(sfo, image, keystone, nativeDir);
                }
                catch (Exception ex) when (ex is GarlicException or SfoException)
                {
                    errors.Add(ex.Message);
                }
                finally
                {
                    GarlicUnmount(o.Host, o.Port);
                }
            }

            throw new GarlicException(Loc.F("Resign.NoNative", title, o.UserHex) + (errors.Count > 0 ? " " + errors[0] : string.Empty));
        }
        finally
        {
            TryDelete(temp);
        }
    }

    private static ulong ImageBlocks(string image, byte[]? sourceSfo = null)
    {
        Span<byte> header = stackalloc byte[64];
        if (!ReadAt(image, 0, header))
        {
            throw new GarlicException(Loc.T("Resign.NoPfsHeader"));
        }

        var flags = BinaryPrimitives.ReadUInt32LittleEndian(header[32..]);
        var blocks = BinaryPrimitives.ReadUInt64LittleEndian(header[56..]);
        if ((flags & 0x18000) == 0x10000 && blocks > 0 && blocks <= ulong.MaxValue / 65536 && blocks * 65536 == (ulong)SizeOf(image))
        {
            return blocks;
        }

        if (sourceSfo != null)
        {
            var entries = SaveSfo.Parse(sourceSfo);
            var entry = SaveSfo.Find(entries, "SAVEDATA_BLOCKS");
            if (entry is { Length: >= 8 })
            {
                return BinaryPrimitives.ReadUInt64LittleEndian(sourceSfo.AsSpan(entry.DataOffset));
            }
        }

        throw new GarlicException(Loc.T("Resign.UnknownBlocks"));
    }

    private static byte[] NativeSfoForSlot(NativePs5Save native, string title, string dir, ulong imageBlocks, ResignOptions o)
    {
        var sfo = (byte[])native.Sfo.Clone();
        var entries = ValidateNativePs5Sfo(sfo, title, o);
        if (string.IsNullOrWhiteSpace(dir) || dir.Contains('/') || dir.Contains('\\') || dir is "." or ".." || imageBlocks == 0)
        {
            throw new GarlicException(Loc.T("Resign.BadSlot"));
        }

        SaveSfo.PatchString(sfo, entries, "SAVEDATA_DIRECTORY", dir);
        // Giữ nguyên PARAMS, revision, lineage và mọi trường native chưa biết.
        BinaryPrimitives.WriteUInt64LittleEndian(sfo.AsSpan(SaveSfo.Find(entries, "SAVEDATA_BLOCKS")!.DataOffset), imageBlocks);
        ValidateNativePs5Sfo(sfo, title, o);
        return sfo;
    }

    private static void GarlicDecryptResignPs5(ResignOptions o, string path, string outZip, Action<string> log)
    {
        log("    [*] " + Loc.F("Resign.GarlicDecrypt", Path.GetFileName(path), SizeOf(path).ToString("N0")));
        var watch = Stopwatch.StartNew();
        var temp = NewTempDirectory("http_ps5_");
        try
        {
            MountPs5Copy(o, path, log);
            var sourceSfo = Send(o.Host, o.Port, HttpMethod.Get, "/api/download_file?name=sce_sys%2Fparam.sfo", 60);
            var blob = Send(o.Host, o.Port, HttpMethod.Get, "/api/download", 600);
            GarlicUnmount(o.Host, o.Port);
            string? sourceTitle = null;
            try
            {
                sourceTitle = SaveSfo.ReadString(sourceSfo, SaveSfo.Parse(sourceSfo), "TITLE_ID");
            }
            catch (SfoException)
            {
            }

            var title = Ps5TitleFromPath(path, o.TitleId) ?? sourceTitle ?? throw new GarlicException(Loc.T("Resign.NeedTitle"));
            var dir = Ps5DirectoryFromImage(path);
            var native = ReadNativePs5Save(o, title, dir, log);
            var sfo = NativeSfoForSlot(native, title, dir, ImageBlocks(path), o);
            using var input = new MemoryStream(blob);
            using var archive = new ZipArchive(input, ZipArchiveMode.Read);
            if (!archive.Entries.Any(e => e.FullName == "sce_sys/param.sfo"))
            {
                throw new GarlicException(Loc.T("Resign.ZipNoSfo"));
            }

            var keyEntry = archive.GetEntry("sce_sys/keystone") ?? throw new GarlicException(Loc.T("Resign.NoKeystone"));
            using (var keyStream = keyEntry.Open())
            using (var key = new MemoryStream())
            {
                keyStream.CopyTo(key);
                if (!key.ToArray().SequenceEqual(native.Keystone))
                {
                    throw new GarlicException(Loc.T("Resign.KeystoneMismatch"));
                }
            }

            var stagedZip = Path.Combine(temp, "save.zip");
            using (var output = ZipFile.Open(stagedZip, ZipArchiveMode.Create))
            {
                foreach (var entry in archive.Entries)
                {
                    var name = entry.FullName.Replace('\\', '/');
                    if (name.StartsWith('/') || name.Split('/').Any(p => p is ".." or "."))
                    {
                        throw new GarlicException(Loc.T("Resign.ZipBadPath"));
                    }

                    var target = output.CreateEntry("save/" + name, CompressionLevel.Optimal);
                    using var dst = target.Open();
                    if (name == "sce_sys/param.sfo")
                    {
                        dst.Write(sfo);
                    }
                    else
                    {
                        using var src = entry.Open();
                        src.CopyTo(dst);
                    }
                }
            }

            File.Copy(stagedZip, outZip, true);
            log("    [OK] " + Loc.F("Resign.GarlicDecryptDone", watch.Elapsed.TotalSeconds.ToString("F1"), outZip));
        }
        finally
        {
            GarlicUnmount(o.Host, o.Port);
            TryDelete(temp);
        }
    }

    /// <summary>Danh sách người dùng trên PS5 (cả người chưa có save) qua garlic-savemgr.</summary>
    public static List<Ps5User> ListUsers(string host)
    {
        var node = Garlic(host, GarlicPort, "/api/users", 8);
        var raw = node is JsonObject o ? o["users"] as JsonArray : node as JsonArray;
        var users = new List<Ps5User>();
        foreach (var item in raw ?? new JsonArray())
        {
            if (item is not JsonObject user)
            {
                continue;
            }

            var idNode = user["id"] ?? user["uid"] ?? user["user_id"];
            var id = idNode is JsonValue v && v.TryGetValue<long>(out var number) ? ((uint)number).ToString("x8") : NormalizeHex(idNode?.ToString(), 4);
            if (id == null)
            {
                continue;
            }

            var name = (user["name"] ?? user["username"])?.ToString().Trim();
            users.Add(new Ps5User(id, string.IsNullOrEmpty(name) ? "User" : name));
        }

        if (users.Count == 0)
        {
            throw new GarlicException(Loc.T("Resign.NoUsers"));
        }

        return users;
    }

    /// <summary>Mọi save trên PS5 do garlic-savemgr liệt kê.</summary>
    public static List<GarlicSave> ListSaves(string host)
    {
        var rows = Rows(Garlic(host, GarlicPort, "/api/saves", 15), "saves");
        return rows.Select((row, i) => row is JsonObject o
                ? new GarlicSave(i, Text(o, "uid"), Text(o, "title_id"), Text(o, "save_name"), Text(o, "type"), Truthy(o["backup"]))
                : null)
            .OfType<GarlicSave>()
            .ToList();
    }

    private static Dictionary<string, byte[]> MountedPs5Files(ResignOptions o)
    {
        var blob = Send(o.Host, o.Port, HttpMethod.Get, "/api/download", 600);
        using var memory = new MemoryStream(blob);
        using var zip = new ZipArchive(memory, ZipArchiveMode.Read);
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var entry in zip.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            if (name.EndsWith('/'))
            {
                continue;
            }

            if (name.StartsWith('/') || name.Split('/').Any(p => p is ".." or ".") || files.ContainsKey(name))
            {
                throw new GarlicException(Loc.T("Resign.ZipDup"));
            }

            using var input = entry.Open();
            using var data = new MemoryStream();
            input.CopyTo(data);
            files.Add(name, data.ToArray());
        }

        return files;
    }

    private static void VerifyPs5Files(ResignOptions o, Dictionary<string, byte[]> expected)
    {
        var actual = MountedPs5Files(o);
        if (actual.Count != expected.Count || expected.Any(x => !actual.TryGetValue(x.Key, out var data) || !data.SequenceEqual(x.Value)))
        {
            throw new GarlicException(Loc.T("Resign.VerifyMismatch"));
        }
    }

    private static void VerifyImageGeometry(string original, string rebuilt)
    {
        Span<byte> before = stackalloc byte[64];
        Span<byte> after = stackalloc byte[64];
        if (!IsPs5Save(rebuilt) || !ReadAt(original, 0, before) || !ReadAt(rebuilt, 0, after) || !before[32..36].SequenceEqual(after[32..36]) ||
            !before[56..64].SequenceEqual(after[56..64]) || ((BinaryPrimitives.ReadUInt32LittleEndian(before[32..]) & 0x18000) == 0x10000 && SizeOf(original) != SizeOf(rebuilt)))
        {
            throw new GarlicException(Loc.T("Resign.Geometry"));
        }
    }

    /// <summary>Dựng rồi mở lại một ảnh PS5 tạm. Không cài hay đổi cơ sở dữ liệu save của máy.</summary>
    public static byte[] GarlicRebuildPs5Save(string contentDir, string? sourceImage, ResignOptions options, string outImage, Action<string> log)
    {
        var o = options with { Port = GarlicPort };
        if (Text(Garlic(o.Host, o.Port, "/api/status", 30), "ps5param_version") != "1")
        {
            throw new GarlicException(Loc.T("Resign.OldGarlic"));
        }

        var sfoPath = Path.Combine(contentDir, "sce_sys", "param.sfo");
        if (!File.Exists(sfoPath))
        {
            throw new GarlicException(Loc.T("Resign.FolderNoSfo"));
        }

        var inputSfo = File.ReadAllBytes(sfoPath);
        var entries = SaveSfo.Parse(inputSfo);
        var title = SaveSfo.ReadString(inputSfo, entries, "TITLE_ID") ?? throw new GarlicException(Loc.T("Resign.NoTitleId"));
        var dir = SaveSfo.ReadString(inputSfo, entries, "SAVEDATA_DIRECTORY") ?? throw new GarlicException(Loc.T("Resign.NoSlot"));
        var files = SaveFiles.Walk(contentDir).Where(f => IsUploadable(contentDir, f))
            .ToDictionary(f => Path.GetRelativePath(contentDir, f).Replace('\\', '/'), File.ReadAllBytes, StringComparer.Ordinal);
        if (!files.TryGetValue("sce_sys/keystone", out var keystone))
        {
            throw new GarlicException(Loc.T("Resign.NoKeystone"));
        }

        var temp = NewTempDirectory("rebuild_ps5_");
        try
        {
            var native = ReadNativePs5Save(o, title, dir, log);
            if (!keystone.SequenceEqual(native.Keystone))
            {
                throw new GarlicException(Loc.T("Resign.KeystoneMismatch"));
            }

            var image = sourceImage;
            if (image == null)
            {
                image = Path.Combine(temp, "native_image");
                File.WriteAllBytes(image, native.Image);
            }

            if (!IsPs5Save(image))
            {
                throw new GarlicException(Loc.T("Resign.NotPs5Image"));
            }

            var blocks = ImageBlocks(image, sourceImage == null ? native.Sfo : null);
            var sfo = NativeSfoForSlot(native, title, dir, blocks, o);
            files["sce_sys/param.sfo"] = sfo;
            MountPs5Copy(o, image, log);
            foreach (var extra in MountedPs5Files(o).Keys.Except(files.Keys, StringComparer.Ordinal))
            {
                Garlic(o.Host, o.Port, "/api/delete_file?name=" + Q(extra), 30);
            }

            foreach (var file in files)
            {
                var upload = Path.Combine(temp, "upload");
                File.WriteAllBytes(upload, file.Value);
                var result = GarlicUpload(o.Host, o.Port, "/api/upload_file?name=" + Q(file.Key), upload, 300);
                if (!Truthy((result as JsonObject)?["ok"]) || Text(result, "size") != file.Value.Length.ToString())
                {
                    throw new GarlicException(Loc.F("Resign.UploadShort", file.Key));
                }
            }

            VerifyPs5Files(o, files);
            var rebuilt = Send(o.Host, o.Port, HttpMethod.Get, "/api/download_new?name=" + Q(dir), 600);
            var staged = Path.Combine(temp, "rebuilt_image");
            File.WriteAllBytes(staged, rebuilt);
            VerifyImageGeometry(image, staged);
            MountPs5Copy(o, staged, log);
            VerifyPs5Files(o, files);
            ValidateNativePs5Sfo(MountedPs5Files(o)["sce_sys/param.sfo"], title, o);
            if (!Truthy((Garlic(o.Host, o.Port, "/api/ps5param_check", 30) as JsonObject)?["ok"]))
            {
                throw new GarlicException(Loc.T("Resign.Ps5ParamMismatch"));
            }

            GarlicUnmount(o.Host, o.Port);
            File.Copy(staged, outImage, true);
            log("    [OK] " + Loc.F("Resign.ImageOk", title, dir, blocks));
            return sfo;
        }
        finally
        {
            GarlicUnmount(o.Host, o.Port);
            TryDelete(temp);
        }
    }

    private static bool BackupConsoleSlot(ResignOptions o, string title, string dir, Action<string> log)
    {
        var rows = Rows(Garlic(o.Host, o.Port, "/api/saves", 30), "saves");
        var index = rows.FindIndex(r => Text(r, "uid").Equals(o.UserHex, StringComparison.OrdinalIgnoreCase) && Text(r, "title_id") == title && Text(r, "save_name") == "sdimg_" + dir);
        if (index < 0)
        {
            return false;
        }

        var image = Send(o.Host, o.Port, HttpMethod.Get, "/api/download_raw?idx=" + index, 300);
        var backupRoot = Environment.GetEnvironmentVariable("PSVIETHOA_SAVE_BACKUP_ROOT") ?? BackupRoot;
        var backupDir = Path.Combine(backupRoot, o.UserHex, title, DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(backupDir);
        var backup = Path.Combine(backupDir, "sdimg_" + dir);
        File.WriteAllBytes(backup, image);
        if (!IsPs5Save(backup))
        {
            throw new GarlicException(Loc.T("Resign.BackupFailed"));
        }

        File.WriteAllText(Path.Combine(backupDir, "metadata.json"), new JsonObject
        {
            ["user_id"] = o.UserHex,
            ["title_id"] = title,
            ["directory"] = dir,
            ["sha256"] = Convert.ToHexString(SHA256.HashData(image)).ToLowerInvariant(),
            ["save_entry"] = rows[index]?.DeepClone(),
        }.ToJsonString());
        log("    [OK] " + Loc.F("Resign.BackupDone", backup));
        return true;
    }

    /// <summary>Nhập save PS5 đã giải mã vào máy: dựng ảnh, sao lưu slot cũ, nhập, đọc lại và so từng tệp.</summary>
    public static JsonNode? GarlicImportDecrypted(string contentDir, ResignOptions options, Action<string> log, string? sourceImage = null)
    {
        var o = options with { Port = GarlicPort };
        var temp = NewTempDirectory("import_ps5_");
        try
        {
            var image = Path.Combine(temp, "prepared_image");
            var sfo = GarlicRebuildPs5Save(contentDir, sourceImage, o, image, log);
            var entries = SaveSfo.Parse(sfo);
            var title = SaveSfo.ReadString(sfo, entries, "TITLE_ID")!;
            var dir = SaveSfo.ReadString(sfo, entries, "SAVEDATA_DIRECTORY")!;
            var existed = BackupConsoleSlot(o, title, dir, log);
            MountPs5Copy(o, image, log);
            Garlic(o.Host, o.Port, "/api/import_check?uid=" + Q(o.UserHex), 30);
            var result = Garlic(o.Host, o.Port, "/api/import_finish?uid=" + Q(o.UserHex), 300);
            if (!Truthy((result as JsonObject)?["ok"]) || !string.IsNullOrEmpty(Text(result, "warning")))
            {
                throw new GarlicException(Loc.F("Resign.ImportNotConfirmed", result?.ToJsonString()));
            }

            var rows = Rows(Garlic(o.Host, o.Port, "/api/saves", 30), "saves");
            var index = rows.FindIndex(r => Text(r, "uid").Equals(o.UserHex, StringComparison.OrdinalIgnoreCase) && Text(r, "title_id") == title && Text(r, "save_name") == "sdimg_" + dir);
            if (index < 0)
            {
                throw new GarlicException(Loc.T("Resign.ImportNotFound"));
            }

            var installed = Path.Combine(temp, "installed_copy");
            File.WriteAllBytes(installed, Send(o.Host, o.Port, HttpMethod.Get, "/api/download_raw?idx=" + index, 300));
            VerifyImageGeometry(image, installed);
            MountPs5Copy(o, installed, log);
            var expected = SaveFiles.Walk(contentDir).Where(f => IsUploadable(contentDir, f))
                .ToDictionary(f => Path.GetRelativePath(contentDir, f).Replace('\\', '/'), File.ReadAllBytes, StringComparer.Ordinal);
            expected["sce_sys/param.sfo"] = sfo;
            VerifyPs5Files(o, expected);
            if (!Truthy((Garlic(o.Host, o.Port, "/api/ps5param_check", 30) as JsonObject)?["ok"]))
            {
                throw new GarlicException(Loc.T("Resign.InstalledParamMismatch"));
            }

            if (result is JsonObject r)
            {
                r["exists"] = existed;
            }

            log("    [OK] " + Loc.F("Resign.ImportDone", title, dir));
            return result;
        }
        finally
        {
            GarlicUnmount(o.Host, o.Port);
            TryDelete(temp);
        }
    }

    /// <summary>Tải save đã giải mã của một người dùng PS5 khác qua Garlic rồi nhập / resign sang người dùng đích.</summary>
    public static JsonNode? GarlicTransferSave(GarlicSave source, ResignOptions target, Action<string> log)
    {
        var host = target.Host;
        const int port = GarlicPort;
        var sourceUid = NormalizeHex(source.Uid, 4);
        if (sourceUid == null || source.TitleId.Length == 0 || source.SaveName.Length == 0)
        {
            throw new GarlicException(Loc.T("Resign.TransferMissing"));
        }

        var rows = Rows(Garlic(host, port, "/api/saves", 30), "saves");
        var index = rows.FindIndex(r => r is JsonObject && Text(r, "uid").Equals(sourceUid, StringComparison.OrdinalIgnoreCase) &&
                                        Text(r, "title_id").Equals(source.TitleId, StringComparison.OrdinalIgnoreCase) && Text(r, "save_name") == source.SaveName);
        if (index < 0)
        {
            throw new GarlicException(Loc.F("Resign.TransferNotFound", source.TitleId, source.SaveName, sourceUid));
        }

        log("[*] " + Loc.F("Resign.TransferStart", source.TitleId, source.SaveName, sourceUid));
        GarlicUnmount(host, port);
        Garlic(host, port, "/api/mount?idx=" + index, 60);
        byte[] blob;
        try
        {
            blob = Send(host, port, HttpMethod.Get, "/api/download", 300);
        }
        finally
        {
            GarlicUnmount(host, port);
        }

        var temp = NewTempDirectory("transfer_");
        try
        {
            var zip = Path.Combine(temp, "source.zip");
            File.WriteAllBytes(zip, blob);
            var extract = Directory.CreateDirectory(Path.Combine(temp, "save")).FullName;
            if (!SafeExtractZip(zip, extract))
            {
                throw new GarlicException(Loc.T("Resign.ZipBroken"));
            }

            var sfo = SaveFiles.Walk(extract).FirstOrDefault(f => Path.GetFileName(f).Equals("param.sfo", StringComparison.OrdinalIgnoreCase))
                      ?? throw new GarlicException(Loc.T("Resign.SourceNoSfo"));
            var result = GarlicImportDecrypted(Path.GetDirectoryName(Path.GetDirectoryName(sfo))!, target, log);
            log("[OK] " + Loc.F("Resign.TransferDone", source.TitleId, source.SaveName, sourceUid, target.UserHex));
            return result;
        }
        finally
        {
            TryDelete(temp);
        }
    }

    private sealed record Installer(bool Garlic, int Port);

    private static Installer ResolveInstaller(ResignOptions o, Action<string> log)
    {
        if (o.Port != GarlicPort)
        {
            // Đã chọn cổng riêng cho daemon: không ping trước (mỗi lần ping tốn một yêu cầu của daemon).
            log("    [*] " + Loc.F("Resign.UseDaemon", o.Host, o.Port));
            return new Installer(false, o.Port);
        }

        if (PingGarlic(o.Host, GarlicPort, 4).Ok)
        {
            log("    [OK] " + Loc.F("Resign.UseGarlic", o.Host, GarlicPort));
            return new Installer(true, GarlicPort);
        }

        try
        {
            var (ok, version) = PingDaemon(o.Host, o.Port, 3);
            if (ok)
            {
                log("    [OK] " + Loc.F("Resign.UseDaemonVersion", o.Host, o.Port, version));
                return new Installer(false, o.Port);
            }
        }
        catch (Exception)
        {
            // Không có daemon: báo lỗi chung bên dưới.
        }

        throw new GarlicException(Loc.F("Resign.NoService", GarlicPort, o.Port));
    }

    private static void InstallPs5Content(string contentDir, ResignOptions o, Action<string> log, Installer? installer = null, string? sourceImage = null)
    {
        installer ??= ResolveInstaller(o, log);
        if (installer.Garlic)
        {
            GarlicImportDecrypted(contentDir, o, log, sourceImage);
        }
        else
        {
            SendInstall(o with { Port = installer.Port }, contentDir, log);
        }
    }

    private static void GarlicResignPs4(string image, string key, ResignOptions o, Action<string> log, string outDir)
    {
        var port = ResolveGarlic(o);
        log("    [*] " + Loc.F("Resign.Ps4Encrypted", Path.GetFileName(image), Path.GetFileName(key)));
        GarlicUnmount(o.Host, port);
        GarlicUpload(o.Host, port, "/api/upload_key", key, 30);
        if (o.Install)
        {
            if (o.UserId == 0)
            {
                throw new GarlicException(Loc.T("Resign.NeedUserPs4"));
            }

            var info = GarlicUpload(o.Host, port, $"/api/import_encrypted?uid={Q(o.UserHex)}&ps4=1", image, 300, log);
            if (info is JsonObject i)
            {
                log($"    [*] {Text(i, "title_id")} / {Text(i, "dir_name")}  AID {Text(i, "save_aid")} → {Text(i, "user_aid")}");
                if (i["exists"] is JsonValue e && e.TryGetValue<bool>(out var exists) && !exists)
                {
                    log("    [*] " + Loc.T("Resign.Ps4NewEntry"));
                }
            }

            var finish = Garlic(o.Host, port, $"/api/import_finish?uid={Q(o.UserHex)}", 180);
            log("    [OK] " + Loc.F("Resign.Ps4Installed", Text(finish, "title_id"), Text(finish, "dir_name")));
            return;
        }

        GarlicUpload(o.Host, port, $"/api/resign?aid={Q(o.AccountHex)}&ps4=1", image, 300, log);
        var stem = Path.GetFileNameWithoutExtension(image) + "_resigned";
        var data = Send(o.Host, port, HttpMethod.Get, $"/api/resign_download?name={Q(stem)}&ps4=1", 180);
        var output = Path.Combine(outDir, stem + ".zip");
        File.WriteAllBytes(output, data);
        log($"    [OK] {output} ({data.Length:N0} bytes)");
    }

    private static string ExtractDaemonZip(string zip, string destination, string folderName)
    {
        SafeExtractZip(zip, destination);
        var source = Path.Combine(destination, "save");
        var target = Path.Combine(destination, folderName);
        if (Directory.Exists(source) && !Directory.Exists(target))
        {
            Directory.Move(source, target);
        }

        return target;
    }

    private static bool PatchSfo(string sfo, ResignOptions o, string? titleId, bool backup, Action<string> log) =>
        SaveSfo.ProcessPath(sfo, o.AccountId, titleId, backup, log);

    private static bool IsPs5PlainSfo(string sfo)
    {
        try
        {
            var buf = File.ReadAllBytes(sfo);
            return SaveSfo.IsPs5Format(buf, SaveSfo.Parse(buf));
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Nhận ra PS4 / PS5 (mã hoá hay đã giải mã) và resign một mục của hàng đợi.</summary>
    public static ResignCount ProcessAny(string path, ResignOptions o, Action<string> log)
    {
        if (File.Exists(path) && IsPs4SealedKey(path))
        {
            log("    [*] " + Loc.F("Resign.SkipKeyFile", Path.GetFileName(path)));
            return new ResignCount(0, 0);
        }

        if (!File.Exists(path) && !Directory.Exists(path))
        {
            log("[!] " + Loc.F("Resign.NotExists", path));
            return new ResignCount(0, 1);
        }

        string? temp = null;
        try
        {
            var scan = path;
            if (File.Exists(path) && path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                temp = NewTempDirectory("any_");
                if (!SafeExtractZip(path, temp))
                {
                    log("[!] " + Loc.T("Resign.ZipUnreadable"));
                    return new ResignCount(0, 1);
                }

                scan = temp;
            }

            var jobs = DiscoverJobs(scan);
            if (jobs.Count == 0)
            {
                log("[!] " + Loc.F("Resign.NotRecognized", path));
                return new ResignCount(0, 1);
            }

            var hasPs4 = jobs.Any(j => j.Kind == SaveKind.Ps4Encrypted);
            var hasPs5 = jobs.Any(j => j.Kind == SaveKind.Ps5Encrypted);
            if (!hasPs4 && !hasPs5)
            {
                // Save đã giải mã: sửa ngay trên máy tính. Save PS5 đã giải mã đi đường PS5 khi bật "chép vào máy".
                var ps5Plain = o.Install && jobs.Any(j => IsPs5PlainSfo(j.Path));
                TryDelete(temp);
                temp = null;
                return ps5Plain ? Ps5ProcessPath(path, o, log) : Ps4ProcessPath(path, o, log);
            }

            if (!hasPs4)
            {
                TryDelete(temp);
                temp = null;
                return Ps5ProcessPath(path, o, log);
            }

            int ok = 0;
            int fail = 0;
            var outDir = Directory.Exists(path) ? path : Path.GetDirectoryName(path) ?? ".";
            foreach (var job in jobs)
            {
                try
                {
                    switch (job.Kind)
                    {
                        case SaveKind.Ps4Encrypted:
                            GarlicResignPs4(job.Path, job.Key ?? throw new IOException(Loc.T("Resign.MissingBin")), o, log, outDir);
                            ok++;
                            break;
                        case SaveKind.Ps5Encrypted:
                            var r = Ps5ProcessPath(job.Path, o, log);
                            ok += r.Ok;
                            fail += r.Fail;
                            break;
                        default:
                            if (PatchSfo(job.Path, o, o.TitleId, true, log))
                            {
                                ok++;
                            }
                            else
                            {
                                fail++;
                            }

                            break;
                    }
                }
                catch (Exception ex)
                {
                    fail++;
                    log("[!] " + ex.Message);
                }
            }

            return new ResignCount(ok, fail);
        }
        finally
        {
            TryDelete(temp);
        }
    }

    private static ResignCount Ps5ProcessPath(string path, ResignOptions o, Action<string> log)
    {
        int ok = 0;
        int fail = 0;
        if (File.Exists(path) && path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            log("[*] " + Loc.F("Resign.Unzipping", Path.GetFileName(path)));
            var temp = NewTempDirectory("ps5z_");
            try
            {
                if (!SafeExtractZip(path, temp))
                {
                    log("[!] " + Loc.F("Resign.ZipUnreadablePath", path));
                    return new ResignCount(0, 1);
                }

                var saves = new List<string>();
                var sfos = new List<string>();
                var others = new List<string>();
                foreach (var file in SaveFiles.Walk(temp))
                {
                    (IsPs5Save(file) ? saves : Path.GetFileName(file).Equals("param.sfo", StringComparison.OrdinalIgnoreCase) ? sfos : others).Add(file);
                }

                if (saves.Count == 0 && sfos.Count == 0)
                {
                    log("[!] " + Loc.T("Resign.ZipNoPs5"));
                    return new ResignCount(0, 1);
                }

                var outTree = NewTempDirectory("ps5o_");
                var installRoots = new List<(string Root, string? Image)>();
                try
                {
                    string Mirror(string file)
                    {
                        var dst = Path.Combine(outTree, Path.GetRelativePath(temp, file));
                        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                        return dst;
                    }

                    foreach (var file in others)
                    {
                        File.Copy(file, Mirror(file), true);
                    }

                    foreach (var file in sfos)
                    {
                        var dst = Mirror(file);
                        File.Copy(file, dst, true);
                        if (PatchSfo(dst, o, null, false, log))
                        {
                            ok++;
                            installRoots.Add((Path.GetDirectoryName(Path.GetDirectoryName(dst))!, null));
                        }
                        else
                        {
                            fail++;
                        }
                    }

                    foreach (var file in saves)
                    {
                        if (BackupHasPrimary(file))
                        {
                            log("    [*] " + Loc.F("Resign.SkipBackup", Path.GetFileName(file)));
                            continue;
                        }

                        var tempZip = Path.Combine(Path.GetTempPath(), $"psviethoa_dec_{Environment.CurrentManagedThreadId}_{Path.GetFileName(file)}.zip");
                        try
                        {
                            DecryptResignPs5(o, file, tempZip, log);
                            var dest = Path.GetDirectoryName(Mirror(file))!;
                            installRoots.Add((ExtractDaemonZip(tempZip, dest, Path.GetFileNameWithoutExtension(file)), file));
                            ok++;
                        }
                        catch (Exception ex)
                        {
                            fail++;
                            log($"[!] {Path.GetRelativePath(temp, file)}: {ex.Message}");
                        }
                        finally
                        {
                            TryDelete(tempZip);
                        }
                    }

                    if (o.Install && installRoots.Count > 0)
                    {
                        var installer = ResolveInstaller(o, log);
                        foreach (var root in installRoots)
                        {
                            try
                            {
                                InstallPs5Content(root.Root, o, log, installer, root.Image);
                            }
                            catch (Exception ex)
                            {
                                fail++;
                                log("[!] " + Loc.F("Resign.InstallFailed", Path.GetFileName(root.Root), ex.Message));
                            }
                        }
                    }

                    var outZip = Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + "_dec_resigned.zip");
                    ZipDirectory(outTree, outZip, CompressionLevel.Optimal);
                    log("[OK] " + Loc.F("Resign.ZipDone", outZip));
                }
                finally
                {
                    TryDelete(outTree);
                }
            }
            finally
            {
                TryDelete(temp);
            }

            return new ResignCount(ok, fail);
        }

        if (File.Exists(path) && IsPs5Save(path))
        {
            try
            {
                DecryptOne(path, o, log);
                ok++;
            }
            catch (Exception ex)
            {
                fail++;
                log($"[!] {Path.GetFileName(path)}: {ex.Message}");
            }

            return new ResignCount(ok, fail);
        }

        if (File.Exists(path) && Path.GetFileName(path).Equals("param.sfo", StringComparison.OrdinalIgnoreCase))
        {
            if (!PatchSfo(path, o, null, true, log))
            {
                return new ResignCount(0, 1);
            }

            if (!o.Install)
            {
                return new ResignCount(1, 0);
            }

            try
            {
                InstallPs5Content(Path.GetDirectoryName(Path.GetDirectoryName(path))!, o, log);
                return new ResignCount(1, 0);
            }
            catch (Exception ex)
            {
                log("[!] " + Loc.F("Resign.InstallError", ex.Message));
                return new ResignCount(0, 1);
            }
        }

        if (File.Exists(path))
        {
            log("[!] " + Loc.F("Resign.SkipNotPs5", Path.GetFileName(path)));
            return new ResignCount(0, 1);
        }

        if (!Directory.Exists(path))
        {
            log("[!] " + Loc.F("Resign.NotExists", path));
            return new ResignCount(0, 1);
        }

        // Liệt kê trước để thư mục *_dec vừa tạo không bị xử lý lại.
        var targets = DiscoverJobs(path).Where(j => j.Kind is SaveKind.Ps5Encrypted or SaveKind.ParamSfo).Select(j => j.Path).ToList();
        if (targets.Count == 0)
        {
            log("[!] " + Loc.F("Resign.NoPs5In", path));
            return new ResignCount(0, 1);
        }

        foreach (var file in targets)
        {
            try
            {
                if (IsPs5Save(file))
                {
                    DecryptOne(file, o, log);
                    ok++;
                    continue;
                }

                if (!PatchSfo(file, o, null, true, log))
                {
                    fail++;
                    continue;
                }

                if (o.Install)
                {
                    InstallPs5Content(Path.GetDirectoryName(Path.GetDirectoryName(file))!, o, log);
                }

                ok++;
            }
            catch (Exception ex)
            {
                fail++;
                log($"[!] {Path.GetFileName(file)}: {ex.Message}");
            }
        }

        return new ResignCount(ok, fail);
    }

    private static void DecryptOne(string file, ResignOptions o, Action<string> log)
    {
        var stem = Path.Combine(Path.GetDirectoryName(file)!, Path.GetFileNameWithoutExtension(file));
        var outZip = stem + "_dec_resigned.zip";
        DecryptResignPs5(o, file, outZip, log);
        var decDir = Directory.CreateDirectory(stem + "_dec").FullName;
        var root = ExtractDaemonZip(outZip, decDir, Path.GetFileNameWithoutExtension(file));
        log("    [OK] " + Loc.F("Resign.DecryptedTo", decDir));
        if (o.Install)
        {
            InstallPs5Content(root, o, log, sourceImage: file);
        }
    }

    private static ResignCount Ps4ProcessPath(string path, ResignOptions o, Action<string> log)
    {
        int ok = 0;
        int fail = 0;
        if (File.Exists(path) && path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            log("[*] " + Loc.F("Resign.Unzipping", Path.GetFileName(path)));
            var temp = NewTempDirectory("ps4z_");
            try
            {
                if (!SafeExtractZip(path, temp))
                {
                    log("[!] " + Loc.F("Resign.ZipUnreadablePath", path));
                    return new ResignCount(0, 1);
                }

                var sfos = SaveFiles.Walk(temp).Where(f => Path.GetFileName(f).Equals("param.sfo", StringComparison.OrdinalIgnoreCase)).ToList();
                if (sfos.Count == 0)
                {
                    log("[!] " + Loc.T("Resign.ZipNoSfoAny"));
                    return new ResignCount(0, 1);
                }

                foreach (var sfo in sfos)
                {
                    if (PatchSfo(sfo, o, o.TitleId, false, log))
                    {
                        ok++;
                    }
                    else
                    {
                        fail++;
                    }
                }

                var outZip = Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + "_resigned.zip");
                ZipDirectory(temp, outZip, CompressionLevel.Optimal);
                log("[OK] " + Loc.F("Resign.ZipDone", outZip));
            }
            finally
            {
                TryDelete(temp);
            }

            return new ResignCount(ok, fail);
        }

        if (File.Exists(path))
        {
            if (Path.GetFileName(path).Equals("param.sfo", StringComparison.OrdinalIgnoreCase))
            {
                return PatchSfo(path, o, o.TitleId, true, log) ? new ResignCount(1, 0) : new ResignCount(0, 1);
            }

            log("[!] " + Loc.F("Resign.SkipNotSfo", Path.GetFileName(path)));
            return new ResignCount(0, 1);
        }

        if (!Directory.Exists(path))
        {
            log("[!] " + Loc.F("Resign.NotExists", path));
            return new ResignCount(0, 1);
        }

        var list = SaveFiles.Walk(path).Where(f => Path.GetFileName(f).Equals("param.sfo", StringComparison.OrdinalIgnoreCase)).ToList();
        if (list.Count == 0)
        {
            log("[!] " + Loc.F("Resign.NoSfoIn", path));
            return new ResignCount(0, 1);
        }

        foreach (var sfo in list)
        {
            if (PatchSfo(sfo, o, o.TitleId, true, log))
            {
                ok++;
            }
            else
            {
                fail++;
            }
        }

        return new ResignCount(ok, fail);
    }
}
