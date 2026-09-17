using PsViethoa.FpkgBuilder.Core.Models;
using PsViethoa.FpkgBuilder.Core.Services;
using Xunit;

namespace PsViethoa.FpkgBuilder.Tests;

/// <summary>Ép applicationDrmType = "standard" trong lúc tạo gói và khôi phục tệp nguồn (fpkg-gui 0.6.5).</summary>
public sealed class DrmTests : IDisposable
{
    private const string Passcode = "00000000000000000000000000000000";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "psviethoa-drm-" + Guid.NewGuid().ToString("N"));

    public DrmTests() => Directory.CreateDirectory(_root);

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

    private string MakeSource(string name, string? drm)
    {
        var app = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.Combine(app, "sce_sys"));
        var drmField = drm == null ? string.Empty : $"\"applicationDrmType\":\"{drm}\",";
        File.WriteAllText(Path.Combine(app, "sce_sys", "param.json"),
            "{" + drmField + "\"contentId\":\"UP9000-PPSA00007_00-PSVIETHOADRMTEST\",\"contentVersion\":\"01.000.000\",\"sdkVersion\":\"0x0450000000000000\",\"localizedParameters\":{\"defaultLanguage\":\"en-US\",\"en-US\":{\"titleName\":\"DRM test\"}}}");
        File.WriteAllBytes(Path.Combine(app, "eboot.bin"), new byte[4096]);
        return app;
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("standard", false)]
    [InlineData("free", true)]
    [InlineData("Standard", false)]
    public void NeedsRewrite_OnlyForNonStandardValues(string? value, bool expected) =>
        Assert.Equal(expected, ParamJsonPatch.NeedsDrmRewrite(value));

    [Fact]
    public void Swap_RewritesAndRestoresByteForByte()
    {
        var app = MakeSource("swap", "free");
        var param = Path.Combine(app, "sce_sys", "param.json");
        var original = File.ReadAllBytes(param);
        Assert.Equal("free", ParamJsonPatch.ReadDrmType(param));
        using (var swap = ParamJsonPatch.Apply(param, ParamJsonPatchOptions.DrmOnly, null))
        {
            Assert.NotNull(swap);
            Assert.Single(swap!.Changes);
            Assert.Equal("standard", ParamJsonPatch.ReadDrmType(param));
            Assert.Equal("UP9000-PPSA00007_00-PSVIETHOADRMTEST", MetadataReader.Read(app, CancellationToken.None).ContentId);
        }

        Assert.Equal(original, File.ReadAllBytes(param));
        Assert.Null(ParamJsonPatch.Apply(Path.Combine(MakeSource("std", "standard"), "sce_sys", "param.json"), ParamJsonPatchOptions.DrmOnly, null));
        Assert.Null(ParamJsonPatch.Apply(Path.Combine(MakeSource("none", null), "sce_sys", "param.json"), ParamJsonPatchOptions.DrmOnly, null));
    }

    [Theory]
    [InlineData(true, "standard")]
    [InlineData(false, "free")]
    public async Task Build_ForcesStandardDrmWhenEnabled(bool force, string expected)
    {
        if (!BuildEngine.KeysAvailable)
        {
            return;
        }

        var app = MakeSource("build-" + expected, "free");
        var param = Path.Combine(app, "sce_sys", "param.json");
        var original = File.ReadAllBytes(param);
        var log = new List<LogEntry>();
        var outcome = await new BuildEngine().BuildAsync(new BuildRequest
        {
            UseSonySdk = false,
            SourcePath = app,
            OutputFolder = Path.Combine(_root, "out-" + expected),
            TemporaryFolder = Path.Combine(_root, "tmp-" + expected),
            ContentId = "UP9000-PPSA00007_00-PSVIETHOADRMTEST",
            KrakenBackend = KrakenBackendKind.BuiltIn,
            PreventSleep = false,
            ForceStandardDrm = force,
            ClearVersionFileUri = false,
            ClearPlayGoAttributes = false,
        }, log.Add, null, CancellationToken.None);

        Assert.Equal(original, File.ReadAllBytes(param)); // tệp nguồn được khôi phục
        var info = PackageInspector.Inspect(outcome.OutputPath, Passcode, CancellationToken.None);
        Assert.NotNull(info.Params);
        Assert.Contains(info.Params!.Fields, f => f.Key == "applicationDrmType" && f.Value == expected);
        Assert.Equal(force, log.Any(e => e.Message.Contains("\"free\"", StringComparison.Ordinal) && e.Message.Contains("standard", StringComparison.Ordinal)));
    }
}
