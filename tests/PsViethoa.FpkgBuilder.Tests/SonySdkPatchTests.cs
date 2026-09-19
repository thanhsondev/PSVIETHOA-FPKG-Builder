using System.Text.Json.Nodes;
using PsViethoa.FpkgBuilder.Core.Models;
using PsViethoa.FpkgBuilder.Core.Services;
using Xunit;

namespace PsViethoa.FpkgBuilder.Tests;

/// <summary>
/// Bản vá của bộ công cụ fix8 (<c>img_create --ref_pkg_path</c>): kiểm tra gói gốc trước khi chạy SDK, tạo bản vá thật qua
/// Publishing Tools (khi có bộ công cụ/Wine) và các lỗi người dùng hay gặp (quên tăng contentVersion, chọn nhầm game).
/// </summary>
public sealed class SonySdkPatchTests : IDisposable
{
    private const string Passcode = "00000000000000000000000000000000";
    private const string ContentId = "EP9999-PPSA99996_00-PSVIETHOAPATCH00";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "psviethoa-patch-" + Guid.NewGuid().ToString("N"));

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

    [Theory]
    [InlineData("01.001.000", "01.000.000", 1)]
    [InlineData("01.000.000", "01.000.000", 0)]
    [InlineData("01.000.000", "01.000.001", -1)]
    [InlineData("01.10.000", "01.9.000", 1)]
    [InlineData("02", "01.999.999", 1)]
    [InlineData("01.000", "01.000.000", 0)]
    [InlineData(" 01.002.000 ", "01.001.000", 1)]
    public void CompareVersions_IsNumericPerGroup(string left, string right, int expected) =>
        Assert.Equal(expected, Math.Sign(SonySdkPatchReference.CompareVersions(left, right)));

    [Fact]
    public void Mismatch_ExplainsWrongGameAndVersionNotRaised()
    {
        var reference = new SonySdkReferenceInfo("/x/base.pkg", ContentId, "01.000.000", 10);
        Assert.Null(SonySdkPatchReference.Mismatch(reference, ContentId, "01.001.000"));
        Assert.Null(SonySdkPatchReference.Mismatch(reference, ContentId.ToLowerInvariant(), "01.001.000"));
        Assert.Contains("01.000.000", SonySdkPatchReference.Mismatch(reference, ContentId, "01.000.000"));
        Assert.NotNull(SonySdkPatchReference.Mismatch(reference, ContentId, "00.999.000"));
        Assert.Contains("UP0000", SonySdkPatchReference.Mismatch(reference, "UP0000-PPSA00000_00-0000000000000000", "02.000.000"));
        // Không đọc được phiên bản ở một phía: để Publishing Tools quyết định, không chặn.
        Assert.Null(SonySdkPatchReference.Mismatch(reference, ContentId, null));
        Assert.Null(SonySdkPatchReference.Mismatch(reference with { ContentVersion = null }, ContentId, "01.000.000"));
    }

    [Fact]
    public void Inspect_RejectsMissingWrongAndUnreadableFiles()
    {
        Directory.CreateDirectory(_root);
        Assert.Throws<InvalidDataException>(() => SonySdkPatchReference.Inspect(Path.Combine(_root, "none.pkg"), Passcode, CancellationToken.None));

        var notPackage = Path.Combine(_root, "game.zip");
        File.WriteAllBytes(notPackage, new byte[64]);
        Assert.Throws<InvalidDataException>(() => SonySdkPatchReference.Inspect(notPackage, Passcode, CancellationToken.None));

        var companion = Path.Combine(_root, "game.pkg" + SonySdkPatchReference.RemasteredSuffix);
        File.WriteAllBytes(companion, new byte[64]);
        Assert.Throws<InvalidDataException>(() => SonySdkPatchReference.Inspect(companion, Passcode, CancellationToken.None));

        var garbage = Path.Combine(_root, "garbage.pkg");
        File.WriteAllBytes(garbage, Enumerable.Range(0, 4096).Select(i => (byte)i).ToArray());
        var error = Assert.Throws<InvalidDataException>(() => SonySdkPatchReference.Inspect(garbage, Passcode, CancellationToken.None));
        Assert.Contains("garbage.pkg", error.Message);
    }

    [Fact]
    public void SourceContentVersion_ReadsParamJsonOrReturnsNull()
    {
        var source = MakeSource("version", "03.004.005");
        Assert.Equal("03.004.005", SonySdkPatchReference.SourceContentVersion(source));
        Assert.Null(SonySdkPatchReference.SourceContentVersion(Path.Combine(_root, "missing")));
        File.WriteAllText(Path.Combine(source, "sce_sys", "param.json"), "{not json");
        Assert.Null(SonySdkPatchReference.SourceContentVersion(source));
    }

    /// <summary>Engine tích hợp không tạo được bản vá: phải dừng với lý do rõ, không âm thầm cho ra gói đầy đủ.</summary>
    [Fact]
    public async Task Patch_WithoutTheSonySdkFailsInsteadOfBuildingAFullPackage()
    {
        var source = MakeSource("no-sdk", "01.001.000");
        var reference = Path.Combine(_root, "base.pkg");
        File.WriteAllBytes(reference, new byte[64]);
        var request = Request(source, "no-sdk-out", "01.001.000");
        request.UseSonySdk = false;
        request.SdkReferencePackage = reference;

        await Assert.ThrowsAsync<InvalidOperationException>(() => new BuildEngine().BuildAsync(request, _ => { }, null, CancellationToken.None));
        Assert.Empty(Directory.Exists(request.OutputFolder) ? Directory.EnumerateFiles(request.OutputFolder, "*.pkg") : []);
    }

    /// <summary>Luồng thật: gói gốc 01.000.000 → sửa một tệp + tăng contentVersion → bản vá nhỏ + gói .remastered.pkg đi kèm.</summary>
    [Fact]
    public async Task Patch_BuildsADeltaAndItsRemasteredCompanion()
    {
        if (SonySdkToolchain.Resolve(out _) == null)
        {
            return;
        }

        var source = MakeSource("game", "01.000.000");
        var baseOutcome = await new BuildEngine().BuildAsync(Request(source, "base-out", "01.000.000"), _ => { }, null, CancellationToken.None);
        Assert.True(baseOutcome.Verification.Contents!.IsValid);

        // Bản mới: đổi 1 KB giữa tệp dữ liệu, tăng contentVersion.
        var data = Path.Combine(source, "data", "Big.bin");
        var bytes = File.ReadAllBytes(data);
        Array.Fill(bytes, (byte)0xAB, 1000, 1000);
        File.WriteAllBytes(data, bytes);
        SetContentVersion(source, "01.001.000");

        var log = new List<LogEntry>();
        var request = Request(source, "patch-out", "01.001.000");
        request.SdkReferencePackage = baseOutcome.OutputPath;
        var outcome = await new BuildEngine().BuildAsync(request, log.Add, null, CancellationToken.None);

        var companion = SonySdkPatchReference.CompanionPathOf(outcome.OutputPath);
        Assert.True(File.Exists(outcome.OutputPath));
        Assert.True(File.Exists(companion));
        Assert.Equal("UPDATE_" + ContentId + "-A0101-V0101.pkg", Path.GetFileName(outcome.OutputPath));
        Assert.True(new FileInfo(outcome.OutputPath).Length < new FileInfo(companion).Length, "the delta must be smaller than the full companion");
        Assert.Equal(new FileInfo(outcome.OutputPath).Length, outcome.Verification.Length);
        Assert.Empty(Directory.EnumerateFiles(request.OutputFolder, "*.partial.pkg*"));
        Assert.Contains(log, e => e.Message.Contains("--ref_pkg_path", StringComparison.Ordinal));
        Assert.Equal("01.001.000", PackageInspector.Inspect(companion, Passcode, CancellationToken.None).Params!.ContentVersion);
        var imageLog = File.ReadAllText(Directory.EnumerateFiles(Directory.EnumerateDirectories(request.OutputFolder, "*-build-logs").Single(), "02-img-create.log").Single());
        Assert.Contains("--ref_pkg_path", imageLog);
        // Nguồn đầy đủ (không thiếu tệp nào của gói gốc): không ghép, không giải nén gì — đúng như bộ công cụ gốc.
        var projectLog = File.ReadAllText(Directory.EnumerateFiles(Directory.EnumerateDirectories(request.OutputFolder, "*-build-logs").Single(), "01-create-gp5.log").Single());
        Assert.DoesNotContain("Update folder", projectLog);
        Assert.Empty(Directory.Exists(request.TemporaryFolder) ? Directory.EnumerateDirectories(request.TemporaryFolder, "patch-base-*") : []);

        // Gói gốc không bị đụng tới.
        Assert.True(PackageVerifier.VerifyContents(baseOutcome.OutputPath, Passcode, false, CancellationToken.None, null).IsValid);
    }

    [Fact]
    public async Task Patch_StopsBeforePublishingToolsWhenTheVersionWasNotRaised()
    {
        if (SonySdkToolchain.Resolve(out _) == null)
        {
            return;
        }

        var source = MakeSource("same-version", "01.000.000");
        var baseOutcome = await new BuildEngine().BuildAsync(Request(source, "same-base", "01.000.000"), _ => { }, null, CancellationToken.None);

        var log = new List<LogEntry>();
        var request = Request(source, "same-patch", "01.000.000");
        request.SdkReferencePackage = baseOutcome.OutputPath;
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => new BuildEngine().BuildAsync(request, log.Add, null, CancellationToken.None));
        Assert.Contains("contentVersion", error.Message);
        Assert.DoesNotContain(log, e => e.Message.StartsWith("SDK: ", StringComparison.Ordinal));
        Assert.Empty(Directory.EnumerateFiles(request.OutputFolder, "*.pkg"));
    }

    [Fact]
    public async Task Patch_RejectsABasePackageOfAnotherGame()
    {
        if (SonySdkToolchain.Resolve(out _) == null)
        {
            return;
        }

        var other = MakeSource("other", "01.000.000", "EP9999-PPSA99995_00-PSVIETHOAOTHER00");
        var otherRequest = Request(other, "other-out", "01.000.000");
        otherRequest.ContentId = "EP9999-PPSA99995_00-PSVIETHOAOTHER00";
        var baseOutcome = await new BuildEngine().BuildAsync(otherRequest, _ => { }, null, CancellationToken.None);

        var source = MakeSource("mine", "01.001.000");
        var request = Request(source, "mine-out", "01.001.000");
        request.SdkReferencePackage = baseOutcome.OutputPath;
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => new BuildEngine().BuildAsync(request, _ => { }, null, CancellationToken.None));
        Assert.Contains("PPSA99995", error.Message);
    }

    /// <summary>Ô "Phiên bản" / "Tên ứng dụng": chỉ ghi vào param.json chuẩn hoá khi khác nguồn; không đổi thì không có thay đổi nào.</summary>
    [Fact]
    public void PackageDetails_AreWrittenOnlyWhenTheyDiffer()
    {
        var source = MakeSource("details", "01.000.000");
        var paramPath = Path.Combine(source, "sce_sys", "param.json");

        var same = JsonNode.Parse(File.ReadAllText(paramPath))!.AsObject();
        Assert.Empty(ParamJsonPatch.ApplyTo(same, new ParamJsonPatchOptions(false, false, false, false, "01.000.000", "Patch test")));
        Assert.Empty(ParamJsonPatch.ApplyTo(same, new ParamJsonPatchOptions(false, false, false, false, "1.0", "Patch test")));
        Assert.Equal("01.000.000", (string?)same["contentVersion"]);

        var changed = JsonNode.Parse(File.ReadAllText(paramPath))!.AsObject();
        var applied = ParamJsonPatch.ApplyTo(changed, new ParamJsonPatchOptions(false, false, false, false, "01.002.000", "Patch test — Việt hoá"));
        Assert.Equal(2, applied.Count);
        Assert.Equal("01.002.000", (string?)changed["contentVersion"]);
        Assert.Equal("Patch test — Việt hoá", (string?)changed["localizedParameters"]!["en-US"]!["titleName"]);

        // BuildRequest: engine tích hợp (SDK không chạy) không bao giờ áp hai ô này qua ParamPatch.
        var request = Request(source, "details-out", "01.002.000");
        request.SdkApplyPackageDetails = true;
        Assert.Null(request.ParamPatch.ContentVersion);
        Assert.Null(request.ParamPatch.TitleName);
    }

    /// <summary>Thư mục update ghép lên thư mục gốc khi liệt kê GP5: tệp đè giữ tên của gốc, tệp mới được thêm, thứ tự vẫn là casefold.</summary>
    [Fact]
    public void UpdateFolder_IsLayeredOverTheBaseInTheProject()
    {
        var game = MakeSource("layer-base", "01.000.000");
        var update = Path.Combine(_root, "layer-update");
        Directory.CreateDirectory(Path.Combine(update, "DATA", "vi"));
        Directory.CreateDirectory(Path.Combine(update, "sce_sys"));
        File.WriteAllBytes(Path.Combine(update, "DATA", "big.BIN"), new byte[10]);
        File.WriteAllBytes(Path.Combine(update, "DATA", "vi", "text.bin"), new byte[20]);
        File.WriteAllBytes(Path.Combine(update, "eboot.bin"), new byte[30]);
        File.WriteAllBytes(Path.Combine(update, ".DS_Store"), new byte[5]);
        File.Copy(Path.Combine(game, "sce_sys", "param.json"), Path.Combine(update, "sce_sys", "param.json"));
        SetContentVersion(update, "01.001.000");

        var output = Path.Combine(_root, "layer-out");
        Directory.CreateDirectory(output);
        var plan = new SonySdkSourcePlan(new HashSet<string>(StringComparer.OrdinalIgnoreCase), new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), SkipJunk: true) { OverlayRoot = update };
        var result = SonySdkProject.Create(game, Path.Combine(output, "layer.gp5"), Passcode, path => path, plan);

        Assert.Equal(new[] { "data/Big.bin", "eboot.bin", "sce_sys/param.json" }, result.OverlayReplaced.OrderBy(p => p, StringComparer.Ordinal));
        Assert.Equal(new[] { "data/vi/text.bin" }, result.OverlayAdded);
        var gp5 = File.ReadAllText(result.ProjectPath);
        var realUpdate = SonySdkProject.RealPath(update);
        Assert.Contains("dst_path=\"data/Big.bin\" src_path=\"" + Path.Combine(realUpdate, "DATA", "big.BIN") + "\"", gp5);
        Assert.Contains("dst_path=\"data/vi/text.bin\" src_path=\"" + Path.Combine(realUpdate, "DATA", "vi", "text.bin") + "\"", gp5);
        Assert.Contains("dst_path=\"sce_sys/keystone\" src_path=\"" + Path.Combine(SonySdkProject.RealPath(game), "sce_sys", "keystone") + "\"", gp5);
        Assert.DoesNotContain(".DS_Store", gp5);
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(gp5, "dst_path=\"data/Big.bin\"").Count);
        Assert.Equal("01.001.000", (string?)JsonNode.Parse(File.ReadAllText(result.ParamJsonPath))!["contentVersion"]);
        Assert.Contains("Update folder: 3 file(s) replaced, 1 file(s) added", result.Report);

        // Không có thư mục update: GP5 y như trước (không dòng "Update folder").
        var plain = SonySdkProject.Create(game, Path.Combine(output, "plain.gp5"), Passcode, path => path, plan with { OverlayRoot = null });
        Assert.DoesNotContain("Update folder", plain.Report);
        Assert.Empty(plain.OverlayAdded);
    }

    /// <summary>
    /// Giải nén từng phần (thư mục gốc chỉ có tệp nguồn thiếu): danh sách đường dẫn của gói gốc cho biết tệp nào là "thay", và tệp/thư mục
    /// của thư mục update nhận đúng tên (hoa-thường) của gói gốc.
    /// </summary>
    [Fact]
    public void UpdateFolder_UsesTheBasePackageListingForNamesAndReplacements()
    {
        var game = MakeSource("listing-base", "01.000.000");
        File.Delete(Path.Combine(game, "eboot.bin"));
        var update = Path.Combine(_root, "listing-update");
        Directory.CreateDirectory(Path.Combine(update, "DATA", "VI"));
        File.WriteAllBytes(Path.Combine(update, "EBOOT.BIN"), new byte[30]);
        File.WriteAllBytes(Path.Combine(update, "DATA", "VI", "text.bin"), new byte[20]);

        var output = Path.Combine(_root, "listing-out");
        Directory.CreateDirectory(output);
        var plan = new SonySdkSourcePlan(new HashSet<string>(StringComparer.OrdinalIgnoreCase), new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), SkipJunk: true)
        {
            OverlayRoot = update,
            OverlayBasePaths = ["eboot.bin", "data/Big.bin", "data/vi/old.bin", "sce_sys/keystone"],
        };
        var result = SonySdkProject.Create(game, Path.Combine(output, "listing.gp5"), Passcode, path => path, plan);

        Assert.Equal(new[] { "eboot.bin" }, result.OverlayReplaced);
        Assert.Equal(new[] { "data/vi/text.bin" }, result.OverlayAdded);
        var gp5 = File.ReadAllText(result.ProjectPath);
        Assert.Contains("dst_path=\"eboot.bin\"", gp5);
        Assert.Contains("dst_path=\"data/vi/text.bin\"", gp5);
        Assert.DoesNotContain("EBOOT.BIN\" src_path", gp5);
    }

    /// <summary>
    /// Luồng thật kiểu Patch Builder: thư mục update chỉ có eboot.bin mới + tệp Việt hoá (không sce_sys). Công cụ tự giải nén gói gốc,
    /// ghép, tăng phiên bản từ ô "Phiên bản" và tạo bản vá; thư mục update và gói gốc không bị đụng tới, bản giải nén tạm bị xoá.
    /// </summary>
    [Fact]
    public async Task Patch_FromAnUpdateFolderExtractsTheBaseAndBuildsADelta()
    {
        if (SonySdkToolchain.Resolve(out _) == null)
        {
            return;
        }

        var game = MakeSource("upd-game", "01.000.000");
        var baseOutcome = await new BuildEngine().BuildAsync(Request(game, "upd-base-out", "01.000.000"), _ => { }, null, CancellationToken.None);

        var update = Path.Combine(_root, "upd-folder");
        Directory.CreateDirectory(Path.Combine(update, "data", "vi"));
        var eboot = new byte[4096];
        Array.Fill(eboot, (byte)0x5A);
        File.WriteAllBytes(Path.Combine(update, "eboot.bin"), eboot);
        File.WriteAllBytes(Path.Combine(update, "data", "vi", "lang.bin"), new byte[50_000]);
        var before = Snapshot(update);

        var log = new List<LogEntry>();
        var request = Request(update, "upd-patch-out", "01.001.000");
        request.Title = "Patch test VH";
        request.SdkApplyPackageDetails = true;
        request.SdkReferencePackage = baseOutcome.OutputPath;
        var outcome = await new BuildEngine().BuildAsync(request, log.Add, null, CancellationToken.None);

        var companion = SonySdkPatchReference.CompanionPathOf(outcome.OutputPath);
        Assert.True(File.Exists(outcome.OutputPath));
        Assert.True(File.Exists(companion));
        Assert.True(new FileInfo(outcome.OutputPath).Length < new FileInfo(baseOutcome.OutputPath).Length, "the update must be smaller than the base package");
        var info = PackageInspector.Inspect(companion, Passcode, CancellationToken.None);
        Assert.Equal("01.001.000", info.Params!.ContentVersion);
        Assert.Equal(ContentId, info.ContentId);

        var projectLog = File.ReadAllText(Directory.EnumerateFiles(Directory.EnumerateDirectories(request.OutputFolder, "*-build-logs").Single(), "01-create-gp5.log").Single());
        Assert.Contains("Update folder: 1 file(s) replaced, 1 file(s) added", projectLog);
        Assert.Contains("* eboot.bin", projectLog);
        Assert.Contains("+ data/vi/lang.bin", projectLog);

        Assert.Equal(before, Snapshot(update));
        Assert.Empty(Directory.Exists(request.TemporaryFolder) ? Directory.EnumerateDirectories(request.TemporaryFolder, "patch-base-*") : []);
        Assert.True(PackageVerifier.VerifyContents(baseOutcome.OutputPath, Passcode, false, CancellationToken.None, null).IsValid);
    }

    /// <summary>Tệp xuất ra theo lựa chọn: chỉ file update (xoá gói đầy đủ sau khi kiểm tra) hoặc chỉ gói game đầy đủ đã kèm update (xoá file update).</summary>
    [Fact]
    public async Task Patch_OutputSelectionKeepsOnlyTheChosenFile()
    {
        if (SonySdkToolchain.Resolve(out _) == null)
        {
            return;
        }

        var game = MakeSource("sel-game", "01.000.000");
        var baseOutcome = await new BuildEngine().BuildAsync(Request(game, "sel-base-out", "01.000.000"), _ => { }, null, CancellationToken.None);
        File.WriteAllBytes(Path.Combine(game, "data", "vi.pak"), new byte[40_000]);
        SetContentVersion(game, "01.001.000");

        var updateOnly = Request(game, "sel-update-out", "01.001.000");
        updateOnly.SdkReferencePackage = baseOutcome.OutputPath;
        updateOnly.SdkPatchOutput = SdkPatchOutput.UpdateOnly;
        var first = await new BuildEngine().BuildAsync(updateOnly, _ => { }, null, CancellationToken.None);
        Assert.StartsWith("UPDATE_", Path.GetFileName(first.OutputPath));
        Assert.True(File.Exists(first.OutputPath));
        Assert.False(File.Exists(SonySdkPatchReference.CompanionPathOf(first.OutputPath)));
        Assert.Equal(new FileInfo(first.OutputPath).Length, first.Verification.Length);

        var fullOnly = Request(game, "sel-full-out", "01.001.000");
        fullOnly.SdkReferencePackage = baseOutcome.OutputPath;
        fullOnly.SdkPatchOutput = SdkPatchOutput.FullOnly;
        var second = await new BuildEngine().BuildAsync(fullOnly, _ => { }, null, CancellationToken.None);
        Assert.Equal(ContentId + "-A0101-V0101.remastered.pkg", Path.GetFileName(second.OutputPath));
        Assert.True(File.Exists(second.OutputPath));
        Assert.Empty(Directory.EnumerateFiles(fullOnly.OutputFolder, "UPDATE_*.pkg"));
        Assert.Equal("01.001.000", PackageInspector.Inspect(second.OutputPath, Passcode, CancellationToken.None).Params!.ContentVersion);
        Assert.True(new FileInfo(second.OutputPath).Length > new FileInfo(first.OutputPath).Length);
    }

    /// <summary>--sdk-patch-exact: nguồn được coi là bản đầy đủ đúng như nó có — tệp thiếu = đã xoá (gói đi kèm nhỏ hẳn so với gói gốc).</summary>
    [Fact]
    public async Task Patch_ExactSourceTreatsMissingFilesAsDeleted()
    {
        if (SonySdkToolchain.Resolve(out _) == null)
        {
            return;
        }

        var game = MakeSource("exact-game", "01.000.000");
        var baseOutcome = await new BuildEngine().BuildAsync(Request(game, "exact-base-out", "01.000.000"), _ => { }, null, CancellationToken.None);

        File.Delete(Path.Combine(game, "data", "Big.bin"));
        SetContentVersion(game, "01.001.000");
        var request = Request(game, "exact-patch-out", "01.001.000");
        request.SdkReferencePackage = baseOutcome.OutputPath;
        request.SdkPatchExactSource = true;
        var outcome = await new BuildEngine().BuildAsync(request, _ => { }, null, CancellationToken.None);

        var companion = new FileInfo(SonySdkPatchReference.CompanionPathOf(outcome.OutputPath));
        Assert.True(companion.Length < new FileInfo(baseOutcome.OutputPath).Length - 2_000_000, "Big.bin must be gone from the new version");

        // Mặc định (không ép): tệp thiếu được lấy lại từ gói gốc → gói đi kèm vẫn mang Big.bin.
        var merged = Request(game, "exact-merged-out", "01.001.000");
        merged.SdkReferencePackage = baseOutcome.OutputPath;
        var mergedOutcome = await new BuildEngine().BuildAsync(merged, _ => { }, null, CancellationToken.None);
        Assert.True(new FileInfo(SonySdkPatchReference.CompanionPathOf(mergedOutcome.OutputPath)).Length > 3_000_000);
    }

    /// <summary>Có thư mục game gốc thì không giải nén gói gốc; thư mục gốc chỉ được đọc.</summary>
    [Fact]
    public async Task Patch_FromAnUpdateFolderCanUseTheBaseGameFolder()
    {
        if (SonySdkToolchain.Resolve(out _) == null)
        {
            return;
        }

        var game = MakeSource("upd2-game", "01.000.000");
        var baseOutcome = await new BuildEngine().BuildAsync(Request(game, "upd2-base-out", "01.000.000"), _ => { }, null, CancellationToken.None);
        var gameBefore = Snapshot(game);

        var update = Path.Combine(_root, "upd2-folder");
        Directory.CreateDirectory(Path.Combine(update, "sce_sys"));
        File.Copy(Path.Combine(game, "sce_sys", "param.json"), Path.Combine(update, "sce_sys", "param.json"));
        SetContentVersion(update, "01.005.000");
        File.WriteAllBytes(Path.Combine(update, "vi.pak"), new byte[9000]);

        var log = new List<LogEntry>();
        var request = Request(update, "upd2-patch-out", "01.005.000");
        request.SdkReferencePackage = baseOutcome.OutputPath;
        request.SdkPatchBaseFolder = game;
        var outcome = await new BuildEngine().BuildAsync(request, log.Add, null, CancellationToken.None);

        Assert.True(File.Exists(SonySdkPatchReference.CompanionPathOf(outcome.OutputPath)));
        Assert.Equal("01.005.000", PackageInspector.Inspect(SonySdkPatchReference.CompanionPathOf(outcome.OutputPath), Passcode, CancellationToken.None).Params!.ContentVersion);
        Assert.Equal(gameBefore, Snapshot(game));

        // Thư mục game gốc trùng / chứa thư mục update: dừng sớm.
        request.SdkPatchBaseFolder = update;
        request.OutputFolder = Path.Combine(_root, "upd2-bad-out");
        await Assert.ThrowsAsync<InvalidDataException>(() => new BuildEngine().BuildAsync(request, _ => { }, null, CancellationToken.None));
    }

    /// <summary>
    /// Bản vá ghi ra UPDATE_&lt;contentId&gt;-….pkg: gói gốc nằm ngay trong thư mục xuất không bị hỏi ghi đè (ghi đè = xoá mất gói gốc);
    /// chỉ bản vá cũ cùng tên mới là trùng.
    /// </summary>
    [Fact]
    public void Patch_OutputNameNeverCollidesWithTheBasePackage()
    {
        var output = Path.Combine(_root, "conflict-out");
        Directory.CreateDirectory(output);
        var basePackage = Path.Combine(output, ContentId + "-A0100-V0100.pkg");
        var oldPatch = Path.Combine(output, "UPDATE_" + ContentId + "-A0101-V0101.pkg");
        File.WriteAllBytes(basePackage, new byte[8]);

        Assert.Equal("UPDATE_" + ContentId + "-A0102-V0102.pkg", SonySdkBuilder.PackageFileName(ContentId, "01.002.000", patch: true));
        Assert.Equal(new[] { basePackage }, OutputConflict.Find(output, ContentId));
        Assert.Empty(OutputConflict.Find(output, ContentId, SonySdkBuilder.PatchPrefix, basePackage));

        File.WriteAllBytes(oldPatch, new byte[8]);
        File.WriteAllBytes(SonySdkPatchReference.CompanionPathOf(oldPatch), new byte[8]);
        var found = OutputConflict.Find(output, ContentId, SonySdkBuilder.PatchPrefix, basePackage);
        Assert.Equal(2, found.Count);
        Assert.DoesNotContain(basePackage, found);
        Assert.Equal(ContentId + "-A0101-V0101.remastered.pkg", Path.GetFileName(SonySdkPatchReference.CompanionPathOf(oldPatch)));
        // Gói đầy đủ thường không coi bản vá hay gói đi kèm của nó là trùng.
        Assert.Equal(new[] { basePackage }, OutputConflict.Find(output, ContentId));
    }

    private static List<string> Snapshot(string folder) =>
        Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.Ordinal)
            .Select(p => Path.GetRelativePath(folder, p) + ":" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(p))))
            .ToList();

    private BuildRequest Request(string source, string output, string version) => new()
    {
        SourcePath = source,
        OutputFolder = Path.Combine(_root, output),
        TemporaryFolder = Path.Combine(_root, output + "-tmp"),
        ContentId = ContentId,
        Version = version,
        PreventSleep = false,
    };

    private string MakeSource(string name, string contentVersion, string contentId = ContentId)
    {
        var folder = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.Combine(folder, "sce_sys"));
        Directory.CreateDirectory(Path.Combine(folder, "data"));
        File.WriteAllText(Path.Combine(folder, "sce_sys", "param.json"), new JsonObject
        {
            ["contentId"] = contentId,
            ["contentVersion"] = contentVersion,
            ["masterVersion"] = "01.00",
            ["titleId"] = contentId.Substring(7, 9),
            ["sdkVersion"] = "0x1100000000000000",
            ["requiredSystemSoftwareVersion"] = "0x0900000000000000",
            ["applicationCategoryType"] = 0,
            ["applicationDrmType"] = "standard",
            ["attribute"] = 0,
            ["attribute2"] = 0,
            ["attribute3"] = 0,
            ["localizedParameters"] = new JsonObject { ["defaultLanguage"] = "en-US", ["en-US"] = new JsonObject { ["titleName"] = "Patch test" } },
        }.ToJsonString());
        File.WriteAllBytes(Path.Combine(folder, "sce_sys", "keystone"), new byte[96]);
        File.WriteAllBytes(Path.Combine(folder, "eboot.bin"), new byte[4096]);
        var random = new Random(7);
        var big = new byte[3_000_000];
        random.NextBytes(big);
        File.WriteAllBytes(Path.Combine(folder, "data", "Big.bin"), big);
        return folder;
    }

    private static void SetContentVersion(string source, string version)
    {
        var path = Path.Combine(source, "sce_sys", "param.json");
        var node = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        node["contentVersion"] = version;
        File.WriteAllText(path, node.ToJsonString());
    }
}
