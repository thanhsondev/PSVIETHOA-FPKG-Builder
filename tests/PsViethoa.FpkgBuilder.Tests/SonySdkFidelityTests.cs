using System.Diagnostics;
using System.Text;
using PsViethoa.FpkgBuilder.Core.Services;
using Xunit;

namespace PsViethoa.FpkgBuilder.Tests;

/// <summary>
/// GP5 của công cụ phải giống từng byte với <c>create-gp5-from-folder.py</c> của bộ sdk-fpkg729-fix (thứ tự tệp quyết định bố cục
/// gói của Publishing Tools). So trực tiếp với script Python khi máy có python3; các phép thử còn lại không cần Python.
/// </summary>
public sealed class SonySdkFidelityTests : IDisposable
{
    private const string Passcode = "00000000000000000000000000000000";
    private const string ContentId = "EP9999-PPSA99997_00-PSVIETHOAFIDEL00";

    /// <summary>Tên có ký tự XML phải escape; Windows cấm &lt; &gt; " trong tên tệp nên chỉ thử &amp; và ' ở đó.</summary>
    private static readonly string XmlName = OperatingSystem.IsWindows() ? "A&B 'q' #1.bin" : "A&B <1> \"q\".bin";

    /// <summary>Script gốc chạy trên Windows ghi tệp văn bản với "\r\n" (Python chế độ văn bản); công cụ ghi đúng như vậy trên mọi máy.</summary>
    private static string Crlf(string text) => text.Replace("\r\n", "\n").Replace("\n", "\r\n");

    private static string Crlf(byte[] bytes) => Crlf(Encoding.UTF8.GetString(bytes));

    private readonly string _root = Path.Combine(Path.GetTempPath(), "psviethoa-sdkfid-" + Guid.NewGuid().ToString("N"));

    public SonySdkFidelityTests() => Directory.CreateDirectory(_root);

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

    /// <summary>Cây tệp có đủ ca khó: khoảng trắng, ký tự XML, ß/Ü (casefold ≠ lower), hoa/thường, tệp SDK tự sinh, tệp rác, dự án cũ.</summary>
    private string MakeTree()
    {
        var source = Path.Combine(_root, "src");
        var created = MakeTreeAt(source);
        return SonySdkProject.RealPath(created);
    }

    private static string MakeTreeAt(string source)
    {
        foreach (var directory in new[] { "sce_sys/about", "Data Files/sub", "fakelib", "zeta", "sce_sys/trophy2" })
        {
            Directory.CreateDirectory(Path.Combine(source, directory));
        }

        File.WriteAllText(Path.Combine(source, "sce_sys", "param.json"), "{\"contentId\":\"" + ContentId + "\",\"localizedParameters\":{\"defaultLanguage\":\"ja-JP\"}}");
        File.WriteAllBytes(Path.Combine(source, "sce_sys", "keystone"), new byte[96]);
        File.WriteAllBytes(Path.Combine(source, "sce_sys", "playgo-chunk.dat"), new byte[4]);
        File.WriteAllBytes(Path.Combine(source, "sce_sys", "icon0.dds"), new byte[4]);
        File.WriteAllBytes(Path.Combine(source, "sce_sys", "icon0.png"), new byte[4]);
        File.WriteAllBytes(Path.Combine(source, "sce_sys", "about", "right.sprx"), new byte[4]);
        File.WriteAllBytes(Path.Combine(source, "sce_sys", "trophy2", "trophy00.ucp"), new byte[4]);
        File.WriteAllBytes(Path.Combine(source, "eboot.bin"), new byte[16]);
        File.WriteAllBytes(Path.Combine(source, "Data Files", "sub", "b.bin"), new byte[1]);
        File.WriteAllBytes(Path.Combine(source, "Data Files", XmlName), new byte[1]);
        File.WriteAllBytes(Path.Combine(source, "Data Files", "Straße.bin"), new byte[1]);
        File.WriteAllBytes(Path.Combine(source, "zeta", "Über.dat"), new byte[1]);
        File.WriteAllBytes(Path.Combine(source, "Zeta.bin"), new byte[1]);
        File.WriteAllBytes(Path.Combine(source, "apple.bin"), new byte[1]);
        File.WriteAllBytes(Path.Combine(source, "fakelib", "libSceAmpr.sprx"), new byte[1]);
        File.WriteAllBytes(Path.Combine(source, "ampr_emu.index"), new byte[1]);
        File.WriteAllBytes(Path.Combine(source, ".DS_Store"), new byte[1]);
        File.WriteAllText(Path.Combine(source, "old.gp5"), "<psproject/>");
        File.WriteAllText(Path.Combine(source, "notes.playgo-scenario.json"), "{}");
        File.WriteAllBytes(Path.Combine(source, "sce_sys", "license.dat"), new byte[4]);
        File.WriteAllBytes(Path.Combine(source, "sce_sys", "license.info"), new byte[4]);
        File.WriteAllBytes(Path.Combine(source, "fakelib", "libScePlayGo.sprx"), new byte[1]);
        Directory.CreateDirectory(Path.Combine(source, ".gp5-assets", "old"));
        File.WriteAllBytes(Path.Combine(source, ".gp5-assets", "old", "x.txt"), new byte[1]);
        return source;
    }

