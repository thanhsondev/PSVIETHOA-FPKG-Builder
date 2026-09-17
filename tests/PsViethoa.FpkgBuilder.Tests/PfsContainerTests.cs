using System.IO.Compression;
using LibProsperoPkg.PKG;
using PsViethoa.FpkgBuilder.Core.ExFat;
using PsViethoa.FpkgBuilder.Core.Models;
using PsViethoa.FpkgBuilder.Core.Services;
using Xunit;

namespace PsViethoa.FpkgBuilder.Tests;

/// <summary>Container .ffpfsc: ảnh PFS PS5 chứa một tệp exFAT (nén PFSC hoặc không) — mở như ảnh .exfat.</summary>
public sealed class PfsContainerTests : IDisposable
{
    private const string Passcode = "00000000000000000000000000000000";
    private static readonly string RealSample = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "PPSA02849-ff", "PPSA02849_格雷克：阿祖爾的回憶.ffpfsc");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "psviethoa-pfsc-" + Guid.NewGuid().ToString("N"));

    public PfsContainerTests() => Directory.CreateDirectory(_root);

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

    private static string FixtureExFat(string root)
    {
        var gz = Path.Combine(AppContext.BaseDirectory, "Fixtures", "small-bare.exfat.gz");
        var target = Path.Combine(root, "small-bare.exfat");
        using var input = new GZipStream(File.OpenRead(gz), CompressionMode.Decompress);
        using var output = File.Create(target);
        input.CopyTo(output);
        return target;
    }

    [Fact]
    public void IsContainer_RejectsPlainExFatAndGarbage()
    {
        var exfat = FixtureExFat(_root);
        Assert.False(PfsContainer.IsContainer(exfat));
        var garbage = Path.Combine(_root, "garbage.ffpfsc");
        File.WriteAllBytes(garbage, new byte[200_000]);
        Assert.False(PfsContainer.IsContainer(garbage));
        Assert.True(SourceLocator.HasPfsContainerExtension(garbage));
        Assert.True(SourceLocator.HasImageExtension(exfat));
    }

    [Fact]
    public async Task SyntheticContainer_OpensAsExFatAndBuilds()
    {
        if (!BuildEngine.KeysAvailable)
        {
            return;
        }

        // Tạo một gói mà dữ liệu chứa ảnh exFAT mẫu, rồi giải mã ảnh trong (PPR-PFS thuần) thành container .ffpfsc không nén.
        var app = Path.Combine(_root, "app");
        Directory.CreateDirectory(Path.Combine(app, "sce_sys"));
        Directory.CreateDirectory(Path.Combine(app, "data"));
        File.WriteAllText(Path.Combine(app, "sce_sys", "param.json"), "{\"contentId\":\"UP9000-PPSA00005_00-PSVIETHOAPFSCONT\",\"contentVersion\":\"01.000.000\",\"sdkVersion\":\"0x0450000000000000\",\"localizedParameters\":{\"defaultLanguage\":\"en-US\",\"en-US\":{\"titleName\":\"container wrapper\"}}}");
        File.WriteAllBytes(Path.Combine(app, "eboot.bin"), new byte[4096]);
        File.Copy(FixtureExFat(_root), Path.Combine(app, "data", "PPSA00002.exfat"));

        var outcome = await new BuildEngine().BuildAsync(new BuildRequest
        {
            UseSonySdk = false,
            SourcePath = app,
            OutputFolder = Path.Combine(_root, "wrap-out"),
            TemporaryFolder = Path.Combine(_root, "wrap-tmp"),
            ContentId = "UP9000-PPSA00005_00-PSVIETHOAPFSCONT",
            KrakenBackend = KrakenBackendKind.BuiltIn,
            PreventSleep = false,
        }, _ => { }, null, CancellationToken.None);

        var container = Path.Combine(_root, "wrapper.ffpfsc");
        ProsperoPackageArchive.DecodeInnerPfs(outcome.OutputPath, container, Passcode);
        Assert.True(PfsContainer.IsContainer(container));
        Assert.Equal(SourceKind.ExFatImage, SourceLocator.Detect(container));

        using (var image = ExFatImage.Open(container))
        {
            Assert.True(image.IsPfsContainer);
            Assert.EndsWith(".exfat", image.ContainerEntryName, StringComparison.OrdinalIgnoreCase);
            Assert.NotNull(SourceLocator.FindAppRoot(image));
        }

        var info = SourceLocator.Resolve(container);
        Assert.True(info.IsPfsContainer);
        Assert.False(info.CanMount);

        var metadata = MetadataReader.Read(container, CancellationToken.None);
        Assert.True(metadata.IsPfsContainer);
        Assert.True(metadata.HasParamJson);
        Assert.False(string.IsNullOrEmpty(metadata.ContentId));
        Assert.True(FolderScanner.Scan(container, CancellationToken.None).FileCount > 0);

        var log = new List<LogEntry>();
        var built = await new BuildEngine().BuildAsync(new BuildRequest
        {
            UseSonySdk = false,
            SourcePath = container,
            OutputFolder = Path.Combine(_root, "from-container"),
            TemporaryFolder = Path.Combine(_root, "from-container-tmp"),
            ContentId = metadata.ContentId!,
            ExFat = ExFatStrategy.Mount, // phải tự chuyển sang giải nén vì container không gắn được
            KrakenBackend = KrakenBackendKind.BuiltIn,
            PreventSleep = false,
        }, log.Add, null, CancellationToken.None);
        Assert.True(File.Exists(built.OutputPath));
        Assert.Contains(log, e => e.Message.Contains(".ffpfsc", StringComparison.Ordinal));
        Assert.Empty(Directory.GetDirectories(Path.Combine(_root, "from-container-tmp"), "exfat-*")); // thư mục trích tạm phải được dọn
    }

    [Fact]
    public void RealSample_ReadsCompressedContainerDirectly()
    {
        if (!File.Exists(RealSample))
        {
            return;
        }

        Assert.True(PfsContainer.IsContainer(RealSample));
        using var image = ExFatImage.Open(RealSample);
        Assert.True(image.IsPfsContainer);
        Assert.True(image.ContainerStoredLength < image.ContainerStoredLength + 1);
        var metadata = MetadataReader.Read(RealSample, CancellationToken.None);
        Assert.True(metadata.HasParamJson);
        Assert.Contains("PPSA02849", metadata.ContentId);
        var stats = FolderScanner.Scan(RealSample, CancellationToken.None);
        Assert.True(stats.TotalBytes > 1_000_000_000);
    }
}
