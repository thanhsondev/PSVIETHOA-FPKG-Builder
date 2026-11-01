using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using PsViethoa.FpkgBuilder.Core.Localization;
using PsViethoa.FpkgBuilder.Core.Services;

namespace PsViethoa.FpkgBuilder.Core.Ps4;

/// <summary>Bộ orbis-pub-cmd đã tìm thấy và cách chạy nó (thẳng trên Windows, qua Wine ở macOS/Linux).</summary>
public sealed record Ps4Runtime(string ToolDirectory, string? WinePath, string? WinePrefix)
{
    public bool UsesWine => WinePath != null;

    public string ToolPath(string path) => UsesWine ? SonySdkToolchain.ToWinePath(path) : Path.GetFullPath(path);
}

/// <summary>
/// Ổ ảo ngắn trong WINEPREFIX của PS4 (dosdevices/m: → thư mục thật) để orbis-pub-cmd (32-bit, giới hạn MAX_PATH 260 ký tự) mở được
/// tệp nằm sâu: "M:\image0\Media\…" thay vì "Z:\Users\…\image0\Media\…". Chỉ là liên kết trong prefix riêng của ứng dụng, xoá khi
/// xong; nguồn không bị đụng tới. Trên Windows không làm gì (giữ nguyên cách chạy của bản Studio).
/// </summary>
internal sealed class Ps4DriveMap : IDisposable
{
    private const string Letters = "mnopqrstuvwxy";
    private static readonly object Gate = new();
    private static int _active;

    private readonly Ps4Runtime _runtime;
    private readonly List<(string Root, char Letter, string Link)> _maps = new();
    private bool _disposed;

    public Ps4DriveMap(Ps4Runtime runtime)
    {
        _runtime = runtime;
        if (!runtime.UsesWine)
        {
            return;
        }

        lock (Gate)
        {
            if (_active == 0)
            {
                CleanupStale(runtime);
            }

            _active++;
        }
    }

    private string Devices => Path.Combine(_runtime.WinePrefix!, "dosdevices");

    /// <summary>Gắn một ổ cho <paramref name="directory"/> (bỏ qua nếu đã nằm trong một ổ đã gắn).</summary>
    public void Map(string? directory)
    {
        if (!_runtime.UsesWine || string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return;
        }

        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (_maps.Any(map => Inside(full, map.Root)))
        {
            return;
        }

        lock (Gate)
        {
            Directory.CreateDirectory(Devices);
            foreach (var letter in Letters)
            {
                var link = Path.Combine(Devices, letter + ":");
                if (File.Exists(link) || Directory.Exists(link) || new FileInfo(link).LinkTarget != null)
                {
                    continue;
                }

                try
                {
                    File.CreateSymbolicLink(link, full);
                    _maps.Add((full, letter, link));
                    return;
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                    return;
                }
            }
        }
    }

    /// <summary>Đường dẫn Windows ngắn nhất cho <paramref name="path"/>: qua ổ đã gắn sâu nhất, nếu không thì ổ Z:.</summary>
    public string ToolPath(string path)
    {
        if (!_runtime.UsesWine)
        {
            return _runtime.ToolPath(path);
        }

        var full = Path.GetFullPath(path);
        var best = _maps.Where(map => Inside(full, map.Root)).OrderByDescending(map => map.Root.Length).FirstOrDefault();
        if (best.Link == null)
        {
            return _runtime.ToolPath(path);
        }

        var relative = full.Length > best.Root.Length ? full[(best.Root.Length + 1)..] : string.Empty;
        return char.ToUpperInvariant(best.Letter) + ":\\" + relative.Replace('/', '\\');
    }

    public void Dispose()
    {
        if (_disposed || !_runtime.UsesWine)
        {
            return;
        }

        _disposed = true;
        lock (Gate)
        {
            foreach (var map in _maps)
            {
                try
                {
                    File.Delete(map.Link);
                }
                catch (Exception)
                {
                }
            }

            _maps.Clear();
            _active--;
        }
    }

    private static bool Inside(string child, string root) =>
        child.Equals(root, StringComparison.Ordinal) || child.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    /// <summary>Xoá ổ m:…y: còn sót (ứng dụng bị tắt ngang) khi không còn tác vụ PS4 nào chạy.</summary>
    private static void CleanupStale(Ps4Runtime runtime)
    {
        var devices = Path.Combine(runtime.WinePrefix!, "dosdevices");
        if (!Directory.Exists(devices))
        {
            return;
        }

        foreach (var letter in Letters)
        {
            var link = Path.Combine(devices, letter + ":");
            try
            {
                if (new FileInfo(link).LinkTarget != null)
                {
                    File.Delete(link);
                }
            }
            catch (Exception)
            {
            }
        }
    }
}