    [Fact]
    public void CaseFold_MatchesPython()
    {
        Assert.Equal("strasse", PythonCaseFold.Fold("Straße"));
        Assert.Equal("über", PythonCaseFold.Fold("Über"));
        Assert.Equal("data files", PythonCaseFold.Fold("Data Files"));
        Assert.Equal("ffi", PythonCaseFold.Fold("ﬃ"));
        Assert.Equal("σ", PythonCaseFold.Fold("ς"));
        Assert.Equal("i\u0307", PythonCaseFold.Fold("İ"));
        Assert.Equal("ascii.BIN".ToLowerInvariant(), PythonCaseFold.Fold("ascii.BIN"));
        Assert.Same("already", PythonCaseFold.Fold("already"));
    }

    [Fact]
    public void NameOrder_MatchesPythonSorted()
    {
        // sorted(names, key=str.casefold) của CPython 3.9 trên cùng danh sách.
        var names = new[] { "zeta", "Zeta.bin", "Data Files", "apple.bin", ".DS_Store", "Straße.bin", "sub", "sce_sys", "eboot.bin", "ampr_emu.index", "fakelib", "old.gp5", "Über.dat", "ünder.bin", "\u00DFeta" };
        var sorted = names.OrderBy(name => name, PythonCaseFold.NameComparer).ToArray();
        Assert.Equal(
            [".DS_Store", "ampr_emu.index", "apple.bin", "Data Files", "eboot.bin", "fakelib", "old.gp5", "sce_sys", "\u00DFeta", "Straße.bin", "sub", "zeta", "Zeta.bin", "Über.dat", "ünder.bin"],
            sorted);
    }

    [Fact]
    public void CodePointOrder_NotUtf16Order()
    {
        // U+FF5E (BMP) < U+1F600 (astral): Python so theo mã điểm; UTF-16 thuần thì surrogate D83D đứng trước FF5E.
        Assert.True(PythonCaseFold.CompareCodePoints("\uFF5E", "\U0001F600") < 0);
        Assert.True(string.CompareOrdinal("\uFF5E", "\U0001F600") > 0);
    }

