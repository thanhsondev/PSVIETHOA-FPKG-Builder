using PsViethoa.FpkgBuilder.Core.Ftp;
using PsViethoa.FpkgBuilder.Core.Localization;
using Xunit;

namespace PsViethoa.FpkgBuilder.Tests.Ftp;

/// <summary>
/// <see cref="FtpTransferQueue"/> với máy chủ giả: truyền khứ hồi giữ nguyên từng byte, tiếp tục bằng REST (hoặc APPE / tải lại khi
/// REST bị từ chối), tải chia đoạn, chép cả cây thư mục, thử lại sau khi rớt kênh dữ liệu và báo lỗi đúng lúc.
/// </summary>
public sealed class FtpTransferQueueTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1500)]
    [InlineData(6 * 1024 * 1024 + 123)]
    public async Task UploadThenDownload_KeepsEveryByte(int size)
    {
        await using var box = new FtpSandbox();
        var content = FtpTestData.Bytes(size, size + 1);
        var source = box.LocalPath("source.bin");
        var back = box.LocalPath("back", "copy.bin");
        FtpTestData.Write(source, content);

        var upload = new FtpTransferItem(FtpDirection.Upload, source, "/round trip/Tệp gốc.bin", size);
        var uploadQueue = box.NewQueue();
        uploadQueue.Enqueue(upload);
        await box.RunAsync(uploadQueue);

        Assert.Equal(FtpItemState.Done, upload.State);
        Assert.Equal(1, upload.Attempts);
        Assert.Equal(size, upload.Transferred);
        Assert.Equal(100, upload.Percent);
        Assert.NotNull(upload.Started);
        Assert.NotNull(upload.Finished);
        Assert.Equal(0, upload.Speed);
        Assert.Equal(size, uploadQueue.BytesMoved);
        Assert.Equal(1, uploadQueue.FinishedCount);
        Assert.Equal(FtpTestData.Hash(content), FtpTestData.Hash(box.Server.RealPath("/round trip/Tệp gốc.bin")));

        var download = new FtpTransferItem(FtpDirection.Download, back, "/round trip/Tệp gốc.bin", -1);
        var downloadQueue = box.NewQueue();
        downloadQueue.Enqueue(download);
        await box.RunAsync(downloadQueue);

        Assert.Equal(FtpItemState.Done, download.State);
        Assert.Equal(size, download.Size);
        Assert.Equal(1, download.Segments);
        Assert.Equal(size, downloadQueue.BytesMoved);
        Assert.Equal(FtpTestData.Hash(content), FtpTestData.Hash(back));
        Assert.False(downloadQueue.Running);
    }

    [Fact]
    public async Task ResumeUpload_SendsOnlyTheMissingTail()
    {
        await using var box = new FtpSandbox();
        var content = FtpTestData.Bytes(2_000_000, 11);
        var source = box.LocalPath("big.bin");
        FtpTestData.Write(source, content);
        box.ServerFile("/big.bin", content.AsSpan(0, 700_000).ToArray());

        var item = new FtpTransferItem(FtpDirection.Upload, source, "/big.bin", content.Length);
        var queue = box.NewQueue(q => q.ExistsPolicy = FtpExistsPolicy.Resume);
        queue.Enqueue(item);
        await box.RunAsync(queue);

        Assert.Equal(FtpItemState.Done, item.State);
        Assert.Equal(content.Length - 700_000, queue.BytesMoved);
        Assert.Equal(1, box.Server.CountCommands("REST", "700000"));
        Assert.Equal(1, box.Server.CountCommands("STOR", "/big.bin"));
        Assert.Equal(FtpTestData.Hash(content), FtpTestData.Hash(box.Server.RealPath("/big.bin")));
    }

    [Fact]
    public async Task ResumeDownload_RequestsOnlyTheMissingTail()
    {
        await using var box = new FtpSandbox();
        var content = FtpTestData.Bytes(2_500_000, 12);
        box.ServerFile("/data/big.bin", content);
        var target = box.LocalPath("big.bin");
        FtpTestData.Write(target, content.AsSpan(0, 900_001).ToArray());

        var item = new FtpTransferItem(FtpDirection.Download, target, "/data/big.bin", content.Length);
        var queue = box.NewQueue(q => q.ExistsPolicy = FtpExistsPolicy.Resume);
        queue.Enqueue(item);
        await box.RunAsync(queue);

        Assert.Equal(FtpItemState.Done, item.State);
        Assert.Equal(content.Length - 900_001, queue.BytesMoved);
        Assert.Equal(1, box.Server.CountCommands("REST", "900001"));
        Assert.Equal(FtpTestData.Hash(content), FtpTestData.Hash(target));
    }

    [Fact]
    public async Task ResumeUpload_WhenRestIsRefused_AppendsInstead()
    {
        await using var box = new FtpSandbox();
        box.Server.RefuseRest = true;
        var content = FtpTestData.Bytes(600_000, 13);
        var source = box.LocalPath("x.bin");
        FtpTestData.Write(source, content);
        box.ServerFile("/x.bin", content.AsSpan(0, 250_000).ToArray());

        var item = new FtpTransferItem(FtpDirection.Upload, source, "/x.bin", content.Length);
        var queue = box.NewQueue();
        queue.Enqueue(item);
        await box.RunAsync(queue);

        Assert.Equal(FtpItemState.Done, item.State);
        Assert.Equal(1, box.Server.CountCommands("APPE", "/x.bin"));
        Assert.Equal(0, box.Server.CountCommands("STOR"));
        Assert.Equal(FtpTestData.Hash(content), FtpTestData.Hash(box.Server.RealPath("/x.bin")));
    }

    [Fact]
    public async Task ResumeDownload_WhenRestIsRefused_StartsOver()
    {
        await using var box = new FtpSandbox();
        box.Server.RefuseRest = true;
        var content = FtpTestData.Bytes(800_000, 14);
        box.ServerFile("/x.bin", content);
        var target = box.LocalPath("x.bin");
        // Phần đã có bị hỏng: nếu tool nối thêm thay vì tải lại từ đầu thì mã băm sẽ sai.
        FtpTestData.Write(target, FtpTestData.Bytes(300_000, 99));

        var item = new FtpTransferItem(FtpDirection.Download, target, "/x.bin", content.Length);
        var queue = box.NewQueue();
        queue.Enqueue(item);
        await box.RunAsync(queue);

        Assert.Equal(FtpItemState.Done, item.State);
        Assert.Equal(1, box.Server.CountCommands("REST", "300000"));
        Assert.Equal(FtpTestData.Hash(content), FtpTestData.Hash(target));
    }

    [Fact]
    public async Task SegmentedDownload_SplitsTheFileIntoRestRangesAndMatchesTheHash()
    {
        await using var box = new FtpSandbox();
        var content = FtpTestData.Bytes(3 * 1024 * 1024 + 7, 21);
        box.ServerFile("/usb/big file.bin", content);
        var target = box.LocalPath("big file.bin");

        var item = new FtpTransferItem(FtpDirection.Download, target, "/usb/big file.bin", -1);
        var queue = box.NewQueue(q =>
        {
            q.SegmentsPerFile = 4;
            q.SegmentThreshold = 64 * 1024;
        });
        queue.Enqueue(item);
        await box.RunAsync(queue);

        Assert.Equal(FtpItemState.Done, item.State);
        Assert.Equal(4, item.Segments);
        Assert.Equal(content.Length, item.Transferred);
        Assert.Equal(content.Length, queue.BytesMoved);
        Assert.Equal(4, box.Server.CountCommands("RETR", "/usb/big file.bin"));
        var part = content.Length / 4;
        Assert.Equal(1, box.Server.CountCommands("REST", part.ToString()));
        Assert.Equal(1, box.Server.CountCommands("REST", (part * 2).ToString()));
        Assert.Equal(1, box.Server.CountCommands("REST", (part * 3).ToString()));
        Assert.Equal(FtpTestData.Hash(content), FtpTestData.Hash(target));
    }

    [Fact]
    public async Task SegmentedDownload_RecoversADroppedSegment()
    {
        await using var box = new FtpSandbox();
        var content = FtpTestData.Bytes(2 * 1024 * 1024 + 3, 22);
        box.ServerFile("/big.bin", content);
        box.Server.DropNextRetr();
        var target = box.LocalPath("big.bin");

        var item = new FtpTransferItem(FtpDirection.Download, target, "/big.bin", content.Length);
        var queue = box.NewQueue(q =>
        {
            q.SegmentsPerFile = 4;
            q.SegmentThreshold = 64 * 1024;
        });
        queue.Enqueue(item);
        await box.RunAsync(queue);

        Assert.Equal(FtpItemState.Done, item.State);
        Assert.Equal(1, item.Attempts);
        Assert.Equal(5, box.Server.CountCommands("RETR", "/big.bin"));
        Assert.Equal(FtpTestData.Hash(content), FtpTestData.Hash(target));
    }

    [Fact]
    public async Task UploadTree_CreatesEveryRemoteFolderIncludingEmptyOnes()
    {
        await using var box = new FtpSandbox();
        var root = box.LocalPath("Game Folder");
        var files = new Dictionary<string, byte[]>
        {
            ["sce_sys/param.json"] = "{\"titleId\":\"PPSA00000\"}"u8.ToArray(),
            ["eboot.bin"] = FtpTestData.Bytes(150_000, 31),
            ["a/b/c/deep.txt"] = FtpTestData.Bytes(10, 32),
            ["Thư mục/tệp.txt"] = FtpTestData.Bytes(2048, 33),
            ["zero.bin"] = [],
        };
        foreach (var (relative, content) in files)
        {
            FtpTestData.Write(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)), content);
        }

        Directory.CreateDirectory(Path.Combine(root, "empty"));
        Directory.CreateDirectory(Path.Combine(root, "a", "empty2"));
        Directory.CreateDirectory(box.Server.RealPath("/data"));

        var queue = box.NewQueue(q => q.Workers = 3);
        var added = new List<FtpTransferItem>();
        queue.ItemAdded += added.Add;
        var count = queue.EnqueueUploadTree(root, "/data/homebrew");
        await box.RunAsync(queue);

        Assert.Equal(files.Count, count);
        var items = queue.Items;
        Assert.Equal(added, items);
        // Thư mục gốc + 7 thư mục con (sce_sys, a, a/b, a/b/c, a/empty2, Thư mục, empty).
        Assert.Equal(8, items.Count(i => i.IsDirectoryMarker));
        Assert.All(items, i => Assert.Equal(FtpItemState.Done, i.State));
        var job = Assert.Single(items.Select(i => i.Job).Distinct());
        Assert.NotNull(job);
        Assert.True(job.IsFolder);
        Assert.Equal(FtpDirection.Upload, job.Direction);
        Assert.Equal("Game Folder", job.Name);
        Assert.Equal("/data/homebrew/Game Folder", job.Target);
        Assert.Equal(items.Count, queue.FinishedCount);

        foreach (var folder in new[] { "", "/sce_sys", "/a", "/a/b", "/a/b/c", "/a/empty2", "/Thư mục", "/empty" })
        {
            Assert.True(Directory.Exists(box.Server.RealPath("/data/homebrew/Game Folder" + folder)), folder);
        }

        foreach (var (relative, content) in files)
        {
            Assert.Equal(FtpTestData.Hash(content), FtpTestData.Hash(box.Server.RealPath("/data/homebrew/Game Folder/" + relative)));
        }
    }

    [Fact]
    public async Task DownloadTree_QueuesEveryFileAndRecreatesTheFolders()
    {
        await using var box = new FtpSandbox();
        var files = new Dictionary<string, byte[]>
        {
            ["eboot.bin"] = FtpTestData.Bytes(200_000, 41),
            ["sce_sys/icon0.png"] = FtpTestData.Bytes(5000, 42),
            ["sce_sys/about/right.sprx"] = FtpTestData.Bytes(1, 43),
            ["Âm thanh/nhạc nền.at9"] = FtpTestData.Bytes(30_000, 44),
            ["empty.bin"] = [],
        };
        foreach (var (relative, content) in files)
        {
            box.ServerFile("/user/app/Game/" + relative, content);
        }

        Directory.CreateDirectory(box.Server.RealPath("/user/app/Game/void"));
        // Mốc gần đây: LIST kiểu "ls -l" chỉ ghi giờ:phút cho tệp dưới ~6 tháng (cũ hơn thì chỉ còn năm).
        var recent = DateTime.Now.AddDays(-3);
        var stamp = new DateTime(recent.Year, recent.Month, recent.Day, recent.Hour, recent.Minute, 0);
        File.SetLastWriteTime(box.Server.RealPath("/user/app/Game/eboot.bin"), stamp);
        var output = box.LocalPath("out");

        using var browser = await box.ConnectAsync();
        var queue = box.NewQueue(q => q.Workers = 3);
        var count = await queue.EnqueueDownloadTreeAsync(browser, "/user/app/Game", output, box.Token);
        await box.RunAsync(queue);

        Assert.Equal(files.Count, count);
        Assert.All(queue.Items, i => Assert.Equal(FtpItemState.Done, i.State));
        var job = Assert.Single(queue.Items.Select(i => i.Job).Distinct());
        Assert.NotNull(job);
        Assert.Equal(FtpDirection.Download, job.Direction);
        Assert.Equal("Game", job.Name);
        Assert.Equal("/user/app/Game", job.Source);
        Assert.Equal(Path.Combine(output, "Game"), job.Target);
        Assert.True(Directory.Exists(Path.Combine(output, "Game", "void")));
        foreach (var (relative, content) in files)
        {
            var local = Path.Combine([output, "Game", .. relative.Split('/')]);
            Assert.Equal(FtpTestData.Hash(content), FtpTestData.Hash(local));
        }

        // Giữ thời điểm sửa theo danh sách của PS5 (độ chính xác phút, như ftpsrv).
        Assert.Equal(stamp, File.GetLastWriteTime(Path.Combine(output, "Game", "eboot.bin")));
    }

    [Fact]
    public async Task Download_RetriesAfterTheServerDropsTheDataConnection()
    {
        await using var box = new FtpSandbox();
        var content = FtpTestData.Bytes(1_000_000, 51);
        box.ServerFile("/flaky.bin", content);
        box.Server.DropNextRetr();
        var target = box.LocalPath("flaky.bin");

        var item = new FtpTransferItem(FtpDirection.Download, target, "/flaky.bin", content.Length);
        var queue = box.NewQueue();
        queue.Enqueue(item);
        await box.RunAsync(queue);

        Assert.Equal(FtpItemState.Done, item.State);
        Assert.Equal(2, item.Attempts);
        // Lần thử lại tiếp tục từ phần đã nhận được (chính sách mặc định là Resume).
        Assert.Equal(1, box.Server.CountCommands("REST"));
        Assert.Equal(FtpTestData.Hash(content), FtpTestData.Hash(target));
    }

    [Fact]
    public async Task Upload_RetriesAfterTheServerDropsTheDataConnection()
    {
        await using var box = new FtpSandbox();
        var content = FtpTestData.Bytes(1_500_000, 52);
        var source = box.LocalPath("flaky.bin");
        FtpTestData.Write(source, content);
        box.Server.DropNextStor();

        var item = new FtpTransferItem(FtpDirection.Upload, source, "/flaky.bin", content.Length);
        var queue = box.NewQueue();
        queue.Enqueue(item);
        await box.RunAsync(queue);

        Assert.Equal(FtpItemState.Done, item.State);
        Assert.Equal(2, item.Attempts);
        Assert.Equal(FtpTestData.Hash(content), FtpTestData.Hash(box.Server.RealPath("/flaky.bin")));
    }

    [Fact]
    public async Task RepeatedDrops_FailAfterMaxAttempts_AndRetryFailedRequeues()
    {
        await using var box = new FtpSandbox();
        var content = FtpTestData.Bytes(400_000, 53);
        box.ServerFile("/bad.bin", content);
        box.Server.DropNextRetr(3);
        var target = box.LocalPath("bad.bin");

        var item = new FtpTransferItem(FtpDirection.Download, target, "/bad.bin", content.Length);
        var finished = new List<FtpTransferItem>();
        var queue = box.NewQueue(q => q.MaxAttempts = 3);
        queue.ItemFinished += i =>
        {
            lock (finished)
            {
                finished.Add(i);
            }
        };
        queue.Enqueue(item);
        await box.RunAsync(queue);

        Assert.Equal(FtpItemState.Failed, item.State);
        Assert.Equal(3, item.Attempts);
        Assert.False(string.IsNullOrEmpty(item.Error));
        Assert.Equal(0, queue.FinishedCount);
        Assert.Same(item, Assert.Single(finished));

        Assert.Equal(1, queue.RetryFailed());
        Assert.Equal(FtpItemState.Queued, item.State);
        Assert.Equal(0, item.Attempts);
        Assert.Equal("", item.Error);
        await box.RunAsync(queue);

        Assert.Equal(FtpItemState.Done, item.State);
        Assert.Equal(1, queue.FinishedCount);
        Assert.Equal(FtpTestData.Hash(content), FtpTestData.Hash(target));
    }

    [Fact]
    public async Task PermanentErrors_FailAtOnceWithoutRetrying()
    {
        await using var box = new FtpSandbox();
        var missingRemote = new FtpTransferItem(FtpDirection.Download, box.LocalPath("nothing.bin"), "/nothing.bin", 10);
        var missingLocal = new FtpTransferItem(FtpDirection.Upload, box.LocalPath("not-here.bin"), "/not-here.bin", 10);
        var queue = box.NewQueue(q => q.MaxAttempts = 1);
        queue.Enqueue(missingRemote);
        queue.Enqueue(missingLocal);
        await box.RunAsync(queue);

        Assert.Equal(FtpItemState.Failed, missingRemote.State);
        Assert.Equal(1, missingRemote.Attempts);
        Assert.StartsWith("550", missingRemote.Error, StringComparison.Ordinal);
        Assert.Equal(FtpItemState.Failed, missingLocal.State);
        Assert.Equal(Loc.T("Ftp.Core.SourceMissing"), missingLocal.Error);
        Assert.Equal(0, box.Server.CountCommands("STOR"));
    }

    [Fact]
    public async Task ManagingItems_WithoutRunning()
    {
        await using var box = new FtpSandbox();
        var queue = box.NewQueue();
        var a = new FtpTransferItem(FtpDirection.Upload, box.LocalPath("a.bin"), "/a.bin", 1);
        var b = new FtpTransferItem(FtpDirection.Upload, box.LocalPath("b.bin"), "/b.bin", 1);
        var c = new FtpTransferItem(FtpDirection.Upload, box.LocalPath("c.bin"), "/c.bin", 1);
        queue.Enqueue(a);
        queue.Enqueue(b);
        queue.Enqueue(c);
        Assert.Equal(3, queue.Items.Count);
        Assert.False(queue.Running);

        a.State = FtpItemState.Done;
        b.State = FtpItemState.Skipped;
        queue.ClearFinished();
        Assert.Same(c, Assert.Single(queue.Items));

        // Đang chạy thì không gỡ được.
        c.State = FtpItemState.Running;
        queue.Remove(c);
        Assert.Single(queue.Items);
        c.State = FtpItemState.Queued;
        queue.Remove(c);
        Assert.Equal(FtpItemState.Cancelled, c.State);
        Assert.Empty(queue.Items);

        // Mục đã gỡ (Cancelled) không bao giờ được chạy.
        await box.RunAsync(queue);
        Assert.Equal(0, box.Server.CountCommands("STOR"));
        Assert.Equal(0, box.Server.TotalConnections);
    }
}
