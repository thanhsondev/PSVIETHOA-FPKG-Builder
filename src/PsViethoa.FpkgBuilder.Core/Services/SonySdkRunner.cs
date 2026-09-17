using System.Diagnostics;
using System.Text;
using PsViethoa.FpkgBuilder.Core.Localization;
using PsViethoa.FpkgBuilder.Core.Models;

namespace PsViethoa.FpkgBuilder.Core.Services;

/// <summary>Tiến độ của prospero-pub-cmd.exe: phần trăm theo thanh "=" của SDK và số byte gói thô đã ghi.</summary>
public readonly record struct SonySdkProgress(double Percent, long BytesWritten);

/// <summary>Kết quả một lần chạy img_create: mã thoát, các dòng "[Error]" và dòng lệnh đã chạy (để ghi 02-img-create.log).</summary>
public sealed record SonySdkImageResult(int ExitCode, IReadOnlyList<string> Errors, string CommandLine)
{
    public bool Succeeded => ExitCode == 0 && Errors.Count == 0;
}

/// <summary>
/// Chạy prospero-pub-cmd.exe (trực tiếp trên Windows, qua Wine nơi khác), chuyển nhật ký "[Info]/[Warn]/[Error]" của SDK
/// sang nhật ký ứng dụng và đọc thanh tiến trình của SDK theo từng ký tự: SDK in thước "|____.____|…" rồi thêm dần 51 dấu "="
/// ngay cả khi đầu ra bị chuyển hướng (không có ký tự xuống dòng cho tới khi xong).
/// </summary>
public static class SonySdkRunner
{
    /// <summary>Số dấu "=" của thanh tiến trình đầy (thước "0 … 100" rộng 51 cột).</summary>
    public const int ProgressBarWidth = 51;

    /// <summary>Tạo WINEPREFIX riêng nếu chưa có (lần đầu mất ~20-30 giây). Không làm gì trên Windows.</summary>
    public static async Task EnsureWinePrefixAsync(SonySdkRuntime runtime, Action<LogEntry> log, CancellationToken cancellationToken)
    {
        if (!runtime.UsesWine || runtime.WinePrefix == null)
        {
            return;
        }

        if (File.Exists(Path.Combine(runtime.WinePrefix, "system.reg")))
        {
            return;
        }

        log(new LogEntry(LogLevel.Info, Loc.F("Sdk.WinePrefixCreating", runtime.WinePrefix)));
        Directory.CreateDirectory(runtime.WinePrefix);
        var watch = Stopwatch.StartNew();
        var exit = await RunAsync(runtime, ["wineboot.exe", "--init"], runtime.WinePrefix, _ => { }, null, cancellationToken, wineCommand: true)
            .ConfigureAwait(false);
        if (exit != 0 || !File.Exists(Path.Combine(runtime.WinePrefix, "system.reg")))
        {
            throw new InvalidOperationException(Loc.F("Sdk.WinePrefixFailed", exit));
        }

        log(new LogEntry(LogLevel.Info, Loc.F("Sdk.WinePrefixCreated", Formatters.Duration(watch.Elapsed))));
    }