    [Fact]
    public void Gp5_MatchesReferenceTextWithoutPython()
    {
        var source = MakeTree();
        var work = SonySdkProject.RealPath(Path.Combine(_root, "cs"));
        var result = SonySdkProject.Create(source, Path.Combine(work, "game.gp5"), Passcode, path => "Z:" + path.Replace('/', '\\'), SonySdkSourcePlan.Pure);

        var expectedFiles = new (string Dst, string Src)[]
        {
            ("sce_sys/playgo-scenario.json", Path.Combine(work, "game.playgo-scenario.json")),
            (".DS_Store", Path.Combine(source, ".DS_Store")),
            ("apple.bin", Path.Combine(source, "apple.bin")),
            ("Data Files/" + XmlName, Path.Combine(source, "Data Files", XmlName)),
            ("Data Files/Straße.bin", Path.Combine(source, "Data Files", "Straße.bin")),
            ("Data Files/sub/b.bin", Path.Combine(source, "Data Files", "sub", "b.bin")),
            ("eboot.bin", Path.Combine(source, "eboot.bin")),
            ("sce_sys/icon0.png", Path.Combine(source, "sce_sys", "icon0.png")),
            ("sce_sys/keystone", Path.Combine(source, "sce_sys", "keystone")),
            ("sce_sys/param.json", Path.Combine(work, ".gp5-assets", "game", "sce_sys", "param.json")),
            ("sce_sys/trophy2/trophy00.ucp", Path.Combine(source, "sce_sys", "trophy2", "trophy00.ucp")),
            ("zeta/Über.dat", Path.Combine(source, "zeta", "Über.dat")),
            ("Zeta.bin", Path.Combine(source, "Zeta.bin")),
        };
        var expected = new StringBuilder()
            .Append("<?xml version='1.0' encoding='utf-8'?>\n")
            .Append("<psproject fmt=\"gp5\">\n  <volume>\n    <volume_type>prospero_app</volume_type>\n")
            .Append("    <package passcode=\"" + Passcode + "\" content_id=\"" + ContentId + "\" />\n")
            .Append("    <chunk_info chunk_count=\"1\" scenario_count=\"1\">\n      <chunks>\n        <chunk id=\"0\" label=\"Chunk #0\" />\n      </chunks>\n")
            .Append("      <scenarios default_id=\"0\">\n        <scenario id=\"0\" type=\"playmode\" initial_chunk_count=\"1\" label=\"Scenario #0\">0</scenario>\n      </scenarios>\n")
            .Append("    </chunk_info>\n  </volume>\n  <files>\n");
        foreach (var (dst, src) in expectedFiles)
        {
            expected.Append("    <file dst_path=\"").Append(SonySdkProject.EscapeAttribute(dst)).Append("\" src_path=\"").Append(SonySdkProject.EscapeAttribute("Z:" + src.Replace('/', '\\'))).Append("\" />\n");
        }

        expected.Append("  </files>\n</psproject>");
        Assert.Equal(Crlf(expected.ToString()), File.ReadAllText(result.ProjectPath));
        Assert.Equal(expectedFiles.Length, result.FileCount);
        Assert.Equal(
            Crlf("{\n  \"scenarioCount\": 1,\n  \"scenarioDefaultId\": 0,\n  \"scenarioDefaultLanguage\": \"ja-JP\",\n  \"scenarios\": [\n    {\n      \"id\": 0,\n      \"type\": \"playmode\",\n      \"ja-JP\": {\n        \"title\": \"Scenario #0\",\n        \"description\": \"Default play scenario\"\n      }\n    }\n  ]\n}\n"),
            File.ReadAllText(result.ScenarioPath));
        Assert.Equal(
            [
                ".gp5-assets/ (generated GP5 assets)",
                "ampr_emu.index (host emulator index)",
                "fakelib/libSceAmpr.sprx (SDK/runtime fakelib module)",
                "fakelib/libScePlayGo.sprx (SDK/runtime fakelib module)",
                "notes.playgo-scenario.json (generated GP5 scenario sidecar)",
                "old.gp5 (project artifact)",
                "sce_sys/about/right.sprx (reserved/SDK-generated sce_sys artifact)",
                "sce_sys/icon0.dds (reserved/SDK-generated sce_sys artifact)",
                "sce_sys/license.dat (reserved/SDK-generated sce_sys artifact)",
                "sce_sys/license.info (reserved/SDK-generated sce_sys artifact)",
                "sce_sys/playgo-chunk.dat (reserved/SDK-generated sce_sys artifact)",
            ],
            result.Excluded);

        // write_standard_param: json.dumps(indent=2, ensure_ascii=False) + "\n", applicationDrmType thêm vào cuối khi nguồn không có.
        Assert.Equal(Path.Combine(work, ".gp5-assets", "game", "sce_sys", "param.json"), result.ParamJsonPath);
        Assert.Equal(
            Crlf("{\n  \"contentId\": \"" + ContentId + "\",\n  \"localizedParameters\": {\n    \"defaultLanguage\": \"ja-JP\"\n  },\n  \"applicationDrmType\": \"standard\"\n}\n"),
            File.ReadAllText(result.ParamJsonPath));
        Assert.Empty(result.ParamChanges);
        Assert.Empty(result.RecoveredPngs);
    }

