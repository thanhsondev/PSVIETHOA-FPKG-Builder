using System.Text.Json;
using LibProsperoPkg.Content;
using PsViethoa.FpkgBuilder.Core.Models;
using PsViethoa.FpkgBuilder.Core.Services;
using Xunit;

namespace PsViethoa.FpkgBuilder.Tests;

/// <summary>
/// Báo lỗi 2.2.6: gói tạo bằng SDK Sony chơi được một đoạn rồi lỗi, còn bản dump và gói của fpkg-gui 0.6.11 thì không. Những chỗ gói SDK
/// khác gói của engine tích hợp mà game chỉ dùng tới giữa chừng: thư mục rỗng bị mất, UCP lệch digest (cúp/hoạt động), gói DLC tạo ở
/// dạng Native trong khi gói game là PLAINTEXT_NOAUTH.
/// </summary>
public sealed class SdkParityTests : IDisposable
{
    private const string Passcode = "00000000000000000000000000000000";
    private const string ContentId = "EP9999-PPSA99994_00-PSVIETHOAPARITY0";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "psviethoa-parity-" + Guid.NewGuid().ToString("N"));

    public SdkParityTests() => Directory.CreateDirectory(_root);

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

    /// <summary>
    /// Game có thư mục rỗng ở nhiều kiểu: movies (rỗng), extras/sub (rỗng lồng nhau), junkonly (chỉ có .DS_Store), sce_sys/emptydir
    /// (trong sce_sys), fakelib chỉ có module bị bỏ, _DUBLEX_ bị bỏ theo tuỳ chọn.
    /// </summary>
    private string MakeSource(string name)
    {
        var folder = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.Combine(folder, "sce_sys", "emptydir"));
        Directory.CreateDirectory(Path.Combine(folder, "movies"));
        Directory.CreateDirectory(Path.Combine(folder, "extras", "sub"));
        Directory.CreateDirectory(Path.Combine(folder, "junkonly"));
        Directory.CreateDirectory(Path.Combine(folder, "fakelib"));
        Directory.CreateDirectory(Path.Combine(folder, "_DUBLEX_", "inner"));
        Directory.CreateDirectory(Path.Combine(folder, "data"));
        File.WriteAllText(Path.Combine(folder, "sce_sys", "param.json"), JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["contentId"] = ContentId,
            ["contentVersion"] = "01.000.000",
            ["masterVersion"] = "01.00",
            ["titleId"] = "PPSA99994",
            ["sdkVersion"] = "0x0450000000000000",
            ["requiredSystemSoftwareVersion"] = "0x0450000000000000",
            ["applicationCategoryType"] = 0,
            ["applicationDrmType"] = "standard",
            ["localizedParameters"] = new Dictionary<string, object>
            {
                ["defaultLanguage"] = "en-US",
                ["en-US"] = new Dictionary<string, string> { ["titleName"] = "Parity test" },
            },
        }));
        File.WriteAllBytes(Path.Combine(folder, "sce_sys", "keystone"), new byte[96]);
        File.WriteAllBytes(Path.Combine(folder, "eboot.bin"), Enumerable.Range(0, 64 * 1024).Select(i => (byte)(i * 7)).ToArray());
        File.WriteAllBytes(Path.Combine(folder, "data", "level1.bin"), Enumerable.Range(0, 8192).Select(i => (byte)(i * 3)).ToArray());
        File.WriteAllBytes(Path.Combine(folder, "junkonly", ".DS_Store"), new byte[64]);
        File.WriteAllBytes(Path.Combine(folder, "fakelib", "libSceAmpr.sprx"), new byte[512]);
        File.WriteAllBytes(Path.Combine(folder, "_DUBLEX_", "inner", "x.bin"), new byte[16]);
        return folder;
    }

    private BuildRequest Request(string source, string output, bool useSdk) => new()
    {
        UseSonySdk = useSdk,
        SourcePath = source,
        OutputFolder = Path.Combine(_root, output),
        TemporaryFolder = Path.Combine(_root, "tmp-" + output),
        ContentId = ContentId,
        Title = "Parity test",
        KrakenBackend = KrakenBackendKind.BuiltIn,
        KrakenLevel = -4,
        PreventSleep = false,
    };

    private HashSet<string> PackageEntries(string package)
    {
        using var reader = PackageReader.Open(package, Passcode, Path.Combine(_root, "read-tmp"), CancellationToken.None);
        return new HashSet<string>(reader.Entries.Select(entry => entry.Path.Replace('\\', '/').Trim('/')), StringComparer.Ordinal);
    }

    private static Dictionary<string, string> Snapshot(string folder) =>
        Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .ToDictionary(path => Path.GetRelativePath(folder, path), path => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))));

    /// <summary>src_path của mục dst_path trong GP5 (đường dẫn máy, đã bỏ escape XML).</summary>
    private static string SourceOf(string gp5, string destination)
    {
        var match = System.Text.RegularExpressions.Regex.Match(gp5, "<file dst_path=\"" + System.Text.RegularExpressions.Regex.Escape(destination) + "\" src_path=\"([^\"]*)\"");
        Assert.True(match.Success, destination + " is not in the GP5");
        return System.Net.WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    private static byte[] Ucp(params (string Name, int Size)[] entries) =>
        ProsperoUcp.Build(entries.Select(entry => new UcpEntry(entry.Name, Enumerable.Range(0, entry.Size).Select(i => (byte)(i * 13 + entry.Name.Length)).ToArray())));

    // ===================== Thư mục rỗng =====================

    [Fact]
    public void Project_DeclaresEveryEmptySourceFolder()
    {
        var source = MakeSource("empty");
        var plan = SonySdkSourcePlan.Pure with
        {
            Skip = new HashSet<string>(["_DUBLEX_"], StringComparer.OrdinalIgnoreCase),
            SkipJunk = true,
            KeepEmptyDirectories = true,
        };

        var result = SonySdkProject.Create(source, Path.Combine(_root, "empty-out", "game.gp5"), Passcode, path => path, plan);
        var gp5 = File.ReadAllText(result.ProjectPath).Replace("\r\n", "\n");

        // Thư mục rỗng thật (cả thư mục chỉ có tệp rác) — không phải thư mục trong sce_sys, thư mục chỉ rỗng vì tệp bị loại, hay thư mục bị bỏ.
        Assert.Equal(["extras/sub", "junkonly", "movies"], result.KeptEmptyDirectories);
        Assert.EndsWith(
            "    <empty_dir dst_path=\"extras/sub\" />\n    <empty_dir dst_path=\"junkonly\" />\n    <empty_dir dst_path=\"movies\" />\n  </files>\n</psproject>",
            gp5);
        Assert.DoesNotContain("emptydir", gp5);
        Assert.DoesNotContain("dst_path=\"fakelib\"", gp5);
        Assert.DoesNotContain("_DUBLEX_", gp5);
    }

    /// <summary>Tắt tuỳ chọn: GP5 đúng như trước (như script) — không có &lt;empty_dir&gt; nào.</summary>
    [Fact]
    public void Project_WithoutTheOptionMatchesTheToolkit()
    {
        var source = MakeSource("empty-off");
        var off = SonySdkProject.Create(source, Path.Combine(_root, "off-a", "game.gp5"), Passcode, path => path.Replace(Path.Combine(_root, "off-a"), "OUT"));
        var pure = SonySdkProject.Create(source, Path.Combine(_root, "off-b", "game.gp5"), Passcode, path => path.Replace(Path.Combine(_root, "off-b"), "OUT"), SonySdkSourcePlan.Pure with { KeepEmptyDirectories = false });

        Assert.Equal(File.ReadAllBytes(off.ProjectPath), File.ReadAllBytes(pure.ProjectPath));
        Assert.DoesNotContain("empty_dir", File.ReadAllText(off.ProjectPath));
    }

    [Fact]
    public async Task SdkBuild_KeepsEmptyFoldersLikeTheBuiltInEngine()
    {
        if (SonySdkToolchain.Resolve(out _) == null || !BuildEngine.KeysAvailable)
        {
            return;
        }

        var source = MakeSource("build");
        var before = Snapshot(source);
        var log = new List<LogEntry>();
        var sdk = PackageEntries((await new BuildEngine().BuildAsync(Request(source, "sdk-out", useSdk: true), log.Add, null, CancellationToken.None)).OutputPath);
        var engine = PackageEntries((await new BuildEngine().BuildAsync(Request(source, "engine-out", useSdk: false), _ => { }, null, CancellationToken.None)).OutputPath);

        foreach (var folder in new[] { "movies", "extras/sub", "junkonly" })
        {
            Assert.Contains(folder, sdk);
            Assert.Contains(folder, engine);
        }

        Assert.DoesNotContain("junkonly/.DS_Store", sdk);
        Assert.DoesNotContain("junkonly/.DS_Store", engine);
        Assert.Contains(log, entry => entry.Message.Contains("movies/", StringComparison.Ordinal) && entry.Message.Contains("GP5", StringComparison.Ordinal));
        Assert.Equal(before, Snapshot(source));

        // Tắt: như script gốc, thư mục rỗng mất (engine tích hợp thì luôn giữ).
        var off = Request(source, "sdk-off", useSdk: true);
        off.SdkKeepEmptyFolders = false;
        var dropped = PackageEntries((await new BuildEngine().BuildAsync(off, _ => { }, null, CancellationToken.None)).OutputPath);
        Assert.DoesNotContain("movies", dropped);
        Assert.Contains("data/level1.bin", dropped);
    }

    // ===================== UCP =====================

    [Fact]
    public void UcpInspector_TellsValidStaleAndBrokenArchivesApart()
    {
        var valid = Ucp(("TROP.SFM", 300), ("ICON0.PNG", 1000));
        Assert.Equal(UcpState.Valid, UcpInspector.Inspect(valid, out _));

        var stale = valid.ToArray();
        stale[30] ^= 0xFF;
        Assert.Equal(UcpState.StaleDigest, UcpInspector.Inspect(stale, out _));
        Assert.Equal(valid, UcpInspector.Repair(stale));

        Assert.Equal(UcpState.Invalid, UcpInspector.Inspect(new byte[200], out var garbage));
        Assert.NotNull(garbage);
        var truncated = valid[..(valid.Length - 32)];
        Assert.Equal(UcpState.Invalid, UcpInspector.Inspect(truncated, out _));

        Assert.True(UcpInspector.IsCandidate("sce_sys/trophy2/trophy00.ucp"));
        Assert.True(UcpInspector.IsCandidate("sce_sys/uds/uds00.UCP"));
        Assert.False(UcpInspector.IsCandidate("sce_sys/trophy00.ucp"));
        Assert.False(UcpInspector.IsCandidate("data/trophy2/trophy00.ucp"));
    }

    /// <summary>UCP lệch digest: GP5 trỏ sang bản đã tính lại digest trong .gp5-assets; UCP đúng giữ nguyên; UCP hỏng báo lại. Nguồn không đổi.</summary>
    [Fact]
    public void Project_RepairsStaleUcpDigests()
    {
        var source = MakeSource("ucp");
        Directory.CreateDirectory(Path.Combine(source, "sce_sys", "trophy2"));
        Directory.CreateDirectory(Path.Combine(source, "sce_sys", "uds"));
        var stale = Ucp(("TROP.SFM", 512), ("TROP000.PNG", 2000));
        stale[40] ^= 0x5A;
        File.WriteAllBytes(Path.Combine(source, "sce_sys", "trophy2", "trophy00.ucp"), stale);
        File.WriteAllBytes(Path.Combine(source, "sce_sys", "uds", "uds00.ucp"), Ucp(("uds.json", 700)));
        File.WriteAllBytes(Path.Combine(source, "sce_sys", "uds", "uds01.ucp"), new byte[128]);
        var before = Snapshot(source);

        var result = SonySdkProject.Create(source, Path.Combine(_root, "ucp-out", "game.gp5"), Passcode, path => path, SonySdkSourcePlan.Pure);
        var gp5 = File.ReadAllText(result.ProjectPath);

        Assert.Equal(["sce_sys/trophy2/trophy00.ucp"], result.RepairedUcps);
        var repaired = SourceOf(gp5, "sce_sys/trophy2/trophy00.ucp");
        Assert.EndsWith(Path.Combine(SonySdkProject.AssetsFolderName, "game", SonySdkProject.RepairedUcpFolderName, "sce_sys", "trophy2", "trophy00.ucp"), repaired);
        Assert.Equal(UcpState.Valid, UcpInspector.Inspect(File.ReadAllBytes(repaired), out _));
        Assert.EndsWith(Path.Combine("ucp", "sce_sys", "uds", "uds00.ucp"), SourceOf(gp5, "sce_sys/uds/uds00.ucp"));
        Assert.Equal("sce_sys/uds/uds01.ucp", Assert.Single(result.InvalidUcps).Relative);
        Assert.Contains("Repairing UCP digest sce_sys/trophy2/trophy00.ucp...", result.Report);
        Assert.Equal(before, Snapshot(source));
    }

    /// <summary>
    /// Gói thật: UCP lệch digest phải được đóng gói ở dạng đã sửa trên cả hai đường — cả mục CNT lẫn bản trong ảnh PFS. Engine 0.6.12 có
    /// hàm sửa nhưng không chạy trong luồng publisher (gói của nó mang nguyên UCP lệch), nên engine tích hợp được sửa qua gương.
    /// </summary>
    [Fact]
    public async Task Builds_PackRepairedUcpsOnBothPaths()
    {
        if (!BuildEngine.KeysAvailable)
        {
            return;
        }

        var source = MakeSource("ucp-build");
        Directory.CreateDirectory(Path.Combine(source, "sce_sys", "trophy2"));
        var stale = Ucp(("TROP.SFM", 512), ("TROP000.PNG", 2000));
        stale[40] ^= 0x5A;
        File.WriteAllBytes(Path.Combine(source, "sce_sys", "trophy2", "trophy00.ucp"), stale);
        var before = Snapshot(source);

        void AssertRepaired(string package)
        {
            var cnt = Path.Combine(_root, "cnt-" + Guid.NewGuid().ToString("N"));
            PackageReader.ExportCntEntries(package, cnt, Passcode, CancellationToken.None);
            Assert.Equal(UcpState.Valid, UcpInspector.Inspect(File.ReadAllBytes(Path.Combine(cnt, "trophy2", "trophy00.ucp")), out _));
            using var reader = PackageReader.Open(package, Passcode, Path.Combine(_root, "read-tmp"), CancellationToken.None);
            if (reader.Entries.FirstOrDefault(entry => entry.Path.Replace('\\', '/').Trim('/') == "sce_sys/trophy2/trophy00.ucp") is { } inImage)
            {
                var image = Path.Combine(_root, "img-" + Guid.NewGuid().ToString("N"));
                reader.Extract([inImage], image, null, CancellationToken.None);
                Assert.Equal(UcpState.Valid, UcpInspector.Inspect(File.ReadAllBytes(Path.Combine(image, "sce_sys", "trophy2", "trophy00.ucp")), out _));
            }
        }

        var log = new List<LogEntry>();
        AssertRepaired((await new BuildEngine().BuildAsync(Request(source, "ucp-engine", useSdk: false), log.Add, null, CancellationToken.None)).OutputPath);
        Assert.Contains(log, entry => entry.Level == LogLevel.Warning && entry.Message.Contains("trophy00.ucp", StringComparison.Ordinal));
        if (SonySdkToolchain.Resolve(out _) != null)
        {
            AssertRepaired((await new BuildEngine().BuildAsync(Request(source, "ucp-sdk", useSdk: true), _ => { }, null, CancellationToken.None)).OutputPath);
        }

        Assert.Equal(before, Snapshot(source));
    }

    /// <summary>
    /// UCP không đọc được (bản niêm phong): Publishing Tools từ chối cả lượt ("Format of the UCP file is not valid"), nên công cụ chuyển sang
    /// engine tích hợp ngay từ đầu (như fpkg-gui đóng nguyên tệp) thay vì để lượt tạo gói dừng.
    /// </summary>
    [Fact]
    public async Task SdkBuild_FallsBackToTheEngineForAnUnreadableUcp()
    {
        if (SonySdkToolchain.Resolve(out _) == null || !BuildEngine.KeysAvailable)
        {
            return;
        }

        var source = MakeSource("ucp-sealed");
        Directory.CreateDirectory(Path.Combine(source, "sce_sys", "uds"));
        File.WriteAllBytes(Path.Combine(source, "sce_sys", "uds", "uds00.ucp"), Enumerable.Range(0, 300).Select(i => (byte)(i * 31 + 7)).ToArray());
        var log = new List<LogEntry>();

        var outcome = await new BuildEngine().BuildAsync(Request(source, "ucp-sealed-out", useSdk: true), log.Add, null, CancellationToken.None);

        Assert.True(File.Exists(outcome.OutputPath));
        Assert.Contains(log, entry => entry.Level == LogLevel.Warning && entry.Message.Contains("uds00.ucp", StringComparison.Ordinal) && entry.Message.Contains("Publishing Tools", StringComparison.Ordinal));
        Assert.DoesNotContain(log, entry => entry.Message.Contains("img_create", StringComparison.Ordinal));
    }

    // ===================== Gói DLC từ dlc_emu.ini =====================

    /// <summary>Trước 2.2.7 gói DLC không đặt chế độ ảnh nên thư viện tạo Native (AES-XTS), khác gói game PLAINTEXT_NOAUTH và fpkg-gui.</summary>
    [Fact]
    public void DlcPackages_UsePlaintextByDefault()
    {
        if (!BuildEngine.KeysAvailable)
        {
            return;
        }

        var entry = new DlcEmuEntry("EP9999-PPSA99994_00-PARITYDLC0000001", "NO_EXTRA_DATA");
        var plain = DlcPackageBuilder.BuildOne(entry, Path.Combine(_root, "dlc-plain"), Path.Combine(_root, "dlc-tmp"), "Parity", null, CancellationToken.None);
        var native = DlcPackageBuilder.BuildOne(entry, Path.Combine(_root, "dlc-native"), Path.Combine(_root, "dlc-tmp2"), "Parity", null, CancellationToken.None, imageMode: OuterImageMode.Native);

        Assert.True(plain.Success, plain.Error);
        Assert.True(native.Success, native.Error);
        using (var stream = File.OpenRead(plain.OutputPath!))
        {
            Assert.Equal(PackageVerifier.PlaintextMarker, PackageVerifier.ReadFihSummary(stream).Marker);
        }

        using (var stream = File.OpenRead(native.OutputPath!))
        {
            Assert.NotEqual(PackageVerifier.PlaintextMarker, PackageVerifier.ReadFihSummary(stream).Marker);
        }

        var info = PackageInspector.Inspect(plain.OutputPath!, Passcode, CancellationToken.None);
        Assert.Equal("01.000.000", info.Params?.ContentVersion ?? "01.000.000");
    }
}
