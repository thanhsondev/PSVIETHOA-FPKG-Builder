using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using PsViethoa.FpkgBuilder.Core.Localization;
using PsViethoa.FpkgBuilder.Core.Services;

namespace PsViethoa.FpkgBuilder.Core.Ps4;

// Cơ chế PS4 PKG chuyển nguyên từ PSVIETHOA STUDIO 1.7.0 (Ngô Phi Phương): xả bằng LibOrbisPkg, đóng gói bằng orbis-pub-cmd
// (PS4 Fake PKG Tools 3.87 bản vá CyB1K) theo GP4 tự sinh. Hoàn toàn tách khỏi luồng PS5 FPKG.

/// <summary>Năm việc của trang PS4 PKG.</summary>
public enum Ps4JobKind
{
    Extract,
    Base,
    Patch,
    Merge,
    Dlc,
}

/// <summary>Hằng số dùng chung của PS4 PKG.</summary>
public static class Ps4Defaults
{
    /// <summary>Passcode mặc định của FPKG (32 số 0).</summary>
    public const string Passcode = "00000000000000000000000000000000";
}

/// <summary>Yêu cầu một tác vụ PS4 (cùng các trường như JobRequest của bản Studio).</summary>
public sealed record Ps4JobRequest(
    Ps4JobKind Kind,
    string Input,
    string Output,
    string BasePackage = "",
    string Passcode = Ps4Defaults.Passcode,
    string Version = "",
    bool Verify = false,
    bool ProjectOnly = false,
    string NativeTool = "",
    string[]? ExtractOnly = null,
    string EntitlementKey = "",
    string BasePasscode = "",
    string CustomParamSfo = "");

/// <summary>Thông tin đọc từ header + param.sfo của một PKG (hoặc thư mục game).</summary>
public sealed record Ps4PackageInfo(
    string Path,
    string Title,
    string TitleId,
    string ContentId,
    string Version,
    string Category,
    long Bytes,
    bool PasscodeMatches,
    bool RecoverableFakePackage,
    string MasterVersion = "");

/// <summary>Tiến độ: giai đoạn (đã dịch), chi tiết, phần trăm, byte đã xử lý, tốc độ, thời gian còn lại.</summary>
public sealed record Ps4Progress(
    string Phase,
    string Detail,
    double Percent,
    long BytesDone,
    long BytesTotal,
    double BytesPerSecond,
    double? SecondsRemaining,
    bool Estimated = false);

/// <summary>Kết quả tác vụ. <see cref="Status"/>: completed / project_ready.</summary>
public sealed record Ps4JobResult(
    string Directory,
    string? Package,
    string? Project,
    string Log,
    bool Verified,
    int FileCount,
    long Bytes,
    string Status);

public sealed record Ps4ManifestFile(string Path, long Bytes, string Sha256);

public sealed record Ps4BuildFile(string Target, string Source, long Bytes, long MtimeUtcTicks);

public sealed record Ps4DetectedVersion(string Version, string SfoPath, string Note);

/// <summary>Mức của một dòng nhật ký tác vụ PS4 (giữ nguyên nhãn của bản Studio).</summary>
public static class Ps4LogLevel
{
    public const string Info = "INFO";
    public const string Ok = "OK";
    public const string Warn = "WARN";
    public const string Error = "ERROR";
    public const string Engine = "ENGINE";
    public const string Stop = "STOP";
}

/// <summary>
/// Thư mục tác vụ (tên riêng, không bao giờ dùng lại thư mục cũ), tệp job.log và các báo cáo JSON. Mọi thứ ghi ra đều nằm trong
/// thư mục xuất; nguồn chỉ được đọc.
/// </summary>
public sealed class Ps4JobContext : IDisposable
{
    private readonly object _gate = new();
    private readonly StreamWriter _writer;

    public Ps4JobContext(string output, string prefix, CancellationToken token, Action<string, string>? log, Action<Ps4Progress>? progress, string? folderName = null)
    {
        Directory.CreateDirectory(output);
        var root = System.IO.Path.GetFullPath(output);
        if (folderName != null)
        {
            // Thư mục đặt tên (ví dụ CUSA12345-update) không bao giờ dùng lại thư mục đã có.
            Folder = System.IO.Path.Combine(root, folderName);
            for (var i = 1; Directory.Exists(Folder) || File.Exists(Folder); i++)
            {
                Folder = System.IO.Path.Combine(root, $"{folderName}_{i}");
            }
        }
        else
        {
            Folder = System.IO.Path.Combine(root, $"{prefix}_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N")[..4]}");
        }

        Directory.CreateDirectory(Folder);
        LogPath = System.IO.Path.Combine(Folder, "job.log");
        _writer = new StreamWriter(new FileStream(LogPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
        Token = token;
        OnLog = log;
        OnProgress = progress;
    }

    public Action<string, string>? OnLog { get; }

    public Action<Ps4Progress>? OnProgress { get; }

    public string Folder { get; }

    public string LogPath { get; }

    public CancellationToken Token { get; }

    /// <summary>Phần trăm bắt đầu của bước đóng gói (gộp từ PKG: 50%, update có base phụ: 10%).</summary>
    public double BuildProgressStart { get; set; }

    /// <summary>Đường dẫn để orbis-pub-cmd mở được tệp: nguyên vẹn trên Windows, ổ Z: của Wine ở macOS/Linux.</summary>
    public Func<string, string> ToolPath { get; set; } = System.IO.Path.GetFullPath;

    public void Log(string message, string level = Ps4LogLevel.Info)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] [{level}] {message}";
        lock (_gate)
        {
            _writer.WriteLine(line);
        }