    [Fact]
    public void PythonJson_MatchesJsonDumps()
    {
        var node = new System.Text.Json.Nodes.JsonObject
        {
            ["a"] = 1,
            ["b"] = new System.Text.Json.Nodes.JsonArray(),
            ["c"] = new System.Text.Json.Nodes.JsonObject(),
            ["d"] = "x\"y\\z\n\t\u0001é漢",
            ["e"] = true,
            ["f"] = null,
            ["g"] = new System.Text.Json.Nodes.JsonArray(1, "two", new System.Text.Json.Nodes.JsonObject { ["h"] = false }),
        };
        Assert.Equal(
            "{\n  \"a\": 1,\n  \"b\": [],\n  \"c\": {},\n  \"d\": \"x\\\"y\\\\z\\n\\t\\u0001é漢\",\n  \"e\": true,\n  \"f\": null,\n  \"g\": [\n    1,\n    \"two\",\n    {\n      \"h\": false\n    }\n  ]\n}",
            PythonJson.Serialize(node));

        // Số đọc từ JSON giữ nguyên chữ số; số thực theo repr() của Python.
        var parsed = System.Text.Json.Nodes.JsonNode.Parse("{\"i\": 4718660, \"j\": 1.50, \"k\": 100000000000000000000, \"l\": 1e-7}")!;
        Assert.Equal("{\n  \"i\": 4718660,\n  \"j\": 1.5,\n  \"k\": 100000000000000000000,\n  \"l\": 1e-07\n}", PythonJson.Serialize(parsed));
    }

    [Fact]
    public void MissingPresentationPngs_FollowTheScriptRules()
    {
        var source = Path.Combine(_root, "pngs", "sce_sys");
        Directory.CreateDirectory(source);
        foreach (var name in new[] { "pic0.dds", "PIC1.DDS", "pic1.png", "pic2.dds", "icon0.dds", "picture.dds", "pic3.DDS" })
        {
            File.WriteAllBytes(Path.Combine(source, name), new byte[4]);
        }

        var missing = SonySdkProject.MissingPresentationPngs(Path.Combine(_root, "pngs"));
        Assert.Equal(["pic0.png", "pic2.png", "pic3.png", "picture.png"], missing.Select(item => item.PngName));
        Assert.Equal(["pic0.dds", "pic2.dds", "pic3.DDS", "picture.dds"], missing.Select(item => Path.GetFileName(item.DdsPath)));
    }

    [Fact]
    public void Recovery_RunsTheConverterAndListsPngsAfterTheScenario()
    {
        var source = MakeTree();
        File.WriteAllBytes(Path.Combine(source, "sce_sys", "pic2.dds"), new byte[16]);
        File.WriteAllBytes(Path.Combine(source, "sce_sys", "pic0.dds"), new byte[16]);
        var calls = new List<(string Dds, string Png, bool Alpha)>();
        void Convert(string dds, string png, bool alpha)
        {
            calls.Add((Path.GetFileName(dds), png, alpha));
            File.WriteAllBytes(png, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3]);
        }

        var work = SonySdkProject.RealPath(Path.Combine(_root, "rec"));
        var result = SonySdkProject.Create(source, Path.Combine(work, "game.gp5"), Passcode, path => path, SonySdkSourcePlan.Pure, ddsConverter: Convert);

        Assert.Equal([("pic0.dds", Path.Combine(work, ".gp5-assets", "game", "sce_sys", "pic0.png"), false), ("pic2.dds", Path.Combine(work, ".gp5-assets", "game", "sce_sys", "pic2.png"), true)], calls);
        var lines = File.ReadAllLines(result.ProjectPath);
        var files = lines.Where(line => line.Contains("<file ", StringComparison.Ordinal)).Select(line => line.Split("dst_path=\"")[1].Split('"')[0]).ToList();
        Assert.Equal(["sce_sys/playgo-scenario.json", "sce_sys/pic0.png", "sce_sys/pic2.png"], files.Take(3));
        Assert.StartsWith("Recovering sce_sys/pic0.png from pic0.dds...\nRecovering sce_sys/pic2.png from pic2.dds...\nCreated ", result.Report);
        Assert.Equal(2, result.RecoveredPngs.Count);

