using PsViethoa.FpkgBuilder.Core.Models;
using PsViethoa.FpkgBuilder.Core.Services;
using Xunit;

namespace PsViethoa.FpkgBuilder.Tests;

public class FileSystemTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "psviethoa-tests-" + Guid.NewGuid().ToString("N"));

    public FileSystemTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src", "sce_sys"));
        Directory.CreateDirectory(Path.Combine(_root, "src", "data", "sub"));
        File.WriteAllBytes(Path.Combine(_root, "src", "eboot.bin"), new byte[1024]);
        File.WriteAllText(Path.Combine(_root, "src", "sce_sys", "param.json"), "{\"contentId\":\"UP9000-PPSA00001_00-PSVIETHOATEST001\",\"contentVersion\":\"01.000.000\",\"sdkVersion\":\"0x0450000000000000\",\"localizedParameters\":{\"defaultLanguage\":\"en-US\",\"en-US\":{\"titleName\":\"Test App\"}}}");
        File.WriteAllBytes(Path.Combine(_root, "src", "data", "a.bin"), new byte[3000]);
        File.WriteAllBytes(Path.Combine(_root, "src", "data", "sub", "b.bin"), new byte[500]);
        File.WriteAllBytes(Path.Combine(_root, "src", ".DS_Store"), new byte[10]);
        File.WriteAllBytes(Path.Combine(_root, "src", "data", "Thumbs.db"), new byte[10]);
        File.WriteAllBytes(Path.Combine(_root, "src", "data", "._a.bin"), new byte[10]);
        Directory.CreateDirectory(Path.Combine(_root, "src", "__MACOSX"));
        File.WriteAllBytes(Path.Combine(_root, "src", "__MACOSX", "junk"), new byte[10]);
    }

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

    [Fact]
    public void MetadataReader_ReadsParamJson()
    {
        var metadata = MetadataReader.Read(Path.Combine(_root, "src"), CancellationToken.None);
        Assert.True(metadata.HasSceSys);
        Assert.True(metadata.HasParamJson);
        Assert.True(metadata.HasEboot);
        Assert.Equal("UP9000-PPSA00001_00-PSVIETHOATEST001", metadata.ContentId);
        Assert.Equal("PPSA00001", metadata.TitleId);
        Assert.Equal("Test App", metadata.Title);
        Assert.Equal(4, metadata.SdkMajor);
        Assert.Equal(0, metadata.PlayGoFileCount);
    }

    [Fact]
    public void FolderScanner_CountsFilesAndBytes()
    {
        var stats = FolderScanner.Scan(Path.Combine(_root, "src"), CancellationToken.None);
        Assert.Equal(8, stats.FileCount);
        Assert.Equal(1024 + 3000 + 500 + 10 + 10 + 10 + 10 + new FileInfo(Path.Combine(_root, "src", "sce_sys", "param.json")).Length, stats.TotalBytes);
        Assert.Equal(3000, stats.LargestFileBytes);
    }

    [Fact]
    public void JunkFileFinder_FindsAndDeletesJunk()
    {
        var source = Path.Combine(_root, "src");
        var junk = JunkFileFinder.Find(source, CancellationToken.None);
        Assert.Equal(4, junk.Count);
        Assert.Contains(junk, j => j.IsDirectory && j.Path.EndsWith("__MACOSX", StringComparison.Ordinal));

        var (deleted, errors) = JunkFileFinder.Delete(junk);
        Assert.Equal(4, deleted);
        Assert.Empty(errors);
        Assert.Empty(JunkFileFinder.Find(source, CancellationToken.None));
        Assert.True(File.Exists(Path.Combine(source, "data", "a.bin")));
    }

    [Fact]
    public void BuildPreparer_ValidatesAndNormalizes()
    {
        var request = new BuildRequest
        {
            UseSonySdk = false,
            SourcePath = Path.Combine(_root, "src"),
            OutputFolder = Path.Combine(_root, "src", "out"),
            ContentId = "up9000-ppsa00001_00-psviethoatest001",
            Version = "01.000.000",
            Passcode = "short",
            PlayGoChunks = 256,
        };

        var errors = BuildPreparer.Validate(request);
        Assert.Contains(errors, e => e.Field == BuildPreparer.FieldOutput);
        Assert.Contains(errors, e => e.Field == BuildPreparer.FieldPasscode);
        Assert.Contains(errors, e => e.Field == BuildPreparer.FieldPlayGo);
        Assert.DoesNotContain(errors, e => e.Field == BuildPreparer.FieldContentId);

        request.OutputFolder = Path.Combine(_root, "out");
        request.Passcode = new string('0', 32);
        request.PlayGoChunks = 255;
        request.KrakenBackend = KrakenBackendKind.BuiltIn;
        Assert.Empty(BuildPreparer.Validate(request));

        var normalized = BuildPreparer.Normalize(request);
        Assert.Equal("UP9000-PPSA00001_00-PSVIETHOATEST001", normalized.ContentId);
        Assert.False(string.IsNullOrEmpty(normalized.TemporaryFolder));
        Assert.False(BuildPreparer.IsInside(normalized.TemporaryFolder, normalized.SourcePath));
    }

    [Fact]
    public void BuildPreparer_RejectsPublishingToolsOffWindows()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var request = new BuildRequest
        {
            UseSonySdk = false,
            SourcePath = Path.Combine(_root, "src"),
            OutputFolder = Path.Combine(_root, "out"),
            ContentId = "UP9000-PPSA00001_00-PSVIETHOATEST001",
            KrakenBackend = KrakenBackendKind.PublishingTools,
        };

        Assert.Contains(BuildPreparer.Validate(request), e => e.Field == BuildPreparer.FieldPublishingTools);
        Assert.Equal(KrakenBackendKind.BuiltIn, BuildPreparer.ResolveBackend(new BuildRequest(), out _));
    }

    [Fact]
    public void DiskSpaceAdvisor_ProbesVolumes()
    {
        var info = DiskSpaceAdvisor.Probe(_root);
        Assert.NotNull(info);
        Assert.True(info!.FreeBytes > 0);

        var report = DiskSpaceAdvisor.Check(Path.Combine(_root, "out"), Path.Combine(_root, "tmp"), 10_000);
        Assert.True(report.SameVolume);
        Assert.True(report.Sufficient);
        Assert.Equal((long)(10_000 * DiskSpaceAdvisor.TemporaryFactor), report.EstimatedTemporaryBytes);
    }

    [Fact]
    public void Presets_MapToLevels()
    {
        Assert.Equal(BuildPresets.Balanced, BuildPresets.Default);
        Assert.Equal(BuildRequest.DefaultKrakenLevel, BuildPresets.Default.KrakenLevel);
        Assert.Equal(BuildPresets.Fast, BuildPresets.Match(KrakenBackendKind.Auto, 2));
        Assert.Null(BuildPresets.Match(KrakenBackendKind.Auto, 5));
        Assert.Null(BuildPresets.Match(KrakenBackendKind.Auto, 9));
        Assert.Equal(BuildPresets.Maximum, BuildPresets.Match(KrakenBackendKind.Auto, 9, PfsFormat.V3, shuffleAnalysis: true));
        var request = new BuildRequest();
        BuildPresets.Apply(BuildPresets.Smallest, request);
        Assert.Equal(7, request.KrakenLevel);
        Assert.Equal(PfsFormat.V2, request.PfsFormat);
        request.ShufflePattern = ShufflePatternKind.Shuffle116;
        BuildPresets.Apply(BuildPresets.Maximum, request);
        Assert.Equal(9, request.KrakenLevel);
        Assert.Equal(PfsFormat.V3, request.PfsFormat);
        Assert.True(request.ShuffleAnalysis);
        // Preset không giữ mẫu shuffle cố định — nếu giữ, Match() sẽ không nhận ra preset vừa áp dụng.
        Assert.Equal(ShufflePatternKind.None, request.ShufflePattern);
        Assert.Equal(BuildPresets.Balanced, BuildPresets.ById("sony"));
        Assert.Equal(BuildPresets.Balanced, BuildPresets.ById("STANDARD"));
        Assert.Null(BuildPresets.ById("nope"));
        Assert.Equal("Shuffle 1-1-6 · stride 8", BuildPresets.ShufflePatternLabel(ShufflePatternKind.Shuffle116));
        Assert.Equal("Shuffle 4-4-4-4 · stride 16", BuildPresets.ShufflePatternLabel(ShufflePatternKind.Shuffle4444));
    }

    [Fact]
    public void BuildPreparer_ValidatesNewCompressionOptions()
    {
        var request = new BuildRequest
        {
            UseSonySdk = false,
            SourcePath = Path.Combine(_root, "src"),
            OutputFolder = Path.Combine(_root, "out"),
            ContentId = "UP9000-PPSA00001_00-PSVIETHOATEST001",
            KrakenBackend = KrakenBackendKind.BuiltIn,
            KrakenBlockKiB = 64,
            ShufflePredictionLevel = 12,
            SourceMode = SourceMode.Gp5Project,
        };

        var errors = BuildPreparer.Validate(request);
        Assert.Contains(errors, e => e.Field == BuildPreparer.FieldKrakenBlock);
        Assert.Contains(errors, e => e.Field == BuildPreparer.FieldShuffle);
        Assert.Contains(errors, e => e.Field == BuildPreparer.FieldProject);

        request.KrakenBlockKiB = 128;
        request.ShufflePredictionLevel = null;
        request.SourceMode = SourceMode.Auto;
        Assert.Empty(BuildPreparer.Validate(request));

        var options = BuildEngine.CreateOptions(request, request.SourcePath, KrakenBackendKind.BuiltIn, null, CancellationToken.None);
        Assert.Equal(128 * 1024, options.KrakenCompressionBlockSize);
        Assert.Equal(LibProsperoPkg.PFS.Compression.ProsperoPfsCompressionFormat.Version2, options.PfsCompressionFormat);
    }
}
