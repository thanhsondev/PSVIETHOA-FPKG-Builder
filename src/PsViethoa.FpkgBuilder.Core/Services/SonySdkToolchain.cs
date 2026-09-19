using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using PsViethoa.FpkgBuilder.Core.Localization;

namespace PsViethoa.FpkgBuilder.Core.Services;

/// <summary>Bộ công cụ SDK Sony đã tìm thấy và cách chạy nó trên hệ điều hành hiện tại.</summary>
/// <param name="Directory">Thư mục chứa prospero-pub-cmd.exe (kèm libScePubTools.dll và ext/) — thư mục "toolchain" của bộ sdk-fpkg729-fix.</param>
/// <param name="WinePath">Tệp chạy wine (macOS/Linux); null trên Windows.</param>
/// <param name="WinePrefix">Thư mục WINEPREFIX riêng của ứng dụng; null trên Windows.</param>
public sealed record SonySdkRuntime(string Directory, string? WinePath, string? WinePrefix)
{
    public string PublisherPath => Path.Combine(Directory, SonySdkToolchain.PublisherFileName);

    /// <summary>Thư mục gốc của bộ sdk-fpkg729-fix (README, build.bat, scripts/) khi bộ công cụ giữ nguyên bố cục gốc; nếu không thì chính <see cref="Directory"/>.</summary>
    public string ToolkitRoot =>
        string.Equals(Path.GetFileName(Path.TrimEndingDirectorySeparator(Directory)), SonySdkToolchain.ToolchainFolderName, StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Directory)) ?? Directory
            : Directory;

    public bool UsesWine => WinePath != null;

    /// <summary>Đường dẫn để chương trình Windows mở được tệp: nguyên vẹn trên Windows, ổ Z: của Wine (Z: = "/") nơi khác.</summary>
    public string ToolPath(string path) => UsesWine ? SonySdkToolchain.ToWinePath(path) : Path.GetFullPath(path);
}

/// <summary>
/// Tìm bộ công cụ Publishing Tools 2.79 đã vá (profile <c>sdk279-plaintext-unsigned-v2</c>) đóng gói kèm ứng dụng và môi trường
/// để chạy nó. Mọi tệp của SDK là chương trình Windows x86-64: trên Windows chạy thẳng (cần Visual C++ 2015-2022 x64), trên macOS
/// chạy qua Wine kèm theo (máy Apple Silicon cần Rosetta 2); trên Linux dùng Wine cài từ distro (không kèm theo).
/// </summary>
public static class SonySdkToolchain
{
    public const string PublisherFileName = "prospero-pub-cmd.exe";

    /// <summary>Thư mục bộ sdk-fpkg729-fix kèm ứng dụng, giữ nguyên bố cục gốc: README.md, build.bat, build-from-folder.ps1, scripts/, toolchain/.</summary>
    public const string FolderName = "sony-sdk";

    /// <summary>Thư mục con chứa prospero-pub-cmd.exe trong bộ gốc.</summary>
    public const string ToolchainFolderName = "toolchain";

    public const string ToolchainEnvironmentVariable = "PSVIETHOA_SONY_SDK";

    public const string WineEnvironmentVariable = "PSVIETHOA_WINE";

    /// <summary>Profile của bản vá SDK mà bước hậu xử lý được viết cho.</summary>
    public const string Profile = "sdk279-plaintext-direct-v3";

    public const string VcRedistFileName = "vc_redist.x64.exe";

    /// <summary>
    /// Thư mục chứa prospero-pub-cmd.exe, null nếu gói ứng dụng không kèm. Nhận cả bố cục gốc (sony-sdk/toolchain/) lẫn thư mục
    /// toolchain đặt thẳng (biến môi trường <see cref="ToolchainEnvironmentVariable"/> trỏ vào một trong hai).
    /// </summary>
    public static string? FindToolchain()
    {
        var fromEnvironment = Environment.GetEnvironmentVariable(ToolchainEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(fromEnvironment) && ToolchainIn(fromEnvironment) is { } explicitDirectory)
        {
            return explicitDirectory;
        }

        return CandidateDirectories(FolderName).Select(ToolchainIn).FirstOrDefault(directory => directory != null);
    }