        // Không có bộ chuyển đổi: lỗi rõ ràng như script gốc, không tạo GP5.
        var error = Assert.Throws<InvalidOperationException>(() => SonySdkProject.Create(source, Path.Combine(_root, "rec2", "game.gp5"), Passcode, path => path, SonySdkSourcePlan.Pure));
        Assert.Contains("pic0.dds", error.Message);
    }

    /// <summary>Chạy chính script Python của bộ công cụ (kèm trong libs/sony-sdk) trên cùng cây tệp và so từng byte.</summary>
    [Fact]
    public void Gp5_IsByteIdenticalToTheToolkitScript()
    {
        var script = FindToolkitScript();
        var python = FindPython();
        if (script == null || python == null)
        {
            return;
        }

        var source = MakeTree();
        var pythonOut = SonySdkProject.RealPath(Path.Combine(_root, "py"));
        var sharpOut = SonySdkProject.RealPath(Path.Combine(_root, "cs"));
        Directory.CreateDirectory(pythonOut);
        File.WriteAllText(Path.Combine(source, "sce_sys", "param.json"), "{\"contentId\":\"" + ContentId + "\",\"versionFileUri\":\"\",\"attribute3\":0,\"localizedParameters\":{\"defaultLanguage\":\"ja-JP\",\"ja-JP\":{\"titleName\":\"Yōtei 格雷克 \\\"q\\\"\"}},\"applicationDrmType\":\"free\"}");

        // Khôi phục PNG: cùng prospero-dds2png.exe cho cả hai bên (script nhận bộ chuyển đổi .py → bọc Wine trên macOS).
        var runtime = SonySdkToolchain.Resolve(out _);
        var converterExe = runtime == null ? null : Path.Combine(runtime.ToolkitRoot, SonySdkBuilder.DdsConverterFileName);
        Action<string, string, bool>? converter = null;
        var extra = new List<string>();
        if (converterExe != null && File.Exists(converterExe))
        {
            using (var gz = new System.IO.Compression.GZipStream(File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", "bc7-icon0.dds.gz")), System.IO.Compression.CompressionMode.Decompress))
            using (var dds = File.Create(Path.Combine(source, "sce_sys", "pic0.dds")))
            {
                gz.CopyTo(dds);
            }

            File.Copy(Path.Combine(source, "sce_sys", "pic0.dds"), Path.Combine(source, "sce_sys", "pic2.dds"));
            string converterForScript;
            if (runtime!.UsesWine)
            {
                converterForScript = Path.Combine(_root, "dds2png-wine.py");
                File.WriteAllText(converterForScript,
                    "import os, subprocess, sys\n" +
                    "def z(p): return 'Z:' + os.path.abspath(p).replace('/', '\\\\')\n" +
                    "a = sys.argv[1:]\n" +
                    "env = dict(os.environ, WINEPREFIX=" + Quote(runtime.WinePrefix!) + ", WINEDEBUG='-all', WINEDLLOVERRIDES='mscoree,mshtml=')\n" +
                    "sys.exit(subprocess.call([" + Quote(runtime.WinePath!) + ", " + Quote(runtime.ToolPath(converterExe)) + ", z(a[0]), z(a[1])] + a[2:], env=env, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL))\n");
            }
            else
            {
                converterForScript = converterExe;
            }

            extra.AddRange(["--dds-converter", converterForScript]);
            converter = (dds, png, alpha) =>
            {
                var arguments = new List<string> { runtime.ToolPath(dds), runtime.ToolPath(png) };
                if (alpha)
                {
                    arguments.Add("--preserve-alpha");
                }

                var (exit, output) = SonySdkRunner.RunToolAsync(runtime, converterExe, arguments, CancellationToken.None).GetAwaiter().GetResult();
                Assert.True(exit == 0, output);
            };
        }

        var startInfo = new ProcessStartInfo(python)
        {
            ArgumentList = { script, source, Path.Combine(pythonOut, "game.gp5"), "--passcode", Passcode, "--absolute-paths", "--keep-keystone" },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in extra)
        {
            startInfo.ArgumentList.Add(argument);
        }

        var run = Process.Start(startInfo)!;
        var stdout = run.StandardOutput.ReadToEnd();
        var stderr = run.StandardError.ReadToEnd();
        run.WaitForExit();
        Assert.True(run.ExitCode == 0, stderr);

        var result = SonySdkProject.Create(source, Path.Combine(sharpOut, "game.gp5"), Passcode, path => path, SonySdkSourcePlan.Pure, ddsConverter: converter);
        // Python trên macOS/Linux ghi "\n", trên Windows ghi "\r\n": chuẩn là bộ công cụ chạy trên Windows.
        var expectedGp5 = Crlf(File.ReadAllText(Path.Combine(pythonOut, "game.gp5"))).Replace(SonySdkProject.EscapeAttribute(pythonOut), SonySdkProject.EscapeAttribute(sharpOut));
        Assert.Equal(expectedGp5, File.ReadAllText(result.ProjectPath));
        Assert.Equal(Crlf(File.ReadAllBytes(Path.Combine(pythonOut, "game.playgo-scenario.json"))), Encoding.UTF8.GetString(File.ReadAllBytes(result.ScenarioPath)));
        Assert.Equal(Crlf(File.ReadAllBytes(Path.Combine(pythonOut, ".gp5-assets", "game", "sce_sys", "param.json"))), Encoding.UTF8.GetString(File.ReadAllBytes(result.ParamJsonPath)));
        Assert.Contains("\"applicationDrmType\": \"standard\"", File.ReadAllText(result.ParamJsonPath));
        // Đầu ra console: Windows đổi "\n" thành "\r\n" khi in; nội dung nhật ký so theo dòng.
        Assert.Equal(stdout.Replace("\r\n", "\n").Replace(pythonOut, sharpOut), result.Report);
        foreach (var png in result.RecoveredPngs)
        {
            Assert.Equal(File.ReadAllBytes(Path.Combine(pythonOut, ".gp5-assets", "game", "sce_sys", Path.GetFileName(png.PngPath))), File.ReadAllBytes(png.PngPath));
        }

        Assert.Equal(converter == null ? 0 : 2, result.RecoveredPngs.Count);
    }

    [Fact]
    public void Symlink_IsRejectedLikeTheScript()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var source = MakeTree();
        File.CreateSymbolicLink(Path.Combine(source, "link.bin"), Path.Combine(source, "apple.bin"));
        var error = Assert.Throws<InvalidDataException>(() => SonySdkProject.Create(source, Path.Combine(_root, "link", "game.gp5"), Passcode, path => path, SonySdkSourcePlan.Pure));
        Assert.Contains("link.bin", error.Message);
    }

    [Fact]
    public void Plan_SkipsJunkAndOptionsAndPointsAtReplacements()
    {
        var source = MakeTree();
        var replacement = SonySdkProject.RealPath(Path.Combine(_root, "replace", "sce_sys", "param.json"));
        Directory.CreateDirectory(Path.GetDirectoryName(replacement)!);
        File.WriteAllText(replacement, "{\"contentId\":\"" + ContentId + "\",\"applicationDrmType\":\"standard\"}");
        File.WriteAllText(replacement, "{\"contentId\":\"" + ContentId + "\",\"versionFileUri\":\"https://example/x.json\",\"applicationDrmType\":\"free\"}");
        var plan = new SonySdkSourcePlan(
            new HashSet<string>(["zeta/Über.dat"], StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["sce_sys/param.json"] = replacement },
            SkipJunk: true,
            new ParamJsonPatchOptions(false, ClearVersionFileUri: true, ClearPlayGoAttributes: false));

        var work = SonySdkProject.RealPath(Path.Combine(_root, "plan"));
        var result = SonySdkProject.Create(source, Path.Combine(work, "game.gp5"), Passcode, path => path, plan);
        var gp5 = File.ReadAllText(result.ProjectPath);
        Assert.DoesNotContain(".DS_Store", gp5);
        Assert.DoesNotContain("Über", gp5);
        var normalized = Path.Combine(work, ".gp5-assets", "game", "sce_sys", "param.json");
        Assert.Contains("dst_path=\"sce_sys/param.json\" src_path=\"" + SonySdkProject.EscapeAttribute(normalized) + "\"", gp5);
        Assert.DoesNotContain(SonySdkProject.EscapeAttribute(replacement), gp5);
        Assert.Equal([".DS_Store (OS junk file)", "zeta/Über.dat (excluded by option)"], result.SkippedByApp);
        Assert.Equal(["sce_sys/param.json"], result.Replaced);
        Assert.Contains("Skipped 2 file(s) by PSVIETHOA FPKG Builder options:", result.Report);
        Assert.Equal(ContentId, result.ContentId);

        // Bản thay thế là đầu vào của bước chuẩn hoá: DRM → standard (luôn), versionFileUri xoá theo tuỳ chọn, ghi lại kiểu Python.
        Assert.Equal(Crlf("{\n  \"contentId\": \"" + ContentId + "\",\n  \"versionFileUri\": \"\",\n  \"applicationDrmType\": \"standard\"\n}\n"), File.ReadAllText(normalized));
        Assert.Equal(2, result.ParamChanges.Count);
    }

    /// <summary>Huỷ giữa lúc ghi GP5 (ví dụ toolPath chậm trên ổ USB) phải dừng trong vài nghìn tệp, không đợi hết cả danh sách.</summary>
    [Fact]
    public void Create_StopsPromptlyWhenCancelledWhileWritingTheGp5()
    {
        var source = Path.Combine(_root, "many");
        Directory.CreateDirectory(Path.Combine(source, "sce_sys"));
        File.WriteAllText(Path.Combine(source, "sce_sys", "param.json"), "{\"contentId\":\"" + ContentId + "\"}");
        File.WriteAllBytes(Path.Combine(source, "sce_sys", "keystone"), new byte[96]);
        for (var folder = 0; folder < 20; folder++)
        {
            var directory = Path.Combine(source, "d" + folder);
            Directory.CreateDirectory(directory);
            for (var file = 0; file < 500; file++)
            {
                File.WriteAllBytes(Path.Combine(directory, $"f{file:000}.bin"), new byte[1]);
            }
        }

        using var cancellation = new CancellationTokenSource();
        var mapped = 0;
        string ToolPath(string path)
        {
            if (++mapped == 3000)
            {
                cancellation.Cancel();
            }

            return path;
        }

        Assert.Throws<OperationCanceledException>(() => SonySdkProject.Create(source, Path.Combine(_root, "many-out", "game.gp5"), Passcode, ToolPath, SonySdkSourcePlan.Pure, cancellation.Token));
        Assert.InRange(mapped, 3000, 5001);
        Assert.False(File.Exists(Path.Combine(_root, "many-out", "game.gp5")));
    }

    [Fact]
    public void ToolchainIn_AcceptsBothLayouts()
    {
        var nested = Path.Combine(_root, "kit");
        Directory.CreateDirectory(Path.Combine(nested, "toolchain"));
        File.WriteAllBytes(Path.Combine(nested, "toolchain", SonySdkToolchain.PublisherFileName), new byte[1]);
        Assert.Equal(Path.Combine(nested, "toolchain"), SonySdkToolchain.ToolchainIn(nested));
        Assert.Equal(Path.Combine(nested, "toolchain"), SonySdkToolchain.ToolchainIn(Path.Combine(nested, "toolchain")));
        Assert.Null(SonySdkToolchain.ToolchainIn(_root));
        Assert.Equal(nested, new SonySdkRuntime(Path.Combine(nested, "toolchain"), null, null).ToolkitRoot);
    }

    private static string Quote(string value) => "'" + value.Replace("\\", "\\\\").Replace("'", "\\'") + "'";

    private static string? FindToolkitScript()
    {
        var current = AppContext.BaseDirectory;
        for (var depth = 0; depth < 8 && current != null; depth++)
        {
            var candidate = Path.Combine(current, "libs", "sony-sdk", "scripts", "create-gp5-from-folder.py");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            current = Path.GetDirectoryName(current);
        }

        return null;
    }

    private static string? FindPython()
    {
        foreach (var candidate in new[] { "/usr/bin/python3", "/opt/homebrew/bin/python3", "/usr/local/bin/python3", "python3", "python" })
        {
            try
            {
                using var probe = Process.Start(new ProcessStartInfo(candidate, "--version") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
                if (probe != null)
                {
                    probe.WaitForExit(10_000);
                    if (probe.ExitCode == 0)
                    {
                        return candidate;
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        return null;
    }
}