    /// <summary>
    /// <c>img_create --oformat nwonly [--compression_level N] project.gp5 raw.pkg</c>. Không ném lỗi theo mã thoát: người gọi xem
    /// <see cref="SonySdkImageResult.Succeeded"/> (bản gốc coi cả "[Error]" trong đầu ra là thất bại) và dùng
    /// <see cref="Describe"/> để nêu lý do.
    /// </summary>
    public static async Task<SonySdkImageResult> CreateImageAsync(
        SonySdkRuntime runtime,
        Func<string, string> toolPath,
        string projectPath,
        string rawPackagePath,
        int? compressionLevel,
        Action<LogEntry> log,
        Action<SonySdkProgress> progress,
        CancellationToken cancellationToken)
    {
        var arguments = new List<string> { "img_create", "--oformat", "nwonly" };
        if (compressionLevel is { } level)
        {
            arguments.Add("--compression_level");
            arguments.Add(level.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        arguments.Add(toolPath(projectPath));
        arguments.Add(toolPath(rawPackagePath));

        var errors = new List<string>();
        var parser = new OutputParser(line =>
        {
            var entry = Classify(line);
            if (entry == null)
            {
                return;
            }

            if (entry.Level == LogLevel.Error)
            {
                errors.Add(entry.Message);
            }

            log(entry);
        });

        var bytesWritten = 0L;
        var percent = 0.0;
        void Report() => progress(new SonySdkProgress(percent, bytesWritten));
        parser.BarAdvanced = count =>
        {
            percent = Math.Min(100, count * 100.0 / ProgressBarWidth);
            Report();
        };

        using var sizeWatch = new Timer(_ =>
        {
            try
            {
                var info = new FileInfo(rawPackagePath);
                if (info.Exists && info.Length != bytesWritten)
                {
                    bytesWritten = info.Length;
                    Report();
                }
            }
            catch (IOException)
            {
            }
        }, null, TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(500));

        // Thư mục làm việc phải ASCII như mọi đường dẫn khác đưa cho SDK; thư mục toolchain của bộ công cụ là lựa chọn đầu.
        var workingDirectory = new[] { runtime.Directory, Path.GetTempPath(), Path.GetDirectoryName(Path.GetFullPath(projectPath))! }
            .First(folder => SonySdkPathAliases.IsAsciiSafe(folder) && Directory.Exists(folder));
        var exit = await RunAsync(runtime, arguments, workingDirectory, parser.Feed, parser, cancellationToken).ConfigureAwait(false);
        parser.Flush();

        var result = new SonySdkImageResult(exit, errors, string.Join(' ', arguments.Select(Quote)));
        if (result.Succeeded && !File.Exists(rawPackagePath))
        {
            result = result with { Errors = [Loc.T("Sdk.NoOutput")] };
        }

        if (result.Succeeded)
        {
            percent = 100;
            bytesWritten = new FileInfo(rawPackagePath).Length;
            Report();
        }

        return result;
    }

    /// <summary>
    /// Chạy một chương trình khác của bộ công cụ (prospero-dds2png.exe…) với cùng môi trường (Wine trên macOS); trả về mã thoát
    /// và toàn bộ đầu ra.
    /// </summary>
    public static async Task<(int ExitCode, string Output)> RunToolAsync(SonySdkRuntime runtime, string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var output = new StringBuilder();
        var parser = new OutputParser(line => { lock (output) { output.AppendLine(line); } });
        var workingDirectory = new[] { runtime.Directory, Path.GetTempPath() }.First(folder => SonySdkPathAliases.IsAsciiSafe(folder) && Directory.Exists(folder));
        var exit = await RunAsync(runtime, arguments, workingDirectory, parser.Feed, parser, cancellationToken, executable: executable).ConfigureAwait(false);
        parser.Flush();
        return (exit, output.ToString());
    }

    /// <summary>Lý do thất bại (đã dịch) của một lần chạy img_create.</summary>
    public static string Describe(SonySdkImageResult result) =>
        result.Errors.Count > 0
            ? string.Join(" · ", result.Errors.Take(3))
            : Loc.F("Sdk.ExitCode", result.ExitCode, ExitCodeHint(result.ExitCode));

    /// <summary>Đối số có khoảng trắng đặt trong nháy kép như build-from-folder.ps1 ghi vào nhật ký.</summary>
    private static string Quote(string argument) =>
        argument.Length > 0 && !argument.Any(char.IsWhiteSpace) ? argument : "\"" + argument + "\"";

    /// <summary>Mã thoát đặc biệt của Windows: thiếu DLL (thường là Visual C++ runtime).</summary>
    private static string ExitCodeHint(int exit) =>
        unchecked((uint)exit) switch
        {
            0xC0000135 => Loc.T("Sdk.HintDllMissing"),
            0xC000007B => Loc.T("Sdk.HintBadImage"),
            _ => string.Empty,
        };

    /// <summary>Phân loại một dòng đầu ra; null = bỏ qua (tiếng ồn của Wine/MoltenVK, dòng trống, thước tiến trình).</summary>
    public static LogEntry? Classify(string line)
    {
        var text = line.Trim();
        if (text.Length == 0 || text.StartsWith("|____", StringComparison.Ordinal) || text.StartsWith("0        20", StringComparison.Ordinal))
        {
            return null;
        }

        static string Body(string value, int prefix) => value[prefix..].Trim();

        if (text.StartsWith("[Error]", StringComparison.OrdinalIgnoreCase))
        {
            return new LogEntry(LogLevel.Error, "SDK: " + Body(text, 7));
        }

        if (text.StartsWith("[Warn]", StringComparison.OrdinalIgnoreCase))
        {
            var body = Body(text, 6);
            return new LogEntry(IsRoutineWarning(body) ? LogLevel.Info : LogLevel.Warning, "SDK: " + body);
        }

        if (text.StartsWith("[Info]", StringComparison.OrdinalIgnoreCase))
        {
            return new LogEntry(LogLevel.Info, "SDK: " + Body(text, 6));
        }

        if (text.StartsWith("[Debug]", StringComparison.OrdinalIgnoreCase))
        {
            return new LogEntry(LogLevel.Info, "SDK: " + Body(text, 7));
        }

        if (text.StartsWith("Creating an image", StringComparison.OrdinalIgnoreCase))
        {
            return new LogEntry(LogLevel.Info, "SDK: " + text);
        }

        // Wine in lỗi nạp thư viện / chương trình theo dạng "wine: …" hoặc "0024:err:…".
        if (text.StartsWith("wine:", StringComparison.OrdinalIgnoreCase) || text.Contains(":err:", StringComparison.Ordinal))
        {
            return new LogEntry(LogLevel.Warning, text);
        }

        return null;
    }

    /// <summary>
    /// Cảnh báo SDK luôn in ra với bộ công cụ này và không nói gì về gói: thiếu bộ SDK đầy đủ, kiểm tra phiên bản trực tuyến,
    /// gói không dựng để nộp Sony. Ghi ở mức thông tin để cảnh báo thật không bị lẫn.
    /// </summary>
    private static readonly string[] RoutineWarnings =
    [
        "The version of the SDK toolchain is invalid or missing",
        "(online check)",
        "has not been built for submission",
        "- Perform package verification",
        "- Rebuild the package for submission",
        "Process finished with warning(s)",
    ];

    public static bool IsRoutineWarning(string message) =>
        RoutineWarnings.Any(pattern => message.Contains(pattern, StringComparison.OrdinalIgnoreCase));

    private static async Task<int> RunAsync(
        SonySdkRuntime runtime,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        Action<string> onStderrLine,
        OutputParser? stdoutParser,
        CancellationToken cancellationToken,
        bool wineCommand = false,
        string? executable = null)
    {
        executable ??= runtime.PublisherPath;
        var info = new ProcessStartInfo
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        if (runtime.UsesWine)
        {
            info.FileName = runtime.WinePath!;
            if (!wineCommand)
            {
                info.ArgumentList.Add(runtime.ToolPath(executable));
            }

            info.Environment["WINEPREFIX"] = runtime.WinePrefix!;
            info.Environment["WINEDEBUG"] = "-all";
            // Không cài Mono/Gecko, không tạo lối tắt / liên kết tệp của Wine trên máy người dùng.
            info.Environment["WINEDLLOVERRIDES"] = "mscoree,mshtml=;winemenubuilder.exe=d";
            info.Environment["MVK_CONFIG_LOG_LEVEL"] = "0";
        }
        else
        {
            info.FileName = executable;
        }

        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = info };
        process.Start();
        process.StandardInput.Close();

        var stdout = Task.Run(async () =>
        {
            var buffer = new char[256];
            var reader = process.StandardOutput;
            int read;
            while ((read = await reader.ReadAsync(buffer.AsMemory(), CancellationToken.None).ConfigureAwait(false)) > 0)
            {
                stdoutParser?.Feed(buffer.AsSpan(0, read));
            }
        }, CancellationToken.None);

        var stderr = Task.Run(async () =>
        {
            string? line;
            while ((line = await process.StandardError.ReadLineAsync(CancellationToken.None).ConfigureAwait(false)) != null)
            {
                onStderrLine(line);
            }
        }, CancellationToken.None);

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception)
            {
            }

            if (runtime.UsesWine)
            {
                StopWineServer(runtime);
            }

            throw;
        }

