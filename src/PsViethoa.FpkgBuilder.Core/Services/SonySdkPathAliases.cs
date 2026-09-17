using PsViethoa.FpkgBuilder.Core.Localization;
using PsViethoa.FpkgBuilder.Core.Models;

namespace PsViethoa.FpkgBuilder.Core.Services;

/// <summary>
/// Bí danh ASCII cho các thư mục có ký tự ngoài ASCII. prospero-pub-cmd.exe nhận dòng lệnh theo bảng mã ANSI và từ chối
/// <c>src_path</c> ngoài ASCII trong GP5 ("File not found" với "Yōtei" → "Yotei", "Format of the GP5 file is not valid …
/// invalid attribute value src_path") — bộ gốc chạy bằng build.bat cũng vậy. Cách xử lý: tạo liên kết tượng trưng (macOS/Linux)
/// hoặc junction (Windows, cần NTFS) trong một thư mục ASCII ghi được, rồi đưa SDK mọi đường dẫn qua bí danh; xoá liên kết khi xong,
/// thư mục thật không bị đụng tới.
/// </summary>
public sealed class SonySdkPathAliases : IDisposable
{
    private readonly List<(string Real, string Alias)> _map = new();
    private readonly Action<LogEntry> _log;
    private string? _folder;
    private readonly IReadOnlyList<string> _bases;

    private SonySdkPathAliases(IReadOnlyList<string> bases, Action<LogEntry> log)
    {
        _bases = bases;
        _log = log;
    }

    /// <summary>Thư mục chứa các bí danh (ASCII), null khi chưa cần bí danh nào.</summary>
    public string? Folder => _folder;

    public int Count => _map.Count;

    /// <summary>Chỉ ASCII mới chắc chắn an toàn: bảng mã ANSI của Windows tuỳ máy, Wine tuỳ locale.</summary>
    public static bool IsAsciiSafe(string path) => path.All(c => c < 128);

