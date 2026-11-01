using System.Collections.Concurrent;
using PsViethoa.FpkgBuilder.Core.Ftp;
using Xunit;

namespace PsViethoa.FpkgBuilder.Tests.Ftp;

/// <summary>
/// Mỗi chính sách "tệp đích đã có" (<see cref="FtpExistsPolicy"/>) cho cả tải lên lẫn tải về, trong cùng một hàng đợi: đích được giữ
/// nguyên, thay mới, hay ghi tiếp đúng như chính sách.
/// </summary>
public sealed class FtpExistsPolicyTests
{
    /// <summary>Một cặp nguồn/đích dựng sẵn: <see cref="Source"/> là nội dung nguồn, <see cref="Existing"/> là đích đang có.</summary>
    private sealed record Case(FtpTransferItem Item, byte[] Source, byte[] Existing, string TargetPath);

    private static Case Upload(FtpSandbox box, string name, byte[] source, byte[] existing)
    {
        var local = box.LocalPath("up-" + name);
        FtpTestData.Write(local, source);
        var target = box.ServerFile("/up/" + name, existing);
        return new Case(new FtpTransferItem(FtpDirection.Upload, local, "/up/" + name, source.Length), source, existing, target);
    }

    private static Case Download(FtpSandbox box, string name, byte[] source, byte[] existing)
    {
        box.ServerFile("/down/" + name, source);
        var local = box.LocalPath("down", name);
        FtpTestData.Write(local, existing);
        return new Case(new FtpTransferItem(FtpDirection.Download, local, "/down/" + name, source.Length), source, existing, local);
    }

    private static async Task RunAsync(FtpSandbox box, FtpExistsPolicy policy, Action<FtpTransferQueue>? configure, params Case[] cases)
    {
        var queue = box.NewQueue(q =>
        {
            q.ExistsPolicy = policy;
            q.PreserveTimestamps = false;
            configure?.Invoke(q);
        });
        foreach (var c in cases)
        {
            queue.Enqueue(c.Item);
        }

        await box.RunAsync(queue);
    }

    private static void AssertKept(Case c)
    {
        Assert.Equal(FtpItemState.Skipped, c.Item.State);
        Assert.Equal(c.Item.Size, c.Item.Transferred);
        Assert.Equal(FtpTestData.Hash(c.Existing), FtpTestData.Hash(c.TargetPath));
    }

    private static void AssertReplaced(Case c)
    {
        Assert.Equal(FtpItemState.Done, c.Item.State);
        Assert.Equal(FtpTestData.Hash(c.Source), FtpTestData.Hash(c.TargetPath));
    }

    [Fact]
    public async Task Skip_LeavesEveryExistingTargetAlone()
    {
        await using var box = new FtpSandbox();
        var up = Upload(box, "a.bin", FtpTestData.Bytes(2000, 1), FtpTestData.Bytes(500, 2));
        var down = Download(box, "b.bin", FtpTestData.Bytes(2000, 3), FtpTestData.Bytes(300, 4));
        var fresh = Upload(box, "fresh.bin", FtpTestData.Bytes(100, 5), []);
        File.Delete(fresh.TargetPath);

        await RunAsync(box, FtpExistsPolicy.Skip, null, up, down, fresh);

        AssertKept(up);
        AssertKept(down);
        // Đích chưa có thì chính sách không áp dụng: vẫn truyền bình thường.
        AssertReplaced(fresh);
        Assert.Equal(1, box.Server.CountCommands("STOR"));
        Assert.Equal(0, box.Server.CountCommands("RETR"));
    }

    [Fact]
    public async Task Overwrite_ReplacesTargetsOfAnySize()
    {
        await using var box = new FtpSandbox();
        var upSmaller = Upload(box, "a.bin", FtpTestData.Bytes(3000, 1), FtpTestData.Bytes(5000, 2));
        var upPrefix = Upload(box, "p.bin", FtpTestData.Bytes(3000, 3), FtpTestData.Bytes(3000, 3).AsSpan(0, 1000).ToArray());
        var down = Download(box, "b.bin", FtpTestData.Bytes(3000, 4), FtpTestData.Bytes(5000, 5));

        await RunAsync(box, FtpExistsPolicy.Overwrite, null, upSmaller, upPrefix, down);

        AssertReplaced(upSmaller);
        AssertReplaced(upPrefix);
        AssertReplaced(down);
        Assert.Equal(0, box.Server.CountCommands("REST"));
    }

