using PsViethoa.FpkgBuilder.Core.Models;
using PsViethoa.FpkgBuilder.Core.Services;
using Xunit;

namespace PsViethoa.FpkgBuilder.Tests;

/// <summary>Gói trùng tên trong thư mục xuất: phát hiện, giữ bản cũ bằng cách đổi tên, hoặc huỷ.</summary>
public sealed class OutputConflictTests : IDisposable
{
    private const string ContentId = "EP9000-PPSA13197_00-STELLARBLADEDLC1";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "psviethoa-conflict-" + Guid.NewGuid().ToString("N"));

    public OutputConflictTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception)
        {
        }
    }

    private string Write(string name, string content = "old")
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void Find_MatchesOnlyPackagesOfThatContentId()
    {
        Write(ContentId + "-A0100-V0100.pkg");
        Write(ContentId + "-A0101-V0101.pkg");
        Write("EP9000-PPSA13197_00-STELLARBLADEDLC2-A0100-V0100.pkg");
        Write(ContentId + "-A0100-V0100.txt");

        var found = OutputConflict.Find(_root, ContentId);
        Assert.Equal(2, found.Count);
        Assert.All(found, p => Assert.EndsWith(".pkg", p, StringComparison.Ordinal));
        Assert.All(found, p => Assert.Contains("STELLARBLADEDLC1", p, StringComparison.Ordinal));
    }

    [Fact]
    public void Find_IsEmptyForAMissingFolderOrNoMatch()
    {
        Assert.Empty(OutputConflict.Find(Path.Combine(_root, "nope"), ContentId));
        Assert.Empty(OutputConflict.Find(_root, ContentId));
        Assert.Empty(OutputConflict.Find(_root, string.Empty));
    }

    [Fact]
    public void KeepExisting_RenamesAndNeverLosesAFile()
    {
        var path = Write(ContentId + "-A0100-V0100.pkg", "first");
        var renamed = OutputConflict.KeepExisting(path);
        Assert.NotNull(renamed);
        Assert.False(File.Exists(path));
        Assert.Equal("first", File.ReadAllText(renamed!));
        Assert.Contains("(1)", renamed!, StringComparison.Ordinal);

        // Lần thứ hai phải chọn hậu tố khác, không đè lên bản đã đổi tên.
        var again = Write(ContentId + "-A0100-V0100.pkg", "second");
        var renamedAgain = OutputConflict.KeepExisting(again);
        Assert.NotNull(renamedAgain);
        Assert.NotEqual(renamed, renamedAgain);
        Assert.Equal("first", File.ReadAllText(renamed!));
        Assert.Equal("second", File.ReadAllText(renamedAgain!));
    }

    [Fact]
    public void Apply_HonoursEveryChoice()
    {
        var path = Write(ContentId + "-A0100-V0100.pkg");
        var existing = new[] { path };

        Assert.False(OutputConflict.Apply(existing, OutputConflictChoice.Cancel, null));
        Assert.True(File.Exists(path));

        Assert.True(OutputConflict.Apply(existing, OutputConflictChoice.Overwrite, null));
        Assert.True(File.Exists(path));

        var moves = new List<(string From, string? To)>();
        Assert.True(OutputConflict.Apply(existing, OutputConflictChoice.KeepExisting, (from, to) => moves.Add((from, to))));
        Assert.False(File.Exists(path));
        var move = Assert.Single(moves);
        Assert.Equal(path, move.From);
        Assert.NotNull(move.To);
    }

    [Fact]
    public async Task Build_AsksBeforeOverwritingAndCanBeCancelled()
    {
        if (!BuildEngine.KeysAvailable)
        {
            return;
        }

        var source = Path.Combine(_root, "src");
        Directory.CreateDirectory(Path.Combine(source, "sce_sys"));
        File.WriteAllText(Path.Combine(source, "sce_sys", "param.json"), "{\"titleId\":\"PPSA00001\"}");
        File.WriteAllBytes(Path.Combine(source, "eboot.bin"), new byte[4096]);

        var request = new BuildRequest
        {
            UseSonySdk = false,
            SourcePath = source,
            OutputFolder = Path.Combine(_root, "out"),
            TemporaryFolder = Path.Combine(_root, "tmp"),
            ContentId = "UP9000-PPSA00001_00-PSVIETHOACONF001",
            Title = "Conflict test",
            KrakenLevel = -4,
        };

        var engine = new BuildEngine();
        var asked = 0;
        var first = await engine.BuildAsync(request, _ => { }, null, CancellationToken.None, null, _ => { asked++; return OutputConflictChoice.Overwrite; });
        Assert.Equal(0, asked);
        Assert.True(File.Exists(first.OutputPath));

        // Lần hai: giữ bản cũ → tệp cũ được đổi tên, gói mới vẫn ra đúng tên gốc.
        var second = await engine.BuildAsync(request, _ => { }, null, CancellationToken.None, null, _ => { asked++; return OutputConflictChoice.KeepExisting; });
        Assert.Equal(1, asked);
        Assert.Equal(first.OutputPath, second.OutputPath);
        Assert.Equal(2, Directory.GetFiles(request.OutputFolder, "*.pkg").Length);

        // Lần ba: huỷ → không tạo thêm gói nào.
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            engine.BuildAsync(request, _ => { }, null, CancellationToken.None, null, _ => OutputConflictChoice.Cancel));
        Assert.Equal(2, Directory.GetFiles(request.OutputFolder, "*.pkg").Length);
    }
}