    /// <summary>
    /// Các thư mục có thể chứa bí danh, theo thứ tự thử: thư mục tạm hệ thống, thư mục tạm của lượt này, ProgramData (Windows),
    /// gốc ổ xuất. Thư mục nào ASCII và tạo được liên kết mới được dùng. Không bao giờ dùng ổ nguồn: nguồn chỉ được đọc (ổ game của
    /// người dùng có thể là ổ lưu trữ không được phép ghi gì lên).
    /// </summary>
    public static IReadOnlyList<string> DefaultBases(string temporaryFolder, string outputFolder)
    {
        var bases = new List<string?> { Path.GetTempPath(), temporaryFolder };
        if (OperatingSystem.IsWindows())
        {
            bases.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PSVIETHOA FPKG Builder"));
        }
        else
        {
            bases.Add("/tmp");
        }

        bases.Add(Path.GetPathRoot(Path.GetFullPath(outputFolder)));
        return bases
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate))
            .Select(candidate => Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate!)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static SonySdkPathAliases Create(IReadOnlyList<string> bases, Action<LogEntry> log)
    {
        CleanupStale(bases);
        return new SonySdkPathAliases(bases, log);
    }

    /// <summary>
    /// Gỡ thư mục bí danh của các lượt trước không được dọn (ứng dụng bị tắt giữa chừng): tên thư mục mang PID của tiến trình tạo
    /// ra nó, tiến trình không còn thì chỉ xoá liên kết và thư mục — thư mục đích không bị đụng.
    /// </summary>
    public static void CleanupStale(IEnumerable<string> bases)
    {
        foreach (var baseFolder in bases.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            IEnumerable<string> stale;
            try
            {
                if (!Directory.Exists(baseFolder))
                {
                    continue;
                }

                stale = Directory.EnumerateDirectories(baseFolder, "psviethoa-sdk-*").ToList();
            }
            catch (Exception)
            {
                continue;
            }

            foreach (var folder in stale)
            {
                var parts = Path.GetFileName(folder).Split('-');
                if (parts.Length < 4 || !int.TryParse(parts[2], System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var pid) || pid == Environment.ProcessId || IsProcessAlive(pid))
                {
                    continue;
                }

                try
                {
                    foreach (var entry in new DirectoryInfo(folder).EnumerateFileSystemInfos())
                    {
                        if (entry.LinkTarget != null || (entry.Attributes & FileAttributes.ReparsePoint) != 0)
                        {
                            if (OperatingSystem.IsWindows())
                            {
                                Directory.Delete(entry.FullName);
                            }
                            else
                            {
                                File.Delete(entry.FullName);
                            }
                        }
                    }

                    if (!Directory.EnumerateFileSystemEntries(folder).Any())
                    {
                        Directory.Delete(folder);
                    }
                }
                catch (Exception)
                {
                }
            }
        }
    }

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Bí danh cho <paramref name="realDirectory"/> (đường dẫn thật, đã giải liên kết): trả về đường dẫn thật khi nó đã ASCII,
    /// đường dẫn bí danh khi tạo được, ném lỗi (đã dịch) khi không thư mục nào tạo được liên kết.
    /// </summary>
    public string Add(string realDirectory, string name)
    {
        var real = Path.TrimEndingDirectorySeparator(Path.GetFullPath(realDirectory));
        if (IsAsciiSafe(real))
        {
            return real;
        }

        foreach (var existing in _map)
        {
            if (string.Equals(existing.Real, real, Comparison))
            {
                return existing.Alias;
            }
        }

        var failures = new List<string>();
        foreach (var candidate in _folder != null ? [_folder] : _bases.Where(IsAsciiSafe))
        {
            var folder = _folder ?? Path.Combine(candidate, "psviethoa-sdk-" + Environment.ProcessId.ToString("x") + "-" + Guid.NewGuid().ToString("N")[..6]);
            var alias = Path.Combine(folder, name);
            try
            {
                Directory.CreateDirectory(folder);
                if (Directory.Exists(alias) || File.Exists(alias))
                {
                    Directory.Delete(alias);
                }

                if (OperatingSystem.IsWindows())
                {
                    SourceMirror.CreateJunction(alias, real);
                }
                else
                {
                    Directory.CreateSymbolicLink(alias, real);
                }

                // Kiểm tra liên kết dùng được thật (junction tới ổ khác, symlink…): phải liệt kê được thư mục đích qua bí danh.
                _ = Directory.EnumerateFileSystemEntries(alias).Any();
                _folder = folder;
                _map.Add((real, alias));
                _log(new LogEntry(LogLevel.Info, Loc.F("Sdk.AliasCreated", real, alias)));
                return alias;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                failures.Add(candidate + ": " + ex.Message);
                if (_folder == null)
                {
                    try
                    {
                        if (Directory.Exists(alias))
                        {
                            Directory.Delete(alias);
                        }

                        if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
                        {
                            Directory.Delete(folder);
                        }
                    }
                    catch (Exception)
                    {
                    }
                }
            }
        }

        throw new InvalidOperationException(Loc.F("Sdk.AliasFailed", real, string.Join(" · ", failures)));
    }

    /// <summary>Đổi một đường dẫn thật nằm trong thư mục đã có bí danh sang đường dẫn qua bí danh; đường dẫn khác giữ nguyên.</summary>
    public string Map(string path)
    {
        foreach (var (real, alias) in _map)
        {
            if (string.Equals(path, real, Comparison))
            {
                return alias;
            }

            if (path.Length > real.Length && path.StartsWith(real, Comparison) && (path[real.Length] == Path.DirectorySeparatorChar || path[real.Length] == Path.AltDirectorySeparatorChar))
            {
                return alias + path[real.Length..];
            }
        }

        return path;
    }

    private static StringComparison Comparison =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>Xoá liên kết (không phải thư mục đích) và thư mục chứa bí danh.</summary>
    public void Dispose()
    {
        foreach (var (_, alias) in _map)
        {
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    // Với junction, RemoveDirectory chỉ gỡ điểm nối.
                    Directory.Delete(alias);
                }
                else
                {
                    File.Delete(alias);
                }
            }
            catch (Exception)
            {
            }
        }

        _map.Clear();
        if (_folder != null)
        {
            try
            {
                Directory.Delete(_folder, recursive: false);
            }
            catch (Exception)
            {
            }

            _folder = null;
        }
    }
}
