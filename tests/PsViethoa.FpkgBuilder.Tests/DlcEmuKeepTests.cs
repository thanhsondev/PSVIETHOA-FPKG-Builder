using System.Text.Json;
using PsViethoa.FpkgBuilder.Core.Models;
using PsViethoa.FpkgBuilder.Core.Services;
using Xunit;

namespace PsViethoa.FpkgBuilder.Tests;

/// <summary>
/// Issue #9 (Thomas H): "Builder luôn xoá dlc_emu.ini dù bật Giữ bộ giả lập DLC trong gói". dlc_emu.ini chưa từng bị bỏ khi bật tuỳ chọn,
/// nhưng ở chế độ SDK Sony thư mục gắn DLC rỗng (mount_point của dlc_emu.ini) mất khỏi gói — GP5 phẳng chỉ liệt kê tệp, còn DLC-emu không
/// tự tạo thư mục gắn — nên DLC không gắn được; nhật ký lại ghi "._dlc_emu.ini" ngay sau chữ "bộ giả lập DLC" như thể đã bị xoá.
/// </summary>
public sealed class DlcEmuKeepTests : IDisposable
{
    private const string Passcode = "00000000000000000000000000000000";
    private const string ContentId = "EP9999-PPSA99992_00-PSVIETHOADLCEMU0";