        OnLog?.Invoke(line, level);
    }

    public void Progress(Ps4Progress state)
    {
        Token.ThrowIfCancellationRequested();
        OnProgress?.Invoke(state);
    }

    public void Save(string name, object value) =>
        File.WriteAllText(System.IO.Path.Combine(Folder, name), JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));

    public void Dispose() => _writer.Dispose();
}

/// <summary>Tốc độ trượt trên ~6 giây gần nhất và thời gian còn lại.</summary>
internal sealed class Ps4RateMeter
{
    private readonly Stopwatch _watch = Stopwatch.StartNew();
    private readonly Queue<(double Time, long Bytes)> _points = new();

    public (double Speed, double? Eta) Update(long done, long total)
    {
        var now = _watch.Elapsed.TotalSeconds;
        _points.Enqueue((now, done));
        while (_points.Count > 2 && now - _points.Peek().Time > 6)
        {
            _points.Dequeue();
        }

        var oldest = _points.Peek();
        var speed = now - oldest.Time > 0.15 ? Math.Max(0, done - oldest.Bytes) / (now - oldest.Time) : 0;
        return (speed, speed > 1 && total > done ? (total - done) / speed : null);
    }
}

/// <summary>Kiểm tra đường dẫn, dung lượng trống và duyệt cây thư mục nguồn.</summary>
public static class Ps4Files
{
    public static string Hash(string path)
    {
        using var file = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant();
    }

    /// <summary>Đường dẫn con an toàn trong <paramref name="root"/>: chặn "..", đường tuyệt đối, tên thiết bị Windows, ký tự cấm.</summary>
    public static string SafeChild(string root, string relative)
    {
        relative = relative.Replace('\\', '/');
        if (relative.StartsWith('/') || relative.Contains(':') || relative.Split('/').Any(part => part is ".." or "." || part.EndsWith(' ') || part.EndsWith('.')))
        {
            throw new InvalidDataException(Loc.F("Ps4.Core.BadPkgPath", relative));
        }

        var fullRoot = System.IO.Path.GetFullPath(root).TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
        var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(fullRoot, relative));
        if (!full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(Loc.T("Ps4.Core.PathEscapes"));
        }

        foreach (var part in relative.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            // Giữ quy tắc tên tệp của Windows trên mọi hệ điều hành để thư mục đã xả dùng được ở mọi máy.
            if (part.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0 || part.IndexOfAny(['<', '>', ':', '"', '|', '?', '*']) >= 0)
            {
                throw new InvalidDataException(Loc.F("Ps4.Core.BadFileName", part));
            }

            var stem = part.Split('.')[0].ToUpperInvariant();
            if (stem is "CON" or "PRN" or "AUX" or "NUL" || Regex.IsMatch(stem, "^(COM|LPT)[1-9]$"))
            {
                throw new InvalidDataException(Loc.F("Ps4.Core.DeviceName", part));
            }
        }

        return full;
    }

    public static void CheckSpace(string directory, long required)
    {
        var volume = DiskSpaceAdvisor.Probe(directory);
        if (volume != null && volume.FreeBytes < required)
        {
            throw new IOException(Loc.F("Ps4.Core.NoSpace", volume.MountPoint, (volume.FreeBytes / 1e9).ToString("F1", CultureInfo.InvariantCulture),
                (required / 1e9).ToString("F1", CultureInfo.InvariantCulture)));
        }
    }

    public static bool IsInside(string child, string parent)
    {
        var c = System.IO.Path.GetFullPath(child).TrimEnd('\\', '/');
        var p = System.IO.Path.GetFullPath(parent).TrimEnd('\\', '/');
        return c.Equals(p, StringComparison.OrdinalIgnoreCase) || c.StartsWith(p + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Mọi tệp trong <paramref name="root"/>; nguồn chứa liên kết thư mục/tệp bị từ chối như bản Studio.</summary>
    public static IEnumerable<string> Enumerate(string root)
    {
        foreach (var item in Directory.EnumerateFileSystemEntries(root))
        {
            var attributes = File.GetAttributes(item);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException(Loc.F("Ps4.Core.SourceHasLink", item));
            }

            if ((attributes & FileAttributes.Directory) != 0)
            {
                foreach (var file in Enumerate(item))
                {
                    yield return file;
                }
            }
            else
            {
                yield return item;
            }
        }
    }
}

/// <summary>Phiên bản ứng dụng PS4 dạng "NN.NN" (APP_VER / VERSION của param.sfo).</summary>
public static class Ps4AppVersion
{
    public static bool TryParse(string? text, out int value)
    {
        value = 0;
        var match = Regex.Match(text?.Trim() ?? string.Empty, @"^([0-9]{1,2})\.([0-9]{2})$");
        if (!match.Success)
        {
            return false;
        }

        value = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) * 100 + int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        return true;
    }

    public static string Format(int value) => $"{Math.Clamp(value, 0, 9999) / 100:00}.{Math.Clamp(value, 0, 9999) % 100:00}";

    public static string Normalize(string text) => TryParse(text, out var value) ? Format(value) : throw new ArgumentException(Loc.T("Ps4.Core.BadVersion"));

    public static string Step(string text, int delta)
    {
        if (!TryParse(text, out var value))
        {
            throw new ArgumentException(Loc.T("Ps4.Core.VersionFirst"));
        }

        return Format(value + delta);
    }
}
