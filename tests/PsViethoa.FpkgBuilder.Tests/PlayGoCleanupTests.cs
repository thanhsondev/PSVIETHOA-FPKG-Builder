using System.Text.Json;
using LibProsperoPkg.GP5;
using PsViethoa.FpkgBuilder.Core.ExFat;
using PsViethoa.FpkgBuilder.Core.Models;
using PsViethoa.FpkgBuilder.Core.Services;
using Xunit;

namespace PsViethoa.FpkgBuilder.Tests;

/// <summary>
/// Sửa lỗi PlayGo (game không khởi chạy / màn hình đen): bỏ bộ sce_sys/playgo* của bản dump, GIỮ fakelib/libScePlayGo.sprx,
/// xoá versionFileUri và attribute3 trong param.json — cho mọi loại nguồn (thư mục, ảnh exFAT, dự án GP5 Normal và Flat) và
/// không đụng gì tới tệp trong thư mục nguồn.
/// </summary>
public sealed class PlayGoCleanupTests : IDisposable
{
    private const string Passcode = "00000000000000000000000000000000";
    private const string VersionUri = "https://sgst.prod.dl.playstation.net/sgst/prod/PPSA26344/4/f_1234567890abcdef/f/PPSA26344_00.json";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "psviethoa-playgo-" + Guid.NewGuid().ToString("N"));

    public PlayGoCleanupTests() => Directory.CreateDirectory(_root);

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

    /// <summary>Bản dump kiểu Ghost of Yotei: sce_sys có bộ playgo*, fakelib có libScePlayGo.sprx cùng module backport khác.</summary>
    private string MakeSource(string name, bool withPlayGo = true, string? contentId = null)
    {
        var folder = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.Combine(folder, "sce_sys"));
        Directory.CreateDirectory(Path.Combine(folder, "fakelib"));
        File.WriteAllText(Path.Combine(folder, "sce_sys", "param.json"), JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["contentId"] = contentId ?? "UP9000-PPSA26344_00-PSVIETHOAPLAYGO0",
            ["contentVersion"] = "01.000.000",
            ["sdkVersion"] = "0x0450000000000000",
            ["applicationDrmType"] = "free",
            ["versionFileUri"] = VersionUri,
            ["attribute"] = 0,
            ["attribute2"] = 0,
            ["attribute3"] = 4160,
            ["localizedParameters"] = new Dictionary<string, object>
            {
                ["defaultLanguage"] = "en-US",
                ["en-US"] = new Dictionary<string, string> { ["titleName"] = "PlayGo test" },
            },
        }));
        File.WriteAllBytes(Path.Combine(folder, "eboot.bin"), new byte[64 * 1024]);
        File.WriteAllBytes(Path.Combine(folder, "fakelib", "libSceNgs2.sprx"), new byte[1024]);
        if (withPlayGo)
        {
            var random = new byte[2000];
            Random.Shared.NextBytes(random);
            foreach (var playgo in new[] { "playgo-chunk.dat", "playgo-hash-table.dat", "playgo-ficm.dat", "playgo-scenario.json" })
            {
                File.WriteAllBytes(Path.Combine(folder, "sce_sys", playgo), random);
            }

            File.WriteAllBytes(Path.Combine(folder, "fakelib", "libScePlayGo.sprx"), new byte[2048]);
        }

        return folder;
    }

    private BuildRequest Request(string source, string suffix, string contentId = "UP9000-PPSA26344_00-PSVIETHOAPLAYGO0") => new()
    {
        SourcePath = source,
        OutputFolder = Path.Combine(_root, "out-" + suffix),
        TemporaryFolder = Path.Combine(_root, "tmp-" + suffix),
        ContentId = contentId,
        Title = "PlayGo test",
        KrakenBackend = KrakenBackendKind.BuiltIn,
        KrakenLevel = -4,
        PreventSleep = false,
    };

    private static List<string> Entries(string package, string temporary)
    {
        using var reader = PackageReader.Open(package, Passcode, temporary, CancellationToken.None);
        return reader.Entries.Where(e => !e.IsDirectory).Select(e => e.Path.Replace('\\', '/')).ToList();
    }

    // ===================== Liệt kê =====================

    [Theory]
    [InlineData("playgo-chunk.dat", true)]
    [InlineData("PLAYGO-scenario.json", true)]
    [InlineData("param.json", false)]
    [InlineData("nptitle.dat", false)]
    public void IsPlayGoFile_MatchesThePrefixOnly(string name, bool expected) => Assert.Equal(expected, PlayGoCleanup.IsPlayGoFile(name));

    [Fact]
    public void IsCandidate_CoversSceSysOnly_NeverTheFakelibModule()
    {
        Assert.True(PlayGoCleanup.IsCandidate("sce_sys/playgo-chunk.dat"));
        Assert.True(PlayGoCleanup.IsCandidate("sce_sys\\playgo-ficm.dat"));
        Assert.False(PlayGoCleanup.IsCandidate("playgo-chunk.dat"));
        Assert.False(PlayGoCleanup.IsCandidate("sce_sys/param.json"));
        Assert.False(PlayGoCleanup.IsCandidate("ampr_emu.index"));

        // Module trong fakelib không bao giờ nằm trong danh sách công cụ bỏ (engine tự quyết số phận của nó).
        Assert.False(PlayGoCleanup.IsCandidate(PlayGoCleanup.EmuModule));
        Assert.Equal("fakelib/libScePlayGo.sprx", PlayGoCleanup.EmuModule);
    }

    [Fact]
    public void ListFolder_FindsTheFourPlayGoFilesAndNotTheModule()
    {
        var files = PlayGoCleanup.ListFolder(MakeSource("list"));
        Assert.Equal(4, files.Count);
        Assert.All(files, file => Assert.StartsWith("sce_sys/", file, StringComparison.Ordinal));
        Assert.Contains("sce_sys/playgo-chunk.dat", files);
        Assert.Contains("sce_sys/playgo-hash-table.dat", files);
        Assert.Contains("sce_sys/playgo-ficm.dat", files);
        Assert.Contains("sce_sys/playgo-scenario.json", files);
        Assert.DoesNotContain(PlayGoCleanup.EmuModule, files);
        Assert.DoesNotContain("sce_sys/param.json", files);

        Assert.Empty(PlayGoCleanup.ListFolder(MakeSource("list-none", withPlayGo: false)));
        Assert.Empty(PlayGoCleanup.ListFolder(Path.Combine(_root, "missing")));
        Assert.Equal("playgo-chunk.dat, playgo-ficm.dat", PlayGoCleanup.Describe(new[] { "sce_sys/playgo-chunk.dat", "sce_sys/playgo-ficm.dat" }));
    }

    [Fact]
    public void ListImage_ReadsAnExFatImageWithoutTouchingTheModule()
    {
        var target = Path.Combine(_root, "small-bare.exfat");
        using (var input = new System.IO.Compression.GZipStream(
                   File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", "small-bare.exfat.gz")),
                   System.IO.Compression.CompressionMode.Decompress))
        using (var output = File.Create(target))
        {
            input.CopyTo(output);
        }

        using var image = ExFatImage.Open(target);
        var files = PlayGoCleanup.ListImage(image, image.Root);
        Assert.All(files, file => Assert.StartsWith("sce_sys/", file, StringComparison.Ordinal));
        Assert.DoesNotContain(PlayGoCleanup.EmuModule, files);
    }

    // ===================== param.json =====================

    [Fact]
    public void BuildRequest_AppliesTheWholeGuideByDefault()
    {
        var request = new BuildRequest();
        Assert.True(request.RemovePlayGoFiles);
        Assert.True(request.ClearVersionFileUri);
        Assert.True(request.ClearPlayGoAttributes);
        Assert.True(request.ForceStandardDrm);
        Assert.True(request.ParamPatch.Any);
        Assert.False(ParamJsonPatchOptions.None.Any);
    }

    [Fact]
    public void ParamPatch_ClearsTheThreeFieldsAndRestoresByteForByte()
    {
        var folder = MakeSource("patch");
        var param = Path.Combine(folder, "sce_sys", "param.json");
        var original = File.ReadAllBytes(param);

        using (var patch = ParamJsonPatch.Apply(param, new ParamJsonPatchOptions(true, true, true), null))
        {
            Assert.NotNull(patch);
            Assert.Equal(3, patch!.Changes.Count);
            using var document = JsonDocument.Parse(File.ReadAllBytes(param));
            Assert.Equal("standard", document.RootElement.GetProperty("applicationDrmType").GetString());
            Assert.Equal(string.Empty, document.RootElement.GetProperty("versionFileUri").GetString());
            Assert.Equal(0, document.RootElement.GetProperty("attribute3").GetInt32());

            // Các trường khác giữ nguyên.
            Assert.Equal("01.000.000", document.RootElement.GetProperty("contentVersion").GetString());
            Assert.Equal("PlayGo test", document.RootElement.GetProperty("localizedParameters").GetProperty("en-US").GetProperty("titleName").GetString());
        }

        Assert.Equal(original, File.ReadAllBytes(param));
    }

    [Fact]
    public void ParamPatch_TouchesOnlyTheFieldsItWasAskedFor()
    {
        var param = Path.Combine(MakeSource("patch-one"), "sce_sys", "param.json");
        var source = File.ReadAllBytes(param);

        Assert.NotNull(ParamJsonPatch.Rewrite(source, new ParamJsonPatchOptions(false, true, false), out var uriOnly));
        Assert.Single(uriOnly);
        Assert.Contains("versionFileUri", uriOnly[0], StringComparison.Ordinal);

        Assert.NotNull(ParamJsonPatch.Rewrite(source, new ParamJsonPatchOptions(false, false, true), out var attributeOnly));
        Assert.Single(attributeOnly);
        Assert.Contains("attribute3", attributeOnly[0], StringComparison.Ordinal);

        Assert.Null(ParamJsonPatch.Rewrite(source, ParamJsonPatchOptions.None, out var none));
        Assert.Empty(none);

        // Giá trị đã đúng sẵn thì không ghi gì.
        var clean = Path.Combine(MakeSource("patch-noop"), "sce_sys", "param.json");
        File.WriteAllText(clean, "{\"applicationDrmType\":\"standard\",\"versionFileUri\":\"\",\"attribute3\":0}");
        Assert.Null(ParamJsonPatch.Apply(clean, new ParamJsonPatchOptions(true, true, true), null));
    }

    // ===================== Tạo gói thật: thư mục =====================

    [Fact]
    public async Task Build_FromAFolder_DropsPlayGoDataKeepsTheModuleAndPatchesParam()
    {
        if (!BuildEngine.KeysAvailable)
        {
            return;
        }

        var folder = MakeSource("build");
        var param = Path.Combine(folder, "sce_sys", "param.json");
        var originalParam = File.ReadAllBytes(param);
        var originalChunk = File.ReadAllBytes(Path.Combine(folder, "sce_sys", "playgo-chunk.dat"));
        var request = Request(folder, "build");
        var log = new List<LogEntry>();

        var outcome = await new BuildEngine().BuildAsync(request, log.Add, null, CancellationToken.None);
        var entries = Entries(outcome.OutputPath, request.TemporaryFolder);

        // Module backport khác trong fakelib vẫn nằm trong gói; fakelib/libScePlayGo.sprx bị chính engine loại (như libSceAmpr.sprx).
        Assert.Contains(entries, e => e.EndsWith("libSceNgs2.sprx", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(entries, e => e.EndsWith("libScePlayGo.sprx", StringComparison.OrdinalIgnoreCase));

        // Bộ playgo* trong gói là bản thư viện tạo, không phải dữ liệu ngẫu nhiên của bản dump.
        var export = Path.Combine(_root, "export-build");
        PackageReader.ExportSceSys(outcome.OutputPath, export, Passcode, CancellationToken.None);
        Assert.NotEqual(originalChunk, File.ReadAllBytes(Path.Combine(export, "sce_sys", "playgo-chunk.dat")));

        // param.json trong gói đã được sửa cả ba trường.
        using (var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(export, "sce_sys", "param.json"))))
        {
            Assert.Equal("standard", document.RootElement.GetProperty("applicationDrmType").GetString());
            Assert.True(string.IsNullOrWhiteSpace(document.RootElement.GetProperty("versionFileUri").GetString()));
            Assert.Equal(0, document.RootElement.GetProperty("attribute3").GetInt32());
        }

        Assert.Contains(log, e => e.Message.Contains("playgo-chunk.dat", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(log, e => e.Message.Contains("versionFileUri", StringComparison.Ordinal));
        Assert.Contains(log, e => e.Message.Contains("attribute3", StringComparison.Ordinal));

        // Thư mục nguồn nguyên vẹn, kể cả module trong fakelib.
        Assert.Equal(originalParam, File.ReadAllBytes(param));
        Assert.Equal(originalChunk, File.ReadAllBytes(Path.Combine(folder, "sce_sys", "playgo-chunk.dat")));
        Assert.Equal(4, PlayGoCleanup.ListFolder(folder).Count);
        Assert.True(File.Exists(Path.Combine(folder, "fakelib", "libScePlayGo.sprx")));
        // Thư mục trích tạm phải được dọn; thư mục tạm rỗng do lần tạo gói sinh ra cũng bị xoá luôn.
        if (Directory.Exists(request.TemporaryFolder))
        {
            Assert.Empty(Directory.GetDirectories(request.TemporaryFolder, "hidden-*"));
        }
    }

    [Fact]
    public async Task Build_KeepsEverythingWhenTheOptionsAreOff()
    {
        if (!BuildEngine.KeysAvailable)
        {
            return;
        }

        var folder = MakeSource("keep");
        var request = Request(folder, "keep", "UP9000-PPSA26344_00-PSVIETHOAPLAYGO1");
        request.RemovePlayGoFiles = false;
        request.ClearVersionFileUri = false;
        request.ClearPlayGoAttributes = false;
        request.ForceStandardDrm = false;

        var log = new List<LogEntry>();
        var outcome = await new BuildEngine().BuildAsync(request, log.Add, null, CancellationToken.None);
        var export = Path.Combine(_root, "export-keep");
        PackageReader.ExportSceSys(outcome.OutputPath, export, Passcode, CancellationToken.None);

        using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(export, "sce_sys", "param.json")));
        Assert.StartsWith(VersionUri, document.RootElement.GetProperty("versionFileUri").GetString(), StringComparison.Ordinal);
        Assert.Equal(4160, document.RootElement.GetProperty("attribute3").GetInt32());
        Assert.Equal("free", document.RootElement.GetProperty("applicationDrmType").GetString());
        Assert.DoesNotContain(log, e => e.Message.Contains("playgo-chunk.dat", StringComparison.OrdinalIgnoreCase));
    }

    // ===================== Tạo gói thật: dự án GP5 =====================

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Build_FromAGp5Project_DropsPlayGoDataAndRestoresTheProjectFile(bool flat)
    {
        if (!BuildEngine.KeysAvailable)
        {
            return;
        }

        var suffix = flat ? "gp5-flat" : "gp5-normal";
        var contentId = "UP9000-PPSA26344_00-PSVIETHOAPLAYG" + (flat ? "0F" : "0N");
        var folder = MakeSource(suffix + "-src", contentId: contentId);
        var projects = Path.Combine(_root, suffix + "-proj");
        Directory.CreateDirectory(projects);
        var projectPath = Path.Combine(projects, "project.gp5");
        Gp5Project.WriteTo(
            flat
                ? Gp5Creator.FromFolderExplicit(folder, Gp5VolumeType.prospero_app, Passcode)
                : Gp5Creator.FromFolder(folder, Gp5VolumeType.prospero_app, Passcode, null),
            projectPath);
        var originalProject = File.ReadAllBytes(projectPath);
        var originalParam = File.ReadAllBytes(Path.Combine(folder, "sce_sys", "param.json"));

        var request = Request(projectPath, suffix, contentId);
        var outcome = await new BuildEngine().BuildAsync(request, _ => { }, null, CancellationToken.None);
        var entries = Entries(outcome.OutputPath, request.TemporaryFolder);

        Assert.DoesNotContain(entries, e => e.Contains("playgo-hash-table.dat", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(entries, e => e.Contains("playgo-ficm.dat", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(entries, e => e.Contains("playgo-scenario.json", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(entries, e => e.Equals("eboot.bin", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(entries, e => e.EndsWith("libSceNgs2.sprx", StringComparison.OrdinalIgnoreCase));

        // Dự án và thư mục nguồn nguyên vẹn.
        Assert.Equal(originalProject, File.ReadAllBytes(projectPath));
        Assert.Equal(originalParam, File.ReadAllBytes(Path.Combine(folder, "sce_sys", "param.json")));
        Assert.Equal(4, PlayGoCleanup.ListFolder(folder).Count);
    }

}