    private const string Ini =
        "[PSAL]\r\ncontent_id=EP9999-PPSA99992_00-SAMPLELICENSE001\r\nlabel=SAMPLELICENSE001\r\n\r\n" +
        "[PSAC]\r\ncontent_id=EP9999-PPSA99992_00-SAMPLECONTENT001\r\ndownload_status=INSTALLED\r\nmount_point=/app0/addcont0\r\n\r\n" +
        "[PSAC]\r\ncontent_id=EP9999-PPSA99992_00-SAMPLECONTENT002\r\ndownload_status=INSTALLED\r\nmount_point=/app0/addcont1\r\n";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "psviethoa-dlckeep-" + Guid.NewGuid().ToString("N"));

    public DlcEmuKeepTests() => Directory.CreateDirectory(_root);

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

    /// <summary>Game có bộ giả lập DLC: dlc_emu.ini, 2 module trong fakelib, addcont0 rỗng, addcont1 có dữ liệu, tệp phụ macOS ._dlc_emu.ini.</summary>
    private string MakeSource(string name, string version = "01.000.000")
    {
        var folder = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.Combine(folder, "sce_sys"));
        Directory.CreateDirectory(Path.Combine(folder, "fakelib"));
        Directory.CreateDirectory(Path.Combine(folder, "addcont0"));
        Directory.CreateDirectory(Path.Combine(folder, "addcont1"));
        File.WriteAllText(Path.Combine(folder, "sce_sys", "param.json"), JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["contentId"] = ContentId,
            ["contentVersion"] = version,
            ["masterVersion"] = "01.00",
            ["titleId"] = "PPSA99992",
            ["sdkVersion"] = "0x0450000000000000",
            ["requiredSystemSoftwareVersion"] = "0x0450000000000000",
            ["applicationCategoryType"] = 0,
            ["applicationDrmType"] = "standard",
            ["localizedParameters"] = new Dictionary<string, object>
            {
                ["defaultLanguage"] = "en-US",
                ["en-US"] = new Dictionary<string, string> { ["titleName"] = "DLC emu test" },
            },
        }));
        File.WriteAllBytes(Path.Combine(folder, "sce_sys", "keystone"), new byte[96]);
        File.WriteAllBytes(Path.Combine(folder, "eboot.bin"), Enumerable.Range(0, 64 * 1024).Select(i => (byte)(i * 7)).ToArray());
        File.WriteAllText(Path.Combine(folder, DlcEmuInspector.ConfigName), Ini);
        File.WriteAllBytes(Path.Combine(folder, "._" + DlcEmuInspector.ConfigName), new byte[4096]);
        File.WriteAllBytes(Path.Combine(folder, "fakelib", "libSceAppContent.sprx"), Enumerable.Range(0, 4096).Select(i => (byte)i).ToArray());
        File.WriteAllBytes(Path.Combine(folder, "fakelib", "libSceNpEntitlementAccess.sprx"), Enumerable.Range(0, 4096).Select(i => (byte)(i * 3)).ToArray());
        File.WriteAllBytes(Path.Combine(folder, "addcont1", "data.bin"), Enumerable.Range(0, 2048).Select(i => (byte)(i * 5)).ToArray());
        return folder;
    }

    private BuildRequest Request(string source, string output, bool useSdk) => new()
    {
        UseSonySdk = useSdk,
        SourcePath = source,
        OutputFolder = Path.Combine(_root, output),
        TemporaryFolder = Path.Combine(_root, "tmp-" + output),
        ContentId = ContentId,
        Title = "DLC emu test",
        KrakenBackend = KrakenBackendKind.BuiltIn,
        KrakenLevel = -4,
        PreventSleep = false,
    };

    private HashSet<string> PackageEntries(string package)
    {
        using var reader = PackageReader.Open(package, Passcode, Path.Combine(_root, "read-tmp"), CancellationToken.None);
        return new HashSet<string>(reader.Entries.Select(entry => entry.Path.Replace('\\', '/').Trim('/')), StringComparer.Ordinal);
    }

    private static readonly string[] Emulator = ["dlc_emu.ini", "fakelib/libSceAppContent.sprx", "fakelib/libSceNpEntitlementAccess.sprx"];

    // ===================== Thư mục gắn DLC =====================

    [Theory]
    [InlineData("/app0/addcont0", "addcont0")]
    [InlineData("/APP0/dlc/a1/", "dlc/a1")]
    [InlineData(" /app0//addcont3 ", "addcont3")]
    [InlineData("\\app0\\addcont4", "addcont4")]
    [InlineData("/app0/", null)]
    [InlineData("/app0/../escape", null)]
    [InlineData("/data/addcont0", null)]
    [InlineData("addcont0", null)]
    [InlineData(null, null)]
    public void RelativeMountPoint_AcceptsOnlyPathsUnderApp0(string? mountPoint, string? expected) =>
        Assert.Equal(expected, DlcEmuInspector.RelativeMountPoint(mountPoint));

    [Fact]
    public void FindMounts_ReadsExplicitAndDefaultMountFoldersWithTheirRealCase()
    {
        var game = Path.Combine(_root, "mounts");
        Directory.CreateDirectory(Path.Combine(game, "AddCont0"));
        Directory.CreateDirectory(Path.Combine(game, "addcont1"));
        Directory.CreateDirectory(Path.Combine(game, "addcont7"));
        Directory.CreateDirectory(Path.Combine(game, "addcontX"));
        File.WriteAllText(Path.Combine(game, DlcEmuInspector.ConfigName), Ini +
            "\r\n[PSAC]\r\ncontent_id=EP9999-PPSA99992_00-SAMPLECONTENT003\r\nmount_point=/app0/dlc3\r\n" +
            "\r\n[PSAC]\r\ncontent_id=EP9999-PPSA99992_00-SAMPLECONTENT004\r\nmount_point=/data/outside\r\n" +
            "\r\n[PSAC]\r\ncontent_id=EP9999-PPSA99992_00-SAMPLECONTENT005\r\n");

        var mounts = DlcEmuInspector.FindMounts(game);

        // addcont0 trong ini khớp thư mục AddCont0 (giữ tên trên đĩa); addcont7 là thư mục mặc định /app0/addcontN của mục không ghi mount_point.
        Assert.Equal(["AddCont0", "addcont1", "addcont7"], mounts.Existing);
        Assert.Equal(["dlc3"], mounts.Missing);
    }

    [Fact]
    public void FindMounts_UsesTheUpdateFoldersIniAndLooksInBothFolders()
    {
        var game = Path.Combine(_root, "base");
        var update = Path.Combine(_root, "update");
        Directory.CreateDirectory(Path.Combine(game, "addcont0"));
        Directory.CreateDirectory(Path.Combine(update, "dlc9"));
        File.WriteAllText(Path.Combine(game, DlcEmuInspector.ConfigName), "[PSAC]\ncontent_id=EP9999-PPSA99992_00-OLDCONTENT000001\nmount_point=/app0/old\n");
        File.WriteAllText(Path.Combine(update, DlcEmuInspector.ConfigName), "[PSAC]\ncontent_id=EP9999-PPSA99992_00-NEWCONTENT000001\nmount_point=/app0/addcont0\n[PSAC]\ncontent_id=EP9999-PPSA99992_00-NEWCONTENT000002\nmount_point=/app0/dlc9\n");

        var mounts = DlcEmuInspector.FindMounts(game, update);

        Assert.Equal(["addcont0", "dlc9"], mounts.Existing);
        Assert.Empty(mounts.Missing);
    }

    // ===================== GP5 =====================

    /// <summary>Thư mục giữ mà không chứa tệp nào → &lt;empty_dir&gt; đúng cú pháp Publishing Tools, sau mọi tệp; có tệp thì không cần.</summary>
    [Fact]
    public void Project_DeclaresOnlyEmptyKeptFoldersAfterTheFiles()
    {
        var source = MakeSource("gp5");
        Directory.CreateDirectory(Path.Combine(source, "_DUBLEX_", "addcont9"));
        var plan = SonySdkSourcePlan.Pure with
        {
            Skip = new HashSet<string>(["_DUBLEX_"], StringComparer.OrdinalIgnoreCase),
            SkipJunk = true,
            KeepDirectories = ["addcont0", "addcont1", "_DUBLEX_/addcont9", "missing-dir"],
        };

        var result = SonySdkProject.Create(source, Path.Combine(_root, "gp5-out", "game.gp5"), Passcode, path => path, plan);

        var gp5 = File.ReadAllText(result.ProjectPath).Replace("\r\n", "\n");
        Assert.EndsWith("    <empty_dir dst_path=\"addcont0\" />\n    <empty_dir dst_path=\"missing-dir\" />\n  </files>\n</psproject>", gp5);
        Assert.Equal(["addcont0", "missing-dir"], result.KeptEmptyDirectories);
        Assert.Contains("dst_path=\"addcont1/data.bin\"", gp5);
        Assert.Contains("dst_path=\"dlc_emu.ini\"", gp5);
        Assert.DoesNotContain("_DUBLEX_", gp5);
        Assert.Contains("Kept 2 empty folder(s) as <empty_dir> entries (DLC mount folders from dlc_emu.ini, empty source folders):\n  addcont0/\n  missing-dir/\n", result.Report);

        // Tệp phụ macOS và thư mục bỏ theo tuỳ chọn ghi riêng: "._dlc_emu.ini" là rác, dlc_emu.ini thật vẫn trong GP5.
        Assert.Equal(["._dlc_emu.ini"], result.SkippedJunk);
        Assert.Equal(["_DUBLEX_/"], result.SkippedByOption);
    }

    /// <summary>Không có thư mục cần giữ (game không có dlc_emu.ini hoặc tắt giữ bộ giả lập): GP5 giống hệt như trước.</summary>
    [Fact]
    public void Project_WithoutKeptFoldersMatchesTheToolkitLayout()
    {
        var source = MakeSource("plain");
        var first = SonySdkProject.Create(source, Path.Combine(_root, "plain-a", "game.gp5"), Passcode, path => path.Replace(Path.Combine(_root, "plain-a"), "OUT"));
        var second = SonySdkProject.Create(source, Path.Combine(_root, "plain-b", "game.gp5"), Passcode, path => path.Replace(Path.Combine(_root, "plain-b"), "OUT"), SonySdkSourcePlan.Pure with { KeepDirectories = [] });

        Assert.Equal(File.ReadAllBytes(first.ProjectPath), File.ReadAllBytes(second.ProjectPath));
        Assert.DoesNotContain("empty_dir", File.ReadAllText(first.ProjectPath));
        Assert.Empty(first.KeptEmptyDirectories);
    }

    // ===================== Build thật =====================

    [Fact]
    public async Task SdkBuild_KeepsTheDlcEmulatorAndItsEmptyMountFolder()
    {
        if (SonySdkToolchain.Resolve(out _) == null)
        {
            return;
        }

        var source = MakeSource("sdk");
        var log = new List<LogEntry>();
        var outcome = await new BuildEngine().BuildAsync(Request(source, "sdk-keep", useSdk: true), log.Add, null, CancellationToken.None);

        var entries = PackageEntries(outcome.OutputPath);
        Assert.All(Emulator, path => Assert.Contains(path, entries));
        Assert.Contains("addcont0", entries);
        Assert.Contains("addcont1/data.bin", entries);
        Assert.DoesNotContain("._dlc_emu.ini", entries);
        Assert.Contains(log, entry => entry.Level == LogLevel.Success && entry.Message.Contains("dlc_emu.ini", StringComparison.Ordinal) && entry.Message.Contains("addcont0", StringComparison.Ordinal));
        Assert.DoesNotContain(log, entry => entry.Level == LogLevel.Warning && entry.Message.Contains("dlc_emu.ini", StringComparison.Ordinal));

        // Tắt giữ bộ giả lập: dlc_emu.ini + module bị bỏ như người dùng chọn; addcont0 vẫn là thư mục rỗng của nguồn nên vẫn có (như
        // engine tích hợp) — trừ khi tắt cả giữ thư mục rỗng, khi đó GP5 như script gốc.
        var strip = Request(source, "sdk-strip", useSdk: true);
        strip.KeepDlcEmu = false;
        var stripped = PackageEntries((await new BuildEngine().BuildAsync(strip, _ => { }, null, CancellationToken.None)).OutputPath);
        Assert.All(Emulator, path => Assert.DoesNotContain(path, stripped));
        Assert.Contains("addcont0", stripped);
        Assert.Contains("addcont1/data.bin", stripped);

        var script = Request(source, "sdk-script", useSdk: true);
        script.KeepDlcEmu = false;
        script.SdkKeepEmptyFolders = false;
        var scripted = PackageEntries((await new BuildEngine().BuildAsync(script, _ => { }, null, CancellationToken.None)).OutputPath);
        Assert.DoesNotContain("addcont0", scripted);
        Assert.Contains("addcont1/data.bin", scripted);
    }

    [Fact]
    public async Task EngineBuild_KeepsTheDlcEmulatorAndItsEmptyMountFolder()
    {
        if (!BuildEngine.KeysAvailable)
        {
            return;
        }

        var source = MakeSource("engine");
        var log = new List<LogEntry>();
        var outcome = await new BuildEngine().BuildAsync(Request(source, "engine-keep", useSdk: false), log.Add, null, CancellationToken.None);

        var entries = PackageEntries(outcome.OutputPath);
        Assert.All(Emulator, path => Assert.Contains(path, entries));
        Assert.Contains("addcont0", entries);
        Assert.Contains(log, entry => entry.Level == LogLevel.Success && entry.Message.Contains("dlc_emu.ini", StringComparison.Ordinal) && entry.Message.Contains("addcont0", StringComparison.Ordinal));
    }

    /// <summary>Gói Update từ thư mục chỉ có tệp đổi: phần còn lại lấy từ gói base — thư mục gắn DLC rỗng của base phải còn trong gói remastered.</summary>
    [Fact]
    public async Task UpdateFromAChangedFilesFolder_KeepsTheBasesEmptyMountFolder()
    {
        if (SonySdkToolchain.Resolve(out _) == null)
        {
            return;
        }

        var source = MakeSource("patch-base");
        var baseOutcome = await new BuildEngine().BuildAsync(Request(source, "patch-base-out", useSdk: true), _ => { }, null, CancellationToken.None);
        Assert.Contains("addcont0", PackageEntries(baseOutcome.OutputPath));

        var update = Path.Combine(_root, "patch-update");
        Directory.CreateDirectory(Path.Combine(update, "sce_sys"));
        var param = JsonSerializer.Deserialize<Dictionary<string, object>>(File.ReadAllText(Path.Combine(source, "sce_sys", "param.json")))!;
        param["contentVersion"] = "01.001.000";
        File.WriteAllText(Path.Combine(update, "sce_sys", "param.json"), JsonSerializer.Serialize(param));
        File.WriteAllBytes(Path.Combine(update, "eboot.bin"), Enumerable.Range(0, 64 * 1024).Select(i => (byte)(i * 11)).ToArray());

        var request = Request(update, "patch-out", useSdk: true);
        request.SdkReferencePackage = baseOutcome.OutputPath;
        request.Version = "01.001.000";
        var log = new List<LogEntry>();
        var outcome = await new BuildEngine().BuildAsync(request, log.Add, null, CancellationToken.None);

        var remastered = PackageEntries(SonySdkPatchReference.CompanionPathOf(outcome.OutputPath));
        Assert.All(Emulator, path => Assert.Contains(path, remastered));
        Assert.Contains("addcont0", remastered);
        Assert.Contains(log, entry => entry.Level == LogLevel.Success && entry.Message.Contains("addcont0", StringComparison.Ordinal));
        Assert.DoesNotContain(log, entry => entry.Level == LogLevel.Warning && entry.Message.Contains("/app0/addcont0", StringComparison.Ordinal));
    }
}
