using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PsViethoa.FpkgBuilder.Core.Services;

/// <summary>Ngăn hệ điều hành ngủ trong lúc tạo gói (caffeinate trên macOS, SetThreadExecutionState trên Windows, systemd-inhibit trên Linux).</summary>
public sealed partial class SleepInhibitor : IDisposable
{
    private Process? _caffeinate;
    private Thread? _windowsThread;
    private ManualResetEventSlim? _release;
    private bool _disposed;

    private SleepInhibitor()
    {
    }

    /// <summary>Mô tả cơ chế đang dùng, để ghi nhật ký.</summary>
    public string Mechanism { get; private set; } = "không hỗ trợ";

    public static SleepInhibitor? TryAcquire()
    {
        var inhibitor = new SleepInhibitor();
        try
        {
            if (OperatingSystem.IsMacOS())
            {
                inhibitor._caffeinate = Process.Start(new ProcessStartInfo("caffeinate", $"-i -w {Environment.ProcessId}")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = false,
                    RedirectStandardError = false,
                });
                inhibitor.Mechanism = "caffeinate";
                return inhibitor;
            }

            if (OperatingSystem.IsWindows())
            {
                inhibitor.StartWindowsThread();
                inhibitor.Mechanism = "SetThreadExecutionState";
                return inhibitor;
            }

            if (OperatingSystem.IsLinux() && File.Exists(SystemdInhibitPath))
            {
                // Giữ khoá "idle:sleep" của systemd-logind chừng nào tiến trình con còn sống; Dispose giết cả cây tiến trình.
                var info = new ProcessStartInfo(SystemdInhibitPath)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = false,
                    RedirectStandardError = false,
                };
                foreach (var argument in new[] { "--what=idle:sleep", "--who=PSVIETHOA FPKG Builder", "--why=Building a PS5 package", "--mode=block", "sleep", "infinity" })
                {
                    info.ArgumentList.Add(argument);
                }

                inhibitor._caffeinate = Process.Start(info);
                inhibitor.Mechanism = "systemd-inhibit";
                return inhibitor;
            }
        }
        catch (Exception)
        {
            // Không ngăn được máy ngủ thì vẫn tiếp tục tạo gói.
        }

        return null;
    }

    public const string SystemdInhibitPath = "/usr/bin/systemd-inhibit";

    private const uint EsContinuous = 0x80000000;
    private const uint EsSystemRequired = 0x00000001;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint SetThreadExecutionState(uint flags);

    private void StartWindowsThread()
    {
        _release = new ManualResetEventSlim(false);
        var release = _release;
        _windowsThread = new Thread(() =>
        {
            try
            {
                SetThreadExecutionState(EsContinuous | EsSystemRequired);
                release.Wait();
            }
            finally
            {
                SetThreadExecutionState(EsContinuous);
            }
        })
        {
            IsBackground = true,
            Name = "PSVIETHOA.SleepInhibitor",
        };
        _windowsThread.Start();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            if (_caffeinate is { HasExited: false })
            {
                _caffeinate.Kill(entireProcessTree: true);
            }

            _caffeinate?.Dispose();
        }
        catch (Exception)
        {
        }

        try
        {
            _release?.Set();
            _windowsThread?.Join(TimeSpan.FromSeconds(2));
            _release?.Dispose();
        }
        catch (Exception)
        {
        }
    }
}
