using PsViethoa.FpkgBuilder.Core.Ftp;
using PsViethoa.FpkgBuilder.Core.Localization;
using Xunit;

namespace PsViethoa.FpkgBuilder.Tests.Ftp;

/// <summary>
/// <see cref="FtpTreeDeleter"/>: đếm cả cây rồi xoá sạch, chịu được kết nối rớt ngay sau DELE và PS5 chỉ nhận ít kết nối, và liệt kê
/// đúng những mục không xoá được.
/// </summary>
public sealed class FtpTreeDeleterTests
{
    /// <summary>Dựng cây thử dưới <paramref name="remoteRoot"/>; trả số tệp, số thư mục (gồm cả gốc) và tổng dung lượng.</summary>
    private static (int Files, int Folders, long Bytes) BuildTree(FtpSandbox box, string remoteRoot, int filesPerFolder = 4)
    {
        var folders = new[] { "", "/sce_sys", "/sce_sys/about", "/a", "/a/b", "/a/b/c", "/Thư mục có dấu", "/empty", "/a/empty too" };
        var files = 0;
        long bytes = 0;
        foreach (var folder in folders)
        {
            Directory.CreateDirectory(box.Server.RealPath(remoteRoot + folder));
            if (folder.Contains("empty", StringComparison.Ordinal))
            {
                continue;
            }

            for (var i = 0; i < filesPerFolder; i++)
            {
                var size = 100 + files * 13;
                box.ServerFile($"{remoteRoot}{folder}/tệp số {i}.bin", FtpTestData.Bytes(size, files));
                files++;
                bytes += size;
            }
        }

        return (files, folders.Length, bytes);
    }

    [Fact]
    public async Task CountThenDelete_RemovesTheWholeTree()
    {
        await using var box = new FtpSandbox();
        var (files, folders, bytes) = BuildTree(box, "/Game PPSA00000");
        box.ServerFile("/loose file.bin", FtpTestData.Bytes(777, 1));
        using var deleter = new FtpTreeDeleter(box.Connection) { Workers = 4, TimeoutSeconds = 10 };
        Assert.Equal(FtpDeletePhase.Idle, deleter.Phase);

        await deleter.CountAsync([("/Game PPSA00000", true, 0), ("/loose file.bin", false, 777)], box.Token).WaitAsync(FtpSandbox.Limit, box.Token);

        Assert.Equal(FtpDeletePhase.WaitingConfirm, deleter.Phase);
        Assert.Equal(files + 1, deleter.Stats.FilesFound);
        Assert.Equal(folders, deleter.Stats.DirsFound);
        Assert.Equal(folders, deleter.Stats.DirsListed);
        Assert.Equal(bytes + 777, deleter.Stats.BytesFound);
        Assert.Equal(files + 1 + folders, deleter.Stats.TotalItems);
        Assert.Equal(0, deleter.Stats.ProcessedItems);
        Assert.Equal(4, deleter.Stats.Connections);
        Assert.Equal(0, deleter.Stats.DroppedWorkers);
        Assert.Equal(0, deleter.Stats.ActiveWorkers);
        // Đếm xong mà chưa xác nhận thì chưa có gì bị xoá.
        Assert.Equal(files, Directory.EnumerateFiles(box.Server.RealPath("/Game PPSA00000"), "*", SearchOption.AllDirectories).Count());

        await deleter.DeleteAsync(box.Token).WaitAsync(FtpSandbox.Limit, box.Token);

        Assert.Equal(FtpDeletePhase.Done, deleter.Phase);
        Assert.Equal(files + 1, deleter.Stats.FilesDeleted);
        Assert.Equal(folders, deleter.Stats.DirsDeleted);
        Assert.Equal(bytes + 777, deleter.Stats.BytesDeleted);
        Assert.Equal(0, deleter.Stats.Failed);
        Assert.Empty(deleter.Failures);
        Assert.Equal(deleter.Stats.TotalItems, deleter.Stats.ProcessedItems);
        Assert.Equal("", deleter.Stats.Current);
        // Xoá xong thì đóng cả nhóm kết nối.
        Assert.Equal(0, deleter.Stats.Connections);
        Assert.Empty(Directory.EnumerateFileSystemEntries(box.Server.Root));
        Assert.Equal(files + 1, box.Server.CountCommands("DELE"));
        Assert.Equal(folders, box.Server.CountCommands("RMD"));
    }

