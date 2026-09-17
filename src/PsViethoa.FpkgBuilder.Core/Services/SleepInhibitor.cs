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

    /// <summary>Đường dẫn systemd-inhibit trên máy này, hoặc null.</summary>
    public static string? FindSystemdInhibit()
    {
        foreach (var candidate in new[] { "/usr/bin/systemd-inhibit", "/bin/systemd-inhibit", "/usr/local/bin/systemd-inhibit" })
        {
            try
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (Exception)
            {
            }
        }

        return null;
    }

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

            if (OperatingSystem.IsLinux() && FindSystemdInhibit() is { } systemdInhibit)
            {
                // systemd-inhibit giữ khoá trong lúc tiến trình con còn sống; "sleep infinity" sống tới khi bị kết thúc
                // ở Dispose. --what=idle:sleep chặn cả tự ngủ do rảnh lẫn lệnh ngủ.
                var start = new ProcessStartInfo(systemdInhibit)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,

                    // Do NOT redirect: the pipes would be inherited by the long-lived "sleep infinity" grandchild,
                    // so anything reading them to the end would block until the build finishes.
                    RedirectStandardOutput = false,
                    RedirectStandardError = false,
                };
                start.ArgumentList.Add("--what=idle:sleep");
                start.ArgumentList.Add("--who=PSVIETHOA FPKG Builder");
                start.ArgumentList.Add("--why=Building an FPKG package");
                start.ArgumentList.Add("--mode=block");
                start.ArgumentList.Add("sleep");
                start.ArgumentList.Add("infinity");

                var process = Process.Start(start);
                if (process != null)
                {
                    // Process.Start succeeding says nothing: systemd-inhibit exits in a few ms when it cannot take
                    // the lock or exec its child, and HasExited is still false at that instant. Give it a moment and
                    // check it is genuinely running before claiming the machine will stay awake.
                    if (!process.WaitForExit(250) && !process.HasExited)
                    {
                        inhibitor._caffeinate = process;
                        inhibitor.Mechanism = "systemd-inhibit";
                        return inhibitor;
                    }

                    process.Dispose();
                }
            }
        }
        catch (Exception)
        {
            // Không ngăn được máy ngủ thì vẫn tiếp tục tạo gói.
        }

        return null;
    }

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
                // systemd-inhibit sinh tiến trình con ("sleep infinity") giữ khoá; giết cả cây, nếu không
                // tiến trình con mồ côi vẫn chặn máy ngủ sau khi tạo gói xong.
                _caffeinate.Kill(entireProcessTree: true);
                _caffeinate.WaitForExit(5000);
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
