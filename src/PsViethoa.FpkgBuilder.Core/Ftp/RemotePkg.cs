using System.Collections.Concurrent;
using System.Text.Json;
using PsViethoa.FpkgBuilder.Core.Localization;
using PsViethoa.FpkgBuilder.Core.Services;

namespace PsViethoa.FpkgBuilder.Core.Ftp;

/// <summary>
/// FPKG trên PS5: luồng chỉ đọc, tua được, trên MỘT tệp của PS5 qua FTP. Mỗi lần trượt bộ đệm = PASV + REST + RETR một khối
/// 1 MiB rồi cắt kênh dữ liệu (ftpsrv có REST STREAM). Giữ 64 khối gần nhất (LRU). Mỗi luồng một kết nối riêng, không dùng chung
/// giữa các thread. Đứt kết nối thì tự nối lại (tối đa 3 lần) — không ghi gì lên PS5.
/// </summary>
public sealed class FtpRangeStream : Stream
{
    public const int BlockSize = 1 << 20;
    private const int CacheBlocks = 64;

    private static long requests;
    private static long bytesDownloaded;

    private readonly FtpConnectionInfo info;
    private readonly string path;
    private readonly long length;
    private readonly CancellationToken token;
    private readonly Dictionary<long, byte[]> cache = new();
    private readonly LinkedList<long> lru = new();
    private Ps5Ftp? ftp;
    private long position;

    public FtpRangeStream(FtpConnectionInfo info, string path, long length, CancellationToken token = default)
    {
        this.info = info;
        this.path = path;
        this.length = length;
        this.token = token;
    }

    /// <summary>Tổng số lượt RETR đã gửi (mọi luồng) — để đo.</summary>
    public static long Requests => Interlocked.Read(ref requests);

    /// <summary>Tổng số byte đã tải qua mạng (mọi luồng) — để tính tốc độ mạng khi trích.</summary>
    public static long BytesDownloaded => Interlocked.Read(ref bytesDownloaded);

    public string RemotePath => path;

    public override bool CanRead => true;

    public override bool CanSeek => true;

    public override bool CanWrite => false;

    public override long Length => length;

    public override long Position
    {
        get => position;
        set => position = value;
    }

    private byte[] Block(long index)
    {
        if (cache.TryGetValue(index, out var hit))
        {
            lru.Remove(index);
            lru.AddFirst(index);
            return hit;
        }

        var offset = index * BlockSize;
        var count = (int)Math.Min(BlockSize, length - offset);
        var data = Fetch(offset, count);
        cache[index] = data;
        lru.AddFirst(index);
        if (lru.Count > CacheBlocks)
        {
            cache.Remove(lru.Last!.Value);
            lru.RemoveLast();
        }

        return data;
    }

    private byte[] Fetch(long offset, int count)
    {
        for (var attempt = 1; ; attempt++)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                ftp ??= Connect();
                var buffer = new MemoryStream(count);
                ftp.DownloadRangeAsync(path, buffer, offset, count, null, token).GetAwaiter().GetResult();
                Interlocked.Increment(ref requests);
                Interlocked.Add(ref bytesDownloaded, buffer.Length);
                return buffer.Length == count && buffer.GetBuffer().Length == count ? buffer.GetBuffer() : buffer.ToArray();
            }
            catch (Exception ex) when (attempt < 3 && !token.IsCancellationRequested && ex is IOException or FtpException or System.Net.Sockets.SocketException)
            {
                ftp?.Dispose();
                ftp = null;
                Thread.Sleep(300 * attempt);
            }
        }
    }

    private Ps5Ftp Connect()
    {
        var client = new Ps5Ftp();
        client.ConnectAsync(info.Host, info.Port, info.User, info.Pass, token).GetAwaiter().GetResult();
        return client;
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var total = 0;
        while (buffer.Length > 0 && position < length)
        {
            var block = Block(position / BlockSize);
            var inBlock = (int)(position % BlockSize);
            var n = Math.Min(buffer.Length, block.Length - inBlock);
            if (n <= 0)
            {
                break;
            }

            block.AsSpan(inBlock, n).CopyTo(buffer);
            buffer = buffer[n..];
            position += n;
            total += n;
        }

        return total;
    }

    public override long Seek(long offset, SeekOrigin origin) => position = origin switch
    {
        SeekOrigin.Begin => offset,
        SeekOrigin.Current => position + offset,
        _ => length + offset,
    };

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ftp?.Dispose();
            ftp = null;
            cache.Clear();
            lru.Clear();
        }

        base.Dispose(disposing);
    }
}