    [Fact]
    public async Task Resume_ContinuesPartialTargets_SkipsCompleteOnes_AndOverwritesLargerOnes()
    {
        await using var box = new FtpSandbox();
        var upSource = FtpTestData.Bytes(300_000, 1);
        var upPartial = Upload(box, "partial.bin", upSource, upSource.AsSpan(0, 100_000).ToArray());
        var upSameSize = Upload(box, "same.bin", FtpTestData.Bytes(4000, 2), FtpTestData.Bytes(4000, 3));
        var upLarger = Upload(box, "larger.bin", FtpTestData.Bytes(1000, 4), FtpTestData.Bytes(3000, 5));
        var downSource = FtpTestData.Bytes(310_000, 6);
        var downPartial = Download(box, "partial.bin", downSource, downSource.AsSpan(0, 120_000).ToArray());
        var downSameSize = Download(box, "same.bin", FtpTestData.Bytes(4000, 7), FtpTestData.Bytes(4000, 8));
        var downLarger = Download(box, "larger.bin", FtpTestData.Bytes(1000, 9), FtpTestData.Bytes(3000, 10));

        await RunAsync(box, FtpExistsPolicy.Resume, null, upPartial, upSameSize, upLarger, downPartial, downSameSize, downLarger);

        AssertReplaced(upPartial);
        AssertReplaced(downPartial);
        Assert.Equal(1, box.Server.CountCommands("REST", "100000"));
        Assert.Equal(1, box.Server.CountCommands("REST", "120000"));
        // Resume chỉ so dung lượng: cùng cỡ là coi như đã xong (giữ nguyên như bản gốc).
        AssertKept(upSameSize);
        AssertKept(downSameSize);
        AssertReplaced(upLarger);
        AssertReplaced(downLarger);
        Assert.Equal(2, box.Server.CountCommands("REST"));
    }

    [Fact]
    public async Task OverwriteIfSizeDiffers_OnlyReplacesTargetsOfAnotherSize()
    {
        await using var box = new FtpSandbox();
        var upSame = Upload(box, "same.bin", FtpTestData.Bytes(4000, 1), FtpTestData.Bytes(4000, 2));
        var upOther = Upload(box, "other.bin", FtpTestData.Bytes(4000, 3), FtpTestData.Bytes(1000, 4));
        var downSame = Download(box, "same.bin", FtpTestData.Bytes(4000, 5), FtpTestData.Bytes(4000, 6));
        var downOther = Download(box, "other.bin", FtpTestData.Bytes(4000, 7), FtpTestData.Bytes(1000, 8));

        await RunAsync(box, FtpExistsPolicy.OverwriteIfSizeDiffers, null, upSame, upOther, downSame, downOther);

        AssertKept(upSame);
        AssertKept(downSame);
        AssertReplaced(upOther);
        AssertReplaced(downOther);
        // Thay mới từ đầu, không ghi tiếp.
        Assert.Equal(0, box.Server.CountCommands("REST"));
    }

    [Fact]
    public async Task OverwriteIfNewer_ComparesTimestamps()
    {
        await using var box = new FtpSandbox();
        var older = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Local);
        var newer = new DateTime(2024, 6, 7, 8, 9, 10, DateTimeKind.Local);

        // Tải lên: so thời điểm sửa của tệp máy tính với MDTM của PS5.
        var upStale = Upload(box, "stale.bin", FtpTestData.Bytes(2000, 1), FtpTestData.Bytes(900, 2));
        File.SetLastWriteTime(upStale.Item.LocalPath, older);
        File.SetLastWriteTime(upStale.TargetPath, newer);
        var upFresh = Upload(box, "fresh.bin", FtpTestData.Bytes(2000, 3), FtpTestData.Bytes(900, 4));
        File.SetLastWriteTime(upFresh.Item.LocalPath, newer);
        File.SetLastWriteTime(upFresh.TargetPath, older);

        // Tải về: so với thời điểm lấy từ danh sách thư mục (RemoteModified).
        var downStale = Download(box, "stale.bin", FtpTestData.Bytes(2000, 5), FtpTestData.Bytes(900, 6));
        downStale.Item.RemoteModified = older;
        File.SetLastWriteTime(downStale.TargetPath, newer);
        var downFresh = Download(box, "fresh.bin", FtpTestData.Bytes(2000, 7), FtpTestData.Bytes(900, 8));
        downFresh.Item.RemoteModified = newer;
        File.SetLastWriteTime(downFresh.TargetPath, older);

