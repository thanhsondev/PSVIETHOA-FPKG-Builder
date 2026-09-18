using System.Text.Json;
using PsViethoa.FpkgBuilder.Core.Models;
using PsViethoa.FpkgBuilder.Core.Services;
using Xunit;

namespace PsViethoa.FpkgBuilder.Tests;

/// <summary>
/// Giữ cấu trúc PlayGo của gói gốc (chunk ngôn ngữ thoại) khi tạo GP5 cho SDK Sony. Bảng mẫu trong Fixtures/playgo-multi do chính
/// Publishing Tools 2.79 tạo từ một GP5 có 3 chunk (chunk 1 = ja-JP en-US, chunk 2 = fr-FR), 2 kịch bản và data0/1/2.bin gán vào
/// chunk 1/2/2 — nên phép thử vòng tròn bảng → GP5 → SDK → bảng kiểm được cả bộ đọc lẫn bộ ghi.
/// </summary>
public sealed class SonySdkPlayGoTests : IDisposable
{
    private const string Passcode = "00000000000000000000000000000000";
    private const string ContentId = "EP9999-PPSA99999_00-PSVIETHOATEST000";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "psviethoa-playgo-sdk-" + Guid.NewGuid().ToString("N"));

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "playgo-multi", name);

    public SonySdkPlayGoTests() => Directory.CreateDirectory(_root);

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

    /// <summary>Thư mục game giống mẫu đã dựng bảng: data0..5.bin, eboot.bin, sce_sys với keystone, param.json và bảng PlayGo gốc.</summary>
    private string MakeSource(string name, bool withTables = true)
    {
        var folder = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.Combine(folder, "sce_sys"));
        File.WriteAllText(Path.Combine(folder, "sce_sys", "param.json"), JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["contentId"] = ContentId,
            ["contentVersion"] = "01.000.000",
            ["masterVersion"] = "01.00",
            ["titleId"] = "PPSA99999",
            ["sdkVersion"] = "0x0450000000000000",
            ["requiredSystemSoftwareVersion"] = "0x0450000000000000",
            ["applicationCategoryType"] = 0,
            ["applicationDrmType"] = "standard",
            ["attribute"] = 0,
            ["attribute2"] = 0,
            ["attribute3"] = 0,
            ["localizedParameters"] = new Dictionary<string, object>
            {
                ["defaultLanguage"] = "en-US",
                ["en-US"] = new Dictionary<string, string> { ["titleName"] = "PlayGo test" },
            },
        }));
        File.WriteAllBytes(Path.Combine(folder, "sce_sys", "keystone"), new byte[96]);
        File.WriteAllBytes(Path.Combine(folder, "eboot.bin"), new byte[64 * 1024]);
        for (var i = 0; i < 6; i++)
        {
            File.WriteAllBytes(Path.Combine(folder, $"data{i}.bin"), Enumerable.Range(0, 200_000).Select(x => (byte)(x * (i + 1))).ToArray());
        }

        if (withTables)
        {
            foreach (var table in new[] { "playgo-chunk.dat", "playgo-ficm.dat", "playgo-hash-table.dat" })
            {
                File.Copy(Fixture(table), Path.Combine(folder, "sce_sys", table));
            }
        }

        return folder;
    }

    [Fact]
    public void Parse_ReadsChunksLanguagesScenariosAndFileMap()
    {
        var structure = SonySdkPlayGo.Parse(File.ReadAllBytes(Fixture("playgo-chunk.dat")), File.ReadAllBytes(Fixture("playgo-ficm.dat")), File.ReadAllBytes(Fixture("playgo-hash-table.dat")));

        Assert.Equal(3, structure.ChunkCount);
        Assert.False(structure.IsTrivial);
        Assert.Equal(["ja-JP", "en-US", "fr-FR", "de-DE"], SonySdkPlayGo.Codes(structure.SupportedLanguageMask));
        Assert.Equal(1, structure.DefaultLanguageId);
        Assert.Equal(0, structure.DefaultScenarioId);
        Assert.Equal(["Chunk #0", "Voice JP EN", "Voice FR"], structure.Chunks.Select(chunk => chunk.Label));
        Assert.Equal(structure.SupportedLanguageMask, structure.Chunks[0].LanguageMask);
        Assert.Equal(["ja-JP", "en-US"], SonySdkPlayGo.Codes(structure.Chunks[1].LanguageMask));
        Assert.Equal(["fr-FR"], SonySdkPlayGo.Codes(structure.Chunks[2].LanguageMask));
        Assert.Equal(2, structure.Scenarios.Count);
        Assert.Equal(1, structure.Scenarios[0].InitialChunkCount);
        Assert.Equal([0, 2, 1], structure.Scenarios[0].ChunkOrder);
        Assert.Equal("Scenario #0", structure.Scenarios[0].Label);
        Assert.Equal(2, structure.Scenarios[1].InitialChunkCount);
        Assert.Equal([0, 1, 2], structure.Scenarios[1].ChunkOrder);
        Assert.Equal("Scenario #1", structure.Scenarios[1].Label);
        Assert.Equal(9, structure.FileChunks.Count);
        Assert.Equal(1, structure.ChunkOf("data0.bin"));
        Assert.Equal(2, structure.ChunkOf("data1.bin"));
        Assert.Equal(2, structure.ChunkOf("data2.bin"));
        Assert.Equal(0, structure.ChunkOf("data3.bin"));
        Assert.Equal(0, structure.ChunkOf("eboot.bin"));
        Assert.Null(structure.ChunkOf("sce_sys/param.json"));
        Assert.Null(structure.ChunkOf("missing.bin"));
    }

    [Fact]
    public void ChunkInfoXml_MatchesWhatPublishingToolsWrites()
    {
        var structure = SonySdkPlayGo.Parse(File.ReadAllBytes(Fixture("playgo-chunk.dat")), default, default);
        Assert.Equal(
            "    <chunk_info chunk_count=\"3\" scenario_count=\"2\">\n" +
            "      <chunks supported_languages=\"ja-JP en-US fr-FR de-DE\" default_language=\"en-US\">\n" +
            "        <chunk id=\"0\" label=\"Chunk #0\" />\n" +
            "        <chunk id=\"1\" languages=\"ja-JP en-US\" label=\"Voice JP EN\" />\n" +
            "        <chunk id=\"2\" languages=\"fr-FR\" label=\"Voice FR\" />\n" +
            "      </chunks>\n" +
            "      <scenarios default_id=\"0\">\n" +
            "        <scenario id=\"0\" type=\"playmode\" initial_chunk_count=\"3\" label=\"Scenario #0\">0 2 1</scenario>\n" +
            "        <scenario id=\"1\" type=\"playmode\" initial_chunk_count=\"3\" label=\"Scenario #1\">0 1 2</scenario>\n" +
            "      </scenarios>\n" +
            "    </chunk_info>\n",
            SonySdkPlayGo.ChunkInfoXml(structure));
        Assert.Empty(structure.FileChunks);
    }

    [Fact]
    public void Parse_RejectsForeignData()
    {
        Assert.Throws<InvalidDataException>(() => SonySdkPlayGo.Parse(new byte[512], default, default));
        var truncated = File.ReadAllBytes(Fixture("playgo-chunk.dat"))[..300];
        Assert.Throws<InvalidDataException>(() => SonySdkPlayGo.Parse(truncated, default, default));
        Assert.Null(SonySdkPlayGo.TryRead(Path.Combine(_root, "nowhere"), out var problem));
        Assert.Null(problem);
    }

    [Fact]
    public void Create_KeepsTheStructureAndAssignsFilesToTheirChunks()
    {
        var source = MakeSource("gp5");
        var structure = SonySdkPlayGo.TryRead(Path.Combine(source, "sce_sys"), out _)!;
        var plan = SonySdkSourcePlan.Pure with { PlayGo = structure };
        var result = SonySdkProject.Create(source, Path.Combine(_root, "gp5-out", "game.gp5"), Passcode, path => path, plan);

        var gp5 = File.ReadAllText(result.ProjectPath);
        Assert.Contains(SonySdkPlayGo.ChunkInfoXml(structure).Replace("\n", "\r\n"), gp5);
        Assert.Contains("dst_path=\"data0.bin\" src_path=\"" + SonySdkProject.EscapeAttribute(Path.Combine(SonySdkProject.RealPath(source), "data0.bin")) + "\" chunk=\"1\" />", gp5);
        Assert.Contains("dst_path=\"data1.bin\" src_path=\"" + SonySdkProject.EscapeAttribute(Path.Combine(SonySdkProject.RealPath(source), "data1.bin")) + "\" chunk=\"2\" />", gp5);
        Assert.Contains("dst_path=\"data3.bin\" src_path=\"" + SonySdkProject.EscapeAttribute(Path.Combine(SonySdkProject.RealPath(source), "data3.bin")) + "\" />", gp5);
        Assert.Equal(3, result.PlayGoMappedFiles);

        var scenario = JsonDocument.Parse(File.ReadAllText(result.ScenarioPath)).RootElement;
        Assert.Equal(2, scenario.GetProperty("scenarioCount").GetInt32());
        Assert.Equal("en-US", scenario.GetProperty("scenarioDefaultLanguage").GetString());
        Assert.Equal("Scenario #1", scenario.GetProperty("scenarios")[1].GetProperty("en-US").GetProperty("title").GetString());

        // Bảng PlayGo của nguồn vẫn bị loại khỏi gói như script gốc (SDK tạo lại từ chunk_info).
        Assert.Contains("sce_sys/playgo-chunk.dat (reserved/SDK-generated sce_sys artifact)", result.Excluded);
        Assert.DoesNotContain("playgo-chunk.dat\"", gp5);
        Assert.False(result.ScenarioFromSource);
    }

    /// <summary>
    /// Publishing Tools chép nguyên playgo-scenario.json vào gói: nguồn nhiều chunk có tệp khớp cấu trúc (cùng số kịch bản, kịch bản mặc
    /// định, id, kiểu playmode) thì dùng lại từng byte; không khớp thì tạo từ nhãn như trước.
    /// </summary>
    [Fact]
    public void Create_ReusesTheOriginalScenarioJsonOnlyWhenItMatchesTheStructure()
    {
        var source = MakeSource("scenario-original");
        var original = System.Text.Encoding.UTF8.GetPreamble().Concat(System.Text.Encoding.UTF8.GetBytes(
            "{\r\n \"chunkDefaultLanguage\": \"en-US\",\r\n \"scenarioCount\": 2,\r\n \"scenarioDefaultId\": 0,\r\n \"scenarioDefaultLanguage\": \"en-US\",\r\n" +
            " \"scenarios\": [\r\n  {\"id\": 0, \"type\": \"playmode\", \"en-US\": {\"title\": \"Campaign\", \"description\": \"Story mode\"}},\r\n" +
            "  {\"id\": 1, \"type\": \"playmode\", \"en-US\": {\"title\": \"Online\", \"description\": \"Multiplayer\"}}\r\n ]\r\n}"))
            .ToArray();
        var scenarioFile = Path.Combine(source, "sce_sys", "playgo-scenario.json");
        File.WriteAllBytes(scenarioFile, original);
        var structure = SonySdkPlayGo.TryRead(Path.Combine(source, "sce_sys"), out _)!;
        var plan = SonySdkSourcePlan.Pure with { PlayGo = structure };

        var reused = SonySdkProject.Create(source, Path.Combine(_root, "scenario-reused", "game.gp5"), Passcode, path => path, plan);
        Assert.True(reused.ScenarioFromSource);
        Assert.Equal(original, File.ReadAllBytes(reused.ScenarioPath));
        Assert.Contains("sce_sys/playgo-scenario.json (reserved/SDK-generated sce_sys artifact)", reused.Excluded);

        foreach (var mismatch in new[]
        {
            "{\"scenarioCount\": 1, \"scenarioDefaultId\": 0, \"scenarios\": [{\"id\": 0, \"type\": \"playmode\"}]}",
            "{\"scenarioCount\": 2, \"scenarioDefaultId\": 1, \"scenarios\": [{\"id\": 0, \"type\": \"playmode\"}, {\"id\": 1, \"type\": \"playmode\"}]}",
            "{\"scenarioCount\": 2, \"scenarioDefaultId\": 0, \"scenarios\": [{\"id\": 1, \"type\": \"playmode\"}, {\"id\": 0, \"type\": \"playmode\"}]}",
            "not json",
        })
        {
            File.WriteAllText(scenarioFile, mismatch);
            var generated = SonySdkProject.Create(source, Path.Combine(_root, "scenario-generated-" + Guid.NewGuid().ToString("N"), "game.gp5"), Passcode, path => path, plan);
            Assert.False(generated.ScenarioFromSource);
            var scenario = JsonDocument.Parse(File.ReadAllText(generated.ScenarioPath)).RootElement;
            Assert.Equal(2, scenario.GetProperty("scenarioCount").GetInt32());
            Assert.Equal("Scenario #1", scenario.GetProperty("scenarios")[1].GetProperty("en-US").GetProperty("title").GetString());
        }

        // 1 chunk / 1 kịch bản: luôn như script gốc, không dùng tệp của nguồn.
        File.WriteAllBytes(scenarioFile, original);
        var trivial = SonySdkProject.Create(source, Path.Combine(_root, "scenario-trivial", "game.gp5"), Passcode, path => path, SonySdkSourcePlan.Pure);
        Assert.False(trivial.ScenarioFromSource);
        Assert.Equal(SonySdkProject.DefaultScenario("en-US").Replace("\n", "\r\n"), File.ReadAllText(trivial.ScenarioPath));
    }

    /// <summary>Gói tạo qua Publishing Tools từ nguồn có bảng gốc phải có đúng 3 chunk, 2 kịch bản và ánh xạ tệp như gói gốc.</summary>
    [Fact]
    public async Task Build_ReproducesTheOriginalChunksInTheNewPackage()
    {
        if (SonySdkToolchain.Resolve(out _) == null)
        {
            return;
        }

        var source = MakeSource("build");
        var log = new List<LogEntry>();
        var request = new BuildRequest
        {
            SourcePath = source,
            OutputFolder = Path.Combine(_root, "out"),
            TemporaryFolder = Path.Combine(_root, "tmp"),
            ContentId = ContentId,
            PreventSleep = false,
        };

        var outcome = await new BuildEngine().BuildAsync(request, log.Add, null, CancellationToken.None);

        Assert.True(outcome.Verification.Contents!.IsValid);
        Assert.Contains(outcome.Verification.Contents.Checks, check => check.Contains("3 chunks, 2 scenarios", StringComparison.Ordinal));
        Assert.Contains(log, e => e.Message.Contains("3 chunk", StringComparison.Ordinal));

        var cnt = Path.Combine(_root, "cnt");
        PackageReader.ExportCntEntries(outcome.OutputPath, cnt, Passcode, CancellationToken.None);
        var rebuilt = SonySdkPlayGo.Parse(
            File.ReadAllBytes(Path.Combine(cnt, "playgo-chunk.dat")),
            File.ReadAllBytes(Path.Combine(cnt, "playgo-ficm.dat")),
            File.ReadAllBytes(Path.Combine(cnt, "playgo-hash-table.dat")));
        Assert.Equal(["Chunk #0", "Voice JP EN", "Voice FR"], rebuilt.Chunks.Select(chunk => chunk.Label));
        Assert.Equal(["ja-JP", "en-US"], SonySdkPlayGo.Codes(rebuilt.Chunks[1].LanguageMask));
        Assert.Equal([0, 2, 1], rebuilt.Scenarios[0].ChunkOrder);
        Assert.Equal(1, rebuilt.ChunkOf("data0.bin"));
        Assert.Equal(2, rebuilt.ChunkOf("data1.bin"));
        Assert.Equal(2, rebuilt.ChunkOf("data2.bin"));
        Assert.Equal(0, rebuilt.ChunkOf("data5.bin"));
    }

    [Fact]
    public async Task Build_WithoutTablesStillUsesOneChunk()
    {
        if (SonySdkToolchain.Resolve(out _) == null)
        {
            return;
        }

        var source = MakeSource("single", withTables: false);
        var log = new List<LogEntry>();
        var request = new BuildRequest
        {
            // Không PlayGo dự phòng: hành vi thuần của bộ công cụ gốc (1 chunk / 1 kịch bản).
            SdkPlayGoFallback = false,
            SourcePath = source,
            OutputFolder = Path.Combine(_root, "out-single"),
            TemporaryFolder = Path.Combine(_root, "tmp-single"),
            ContentId = ContentId,
            PreventSleep = false,
        };

        var outcome = await new BuildEngine().BuildAsync(request, log.Add, null, CancellationToken.None);
        Assert.Contains(outcome.Verification.Contents!.Checks, check => check.Contains("1 chunks, 1 scenarios", StringComparison.Ordinal));
        Assert.DoesNotContain(log, e => e.Message.Contains("3 chunk", StringComparison.Ordinal));
    }

    [Fact]
    public void Fallback_WritesChunksAndScenariosWithoutLanguages()
    {
        var fallback = SonySdkPlayGo.Fallback(3, 2);
        Assert.False(fallback.IsTrivial);
        Assert.Equal(
            "    <chunk_info chunk_count=\"3\" scenario_count=\"2\">\n" +
            "      <chunks>\n" +
            "        <chunk id=\"0\" label=\"Chunk #0\" />\n" +
            "        <chunk id=\"1\" label=\"Chunk #1\" />\n" +
            "        <chunk id=\"2\" label=\"Chunk #2\" />\n" +
            "      </chunks>\n" +
            "      <scenarios default_id=\"0\">\n" +
            "        <scenario id=\"0\" type=\"playmode\" initial_chunk_count=\"3\" label=\"Scenario #0\">0 1 2</scenario>\n" +
            "        <scenario id=\"1\" type=\"playmode\" initial_chunk_count=\"3\" label=\"Scenario #1\">0 1 2</scenario>\n" +
            "      </scenarios>\n" +
            "    </chunk_info>\n",
            SonySdkPlayGo.ChunkInfoXml(fallback));
        Assert.True(SonySdkPlayGo.Fallback(1, 1).IsTrivial);
        Assert.Equal(255, SonySdkPlayGo.Fallback(1000, 1).Chunks.Count);
        Assert.Equal(64, SonySdkPlayGo.Fallback(1, 99).Scenarios.Count);

        var sceSys = Path.Combine(_root, "scenario-count", "sce_sys");
        Directory.CreateDirectory(sceSys);
        Assert.Null(SonySdkPlayGo.SourceScenarioCount(sceSys));
        File.WriteAllText(Path.Combine(sceSys, "playgo-scenario.json"), "{\"scenarioCount\": 2, \"scenarios\": [{\"type\": \"playmode\", \"id\": 0}, {\"type\": \"playmode\", \"id\": 1}], \"scenarioDefaultId\": 0}");
        Assert.Equal(2, SonySdkPlayGo.SourceScenarioCount(sceSys));
        File.WriteAllText(Path.Combine(sceSys, "playgo-scenario.json"), "{\"scenarioCount\": 2, \"scenarios\": [{\"id\": 0}]}");
        Assert.Null(SonySdkPlayGo.SourceScenarioCount(sceSys));
    }

    /// <summary>
    /// Nguồn đã mất playgo-chunk.dat nhưng còn playgo-scenario.json 2 kịch bản (kiểu bản dump The Last of Us Part I): tuỳ chọn PlayGo dự
    /// phòng cho gói 100 chunk / 2 kịch bản và giữ nguyên tệp scenario gốc; không bật thì vẫn 1 chunk / 1 kịch bản như script gốc.
    /// </summary>
    [Fact]
    public async Task Build_FallbackCreatesChunksAndKeepsTheSourceScenarios()
    {
        if (SonySdkToolchain.Resolve(out _) == null)
        {
            return;
        }

        var source = MakeSource("fallback", withTables: false);
        var scenarioJson = System.Text.Encoding.UTF8.GetBytes(
            "{\n    \"scenarioCount\": 2, \n    \"scenarios\": [\n        {\"en-US\": {\"title\": \"Main\", \"description\": \"Story\"}, \"type\": \"playmode\", \"id\": 0}, \n" +
            "        {\"en-US\": {\"title\": \"Extra\", \"description\": \"Side chapter\"}, \"type\": \"playmode\", \"id\": 1}\n    ], \n" +
            "    \"chunkSupportedLanguages\": [\"en-US\", \"fr-FR\"], \n    \"scenarioDefaultLanguage\": \"en-US\", \n    \"chunkDefaultLanguage\": \"en-US\", \n    \"scenarioDefaultId\": 0\n}");
        File.WriteAllBytes(Path.Combine(source, "sce_sys", "playgo-scenario.json"), scenarioJson);
        var log = new List<LogEntry>();
        var request = new BuildRequest
        {
            SourcePath = source,
            OutputFolder = Path.Combine(_root, "out-fallback"),
            TemporaryFolder = Path.Combine(_root, "tmp-fallback"),
            ContentId = ContentId,
            PreventSleep = false,
            SdkPlayGoFallback = true,
        };

        var outcome = await new BuildEngine().BuildAsync(request, log.Add, null, CancellationToken.None);

        Assert.Contains(outcome.Verification.Contents!.Checks, check => check.Contains("100 chunks, 2 scenarios", StringComparison.Ordinal));
        Assert.Contains(log, e => e.Level == LogLevel.Warning && e.Message.Contains("100", StringComparison.Ordinal));
        var cnt = Path.Combine(_root, "cnt-fallback");
        PackageReader.ExportCntEntries(outcome.OutputPath, cnt, Passcode, CancellationToken.None);
        Assert.Equal(scenarioJson, File.ReadAllBytes(Path.Combine(cnt, "playgo-scenario.json")));
        var rebuilt = SonySdkPlayGo.Parse(File.ReadAllBytes(Path.Combine(cnt, "playgo-chunk.dat")), default, default);
        Assert.Equal(100, rebuilt.Chunks.Count);
        Assert.All(rebuilt.Scenarios, scenario => Assert.Equal(100, scenario.InitialChunkCount));
    }

    /// <summary>
    /// Cấu trúc kiểu game first-party có gói thoại từng ngôn ngữ (dạng Ghost of Yōtei, nhãn tổng hợp): 35 chunk, nhiều chunk cùng
    /// một mask (es-ES+es-419 hai lần, en-US năm lần), chunk "mọi ngôn ngữ" xen giữa, 1 kịch bản với initial_chunk_count = 32.
    /// Publishing Tools 2.79 phải nhận GP5 này và tạo lại đúng từng chunk, mask, nhãn, kịch bản và ánh xạ tệp.
    /// </summary>
    [Fact]
    public async Task Sdk_RebuildsManyLanguageChunksWithDuplicateMasks()
    {
        var runtime = SonySdkToolchain.Resolve(out _);
        if (runtime == null)
        {
            return;
        }

        string[] all = ["ja-JP", "en-US", "fr-FR", "es-ES", "de-DE", "it-IT", "nl-NL", "pt-PT", "ru-RU", "ko-KR", "zh-Hant", "zh-Hans", "fi-FI", "sv-SE", "da-DK", "no-NO", "pl-PL", "pt-BR", "en-GB", "tr-TR", "es-419", "ar-AE", "fr-CA", "cs-CZ", "hu-HU", "el-GR", "th-TH"];
        ulong Mask(params string[] codes) => codes.Aggregate(0UL, (mask, code) => mask | SonySdkPlayGo.LanguageBit(SonySdkPlayGo.LanguageCodes.ToList().IndexOf(code)));
        var supported = Mask(all);
        ulong[] masks =
        [
            supported, supported, supported, Mask("fr-FR", "fr-CA"), Mask("es-ES", "es-419"), Mask("de-DE"), Mask("it-IT"), Mask("nl-NL"), Mask("pt-PT", "pt-BR"),
            Mask("ru-RU"), Mask("ko-KR"), Mask("zh-Hant"), Mask("zh-Hans"), Mask("fi-FI"), Mask("sv-SE"), Mask("da-DK"), Mask("no-NO"), Mask("pl-PL"),
            Mask("pt-PT", "pt-BR"), Mask("en-US", "en-GB"), Mask("tr-TR"), Mask("es-ES", "es-419"), Mask("ar-AE"), Mask("en-US"), Mask("cs-CZ"), Mask("hu-HU"),
            Mask("el-GR"), Mask("en-US"), Mask("th-TH"), Mask("en-US"), Mask("en-US"), Mask("en-US"), supported, supported, supported,
        ];
        var chunks = masks.Select((mask, id) => new PlayGoChunk(id, mask, id == 0 ? "pgc_base" : $"pgc_part_{id:00}")).ToList();
        var scenario = new PlayGoScenario(0, 32, Enumerable.Range(0, masks.Length).ToList(), "scenario0");

        var source = MakeSource("many-chunks", withTables: false);
        var files = new Dictionary<ulong, int>();
        var expected = new Dictionary<string, int>();
        foreach (var chunk in new[] { 1, 2, 3, 4, 21, 23, 27, 31, 32, 34 })
        {
            var relative = $"voice/part_{chunk:00}.bin";
            Directory.CreateDirectory(Path.Combine(source, "voice"));
            File.WriteAllBytes(Path.Combine(source, "voice", $"part_{chunk:00}.bin"), Enumerable.Range(0, 5000 + chunk).Select(x => (byte)(x * chunk)).ToArray());
            files[LibProsperoPkg.PFS.ProsperoPs5FlatPathTable.HashPath("/" + relative)] = chunk;
            expected[relative] = chunk;
        }

        var structure = new PlayGoStructure(supported, 1, 0, chunks, [scenario], files);
        var project = SonySdkProject.Create(source, Path.Combine(_root, "many-out", "game.gp5"), Passcode, runtime.ToolPath, SonySdkSourcePlan.Pure with { PlayGo = structure });
        Assert.Equal(expected.Count, project.PlayGoMappedFiles);

        var raw = Path.Combine(_root, "many-out", "game.sdk-plaintext.pkg");
        var image = await SonySdkRunner.CreateImageAsync(runtime, runtime.ToolPath, project.ProjectPath, raw, null, _ => { }, _ => { }, CancellationToken.None);
        Assert.True(image.Succeeded, SonySdkRunner.Describe(image));

        var cnt = Path.Combine(_root, "many-cnt");
        PackageReader.ExportCntEntries(raw, cnt, Passcode, CancellationToken.None);
        var rebuilt = SonySdkPlayGo.Parse(
            File.ReadAllBytes(Path.Combine(cnt, "playgo-chunk.dat")),
            File.ReadAllBytes(Path.Combine(cnt, "playgo-ficm.dat")),
            File.ReadAllBytes(Path.Combine(cnt, "playgo-hash-table.dat")));
        Assert.Equal(supported, rebuilt.SupportedLanguageMask);
        Assert.Equal(1, rebuilt.DefaultLanguageId);
        Assert.Equal(masks, rebuilt.Chunks.Select(chunk => chunk.LanguageMask));
        Assert.Equal(chunks.Select(chunk => chunk.Label), rebuilt.Chunks.Select(chunk => chunk.Label));
        var rebuiltScenario = Assert.Single(rebuilt.Scenarios);
        // Mọi chunk initial (35) thay vì 32 của gói gốc — tránh hộp CE-108111-2 khi game hỏi chunk "tải sau".
        Assert.Equal(35, rebuiltScenario.InitialChunkCount);
        Assert.Equal(scenario.ChunkOrder, rebuiltScenario.ChunkOrder);
        Assert.Equal("scenario0", rebuiltScenario.Label);
        foreach (var (relative, chunk) in expected)
        {
            Assert.Equal(chunk, rebuilt.ChunkOf(relative));
        }

        Assert.Equal(0, rebuilt.ChunkOf("eboot.bin"));
    }
}