    /// <summary>Thư mục toolchain bên trong <paramref name="root"/> (bố cục gốc) hoặc chính nó (đặt thẳng); null nếu không có SDK.</summary>
    public static string? ToolchainIn(string root)
    {
        var nested = Path.Combine(root, ToolchainFolderName);
        if (File.Exists(Path.Combine(nested, PublisherFileName)))
        {
            return Path.GetFullPath(nested);
        }

        return File.Exists(Path.Combine(root, PublisherFileName)) ? Path.GetFullPath(root) : null;
    }

    /// <summary>Tệp chạy wine: kèm ứng dụng trước, rồi bản cài trên máy. Null trên Windows hoặc khi không có.</summary>
    public static string? FindWine()
    {
        if (OperatingSystem.IsWindows())
        {
            return null;
        }

        var fromEnvironment = Environment.GetEnvironmentVariable(WineEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(fromEnvironment) && File.Exists(fromEnvironment))
        {
            return Path.GetFullPath(fromEnvironment);
        }

        var bundled = CandidateDirectories("wine")
            .Select(directory => Path.Combine(directory, "bin", "wine"))
            .FirstOrDefault(File.Exists);
        if (bundled != null)
        {
            return bundled;
        }

        // Bản build tay trong repo: scripts/fetch-wine.sh tải vào .cache/wine-*/wine.
        foreach (var root in AncestorDirectories())
        {
            var cache = Path.Combine(root, ".cache");
            if (!Directory.Exists(cache))
            {
                continue;
            }

            var cached = Directory.EnumerateDirectories(cache, "wine-*")
                .OrderByDescending(path => path, StringComparer.Ordinal)
                .Select(path => Path.Combine(path, "wine", "bin", "wine"))
                .FirstOrDefault(File.Exists);
            if (cached != null)
            {
                return cached;
            }
        }

        var system = new[]
        {
            "/Applications/Wine Stable.app/Contents/Resources/wine/bin/wine",
            "/Applications/Wine Devel.app/Contents/Resources/wine/bin/wine",
            "/Applications/Wine Staging.app/Contents/Resources/wine/bin/wine",
            "/opt/homebrew/bin/wine",
            "/usr/local/bin/wine",
            "/usr/bin/wine",
            // Linux: gói wine của distro (Debian/Ubuntu: wine64, Fedora/Arch: wine) hoặc bản WineHQ trong /opt/wine-*.
            "/usr/bin/wine64",
            "/usr/local/bin/wine64",
            "/usr/lib/wine/wine64",
            "/usr/lib64/wine/wine64",
            "/opt/wine-stable/bin/wine",
            "/opt/wine-staging/bin/wine",
            "/opt/wine-devel/bin/wine",
        };
        return system.FirstOrDefault(File.Exists) ?? FindOnPath("wine") ?? FindOnPath("wine64");
    }

    /// <summary>Tệp chạy có tên <paramref name="name"/> trong PATH (Linux: wine cài ở chỗ lạ, ví dụ ~/.local/bin).</summary>
    private static string? FindOnPath(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(directory, name);
                if (File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }
            catch (Exception)
            {
            }
        }

