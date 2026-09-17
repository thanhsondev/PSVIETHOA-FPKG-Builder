using PsViethoa.FpkgBuilder.Core.Services;
using Xunit;

namespace PsViethoa.FpkgBuilder.Tests;

/// <summary>Kiểm thử các nhánh riêng của Linux: ngăn ngủ, thư mục tạm, gợi ý tệp cập nhật.</summary>
public sealed class LinuxPlatformTests
{
    [Fact]
    public void SleepInhibitor_UsesSystemdInhibitOnLinux()
    {
        using var inhibitor = SleepInhibitor.TryAcquire();

        if (OperatingSystem.IsLinux() && SleepInhibitor.FindSystemdInhibit() != null)
        {
            Assert.NotNull(inhibitor);
            Assert.Equal("systemd-inhibit", inhibitor!.Mechanism);
        }
        else if (!OperatingSystem.IsLinux())
        {
            // macOS/Windows giữ nguyên cơ chế cũ.
            Assert.True(inhibitor == null || inhibitor.Mechanism != "systemd-inhibit");
        }
    }

    /// <summary>
    /// Khoá phải thực sự được systemd ghi nhận, không chỉ là "tiến trình con đã chạy": systemd-inhibit thoát sau
    /// vài mili-giây khi không lấy được khoá, nên kiểm tra HasExited ngay lập tức sẽ báo thành công giả.
    /// Khoá xuất hiện trong systemd-inhibit --list sau khoảng 15–25 ms nên phải chờ có giới hạn.
    /// </summary>
    [Fact]
    public void SleepInhibitor_RegistersARealLockWithSystemd()
    {
        if (!OperatingSystem.IsLinux() || SleepInhibitor.FindSystemdInhibit() == null)
        {
            return;
        }

        const string who = "PSVIETHOA FPKG Builder";

        using (var inhibitor = SleepInhibitor.TryAcquire())
        {
            Assert.NotNull(inhibitor);
            Assert.Equal("systemd-inhibit", inhibitor!.Mechanism);

            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            var registered = false;
            while (!registered && DateTime.UtcNow < deadline)
            {
                registered = ListInhibitors().Contains(who, StringComparison.OrdinalIgnoreCase);
                if (!registered)
                {
                    Thread.Sleep(25);
                }
            }

            Assert.True(registered, "systemd never registered the inhibitor lock — the machine would still sleep mid-build.");
        }

        // Dispose phải nhả khoá và không để lại tiến trình "sleep infinity" mồ côi.
        var released = false;
        var releaseDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!released && DateTime.UtcNow < releaseDeadline)
        {
            released = !ListInhibitors().Contains(who, StringComparison.OrdinalIgnoreCase);
            if (!released)
            {
                Thread.Sleep(25);
            }
        }

        Assert.True(released, "The inhibitor lock survived Dispose — suspend would stay blocked after the build.");
    }

    private static string ListInhibitors()
    {
        try
        {
            var start = new System.Diagnostics.ProcessStartInfo(SleepInhibitor.FindSystemdInhibit()!)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            start.ArgumentList.Add("--list");

            using var process = System.Diagnostics.Process.Start(start);
            if (process == null)
            {
                return string.Empty;
            }

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);
            return output;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    [Fact]
    public void SleepInhibitor_DisposeIsIdempotent()
    {
        var inhibitor = SleepInhibitor.TryAcquire();
        inhibitor?.Dispose();
        inhibitor?.Dispose();
    }

    [Fact]
    public void FindSystemdInhibit_IsNullOffLinux()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Null(SleepInhibitor.FindSystemdInhibit());
        }
    }

    /// <summary>
    /// /tmp thường là tmpfs (RAM) trên Linux — thư mục tạm mặc định phải nằm trong cache XDG trên đĩa thật,
    /// nếu không ảnh giải nén hàng chục GB sẽ ăn hết bộ nhớ.
    /// </summary>
    [Fact]
    public void SuggestTemporaryFolder_AvoidsTmpfsOnLinux()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // Thư mục xuất nằm trên ổ hệ thống → rơi vào nhánh dự phòng theo nền tảng.
        var suggestion = BuildPreparer.SuggestTemporaryFolder("/");

        Assert.False(
            suggestion.StartsWith("/tmp/", StringComparison.Ordinal),
            $"The default temp folder must not live under /tmp (usually tmpfs): {suggestion}");
        Assert.Contains("psviethoa-fpkg-builder", suggestion, StringComparison.Ordinal);
    }

    /// <summary>
    /// Điểm gắn của ổ chứa thư mục xuất thường thuộc root (ví dụ /home): gợi ý phải là nơi ghi được thật,
    /// không phải gốc ổ — trước đây sinh ra "/home/fpkg-temp" rồi hỏng với "Access to the path is denied".
    /// </summary>
    [Fact]
    public void SuggestTemporaryFolder_IsActuallyWritable()
    {
        var output = Path.Combine(Path.GetTempPath(), "psviethoa-out-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(output);
        try
        {
            var suggestion = BuildPreparer.SuggestTemporaryFolder(output);
            Assert.False(string.IsNullOrWhiteSpace(suggestion));

            // Phải tạo được thật, không chỉ là một chuỗi đường dẫn hợp lệ.
            Directory.CreateDirectory(suggestion);
            var probe = Path.Combine(suggestion, "probe.txt");
            File.WriteAllText(probe, "ok");
            Assert.Equal("ok", File.ReadAllText(probe));
            File.Delete(probe);
        }
        finally
        {
            try
            {
                Directory.Delete(output, recursive: true);
            }
            catch (Exception)
            {
            }
        }
    }

    [Fact]
    public void PlatformAssetHint_IsSetOnLinux()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // Rỗng nghĩa là bộ kiểm tra cập nhật không khớp được tệp phát hành nào.
        var hint = UpdateChecker.PlatformAssetHint();
        Assert.False(string.IsNullOrWhiteSpace(hint));
        Assert.StartsWith("linux-", hint, StringComparison.Ordinal);

        var token = UpdateChecker.PreferredAssetToken();
        Assert.Contains(token, new[] { ".tar.gz", ".AppImage" });
        Assert.Equal(UpdateChecker.IsRunningFromAppImage ? ".AppImage" : ".tar.gz", token);
    }
}
