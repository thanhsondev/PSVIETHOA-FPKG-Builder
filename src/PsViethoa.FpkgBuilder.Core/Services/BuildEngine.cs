using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using LibProsperoPkg;
using LibProsperoPkg.PFS.Compression;
using PsViethoa.FpkgBuilder.Core.ExFat;
using PsViethoa.FpkgBuilder.Core.Localization;
using PsViethoa.FpkgBuilder.Core.Models;

namespace PsViethoa.FpkgBuilder.Core.Services;

/// <summary>Bọc LibProsperoPkg: chuẩn bị nguồn (thư mục hoặc ảnh exFAT), tạo gói, theo dõi tiến trình, kiểm tra kết quả.</summary>
public sealed class BuildEngine
{
    public static string LibraryVersion =>
        typeof(ProsperoPackageBuilder).Assembly.GetName().Version?.ToString(3) ?? "?";

    /// <summary>Khoá debug PS5 có sẵn trong thư viện hay không (không có thì không thể tạo gói).</summary>
    public static bool KeysAvailable
    {
        get
        {
            try
            {
                return ProsperoPackageBuilder.KeysAvailable;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    public async Task<BuildOutcome> BuildAsync(
        BuildRequest request,
        Action<LogEntry> log,
        IProgress<BuildProgress>? progress,
        CancellationToken cancellationToken,
        Func<string, bool>? diskFullRetry = null,
        Func<IReadOnlyList<string>, OutputConflictChoice>? onOutputConflict = null)
    {
        var normalized = BuildPreparer.Normalize(request);
        var errors = BuildPreparer.Validate(normalized);
        if (errors.Count > 0)
        {
            throw new BuildValidationException(errors);
        }

        var source = await Task.Run(() => SourceLocator.Resolve(normalized.SourcePath), cancellationToken).ConfigureAwait(false);
        var project = source.IsGp5
            ? await Task.Run(() => Gp5ProjectInfo.Load(source.Path), cancellationToken).ConfigureAwait(false)
            : null;

        Directory.CreateDirectory(normalized.OutputFolder);

        // Nhớ thư mục tạm đã có sẵn chưa: nếu do lần tạo gói này sinh ra và cuối cùng vẫn rỗng thì dọn luôn,
        // đừng để lại thư mục "fpkg-temp" rỗng cạnh thư mục xuất.
        var temporaryFolderExisted = Directory.Exists(normalized.TemporaryFolder);
        Directory.CreateDirectory(normalized.TemporaryFolder);

        // Thư viện luôn ghi đè tệp trùng tên — hỏi trước để người dùng giữ được bản cũ.
        // Chạy trên luồng nền: người gọi chờ đồng bộ hộp thoại trên luồng giao diện, nếu chỗ này đang ở luồng giao diện
        // thì hai bên chờ nhau và ứng dụng treo cứng.
        await Task.Run(() => ResolveOutputConflict(normalized.OutputFolder, normalized.ContentId, onOutputConflict, log, cancellationToken), cancellationToken)
            .ConfigureAwait(false);

        var backend = BuildPreparer.ResolveBackend(normalized, out var publishingToolsPath);
        var strategy = source.IsExFat
            ? await Task.Run(() => DecideStrategy(normalized, source, log), cancellationToken).ConfigureAwait(false)
            : ExFatStrategy.Auto;
        var exFatPhase = source.IsExFat
            ? strategy == ExFatStrategy.Mount ? PhaseCatalog.Mount : PhaseCatalog.Extract
            : source.IsUfs ? PhaseCatalog.Extract : null;

        var stopwatch = Stopwatch.StartNew();
        var tracker = new ProgressTracker(stopwatch, PhaseCatalog.Sequence(normalized.ComputeSha256, exFatPhase));
        LogPlan(normalized, source, project, backend, publishingToolsPath, log);
        progress?.Report(tracker.Current);

        using var sleepGuard = normalized.PreventSleep ? SleepInhibitor.TryAcquire() : null;
        if (sleepGuard != null)
        {
            log(new LogEntry(LogLevel.Info, Loc.F("Plan.SleepGuard", sleepGuard.Mechanism)));
        }

        ImageMount? mount = null;
        string? staging = null;
        try
        {
            // Dự án GP5: thư viện đọc manifest qua ProjectFilePath, SourceFolder là thư mục chứa tệp .gp5.
            var sourceFolder = source.IsGp5 ? source.ProjectDirectory : source.Path;
            if (source.IsExFat)
            {
                progress?.Report(tracker.EnterPhase(exFatPhase!));
                if (strategy == ExFatStrategy.Mount)
                {
                    try
                    {
                        // Ổ ảo Dokan đè được tệp: ép DRM ngay trên ổ ảo, ảnh gốc không bị đụng. hdiutil thì DecideStrategy đã chuyển sang giải nén.
                        var overlays = ImageMounter.CanOverlayFiles ? BuildParamOverlay(normalized, source, log) : null;
                        var hidden = ImageCleanupPaths(normalized, source, log);
                        var mountRequest = new ImageMountRequest(HideJunk: true, overlays, hidden);
                        mount = await Task.Run(() => ImageMounter.Mount(source, mountRequest, cancellationToken), cancellationToken).ConfigureAwait(false);
                        sourceFolder = mount.SourceFolder;
                        log(new LogEntry(LogLevel.Info, Loc.F("Plan.MountedVia", mount.MountPoint, ImageMounter.BackendLabel)));
                        if (mount.Backend is MountBackend.Dokan or MountBackend.Fuse)
                        {
                            log(new LogEntry(LogLevel.Info, Loc.T("Plan.MountJunkHidden")));
                        }

                        progress?.Report(tracker.UpdatePhasePercent(100));
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        log(new LogEntry(LogLevel.Warning, Loc.F("Plan.MountFailed", ex.Message)));
                        strategy = ExFatStrategy.Extract;
                    }
                }

                if (strategy == ExFatStrategy.Extract)
                {
                    staging = Path.Combine(normalized.TemporaryFolder, "exfat-" + StagingName(source.Path) + "-" + Environment.ProcessId.ToString("x"));
                    TryDeleteDirectory(staging);
                    var extractionWatch = Stopwatch.StartNew();
                    var plan = await Task.Run(() =>
                    {
                        using var image = ExFatImage.Open(source.Path);
                        var root = SourceLocator.ResolveAppRoot(image, source);
                        var extractionPlan = ExFatExtractor.CreatePlan(image, root, skipJunk: true, cancellationToken);
                        log(new LogEntry(LogLevel.Info, Loc.F("Plan.Extracting", extractionPlan.Files.Count, Formatters.Size(extractionPlan.TotalBytes), staging)));
                        ExFatExtractor.Extract(
                            image,
                            extractionPlan,
                            staging,
                            (done, total) => progress?.Report(tracker.UpdatePhasePercent(done * 100.0 / Math.Max(1, total))),
                            cancellationToken);
                        return extractionPlan;
                    }, cancellationToken).ConfigureAwait(false);

                    var skipped = plan.SkippedJunk > 0 ? Loc.F("Plan.SkippedJunk", plan.SkippedJunk) : string.Empty;
                    log(new LogEntry(LogLevel.Info, Loc.F("Plan.Extracted", Formatters.Duration(extractionWatch.Elapsed), skipped)));
                    sourceFolder = staging;
                }
            }
            else if (source.IsUfs)
            {
                // Ảnh UFS2 (.ffpkg): không hệ điều hành đích nào gắn được hệ tệp này (macOS bỏ UFS từ 10.7, Windows chưa
                // bao giờ có), nên phải trích ra thư mục tạm bằng bộ đọc riêng của công cụ.
                progress?.Report(tracker.EnterPhase(exFatPhase!));
                staging = Path.Combine(normalized.TemporaryFolder, "ffpkg-" + StagingName(source.Path) + "-" + Environment.ProcessId.ToString("x"));
                TryDeleteDirectory(staging);
                var ufsWatch = Stopwatch.StartNew();
                var ufsPlan = await Task.Run(() =>
                {
                    using var image = UfsImage.Open(source.Path);
                    var root = SourceLocator.ResolveAppRoot(image, source);
                    var extractionPlan = UfsExtractor.CreatePlan(image, root, skipJunk: true, cancellationToken);
                    log(new LogEntry(LogLevel.Info, Loc.F("Plan.Extracting", extractionPlan.Files.Count, Formatters.Size(extractionPlan.TotalBytes), staging)));
                    UfsExtractor.Extract(
                        image,
                        extractionPlan,
                        staging,
                        (done, total) => progress?.Report(tracker.UpdatePhasePercent(done * 100.0 / Math.Max(1, total))),
                        cancellationToken);
                    return extractionPlan;
                }, cancellationToken).ConfigureAwait(false);

                var ufsSkipped = ufsPlan.SkippedJunk > 0 ? Loc.F("Plan.SkippedJunk", ufsPlan.SkippedJunk) : string.Empty;
                log(new LogEntry(LogLevel.Info, Loc.F("Plan.Extracted", Formatters.Duration(ufsWatch.Elapsed), ufsSkipped)));
                sourceFolder = staging;
            }

            cancellationToken.ThrowIfCancellationRequested();

            // Bỏ tệp khỏi gói (playgo*, tàn dư AMPR emu, bộ giả lập DLC khi được chọn) và sửa param.json — làm trên một thư mục
            // gương trong thư mục tạm, KHÔNG bao giờ chạm vào thư mục nguồn, ảnh hay tệp .gp5 của người dùng.
            //
            // Gương dựng được cả trên ổ đã gắn: nó chỉ ĐỌC từ ổ rồi ghi vào thư mục tạm, nên ổ chỉ đọc như hdiutil trên macOS
            // cũng không cản trở. Nhờ vậy ảnh .exfat trên macOS không còn phải giải nén chỉ để bỏ playgo* hay sửa param.json.
            // Ổ ảo Dokan đã tự ẩn tệp và đè param.json khi gắn nên bỏ qua để khỏi làm hai lần.
            var mirrorRequired = false;
            using var mirror = mount is null or { Backend: not MountBackend.Dokan }
                ? BuildSourceMirror(normalized, source, project, sourceFolder, log, out mirrorRequired)
                : null;
            if (mirror != null)
            {
                sourceFolder = mirror.Path;
            }
            else if (mirrorRequired)
            {
                // Không dựng được gương mà vẫn phải bỏ/sửa tệp: dừng hẳn. Tạo tiếp sẽ cho ra gói còn nguyên bộ playgo*
                // trong khi nhật ký đã báo là đã bỏ — người dùng không có cách nào biết gói bị hỏng.
                throw new BuildValidationException([new ValidationError(BuildPreparer.FieldSource, Loc.T("Val.MirrorRequired"))]);
            }

            var options = CreateOptions(normalized, sourceFolder, backend, publishingToolsPath, cancellationToken);

            // Dự án GP5: thư viện đọc danh sách tệp từ chính tệp .gp5, nên bản sửa được ghi ra một tệp .gp5 mới trong thư mục tạm
            // (tệp của người dùng chỉ được đọc) và thư viện được trỏ sang bản đó.
            using var gp5Project = source.IsGp5
                ? Gp5ProjectRedirect.Create(normalized, source, project?.RootFolder, mirror?.Path, log)
                : null;
            if (gp5Project != null)
            {
                options.ProjectFilePath = gp5Project.Path;
                options.SourceFolder = System.IO.Path.GetDirectoryName(gp5Project.Path)!;
            }

            // Đĩa đầy giữa chừng: thư viện 0.6.5 cho phép tạm dừng, chờ người dùng giải phóng dung lượng rồi thử lại thay vì huỷ.
            using var diskRecovery = diskFullRetry != null
                ? ProsperoDiskSpaceRecovery.BeginScope(info =>
                {
                    log(new LogEntry(LogLevel.Warning, Loc.F("Plan.DiskFull", info.Path, info.Error.Message)));
                    var retry = diskFullRetry(info.Path);
                    log(new LogEntry(retry ? LogLevel.Info : LogLevel.Warning, Loc.T(retry ? "Plan.DiskFullRetry" : "Plan.DiskFullCancel")));
                    return retry;
                }, cancellationToken)
                : null;

            var result = await Task.Run(
                () => ProsperoPackageBuilder.Build(options, message =>
                {
                    log(LogEntry.FromLibrary(message));
                    if (tracker.TryUpdate(message, out var snapshot))
                    {
                        progress?.Report(snapshot);
                    }
                }),
                cancellationToken).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();

            log(new LogEntry(LogLevel.Info, Loc.T(normalized.ComputeSha256 ? "Plan.VerifyingSha" : "Plan.Verifying")));
            progress?.Report(tracker.EnterPhase(normalized.ComputeSha256 ? PhaseCatalog.Sha256 : PhaseCatalog.Verify));

            var verification = await Task.Run(
                () => PackageVerifier.Verify(
                    result.OutputPath,
                    normalized.ImageMode,
                    normalized.ComputeSha256,
                    cancellationToken,
                    percent =>
                    {
                        var snapshot = tracker.UpdatePhasePercent(percent);
                        if (percent >= 100)
                        {
                            snapshot = tracker.EnterPhase(PhaseCatalog.Verify);
                        }

                        progress?.Report(snapshot);
                    }),
                cancellationToken).ConfigureAwait(false);

            stopwatch.Stop();
            progress?.Report(tracker.Complete());

            var warnings = result.Warnings is { } list ? list.ToArray() : Array.Empty<string>();
            return new BuildOutcome(result.OutputPath, warnings, verification, stopwatch.Elapsed);
        }
        finally
        {
            if (mount != null)
            {
                await Task.Run(mount.Dispose).ConfigureAwait(false);
                log(new LogEntry(LogLevel.Info, Loc.T("Plan.Unmounted")));
            }

            if (staging != null)
            {
                await Task.Run(() => TryDeleteDirectory(staging)).ConfigureAwait(false);
                log(new LogEntry(LogLevel.Info, Loc.T("Plan.StagingRemoved")));
            }

            // Thư mục tạm do lần tạo gói này sinh ra: xoá nếu vẫn rỗng. Không đụng tới thư mục người dùng đã có
            // từ trước, cũng không xoá khi còn tệp bên trong (có thể là tệp của tiến trình khác hoặc còn dở).
            if (!temporaryFolderExisted)
            {
                await Task.Run(() => TryDeleteEmptyDirectory(normalized.TemporaryFolder)).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Xoá thư mục nếu tồn tại và hoàn toàn rỗng; im lặng bỏ qua trong mọi trường hợp khác.</summary>
    public static void TryDeleteEmptyDirectory(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            {
                return;
            }

            if (Directory.EnumerateFileSystemEntries(path).Any())
            {
                return;
            }

            Directory.Delete(path, recursive: false);
        }
        catch (Exception)
        {
        }
    }

    /// <summary>Gói cùng Content ID đã có trong thư mục xuất: hỏi ghi đè / giữ bản cũ / huỷ.</summary>
    internal static void ResolveOutputConflict(string outputFolder, string contentId, Func<IReadOnlyList<string>, OutputConflictChoice>? ask, Action<LogEntry> log, CancellationToken cancellationToken = default)
    {
        if (ask == null)
        {
            return;
        }

        var existing = OutputConflict.Find(outputFolder, contentId);
        if (existing.Count == 0)
        {
            return;
        }

        var choice = ask(existing);
        if (!OutputConflict.Apply(existing, choice, (from, to) =>
                log(new LogEntry(LogLevel.Info, Loc.F("Plan.OutputKept", Path.GetFileName(from), to == null ? "—" : Path.GetFileName(to))))))
        {
            log(new LogEntry(LogLevel.Warning, Loc.T("Build.UserCanceled")));
            throw new OperationCanceledException(Loc.T("Build.UserCanceled"));
        }

        if (choice == OutputConflictChoice.Overwrite)
        {
            log(new LogEntry(LogLevel.Warning, Loc.F("Plan.OutputOverwrite", string.Join(", ", existing.Select(Path.GetFileName)))));
        }
    }

    /// <summary>Thư mục giả lập sẽ bị bỏ khỏi gói nếu dọn xong không còn tệp nào (fakelib rỗng vẫn có thể làm bộ nạp PS5 rẽ nhánh).</summary>
    private static readonly string[] EmuFolders = { "fakelib", "fakelib2" };

    private static readonly HashSet<string> DlcEmuSet = new(DlcEmuInspector.Candidates(), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Dựng thư mục gương cho lượt tạo gói: bỏ những tệp không được vào gói và thay param.json bằng bản đã sửa, tất cả nằm trong
    /// thư mục tạm. Thư mục nguồn của người dùng chỉ được đọc. Trả về null khi không phải bỏ hay sửa gì.
    /// </summary>
    private static SourceMirror? BuildSourceMirror(BuildRequest request, SourceInfo source, Gp5ProjectInfo? project, string sourceFolder, Action<LogEntry> log, out bool required)
    {
        required = false;
        // Dự án GP5: thư mục ứng dụng thật nằm ở rootdir của dự án, không phải thư mục chứa tệp .gp5.
        var appFolder = source.IsGp5 ? project?.RootFolder : sourceFolder;
        if (appFolder == null || !Directory.Exists(appFolder))
        {
            return null;
        }

        var skip = FolderCleanupPaths(request, appFolder).ToList();

        // Tệp rác hệ điều hành (.DS_Store, ._*, Thumbs.db…): bỏ qua ngay trong gương. Trước đây chỉ bộ giải nén và ổ ảo Dokan
        // làm được việc này, nên ảnh gắn bằng hdiutil trên macOS buộc phải giải nén chỉ vì mấy tệp đó.
        var junk = JunkFileFinder.FindInFolder(appFolder, CancellationToken.None);
        var junkPaths = new List<string>();
        foreach (var item in junk)
        {
            var relative = System.IO.Path.GetRelativePath(appFolder, item.Path).Replace('\\', '/');
            if (!relative.StartsWith("..", StringComparison.Ordinal))
            {
                junkPaths.Add(relative);
            }
        }

        skip.AddRange(junkPaths);
        if (junkPaths.Count > 0)
        {
            log(new LogEntry(LogLevel.Info, Loc.F("Plan.JunkSkipped", junkPaths.Count)));
        }
        var replace = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var paramJson = System.IO.Path.Combine(appFolder, "sce_sys", "param.json");
        if (request.ParamPatch.Any && File.Exists(paramJson))
        {
            try
            {
                if (ParamJsonPatch.Rewrite(File.ReadAllBytes(paramJson), request.ParamPatch, out var changes) is { } patched)
                {
                    replace["sce_sys/param.json"] = patched;
                    foreach (var change in changes)
                    {
                        log(new LogEntry(LogLevel.Info, change));
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
            {
                log(new LogEntry(LogLevel.Warning, Loc.F("Plan.ParamPatchFailed", ex.Message)));
            }
        }

        if (request.KeepDlcEmu && File.Exists(System.IO.Path.Combine(appFolder, DlcEmuInspector.ConfigName)))
        {
            log(new LogEntry(LogLevel.Info, Loc.T("Plan.DlcEmuKept")));
        }

        // Thư viện tự ghi vào thư mục nó đọc trong hai trường hợp: sinh sce_sys/param.json khi thiếu, và ghi
        // sce_sys/pfs-region-hints.json khi bật phân tích shuffle. Cả hai đều phải xảy ra trong gương, không phải trong nguồn.
        var libraryWrites = !File.Exists(paramJson)
                            || (request.PfsFormat == PfsFormat.V3 && request.ShuffleAnalysis);
        // sce_sys luôn là bản sao thật: thư viện tự ghi vào đó (param.json thiếu, gợi ý vùng nén), mà liên kết thì ghi
        // xuyên thẳng vào thư mục nguồn. Thư mục này nhỏ nên sao chép không đáng kể.
        var plan = new MirrorPlan(skip, replace, new Dictionary<string, string>(), new[] { "sce_sys" });
        if (!plan.Any && !libraryWrites)
        {
            return null;
        }

        required = true;
        if (!plan.Any)
        {
            log(new LogEntry(LogLevel.Info, Loc.T("Plan.MirrorForWrites")));
        }

        LogCleanup(skip.Except(junkPaths, StringComparer.OrdinalIgnoreCase).ToList(), Array.Empty<string>(), log);
        return SourceMirror.Create(appFolder, request.TemporaryFolder, plan, log, force: true);
    }

    private static void LogCleanup(IReadOnlyCollection<string> hidden, IReadOnlyCollection<string> emptyFolders, Action<LogEntry> log)
    {
        var playgo = hidden.Where(PlayGoCleanup.IsCandidate).ToList();
        var dlc = hidden.Where(DlcEmuSet.Contains).ToList();
        var ampr = hidden.Except(playgo, StringComparer.OrdinalIgnoreCase).Except(dlc, StringComparer.OrdinalIgnoreCase).ToList();
        if (ampr.Count > 0)
        {
            log(new LogEntry(LogLevel.Info, Loc.F("Plan.AmprRemoved", string.Join(", ", ampr))));
        }

        if (playgo.Count > 0)
        {
            log(new LogEntry(LogLevel.Info, Loc.F("Plan.PlayGoRemoved", PlayGoCleanup.Describe(playgo))));
        }

        if (dlc.Count > 0)
        {
            log(new LogEntry(LogLevel.Warning, Loc.F("Plan.DlcEmuRemoved", string.Join(", ", dlc))));
        }

        if (emptyFolders.Count > 0)
        {
            log(new LogEntry(LogLevel.Info, Loc.F("Plan.EmptyFolderDropped", string.Join(", ", emptyFolders))));
        }
    }

    /// <summary>Các đường dẫn cần bỏ mà không cần đọc thư mục nguồn (tên cố định của AMPR emu và DLC emu).</summary>
    private static IReadOnlyList<string> StaticCleanupPaths(BuildRequest request)
    {
        var paths = new List<string>();
        if (request.RemoveAmprLeftovers)
        {
            paths.AddRange(AmprInspector.LeftoverCandidates());
        }

        if (!request.KeepDlcEmu)
        {
            paths.AddRange(DlcEmuInspector.Candidates());
        }

        return paths;
    }

    /// <summary>Các tệp cần bỏ khỏi gói, tính theo những gì thật sự có trong thư mục ứng dụng.</summary>
    private static IReadOnlyList<string> FolderCleanupPaths(BuildRequest request, string appFolder)
    {
        var paths = new List<string>();
        if (request.RemoveAmprLeftovers)
        {
            paths.AddRange(AmprInspector.LeftoverCandidates());
        }

        if (request.RemovePlayGoFiles)
        {
            paths.AddRange(PlayGoCleanup.ListFolder(appFolder));
        }

        if (!request.KeepDlcEmu)
        {
            paths.AddRange(DlcEmuInspector.Candidates());
        }

        return paths
            .Where(relative => File.Exists(System.IO.Path.Combine(appFolder, relative.Replace('/', System.IO.Path.DirectorySeparatorChar))))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Các đường dẫn cần ẩn trên ổ ảo khi gắn ảnh: đọc tên thật trong ảnh để ghi nhật ký chính xác; nếu mọi tệp trong
    /// fakelib/fakelib2 đều bị ẩn thì ẩn luôn cả thư mục để gói không mang một thư mục fakelib rỗng.
    /// </summary>
    private static string[]? ImageCleanupPaths(BuildRequest request, SourceInfo source, Action<LogEntry> log) =>
        ImageCleanupPaths(request, source, log, out _);

    /// <summary>Ảnh có tệp nào thật sự cần bỏ khỏi gói không (quyết định gắn hay giải nén khi ổ gắn không ẩn được tệp).</summary>
    private static bool HasImageCleanupFiles(BuildRequest request, SourceInfo source)
    {
        ImageCleanupPaths(request, source, _ => { }, out var anyPresent);
        return anyPresent;
    }

    /// <summary>
    /// Như trên, kèm <paramref name="anyPresent"/> cho biết ảnh có THẬT SỰ chứa tệp nào cần bỏ không. Danh sách trả về gồm cả
    /// tên tĩnh chưa chắc có trong ảnh (ẩn thừa một tên là vô hại), nên không dùng nó để quyết định gắn hay giải nén được.
    /// </summary>
    private static string[]? ImageCleanupPaths(BuildRequest request, SourceInfo source, Action<LogEntry> log, out bool anyPresent)
    {
        var paths = new List<string>();
        var present = new List<string>();
        var emptyFolders = new List<string>();
        try
        {
            using var image = ExFatImage.Open(source.Path);
            var appRoot = SourceLocator.ResolveAppRoot(image, source);
            var staticCandidates = StaticCleanupPaths(request).ToList();
            foreach (var candidate in staticCandidates)
            {
                paths.Add(candidate);
                if (image.Find(candidate, appRoot) is { IsDirectory: false })
                {
                    present.Add(candidate);
                }
            }

            if (request.RemovePlayGoFiles)
            {
                var playgo = PlayGoCleanup.ListImage(image, appRoot);
                paths.AddRange(playgo);
                present.AddRange(playgo);
            }

            var removed = new HashSet<string>(present, StringComparer.OrdinalIgnoreCase);
            foreach (var folder in EmuFolders)
            {
                var entry = image.Find(folder, appRoot);
                if (entry is not { IsDirectory: true })
                {
                    continue;
                }

                var children = image.Enumerate(entry).ToList();
                if (children.Count > 0 && children.All(child => !child.IsDirectory && removed.Contains(folder + "/" + child.Name)))
                {
                    paths.Add(folder);
                    emptyFolders.Add(folder);
                }
            }
        }
        catch (Exception)
        {
            // Không đọc được ảnh ở đây thì vẫn ẩn theo tên tĩnh; playgo* cần tên thật trong ảnh nên bỏ qua.
            paths.Clear();
            present.Clear();
            emptyFolders.Clear();
            paths.AddRange(StaticCleanupPaths(request));
        }

        LogCleanup(present, emptyFolders, log);
        anyPresent = present.Count > 0 || emptyFolders.Count > 0;
        return paths.Count == 0 ? null : paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>Nội dung sce_sys/param.json bên trong ảnh exFAT (null khi không đọc được).</summary>
    private static byte[]? ReadImageParamJson(SourceInfo source)
    {
        using var image = ExFatImage.Open(source.Path);
        var appRoot = SourceLocator.ResolveAppRoot(image, source);
        var paramJson = image.Find("sce_sys/param.json", appRoot);
        return paramJson is { IsDirectory: false } ? image.ReadAllBytes(paramJson) : null;
    }

    /// <summary>
    /// Bản param.json đã sửa để đè lên ổ ảo (Dokan) — ảnh gốc không bị đụng. Trả về null khi không cần đổi (thiếu tệp hoặc
    /// mọi giá trị đã đúng); lỗi đọc chỉ ghi cảnh báo và không chặn việc gắn.
    /// </summary>
    private static IReadOnlyDictionary<string, byte[]>? BuildParamOverlay(BuildRequest request, SourceInfo source, Action<LogEntry> log)
    {
        if (!request.ParamPatch.Any)
        {
            return null;
        }

        try
        {
            if (ReadImageParamJson(source) is not { } paramJson)
            {
                return null;
            }

            var rewritten = ParamJsonPatch.Rewrite(paramJson, request.ParamPatch, out var changes);
            if (rewritten == null)
            {
                return null;
            }

            log(new LogEntry(LogLevel.Info, Loc.F("Plan.MountParamOverlay", string.Join(" · ", changes))));
            return new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase) { ["sce_sys/param.json"] = rewritten };
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException)
        {
            log(new LogEntry(LogLevel.Warning, Loc.F("Plan.ParamPatchFailed", ex.Message)));
            return null;
        }
    }

    /// <summary>
    /// Chọn cách xử lý ảnh exFAT: gắn không sao chép (hdiutil trên macOS, ổ ảo Dokan trên Windows) hoặc giải nén ra thư mục tạm.
    /// hdiutil chỉ gắn ảnh .exfat thuần và không sửa/ẩn được gì, nên ảnh có tệp rác hoặc cần ép DRM phải giải nén; ổ ảo Dokan
    /// ẩn tệp rác và đè param.json ngay trên ổ nên gắn được cả container .ffpfsc.
    /// </summary>
    public static ExFatStrategy DecideStrategy(BuildRequest request, SourceInfo source, Action<LogEntry>? log)
    {
        if (request.ExFat == ExFatStrategy.Extract)
        {
            return ExFatStrategy.Extract;
        }

        if (!ImageMounter.CanMount(source))
        {
            if (source.IsPfsContainer)
            {
                // Container .ffpfsc: hdiutil không gắn được → giải nén trực tiếp qua lớp PFS của thư viện.
                log?.Invoke(new LogEntry(LogLevel.Info, Loc.T("Plan.PfsContainerExtract")));
            }
            else if (ImageMounter.DokanMissingOnWindows)
            {
                log?.Invoke(new LogEntry(LogLevel.Info, Loc.T("Plan.MountNeedsDokan")));
            }

            return ExFatStrategy.Extract;
        }

        if (request.ExFat == ExFatStrategy.Mount)
        {
            return ExFatStrategy.Mount;
        }

        return ExFatStrategy.Mount;
    }

    public static ProsperoBuildOptions CreateOptions(
        BuildRequest request,
        string sourceFolder,
        KrakenBackendKind backend,
        string? publishingToolsPath,
        CancellationToken cancellationToken)
    {
        var titleId = ContentIdHelper.TitleIdOf(request.ContentId)
                      ?? throw new BuildValidationException([new ValidationError(BuildPreparer.FieldContentId, Loc.T("Val.ContentIdInvalid"))]);

        return new ProsperoBuildOptions
        {
            SourceFolder = sourceFolder,
            OutputFolder = request.OutputFolder,
            TemporaryDirectory = request.TemporaryFolder,
            ContentId = request.ContentId,
            PrimaryId = request.ContentId,
            TitleId = titleId,
            Title = request.Title,
            Version = request.Version,
            Passcode = request.Passcode,
            Mode = request.Kind switch
            {
                PackageKind.Homebrew => ProsperoPackageMode.Homebrew,
                PackageKind.DlcWithData => ProsperoPackageMode.AdditionalContentData,
                _ => ProsperoPackageMode.Application,
            },
            OutputFormat = ProsperoOutputFormat.DebugImage,
            UsePublisherPprNaps = true,
            KrakenCompressionLevel = request.KrakenLevel,
            KrakenMaxDegreeOfParallelism = request.Threads,
            KrakenBackend = backend switch
            {
                KrakenBackendKind.PublishingTools => ProsperoKrakenBackend.PublishingToolsRequired,
                KrakenBackendKind.Uncompressed => ProsperoKrakenBackend.Uncompressed,
                _ => ProsperoKrakenBackend.BuiltIn,
            },
            PublishingToolsLibraryPath = backend == KrakenBackendKind.PublishingTools ? publishingToolsPath : null,
            PlayGoChunkCount = request.PlayGoChunks,
            SdkVersionOverride = request.SdkMajorOverride is { } major ? SdkVersions.Get(major)?.ExecutableVersion : null,
            PublisherImageMode = request.ImageMode == OuterImageMode.Native
                ? ProsperoPublisherImageMode.Native
                : ProsperoPublisherImageMode.PlaintextNoAuth,
            DeterministicBuild = request.Deterministic,
            GenerateParamJsonIfMissing = true,
            CancellationToken = cancellationToken,
            PfsCompressionFormat = request.PfsFormat == PfsFormat.V3 ? ProsperoPfsCompressionFormat.Version3 : ProsperoPfsCompressionFormat.Version2,
            KrakenCompressionBlockSize = Math.Clamp(request.KrakenBlockKiB, BuildRequest.MinKrakenBlockKiB, BuildRequest.MaxKrakenBlockKiB) * 1024,
            PreCompressionShufflePattern = MapShuffle(request.ShufflePattern),
            EnableShufflePatternAnalysis = request.PfsFormat == PfsFormat.V3 && request.ShuffleAnalysis,
            ShufflePredictionCompressionLevel = request.ShufflePredictionLevel,
            SkipPfsInputDataAllowedCheck = request.SkipPfsInputCheck,
            EnableOuterBlockCoalescing = request.LayoutOptimization,
            EnableRelocationAlignmentAdjustment = request.LayoutOptimization,
            SourceMode = request.SourceMode switch
            {
                SourceMode.Folder => ProsperoSourceMode.Folder,
                SourceMode.Gp5Project => ProsperoSourceMode.Gp5Project,
                _ => ProsperoSourceMode.Automatic,
            },
            ProjectFilePath = request.SourceMode == SourceMode.Gp5Project ? request.ProjectFilePath : null,
        };
    }

    private static ProsperoPfsShufflePattern MapShuffle(ShufflePatternKind pattern) =>
        Enum.TryParse<ProsperoPfsShufflePattern>(pattern.ToString(), out var value) ? value : ProsperoPfsShufflePattern.None;

    /// <summary>Mô tả ngắn cấu hình PFS/shuffle/bố cục để ghi nhật ký và hiển thị.</summary>
    public static string DescribeCompressionProfile(BuildRequest request)
    {
        var shuffle = request.PfsFormat == PfsFormat.V3
            ? BuildPresets.ShufflePatternLabel(request.ShufflePattern) + (request.ShuffleAnalysis ? Loc.T("Plan.ShuffleAnalysisSuffix") : string.Empty)
            : "—";
        return Loc.F("Plan.Pfs", request.PfsFormat == PfsFormat.V3 ? "v3" : "v2", request.KrakenBlockKiB, shuffle, Loc.T(request.LayoutOptimization ? "Common.On" : "Common.Off"));
    }

    private static void LogPlan(BuildRequest request, SourceInfo source, Gp5ProjectInfo? project, KrakenBackendKind backend, string? publishingToolsPath, Action<LogEntry> log)
    {
        log(new LogEntry(LogLevel.Success, Loc.T("Plan.Start")));
        log(new LogEntry(LogLevel.Info, source.IsGp5
            ? Loc.F("Plan.SourceGp5", source.Path, project?.Layout ?? "—", project?.RootFolder ?? "—")
            : source.IsPfsContainer
                ? Loc.F("Plan.SourcePfs", source.Path, source.AppRootInImage)
                : source.IsExFat
                    ? Loc.F("Plan.SourceExFat", source.Path, source.AppRootInImage)
                    : Loc.F("Plan.Source", source.Path)));
        log(new LogEntry(LogLevel.Info, Loc.F("Plan.Output", request.OutputFolder)));
        log(new LogEntry(LogLevel.Info, Loc.F("Plan.Temp", request.TemporaryFolder)));
        log(new LogEntry(LogLevel.Info, Loc.F("Plan.ContentId", request.ContentId)));
        log(new LogEntry(LogLevel.Info, Loc.F("Plan.Version", request.Version)));
        log(new LogEntry(LogLevel.Info, Loc.F("Plan.Kind", request.Kind, request.ImageMode)));

        var workers = request.Threads == 0 ? Math.Max(1, Environment.ProcessorCount) : request.Threads;
        switch (backend)
        {
            case KrakenBackendKind.Uncompressed:
                log(new LogEntry(LogLevel.Info, Loc.T("Plan.KrakenNone")));
                break;
            case KrakenBackendKind.PublishingTools:
                log(new LogEntry(LogLevel.Info, Loc.F("Plan.KrakenNative", request.KrakenLevel, BuildPresets.KrakenLevelName(request.KrakenLevel), workers)));
                log(new LogEntry(LogLevel.Info, Loc.F("Plan.Dll", publishingToolsPath ?? Loc.T("Plan.DllAuto"))));
                break;
            default:
                log(new LogEntry(LogLevel.Info, Loc.F("Plan.KrakenBuiltIn", request.KrakenLevel, BuildPresets.KrakenLevelName(request.KrakenLevel), workers)));
                break;
        }

        log(new LogEntry(LogLevel.Info, DescribeCompressionProfile(request)));
        log(new LogEntry(LogLevel.Info, Loc.T(request.ForceStandardDrm ? "Plan.DrmPolicyOn" : "Plan.DrmPolicyOff")));
        if (request.ClearVersionFileUri)
        {
            log(new LogEntry(LogLevel.Info, Loc.T("Plan.VersionUriPolicy")));
        }

        if (request.ClearPlayGoAttributes)
        {
            log(new LogEntry(LogLevel.Warning, Loc.T("Plan.Attribute3Policy")));
        }

        if (request.PfsFormat == PfsFormat.V3)
        {
            // Theo tác giả thư viện: PFS v3 cần firmware PS5 7.00+, tối ưu shuffle chỉ phát huy đầy đủ ở Kraken mức 9.
            log(new LogEntry(LogLevel.Warning, Loc.T("Plan.PfsV3Firmware")));
            if (request.ShuffleAnalysis && request.KrakenLevel < BuildRequest.MaxKrakenLevel)
            {
                log(new LogEntry(LogLevel.Warning, Loc.F("Plan.ShuffleLevel", request.KrakenLevel)));
            }
        }
        log(new LogEntry(LogLevel.Info, Loc.F("Plan.PlayGo", request.PlayGoChunks)));

        if (request.SdkMajorOverride is { } major && SdkVersions.Get(major) is { } generation)
        {
            log(new LogEntry(LogLevel.Info, Loc.F("Plan.SdkOverride", generation.Release, generation.ExecutableVersion.ToString("X16"))));
        }
        else
        {
            log(new LogEntry(LogLevel.Info, Loc.T("Plan.SdkKeep")));
        }

        log(new LogEntry(LogLevel.Info, Loc.T(request.Deterministic ? "Plan.DeterministicOn" : "Plan.DeterministicOff")));
        log(new LogEntry(LogLevel.Info, Loc.T(request.ComputeSha256 ? "Plan.ShaOn" : "Plan.ShaOff")));
    }

    private static string StagingName(string imagePath)
    {
        var name = Path.GetFileNameWithoutExtension(imagePath);
        var safe = new string(name.Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_').Take(32).ToArray());
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(imagePath))))[..8];
        return (safe.Length > 0 ? safe + "-" : string.Empty) + hash;
    }

    /// <summary>Xoá thư mục (có thử lại) — dùng cho thư mục giải nén tạm.</summary>
    public static void TryDeleteDirectory(string path)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (!Directory.Exists(path))
                {
                    return;
                }

                Directory.Delete(path, recursive: true);
                return;
            }
            catch (Exception)
            {
                Thread.Sleep(300);
            }
        }
    }
}
