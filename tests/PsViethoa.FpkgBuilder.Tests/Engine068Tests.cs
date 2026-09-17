using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using PsViethoa.FpkgBuilder.Core.Models;
using PsViethoa.FpkgBuilder.Core.Services;
using Xunit;

namespace PsViethoa.FpkgBuilder.Tests;

/// <summary>
/// Hành vi theo engine fpkg-gui 0.6.8: DRM "standard" do engine ép trong bộ nhớ, requiredSystemSoftwareVersion hạ về SDK của game,
/// PlayGo chỉ lấy số khối/kịch bản từ nguồn (tệp hỏng bị bỏ thay vì làm dừng lượt tạo gói) và gói được engine kiểm tra nội dung.
/// </summary>
public sealed class Engine068Tests : IDisposable
{
    private const string Passcode = "00000000000000000000000000000000";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "psviethoa-engine068-" + Guid.NewGuid().ToString("N"));

    public Engine068Tests() => Directory.CreateDirectory(_root);

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

    private string MakeSource(string name, string contentId, string sdk = "0x0450000000000000", string required = "0x1040000000000000", string drm = "free")
    {
        var folder = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.Combine(folder, "sce_sys"));
        File.WriteAllText(Path.Combine(folder, "sce_sys", "param.json"), JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["contentId"] = contentId,
            ["contentVersion"] = "01.000.000",
            ["sdkVersion"] = sdk,
            ["requiredSystemSoftwareVersion"] = required,
            ["applicationDrmType"] = drm,
            ["localizedParameters"] = new Dictionary<string, object>
            {
                ["defaultLanguage"] = "en-US",
                ["en-US"] = new Dictionary<string, string> { ["titleName"] = "Engine 0.6.8 test" },
            },
        }));
        File.WriteAllBytes(Path.Combine(folder, "eboot.bin"), new byte[64 * 1024]);
        for (var i = 0; i < 6; i++)
        {
            File.WriteAllBytes(Path.Combine(folder, $"data{i}.bin"), Enumerable.Range(0, 200_000).Select(x => (byte)(x * (i + 1))).ToArray());
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
        Title = "Engine 0.6.8 test",
        KrakenBackend = KrakenBackendKind.BuiltIn,
        KrakenLevel = -4,
        PreventSleep = false,
    };

    /// <summary>Header playgo-chunk.dat tối thiểu mà engine chấp nhận.</summary>
    private static byte[] ChunkDat(int chunks, int scenarios, int length = 512)
    {
        var data = new byte[length];
        Encoding.ASCII.GetBytes("plgx").CopyTo(data, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(4), 0x1000);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(10), (ushort)chunks);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(14), (ushort)scenarios);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(16), (uint)length);
        return data;
    }

    private JsonElement ExportParam(string package, string suffix)
    {
        var export = Path.Combine(_root, "export-" + suffix);
        PackageReader.ExportSceSys(package, export, Passcode, CancellationToken.None);
        using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(export, "sce_sys", "param.json")));
        return document.RootElement.Clone();
    }

    // ===================== requiredSystemSoftwareVersion =====================

    [Theory]
    [InlineData("0x0450000000000000", "0x1040000000000000", "0x0450000000000000")]
    [InlineData("0x0450123400000000", "0x0460000000000000", "0x0450000000000000")]
    [InlineData("0x0100000000000000", "0x0100000000000000", null)]
    [InlineData("0x0600000000000000", "0x0450000000000000", null)]
    public void LowerRequiredSystemVersion_OnlyEverLowersToTheSdk(string sdk, string required, string? expected)
    {
        var param = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { sdkVersion = sdk, requiredSystemSoftwareVersion = required, title = "x" }));
        var options = new ParamJsonPatchOptions(false, false, false, LowerRequiredSystemVersion: true);
        var rewritten = ParamJsonPatch.Rewrite(param, options, out var changes);
        if (expected == null)
        {
            Assert.Null(rewritten);
            Assert.Empty(changes);
            return;
        }

        Assert.NotNull(rewritten);
        using var document = JsonDocument.Parse(rewritten!);
        Assert.Equal(expected, document.RootElement.GetProperty("requiredSystemSoftwareVersion").GetString());
        Assert.Equal(sdk, document.RootElement.GetProperty("sdkVersion").GetString());
        Assert.Equal("x", document.RootElement.GetProperty("title").GetString());
        Assert.Single(changes);
    }

    [Fact]
    public void LowerRequiredSystemVersion_IgnoresMissingOrMalformedFields()
    {
        var options = new ParamJsonPatchOptions(false, false, false, LowerRequiredSystemVersion: true);
        Assert.Null(ParamJsonPatch.Rewrite("{\"requiredSystemSoftwareVersion\":\"0x1040000000000000\"}"u8.ToArray(), options, out _));
        Assert.Null(ParamJsonPatch.Rewrite("{\"sdkVersion\":\"0x0450000000000000\"}"u8.ToArray(), options, out _));
        Assert.Null(ParamJsonPatch.Rewrite("{\"sdkVersion\":\"banana\",\"requiredSystemSoftwareVersion\":\"0x1040000000000000\"}"u8.ToArray(), options, out _));
        Assert.Null(ParamJsonPatch.Rewrite("{\"sdkVersion\":\"0x0000000000000000\",\"requiredSystemSoftwareVersion\":\"0x1040000000000000\"}"u8.ToArray(), options, out _));
        Assert.Equal("10.40", ParamJsonPatch.FormatFirmware(0x1040000000000000));
        Assert.Equal("4.50", ParamJsonPatch.FormatFirmware(0x0450000000000000));
        Assert.Equal(0x1040000000000000UL, ParamJsonPatch.ParseHexVersion("0x1040000000000000"));
        Assert.Null(ParamJsonPatch.ParseHexVersion("0x"));
    }

    [Fact]
    public void BuildRequest_LeavesDrmToTheEngineAndRequiredVersionToAPickedSdk()
    {
        var request = new BuildRequest();
        Assert.True(request.ForceStandardDrm);
        Assert.True(request.LowerRequiredFirmware);
        Assert.False(request.FullVerify);
        Assert.Equal(100, request.PlayGoChunks);
        Assert.False(request.ParamPatch.ForceStandardDrm);
        Assert.True(request.ParamPatch.LowerRequiredSystemVersion);

        request.SdkMajorOverride = 2;
        Assert.False(request.ParamPatch.LowerRequiredSystemVersion);

        request.SdkMajorOverride = null;
        request.LowerRequiredFirmware = false;
        request.ClearVersionFileUri = false;
        request.ClearPlayGoAttributes = false;
        Assert.False(request.ParamPatch.Any);
    }

    // ===================== PlayGo đầu vào =====================

    [Fact]
    public void ReadChunkCounts_FollowsTheEngineHeaderRules()
    {
        Assert.Equal(new PlayGoCleanup.Counts(37, 2), PlayGoCleanup.ReadChunkCounts(new MemoryStream(ChunkDat(37, 2))));
        Assert.Equal(new PlayGoCleanup.Counts(255, 64), PlayGoCleanup.ReadChunkCounts(new MemoryStream(ChunkDat(255, 64))));

        Assert.Null(PlayGoCleanup.ReadChunkCounts(new MemoryStream(ChunkDat(37, 2, length: 200))));
        Assert.Null(PlayGoCleanup.ReadChunkCounts(new MemoryStream(ChunkDat(0, 1))));
        Assert.Null(PlayGoCleanup.ReadChunkCounts(new MemoryStream(ChunkDat(256, 1))));
        Assert.Null(PlayGoCleanup.ReadChunkCounts(new MemoryStream(ChunkDat(10, 65))));

        var wrongLength = ChunkDat(10, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(wrongLength.AsSpan(16), 999);
        Assert.Null(PlayGoCleanup.ReadChunkCounts(new MemoryStream(wrongLength)));

        var wrongVersion = ChunkDat(10, 1);
        BinaryPrimitives.WriteUInt16LittleEndian(wrongVersion.AsSpan(4), 0x0100);
        Assert.Null(PlayGoCleanup.ReadChunkCounts(new MemoryStream(wrongVersion)));

        var random = new byte[2000];
        Random.Shared.NextBytes(random);
        Assert.Null(PlayGoCleanup.ReadChunkCounts(new MemoryStream(random)));
    }

    [Fact]
    public void CheckFolder_ReportsCountsAndBrokenFiles()
    {
        var none = MakeSource("check-none", "UP9000-PPSA26344_00-PSVIETHOAENGN000");
        Assert.Same(PlayGoCleanup.InputCheck.None, PlayGoCleanup.CheckFolder(none));

        var valid = MakeSource("check-valid", "UP9000-PPSA26344_00-PSVIETHOAENGN000");
        File.WriteAllBytes(Path.Combine(valid, "sce_sys", "playgo-chunk.dat"), ChunkDat(12, 3));
        File.WriteAllText(Path.Combine(valid, "sce_sys", "playgo-scenario.json"), "{\"scenarioCount\":3}");
        var validCheck = PlayGoCleanup.CheckFolder(valid);
        Assert.Equal(new PlayGoCleanup.Counts(12, 3), validCheck.Counts);
        Assert.Empty(validCheck.Invalid);

        var scenarioOnly = MakeSource("check-scenario", "UP9000-PPSA26344_00-PSVIETHOAENGN000");
        File.WriteAllText(Path.Combine(scenarioOnly, "sce_sys", "playgo-scenario.json"), "{\"scenarioCount\":4}");
        Assert.Equal(new PlayGoCleanup.Counts(0, 4), PlayGoCleanup.CheckFolder(scenarioOnly).Counts);

        var broken = MakeSource("check-broken", "UP9000-PPSA26344_00-PSVIETHOAENGN000");
        File.WriteAllBytes(Path.Combine(broken, "sce_sys", "playgo-chunk.dat"), new byte[300]);
        File.WriteAllText(Path.Combine(broken, "sce_sys", "playgo-scenario.json"), "{\"scenarioCount\":\"many\"}");
        var brokenCheck = PlayGoCleanup.CheckFolder(broken);
        Assert.Null(brokenCheck.Counts);
        Assert.Equal(new[] { "sce_sys/playgo-chunk.dat", "sce_sys/playgo-scenario.json" }, brokenCheck.Invalid);

        var metadata = MetadataReader.Read(broken, CancellationToken.None);
        Assert.Contains("playgo-chunk.dat", MetadataReader.DescribePlayGo(metadata, 100), StringComparison.Ordinal);
        Assert.Contains("100", MetadataReader.DescribePlayGo(MetadataReader.Read(none, CancellationToken.None), 100), StringComparison.Ordinal);
        Assert.Contains("12", MetadataReader.DescribePlayGo(MetadataReader.Read(valid, CancellationToken.None), 100), StringComparison.Ordinal);
    }

    // ===================== Tạo gói thật =====================

    [Fact]
    public async Task Build_ForcesDrmInTheEngineAndLowersTheRequiredVersion()
    {
        if (!BuildEngine.KeysAvailable)
        {
            return;
        }

        const string contentId = "UP9000-PPSA26344_00-PSVIETHOAENGN001";
        var folder = MakeSource("lower", contentId);
        var paramPath = Path.Combine(folder, "sce_sys", "param.json");
        var original = File.ReadAllBytes(paramPath);
        var request = Request(folder, "lower", contentId);
        request.ClearVersionFileUri = false;
        request.ClearPlayGoAttributes = false;
        var log = new List<LogEntry>();

        var outcome = await new BuildEngine().BuildAsync(request, log.Add, null, CancellationToken.None);
        var param = ExportParam(outcome.OutputPath, "lower");
        Assert.Equal("standard", param.GetProperty("applicationDrmType").GetString());
        Assert.Equal("0x0450000000000000", param.GetProperty("requiredSystemSoftwareVersion").GetString());
        Assert.Equal("0x0450000000000000", param.GetProperty("sdkVersion").GetString());
        Assert.Contains(log, e => e.Message.Contains("10.40", StringComparison.Ordinal) && e.Message.Contains("4.50", StringComparison.Ordinal));
        Assert.Equal(original, File.ReadAllBytes(paramPath));
    }

    [Fact]
    public async Task Build_KeepsTheRequiredVersionWhenAskedAndFollowsAPickedSdk()
    {
        if (!BuildEngine.KeysAvailable)
        {
            return;
        }

        const string contentId = "UP9000-PPSA26344_00-PSVIETHOAENGN002";
        var folder = MakeSource("keep-fw", contentId);

        var keep = Request(folder, "keep-fw", contentId);
        keep.LowerRequiredFirmware = false;
        keep.ForceStandardDrm = false;
        var kept = ExportParam((await new BuildEngine().BuildAsync(keep, _ => { }, null, CancellationToken.None)).OutputPath, "keep-fw");
        Assert.Equal("0x1040000000000000", kept.GetProperty("requiredSystemSoftwareVersion").GetString());
        Assert.Equal("free", kept.GetProperty("applicationDrmType").GetString());

        var picked = Request(folder, "sdk2", contentId);
        picked.SdkMajorOverride = 2;
        var sdk = ExportParam((await new BuildEngine().BuildAsync(picked, _ => { }, null, CancellationToken.None)).OutputPath, "sdk2");
        Assert.Equal("0x0200000000000000", sdk.GetProperty("requiredSystemSoftwareVersion").GetString());
        Assert.Equal("0x0200000000000000", sdk.GetProperty("sdkVersion").GetString());
    }

    [Fact]
    public async Task Build_ImportsPlayGoCountsFromAValidSourceWhenKept()
    {
        if (!BuildEngine.KeysAvailable)
        {
            return;
        }

        const string contentId = "UP9000-PPSA26344_00-PSVIETHOAENGN003";
        var folder = MakeSource("counts", contentId);
        var chunk = ChunkDat(37, 2);
        File.WriteAllBytes(Path.Combine(folder, "sce_sys", "playgo-chunk.dat"), chunk);

        var kept = Request(folder, "counts-keep", contentId);
        kept.RemovePlayGoFiles = false;
        var keptLog = new List<LogEntry>();
        var keptOutcome = await new BuildEngine().BuildAsync(kept, keptLog.Add, null, CancellationToken.None);
        Assert.Contains(keptLog, e => e.Message.Contains("37 chunks / 2 scenarios", StringComparison.Ordinal));
        Assert.Contains(keptOutcome.Verification.Contents!.Checks, c => c.Contains("37 chunks, 2 scenarios", StringComparison.Ordinal));

        // Engine không bao giờ chép bảng của nguồn: tệp trong gói là bản tạo mới.
        var export = Path.Combine(_root, "export-counts");
        PackageReader.ExportSceSys(keptOutcome.OutputPath, export, Passcode, CancellationToken.None);
        Assert.NotEqual(chunk, File.ReadAllBytes(Path.Combine(export, "sce_sys", "playgo-chunk.dat")));

        var dropped = Request(folder, "counts-drop", contentId);
        dropped.PlayGoChunks = 9;
        var droppedLog = new List<LogEntry>();
        await new BuildEngine().BuildAsync(dropped, droppedLog.Add, null, CancellationToken.None);
        Assert.Contains(droppedLog, e => e.Message.Contains("9 chunks / 1 scenarios", StringComparison.Ordinal));
        Assert.Equal(chunk, File.ReadAllBytes(Path.Combine(folder, "sce_sys", "playgo-chunk.dat")));
    }

    [Fact]
    public async Task Build_RunsTheEngineContentCheckQuickByDefaultAndFullOnRequest()
    {
        if (!BuildEngine.KeysAvailable)
        {
            return;
        }

        const string contentId = "UP9000-PPSA26344_00-PSVIETHOAENGN004";
        var folder = MakeSource("verify", contentId);

        var quick = await new BuildEngine().BuildAsync(Request(folder, "verify-quick", contentId), _ => { }, null, CancellationToken.None);
        Assert.NotNull(quick.Verification.Contents);
        Assert.True(quick.Verification.Contents!.IsValid);
        Assert.False(quick.Verification.Contents.IsFull);
        Assert.Contains(quick.Verification.Contents.Checks, c => c.Contains("PlayGo", StringComparison.Ordinal));

        var request = Request(folder, "verify-full", contentId);
        request.FullVerify = true;
        var phases = new List<string>();
        var full = await new BuildEngine().BuildAsync(request, _ => { }, new SyncProgress(p => phases.Add(p.Phase)), CancellationToken.None);
        var contents = full.Verification.Contents!;
        Assert.True(contents.IsValid);
        Assert.True(contents.IsFull);
        Assert.True(contents.FileCount > 0);
        Assert.Equal(contents.FileCount, contents.FilesDecoded);
        Assert.Equal(contents.OuterBlockCount, contents.OuterBlocksVerified);
        Assert.Contains(PhaseCatalog.VerifyFull.Name, phases);

        // Gói hỏng: lật một byte trong ảnh PFS ngoài (40% thân gói; phần đuôi chỉ là đệm) → kiểm tra đầy đủ phải báo lỗi.
        var broken = Path.Combine(_root, "broken.pkg");
        File.Copy(full.OutputPath, broken);
        using (var stream = new FileStream(broken, FileMode.Open, FileAccess.ReadWrite))
        {
            stream.Position = stream.Length * 2 / 5;
            var value = stream.ReadByte();
            stream.Position--;
            stream.WriteByte((byte)(value ^ 0xFF));
        }

        var damaged = PackageVerifier.VerifyContents(broken, Passcode, full: true, CancellationToken.None);
        Assert.False(damaged.IsValid);
        Assert.NotEmpty(damaged.Issues);
    }

    [Theory]
    [InlineData(PackageKind.Homebrew, OuterImageMode.PlaintextNoAuth)]
    [InlineData(PackageKind.Application, OuterImageMode.Native)]
    public async Task Build_OtherKindsAndImageModesPassTheFullCheck(PackageKind kind, OuterImageMode mode)
    {
        if (!BuildEngine.KeysAvailable)
        {
            return;
        }

        var contentId = kind == PackageKind.Homebrew ? "UP9000-PPSA26344_00-PSVIETHOAENGN005" : "UP9000-PPSA26344_00-PSVIETHOAENGN006";
        var folder = MakeSource("kind-" + kind + mode, contentId);
        var request = Request(folder, "kind-" + kind + mode, contentId);
        request.Kind = kind;
        request.ImageMode = mode;
        request.FullVerify = true;
        var outcome = await new BuildEngine().BuildAsync(request, _ => { }, null, CancellationToken.None);
        Assert.True(outcome.Verification.Contents!.IsValid);
        Assert.True(outcome.Verification.Contents.IsFull);
    }

    [Fact]
    public void PhaseSequence_AddsTheFullCheckOnlyWhenRequested()
    {
        Assert.DoesNotContain(PhaseCatalog.VerifyFull, PhaseCatalog.Sequence(false));
        var sequence = PhaseCatalog.Sequence(true, fullVerify: true);
        Assert.Equal(PhaseCatalog.VerifyFull, sequence[^1]);
        Assert.Equal(PhaseCatalog.Verify, sequence[^2]);
    }

    /// <summary>IProgress đồng bộ (Progress&lt;T&gt; gửi qua SynchronizationContext, dễ lỡ báo cáo cuối trong test).</summary>
    private sealed class SyncProgress(Action<BuildProgress> report) : IProgress<BuildProgress>
    {
        public void Report(BuildProgress value) => report(value);
    }
}
