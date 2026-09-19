using System.Text.Json;
using System.Xml.Linq;
using PsViethoa.FpkgBuilder.Core.Models;
using PsViethoa.FpkgBuilder.Core.Services;
using Xunit;

namespace PsViethoa.FpkgBuilder.Tests;

/// <summary>
/// Cách tạo gói bằng SDK Sony (bộ công cụ sdk-fpkg729-fix): quy tắc chọn tệp cho GP5 giống create-gp5-from-folder.py, keystone
/// 96 byte bắt buộc, phân loại nhật ký SDK, và một lượt tạo gói thật khi máy có bộ công cụ + Wine (bỏ qua nếu không có).
/// </summary>
public sealed class SonySdkTests : IDisposable
{
    private const string Passcode = "00000000000000000000000000000000";
    private const string ContentId = "EP9999-PPSA99998_00-PSVIETHOASDKTST0";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "psviethoa-sonysdk-" + Guid.NewGuid().ToString("N"));

    public SonySdkTests() => Directory.CreateDirectory(_root);

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

    private string MakeSource(string name, int keystoneLength = 96, string drm = "free")
    {
        var folder = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.Combine(folder, "sce_sys", "about"));
        Directory.CreateDirectory(Path.Combine(folder, "data"));
        File.WriteAllText(Path.Combine(folder, "sce_sys", "param.json"), JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["contentId"] = ContentId,
            ["contentVersion"] = "01.000.000",
            ["masterVersion"] = "01.00",
            ["titleId"] = "PPSA99998",
            ["sdkVersion"] = "0x0450000000000000",
            ["requiredSystemSoftwareVersion"] = "0x0450000000000000",
            ["applicationCategoryType"] = 0,
            ["applicationDrmType"] = drm,
            ["attribute"] = 0,
            ["attribute2"] = 0,
            ["attribute3"] = 0,
            ["localizedParameters"] = new Dictionary<string, object>
            {
                ["defaultLanguage"] = "ja-JP",
                ["ja-JP"] = new Dictionary<string, string> { ["titleName"] = "SDK テスト" },
            },
        }));
        if (keystoneLength > 0)
        {
            File.WriteAllBytes(Path.Combine(folder, "sce_sys", "keystone"), new byte[keystoneLength]);
        }

        File.WriteAllBytes(Path.Combine(folder, "sce_sys", "playgo-chunk.dat"), new byte[64]);
        File.WriteAllBytes(Path.Combine(folder, "sce_sys", "icon0.dds"), new byte[16]);
        File.WriteAllBytes(Path.Combine(folder, "sce_sys", "icon0.png"), new byte[16]);
        File.WriteAllBytes(Path.Combine(folder, "sce_sys", "about", "right.sprx"), new byte[16]);
        File.WriteAllBytes(Path.Combine(folder, "ampr_emu.index"), new byte[8]);
        File.WriteAllBytes(Path.Combine(folder, ".DS_Store"), new byte[8]);
        File.WriteAllText(Path.Combine(folder, "old.gp5"), "<psproject/>");
        File.WriteAllBytes(Path.Combine(folder, "eboot.bin"), new byte[4096]);
        File.WriteAllBytes(Path.Combine(folder, "data", "Big.bin"), Enumerable.Range(0, 300_000).Select(i => (byte)(i % 7)).ToArray());
        return folder;
    }

    [Theory]
    [InlineData("sce_sys/playgo-chunk.dat", true)]
    [InlineData("sce_sys/PLAYGO-SCENARIO.JSON", true)]
    [InlineData("sce_sys/about/right.sprx", true)]
    [InlineData("sce_sys/icon0.dds", true)]
    [InlineData("sce_sys/license.dat", true)]
    [InlineData("sce_sys/LICENSE.INFO", true)]
    [InlineData("sce_sys/origin-param.json", true)]
    [InlineData("sce_sys/target-param.json", true)]
    [InlineData("sce_sys/nptitle.dat", false)]
    [InlineData("sce_sys/npbind.dat", false)]
    [InlineData("sce_sys/nptitle.dat", false)]
    [InlineData("sce_sys/trophy2/trophy00.ucp", false)]
    [InlineData("sce_sys/icon0.png", false)]
    [InlineData("sce_sys/param.json", false)]
    [InlineData("data/texture.dds", false)]
    [InlineData("keystone", false)]
    public void ServiceArtifacts_MatchTheToolkitRules(string relative, bool expected) =>
        Assert.Equal(expected, SonySdkProject.IsServiceArtifact(relative));

    [Fact]
    public void Project_ListsExplicitFilesAndKeepsTheKeystone()
    {
        var source = MakeSource("project");
        var project = SonySdkProject.Create(source, Path.Combine(_root, "work", "game.gp5"), Passcode, path => path, SonySdkSourcePlan.Pure);

        var document = XDocument.Load(project.ProjectPath);
        var package = document.Root!.Element("volume")!.Element("package")!;
        Assert.Equal(ContentId, package.Attribute("content_id")!.Value);
        Assert.Equal(Passcode, package.Attribute("passcode")!.Value);
        Assert.Equal("prospero_app", document.Root.Element("volume")!.Element("volume_type")!.Value);

        var destinations = document.Root.Element("files")!.Elements("file").Select(e => e.Attribute("dst_path")!.Value).ToList();
        Assert.Equal("sce_sys/playgo-scenario.json", destinations[0]);
        // Kế hoạch "Pure" = đúng script gốc: tệp rác .DS_Store cũng được liệt kê.
        Assert.Equal(["sce_sys/playgo-scenario.json", ".DS_Store", "data/Big.bin", "eboot.bin", "sce_sys/icon0.png", "sce_sys/keystone", "sce_sys/param.json"], destinations);
        Assert.Equal(destinations.Count, project.FileCount);
        Assert.Equal(
            ["ampr_emu.index (host emulator index)", "old.gp5 (project artifact)", "sce_sys/about/right.sprx (reserved/SDK-generated sce_sys artifact)", "sce_sys/icon0.dds (reserved/SDK-generated sce_sys artifact)", "sce_sys/playgo-chunk.dat (reserved/SDK-generated sce_sys artifact)"],
            project.Excluded);
        Assert.Empty(project.SkippedByApp);
        Assert.StartsWith("Created " + project.ProjectPath + " with 7 explicit file mapping(s).\nExcluded 5 service/project artifact(s):\n  ampr_emu.index (host emulator index)\n", project.Report);

        var scenario = JsonDocument.Parse(File.ReadAllText(project.ScenarioPath)).RootElement;
        Assert.Equal("ja-JP", scenario.GetProperty("scenarioDefaultLanguage").GetString());
        Assert.Equal("Scenario #0", scenario.GetProperty("scenarios")[0].GetProperty("ja-JP").GetProperty("title").GetString());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(95)]
    public void Project_RequiresA96ByteKeystone(int length)
    {
        var source = MakeSource("keystone-" + length, keystoneLength: length);
        Assert.False(SonySdkProject.HasValidKeystone(source, out _));
        Assert.Throws<InvalidDataException>(() => SonySdkProject.Create(source, Path.Combine(_root, "work-" + length, "game.gp5"), Passcode, path => path, SonySdkSourcePlan.Pure));
    }

    [Fact]
    public void RoutineSdkWarnings_AreInformational()
    {
        Assert.Equal(LogLevel.Info, SonySdkRunner.Classify("[Warn]\tThe version of the SDK toolchain is invalid or missing.")!.Level);
        Assert.Equal(LogLevel.Info, SonySdkRunner.Classify("[Warn]\t(online check) Publishing Tools Version 2.79 may not be used for master submission. (expired version)")!.Level);
        Assert.Equal(LogLevel.Warning, SonySdkRunner.Classify("[Warn]\tsce_sys/icon0.png has an unexpected size.")!.Level);
        Assert.Equal(LogLevel.Error, SonySdkRunner.Classify("[Error]\tparam.json: contentId is invalid.")!.Level);
        Assert.Null(SonySdkRunner.Classify("[mvk-info] Created VkInstance for Vulkan version 1.0.323"));
        Assert.Null(SonySdkRunner.Classify("|____.____|____.____|____.____|____.____|____.____|"));
    }

    [Fact]
    public void WinePaths_UseTheZDrive()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.Equal(@"Z:\Users\a b\game\eboot.bin", SonySdkToolchain.ToWinePath("/Users/a b/game/eboot.bin"));
    }

    [Fact]
    public void DiskEstimate_HasNoIntermediateImageWithTheSdk()
    {
        var engine = DiskSpaceAdvisor.Check(_root, _root, 1_000_000_000);
        var sdk = DiskSpaceAdvisor.Check(_root, _root, 1_000_000_000, sonySdk: true);
        var sdkWithStaging = DiskSpaceAdvisor.Check(_root, _root, 1_000_000_000, stagingBytes: 500_000_000, sonySdk: true);
        Assert.Equal((long)(1_000_000_000 * DiskSpaceAdvisor.TemporaryFactor), engine.EstimatedTemporaryBytes);
        Assert.Equal(0, sdk.EstimatedTemporaryBytes);
        Assert.Equal(engine.EstimatedOutputBytes, sdk.EstimatedOutputBytes);
        Assert.Equal((long)(500_000_000 * DiskSpaceAdvisor.StagingFactor), sdkWithStaging.EstimatedTemporaryBytes);
    }

    [Fact]
    public void PackageName_MatchesTheBuiltInEngine() =>
        Assert.Equal(ContentId + "-A0100-V0100.pkg", SonySdkBuilder.PackageFileName(ContentId, "01.000.000"));

    [Fact]
    public void Defaults_UseTheSonySdk()
    {
        var request = new BuildRequest();
        Assert.True(request.UseSonySdk);
        Assert.False(request.ParamPatch.ForceStandardDrm);
    }

    [Fact]
    public async Task MissingKeystone_StopsBeforeBuilding()
    {
        if (SonySdkToolchain.Resolve(out _) == null)
        {
            return;
        }

        var source = MakeSource("no-keystone", keystoneLength: 0);
        var request = new BuildRequest
        {
            SourcePath = source,
            OutputFolder = Path.Combine(_root, "out-no-keystone"),
            TemporaryFolder = Path.Combine(_root, "tmp-no-keystone"),
            ContentId = ContentId,
            PreventSleep = false,
        };
        var error = await Assert.ThrowsAsync<BuildValidationException>(() => new BuildEngine().BuildAsync(request, _ => { }, null, CancellationToken.None));
        Assert.Contains(error.Errors, e => e.Message.Contains("keystone", StringComparison.Ordinal));
        Assert.False(Directory.Exists(request.OutputFolder) && Directory.EnumerateFiles(request.OutputFolder, "*.pkg").Any());
    }

    /// <summary>Đo bằng img_create 2.79: ASCII in được trừ % và ; thì nhận; ngoài ASCII hoặc kết thúc bằng dấu chấm thì từ chối.</summary>
    [Theory]
    [InlineData("data/plain.bin", true)]
    [InlineData("data/ leading space & (x) [y] {z} #1 $2 !3 +4 ,5 =6 @7 ^8 _9 `0 ~.bin", true)]
    [InlineData("data/it's.bin", true)]
    [InlineData(".hidden/x.bin", true)]
    [InlineData("data/pct%20.bin", false)]
    [InlineData("data/semi;colon.bin", false)]
    [InlineData("data/Tiếng Việt.bin", false)]
    [InlineData("dữ liệu/file.bin", false)]
    [InlineData("data/日本語.bin", false)]
    [InlineData("data/café.bin", false)]
    [InlineData("data/trailing.", false)]
    public void PackagePaths_FollowWhatPublishingToolsAccepts(string relative, bool expected) =>
        Assert.Equal(expected, SonySdkProject.IsSdkPackagePath(relative));

    /// <summary>Quét trước chỉ mở để đọc: đếm đúng tệp, tệp không mở được không làm hỏng lượt, nội dung và thời gian ghi không đổi.</summary>
    [Fact]
    public void Prescan_OpensEveryFileReadOnly()
    {
        var folder = Path.Combine(_root, "prescan");
        Directory.CreateDirectory(folder);
        var paths = Enumerable.Range(0, 300).Select(i => Path.Combine(folder, $"f{i:000}.bin")).ToList();
        foreach (var path in paths)
        {
            File.WriteAllBytes(path, [1, 2, 3]);
        }

        var written = paths.ToDictionary(path => path, File.GetLastWriteTimeUtc);
        var reports = new List<(int Done, int Total)>();
        var result = SonySdkPrescan.Run([.. paths, Path.Combine(folder, "missing.bin")], 8, (done, total) => { lock (reports) { reports.Add((done, total)); } }, CancellationToken.None);

        Assert.Equal(301, result.Files);
        Assert.Equal(1, result.Failed);
        Assert.Contains((301, 301), reports);
        Assert.All(paths, path => Assert.Equal([1, 2, 3], File.ReadAllBytes(path)));
        Assert.All(paths, path => Assert.Equal(written[path], File.GetLastWriteTimeUtc(path)));
        Assert.Throws<OperationCanceledException>(() => SonySdkPrescan.Run(paths, 4, null, new CancellationToken(canceled: true)));
    }

    /// <summary>Tên tệp SDK không nhận: dừng trước img_create, thông báo nêu đúng tệp; GP5 vẫn được ghi để tra cứu như script gốc.</summary>
    [Fact]
    public async Task UnsupportedFileNames_StopBeforePublishingTools()
    {
        if (SonySdkToolchain.Resolve(out _) == null)
        {
            return;
        }

        var source = MakeSource("bad-names");
        File.WriteAllBytes(Path.Combine(source, "data", "Bản dịch.bin"), new byte[8]);
        File.WriteAllBytes(Path.Combine(source, "data", "50%.bin"), new byte[8]);
        var request = new BuildRequest
        {
            SourcePath = source,
            OutputFolder = Path.Combine(_root, "out-bad-names"),
            TemporaryFolder = Path.Combine(_root, "tmp-bad-names"),
            ContentId = ContentId,
            PreventSleep = false,
        };
        var log = new List<LogEntry>();
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => new BuildEngine().BuildAsync(request, log.Add, null, CancellationToken.None));
        Assert.Contains("data/Bản dịch.bin", error.Message);
        Assert.Contains("data/50%.bin", error.Message);
        Assert.DoesNotContain(log, e => e.Message.StartsWith("SDK: ", StringComparison.Ordinal));
        Assert.Empty(Directory.EnumerateFiles(request.OutputFolder, "*.pkg"));
        Assert.Single(Directory.EnumerateFiles(request.OutputFolder, "*.gp5"));
    }

    /// <summary>Lượt tạo gói thật qua Publishing Tools (và Wine trên macOS): gói hợp lệ, DRM đã ép "standard", nguồn không đổi.</summary>
    [Fact]
    public async Task Build_ProducesAValidPlaintextPackage()
    {
        if (SonySdkToolchain.Resolve(out _) == null)
        {
            return;
        }

        var source = MakeSource("build");
        // Publishing Tools kiểm tra định dạng PNG thật — ảnh giả của MakeSource chỉ dùng cho phép thử chọn tệp.
        File.Delete(Path.Combine(source, "sce_sys", "icon0.png"));
        var paramPath = Path.Combine(source, "sce_sys", "param.json");
        var original = File.ReadAllBytes(paramPath);
        var log = new List<LogEntry>();
        var progress = new List<BuildProgress>();
        var request = new BuildRequest
        {
            SourcePath = source,
            OutputFolder = Path.Combine(_root, "out"),
            // Giữ .gp5 / .gp5-assets cạnh gói để kiểm tra (mặc định xoá như bộ fix6).
            SdkKeepIntermediate = true,
            TemporaryFolder = Path.Combine(_root, "tmp"),
            ContentId = ContentId,
            PreventSleep = false,
        };

        var outcome = await new BuildEngine().BuildAsync(request, log.Add, new SyncProgress(progress.Add), CancellationToken.None);

        Assert.Equal(ContentId + "-A0100-V0100.pkg", Path.GetFileName(outcome.OutputPath));
        Assert.True(outcome.Verification.Contents!.IsValid);
        Assert.Equal(PackageVerifier.PlaintextMarker, outcome.Verification.SeedMarker);
        Assert.Contains(log, e => e.Message.Contains("[3/3]", StringComparison.Ordinal));
        Assert.Contains(progress, p => p.Phase == PhaseCatalog.SdkImage.Name && p.PhasePercent >= 100);

        // Thư mục xuất như build-from-folder.ps1: gói + .gp5 + .playgo-scenario.json + <tên>-build-logs; gói thô và .naps_metric.json đã bỏ.
        var stem = ContentId + "-A0100-V0100";
        Assert.Equal([stem + ".gp5", stem + ".pkg", stem + ".playgo-scenario.json"], Directory.EnumerateFiles(request.OutputFolder).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        var logs = Path.Combine(request.OutputFolder, stem + "-build-logs");
        // Bộ fix6 (direct-v3) ghi thẳng gói plaintext: chỉ còn 01 và 02; bộ cũ có thêm 03-postprocess.log.
        var logNames = Directory.EnumerateFiles(logs).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(["01-create-gp5.log", "02-img-create.log"], logNames.Take(2));
        Assert.True(logNames.Count == 2 || (logNames.Count == 3 && logNames[2] == "03-postprocess.log"));
        Assert.StartsWith("Created " + SonySdkProject.RealPath(Path.Combine(request.OutputFolder, stem + ".gp5")) + " with ", File.ReadAllText(Path.Combine(logs, "01-create-gp5.log")));
        var imageLog = File.ReadAllLines(Path.Combine(logs, "02-img-create.log"));
        Assert.StartsWith("command=img_create --oformat nwonly ", imageLog[0]);
        Assert.Equal(["started_utc", "finished_utc", "elapsed", "exit_code"], imageLog.Skip(1).Take(4).Select(line => line.Split('=')[0]));
        Assert.Equal("exit_code=0", imageLog[4]);
        Assert.False(File.Exists(Path.Combine(request.OutputFolder, stem + ".partial.pkg")));

        // GP5 trỏ thẳng vào nguồn (không có thư mục gương), param.json trỏ sang bản đã sửa trong thư mục tạm, không còn tệp tạm nào.
        var gp5 = File.ReadAllText(Path.Combine(request.OutputFolder, stem + ".gp5"));
        var runtime = SonySdkToolchain.Resolve(out _)!;
        Assert.Contains("dst_path=\"eboot.bin\" src_path=\"" + SonySdkProject.EscapeAttribute(runtime.ToolPath(SonySdkProject.RealPath(Path.Combine(source, "eboot.bin")))) + "\"", gp5);
        Assert.DoesNotContain(SonySdkProject.EscapeAttribute(runtime.ToolPath(SonySdkProject.RealPath(paramPath))), gp5);
        Assert.DoesNotContain("mirror-", gp5);
        Assert.False(Directory.Exists(request.TemporaryFolder) && Directory.EnumerateFileSystemEntries(request.TemporaryFolder).Any(), "temporary folder should be empty after the build");

        var info = PackageInspector.Inspect(outcome.OutputPath, Passcode, CancellationToken.None);
        Assert.Equal("standard", info.Params!.ApplicationDrmType);
        Assert.Equal(original, File.ReadAllBytes(paramPath));
    }

    /// <summary>Hàng chờ: hai lượt SDK chạy song song trong cùng tiến trình (cùng WINEPREFIX, thư mục tạm/bí danh riêng) đều ra gói hợp lệ.</summary>
    [Fact]
    public async Task Build_TwoSdkBuildsInParallelBothSucceed()
    {
        if (SonySdkToolchain.Resolve(out _) == null)
        {
            return;
        }

        var requests = new[] { "parallel-a", "parallel-b" }.Select(name =>
        {
            var source = MakeSource(name);
            File.Delete(Path.Combine(source, "sce_sys", "icon0.png"));
            return new BuildRequest
            {
                SourcePath = source,
                OutputFolder = Path.Combine(_root, name + "-out"),
                TemporaryFolder = Path.Combine(_root, name + "-tmp"),
                ContentId = ContentId,
                PreventSleep = false,
            };
        }).ToArray();

        var engine = new BuildEngine();
        var outcomes = await Task.WhenAll(requests.Select(request => engine.BuildAsync(request, _ => { }, null, CancellationToken.None)));

        Assert.All(outcomes, outcome =>
        {
            Assert.True(outcome.Verification.Contents!.IsValid);
            Assert.Equal(PackageVerifier.PlaintextMarker, outcome.Verification.SeedMarker);
        });
        Assert.NotEqual(outcomes[0].OutputPath, outcomes[1].OutputPath);
        // Mặc định như bộ fix6: .gp5, scenario và .gp5-assets bị xoá sau khi tạo xong, nhật ký giữ lại.
        Assert.All(requests, request =>
        {
            Assert.Empty(Directory.EnumerateFiles(request.OutputFolder, "*.gp5"));
            Assert.Empty(Directory.EnumerateFiles(request.OutputFolder, "*.playgo-scenario.json"));
            Assert.False(Directory.Exists(Path.Combine(request.OutputFolder, ".gp5-assets")));
            Assert.Single(Directory.EnumerateDirectories(request.OutputFolder, "*-build-logs"));
        });
        Assert.All(requests, request => Assert.False(Directory.Exists(request.TemporaryFolder) && Directory.EnumerateFileSystemEntries(request.TemporaryFolder).Any()));
    }

    /// <summary>Hàng chờ: huỷ một lượt SDK đang chạy không được tắt wineserver dùng chung — lượt kia phải chạy tiếp và thành công.</summary>
    [Fact]
    public async Task Build_CancellingOneSdkBuildLeavesTheOtherRunning()
    {
        if (SonySdkToolchain.Resolve(out _) == null)
        {
            return;
        }

        var survivor = MakeSource("survivor");
        var victim = MakeSource("victim");
        foreach (var source in new[] { survivor, victim })
        {
            File.Delete(Path.Combine(source, "sce_sys", "icon0.png"));
        }

        // Nạn nhân có nhiều dữ liệu hơn để chắc chắn còn đang trong img_create lúc bị huỷ.
        File.WriteAllBytes(Path.Combine(victim, "data", "More.bin"), Enumerable.Range(0, 40_000_000).Select(i => (byte)(i * 31 % 251)).ToArray());

        BuildRequest Request(string source, string name) => new()
        {
            SourcePath = source,
            OutputFolder = Path.Combine(_root, name + "-out"),
            TemporaryFolder = Path.Combine(_root, name + "-tmp"),
            ContentId = ContentId,
            PreventSleep = false,
        };

        var engine = new BuildEngine();
        var victimLog = new List<LogEntry>();
        using var cancellation = new CancellationTokenSource();
        var victimTask = engine.BuildAsync(Request(victim, "victim"), entry => { lock (victimLog) { victimLog.Add(entry); } }, null, cancellation.Token);
        var survivorTask = engine.BuildAsync(Request(survivor, "survivor"), _ => { }, null, CancellationToken.None);

        // Chờ nạn nhân vào bước [2/3] (img_create đang chạy) rồi huỷ.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (DateTime.UtcNow < deadline && !victimTask.IsCompleted)
        {
            lock (victimLog)
            {
                if (victimLog.Any(entry => entry.Message.Contains("[2/3]", StringComparison.Ordinal)))
                {
                    break;
                }
            }

            await Task.Delay(50);
        }

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => victimTask);

        var outcome = await survivorTask;
        Assert.True(outcome.Verification.Contents!.IsValid);
        Assert.Equal(PackageVerifier.PlaintextMarker, outcome.Verification.SeedMarker);
    }

    /// <summary>Bộ công cụ fixdss3: DRM luôn standard (kể cả khi bỏ tích), pic*.png thiếu được khôi phục từ DDS và vào CNT của gói.</summary>
    [Fact]
    public async Task Build_ForcesDrmStandardAndRecoversTheSplashPng()
    {
        if (SonySdkToolchain.Resolve(out _) == null)
        {
            return;
        }

        var source = MakeSource("splash", drm: "free");
        File.Delete(Path.Combine(source, "sce_sys", "icon0.png"));
        using (var gz = new System.IO.Compression.GZipStream(File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", "bc7-icon0.dds.gz")), System.IO.Compression.CompressionMode.Decompress))
        using (var dds = File.Create(Path.Combine(source, "sce_sys", "pic0.dds")))
        {
            gz.CopyTo(dds);
        }

        var log = new List<LogEntry>();
        var request = new BuildRequest
        {
            SourcePath = source,
            OutputFolder = Path.Combine(_root, "out-splash"),
            // Giữ .gp5 / .gp5-assets cạnh gói để kiểm tra (mặc định xoá như bộ fix6).
            SdkKeepIntermediate = true,
            TemporaryFolder = Path.Combine(_root, "tmp-splash"),
            ContentId = ContentId,
            ForceStandardDrm = false,
            PreventSleep = false,
        };

        var outcome = await new BuildEngine().BuildAsync(request, log.Add, null, CancellationToken.None);

        Assert.True(outcome.Verification.Contents!.IsValid);
        var stem = ContentId + "-A0100-V0100";
        Assert.True(File.Exists(Path.Combine(request.OutputFolder, ".gp5-assets", stem, "sce_sys", "pic0.png")));
        Assert.Contains("\"applicationDrmType\": \"standard\"", File.ReadAllText(Path.Combine(request.OutputFolder, ".gp5-assets", stem, "sce_sys", "param.json")));
        Assert.Equal("standard", PackageInspector.Inspect(outcome.OutputPath, Passcode, CancellationToken.None).Params!.ApplicationDrmType);
        Assert.Contains(log, e => e.Message.Contains("prospero-dds2png", StringComparison.Ordinal));
        var cnt = PackageReader.ExportCntEntries(outcome.OutputPath, Path.Combine(_root, "cnt-splash"), Passcode, CancellationToken.None);
        Assert.Contains(cnt, name => name.EndsWith("pic0.png", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(cnt, name => name.EndsWith("license.dat", StringComparison.OrdinalIgnoreCase));
        Assert.StartsWith("Recovering sce_sys/pic0.png from pic0.dds...\n", File.ReadAllText(Path.Combine(request.OutputFolder, stem + "-build-logs", "01-create-gp5.log")));
    }

    /// <summary>Gói giải nén ra có thể mang pic2.png vài trăm byte rác: bỏ khỏi GP5 và khôi phục từ pic2.dds thay vì để SDK dừng.</summary>
    [Fact]
    public void Project_ReplacesAnInvalidSplashPngWithTheDdsRecovery()
    {
        var source = MakeSource("bad-png");
        File.Delete(Path.Combine(source, "sce_sys", "icon0.dds"));
        File.WriteAllBytes(Path.Combine(source, "sce_sys", "icon0.png"), MinimalPng());
        File.WriteAllBytes(Path.Combine(source, "sce_sys", "pic2.png"), Enumerable.Range(0, 532).Select(i => (byte)(i * 7)).ToArray());
        File.WriteAllBytes(Path.Combine(source, "sce_sys", "pic2.dds"), new byte[148]);
        Assert.False(SonySdkProject.IsValidPresentationPng(Path.Combine(source, "sce_sys", "pic2.png")));
        Assert.True(SonySdkProject.IsValidPresentationPng(Path.Combine(source, "sce_sys", "icon0.png")));
        Assert.Equal(["pic2.png"], SonySdkProject.MissingPresentationPngs(source).Select(item => item.PngName));

        var projectPath = Path.Combine(_root, "bad-png-out", ContentId + "-A0100-V0100.gp5");
        var result = SonySdkProject.Create(source, projectPath, Passcode, path => path, ddsConverter: (dds, png, preserveAlpha) =>
        {
            Assert.EndsWith("pic2.dds", dds, StringComparison.Ordinal);
            Assert.True(preserveAlpha);
            File.WriteAllBytes(png, MinimalPng());
        });

        var recovered = Assert.Single(result.RecoveredPngs);
        Assert.Equal("sce_sys/pic2.png", recovered.Destination);
        Assert.True(recovered.ReplacedInvalid);
        Assert.Contains(result.Excluded, line => line.StartsWith("sce_sys/pic2.png (not a valid PNG", StringComparison.Ordinal));
        var gp5 = File.ReadAllText(projectPath);
        Assert.Contains("dst_path=\"sce_sys/pic2.png\" src_path=\"" + SonySdkProject.EscapeAttribute(recovered.PngPath) + "\"", gp5);
        Assert.DoesNotContain(SonySdkProject.EscapeAttribute(Path.Combine(source, "sce_sys", "pic2.png")), gp5);
    }

    /// <summary>PNG 1×1 RGBA hợp lệ (IHDR 8 bit, màu 6, không interlace).</summary>
    private static byte[] MinimalPng()
    {
        static byte[] Chunk(string type, byte[] data)
        {
            var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
            var crc = Crc32(typeBytes.Concat(data).ToArray());
            return BitConverter.GetBytes(data.Length).Reverse().Concat(typeBytes).Concat(data).Concat(BitConverter.GetBytes(crc).Reverse()).ToArray();
        }

        static uint Crc32(byte[] bytes)
        {
            uint crc = 0xFFFFFFFF;
            foreach (var b in bytes)
            {
                crc ^= b;
                for (var i = 0; i < 8; i++)
                {
                    crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
                }
            }

            return ~crc;
        }

        var ihdr = new byte[] { 0, 0, 0, 1, 0, 0, 0, 1, 8, 6, 0, 0, 0 };
        using var idat = new MemoryStream();
        using (var zlib = new System.IO.Compression.ZLibStream(idat, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(new byte[] { 0, 255, 0, 0, 255 });
        }

        return new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }
            .Concat(Chunk("IHDR", ihdr)).Concat(Chunk("IDAT", idat.ToArray())).Concat(Chunk("IEND", Array.Empty<byte>())).ToArray();
    }

    [Fact]
    public void Converter_DetectsAPackageThatIsAlreadyPlaintext()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "direct.pkg");
        var bytes = new byte[0x30000];
        "\u007fFIH"u8.CopyTo(bytes);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(0x20), 0x20000);
        File.WriteAllBytes(path, bytes);
        Assert.False(SonySdkConverter.IsAlreadyPlaintext(path));

        SonySdkConverter.PlaintextSeed.CopyTo(bytes.AsSpan(0x20000 + 0x370));
        File.WriteAllBytes(path, bytes);
        Assert.True(SonySdkConverter.IsAlreadyPlaintext(path));
        Assert.False(SonySdkConverter.IsAlreadyPlaintext(Path.Combine(_root, "missing.pkg")));
    }

    [Fact]
    public void Aliases_StaleFoldersOfDeadProcessesAreRemoved()
    {
        var baseFolder = Path.Combine(_root, "stale-base");
        var target = Path.Combine(_root, "stale-target");
        Directory.CreateDirectory(target);
        File.WriteAllBytes(Path.Combine(target, "keep.bin"), new byte[1]);
        var dead = Path.Combine(baseFolder, "psviethoa-sdk-7fffffff-abcdef");
        var alive = Path.Combine(baseFolder, "psviethoa-sdk-" + Environment.ProcessId.ToString("x") + "-123456");
        Directory.CreateDirectory(dead);
        Directory.CreateDirectory(alive);
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        Directory.CreateSymbolicLink(Path.Combine(dead, "src"), target);
        Directory.CreateSymbolicLink(Path.Combine(alive, "src"), target);

        SonySdkPathAliases.CleanupStale([baseFolder]);

        Assert.False(Directory.Exists(dead), "dead process' alias folder should be removed");
        Assert.True(Directory.Exists(alive), "this process' alias folder must stay");
        Assert.True(File.Exists(Path.Combine(target, "keep.bin")), "the target folder must not be touched");
        File.Delete(Path.Combine(alive, "src"));
    }

    [Fact]
    public void Aliases_OnlyForNonAsciiAndRemovedOnDispose()
    {
        var ascii = Path.Combine(_root, "plain");
        var accented = Path.Combine(_root, "Ghost of Yōtei 格雷克");
        Directory.CreateDirectory(ascii);
        Directory.CreateDirectory(accented);
        File.WriteAllBytes(Path.Combine(accented, "eboot.bin"), new byte[4]);
        var log = new List<LogEntry>();
        string alias;
        string? folder;
        using (var aliases = SonySdkPathAliases.Create([Path.Combine(_root, "bases")], log.Add))
        {
            Assert.Equal(SonySdkProject.RealPath(ascii), aliases.Add(SonySdkProject.RealPath(ascii), "a"));
            Assert.Equal(0, aliases.Count);
            alias = aliases.Add(SonySdkProject.RealPath(accented), "src");
            Assert.True(SonySdkPathAliases.IsAsciiSafe(alias), alias);
            Assert.True(File.Exists(Path.Combine(alias, "eboot.bin")));
            Assert.Equal(alias + Path.DirectorySeparatorChar + "eboot.bin", aliases.Map(Path.Combine(SonySdkProject.RealPath(accented), "eboot.bin")));
            Assert.Equal(alias, aliases.Map(SonySdkProject.RealPath(accented)));
            Assert.Equal("/elsewhere/x", aliases.Map("/elsewhere/x"));
            Assert.Equal(alias, aliases.Add(SonySdkProject.RealPath(accented), "src"));
            folder = aliases.Folder;
            Assert.Contains(log, e => e.Message.Contains(alias, StringComparison.Ordinal));
        }

        Assert.False(Directory.Exists(alias) || File.Exists(alias));
        Assert.False(Directory.Exists(folder!));
        Assert.True(File.Exists(Path.Combine(accented, "eboot.bin")), "the real folder must stay untouched");
    }

    /// <summary>Thư mục nguồn và thư mục xuất có chữ ō/漢字: SDK phải đọc qua bí danh và vẫn ra gói hợp lệ.</summary>
    [Fact]
    public async Task Build_WorksWithNonAsciiSourceAndOutputPaths()
    {
        if (SonySdkToolchain.Resolve(out _) == null)
        {
            return;
        }

        var source = MakeSource("Ghost of Yōtei 格雷克", drm: "standard");
        File.Delete(Path.Combine(source, "sce_sys", "icon0.png"));
        var log = new List<LogEntry>();
        var request = new BuildRequest
        {
            SourcePath = source,
            OutputFolder = Path.Combine(_root, "Yōtei-pkg"),
            // Giữ .gp5 / .gp5-assets cạnh gói để kiểm tra (mặc định xoá như bộ fix6).
            SdkKeepIntermediate = true,
            TemporaryFolder = Path.Combine(_root, "tạm"),
            ContentId = ContentId,
            PreventSleep = false,
        };

        var outcome = await new BuildEngine().BuildAsync(request, log.Add, null, CancellationToken.None);

        Assert.True(outcome.Verification.Contents!.IsValid);
        Assert.Equal(request.OutputFolder, Path.GetDirectoryName(outcome.OutputPath));
        var gp5 = File.ReadAllText(Path.Combine(request.OutputFolder, ContentId + "-A0100-V0100.gp5"));
        Assert.DoesNotContain("Yōtei", gp5);
        Assert.Contains(log, e => e.Message.Contains("psviethoa-sdk-", StringComparison.Ordinal));
        // Chỉ xét thư mục bí danh của lượt này (tiến trình này); thư mục cũ của tiến trình khác do CleanupStale xử lý.
        var mine = "psviethoa-sdk-" + Environment.ProcessId.ToString("x") + "-*";
        Assert.False(Directory.EnumerateDirectories(Path.GetTempPath(), mine).Any(), "alias folders must be removed after the build");
    }

    private sealed class SyncProgress(Action<BuildProgress> report) : IProgress<BuildProgress>
    {
        public void Report(BuildProgress value) => report(value);
    }
}