/// <summary>
/// Publishing engine của PS4 Fake PKG Tools 3.87 (CyB1K) kèm ứng dụng trong thư mục <c>orbis-pub</c>. orbis-pub-cmd.exe là chương
/// trình Windows 32-bit: Windows chạy thẳng, macOS chạy qua Wine kèm theo (giữ thư viện i386), Linux dùng Wine của distro có hỗ trợ
/// 32-bit. Dùng WINEPREFIX riêng (<c>wine-prefix-ps4</c>) để không đụng tới môi trường của SDK Sony (PS5).
/// </summary>
public static class Ps4Engine
{
    public const string FolderName = "orbis-pub";
    public const string ToolFileName = "orbis-pub-cmd.exe";
    public const string FastToolFileName = "orbis-pub-cmd-fast.exe";
    public const string EnvironmentVariable = "PSVIETHOA_ORBIS_PUB";

    /// <summary>SHA-256 của engine gốc (digest đầy đủ + kiểm tra toàn vẹn) và bản nhanh của Ngô Phi Phương (FAST_ENGINE_PATCH.json).</summary>
    public const string OriginalHash = "9d136a27a0739555329e46b61208b7105d4c5aea5644cbf8052bfed4250d0e1b";
    public const string FastHash = "9ff56997d84f743dae1c4bcd8769ab9721bf40da471c1e99d6b9fd0f0c3d48fb";

    /// <summary>Bộ cài Visual C++ x86 kèm bản Windows (thư mục redist): 2008 SP1 cho orbis-pub-cmd.exe, 2015-2022 cho zlib1.dll.</summary>
    public const string Vc2008RedistFileName = "vcredist_x86_2008.exe";
    public const string Vc2015RedistFileName = "vc_redist.x86.exe";

    private static readonly SemaphoreSlim PrefixGate = new(1, 1);

    /// <summary>
    /// Windows: thư viện Visual C++ x86 mà engine PS4 cần còn thiếu — "2008" (Microsoft.VC90.CRT trong WinSxS, cho orbis-pub-cmd.exe)
    /// và/hoặc "2015" (vcruntime140.dll 32-bit, cho zlib1.dll). Rỗng trên macOS/Linux (Wine có sẵn bản tương đương).
    /// </summary>
    public static IReadOnlyList<string> MissingVcRuntimes()
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        var missing = new List<string>();
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        try
        {
            var sxs = Path.Combine(windows, "WinSxS");
            if (!Directory.Exists(sxs) || !Directory.EnumerateDirectories(sxs, "x86_microsoft.vc90.crt_1fc8b3b9a1e18e3b_9.0.*").Any())
            {
                missing.Add("2008");
            }
        }
        catch (Exception)
        {
            missing.Add("2008");
        }

        var system32Bit = Path.Combine(windows, Environment.Is64BitOperatingSystem ? "SysWOW64" : "System32");
        if (!File.Exists(Path.Combine(system32Bit, "vcruntime140.dll")) || !File.Exists(Path.Combine(system32Bit, "msvcp140.dll")))
        {
            missing.Add("2015");
        }