        await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        return process.ExitCode;
    }

    /// <summary>Dừng mọi tiến trình Windows còn chạy trong WINEPREFIX của ứng dụng (sau khi huỷ).</summary>
    private static void StopWineServer(SonySdkRuntime runtime)
    {
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

    /// <summary>Ghép đầu ra theo ký tự: đếm dấu "=" sau thước tiến trình, gom phần còn lại thành dòng.</summary>
    private sealed class OutputParser(Action<string> onLine)
    {
        private readonly StringBuilder _line = new();
        private readonly object _gate = new();
        private bool _barActive;
        private int _barCount;

        public Action<int>? BarAdvanced { get; set; }

        public void Feed(string line)
        {
            lock (_gate)
            {
                onLine(line);
            }
        }

        public void Feed(ReadOnlySpan<char> chunk)
        {
            lock (_gate)
            {
                foreach (var c in chunk)
                {
                    if (c is '\r' or '\n')
                    {
                        EndLine();
                        continue;
                    }

                    if (_barActive && c == '=')
                    {
                        _barCount++;
                        BarAdvanced?.Invoke(_barCount);
                        continue;
                    }

                    _line.Append(c);
                }
            }
        }

        public void Flush()
        {
            lock (_gate)
            {
                EndLine();
            }
        }

        private void EndLine()
        {
            var text = _line.ToString();
            _line.Clear();
            if (text.TrimStart().StartsWith("|____", StringComparison.Ordinal))
            {
                _barActive = true;
                _barCount = 0;
                return;
            }

            if (_barActive && _barCount > 0)
            {
                _barActive = false;
            }

            if (text.Trim().Length > 0)
            {
                onLine(text);
            }
        }
    }
}