/// <summary>Một game PS5 đã cài trên máy có tệp gói app.pkg (FPKG) đọc được qua FTP.</summary>
public sealed class RemoteGame
{
    /// <summary>Mã nơi cài cho bộ nhớ trong (/user/app); các nơi khác là tên thư mục trong /mnt (ext1, usb0…).</summary>
    public const string InternalLocation = "internal";

    public required string TitleId { get; init; }

    /// <summary>Thư mục cài, vd "/mnt/ext1/user/app/PPSA10665".</summary>
    public required string Folder { get; init; }

    public required string PkgPath { get; init; }

    public long PkgSize { get; init; }

    /// <summary>Nơi cài: <see cref="InternalLocation"/>, "ext1", "usb0"…</summary>
    public required string Location { get; init; }

    public string Title { get; set; } = string.Empty;

    public string Version { get; set; } = string.Empty;

    public string ContentId { get; set; } = string.Empty;

    /// <summary>Tên tệp .pkg gốc lúc cài (app.json pieces[0].url).</summary>
    public string OriginalName { get; set; } = string.Empty;

    /// <summary>app.json có "fake": true → FPKG. false = gói retail (mã hoá, không trích được).</summary>
    public bool IsFake { get; set; } = true;

    /// <summary>Đường dẫn icon0.png trên PS5 (trống nếu không có).</summary>
    public string IconPath { get; set; } = string.Empty;

    public string DisplayTitle => Title.Length > 0 ? Title
        : OriginalName.Length > 0 ? Path.GetFileNameWithoutExtension(OriginalName)
        : TitleId;
}

/// <summary>
/// Dò các FPKG PS5 đã cài: /user/app, /mnt/ext*/user/app, /mnt/usb*/user/app → &lt;TID&gt;/app.pkg. Tên game lấy từ
/// /user/appmeta/&lt;TID&gt;/param.json. Gói PS4 (header \x7FCNT) bị bỏ qua ngay, cùng app hệ thống NPXS và app.pkg &lt; 64 KB.
/// Chỉ đọc (LIST / SIZE / RETR), không ghi gì lên PS5.
/// </summary>
public static class RemotePkgScanner
{
    private const int MinPackageSize = 64 * 1024;

    public static async Task<List<RemoteGame>> ScanAsync(FtpConnectionInfo info, IProgress<string>? progress, CancellationToken ct, int workers = 4)
    {
        using var ftp = new Ps5Ftp();
        progress?.Report(Loc.F("Remote.Scan.Connecting", info.Host, info.Port));
        await ftp.ConnectAsync(info.Host, info.Port, info.User, info.Pass, ct).ConfigureAwait(false);
        if (!ftp.SupportsRest)
        {
            progress?.Report(Loc.T("Remote.Scan.NoRest"));
        }

        var roots = new List<(string Path, string Location)> { ("/user/app", RemoteGame.InternalLocation) };
        try
        {
            foreach (var mount in await ftp.ListAsync("/mnt", ct).ConfigureAwait(false))
            {
                if (mount.Kind == FtpEntryKind.File)
                {
                    continue;
                }

                if (mount.Name.StartsWith("ext", StringComparison.OrdinalIgnoreCase) || mount.Name.StartsWith("usb", StringComparison.OrdinalIgnoreCase))
                {
                    roots.Add(($"/mnt/{mount.Name}/user/app", mount.Name));
                }
            }
        }
        catch (FtpException)
        {
        }

        var candidates = new List<(string Folder, string Tid, string Location)>();
        foreach (var (root, location) in roots)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(Loc.F("Remote.Scan.Listing", root));
            IReadOnlyList<FtpEntry> dirs;
            try
            {
                dirs = await ftp.ListAsync(root, ct).ConfigureAwait(false);
            }
            catch (FtpException)
            {
                continue;
            }

            foreach (var dir in dirs)
            {
                if (dir.Kind != FtpEntryKind.File && LooksLikeTitleId(dir.Name))
                {
                    candidates.Add(($"{root}/{dir.Name}", dir.Name.ToUpperInvariant(), location));
                }
            }
        }

