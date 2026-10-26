using System.Diagnostics;
using PsViethoa.FpkgBuilder.Core.Services;
using Xunit;

namespace PsViethoa.FpkgBuilder.Tests;

/// <summary>
/// Đo số nhân CPU một tiến trình dùng chỉ đúng khi máy rảnh: chạy riêng, không song song với các lượt tạo gói thật của lớp khác.
/// </summary>
[CollectionDefinition(nameof(CpuTimingCollection), DisableParallelization = true)]
public sealed class CpuTimingCollection;

/// <summary>
/// Issue #6: Publishing Tools không có tuỳ chọn số luồng, nên ô "Luồng nén" ở chế độ SDK Sony giới hạn img_create từ bên ngoài
/// (<see cref="CpuLimiter"/>): mặc định chạy hết tốc độ, hạ xuống hay tăng lại được ngay khi đang chạy.
/// </summary>
[Collection(nameof(CpuTimingCollection))]
public sealed class SonySdkCoresTests
{
    [Fact]
    public void Effective_TreatsEveryCoreAsUnlimited()
    {
        Assert.Equal(0, CpuLimiter.Effective(0));
        Assert.Equal(0, CpuLimiter.Effective(CpuLimiter.CoreCount));
        Assert.Equal(0, CpuLimiter.Effective(CpuLimiter.CoreCount + 5));
        if (CpuLimiter.CoreCount > 1)
        {
            Assert.Equal(1, CpuLimiter.Effective(1));
        }
    }

    /// <summary>Tiến trình đa luồng thật (xz -T4): hết tốc độ → giới hạn 1 nhân → bỏ giới hạn, đổi ngay khi đang chạy.</summary>
    [Fact]
    public void Limiter_ThrottlesAndReleasesALiveProcess()
    {
        var xz = new[] { "/opt/homebrew/bin/xz", "/usr/local/bin/xz", "/usr/bin/xz", "/bin/xz" }.FirstOrDefault(File.Exists);
        if (OperatingSystem.IsWindows() || xz == null || CpuLimiter.CoreCount < 4)
        {
            return;
        }

        using var process = Process.Start(new ProcessStartInfo("/bin/sh", ["-c", $"exec {xz} -T4 -6 -c < /dev/urandom > /dev/null"]) { UseShellExecute = false })!;
        var limit = 0;
        var changes = new List<int>();
        try
        {
            using (new CpuLimiter(process, () => Volatile.Read(ref limit), value => { lock (changes) { changes.Add(value); } }))
            {
                Thread.Sleep(500);
                var full = Cores(process, 2000);
                Volatile.Write(ref limit, 1);
                Thread.Sleep(500);
                var limited = Cores(process, 3000);
                Volatile.Write(ref limit, 0);
                Thread.Sleep(500);
                var released = Cores(process, 2000);

                Assert.True(full > 2.5, $"unlimited used {full:0.00} cores");
                Assert.True(limited < 1.5, $"limited to 1 core but used {limited:0.00}");
                Assert.True(released > 2.5, $"released but used only {released:0.00} cores");
            }

            lock (changes)
            {
                Assert.Equal([0, 1, 0], changes);
            }
        }
        finally
        {
            process.Kill();
        }
    }

    [Fact]
    public void Limiter_SetsTheAffinityOnWindows()
    {
        if (!OperatingSystem.IsWindows() || CpuLimiter.CoreCount < 2)
        {
            return;
        }

        using var process = Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 30 127.0.0.1 >nul") { UseShellExecute = false, CreateNoWindow = true })!;
        var limit = 1;
        try
        {
            using (new CpuLimiter(process, () => Volatile.Read(ref limit)))
            {
                Thread.Sleep(600);
                Assert.Equal((nint)1, process.ProcessorAffinity);
                Volatile.Write(ref limit, 0);
                Thread.Sleep(600);
                Assert.NotEqual((nint)1, process.ProcessorAffinity);
            }
        }
        finally
        {
            process.Kill();
        }
    }

    /// <summary>Số nhân trung bình tiến trình dùng trong <paramref name="milliseconds"/>.</summary>
    private static double Cores(Process process, int milliseconds)
    {
        process.Refresh();
        var before = process.TotalProcessorTime;
        var watch = Stopwatch.StartNew();
        Thread.Sleep(milliseconds);
        process.Refresh();
        return (process.TotalProcessorTime - before).TotalSeconds / watch.Elapsed.TotalSeconds;
    }
}
