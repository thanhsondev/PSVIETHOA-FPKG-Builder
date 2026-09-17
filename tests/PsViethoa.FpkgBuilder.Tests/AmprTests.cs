using System.Text;
using PsViethoa.FpkgBuilder.Core.Models;
using PsViethoa.FpkgBuilder.Core.Services;
using Xunit;

namespace PsViethoa.FpkgBuilder.Tests;

/// <summary>Phát hiện AMPR emu trong nguồn và việc ẩn tạm tàn dư khi tạo gói.</summary>
public sealed class AmprTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "psviethoa-ampr-" + Guid.NewGuid().ToString("N"));

    public AmprTests()
    {
        Directory.CreateDirectory(_root);
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

    private string MakeSource(bool emuModule, bool emuIndex, bool ebootImports)
    {
        var folder = Path.Combine(_root, "src-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(folder, "sce_sys"));
        File.WriteAllText(Path.Combine(folder, "sce_sys", "param.json"), "{\"titleId\":\"PPSA00001\"}");

        var eboot = new byte[3 * 1024 * 1024];
        Random.Shared.NextBytes(eboot);
        if (ebootImports)
        {
            // Chuỗi import nằm gần cuối vùng quét để chứng minh việc đọc theo khối có chồng lấn vẫn tìm ra.
            var marker = Encoding.ASCII.GetBytes("libSceAmpr.sprx\0");
            marker.CopyTo(eboot, (1 << 20) - 8);
        }

        File.WriteAllBytes(Path.Combine(folder, "eboot.bin"), eboot);

        if (emuModule)
        {
            Directory.CreateDirectory(Path.Combine(folder, "fakelib"));
            File.WriteAllBytes(Path.Combine(folder, "fakelib", "libSceAmpr.sprx"), new byte[2048]);
        }

        if (emuIndex)
        {
            File.WriteAllBytes(Path.Combine(folder, "ampr_emu.index"), new byte[4096]);
        }

        return folder;
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, true, true)]
    [InlineData(false, true, false)]
    public void ScanFolder_ReportsWhatIsThere(bool module, bool index, bool imports)
    {
        var folder = MakeSource(module, index, imports);
        var info = AmprInspector.ScanFolder(folder, CancellationToken.None);

        Assert.Equal(module, info.EmuModule);
        Assert.Equal(index, info.EmuIndex);
        Assert.Equal(imports, info.EbootImports);
        Assert.Equal(module || index, info.HasLeftovers);
        Assert.Equal(module || index || imports, info.Relevant);
        Assert.Equal(module || index, info.LeftoverPaths.Count > 0);
        if (index)
        {
            Assert.Contains("ampr_emu.index", info.LeftoverPaths);
        }

        if (module)
        {
            Assert.Contains("fakelib/libSceAmpr.sprx", info.LeftoverPaths);
        }
    }

    [Fact]
    public void ScanFolder_OnAnEmptyFolderIsEmpty()
    {
        var folder = Path.Combine(_root, "empty");
        Directory.CreateDirectory(folder);
        var info = AmprInspector.ScanFolder(folder, CancellationToken.None);
        Assert.False(info.Relevant);
        Assert.Empty(info.LeftoverPaths);
        Assert.Equal("—", AmprInspector.Describe(info));
    }

    [Fact]
    public void ContainsImport_FindsTheMarkerAcrossChunkBoundaries()
    {
        var marker = Encoding.ASCII.GetBytes("libSceAmpr");
        foreach (var offset in new[] { 0, 1000, (1 << 20) - 5, (1 << 20) + 7, (2 << 20) - 1 })
        {
            var data = new byte[(3 << 20)];
            marker.CopyTo(data, offset);
            using var stream = new MemoryStream(data);
            Assert.True(AmprInspector.ContainsImport(stream, CancellationToken.None), $"offset {offset}");
        }

        using var clean = new MemoryStream(new byte[4 << 20]);
        Assert.False(AmprInspector.ContainsImport(clean, CancellationToken.None));
    }

    [Fact]
    public void MetadataReader_ExposesAmprInfo()
    {
        var folder = MakeSource(emuModule: true, emuIndex: true, ebootImports: true);
        var metadata = MetadataReader.Read(folder, CancellationToken.None);
        Assert.True(metadata.Ampr.EbootImports);
        Assert.True(metadata.Ampr.HasLeftovers);
        Assert.Contains("ampr_emu.index", AmprInspector.Describe(metadata.Ampr));
    }

    [Fact]
    public void BuildRequest_RemovesLeftoversByDefault()
    {
        Assert.True(new BuildRequest().RemoveAmprLeftovers);
    }

    [Fact]
    public async Task Build_DropsAmprLeftoversAndRestoresTheSource()
    {
        if (!BuildEngine.KeysAvailable)
        {
            return;
        }

        var folder = MakeSource(emuModule: true, emuIndex: true, ebootImports: false);
        File.WriteAllBytes(Path.Combine(folder, "fakelib", "libSceNgs2.sprx"), new byte[1024]);
        var output = Path.Combine(_root, "out");
        var request = new BuildRequest
        {
            UseSonySdk = false,
            SourcePath = folder,
            OutputFolder = output,
            TemporaryFolder = Path.Combine(_root, "tmp"),
            ContentId = "UP9000-PPSA00001_00-PSVIETHOAAMPR001",
            Title = "AMPR test",
            KrakenLevel = -4,
        };

        var outcome = await new BuildEngine().BuildAsync(request, _ => { }, null, CancellationToken.None);
        using var reader = PackageReader.Open(outcome.OutputPath, new string('0', 32), Path.Combine(_root, "tmp"), CancellationToken.None);
        var entries = reader.Entries.Where(e => !e.IsDirectory).Select(e => e.Path).ToList();

        Assert.DoesNotContain(entries, p => p.EndsWith("ampr_emu.index", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(entries, p => p.EndsWith("libSceAmpr.sprx", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(entries, p => p.EndsWith("libSceNgs2.sprx", StringComparison.OrdinalIgnoreCase));

        // Nguồn phải nguyên vẹn sau khi tạo gói.
        Assert.True(File.Exists(Path.Combine(folder, "ampr_emu.index")));
        Assert.True(File.Exists(Path.Combine(folder, "fakelib", "libSceAmpr.sprx")));
        Assert.DoesNotContain(entries, p => p.Contains("hidden", StringComparison.OrdinalIgnoreCase));
    }
}
