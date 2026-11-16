using System.Security.Cryptography;

namespace PsViethoa.FpkgBuilder.Core.Services;

/// <summary>Tìm libScePubTools.dll (Oodle gốc của Publishing Tools) – chỉ hoạt động trên Windows x64.</summary>
public static class PublishingToolsLocator
{
    public const string FileName = "libScePubTools.dll";
    public const string EnvironmentVariable = "LIBPROSPERO_PUBTOOLS_PATH";

    public static bool IsSupportedPlatform => OperatingSystem.IsWindows() && Environment.Is64BitProcess;

    public static IReadOnlyList<string> CandidatePaths(string? explicitPath)
    {
        var list = new List<string>();
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            list.Add(explicitPath);
        }

        var fromEnvironment = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            list.Add(fromEnvironment);
        }

        list.Add(Path.Combine(AppContext.BaseDirectory, FileName));
        list.Add(Path.Combine(AppContext.BaseDirectory, "libs", FileName));

        if (OperatingSystem.IsWindows())
        {
            list.Add(@"C:\SCE\Prospero\Tools\Publishing Tools\bin\" + FileName);
            var sdkRoot = Environment.GetEnvironmentVariable("SCE_PROSPERO_SDK_DIR");
            if (!string.IsNullOrWhiteSpace(sdkRoot))
            {
                list.Add(Path.Combine(sdkRoot, "..", "Tools", "Publishing Tools", "bin", FileName));
            }
        }

        return list;
    }

    /// <summary>Trả về đường dẫn DLL đầu tiên tồn tại; không có tệp rời thì dùng bản nhúng trong exe (bản Windows x64); không có thì null.</summary>
    public static string? Find(string? explicitPath)
    {
        foreach (var candidate in CandidatePaths(explicitPath))
        {
            try
            {
                var full = Path.GetFullPath(candidate);
                if (File.Exists(full))
                {
                    return full;
                }
            }
            catch (Exception)
            {
            }
        }

        return IsSupportedPlatform ? ExtractEmbedded() : null;
    }

    private const string ResourceName = "PsViethoa.FpkgBuilder.Core.LibScePubTools.dll";
    private static readonly object ExtractLock = new();
    private static string? _extracted;

    /// <summary>Có bản nhúng trong exe không (bản Windows x64).</summary>
    public static bool HasEmbedded => typeof(PublishingToolsLocator).Assembly.GetManifestResourceInfo(ResourceName) != null;

    /// <summary>
    /// Giải bản nhúng ra %LOCALAPPDATA%\PSVIETHOA FPKG Builder\pubtools\&lt;sha256 12 ký tự&gt;\libScePubTools.dll (một lần; tên theo mã băm
    /// nên bản mới không đè bản cũ đang được nạp). Ghi ra tệp tạm rồi đổi tên để không bao giờ để lại DLL dở dang.
    /// </summary>
    private static string? ExtractEmbedded()
    {
        lock (ExtractLock)
        {
            if (_extracted != null && File.Exists(_extracted))
            {
                return _extracted;
            }

            try
            {
                using var resource = typeof(PublishingToolsLocator).Assembly.GetManifestResourceStream(ResourceName);
                if (resource == null)
                {
                    return null;
                }

                var bytes = new byte[resource.Length];
                resource.ReadExactly(bytes);
                var hash = Convert.ToHexString(SHA256.HashData(bytes))[..12].ToLowerInvariant();
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PSVIETHOA FPKG Builder", "pubtools", hash);
                var target = Path.Combine(dir, FileName);
                if (!File.Exists(target) || new FileInfo(target).Length != bytes.Length)
                {
                    Directory.CreateDirectory(dir);
                    var temp = target + "." + Environment.ProcessId + ".tmp";
                    File.WriteAllBytes(temp, bytes);
                    try
                    {
                        File.Move(temp, target, true);
                    }
                    catch (IOException) when (File.Exists(target) && new FileInfo(target).Length == bytes.Length)
                    {
                        File.Delete(temp);   // tiến trình khác vừa giải xong cùng bản
                    }
                }

                _extracted = target;
                return target;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