        progress?.Report(Loc.F("Remote.Scan.Checking", candidates.Count));

        var found = new ConcurrentBag<RemoteGame>();
        var queue = new ConcurrentQueue<(string Folder, string Tid, string Location)>(candidates);
        var done = 0;

        async Task Worker()
        {
            using var client = new Ps5Ftp();
            await client.ConnectAsync(info.Host, info.Port, info.User, info.Pass, ct).ConfigureAwait(false);
            while (queue.TryDequeue(out var item))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var game = await ProbeAsync(client, item.Folder, item.Tid, item.Location, ct).ConfigureAwait(false);
                    if (game != null)
                    {
                        found.Add(game);
                    }
                }
                catch (Exception ex) when (ex is FtpException or IOException or JsonException or FormatException)
                {
                    if (!client.Connected)
                    {
                        await client.ConnectAsync(info.Host, info.Port, info.User, info.Pass, ct).ConfigureAwait(false);
                    }
                }

                var n = Interlocked.Increment(ref done);
                progress?.Report(Loc.F("Remote.Scan.Progress", n, candidates.Count, found.Count));
            }
        }

        var count = Math.Clamp(workers, 1, 8);
        await Task.WhenAll(Enumerable.Range(0, Math.Max(1, Math.Min(count, candidates.Count))).Select(_ => Task.Run(Worker, ct))).ConfigureAwait(false);

        // Cùng một TID cài ở 2 nơi (vd cả ext0 và ext1) vẫn giữ cả hai — người dùng thấy nơi cài trên dòng game.
        return found
            .OrderBy(g => g.DisplayTitle, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(g => g.Folder, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>9 ký tự kiểu PPSA01234; bỏ ứng dụng hệ thống NPXS.</summary>
    private static bool LooksLikeTitleId(string name) =>
        name.Length == 9 && name.Take(4).All(char.IsAsciiLetter) && name.Skip(4).All(char.IsAsciiDigit)
        && !name.StartsWith("NPXS", StringComparison.OrdinalIgnoreCase);

    private static async Task<RemoteGame?> ProbeAsync(Ps5Ftp client, string folder, string tid, string location, CancellationToken ct)
    {
        var entries = await client.ListAsync(folder, ct).ConfigureAwait(false);
        var pkg = entries.FirstOrDefault(e => e.Kind != FtpEntryKind.Directory && e.Name.Equals("app.pkg", StringComparison.OrdinalIgnoreCase));
        if (pkg == null)
        {
            return null; // game chạy dump (ShadowMount…) chỉ có sce_sys + icon — không có gói để đọc
        }

        var size = pkg.Size > 0 ? pkg.Size : await client.GetSizeAsync(pkg.FullPath, ct).ConfigureAwait(false);
        if (size < MinPackageSize)
        {
            return null;
        }

        // 16 byte đầu đủ nhận dạng: gói PS4 (\x7FCNT) bỏ qua ngay; PS5 là \x7FFIH.
        var head = new MemoryStream();
        await client.DownloadRangeAsync(pkg.FullPath, head, 0, 16, null, ct).ConfigureAwait(false);
        var h = head.GetBuffer();
        if (head.Length >= 4 && h[0] == 0x7F && h[1] == (byte)'C' && h[2] == (byte)'N' && h[3] == (byte)'T')
        {
            return null;
        }

        var game = new RemoteGame
        {
            TitleId = tid,
            Folder = folder,
            PkgPath = pkg.FullPath,
            PkgSize = size,
            Location = location,
        };

        if (entries.Any(e => e.Name.Equals("app.json", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                using var doc = JsonDocument.Parse(await client.ReadAllBytesAsync(folder + "/app.json", ct).ConfigureAwait(false));
                if (doc.RootElement.TryGetProperty("pieces", out var pieces) && pieces.ValueKind == JsonValueKind.Array && pieces.GetArrayLength() > 0
                    && pieces[0].TryGetProperty("url", out var url) && url.GetString() is { } u)
                {
                    game.OriginalName = u.Replace('\\', '/').Split('/').Last();
                }

                if (doc.RootElement.TryGetProperty("fake", out var fake) && fake.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    game.IsFake = fake.GetBoolean();
                }
            }
            catch (JsonException)
            {
            }
        }

        var meta = $"/user/appmeta/{tid}";
        try
        {
            using var doc = JsonDocument.Parse(await client.ReadAllBytesAsync(meta + "/param.json", ct).ConfigureAwait(false));
            ReadParam(doc.RootElement, game);
        }
        catch (Exception ex) when (ex is FtpException or JsonException)
        {
            // Không có appmeta: thử sce_sys/param.json nằm cạnh gói (một số bản cài giữ lại).
            if (entries.Any(e => e.Name == "sce_sys"))
            {
                try
                {
                    using var doc = JsonDocument.Parse(await client.ReadAllBytesAsync(folder + "/sce_sys/param.json", ct).ConfigureAwait(false));
                    ReadParam(doc.RootElement, game);
                }
                catch (Exception inner) when (inner is FtpException or JsonException)
                {
                }
            }
        }

        if (await ExistsAsync(client, meta + "/icon0.png", ct).ConfigureAwait(false))
        {
            game.IconPath = meta + "/icon0.png";
        }
        else if (entries.Any(e => e.Name.Equals("icon0.png", StringComparison.OrdinalIgnoreCase)))
        {
            game.IconPath = folder + "/icon0.png";
        }

        return game;
    }

    private static async Task<bool> ExistsAsync(Ps5Ftp client, string path, CancellationToken ct)
    {
        try
        {
            return await client.GetSizeAsync(path, ct).ConfigureAwait(false) > 0;
        }
        catch (FtpException)
        {
            return false;
        }
    }

    /// <summary>param.json PS5: titleName theo en-US (dễ đọc nhất), không có thì theo defaultLanguage; contentVersion "01.103.000" → "01.103".</summary>
    public static void ReadParam(JsonElement root, RemoteGame game)
    {
        if (root.TryGetProperty("localizedParameters", out var localized) && localized.ValueKind == JsonValueKind.Object)
        {
            var fallback = localized.TryGetProperty("defaultLanguage", out var d) ? d.GetString() : null;
            foreach (var language in new[] { "en-US", "en-GB", fallback })
            {
                if (language != null && localized.TryGetProperty(language, out var l) && l.ValueKind == JsonValueKind.Object
                    && l.TryGetProperty("titleName", out var t) && t.GetString() is { Length: > 0 } title)
                {
                    game.Title = title.Trim();
                    break;
                }
            }
        }

        if (root.TryGetProperty("contentVersion", out var v) && v.GetString() is { } version)
        {
            var parts = version.Split('.');
            game.Version = parts.Length >= 2 ? parts[0] + "." + parts[1] : version;
        }

        if (root.TryGetProperty("contentId", out var cid) && cid.GetString() is { Length: > 0 } id)
        {
            game.ContentId = id;
        }
    }

    /// <summary>
    /// Mở FPKG PS5 đã cài (app.pkg trên PS5) mà KHÔNG tải cả gói: mỗi kênh giải nén một <see cref="FtpRangeStream"/> (một kết nối FTP).
    /// </summary>
    public static PackageReader OpenPackage(FtpConnectionInfo info, RemoteGame game, int channels, CancellationToken ct) =>
        PackageReader.Open(() => new FtpRangeStream(info, game.PkgPath, game.PkgSize, ct), game.PkgPath, channels, ct);
}
