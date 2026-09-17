using System.Security.Cryptography;
using System.Text.Json;
using LibProsperoPkg.GP5;
using PsViethoa.FpkgBuilder.Core.Models;
using PsViethoa.FpkgBuilder.Core.Services;
using Xunit;

namespace PsViethoa.FpkgBuilder.Tests;

/// <summary>
/// Quy tắc bất di bất dịch: tạo gói KHÔNG được chạm, xoá hay sửa bất cứ thứ gì trong thư mục nguồn, ảnh hay dự án .gp5 của
/// người dùng. Mọi thay đổi (bỏ playgo*, bỏ tàn dư emu, sửa param.json) chỉ được tồn tại trong thư mục tạm.
/// Bộ kiểm thử này chụp lại toàn bộ cây nguồn — đường dẫn, kích thước, băm SHA-256 và dấu thời gian — rồi so lại sau khi build.
/// </summary>
public sealed class SourceUntouchedTests : IDisposable
{
    private const string Passcode = "00000000000000000000000000000000";
    private const string VersionUri = "https://sgst.prod.dl.playstation.net/sgst/prod/PPSA26344/4/f_abc/f/PPSA26344_00.json";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "psviethoa-untouched-" + Guid.NewGuid().ToString("N"));

    public SourceUntouchedTests() => Directory.CreateDirectory(_root);

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

    /// <summary>So hai ảnh chụp và nêu đúng tệp nào khác nhau (Assert.Equal trên từ điển cắt bớt nội dung).</summary>
    private static void AssertUnchanged(SortedDictionary<string, (long Length, string Hash, DateTime Written)> before, string folder)
    {
        var after = Snapshot(folder);
        var added = after.Keys.Except(before.Keys).ToList();
        var removed = before.Keys.Except(after.Keys).ToList();
        var changed = before.Keys.Intersect(after.Keys).Where(k => !before[k].Equals(after[k])).ToList();
        Assert.True(
            added.Count == 0 && removed.Count == 0 && changed.Count == 0,
            $"nguồn bị đổi — thêm: [{string.Join(", ", added)}] · mất: [{string.Join(", ", removed)}] · sửa: [{string.Join(", ", changed)}]");
    }

    /// <summary>Ảnh chụp cây thư mục: đường dẫn tương đối → (kích thước, băm, lần ghi cuối).</summary>
    private static SortedDictionary<string, (long Length, string Hash, DateTime Written)> Snapshot(string folder)
    {
        var map = new SortedDictionary<string, (long, string, DateTime)>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            var info = new FileInfo(file);
            using var stream = File.OpenRead(file);
            map[Path.GetRelativePath(folder, file).Replace('\\', '/')] =
                (info.Length, Convert.ToHexString(SHA256.HashData(stream)), info.LastWriteTimeUtc);
        }

        foreach (var directory in Directory.EnumerateDirectories(folder, "*", SearchOption.AllDirectories))
        {
            map["<dir> " + Path.GetRelativePath(folder, directory).Replace('\\', '/')] = (0, string.Empty, default);
        }

        return map;
    }

    /// <summary>Bản dump đầy đủ kiểu Ghost of Yotei: có playgo*, tàn dư AMPR, dlc_emu và dữ liệu game.</summary>
    private string MakeSource(string name)
    {
        var folder = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.Combine(folder, "sce_sys"));
        Directory.CreateDirectory(Path.Combine(folder, "fakelib"));
        Directory.CreateDirectory(Path.Combine(folder, "sce_module"));
        Directory.CreateDirectory(Path.Combine(folder, "cache_ps5", "bitmaps"));
        File.WriteAllText(Path.Combine(folder, "sce_sys", "param.json"), JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["contentId"] = "UP9000-PPSA26344_00-PSVIETHOAUNTOUC0",
            ["contentVersion"] = "01.000.000",
            ["sdkVersion"] = "0x0450000000000000",
            ["applicationDrmType"] = "free",
            ["versionFileUri"] = VersionUri,
            ["attribute3"] = 4160,
            ["localizedParameters"] = new Dictionary<string, object>
            {
                ["defaultLanguage"] = "en-US",
                ["en-US"] = new Dictionary<string, string> { ["titleName"] = "Untouched" },
            },
        }));
        File.WriteAllBytes(Path.Combine(folder, "eboot.bin"), new byte[64 * 1024]);
        File.WriteAllBytes(Path.Combine(folder, "ampr_emu.index"), new byte[64]);
        File.WriteAllText(Path.Combine(folder, "dlc_emu.ini"), "[EP4064-PPSA26344_00-GHOSTDLC00000001]\nDownloadStatus=NO_EXTRA_DATA\n");
        File.WriteAllBytes(Path.Combine(folder, "sce_sys", "keystone"), new byte[32]);
        File.WriteAllBytes(Path.Combine(folder, "sce_module", "libSceFios2.prx"), new byte[512]);
        File.WriteAllBytes(Path.Combine(folder, "cache_ps5", "bitmaps", "atsu.sps"), new byte[4096]);
        foreach (var module in new[] { "libScePlayGo.sprx", "libSceAmpr.sprx", "libSceAgc.sprx", "libSceAppContent.sprx" })
        {
            File.WriteAllBytes(Path.Combine(folder, "fakelib", module), new byte[2048]);
        }

        var random = new byte[3000];
        Random.Shared.NextBytes(random);
        foreach (var playgo in new[] { "playgo-chunk.dat", "playgo-hash-table.dat", "playgo-ficm.dat", "playgo-scenario.json", "playgo-manifest.xml" })
        {
            File.WriteAllBytes(Path.Combine(folder, "sce_sys", playgo), random);
        }

        return folder;
    }

    private BuildRequest Request(string source, string suffix, string contentId) => new()
    {
        UseSonySdk = false,
        SourcePath = source,
        OutputFolder = Path.Combine(_root, "out-" + suffix),
        TemporaryFolder = Path.Combine(_root, "tmp-" + suffix),
        ContentId = contentId,
        Title = "Untouched",
        KrakenBackend = KrakenBackendKind.BuiltIn,
        KrakenLevel = -4,
        PreventSleep = false,
    };

    private static List<string> Entries(string package, string temporary)
    {
        using var reader = PackageReader.Open(package, Passcode, temporary, CancellationToken.None);
        return reader.Entries.Where(e => !e.IsDirectory).Select(e => e.Path.Replace('\\', '/')).ToList();
    }

    [Fact]
    public async Task AFolderBuildLeavesTheSourceByteForByteIdentical()
    {
        if (!BuildEngine.KeysAvailable)
        {
            return;
        }

        var source = MakeSource("folder");
        var before = Snapshot(source);
        var request = Request(source, "folder", "UP9000-PPSA26344_00-PSVIETHOAUNTOUC0");
        request.KeepDlcEmu = false; // bật cả đường dọn DLC để chạm vào nhiều tệp nhất

        var outcome = await new BuildEngine().BuildAsync(request, _ => { }, null, CancellationToken.None);

        AssertUnchanged(before, source);

        // Gói vẫn đúng: bộ playgo* của bản dump bị bỏ, dlc_emu bị bỏ theo lựa chọn, dữ liệu game còn nguyên.
        var entries = Entries(outcome.OutputPath, request.TemporaryFolder);
        Assert.DoesNotContain(entries, e => e.Contains("playgo", StringComparison.OrdinalIgnoreCase) && !e.EndsWith(".sprx", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(entries, e => e.Equals("ampr_emu.index", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(entries, e => e.Equals("dlc_emu.ini", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(entries, e => e.Equals("eboot.bin", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(entries, e => e.Equals("cache_ps5/bitmaps/atsu.sps", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(entries, e => e.Equals("sce_module/libSceFios2.prx", StringComparison.OrdinalIgnoreCase));

        // param.json trong gói đã sửa đủ ba trường, còn bản trong nguồn thì không.
        var export = Path.Combine(request.TemporaryFolder, "export");
        PackageReader.ExportSceSys(outcome.OutputPath, export, Passcode, CancellationToken.None);
        using (var packaged = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(export, "sce_sys", "param.json"))))
        {
            Assert.Equal("standard", packaged.RootElement.GetProperty("applicationDrmType").GetString());
            Assert.True(string.IsNullOrWhiteSpace(packaged.RootElement.GetProperty("versionFileUri").GetString()));
            Assert.Equal(0, packaged.RootElement.GetProperty("attribute3").GetInt32());
        }

        using var original = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(source, "sce_sys", "param.json")));
        Assert.Equal("free", original.RootElement.GetProperty("applicationDrmType").GetString());
        Assert.Equal(VersionUri, original.RootElement.GetProperty("versionFileUri").GetString());
        Assert.Equal(4160, original.RootElement.GetProperty("attribute3").GetInt32());
    }

    [Fact]
    public async Task ACancelledBuildLeavesTheSourceByteForByteIdentical()
    {
        if (!BuildEngine.KeysAvailable)
        {
            return;
        }

        var source = MakeSource("cancel");
        var before = Snapshot(source);
        var request = Request(source, "cancel", "UP9000-PPSA26344_00-PSVIETHOAUNTOUC1");

        using var cancellation = new CancellationTokenSource();
        var build = new BuildEngine().BuildAsync(request, _ => cancellation.Cancel(), null, cancellation.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => build);

        AssertUnchanged(before, source);
    }

    [Fact]
    public async Task AFailedBuildLeavesTheSourceByteForByteIdentical()
    {
        if (!BuildEngine.KeysAvailable)
        {
            return;
        }

        var source = MakeSource("fail");

        // eboot.bin rỗng làm thư viện từ chối giữa chừng, sau khi thư mục gương đã được dựng.
        File.WriteAllBytes(Path.Combine(source, "eboot.bin"), Array.Empty<byte>());
        var before = Snapshot(source);
        var request = Request(source, "fail", "UP9000-PPSA26344_00-PSVIETHOAUNTOUC2");

        try
        {
            await new BuildEngine().BuildAsync(request, _ => { }, null, CancellationToken.None);
        }
        catch (Exception)
        {
            // Thất bại hay thành công đều được; điều bắt buộc là thư mục nguồn không đổi.
        }

        AssertUnchanged(before, source);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AGp5BuildLeavesBothTheProjectAndTheSourceIdentical(bool flat)
    {
        if (!BuildEngine.KeysAvailable)
        {
            return;
        }

        var suffix = flat ? "gp5-flat" : "gp5-normal";
        var contentId = "UP9000-PPSA26344_00-PSVIETHOAUNTOU" + (flat ? "3F" : "3N");
        var source = MakeSource(suffix);
        var projects = Path.Combine(_root, suffix + "-proj");
        Directory.CreateDirectory(projects);
        var projectPath = Path.Combine(projects, "project.gp5");
        Gp5Project.WriteTo(
            flat
                ? Gp5Creator.FromFolderExplicit(source, Gp5VolumeType.prospero_app, Passcode)
                : Gp5Creator.FromFolder(source, Gp5VolumeType.prospero_app, Passcode, null),
            projectPath);

        var sourceBefore = Snapshot(source);
        var projectBefore = Snapshot(projects);
        var request = Request(projectPath, suffix, contentId);

        try
        {
            await new BuildEngine().BuildAsync(request, _ => { }, null, CancellationToken.None);
        }
        catch (Exception)
        {
            // Bố cục Flat có thể không dựng được gói trong môi trường kiểm thử; quy tắc về thư mục nguồn vẫn phải đúng.
        }

        AssertUnchanged(sourceBefore, source);
        AssertUnchanged(projectBefore, projects);
    }

    [Fact]
    public void TheMirrorSkipsReplacesAndNeverWritesToTheSource()
    {
        var source = MakeSource("mirror");
        var before = Snapshot(source);
        var work = Path.Combine(_root, "mirror-work");
        Directory.CreateDirectory(work);

        var plan = new MirrorPlan(
            new[] { "sce_sys/playgo-chunk.dat", "ampr_emu.index", "fakelib/libSceAppContent.sprx" },
            new Dictionary<string, byte[]> { ["sce_sys/param.json"] = "{\"patched\":true}"u8.ToArray() },
            new Dictionary<string, string>(),
            new[] { "sce_sys" });

        using (var mirror = SourceMirror.Create(source, work, plan, _ => { }))
        {
            Assert.NotNull(mirror);
            var root = mirror!.Path;

            // Bỏ đúng những gì được yêu cầu.
            Assert.False(File.Exists(Path.Combine(root, "sce_sys", "playgo-chunk.dat")));
            Assert.False(File.Exists(Path.Combine(root, "ampr_emu.index")));
            Assert.False(File.Exists(Path.Combine(root, "fakelib", "libSceAppContent.sprx")));

            // Thay đúng param.json, giữ nguyên mọi thứ khác.
            Assert.Equal("{\"patched\":true}", File.ReadAllText(Path.Combine(root, "sce_sys", "param.json")));
            Assert.True(File.Exists(Path.Combine(root, "sce_sys", "keystone")));
            Assert.True(File.Exists(Path.Combine(root, "sce_sys", "playgo-scenario.json")));
            Assert.True(File.Exists(Path.Combine(root, "fakelib", "libScePlayGo.sprx")));
            Assert.True(File.Exists(Path.Combine(root, "eboot.bin")));
            Assert.True(File.Exists(Path.Combine(root, "cache_ps5", "bitmaps", "atsu.sps")));

            // Thư mục không phải sửa gì chỉ là một liên kết, không phải bản sao.
            Assert.NotNull(new DirectoryInfo(Path.Combine(root, "cache_ps5")).LinkTarget);
            Assert.Null(new DirectoryInfo(Path.Combine(root, "sce_sys")).LinkTarget);
        }

        AssertUnchanged(before, source);
        Assert.Empty(Directory.GetDirectories(work));
    }

    [Fact]
    public void DeletingTheMirrorNeverDeletesThroughALink()
    {
        var source = MakeSource("mirror-delete");
        var before = Snapshot(source);
        var work = Path.Combine(_root, "mirror-delete-work");
        Directory.CreateDirectory(work);

        var mirror = SourceMirror.Create(
            source,
            work,
            new MirrorPlan(new[] { "ampr_emu.index" }, new Dictionary<string, byte[]>(), new Dictionary<string, string>(), Array.Empty<string>()),
            _ => { });
        Assert.NotNull(mirror);
        mirror!.Dispose();
        mirror.Dispose(); // gọi hai lần vẫn an toàn

        AssertUnchanged(before, source);
        Assert.Empty(Directory.GetDirectories(work));
    }

    [Fact]
    public void WhenTheTemporaryFolderCannotHoldTheMirrorTheNextFolderIsUsed()
    {
        var source = MakeSource("mirror-fallback");
        var before = Snapshot(source);

        // Chỗ thứ nhất không dùng được (một đường dẫn nằm dưới một TỆP — giống ổ không tạo được liên kết/thư mục).
        var blocker = Path.Combine(_root, "not-a-folder");
        File.WriteAllText(blocker, "x");
        var unusable = Path.Combine(blocker, "tmp");
        var fallback = Path.Combine(_root, "mirror-fallback-work");
        var log = new List<LogEntry>();
        var plan = new MirrorPlan(new[] { "ampr_emu.index" }, new Dictionary<string, byte[]>(), new Dictionary<string, string>(), new[] { "sce_sys" });

        using (var mirror = SourceMirror.CreateInAny(source, new[] { unusable, fallback }, plan, log.Add))
        {
            Assert.NotNull(mirror);
            Assert.StartsWith(fallback, mirror!.Path, StringComparison.Ordinal);
            Assert.False(File.Exists(Path.Combine(mirror.Path, "ampr_emu.index")));
            Assert.True(File.Exists(Path.Combine(mirror.Path, "eboot.bin")));
        }

        Assert.Contains(log, e => e.Level == LogLevel.Warning && e.Message.Contains(unusable, StringComparison.Ordinal));
        Assert.Null(SourceMirror.CreateInAny(source, new[] { unusable }, plan, _ => { }));
        AssertUnchanged(before, source);
    }

    [Fact]
    public void AnExtractedCopyIsPatchedInPlaceWithoutLinks()
    {
        // Bản giải nén thuộc về công cụ: bỏ/sửa thẳng trên đó (không cần liên kết), thư mục chỉ rỗng vì dọn thì bỏ luôn.
        var owned = MakeSource("owned-copy");
        Directory.CreateDirectory(Path.Combine(owned, "onlyemu"));
        File.WriteAllBytes(Path.Combine(owned, "onlyemu", "libSceAmpr.sprx"), new byte[16]);
        var plan = new MirrorPlan(
            new[] { "sce_sys/playgo-chunk.dat", "ampr_emu.index", "onlyemu/libSceAmpr.sprx", "does/not/exist.bin" },
            new Dictionary<string, byte[]> { ["sce_sys/param.json"] = "{\"patched\":true}"u8.ToArray() },
            new Dictionary<string, string>(),
            Array.Empty<string>());

        SourceMirror.ApplyInPlace(owned, plan);

        Assert.False(File.Exists(Path.Combine(owned, "sce_sys", "playgo-chunk.dat")));
        Assert.False(File.Exists(Path.Combine(owned, "ampr_emu.index")));
        Assert.False(Directory.Exists(Path.Combine(owned, "onlyemu")));
        Assert.Equal("{\"patched\":true}", File.ReadAllText(Path.Combine(owned, "sce_sys", "param.json")));
        Assert.True(File.Exists(Path.Combine(owned, "sce_sys", "playgo-scenario.json")));
        Assert.True(File.Exists(Path.Combine(owned, "eboot.bin")));
        Assert.True(Directory.Exists(owned));
    }

    [Fact]
    public void AppleDoubleArtifactsAreRemovedOnlyFromToolFolders()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Equal(0, SourceMirror.RemoveAppleDoubleArtifacts(_root));
            return;
        }

        // Nguồn có sẵn một "._" của người dùng; gương có "._" do hệ điều hành sinh cho tệp đã sao chép và một thư mục liên kết.
        var source = MakeSource("appledouble-src");
        var appleDouble = new byte[4096];
        new byte[] { 0x00, 0x05, 0x16, 0x07 }.CopyTo(appleDouble, 0);
        File.WriteAllBytes(Path.Combine(source, "sce_sys", "._keystone"), appleDouble);
        File.WriteAllBytes(Path.Combine(source, "cache_ps5", "._atsu"), appleDouble);
        var before = Snapshot(source);

        var mirror = Path.Combine(_root, "appledouble-mirror");
        Directory.CreateDirectory(Path.Combine(mirror, "sce_sys"));
        File.WriteAllText(Path.Combine(mirror, "sce_sys", "param.json"), "{}");
        File.WriteAllBytes(Path.Combine(mirror, "sce_sys", "._param.json"), appleDouble);
        File.WriteAllText(Path.Combine(mirror, "sce_sys", "keystone"), "k");
        File.WriteAllBytes(Path.Combine(mirror, "sce_sys", "._keystone"), appleDouble);
        File.WriteAllBytes(Path.Combine(mirror, "sce_sys", "._orphan"), appleDouble);
        File.WriteAllText(Path.Combine(mirror, "sce_sys", "._notappledouble"), "plain");
        File.WriteAllText(Path.Combine(mirror, "sce_sys", "notappledouble"), "plain");
        Directory.CreateSymbolicLink(Path.Combine(mirror, "cache_ps5"), Path.Combine(source, "cache_ps5"));

        Assert.Equal(1, SourceMirror.RemoveAppleDoubleArtifacts(mirror, source));
        Assert.False(File.Exists(Path.Combine(mirror, "sce_sys", "._param.json")));
        Assert.True(File.Exists(Path.Combine(mirror, "sce_sys", "._keystone")));      // có trong nguồn
        Assert.True(File.Exists(Path.Combine(mirror, "sce_sys", "._orphan")));        // không có tệp chính
        Assert.True(File.Exists(Path.Combine(mirror, "sce_sys", "._notappledouble"))); // không phải AppleDouble
        AssertUnchanged(before, source);                                                // không đi xuyên liên kết
    }

    [Fact]
    public void RobustDeleteRemovesTreesWithAccentedNamesButOnlyUnlinksLinks()
    {
        var source = MakeSource("robust-src");
        var before = Snapshot(source);
        var tree = Path.Combine(_root, "robust-tree");
        Directory.CreateDirectory(Path.Combine(tree, "Thư mục có dấu"));
        File.WriteAllText(Path.Combine(tree, "Thư mục có dấu", "Tệp tên rất dài có dấu.txt".Normalize(System.Text.NormalizationForm.FormD)), "x");
        File.WriteAllText(Path.Combine(tree, "Tệp.txt"), "y");
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(tree, "link"), source);
        }
        catch (IOException) when (OperatingSystem.IsWindows())
        {
            // Windows chưa bật Developer Mode / không chạy quyền admin: không tạo được liên kết, vẫn thử phần xoá cây có dấu.
        }

        Assert.True(RobustDelete.File(Path.Combine(tree, "Tệp.txt")));
        RobustDelete.Tree(tree);

        Assert.False(Directory.Exists(tree));
        AssertUnchanged(before, source);
    }

    [Fact]
    public void MirrorWorkFoldersStartWithTheChosenTemporaryFolder()
    {
        var folders = BuildEngine.MirrorWorkFolders(Path.Combine(_root, "tmp-order"));
        Assert.Equal(Path.Combine(_root, "tmp-order"), folders[0]);
        Assert.Equal(folders.Count, folders.Distinct().Count());
    }

    [Fact]
    public void AnEmptyPlanNeedsNoMirror() =>
        Assert.Null(SourceMirror.Create(MakeSource("mirror-empty"), _root, MirrorPlan.Empty, _ => { }));

    [Fact]
    public async Task TheLibraryNeverWritesIntoTheSourceEvenWithNothingToClean()
    {
        if (!BuildEngine.KeysAvailable)
        {
            return;
        }

        // Thư viện tự ghi vào thư mục nó đọc: sinh param.json khi thiếu, và ghi pfs-region-hints.json khi bật phân tích
        // shuffle. Cả hai phải rơi vào thư mục gương, không phải thư mục nguồn.
        var source = MakeSource("library-writes");
        File.Delete(Path.Combine(source, "sce_sys", "param.json"));
        foreach (var playgo in Directory.GetFiles(Path.Combine(source, "sce_sys"), "playgo*"))
        {
            File.Delete(playgo);
        }

        File.Delete(Path.Combine(source, "ampr_emu.index"));
        File.Delete(Path.Combine(source, "dlc_emu.ini"));
        var before = Snapshot(source);

        var request = Request(source, "library-writes", "UP9000-PPSA26344_00-PSVIETHOAUNTOUC4");
        request.PfsFormat = PfsFormat.V3;
        request.ShuffleAnalysis = true;
        request.KrakenLevel = BuildRequest.MaxKrakenLevel;

        try
        {
            await new BuildEngine().BuildAsync(request, _ => { }, null, CancellationToken.None);
        }
        catch (Exception)
        {
            // Thành bại không quan trọng ở đây; điều bắt buộc là thư mục nguồn không có thêm hay bớt tệp nào.
        }

        AssertUnchanged(before, source);
        Assert.False(File.Exists(Path.Combine(source, "sce_sys", "param.json")));
        Assert.False(File.Exists(Path.Combine(source, "sce_sys", "pfs-region-hints.json")));
    }
}
