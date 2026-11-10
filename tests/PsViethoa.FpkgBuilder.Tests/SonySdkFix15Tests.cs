using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using PsViethoa.FpkgBuilder.Core.Models;
using PsViethoa.FpkgBuilder.Core.Services;
using Xunit;

namespace PsViethoa.FpkgBuilder.Tests;

/// <summary>
/// Những gì bộ sdk-fpkg279-fix15 thêm vào script tạo GP5: bố cục PlayGo của nguồn (nhãn, ngôn ngữ, chunk của từng tệp, kịch bản retail),
/// UCP của UDS theo npbind.dat, SDK đọc từ eboot.bin, loại trừ mới, chọn ngôn ngữ PlayGo — so trực tiếp với script khi có python3; và các
/// chỗ script dừng hẳn (bảng PlayGo đặt nhầm tên như Control/PPSA29802) mà công cụ phải tạo gói được.
/// </summary>
public sealed class SonySdkFix15Tests : IDisposable
{
    private const string Passcode = "00000000000000000000000000000000";
    private const string ContentId = "EP9999-PPSA99996_00-PSVIETHOAFIX1500";

    private static readonly string[] Npwr = ["NPWR12345_00", "NPWR23456_00", "NPWR34567_00"];

    private static string Crlf(string text) => text.Replace("\r\n", "\n").Replace("\n", "\r\n");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "psviethoa-fix15-" + Guid.NewGuid().ToString("N"));

    public SonySdkFix15Tests() => Directory.CreateDirectory(_root);

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

    /// <summary>Các ca fix15 thêm vào cây tệp của phép so byte chính (SonySdkFidelityTests).</summary>
    internal static void AddFix15Cases(string source)
    {
        foreach (var directory in new[] { "decrypted", "playgo-languages", "Game/Saved/Config/Windows/deep", "sce_sys/uds" })
        {
            Directory.CreateDirectory(Path.Combine(source, directory));
        }

        File.WriteAllBytes(Path.Combine(source, "decrypted", "eboot.bin"), new byte[3]);
        File.WriteAllBytes(Path.Combine(source, "playgo-languages", "01-ja-JP.bin"), new byte[3]);
        File.WriteAllText(Path.Combine(source, "Game", "Saved", "Config", "Windows", "Engine.ini"), "[x]");
        File.WriteAllText(Path.Combine(source, "Game", "Saved", "Config", "Windows", "deep", "GameUserSettings.INI"), "[y]");
        File.WriteAllText(Path.Combine(source, "Game", "Saved", "Config", "Kept.ini"), "[z]");
        File.WriteAllText(Path.Combine(source, "entitlements.txt"), "x");
        File.WriteAllBytes(Path.Combine(source, "entitlement_key.dat"), new byte[16]);
        File.WriteAllBytes(Path.Combine(source, "sce_sys", "lem_notification.dat"), new byte[8]);
        // eboot.bin là SELF có .sceversion SDK 10.50: param.json không có sdkVersion nên script ghi sdkVersion và firmware tối thiểu theo nó.
        File.WriteAllBytes(Path.Combine(source, "eboot.bin"), SonySdkFidelityTests.SelfFile(legacy: false, misalignment: 0, [0x10, 0x50, 0, 1, 0, 0, 0, 0]));
        File.WriteAllBytes(Path.Combine(source, "sce_sys", "uds", "npbind.dat"), Npbind(Npwr));
        // uds00: digest lệch (chỉ sửa digest); uds01: thiếu (thêm UCP rỗng); uds02: không đọc được (thay bằng UCP rỗng).
        var stale = Ucp(Npwr[0]);
        stale[0x1C] ^= 0xFF;
        File.WriteAllBytes(Path.Combine(source, "sce_sys", "uds", "uds00.ucp"), stale);
        File.WriteAllBytes(Path.Combine(source, "sce_sys", "uds", "uds02.ucp"), "nope"u8.ToArray());
    }

    internal static void AssertFix15Cases(string pythonOut, SonySdkProjectResult result)
    {
        Assert.Equal(["sce_sys/uds/uds00.ucp", "sce_sys/uds/uds02.ucp", "sce_sys/uds/uds01.ucp"], result.UdsFixes.Select(fix => fix.Relative));
        Assert.Equal([false, true, true], result.UdsFixes.Select(fix => fix.Stub));
        foreach (var fix in result.UdsFixes)
        {
            var name = Path.GetFileName(fix.Path);
            Assert.Equal(File.ReadAllBytes(Path.Combine(pythonOut, ".gp5-assets", "game", "sce_sys", "uds", name)), File.ReadAllBytes(fix.Path));
            Assert.Empty(SonySdkUds.Problems(File.ReadAllBytes(fix.Path), fix.NpCommunicationId));
        }

        var param = File.ReadAllText(result.ParamJsonPath);
        Assert.Contains("\"sdkVersion\": \"0x1050000000000000\"", param);
        Assert.Contains("\"requiredSystemSoftwareVersion\": \"0x1050000000000000\"", param);
        foreach (var excluded in new[] { "decrypted/", "playgo-languages/", "Engine.ini", "GameUserSettings.INI", "entitlements.txt", "entitlement_key.dat", "lem_notification.dat" })
        {
            Assert.Contains(result.Excluded, item => item.Contains(excluded, StringComparison.Ordinal));
        }

        Assert.DoesNotContain(result.Excluded, item => item.Contains("Kept.ini", StringComparison.Ordinal));
    }

    /// <summary>npbind.dat hợp lệ (0x214 byte, SHA-1 ở cuối) với các NPWR id theo thứ tự.</summary>
    internal static byte[] Npbind(IReadOnlyList<string> ids)
    {
        const int entrySize = 0x30;
        var data = new byte[0x214];
        BinaryPrimitives.WriteUInt32BigEndian(data, SonySdkUds.NpbindMagic);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(0x0C), (uint)data.Length);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(0x14), entrySize);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(0x1C), (uint)ids.Count);
        for (var index = 0; index < ids.Count; index++)
        {
            var at = 0x80 + index * entrySize;
            // Một bản ghi lạ trước (tag 0x01) rồi tới NPWR id (tag 0x10, 16 byte có đệm 0).
            BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(at), 0x01);
            BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(at + 2), 4);
            BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(at + 8), 0x10);
            BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(at + 10), 16);
            Encoding.ASCII.GetBytes(ids[index]).CopyTo(data, at + 12);
        }

        SHA1.HashData(data.AsSpan(0, 0x200)).CopyTo(data, 0x200);
        return data;
    }

    /// <summary>UCP hợp lệ có dữ liệu (một sự kiện) cho <paramref name="npwr"/>.</summary>
    internal static byte[] Ucp(string npwr) => SonySdkUds.Pack(
    [
        ("enum_definition.json", Encoding.UTF8.GetBytes($"{{\"schemaVersion\":\"1.1\",\"npCommunicationId\":\"{npwr}\",\"enumArray\":[{{\"a\":1}}]}}")),
        ("events_definition.json", "{\"events\":[{\"id\":7}]}"u8.ToArray()),
        ("stats_definition.json", Encoding.UTF8.GetBytes($"{{\"npCommunicationId\":\"{npwr}\",\"statDefinitionArray\":[1,2,3]}}")),
        ("stats_extraction.json", Encoding.UTF8.GetBytes($"{{\"npCommunicationId\":\"{npwr}\"}}")),
    ]);

    private string MakeSource(string name, bool sourceTables, bool misnamedTables = false)
    {
        var source = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.Combine(source, "sce_sys"));
        Directory.CreateDirectory(Path.Combine(source, "sce_module"));
        File.WriteAllText(Path.Combine(source, "sce_sys", "param.json"), "{\"contentId\":\"" + ContentId + "\",\"applicationCategoryType\":0,\"sdkVersion\":\"0x0000000000000000\",\"requiredSystemSoftwareVersion\":\"0x0900000000000000\",\"localizedParameters\":{\"defaultLanguage\":\"en-US\"}}");
        File.WriteAllBytes(Path.Combine(source, "sce_sys", "keystone"), new byte[96]);
        File.WriteAllText(
            Path.Combine(source, "sce_sys", "playgo-scenario.json"),
            "{\"chunkDefaultLanguage\":\"de-DE\",\"chunkSupportedLanguages\":[\"en-US\",\"de-DE\"],\"scenarioCount\":2,\"scenarioDefaultId\":0,\"scenarioDefaultLanguage\":\"en-US\",\"scenarios\":[" +
            "{\"id\":0,\"type\":\"playmode\",\"en-US\":{\"title\":\"Story\"}},{\"id\":1,\"type\":\"playmode\",\"en-US\":{\"title\":\"Extra\"}}]}");
        File.WriteAllBytes(Path.Combine(source, "eboot.bin"), new byte[64]);
        File.WriteAllBytes(Path.Combine(source, "data0.bin"), new byte[10]);
        File.WriteAllBytes(Path.Combine(source, "data1.bin"), new byte[11]);
        File.WriteAllBytes(Path.Combine(source, "data2.bin"), new byte[12]);
        File.WriteAllBytes(Path.Combine(source, "extra.bin"), new byte[13]);
        File.WriteAllBytes(Path.Combine(source, "sce_module", "libX.prx"), new byte[14]);
        if (sourceTables)
        {
            File.Copy(Fixture(SonySdkPlayGo.ChunkFileName), Path.Combine(source, "sce_sys", SonySdkPlayGo.ChunkFileName));
            if (misnamedTables)
            {
                // Như bản dump Control PPSA34547: playgo-ficm.dat chứa bảng FLT, playgo-hash-table.dat là một bản param.json.
                File.Copy(Fixture(SonySdkPlayGo.HashTableFileName), Path.Combine(source, "sce_sys", SonySdkPlayGo.FicmFileName));
                File.Copy(Path.Combine(source, "sce_sys", "param.json"), Path.Combine(source, "sce_sys", SonySdkPlayGo.HashTableFileName));
            }
            else
            {
                File.Copy(Fixture(SonySdkPlayGo.FicmFileName), Path.Combine(source, "sce_sys", SonySdkPlayGo.FicmFileName));
                File.Copy(Fixture(SonySdkPlayGo.HashTableFileName), Path.Combine(source, "sce_sys", SonySdkPlayGo.HashTableFileName));
            }
        }

        return SonySdkProject.RealPath(source);
    }

    private static (int Exit, string Stdout, string Stderr) RunScript(string source, string gp5, params string[] extra)
    {
        var startInfo = new ProcessStartInfo(SonySdkFidelityTests.FindPython()!)
        {
            ArgumentList = { SonySdkFidelityTests.FindToolkitScript()!, source, gp5, "--passcode", Passcode, "--absolute-paths", "--volume", "app", "--keep-keystone", "--chunk-count", "100" },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in extra)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var run = Process.Start(startInfo)!;
        var stdout = run.StandardOutput.ReadToEnd();
        var stderr = run.StandardError.ReadToEnd();
        run.WaitForExit();
        return (run.ExitCode, stdout, stderr);
    }

    private static bool CanCompare => SonySdkFidelityTests.FindToolkitScript() != null && SonySdkFidelityTests.FindPython() != null;

    private SonySdkProjectResult BuildApp(string source, string folder, PlayGoStructure playGo, bool fillEmptyChunks = false)
    {
        var output = SonySdkProject.RealPath(Path.Combine(_root, folder));
        Directory.CreateDirectory(output);
        return SonySdkProject.Create(source, Path.Combine(output, "game.gp5"), Passcode, path => path, SonySdkSourcePlan.Pure with { PlayGo = playGo, FillEmptyChunks = fillEmptyChunks });
    }

    private void AssertSameAsScript(string pythonOut, SonySdkProjectResult result, string stdout, bool compareReport = true)
    {
        var sharpOut = Path.GetDirectoryName(result.ProjectPath)!;
        Assert.Equal(Crlf(File.ReadAllText(Path.Combine(pythonOut, "game.gp5"))).Replace(SonySdkProject.EscapeAttribute(pythonOut), SonySdkProject.EscapeAttribute(sharpOut)), File.ReadAllText(result.ProjectPath));
        Assert.Equal(Crlf(File.ReadAllText(Path.Combine(pythonOut, "game.playgo-scenario.json"))), File.ReadAllText(result.ScenarioPath));
        Assert.Equal(Crlf(File.ReadAllText(Path.Combine(pythonOut, ".gp5-assets", "game", "sce_sys", "param.json"))), File.ReadAllText(result.ParamJsonPath));
        if (compareReport)
        {
            Assert.Equal(stdout.Replace("\r\n", "\n").Replace(pythonOut, sharpOut), result.Report);
        }
    }

    [Fact]
    public void PathHash_FindsTheFixtureFilesInTheirChunks()
    {
        var sceSys = Path.GetDirectoryName(Fixture(SonySdkPlayGo.ChunkFileName))!;
        Assert.Equal(3, SonySdkPlayGoSource.ReadChunkCount(sceSys, out var problem));
        Assert.Null(problem);
        var lines = new List<string>();
        var layout = SonySdkPlayGoSource.ReadLayout(sceSys, 3, lines, out problem);
        Assert.Null(problem);
        Assert.Empty(lines);
        Assert.NotNull(layout);
        Assert.Equal(1, layout.ChunkOf("data0.bin"));
        Assert.Equal(2, layout.ChunkOf("data1.bin"));
        Assert.Equal(2, layout.ChunkOf("DATA2.BIN"));
        Assert.Equal(0, layout.ChunkOf("eboot.bin"));
        Assert.Null(layout.ChunkOf("missing.bin"));
        Assert.Equal(["Chunk #0", "Voice JP EN", "Voice FR"], layout.Chunks.Select(chunk => chunk.Label));
        Assert.Equal(["ja-JP", "en-US", "fr-FR", "de-DE"], layout.Languages);
        Assert.Equal([(1, 3), (2, 3)], layout.Scenarios!.Select(scenario => (scenario.InitialChunkCount, scenario.ChunkIds.Count)));
        Assert.Equal("0 2 1", SonySdkPlayGoSource.ChunkRanges(layout.Scenarios![0].ChunkIds));
        Assert.Equal("0-31 33 32", SonySdkPlayGoSource.ChunkRanges([.. Enumerable.Range(0, 32), 33, 32]));
    }

    [Fact]
    public void MisnamedTables_FallBackInsteadOfStopping()
    {
        var source = MakeSource("misnamed", sourceTables: true, misnamedTables: true);
        var structure = SonySdkPlayGo.Toolkit(Path.Combine(source, "sce_sys"), "en-US", 100, null, retailScenarios: false, out var problems);
        Assert.Contains("unrecognized header", problems.Layout);
        Assert.Null(problems.ChunkCount);
        Assert.Null(structure.SourceLayout);
        // Số chunk vẫn lấy từ header playgo-chunk.dat (3), không phải 100; mỗi ngôn ngữ của playgo-scenario.json một tệp giữ chỗ.
        Assert.Equal(3, structure.Chunks.Count);
        Assert.Equal([1, 2], structure.LanguagePayloads.Select(payload => payload.ChunkId));
        Assert.StartsWith("PlayGo: using 3 chunks from ", structure.ToolkitLines[0]);
    }

    [Fact]
    public void InvalidChunkHeader_IsIgnoredWithAProblem()
    {
        var source = MakeSource("badheader", sourceTables: false);
        File.WriteAllBytes(Path.Combine(source, "sce_sys", SonySdkPlayGo.ChunkFileName), new byte[4]);
        var structure = SonySdkPlayGo.Toolkit(Path.Combine(source, "sce_sys"), "en-US", 7, null, retailScenarios: false, out var problems);
        Assert.Contains("invalid PS5 playgo-chunk.dat header", problems.ChunkCount);
        Assert.Equal(7, structure.Chunks.Count);
        Assert.Empty(structure.ToolkitLines);
    }

    [Fact]
    public void DefaultScenarios_ListEveryChunkUnlessRetailIsChosen()
    {
        var source = MakeSource("layout", sourceTables: true);
        var everyChunk = SonySdkPlayGo.Toolkit(Path.Combine(source, "sce_sys"), "en-US", 100, null, retailScenarios: false, out _);
        var retail = SonySdkPlayGo.Toolkit(Path.Combine(source, "sce_sys"), "en-US", 100, null, retailScenarios: true, out _);
        Assert.All(everyChunk.Scenarios, scenario => Assert.Equal((3, "0-2"), (scenario.InitialChunkCount, SonySdkPlayGoSource.ChunkRanges(scenario.ChunkOrder))));
        Assert.Equal([(1, "0 2 1"), (2, "0-2")], retail.Scenarios.Select(scenario => (scenario.InitialChunkCount, SonySdkPlayGoSource.ChunkRanges(scenario.ChunkOrder))));
        // Nhãn kịch bản lấy từ bảng nguồn ở cả hai chế độ, như script.
        Assert.Equal(["Scenario #0", "Scenario #1"], everyChunk.Scenarios.Select(scenario => scenario.Label));
        Assert.True(retail.RetailScenarios);
        Assert.False(everyChunk.RetailScenarios);
    }

    [Fact]
    public void UdsStub_IsAWellFormedEmptyUcp()
    {
        var stub = SonySdkUds.Stub(Npwr[1]);
        Assert.Empty(SonySdkUds.Problems(stub, Npwr[1]));
        Assert.Equal(SonySdkUds.MemberOrder, SonySdkUds.Unpack(stub).Select(member => member.Name));
        Assert.Contains("npbind says 'NPWR99999_00'", string.Join("; ", SonySdkUds.Problems(stub, "NPWR99999_00")));
        Assert.Equal(Npwr, SonySdkUds.ReadNpbindIds("npbind.dat", Npbind(Npwr)));
        var broken = Npbind(Npwr);
        broken[0x100] ^= 1;
        Assert.Throws<InvalidDataException>(() => SonySdkUds.ReadNpbindIds("npbind.dat", broken));
    }

    [Fact]
    public void ExecutableSdkVersion_ReadsTheSceVersionTrailer()
    {
        var path = Path.Combine(_root, "eboot.bin");
        File.WriteAllBytes(path, SonySdkFidelityTests.SelfFile(legacy: false, misalignment: 3, [0x07, 0x20, 0, 0, 0, 0, 1, 2]));
        Assert.Equal(0x0720000000000000UL, SonySdkSelfRepair.ExecutableSdkVersion(path));
        File.WriteAllBytes(path, new byte[64]);
        Assert.Null(SonySdkSelfRepair.ExecutableSdkVersion(path));
    }

    [Fact]
    public void SdkOverride_UsesThePackedBcdVersion()
    {
        var request = new BuildRequest { SdkMajorOverride = 10 };
        Assert.Equal(0x1000000000000000UL, request.SdkVersionOverrideValue);
        request.SdkMajorOverride = 5;
        Assert.Equal(0x0500000000000000UL, request.SdkVersionOverrideValue);
        request.SdkMajorOverride = null;
        Assert.Null(request.SdkVersionOverrideValue);
        Assert.Null(new BuildRequest().SdkPlayGoLanguages);
        Assert.Equal(["en-US", "ru-RU"], new BuildRequest { PreserveSourcePlayGoLayout = false, PlayGoLanguages = ["EN-us", "ru-RU", "xx-YY"] }.SdkPlayGoLanguages);
        Assert.Equal(31, new BuildRequest { PreserveSourcePlayGoLayout = false }.SdkPlayGoLanguages!.Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SourceLayout_IsByteIdenticalToTheToolkitScript(bool retail)
    {
        if (!CanCompare)
        {
            return;
        }

        var source = MakeSource("layout", sourceTables: true);
        var pythonOut = SonySdkProject.RealPath(Path.Combine(_root, "py"));
        Directory.CreateDirectory(pythonOut);
        var (exit, stdout, stderr) = RunScript(source, Path.Combine(pythonOut, "game.gp5"));
        Assert.True(exit == 0, stderr);
        Assert.Contains("PlayGo: kept the source layout (3 chunks", stdout);

        var playGo = SonySdkPlayGo.Toolkit(Path.Combine(source, "sce_sys"), SonySdkProject.DefaultLanguageOf(Path.Combine(source, "sce_sys", "param.json")), 100, null, retail, out var problems);
        Assert.Equal(new PlayGoToolkitProblems(null, null, null), problems);
        var result = BuildApp(source, "cs", playGo);
        if (retail)
        {
            AssertSameAsScript(pythonOut, result, stdout);
        }
        else
        {
            // Mặc định của công cụ: chỉ các dòng kịch bản khác script (mọi chunk, mọi chunk initial).
            var expected = Crlf(File.ReadAllText(Path.Combine(pythonOut, "game.gp5")))
                .Replace(SonySdkProject.EscapeAttribute(pythonOut), SonySdkProject.EscapeAttribute(Path.GetDirectoryName(result.ProjectPath)!))
                .Replace("initial_chunk_count=\"1\" label=\"Scenario #0\">0 2 1</scenario>", "initial_chunk_count=\"3\" label=\"Scenario #0\">0-2</scenario>")
                .Replace("initial_chunk_count=\"2\" label=\"Scenario #1\">0-2</scenario>", "initial_chunk_count=\"3\" label=\"Scenario #1\">0-2</scenario>");
            Assert.Equal(expected, File.ReadAllText(result.ProjectPath));
        }

        Assert.Contains("<file dst_path=\"data0.bin\" src_path=\"" + SonySdkProject.EscapeAttribute(Path.Combine(source, "data0.bin")) + "\" chunk=\"1\" />", File.ReadAllText(result.ProjectPath));
        Assert.Contains("\"chunkSupportedLanguages\": [\r\n    \"ja-JP\",\r\n    \"en-US\",\r\n    \"fr-FR\",\r\n    \"de-DE\"\r\n  ]", File.ReadAllText(result.ScenarioPath));
    }

    [Fact]
    public void MisnamedTables_BuildLikeTheScriptWithoutTheBrokenTables()
    {
        if (!CanCompare)
        {
            return;
        }

        var source = MakeSource("misnamed", sourceTables: true, misnamedTables: true);
        var pythonOut = SonySdkProject.RealPath(Path.Combine(_root, "py"));
        Directory.CreateDirectory(pythonOut);
        // Script fix15 dừng hẳn với bảng đặt nhầm tên…
        var failed = RunScript(source, Path.Combine(pythonOut, "game.gp5"));
        Assert.NotEqual(0, failed.Exit);
        Assert.Contains("unrecognized header", failed.Stderr);

        // …công cụ tạo được GP5, giống hệt GP5 script tạo khi nguồn chỉ còn playgo-chunk.dat.
        var playGo = SonySdkPlayGo.Toolkit(Path.Combine(source, "sce_sys"), "en-US", 100, null, retailScenarios: true, out var problems);
        Assert.NotNull(problems.Layout);
        var result = BuildApp(source, "cs", playGo);
        File.Delete(Path.Combine(source, "sce_sys", SonySdkPlayGo.FicmFileName));
        File.Delete(Path.Combine(source, "sce_sys", SonySdkPlayGo.HashTableFileName));
        var (exit, stdout, stderr) = RunScript(source, Path.Combine(pythonOut, "game.gp5"));
        Assert.True(exit == 0, stderr);
        AssertSameAsScript(pythonOut, result, stdout, compareReport: false);
    }

    [Fact]
    public void SelectedLanguages_AreByteIdenticalToTheToolkitScript()
    {
        if (!CanCompare)
        {
            return;
        }

        var source = MakeSource("languages", sourceTables: true);
        var pythonOut = SonySdkProject.RealPath(Path.Combine(_root, "py"));
        Directory.CreateDirectory(pythonOut);
        var (exit, stdout, stderr) = RunScript(source, Path.Combine(pythonOut, "game.gp5"), "--playgo-languages", "ru-RU,fr-FR");
        Assert.True(exit == 0, stderr);
        var playGo = SonySdkPlayGo.Toolkit(Path.Combine(source, "sce_sys"), "en-US", 100, ["ru-RU", "fr-FR"], retailScenarios: true, out var problems);
        Assert.Equal(new PlayGoToolkitProblems(null, null, null), problems);
        var result = BuildApp(source, "cs", playGo);
        AssertSameAsScript(pythonOut, result, stdout);
        // chunkDefaultLanguage de-DE không có trong danh sách chọn, cũng không có en-US → ngôn ngữ đầu tiên.
        Assert.Contains("\"chunkDefaultLanguage\": \"ru-RU\"", File.ReadAllText(result.ScenarioPath));
    }

    [Fact]
    public void FillEmptyChunks_AddsPlaceholdersOnlyToEmptyDeclaredChunks()
    {
        var source = MakeSource("fill", sourceTables: true);
        File.Delete(Path.Combine(source, "data0.bin"));
        var playGo = SonySdkPlayGo.Toolkit(Path.Combine(source, "sce_sys"), "en-US", 100, null, retailScenarios: false, out _);
        var result = BuildApp(source, "cs", playGo, fillEmptyChunks: true);
        // data0.bin (chunk 1) không còn: chunk 1 nhận tệp giữ chỗ; chunk 2 vẫn có data1/data2.
        Assert.Equal([1], result.FilledChunks);
        Assert.Contains("dst_path=\"playgo-chunks/001.bin\"", File.ReadAllText(result.ProjectPath));
        Assert.Contains("[Warn] PlayGo chunk 1 (Voice JP EN; ja-JP en-US) is missing 1 of its 1 file(s) in the source", result.Report);
    }
}