    [Fact]
    public async Task Delete_SurvivesConnectionsDroppedRightAfterDele()
    {
        await using var box = new FtpSandbox();
        // Cứ 5 lệnh DELE thì một lệnh xoá xong rồi cắt kết nối, phản hồi không bao giờ tới.
        box.Server.DropAfterDeleEvery = 5;
        var (files, folders, _) = BuildTree(box, "/flaky", 5);
        using var deleter = new FtpTreeDeleter(box.Connection) { Workers = 3, TimeoutSeconds = 10 };

        await deleter.CountAsync([("/flaky", true, 0)], box.Token).WaitAsync(FtpSandbox.Limit, box.Token);
        await deleter.DeleteAsync(box.Token).WaitAsync(FtpSandbox.Limit, box.Token);

        Assert.Equal(FtpDeletePhase.Done, deleter.Phase);
        Assert.Empty(deleter.Failures);
        Assert.Equal(files, deleter.Stats.FilesDeleted);
        Assert.Equal(folders, deleter.Stats.DirsDeleted);
        Assert.True(deleter.Stats.Retries >= files / 5, "retries " + deleter.Stats.Retries);
        Assert.Empty(Directory.EnumerateFileSystemEntries(box.Server.Root));
    }

    [Fact]
    public async Task Delete_WithAServerConnectionLimit_UsesTheConnectionsItGets()
    {
        await using var box = new FtpSandbox();
        box.Server.MaxConnections = 2;
        var (files, folders, _) = BuildTree(box, "/limited");
        var log = new List<FtpLogLine>();
        using var deleter = new FtpTreeDeleter(box.Connection) { Workers = 6, TimeoutSeconds = 10 };
        deleter.Log += line =>
        {
            lock (log)
            {
                log.Add(line);
            }
        };

        await deleter.CountAsync([("/limited", true, 0)], box.Token).WaitAsync(FtpSandbox.Limit, box.Token);
        Assert.Equal(2, deleter.Stats.Connections);
        Assert.Equal(4, deleter.Stats.DroppedWorkers);
        await deleter.DeleteAsync(box.Token).WaitAsync(FtpSandbox.Limit, box.Token);

        Assert.Equal(FtpDeletePhase.Done, deleter.Phase);
        Assert.Empty(deleter.Failures);
        Assert.Equal(files, deleter.Stats.FilesDeleted);
        Assert.Equal(folders, deleter.Stats.DirsDeleted);
        Assert.InRange(box.Server.PeakConnections, 1, 2);
        Assert.True(box.Server.RefusedConnections >= 4);
        Assert.Empty(Directory.EnumerateFileSystemEntries(box.Server.Root));
        // Phản hồi 4xx/5xx thô (ở đây là "421") được chuyển vào nhật ký của bộ xoá.
        lock (log)
        {
            Assert.Contains(log, l => !l.Outgoing && l.Text.StartsWith("421", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task LockedFile_IsReportedAndItsParentFoldersAreLeftInPlace()
    {
        await using var box = new FtpSandbox();
        box.Server.LockedName = "locked.bin";
        BuildTree(box, "/keep");
        box.ServerFile("/keep/a/b/locked.bin", FtpTestData.Bytes(10, 1));
        using var deleter = new FtpTreeDeleter(box.Connection) { Workers = 2, TimeoutSeconds = 10 };

        await deleter.CountAsync([("/keep", true, 0)], box.Token).WaitAsync(FtpSandbox.Limit, box.Token);
        await deleter.DeleteAsync(box.Token).WaitAsync(FtpSandbox.Limit, box.Token);

        Assert.Equal(FtpDeletePhase.Done, deleter.Phase);
        var failures = deleter.Failures;
        Assert.Equal(4, failures.Count);
        var locked = Assert.Single(failures, f => !f.IsDirectory);
        Assert.Equal("/keep/a/b/locked.bin", locked.Path);
        Assert.Equal("550 Permission denied.", locked.Error);
        var expectedFolderError = Loc.F("Ftp.Core.FolderNotEmpty", "550 Directory not empty.");
        Assert.Equal(new[] { "/keep", "/keep/a", "/keep/a/b" }, failures.Where(f => f.IsDirectory).Select(f => f.Path).Order(StringComparer.Ordinal));
        Assert.All(failures.Where(f => f.IsDirectory), f => Assert.Equal(expectedFolderError, f.Error));
        Assert.Equal(4, deleter.Stats.Failed);
        Assert.Equal(deleter.Stats.TotalItems, deleter.Stats.ProcessedItems);
        Assert.Equal(0, deleter.Stats.Retries);

        // Chỉ còn đúng tệp bị khoá và các thư mục cha của nó.
        Assert.Equal(new[] { box.Server.RealPath("/keep/a/b/locked.bin") }, Directory.EnumerateFiles(box.Server.Root, "*", SearchOption.AllDirectories));
        Assert.Equal(3, Directory.EnumerateDirectories(box.Server.Root, "*", SearchOption.AllDirectories).Count());
    }

    [Fact]
    public async Task Count_ReportsAFolderThatCannotBeListed()
    {
        await using var box = new FtpSandbox();
        box.ServerFile("/real/x.bin", FtpTestData.Bytes(5, 1));
        using var deleter = new FtpTreeDeleter(box.Connection) { Workers = 2, TimeoutSeconds = 10 };

        await deleter.CountAsync([("/missing", true, 0), ("/real", true, 0)], box.Token).WaitAsync(FtpSandbox.Limit, box.Token);

        Assert.Equal(FtpDeletePhase.WaitingConfirm, deleter.Phase);
        var failure = Assert.Single(deleter.Failures);
        Assert.Equal("/missing", failure.Path);
        Assert.True(failure.IsDirectory);
        Assert.Equal(Loc.F("Ftp.Core.ListFolderFailed", "550 No such directory."), failure.Error);
        Assert.Equal(1, deleter.Stats.FilesFound);
        Assert.Equal(2, deleter.Stats.DirsFound);
    }

    [Fact]
    public async Task Count_OfOnlyLooseFiles_NeedsNoConnection()
    {
        await using var box = new FtpSandbox();
        box.ServerFile("/one.bin", FtpTestData.Bytes(10, 1));
        box.ServerFile("/two.bin", FtpTestData.Bytes(20, 2));
        using var deleter = new FtpTreeDeleter(box.Connection) { TimeoutSeconds = 10 };

        await deleter.CountAsync([("/one.bin", false, 10), ("/two.bin", false, 20)], box.Token).WaitAsync(FtpSandbox.Limit, box.Token);

        Assert.Equal(FtpDeletePhase.WaitingConfirm, deleter.Phase);
        Assert.Equal(2, deleter.Stats.FilesFound);
        Assert.Equal(30, deleter.Stats.BytesFound);
        Assert.Equal(0, box.Server.TotalConnections);

        await deleter.DeleteAsync(box.Token).WaitAsync(FtpSandbox.Limit, box.Token);
        Assert.Equal(2, deleter.Stats.FilesDeleted);
        Assert.Empty(Directory.EnumerateFileSystemEntries(box.Server.Root));
    }

    [Fact]
    public async Task Cancelling_StopsTheDeleteAndLeavesTheRestInPlace()
    {
        await using var box = new FtpSandbox();
        box.Server.CommandDelay = TimeSpan.FromMilliseconds(20);
        var (files, _, _) = BuildTree(box, "/slow", 6);
        using var deleter = new FtpTreeDeleter(box.Connection) { Workers = 2, TimeoutSeconds = 10 };
        await deleter.CountAsync([("/slow", true, 0)], box.Token).WaitAsync(FtpSandbox.Limit, box.Token);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(box.Token);
        cancel.CancelAfter(TimeSpan.FromMilliseconds(250));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => deleter.DeleteAsync(cancel.Token).WaitAsync(FtpSandbox.Limit, box.Token));

        Assert.Equal(FtpDeletePhase.Cancelled, deleter.Phase);
        Assert.InRange(deleter.Stats.FilesDeleted, 0, files - 1);
        Assert.Equal(0, deleter.Stats.Connections);
        Assert.NotEmpty(Directory.EnumerateFiles(box.Server.Root, "*", SearchOption.AllDirectories));
    }
}
