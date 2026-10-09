using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PsViethoa.FpkgBuilder.Core.Services;

/// <summary>
/// Giới hạn số nhân một tiến trình con (img_create của Publishing Tools) được dùng, đổi được ngay khi đang chạy. Publishing Tools không
/// có tuỳ chọn số luồng — nó tạo một luồng cho mỗi nhân — nên:
/// <list type="bullet">
/// <item>Windows: đặt CPU affinity (áp cho mọi luồng của tiến trình, đổi lại được bất cứ lúc nào).</item>
/// <item>macOS và Linux: điều tiết kiểu cpulimit — đo thời gian CPU tiến trình đã dùng mỗi 100 ms, vượt mức N nhân thì tạm dừng
/// (SIGSTOP) đúng phần dư rồi cho chạy tiếp (SIGCONT). macOS không có API affinity; trên Linux affinity chỉ áp cho luồng chính
/// nên không đổi được giữa chừng. Dưới Wine, tiến trình khởi chạy chính là tiến trình nén (đo 2026-10-09: ~1370 % CPU/15 nhân).</item>
/// </list>
/// Giới hạn ≥ số nhân của máy (mặc định) = không can thiệp, chạy hết tốc độ.
/// </summary>
public sealed class CpuLimiter : IDisposable
{
    private const int PeriodMs = 100;

    private readonly Process _process;
    private readonly Func<int> _limit;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private volatile bool _suspended;

    /// <param name="limit">Số nhân cho phép, đọc lại liên tục (≤ 0 hoặc ≥ số nhân = không giới hạn).</param>
    /// <param name="changed">Gọi khi giới hạn có hiệu lực đổi (để ghi nhật ký): số nhân mới, 0 = không giới hạn.</param>
    public CpuLimiter(Process process, Func<int> limit, Action<int>? changed = null)
    {
        _process = process;
        _limit = limit;
        _loop = Task.Run(() => Run(changed), CancellationToken.None);
    }

    /// <summary>Số nhân logic của máy.</summary>
    public static int CoreCount => Environment.ProcessorCount;

    /// <summary>Giới hạn thực tế: 0 = không giới hạn.</summary>
    public static int Effective(int requested) => requested <= 0 || requested >= CoreCount ? 0 : requested;

    private void Run(Action<int>? changed)
    {
        var applied = -1;
        try
        {
            while (!_stop.IsCancellationRequested && !HasExited())
            {
                var limit = Effective(_limit());
                if (limit != applied)
                {
                    applied = limit;
                    if (OperatingSystem.IsWindows())
                    {
                        SetAffinity(limit);
                    }

                    changed?.Invoke(limit);
                }

                if (limit == 0 || OperatingSystem.IsWindows())
                {
                    Wait(250);
                    continue;
                }

                // Chạy một nhịp, đo CPU đã dùng; vượt ngân sách N nhân thì dừng đủ lâu để trung bình còn N nhân.
                var cpuBefore = CpuTime();
                var watch = Stopwatch.StartNew();
                Wait(PeriodMs);
                var used = (CpuTime() - cpuBefore).TotalSeconds;
                var pause = used / limit - watch.Elapsed.TotalSeconds;
                if (pause > 0.002)
                {
                    Suspend();
                    try
                    {
                        Wait((int)Math.Min(1000, pause * 1000));
                    }
                    finally
                    {
                        Resume();
                    }
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException or PlatformNotSupportedException)
        {
            // Tiến trình đã thoát giữa chừng, hoặc hệ điều hành không cho: thôi điều tiết.
        }
        finally
        {
            Resume();
        }
    }

    private void Wait(int milliseconds) => _stop.Token.WaitHandle.WaitOne(milliseconds);

    private bool HasExited()
    {
        try
        {
            return _process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    private TimeSpan CpuTime()
    {
        _process.Refresh();
        return _process.TotalProcessorTime;
    }

    private void SetAffinity(int limit)
    {
        var count = limit == 0 ? Math.Min(CoreCount, 64) : Math.Min(limit, 64);
        _process.ProcessorAffinity = (nint)(count >= 64 ? -1L : (1L << count) - 1);
    }

    private void Suspend()
    {
        if (!HasExited() && kill(_process.Id, OperatingSystem.IsMacOS() ? 17 : 19) == 0)
        {
            _suspended = true;
        }
    }

    private void Resume()
    {
        if (_suspended)
        {
            _suspended = false;
            try
            {
                kill(_process.Id, OperatingSystem.IsMacOS() ? 19 : 18);
            }
            catch (Exception)
            {
            }
        }
    }

    [DllImport("libc", SetLastError = true)]
#pragma warning disable SYSLIB1054, CA5392
    private static extern int kill(int pid, int signal);
#pragma warning restore SYSLIB1054, CA5392

    public void Dispose()
    {
        _stop.Cancel();
        try
        {
            _loop.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
        }

        Resume();
        _stop.Dispose();
    }
}