        return missing;
    }

    /// <summary>Cài các bản Visual C++ x86 còn thiếu bằng bộ cài kèm ứng dụng (im lặng, một hộp UAC mỗi bộ). True khi đã đủ.</summary>
    public static bool InstallVcRuntimes(Action<string> log, TimeSpan timeout)
    {
        foreach (var version in MissingVcRuntimes())
        {
            var name = version == "2008" ? Vc2008RedistFileName : Vc2015RedistFileName;
            var installer = SonySdkToolchain.CandidateDirectories("redist").Select(d => Path.Combine(d, name)).FirstOrDefault(File.Exists);
            if (installer == null)
            {
                log(Loc.F("Ps4.Engine.VcRedistMissing", name));
                continue;
            }

            log(Loc.F("Ps4.Engine.VcInstalling", version));
            try
            {
                using var process = Process.Start(new ProcessStartInfo(installer)
                {
                    UseShellExecute = true,
                    Verb = "runas",
                    Arguments = version == "2008" ? "/q" : "/install /quiet /norestart",
                    WindowStyle = ProcessWindowStyle.Hidden,
                });
                if (process == null || !process.WaitForExit(timeout))
                {
                    continue;
                }

                log(Loc.F("Sdk.VcRedistExit", process.ExitCode));
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                log(Loc.T("Sdk.VcRedistCancelled"));
            }
            catch (Exception ex)
            {
                log(ex.Message);
            }
        }

        return MissingVcRuntimes().Count == 0;
    }

    /// <summary>Thư mục chứa orbis-pub-cmd.exe, null nếu không kèm theo.</summary>
    public static string? FindTools()
    {
        var fromEnvironment = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(fromEnvironment) && File.Exists(Path.Combine(fromEnvironment, ToolFileName)))
        {
            return Path.GetFullPath(fromEnvironment);
        }

        return SonySdkToolchain.CandidateDirectories(FolderName).FirstOrDefault(directory => File.Exists(Path.Combine(directory, ToolFileName)));
    }

    /// <summary>WINEPREFIX riêng của PS4 (cạnh prefix của SDK Sony).</summary>
    public static string WinePrefixPath() =>
        Path.Combine(Path.GetDirectoryName(SonySdkToolchain.WinePrefixPath())!, "wine-prefix-ps4");

    /// <summary>Đủ điều kiện chạy engine PS4 chưa. Trả về môi trường chạy, hoặc null kèm lý do (đã dịch).</summary>
    public static Ps4Runtime? Resolve(out string? problem)
    {
        problem = null;
        var directory = FindTools();
        if (directory == null)
        {
            problem = Loc.T("Ps4.Engine.Missing");
            return null;
        }

        if (OperatingSystem.IsWindows())
        {
            return new Ps4Runtime(directory, null, null);
        }

        var wine = SonySdkToolchain.FindWine();
        if (wine == null)
        {
            problem = Loc.T(OperatingSystem.IsMacOS() ? "Ps4.Engine.WineMissingMac" : "Ps4.Engine.WineMissing");
            return null;
        }

        if (OperatingSystem.IsMacOS() && RuntimeInformation.OSArchitecture == Architecture.Arm64 && !SonySdkToolchain.RosettaInstalled)
        {
            problem = Loc.T("Sdk.RosettaMissing");
            return null;
        }

        return new Ps4Runtime(directory, wine, WinePrefixPath());
    }

    /// <summary>
    /// Wine chắc chắn thiếu thư viện Windows 32-bit (lib/wine có x86_64-windows mà không có i386-windows) — orbis-pub-cmd là
    /// chương trình 32-bit nên không chạy được. Không xác định được bố cục (Wine của distro) thì coi như có.
    /// </summary>
    public static bool Wine32Missing(string winePath)
    {
        try
        {
            var resolved = new FileInfo(winePath).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? Path.GetFullPath(winePath);
            var lib = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(resolved)!, "..", "lib", "wine"));
            return Directory.Exists(Path.Combine(lib, "x86_64-windows")) && !Directory.Exists(Path.Combine(lib, "i386-windows"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Tạo WINEPREFIX của PS4 nếu chưa có (lần đầu ~10-30 giây). Không làm gì trên Windows.</summary>
    public static async Task EnsurePrefixAsync(Ps4Runtime runtime, Action<string> log, CancellationToken token)
    {
        if (!runtime.UsesWine || runtime.WinePrefix == null || File.Exists(Path.Combine(runtime.WinePrefix, "system.reg")))
        {
            return;
        }

        await PrefixGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (File.Exists(Path.Combine(runtime.WinePrefix, "system.reg")))
            {
                return;
            }

            log(Loc.F("Ps4.Engine.PrefixCreating", runtime.WinePrefix));
            Directory.CreateDirectory(runtime.WinePrefix);
            var watch = Stopwatch.StartNew();
            using var process = Process.Start(WineStartInfo(runtime, runtime.WinePrefix, ["wineboot.exe", "--init"])) ?? throw new InvalidOperationException(Loc.F("Ps4.Engine.PrefixFailed", -1));

            // Đọc hết đầu ra: wineserver giữ ống tới khi ghi xong registry, nên chờ EOF cũng là chờ prefix hoàn tất (như SDK Sony).
            var stdout = process.StandardOutput.ReadToEndAsync(token);
            var stderr = process.StandardError.ReadToEndAsync(token);
            await process.WaitForExitAsync(token).ConfigureAwait(false);
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            for (var i = 0; i < 60 && !File.Exists(Path.Combine(runtime.WinePrefix, "system.reg")); i++)
            {
                await Task.Delay(500, token).ConfigureAwait(false);
            }

            if (process.ExitCode != 0 || !File.Exists(Path.Combine(runtime.WinePrefix, "system.reg")))
            {
                throw new InvalidOperationException(Loc.F("Ps4.Engine.PrefixFailed", process.ExitCode));
            }

            log(Loc.F("Ps4.Engine.PrefixCreated", Formatters.Duration(watch.Elapsed)));
        }
        finally
        {
            PrefixGate.Release();
        }
    }

    /// <summary>Lệnh chạy một chương trình Windows: thẳng trên Windows, <c>wine &lt;exe&gt; …</c> nơi khác.</summary>
    internal static ProcessStartInfo StartInfo(Ps4Runtime runtime, string executable, IEnumerable<string> arguments)
    {
        if (!runtime.UsesWine)
        {
            var info = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(executable)!,
            };
            foreach (var argument in arguments)
            {
                info.ArgumentList.Add(argument);
            }

            return info;
        }

        return WineStartInfo(runtime, Path.GetDirectoryName(executable)!, new[] { executable }.Concat(arguments));
    }

    private static ProcessStartInfo WineStartInfo(Ps4Runtime runtime, string workingDirectory, IEnumerable<string> arguments)
    {
        var info = new ProcessStartInfo(runtime.WinePath!)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = workingDirectory,
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        info.Environment["WINEPREFIX"] = runtime.WinePrefix!;
        info.Environment["WINEDEBUG"] = "-all";
        info.Environment["WINEDLLOVERRIDES"] = "mscoree,mshtml=;winemenubuilder.exe=d";
        info.Environment["MVK_CONFIG_LOG_LEVEL"] = "0";
        return info;
    }

    /// <summary>Dừng mọi tiến trình còn chạy trong WINEPREFIX của PS4 (sau khi huỷ).</summary>
    internal static void StopWineServer(Ps4Runtime runtime)
    {
        if (!runtime.UsesWine)
        {
            return;
        }

        try
        {
            var server = Path.Combine(Path.GetDirectoryName(runtime.WinePath!)!, "wineserver");
            if (!File.Exists(server))
            {
                return;
            }

            var info = new ProcessStartInfo(server, "-k") { UseShellExecute = false, CreateNoWindow = true };
            info.Environment["WINEPREFIX"] = runtime.WinePrefix!;
            using var process = Process.Start(info);
            process?.WaitForExit(10_000);
        }
        catch (Exception)
        {
        }
    }

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    /// <summary>
    /// Chọn engine theo chế độ: nhanh (bỏ digest đầy đủ, bỏ kiểm tra toàn vẹn gói tham chiếu và quét delta base) hoặc đầy đủ. Ghi
    /// engine.json vào thư mục tác vụ như bản Studio.
    /// </summary>
    internal static string Select(string tool, bool fast, Ps4JobContext job)
    {
        var hash = Hash(tool);
        var selected = tool;
        if (fast && hash == OriginalHash)
        {
            var accelerated = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(tool))!, FastToolFileName);
            if (!File.Exists(accelerated) || Hash(accelerated) != FastHash)
            {
                throw new InvalidDataException(Loc.T("Ps4.Engine.FastMissing"));
            }

            selected = accelerated;
            hash = FastHash;
        }
        else if (!fast && hash == FastHash)
        {
            var original = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(tool))!, ToolFileName);
            if (!File.Exists(original) || Hash(original) != OriginalHash)
            {
                throw new InvalidDataException(Loc.T("Ps4.Engine.OriginalMissing"));
            }

            selected = original;
            hash = OriginalHash;
        }

        var skipsInternal = hash == FastHash;
        job.Save("engine.json", new
        {
            path = Path.GetFullPath(selected),
            sha256 = hash,
            fast_mode = fast,
            skip_full_digest = fast,
            skip_reference_integrity = skipsInternal,
            skip_base_delta_scan = skipsInternal,
            native_process_runs_to_completion = true,
        });
        job.Log(Loc.F("Ps4.Engine.Selected", Path.GetFullPath(selected), hash));
        job.Log(Loc.T(fast ? skipsInternal ? "Ps4.Engine.ModeFast" : "Ps4.Engine.ModeFastCustom" : "Ps4.Engine.ModeFull"));
        return selected;
    }
}