        await RunAsync(box, FtpExistsPolicy.OverwriteIfNewer, q => q.PreserveTimestamps = true, upStale, upFresh, downStale, downFresh);

        AssertKept(upStale);
        AssertReplaced(upFresh);
        AssertKept(downStale);
        AssertReplaced(downFresh);
        Assert.Equal(2, box.Server.CountCommands("MDTM"));
        // PreserveTimestamps: bản tải về mang thời điểm sửa của PS5.
        Assert.Equal(newer, File.GetLastWriteTime(downFresh.TargetPath));
    }

    [Fact]
    public async Task Ask_UsesTheCallbackDecisionForEachFile()
    {
        await using var box = new FtpSandbox();
        var upSkip = Upload(box, "skip.bin", FtpTestData.Bytes(1000, 1), FtpTestData.Bytes(700, 2));
        var upOver = Upload(box, "over.bin", FtpTestData.Bytes(1000, 3), FtpTestData.Bytes(700, 4));
        var downOver = Download(box, "over.bin", FtpTestData.Bytes(1000, 5), FtpTestData.Bytes(1500, 6));
        var downSource = FtpTestData.Bytes(200_000, 7);
        var downResume = Download(box, "resume.bin", downSource, downSource.AsSpan(0, 50_000).ToArray());
        var fresh = Upload(box, "fresh.bin", FtpTestData.Bytes(100, 8), []);
        File.Delete(fresh.TargetPath);
        var asked = new ConcurrentDictionary<FtpTransferItem, long>();

        await RunAsync(box, FtpExistsPolicy.Ask, q => q.AskExists = (item, existing) =>
        {
            asked[item] = existing;
            return item.Name switch
            {
                "up-skip.bin" => FtpExistsPolicy.Skip,
                "resume.bin" => FtpExistsPolicy.Resume,
                _ => FtpExistsPolicy.Overwrite,
            };
        }, upSkip, upOver, downOver, downResume, fresh);

        Assert.Equal(4, asked.Count);
        Assert.Equal(700, asked[upSkip.Item]);
        Assert.Equal(700, asked[upOver.Item]);
        Assert.Equal(1500, asked[downOver.Item]);
        Assert.Equal(50_000, asked[downResume.Item]);
        Assert.False(asked.ContainsKey(fresh.Item));
        AssertKept(upSkip);
        AssertReplaced(upOver);
        AssertReplaced(downOver);
        AssertReplaced(downResume);
        AssertReplaced(fresh);
        Assert.Equal(1, box.Server.CountCommands("REST", "50000"));
    }

    [Fact]
    public async Task Ask_WithoutACallback_FallsBackToResume()
    {
        await using var box = new FtpSandbox();
        var source = FtpTestData.Bytes(250_000, 1);
        var partial = Upload(box, "partial.bin", source, source.AsSpan(0, 60_000).ToArray());
        var same = Upload(box, "same.bin", FtpTestData.Bytes(3000, 2), FtpTestData.Bytes(3000, 3));

        await RunAsync(box, FtpExistsPolicy.Ask, null, partial, same);

        AssertReplaced(partial);
        Assert.Equal(1, box.Server.CountCommands("REST", "60000"));
        AssertKept(same);
    }

    [Fact]
    public async Task ForceOverwrite_BeatsTheQueuePolicy()
    {
        await using var box = new FtpSandbox();
        var local = box.LocalPath("edited.txt");
        FtpTestData.Write(local, "đã sửa trên máy tính"u8.ToArray());
        var target = box.ServerFile("/edited.txt", "bản cũ trên PS5, dài hơn bản mới"u8.ToArray());
        var item = new FtpTransferItem(FtpDirection.Upload, local, "/edited.txt", -1) { ForceOverwrite = true };
        var asked = 0;

        var queue = box.NewQueue(q =>
        {
            q.ExistsPolicy = FtpExistsPolicy.Ask;
            q.AskExists = (_, _) =>
            {
                Interlocked.Increment(ref asked);
                return FtpExistsPolicy.Skip;
            };
        });
        queue.Enqueue(item);
        await box.RunAsync(queue);

        Assert.Equal(FtpItemState.Done, item.State);
        Assert.Equal(0, asked);
        Assert.Equal(FtpTestData.Hash(local), FtpTestData.Hash(target));
    }
}