        return null;
    }

    /// <summary>WINEPREFIX riêng của ứng dụng (tạo lần đầu dùng, ~170 MB), không đụng tới ~/.wine của người dùng.</summary>
    public static string WinePrefixPath()
    {
        var baseFolder = OperatingSystem.IsMacOS()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "PSVIETHOA FPKG Builder")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "psviethoa-fpkg-builder");
        return Path.Combine(baseFolder, "wine-prefix");
    }

    /// <summary>
    /// Kiểm tra đủ điều kiện chạy SDK Sony. Trả về môi trường chạy, hoặc null kèm lý do (đã dịch) để ghi nhật ký / hiển thị.
    /// </summary>
    public static SonySdkRuntime? Resolve(out string? problem)
    {
        problem = null;
        var directory = FindToolchain();
        if (directory == null)
        {
            problem = Loc.T("Sdk.ToolchainMissing");
            return null;
        }

        if (OperatingSystem.IsWindows())
        {
            return new SonySdkRuntime(directory, null, null);
        }

        var wine = FindWine();
        if (wine == null)
        {
            problem = Loc.T(OperatingSystem.IsMacOS() ? "Sdk.WineMissingMac" : "Sdk.WineMissing");
            return null;
        }

        if (OperatingSystem.IsMacOS() && RuntimeInformation.OSArchitecture == Architecture.Arm64 && !RosettaInstalled)
        {
            problem = Loc.T("Sdk.RosettaMissing");
            return null;
        }

        return new SonySdkRuntime(directory, wine, WinePrefixPath());
    }

    /// <summary>Rosetta 2 (bắt buộc để Wine x86-64 chạy trên Apple Silicon).</summary>
    // Chỉ libRosettaRuntime mới chạy được tệp x86-64 của macOS: sau khi nâng cấp macOS lớn, thư mục oah có thể chỉ còn RosettaLinux
    // (dành cho máy ảo Linux) và Wine báo "Bad CPU type in executable" — khi đó phải cài lại Rosetta 2.
    public static bool RosettaInstalled => File.Exists("/Library/Apple/usr/libexec/oah/libRosettaRuntime");

    /// <summary>Máy Apple Silicon chưa có (hoặc mất sau khi nâng cấp macOS) Rosetta 2 — SDK Sony qua Wine x86-64 không chạy được.</summary>
    public static bool RosettaMissing =>
        OperatingSystem.IsMacOS() && RuntimeInformation.OSArchitecture == Architecture.Arm64 && !RosettaInstalled;

    /// <summary>
    /// Cài Rosetta 2 bằng công cụ của Apple (<c>softwareupdate --install-rosetta --agree-to-license</c>, không cần quyền quản trị).
    /// Chỉ gọi khi người dùng đã bấm đồng ý. Trả về true khi cài xong và libRosettaRuntime đã có.
    /// </summary>
    public static bool InstallRosetta(Action<string> log, TimeSpan timeout)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return false;
        }

        try
        {
            var info = new ProcessStartInfo("/usr/sbin/softwareupdate")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            info.ArgumentList.Add("--install-rosetta");
            info.ArgumentList.Add("--agree-to-license");
            using var process = Process.Start(info);
            if (process == null)
            {
                return false;
            }

            process.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) { log(e.Data); } };
            process.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) { log(e.Data); } };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            if (!process.WaitForExit((int)timeout.TotalMilliseconds))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception)
                {
                }

                return false;
            }

            process.WaitForExit();
            return process.ExitCode == 0 && RosettaInstalled;
        }
        catch (Exception ex)
        {
            log(ex.Message);
            return false;
        }
    }

    /// <summary>Thư viện Visual C++ 2015-2022 x64 mà prospero-pub-cmd.exe cần (MSVCP140 / VCRUNTIME140).</summary>
    public static bool VcRuntimeInstalled
    {
        get
        {
            if (!OperatingSystem.IsWindows())
            {
                return true;
            }

            foreach (var name in new[] { "msvcp140.dll", "vcruntime140.dll", "vcruntime140_1.dll" })
            {
                if (!NativeLibrary.TryLoad(name, out var handle))
                {
                    return false;
                }

                NativeLibrary.Free(handle);
            }

            return true;
        }
    }

    /// <summary>Bộ cài Visual C++ x64 kèm ứng dụng (redist/vc_redist.x64.exe), null nếu không kèm.</summary>
    public static string? BundledVcRedist =>
        CandidateDirectories("redist")
            .Select(directory => Path.Combine(directory, VcRedistFileName))
            .FirstOrDefault(File.Exists);

    /// <summary>
    /// Cài Visual C++ x64 kèm theo ở chế độ im lặng với quyền quản trị (một hộp UAC). Trả về true khi thư viện đã dùng được.
    /// </summary>
    public static bool InstallVcRuntime(Action<string> log, TimeSpan timeout)
    {
        if (!OperatingSystem.IsWindows() || VcRuntimeInstalled)
        {
            return true;
        }

        var installer = BundledVcRedist;
        if (installer == null)
        {
            return false;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo(installer)
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = "/install /quiet /norestart",
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            if (process == null || !process.WaitForExit(timeout))
            {
                return false;
            }

            // 0 = xong, 1638 = đã có bản mới hơn, 3010 = cần khởi động lại (thư viện vẫn dùng được ngay).
            log(Loc.F("Sdk.VcRedistExit", process.ExitCode));
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            log(Loc.T("Sdk.VcRedistCancelled"));
            return false;
        }
        catch (Exception ex)
        {
            log(ex.Message);
            return false;
        }

        return VcRuntimeInstalled;
    }

    /// <summary>
    /// macOS: gỡ cờ quarantine (gắn khi tải zip bằng trình duyệt) khỏi Wine và bộ công cụ kèm ứng dụng, để Gatekeeper không chặn
    /// các chương trình con chưa ký. Bỏ qua lỗi — ứng dụng chạy từ vị trí chỉ đọc (App Translocation) thì không gỡ được.
    /// </summary>
    public static void ClearQuarantine(SonySdkRuntime runtime)
    {
        if (!OperatingSystem.IsMacOS() || !File.Exists("/usr/bin/xattr"))
        {
            return;
        }

        var wineRoot = runtime.WinePath is { } wine ? Path.GetDirectoryName(Path.GetDirectoryName(wine)) : null;
        foreach (var directory in new[] { wineRoot, runtime.Directory })
        {
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            {
                continue;
            }

            try
            {
                var info = new ProcessStartInfo("/usr/bin/xattr") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
                info.ArgumentList.Add("-dr");
                info.ArgumentList.Add("com.apple.quarantine");
                info.ArgumentList.Add(directory);
                using var process = Process.Start(info);
                process?.WaitForExit(30_000);
            }
            catch (Exception)
            {
            }
        }
    }

    /// <summary>"/Users/a/b c" → "Z:\Users\a\b c" (ổ Z: của mọi WINEPREFIX trỏ vào gốc "/").</summary>
    public static string ToWinePath(string path)
    {
        var full = Path.GetFullPath(path);
        return "Z:" + full.Replace('/', '\\');
    }

    /// <summary>Phiên bản Wine (dòng đầu của <c>wine --version</c>), null nếu không chạy được.</summary>
    public static string? WineVersion(string winePath)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(winePath, "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            if (process == null)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(15_000);
            return process.ExitCode == 0 ? output.Trim() : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Các thư mục có thể chứa <paramref name="name"/>: cạnh ứng dụng, Contents/Resources của .app, thư mục "app" cạnh fpkg-cli,
    /// .app nằm cạnh fpkg-cli, và libs/ của repo khi chạy bản build tay.
    /// </summary>
    private static IEnumerable<string> CandidateDirectories(string name)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var roots = new List<string>();
        foreach (var directory in new[] { AppContext.BaseDirectory, Path.GetDirectoryName(Environment.ProcessPath) })
        {
            if (string.IsNullOrEmpty(directory))
            {
                continue;
            }

            var trimmed = Path.TrimEndingDirectorySeparator(directory);
            var parent = Path.GetDirectoryName(trimmed);
            roots.Add(trimmed);
            if (parent != null)
            {
                // fpkg-cli cài trong thư mục con của ứng dụng (bộ cài Windows): bộ công cụ nằm ở thư mục cha.
                roots.Add(parent);
                roots.Add(Path.Combine(parent, "Resources"));
                roots.Add(Path.Combine(parent, "app"));
                roots.Add(Path.Combine(parent, "PSVIETHOA FPKG Builder.app", "Contents", "Resources"));
            }
        }

        foreach (var ancestor in AncestorDirectories())
        {
            roots.Add(Path.Combine(ancestor, "libs"));
        }

        foreach (var root in roots)
        {
            var candidate = Path.Combine(root, name);
            if (seen.Add(candidate) && Directory.Exists(candidate))
            {
                yield return candidate;
            }
        }
    }

    private static IEnumerable<string> AncestorDirectories()
    {
        var current = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        for (var depth = 0; depth < 8 && !string.IsNullOrEmpty(current); depth++)
        {
            yield return current;
            current = Path.GetDirectoryName(current);
        }
    }
}
