using System.Text.Json;
using PsViethoa.FpkgBuilder.Core.Localization;
using PsViethoa.FpkgBuilder.Core.Models;
using PsViethoa.FpkgBuilder.Core.Services;
using Xunit;

namespace PsViethoa.FpkgBuilder.Tests;

/// <summary>
/// Engine fpkg-gui 0.6.11: giữ bố cục PlayGo của nguồn (tệp nằm ở chunk nào, nhãn, ngôn ngữ) khi sce_sys còn playgo-chunk.dat +
/// playgo-ficm.dat + playgo-hash-table.dat, hoặc tạo theo danh sách ngôn ngữ. Bảng mẫu Fixtures/playgo-multi: 3 chunk (1 = ja-JP en-US,
/// 2 = fr-FR), 2 kịch bản, data0/1/2.bin ở chunk 1/2/2.
/// </summary>
public sealed class Engine0611Tests : IDisposable
{
    private const string ContentId = "EP9999-PPSA99995_00-PSVIETHOA0611000";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "psviethoa-0611-" + Guid.NewGuid().ToString("N"));

    public Engine0611Tests() => Directory.CreateDirectory(_root);

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

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "playgo-multi", name);

    private string MakeSource(string name)
    {
        var folder = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.Combine(folder, "sce_sys"));
        File.WriteAllText(Path.Combine(folder, "sce_sys", "param.json"), JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["contentId"] = ContentId,
            ["contentVersion"] = "01.000.000",
            ["sdkVersion"] = "0x0450000000000000",
            ["requiredSystemSoftwareVersion"] = "0x0450000000000000",
            ["applicationCategoryType"] = 0,
            ["applicationDrmType"] = "standard",
            ["localizedParameters"] = new Dictionary<string, object>
            {
                ["defaultLanguage"] = "en-US",
                ["en-US"] = new Dictionary<string, string> { ["titleName"] = "Engine 0.6.11 test" },
            },
        }));
        File.WriteAllBytes(Path.Combine(folder, "eboot.bin"), new byte[64 * 1024]);
        for (var i = 0; i < 6; i++)
        {
            File.WriteAllBytes(Path.Combine(folder, $"data{i}.bin"), Enumerable.Range(0, 200_000).Select(x => (byte)(x * (i + 1))).ToArray());
        }

        foreach (var table in new[] { "playgo-chunk.dat", "playgo-ficm.dat", "playgo-hash-table.dat" })
        {
            File.Copy(Fixture(table), Path.Combine(folder, "sce_sys", table));
        }

        return folder;
    }

    private BuildRequest Request(string source, string suffix) => new()
    {
        UseSonySdk = false,
        SourcePath = source,
        OutputFolder = Path.Combine(_root, "out-" + suffix),
        TemporaryFolder = Path.Combine(_root, "tmp-" + suffix),
        ContentId = ContentId,
        Title = "Engine 0.6.11 test",
        KrakenBackend = KrakenBackendKind.BuiltIn,
        KrakenLevel = -4,
        PreventSleep = false,
    };

    private PlayGoStructure PackagePlayGo(string package, string suffix)
    {
        var export = Path.Combine(_root, "export-" + suffix);
        PackageReader.ExportSceSys(package, export, new BuildRequest().Passcode, CancellationToken.None);
        var sceSys = Path.Combine(export, "sce_sys");
        return SonySdkPlayGo.Parse(
            File.ReadAllBytes(Path.Combine(sceSys, "playgo-chunk.dat")),
            File.ReadAllBytes(Path.Combine(sceSys, "playgo-ficm.dat")),
            File.ReadAllBytes(Path.Combine(sceSys, "playgo-hash-table.dat")));
    }

    [Fact]
    public void Defaults_KeepTheSourcePlayGoFilesAndLayout()
    {
        var request = new BuildRequest();
        Assert.False(request.RemovePlayGoFiles);
        Assert.True(request.PreserveSourcePlayGoLayout);
        Assert.Equal(ulong.MaxValue, BuildEngine.PlayGoLanguageMask(request));

        request.PreserveSourcePlayGoLayout = false;
        request.PlayGoLanguages = ["ja-JP", "en-US"];
        Assert.Equal(SonySdkPlayGo.LanguageBit(0) | SonySdkPlayGo.LanguageBit(1), BuildEngine.PlayGoLanguageMask(request));

        // Giữ bố cục nguồn thì danh sách ngôn ngữ không có tác dụng.
        request.PreserveSourcePlayGoLayout = true;
        Assert.Equal(ulong.MaxValue, BuildEngine.PlayGoLanguageMask(request));
    }

    [Fact]
    public void Validate_RejectsUnknownOrEmptyLanguageSelections()
    {
        var source = MakeSource("validate");
        var request = Request(source, "validate");
        request.PreserveSourcePlayGoLayout = false;
        request.PlayGoLanguages = ["xx-YY"];
        var errors = BuildPreparer.Validate(BuildPreparer.Normalize(request));
        Assert.Contains(errors, error => error.Field == BuildPreparer.FieldPlayGo && error.Message.Contains("xx-YY", StringComparison.Ordinal));

        request.PlayGoLanguages = [];
        Assert.Contains(BuildPreparer.Validate(BuildPreparer.Normalize(request)), error => error.Field == BuildPreparer.FieldPlayGo);

        request.PlayGoLanguages = ["ja-jp"];
        Assert.DoesNotContain(BuildPreparer.Validate(BuildPreparer.Normalize(request)), error => error.Field == BuildPreparer.FieldPlayGo);
    }

    /// <summary>Mặc định: gói mới mang đúng 3 chunk, nhãn, ngôn ngữ và cách chia tệp của nguồn.</summary>
    [Fact]
    public async Task Build_KeepsTheSourcePlayGoLayout()
    {
        if (!BuildEngine.KeysAvailable)
        {
            return;
        }

        var source = MakeSource("keep");
        var request = Request(source, "keep");
        var log = new List<LogEntry>();
        var outcome = await new BuildEngine().BuildAsync(request, log.Add, null, CancellationToken.None);

        Assert.True(outcome.Verification.Contents!.IsValid);
        var structure = PackagePlayGo(outcome.OutputPath, "keep");
        Assert.Equal(3, structure.ChunkCount);
        Assert.Equal(["Chunk #0", "Voice JP EN", "Voice FR"], structure.Chunks.Select(chunk => chunk.Label));
        Assert.Equal(["ja-JP", "en-US"], SonySdkPlayGo.Codes(structure.Chunks[1].LanguageMask));
        Assert.Equal(["fr-FR"], SonySdkPlayGo.Codes(structure.Chunks[2].LanguageMask));
        Assert.Equal(1, structure.ChunkOf("data0.bin"));
        Assert.Equal(2, structure.ChunkOf("data1.bin"));
        Assert.Equal(2, structure.ChunkOf("data2.bin"));
        Assert.Equal(0, structure.ChunkOf("data3.bin"));
        Assert.Contains(log, entry => entry.Message.StartsWith(Loc.F("Plan.PlayGoSourceLayout", 3, 2, "x", "y").Split(" — ")[0], StringComparison.Ordinal));
        Assert.True(File.Exists(Path.Combine(source, "sce_sys", "playgo-chunk.dat")));
    }

    /// <summary>"Tạo theo ngôn ngữ": bỏ qua bố cục nguồn, mọi chunk thuộc các ngôn ngữ đã chọn.</summary>
    [Fact]
    public async Task Build_GeneratesChunksForTheSelectedLanguages()
    {
        if (!BuildEngine.KeysAvailable)
        {
            return;
        }

        var source = MakeSource("languages");
        var request = Request(source, "languages");
        request.PreserveSourcePlayGoLayout = false;
        request.PlayGoLanguages = ["ja-JP", "vi-VN"];
        var log = new List<LogEntry>();
        var outcome = await new BuildEngine().BuildAsync(request, log.Add, null, CancellationToken.None);

        Assert.True(outcome.Verification.Contents!.IsValid);
        var structure = PackagePlayGo(outcome.OutputPath, "languages");
        Assert.Equal(["ja-JP", "vi-VN"], SonySdkPlayGo.Codes(structure.SupportedLanguageMask));
        Assert.DoesNotContain(structure.Chunks, chunk => chunk.Label == "Voice JP EN");
        Assert.Contains(log, entry => entry.Message.Contains("ja-JP, vi-VN", StringComparison.Ordinal));
    }
}
