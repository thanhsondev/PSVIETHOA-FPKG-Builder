using PsViethoa.FpkgBuilder.Core.Models;
using PsViethoa.FpkgBuilder.Core.Services;
using Xunit;

namespace PsViethoa.FpkgBuilder.Tests;

/// <summary>Bộ giả lập DLC: đọc dlc_emu.ini, phát hiện trong nguồn, loại khỏi gói và tạo gói DLC chỉ quyền sở hữu.</summary>
public sealed class DlcEmuTests : IDisposable
{
    private const string RealIni = """
[PSAC]
content_id=EP9000-PPSA13197_00-COMPLETEREWARD00
download_status=NO_EXTRA_DATA

[PSAC]
content_id=EP9000-PPSA13197_00-STELLARBLADEDLC1
download_status=NO_EXTRA_DATA

; ghi chú bị bỏ qua
[PSAC]
content_id=EP9000-PPSA13197_00-STELLARBLADEDLC2
download_status=NO_EXTRA_DATA
""";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "psviethoa-dlc-" + Guid.NewGuid().ToString("N"));

    public DlcEmuTests() => Directory.CreateDirectory(_root);

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

    private string MakeSource(bool withEmu)
    {
        var folder = Path.Combine(_root, "src-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(folder, "sce_sys"));
        File.WriteAllText(Path.Combine(folder, "sce_sys", "param.json"), "{\"titleId\":\"PPSA13197\"}");
        File.WriteAllBytes(Path.Combine(folder, "eboot.bin"), new byte[64 * 1024]);
        if (withEmu)
        {
            Directory.CreateDirectory(Path.Combine(folder, "fakelib"));
            File.WriteAllText(Path.Combine(folder, DlcEmuInspector.ConfigName), RealIni);
            foreach (var module in new[] { "libSceAppContent.sprx", "libSceNpEntitlementAccess.sprx", "libSceGameUpdate.sprx" })
            {
                File.WriteAllBytes(Path.Combine(folder, "fakelib", module), new byte[4096]);
            }

            File.WriteAllBytes(Path.Combine(folder, "fakelib", "libSceNgs2.sprx"), new byte[2048]);
        }

        return folder;
    }

    [Fact]
    public void Parse_ReadsEveryEntryOnce()
    {
        var entries = DlcEmuIni.Parse(RealIni);
        Assert.Equal(3, entries.Count);
        Assert.Equal("EP9000-PPSA13197_00-COMPLETEREWARD00", entries[0].ContentId);
        Assert.Equal("NO_EXTRA_DATA", entries[0].DownloadStatus);
        Assert.All(entries, e => Assert.True(e.NoExtraData));
        Assert.Equal("COMPLETEREWARD00", entries[0].Label);
        Assert.Equal("STELLARBLADEDLC2", entries[2].Label);
    }

    [Theory]
    [InlineData("")]
    [InlineData("rác\nkhông phải ini")]
    [InlineData("[PSAC]\ndownload_status=NO_EXTRA_DATA")]
    public void Parse_IgnoresGarbage(string text) => Assert.Empty(DlcEmuIni.Parse(text));

    [Fact]
    public void Parse_KeepsEntriesThatShipData()
    {
        var entries = DlcEmuIni.Parse("[PSAC]\ncontent_id=EP9000-PPSA13197_00-WITHDATA00000000\ndownload_status=DOWNLOADED");
        var entry = Assert.Single(entries);
        Assert.False(entry.NoExtraData);
    }

    [Fact]
    public void Scan_FindsConfigAndModules()
    {
        var info = DlcEmuInspector.ScanFolder(MakeSource(withEmu: true), CancellationToken.None);
        Assert.True(info.Present);
        Assert.True(info.Config);
        Assert.Equal(4, info.Paths.Count);
        Assert.Contains("dlc_emu.ini", info.Paths);
        Assert.Contains("fakelib/libSceAppContent.sprx", info.Paths);
        Assert.DoesNotContain("fakelib/libSceNgs2.sprx", info.Paths);

        Assert.False(DlcEmuInspector.ScanFolder(MakeSource(withEmu: false), CancellationToken.None).Present);
    }

    [Fact]
    public void Read_LoadsTheIniFromAFolderOrAFileDirectly()
    {
        var folder = MakeSource(withEmu: true);
        Assert.Equal(3, DlcEmuIni.Read(folder, CancellationToken.None).Count);
        Assert.Equal(3, DlcEmuIni.Read(Path.Combine(folder, DlcEmuInspector.ConfigName), CancellationToken.None).Count);
        Assert.Empty(DlcEmuIni.Read(MakeSource(withEmu: false), CancellationToken.None));
    }

    [Fact]
    public async Task ExportDlcTemplate_WritesSceSysAndAGp5ThatRebuildsTheDlc()
    {
        if (!BuildEngine.KeysAvailable)
        {
            return;
        }

        var entries = DlcEmuIni.Parse(RealIni);
        var built = DlcPackageBuilder.BuildAll(entries.Take(1).ToList(), Path.Combine(_root, "tpl-dlc"), Path.Combine(_root, "tpl-tmp"), "Stellar Blade", null, CancellationToken.None);
        var package = Assert.Single(built).OutputPath!;
        var passcode = new string('0', 32);
        Assert.True(PackageInspector.Inspect(package, passcode, CancellationToken.None).IsDlcWithData);

        var folder = PackageReader.SuggestDlcTemplateFolder(package);
        Assert.EndsWith("-dlc-template", folder, StringComparison.Ordinal);
        var files = PackageReader.ExportDlcTemplate(package, folder, passcode, CancellationToken.None);

        var contentId = entries[0].ContentId;
        Assert.Contains("sce_sys/param.json", files);
        Assert.Contains(contentId + ".gp5", files);
        var project = File.ReadAllText(Path.Combine(folder, contentId + ".gp5"));
        Assert.Contains("prospero_ac", project, StringComparison.Ordinal);
        Assert.Contains(contentId, project, StringComparison.Ordinal);

        // Thư mục đã có nội dung: không ghi đè, gợi ý thư mục kế tiếp.
        Assert.Throws<IOException>(() => PackageReader.ExportDlcTemplate(package, folder, passcode, CancellationToken.None));
        Assert.EndsWith("-dlc-template (2)", PackageReader.SuggestDlcTemplateFolder(package), StringComparison.Ordinal);

        // Mẫu dùng được ngay: thêm dữ liệu rồi đóng gói lại từ tệp .gp5.
        File.WriteAllBytes(Path.Combine(folder, "extra.bin"), new byte[300_000]);
        var rebuilt = await new BuildEngine().BuildAsync(new BuildRequest
        {
            SourcePath = Path.Combine(folder, contentId + ".gp5"),
            OutputFolder = Path.Combine(_root, "tpl-rebuilt"),
            TemporaryFolder = Path.Combine(_root, "tpl-rebuilt-tmp"),
            ContentId = contentId,
            Title = "Stellar Blade DLC",
            Kind = PackageKind.DlcWithData,
            KrakenBackend = KrakenBackendKind.BuiltIn,
            KrakenLevel = -4,
            PreventSleep = false,
        }, _ => { }, null, CancellationToken.None);
        Assert.True(rebuilt.Verification.Contents!.IsValid);
        Assert.Equal(contentId, PackageInspector.Inspect(rebuilt.OutputPath, passcode, CancellationToken.None).ContentId);
    }

    [Fact]
    public void ExportDlcTemplate_RefusesAGamePackage()
    {
        if (!BuildEngine.KeysAvailable)
        {
            return;
        }

        var app = Path.Combine(_root, "tpl-app");
        Directory.CreateDirectory(Path.Combine(app, "sce_sys"));
        File.WriteAllText(Path.Combine(app, "sce_sys", "param.json"), "{\"contentId\":\"UP9000-PPSA26344_00-PSVIETHOATPLAPP1\",\"contentVersion\":\"01.000.000\",\"sdkVersion\":\"0x0450000000000000\",\"localizedParameters\":{\"defaultLanguage\":\"en-US\",\"en-US\":{\"titleName\":\"A\"}}}");
        File.WriteAllBytes(Path.Combine(app, "eboot.bin"), new byte[4096]);
        var outcome = new BuildEngine().BuildAsync(new BuildRequest
        {
            SourcePath = app,
            OutputFolder = Path.Combine(_root, "tpl-app-out"),
            TemporaryFolder = Path.Combine(_root, "tpl-app-tmp"),
            ContentId = "UP9000-PPSA26344_00-PSVIETHOATPLAPP1",
            KrakenBackend = KrakenBackendKind.BuiltIn,
            KrakenLevel = -4,
            PreventSleep = false,
        }, _ => { }, null, CancellationToken.None).GetAwaiter().GetResult();

        Assert.False(PackageInspector.Inspect(outcome.OutputPath, new string('0', 32), CancellationToken.None).IsDlcWithData);
        Assert.Throws<InvalidOperationException>(() => PackageReader.ExportDlcTemplate(outcome.OutputPath, Path.Combine(_root, "tpl-app-template"), new string('0', 32), CancellationToken.None));
    }

    [Fact]
    public void BuildRequest_KeepsTheEmulatorByDefault() => Assert.True(new BuildRequest().KeepDlcEmu);

    [Fact]
    public void BuildAll_MakesOneEntitlementPackagePerEntry()
    {
        if (!BuildEngine.KeysAvailable)
        {
            return;
        }

        var entries = DlcEmuIni.Parse(RealIni + "\n[PSAC]\ncontent_id=EP9000-PPSA13197_00-WITHDATA00000000\ndownload_status=DOWNLOADED\n");
        var output = Path.Combine(_root, "dlc-out");
        var results = DlcPackageBuilder.BuildAll(entries, output, Path.Combine(_root, "dlc-tmp"), "Stellar Blade", null, CancellationToken.None);

        Assert.Equal(4, results.Count);
        Assert.Equal(3, results.Count(r => r.Success));
        var skipped = Assert.Single(results.Where(r => !r.Success));
        Assert.False(skipped.Entry.NoExtraData);

        foreach (var result in results.Where(r => r.Success))
        {
            Assert.True(File.Exists(result.OutputPath));
            Assert.True(result.Size > 0);
            Assert.Contains(result.Entry.Label, Path.GetFileName(result.OutputPath!));
            var info = PackageInspector.Inspect(result.OutputPath!, new string('0', 32), CancellationToken.None);
            Assert.Equal(result.Entry.ContentId, info.ContentId);
        }
    }

    [Fact]
    public async Task Build_DropsFakelibWhenTheCleanupEmptiesIt()
    {
        if (!BuildEngine.KeysAvailable)
        {
            return;
        }

        // Nguồn kiểu Stellar Blade: fakelib chỉ chứa AMPR emu + 3 module DLC emu.
        var folder = MakeSource(withEmu: true);
        File.WriteAllBytes(Path.Combine(folder, "fakelib", "libSceAmpr.sprx"), new byte[4096]);
        File.Delete(Path.Combine(folder, "fakelib", "libSceNgs2.sprx"));

        var request = new BuildRequest
        {
            SourcePath = folder,
            OutputFolder = Path.Combine(_root, "out-empty"),
            TemporaryFolder = Path.Combine(_root, "tmp-empty"),
            ContentId = "EP9000-PPSA13197_00-PSVIETHOAEMPTY01",
            Title = "Empty fakelib",
            KrakenLevel = -4,
            KeepDlcEmu = false,
        };

        var outcome = await new BuildEngine().BuildAsync(request, _ => { }, null, CancellationToken.None);
        using var reader = PackageReader.Open(outcome.OutputPath, new string('0', 32), request.TemporaryFolder, CancellationToken.None);
        var entries = reader.Entries.Select(e => e.Path).ToList();

        Assert.DoesNotContain(entries, p => p.StartsWith("fakelib", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(entries, p => p.Equals("eboot.bin", StringComparison.OrdinalIgnoreCase));

        // Thư mục nguồn phải nguyên vẹn: fakelib được tạo lại kèm đủ 4 tệp.
        Assert.True(Directory.Exists(Path.Combine(folder, "fakelib")));
        Assert.Equal(4, Directory.GetFiles(Path.Combine(folder, "fakelib")).Length);
        Assert.True(File.Exists(Path.Combine(folder, DlcEmuInspector.ConfigName)));
    }

    [Fact]
    public async Task Build_StripsTheEmulatorOnlyWhenAsked()
    {
        if (!BuildEngine.KeysAvailable)
        {
            return;
        }

        async Task<List<string>> BuildAsync(bool strip, string tag)
        {
            var request = new BuildRequest
            {
                SourcePath = MakeSource(withEmu: true),
                OutputFolder = Path.Combine(_root, "out-" + tag),
                TemporaryFolder = Path.Combine(_root, "tmp-" + tag),
                ContentId = "EP9000-PPSA13197_00-PSVIETHOADLCTEST",
                Title = "DLC emu test",
                KrakenLevel = -4,
                KeepDlcEmu = !strip,
            };

            var outcome = await new BuildEngine().BuildAsync(request, _ => { }, null, CancellationToken.None);
            using var reader = PackageReader.Open(outcome.OutputPath, new string('0', 32), Path.Combine(_root, "tmp-" + tag), CancellationToken.None);
            return reader.Entries.Where(e => !e.IsDirectory).Select(e => e.Path).ToList();
        }

        var kept = await BuildAsync(strip: false, "keep");
        Assert.Contains("dlc_emu.ini", kept);
        Assert.Contains("fakelib/libSceAppContent.sprx", kept);

        var stripped = await BuildAsync(strip: true, "strip");
        Assert.DoesNotContain("dlc_emu.ini", stripped);
        Assert.DoesNotContain("fakelib/libSceAppContent.sprx", stripped);
        Assert.DoesNotContain("fakelib/libSceGameUpdate.sprx", stripped);
        Assert.Contains("fakelib/libSceNgs2.sprx", stripped);
    }
}
