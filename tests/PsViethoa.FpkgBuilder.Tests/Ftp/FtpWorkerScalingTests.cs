using System.Text.RegularExpressions;
using PsViethoa.FpkgBuilder.Core.Ftp;
using PsViethoa.FpkgBuilder.Core.Localization;
using PsViethoa.FpkgBuilder.Core.Services;
using Xunit;

namespace PsViethoa.FpkgBuilder.Tests.Ftp;

/// <summary>
/// Số luồng của <see cref="FtpTransferQueue"/>: tự tăng khi nhiều tệp nhỏ, hạ về <see cref="FtpTransferQueue.HeavyWorkers"/> khi có tệp
/// nặng, và nhớ mức trần khi PS5 từ chối kết nối thay vì làm lỗi tệp.
/// </summary>
public sealed class FtpWorkerScalingTests
{
    private static Dictionary<string, byte[]> SmallTree(string root, int count)
    {
        var files = new Dictionary<string, byte[]>();
        for (var i = 0; i < count; i++)
        {
            var relative = $"part{i % 3}/f{i:D4}.bin";
            var content = FtpTestData.Bytes(512 + i * 7 % 3000, i);
            FtpTestData.Write(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)), content);
            files[relative] = content;
        }

        return files;
    }

    private static void AssertUploaded(FtpSandbox box, string remoteRoot, Dictionary<string, byte[]> files)
    {
        foreach (var (relative, content) in files)
        {
            Assert.Equal(content, File.ReadAllBytes(box.Server.RealPath(remoteRoot + "/" + relative)));
        }
    }

    [Fact]
    public void PlannedWorkers_FollowsTheSmallFilesWaiting()
    {
        var queue = new FtpTransferQueue(new FtpConnectionInfo("127.0.0.1", 1, "anonymous", "")) { Workers = 2, MaxWorkers = 16 };
        for (var i = 0; i < 40; i++)
        {
            queue.Enqueue(new FtpTransferItem(FtpDirection.Upload, "f" + i, "/f" + i, 100));
        }

        Assert.Equal(2, queue.PlannedWorkers());
        queue.AutoWorkers = true;
        // 2 + 40 / 4 = 12.
        Assert.Equal(12, queue.PlannedWorkers());
        for (var i = 0; i < 100; i++)
        {
            queue.Enqueue(new FtpTransferItem(FtpDirection.Upload, "g" + i, "/g" + i, 100));
        }

        Assert.Equal(16, queue.PlannedWorkers());
        queue.MaxWorkers = 500;
        Assert.Equal(2 + 140 / 4, queue.PlannedWorkers());

        // Tệp lớn và dưới 8 tệp nhỏ không làm tăng luồng.
        var big = new FtpTransferQueue(new FtpConnectionInfo("127.0.0.1", 1, "anonymous", "")) { Workers = 3, AutoWorkers = true };
        for (var i = 0; i < 30; i++)
        {
            big.Enqueue(new FtpTransferItem(FtpDirection.Upload, "b" + i, "/b" + i, 64L * 1024 * 1024));
        }

        for (var i = 0; i < 7; i++)
        {
            big.Enqueue(new FtpTransferItem(FtpDirection.Upload, "s" + i, "/s" + i, 10));
        }

        Assert.Equal(3, big.PlannedWorkers());
        Assert.Equal(1, new FtpTransferQueue(new FtpConnectionInfo("h", 1, "u", "p")) { Workers = 0 }.PlannedWorkers());
        Assert.Equal(FtpTransferQueue.MaxWorkersLimit, new FtpTransferQueue(new FtpConnectionInfo("h", 1, "u", "p")) { Workers = 999 }.PlannedWorkers());
    }

    [Fact]
    public async Task AutoWorkers_ManySmallFiles_ScaleUpAndAllFinish()
    {
        await using var box = new FtpSandbox();
        var root = box.LocalPath("many");
        var files = SmallTree(root, 300);
        var queue = box.NewQueue(q =>
        {
            q.AutoWorkers = true;
            q.Workers = 2;
            q.MaxWorkers = 16;
        });
        var maxTarget = 0;
        var reasons = new HashSet<string>();
        queue.ItemFinished += _ =>
        {
            lock (reasons)
            {
                maxTarget = Math.Max(maxTarget, queue.TargetWorkers);
                reasons.Add(queue.AutoReason);
            }
        };

        Assert.Equal(300, queue.EnqueueUploadTree(root, "/"));
        Assert.Equal(16, queue.PlannedWorkers());
        await box.RunAsync(queue);

        Assert.All(queue.Items, i => Assert.Equal(FtpItemState.Done, i.State));
        Assert.Equal(304, queue.FinishedCount);
        Assert.Equal(16, maxTarget);
        // 304 mục (300 tệp + 4 thư mục) đều nhỏ: lý do tự động nêu số tệp nhỏ đang chờ.
        Assert.Contains(reasons, r => Matches(r, "Ftp.Core.AutoSmallFiles"));
        Assert.True(box.Server.PeakConnections > 2, "peak " + box.Server.PeakConnections);
        Assert.True(box.Server.PeakConnections <= 16, "peak " + box.Server.PeakConnections);
        AssertUploaded(box, "/many", files);
        // Hết việc thì mức đích trở về mức cơ bản và không còn lý do tự động.
        Assert.Equal(0, queue.ActiveWorkers);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ServerConnectionLimit_LowersTheCeilingWithoutFailingFiles(bool refuseByClosing)
    {
        await using var box = new FtpSandbox();
        box.Server.MaxConnections = 3;
        box.Server.RefuseByClosing = refuseByClosing;
        var root = box.LocalPath("limited");
        var files = SmallTree(root, 60);
        var queue = box.NewQueue(q => q.Workers = 8);
        var log = new List<FtpLogLine>();
        queue.Log += line =>
        {
            lock (log)
            {
                log.Add(line);
            }
        };

        queue.EnqueueUploadTree(root, "/");
        await box.RunAsync(queue);

        // Kết nối bị từ chối trả tệp lại hàng đợi trước khi tính một lần thử, nên không tệp nào lỗi.
        Assert.All(queue.Items, i => Assert.Equal(FtpItemState.Done, i.State));
        Assert.InRange(queue.WorkerCeiling, 1, 7);
        Assert.True(box.Server.RefusedConnections > 0);
        Assert.InRange(box.Server.PeakConnections, 1, 3);
        AssertUploaded(box, "/limited", files);
        lock (log)
        {
            // Mỗi lần hạ trần được ghi một dòng nhật ký (bằng ngôn ngữ đang dùng), không phải một tệp lỗi.
            Assert.Contains(log, l => !l.Outgoing && Matches(l.Text, "Ftp.Core.RefusedKeepWorkers"));
        }
    }

    [Fact]
    public async Task ConnectionCeiling_IsResetOnTheNextStart()
    {
        await using var box = new FtpSandbox();
        box.Server.MaxConnections = 2;
        var root = box.LocalPath("first");
        SmallTree(root, 20);
        var queue = box.NewQueue(q => q.Workers = 6);
        queue.EnqueueUploadTree(root, "/");
        await box.RunAsync(queue);
        Assert.InRange(queue.WorkerCeiling, 1, 5);
        Assert.Contains(Loc.F("Ftp.Core.AutoCeiling", queue.WorkerCeiling), queue.AutoReason);

        // Lượt sau: PS5 đã thoáng hơn, mức trần được thử lại từ đầu.
        box.Server.MaxConnections = 0;
        var second = box.LocalPath("second");
        SmallTree(second, 20);
        queue.EnqueueUploadTree(second, "/");
        await box.RunAsync(queue);
        Assert.Equal(FtpTransferQueue.MaxWorkersLimit, queue.WorkerCeiling);
        Assert.All(queue.Items, i => Assert.Equal(FtpItemState.Done, i.State));
    }

    [Fact]
    public async Task HeavyFiles_CapTheWorkersWhileTheyMove()
    {
        await using var box = new FtpSandbox();
        // Mỗi phản hồi trễ một chút để kịp quan sát lúc tệp nặng đang chạy.
        box.Server.CommandDelay = TimeSpan.FromMilliseconds(20);
        var root = box.LocalPath("mixed");
        var files = SmallTree(root, 30);
        for (var i = 0; i < 3; i++)
        {
            var content = FtpTestData.Bytes(1024 * 1024, 100 + i);
            FtpTestData.Write(Path.Combine(root, $"heavy{i}.bin"), content);
            files[$"heavy{i}.bin"] = content;
        }

        var queue = box.NewQueue(q =>
        {
            q.AutoWorkers = true;
            q.Workers = 4;
            q.MaxWorkers = 12;
            q.HeavyWorkers = 1;
            q.HeavyFileBytes = 512 * 1024;
        });
        queue.EnqueueUploadTree(root, "/");
        var heavyReason = Loc.F("Ftp.Core.AutoHeavyFile", Formatters.Size(512 * 1024));
        var sawHeavyCap = false;
        var sawHeavyReason = false;
        var idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.Idle += () => idle.TrySetResult();
        queue.Start();
        var watch = Task.Run(async () =>
        {
            while (!idle.Task.IsCompleted)
            {
                if (queue.HeavyActive > 0)
                {
                    sawHeavyCap |= queue.TargetWorkers == 1;
                    sawHeavyReason |= queue.AutoReason == heavyReason;
                }

                await Task.Delay(1);
            }
        });
        await idle.Task.WaitAsync(FtpSandbox.Limit, box.Token);
        await watch.WaitAsync(FtpSandbox.Limit, box.Token);

        Assert.True(sawHeavyCap, "the target never dropped to HeavyWorkers while a heavy file moved");
        Assert.True(sawHeavyReason, "AutoReason never named the heavy file");
        Assert.All(queue.Items, i => Assert.Equal(FtpItemState.Done, i.State));
        Assert.Equal(0, queue.HeavyActive);
        AssertUploaded(box, "/mixed", files);
    }

    /// <summary>Chuỗi <paramref name="text"/> có đúng dạng của khoá dịch <paramref name="key"/> không (mỗi {n} khớp bất kỳ đoạn nào).</summary>
    private static bool Matches(string text, string key)
    {
        var pattern = "^" + Regex.Replace(Regex.Escape(Loc.T(key)), @"\\\{\d+\}", ".*") + "$";
        return Regex.IsMatch(text, pattern, RegexOptions.Singleline);
    }
}
