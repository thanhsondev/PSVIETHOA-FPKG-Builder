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
        Directory.CreateDirectory(normalized.TemporaryFolder);

        // Thư viện luôn ghi đè tệp trùng tên — hỏi trước để người dùng giữ được bản cũ.
        // Chạy trên luồng nền: người gọi chờ đồng bộ hộp thoại trên luồng giao diện, nếu chỗ này đang ở luồng giao diện
        // thì hai bên chờ nhau và ứng dụng treo cứng.
        await Task.Run(() => ResolveOutputConflict(normalized.OutputFolder, normalized.ContentId, onOutputConflict, log, cancellationToken, normalized.SdkReferencePackage), cancellationToken)
            .ConfigureAwait(false);

        // SDK Sony (chuẩn mới, mặc định bật): chỉ gói ứng dụng lớp ngoài không mã hoá dựng từ thư mục/ảnh. Không dùng được
        // (thiếu bộ công cụ / Wine / Rosetta, hoặc loại gói khác) thì ghi lý do và tạo bằng engine tích hợp như trước.
        SonySdkRuntime? sdkRuntime = null;
        string? sdkUnavailable = null;
        var sdkNotApplicable = false;
        if (normalized.UseSonySdk)
        {
            if (SonySdkBuilder.Supports(normalized, source, out var unsupported))
            {
                sdkRuntime = SonySdkToolchain.Resolve(out sdkUnavailable);
            }
            else
            {
                sdkUnavailable = unsupported;
                sdkNotApplicable = true;
            }

            // Bản vá từ thư mục: nguồn có thể chỉ là thư mục update không có keystone — keystone lấy từ gói gốc, kiểm tra khi dựng GP5.
            if (sdkRuntime != null && !PatchMergeApplies(normalized, source))
            {
                await Task.Run(() => RequireKeystone(source), cancellationToken).ConfigureAwait(false);
            }
        }

        normalized.SonySdkActive = sdkRuntime != null;
        var backend = BuildPreparer.ResolveBackend(normalized, out var publishingToolsPath);
        var strategy = source.IsExFat
            ? await Task.Run(() => DecideStrategy(normalized, source, log), cancellationToken).ConfigureAwait(false)
            : ExFatStrategy.Auto;
        var exFatPhase = source.IsExFat
            ? strategy == ExFatStrategy.Mount ? PhaseCatalog.Mount : PhaseCatalog.Extract
            : source.IsUfs ? PhaseCatalog.Extract : null;
        // Bản vá từ thư mục: so đường dẫn tệp của nguồn với gói gốc. Tệp gói gốc có mà nguồn không có (nguồn là "thư mục update") được
        // lấy từ thư mục game gốc, hoặc giải nén riêng những tệp đó từ gói gốc vào thư mục tạm (dùng chung pha "giải nén").
        PatchMerge? patchMerge = null;
        if (sdkRuntime != null && PatchMergeApplies(normalized, source))
        {
            patchMerge = await Task.Run(() => PlanPatchMerge(normalized, source.Path, cancellationToken), cancellationToken).ConfigureAwait(false);
            if (patchMerge is { FromPackage: true })
            {
                exFatPhase ??= PhaseCatalog.Extract;
            }
        }

        var stopwatch = Stopwatch.StartNew();
        var tracker = new ProgressTracker(stopwatch, PhaseCatalog.Sequence(normalized.ComputeSha256, exFatPhase, normalized.FullVerify, sdkRuntime != null));
        LogPlan(normalized, source, project, backend, publishingToolsPath, sdkRuntime != null, log);
        if (sdkRuntime != null)
        {
            log(new LogEntry(LogLevel.Success, Loc.F("Sdk.PlanOn", SonySdkToolchain.Profile, sdkRuntime.Directory)));
            log(normalized.SdkCompressionLevel is { } sdkLevel
                ? new LogEntry(LogLevel.Warning, Loc.F("Sdk.PlanLevelCustom", sdkLevel))
                : new LogEntry(LogLevel.Info, Loc.T("Sdk.PlanLevel")));
        }
        else if (!string.IsNullOrWhiteSpace(normalized.SdkReferencePackage))
        {
            // Bản vá chỉ Publishing Tools làm được: không âm thầm chuyển sang engine tích hợp rồi cho ra gói đầy đủ.
            throw new InvalidOperationException(Loc.F("Patch.SdkRequired", normalized.UseSonySdk ? sdkUnavailable ?? "?" : Loc.T("Patch.NeedsSdk")));
        }
        else if (normalized.UseSonySdk)
        {
            // Loại gói SDK không làm (DLC, homebrew, ảnh Native, dự án GP5) là lựa chọn của người dùng → thông tin; thiếu bộ công cụ → cảnh báo.
            log(new LogEntry(sdkNotApplicable ? LogLevel.Info : LogLevel.Warning, Loc.F("Sdk.PlanFallback", sdkUnavailable ?? "?")));
        }
        else
        {
            log(new LogEntry(LogLevel.Info, Loc.T("Sdk.PlanOff")));
        }
        progress?.Report(tracker.Current);

        using var sleepGuard = normalized.PreventSleep ? SleepInhibitor.TryAcquire() : null;
        if (sleepGuard != null)
        {
            log(new LogEntry(LogLevel.Info, Loc.F("Plan.SleepGuard", sleepGuard.Mechanism)));
        }

        ImageMount? mount = null;
        string? staging = null;
        string? patchBase = null;
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
                        if (mount.Backend == MountBackend.Dokan)
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

            // SDK Sony: GP5 phẳng trỏ thẳng vào từng tệp nguồn (như build-from-folder.ps1 với --absolute-paths), nên không cần
            // thư mục gương — tệp cần bỏ chỉ việc không liệt kê, param.json đã sửa trỏ sang bản trong thư mục tạm. Ổ ảo Dokan đã
            // tự ẩn tệp và đè param.json khi gắn nên dùng nguyên như script gốc.
            if (sdkRuntime != null && patchMerge != null)
            {
                // Nguồn thiếu tệp so với gói gốc ("thư mục update"): ghép nguồn lên nội dung gói gốc (thư mục game gốc, hoặc các tệp thiếu giải nén từ gói gốc).
                var updateFolder = sourceFolder;
                string baseFolder;
                if (!string.IsNullOrWhiteSpace(normalized.SdkPatchBaseFolder))
                {
                    baseFolder = Path.GetFullPath(normalized.SdkPatchBaseFolder);
                    if (!File.Exists(Path.Combine(baseFolder, "sce_sys", "param.json")))
                    {
                        throw new InvalidDataException(Loc.F("Patch.BaseFolderInvalid", baseFolder));
                    }

                    if (PathsOverlap(baseFolder, updateFolder))
                    {
                        throw new InvalidDataException(Loc.F("Patch.BaseFolderIsSource", baseFolder));
                    }

                    log(new LogEntry(LogLevel.Info, Loc.F("Patch.BaseFolderUsed", baseFolder)));
                }
                else
                {
                    patchBase = Path.Combine(normalized.TemporaryFolder, "patch-base-" + StagingName(normalized.SdkReferencePackage!) + "-" + Environment.ProcessId.ToString("x"));
                    TryDeleteDirectory(patchBase);
                    baseFolder = patchBase;
                    var showProgress = exFatPhase == PhaseCatalog.Extract && !source.IsExFat && !source.IsUfs;
                    if (showProgress)
                    {
                        progress?.Report(tracker.EnterPhase(PhaseCatalog.Extract));
                    }

                    await Task.Run(
                        () => ExtractPatchBase(normalized, patchMerge, patchBase, log, showProgress ? percent => progress?.Report(tracker.UpdatePhasePercent(percent)) : null, cancellationToken),
                        cancellationToken).ConfigureAwait(false);
                }

                var overlayPlan = await Task.Run(() => PlanSonySdkSource(normalized, baseFolder, log, updateFolder), cancellationToken).ConfigureAwait(false) with
                {
                    OverlayRoot = updateFolder,
                    OverlayBasePaths = patchMerge.FromPackage ? patchMerge.BasePaths : null,
                };
                log(new LogEntry(LogLevel.Info, Loc.F("Patch.OverlayMode", updateFolder)));
                return await BuildWithSonySdkAsync(normalized, sdkRuntime, baseFolder, overlayPlan, tracker, stopwatch, log, progress, cancellationToken).ConfigureAwait(false);
            }

            if (sdkRuntime != null)
            {
                var plan = mount is { Backend: MountBackend.Dokan }
                    ? SonySdkSourcePlan.Pure with { ParamPatch = normalized.ParamPatch, PlayGo = await Task.Run(() => ReadPlayGoStructure(sourceFolder, normalized, log), cancellationToken).ConfigureAwait(false) }
                    : await Task.Run(() => PlanSonySdkSource(normalized, sourceFolder, log), cancellationToken).ConfigureAwait(false);
                return await BuildWithSonySdkAsync(normalized, sdkRuntime, sourceFolder, plan, tracker, stopwatch, log, progress, cancellationToken).ConfigureAwait(false);
            }

            // Bỏ tệp khỏi gói (playgo*, tàn dư AMPR emu, bộ giả lập DLC khi được chọn) và sửa param.json — làm trên một thư mục
            // gương trong thư mục tạm, KHÔNG bao giờ chạm vào thư mục nguồn, ảnh hay tệp .gp5 của người dùng.
            //
            // Gương dựng được cả trên ổ đã gắn: nó chỉ ĐỌC từ ổ rồi ghi vào thư mục tạm, nên ổ chỉ đọc như hdiutil trên macOS
            // cũng không cản trở. Nhờ vậy ảnh .exfat trên macOS không còn phải giải nén chỉ để bỏ playgo* hay sửa param.json.
            // Ổ ảo Dokan đã tự ẩn tệp và đè param.json khi gắn nên bỏ qua để khỏi làm hai lần.
            var mirrorRequired = false;
            using var mirror = mount is null or { Backend: not MountBackend.Dokan }
                ? BuildSourceMirror(normalized, source, project, sourceFolder, ownsFolder: staging != null, log, out mirrorRequired)
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
            var warnings = result.Warnings is { } list ? list.ToArray() : Array.Empty<string>();
            return await VerifyOutputAsync(normalized, result.OutputPath, warnings, tracker, stopwatch, log, progress, cancellationToken).ConfigureAwait(false);
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

            if (patchBase != null)
            {
                await Task.Run(() => TryDeleteDirectory(patchBase)).ConfigureAwait(false);
                log(new LogEntry(LogLevel.Info, Loc.T("Patch.BaseRemoved")));
            }
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Bản vá tự ghép với gói gốc: có gói gốc, nguồn là thư mục thường và người dùng không ép "nguồn là bản đầy đủ".</summary>
    private static bool PatchMergeApplies(BuildRequest request, SourceInfo source) =>
        !string.IsNullOrWhiteSpace(request.SdkReferencePackage) && !request.SdkPatchExactSource && source.Kind == SourceKind.Folder;

    /// <summary>Kết quả so nguồn với gói gốc: những tệp phải lấy lại từ gói gốc (<see cref="FromPackage"/>) hoặc từ thư mục game gốc.</summary>
    private sealed record PatchMerge(bool FromPackage, IReadOnlySet<string> MissingPaths, int MissingFiles, long MissingBytes, IReadOnlyCollection<string>? BasePaths = null);

    /// <summary>
    /// So đường dẫn tệp (không phân biệt hoa thường) của thư mục nguồn với danh sách tệp trong gói gốc. Null = nguồn đã đầy đủ (mọi tệp
    /// thật của gói gốc đều có, có param.json) → tạo bản vá thẳng từ nguồn như bộ công cụ gốc. Tệp sce_sys do SDK tự sinh (playgo-*.dat…)
    /// không tính là "thiếu", nhưng khi đã phải lấy tệp từ gói gốc thì lấy cả chúng để giữ đúng cấu trúc PlayGo của gói gốc.
    /// </summary>
    private static PatchMerge? PlanPatchMerge(BuildRequest request, string sourceFolder, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(request.SdkPatchBaseFolder))
        {
            return new PatchMerge(false, new HashSet<string>(), 0, 0);
        }

        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(sourceFolder, "*", SearchOption.AllDirectories))
        {
            present.Add(Path.GetRelativePath(sourceFolder, file).Replace('\\', '/'));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { SonySdkProject.KeystonePath };
        var missing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var basePaths = new List<string>();
        var significant = 0;
        long bytes = 0;
        using (var reader = PackageReader.Open(Path.GetFullPath(request.SdkReferencePackage!), request.Passcode, request.TemporaryFolder, cancellationToken))
        {
            foreach (var entry in reader.Entries)
            {
                var relative = entry.Path.Replace('\\', '/').TrimStart('/');
                if (entry.IsDirectory)
                {
                    continue;
                }

                basePaths.Add(relative);
                if (present.Contains(relative))
                {
                    continue;
                }

                missing.Add(relative);
                bytes += entry.Size;
                // "Thiếu thật" = dữ liệu game. sce_sys (SDK tự sinh bảng PlayGo, PNG khôi phục từ .dds…) và tệp giữ chỗ ngôn ngữ do công cụ
                // thêm lúc tạo gói gốc không có trong bản dump đầy đủ nên không tính.
                if (!relative.StartsWith("sce_sys/", StringComparison.OrdinalIgnoreCase) &&
                    !relative.StartsWith(SonySdkPlayGo.LanguagePayloadFolder + "/", StringComparison.OrdinalIgnoreCase) &&
                    SonySdkProject.SkipReason(relative, keep) == null)
                {
                    significant++;
                }
            }
        }

        var hasParam = present.Contains("sce_sys/param.json");
        return significant == 0 && hasParam ? null : new PatchMerge(true, missing, significant, bytes, basePaths);
    }

    private static bool PathsOverlap(string first, string second)
    {
        static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)) + Path.DirectorySeparatorChar;
        var a = Normalize(first);
        var b = Normalize(second);
        var comparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        return a.StartsWith(b, comparison) || b.StartsWith(a, comparison);
    }

    /// <summary>
    /// Giải nén từ gói gốc ĐÚNG những tệp nguồn không có (+ sce_sys trong CNT) vào <paramref name="target"/> trong thư mục tạm để ghép với
    /// nguồn. Chỗ trống cần = tổng dung lượng các tệp thiếu; gói gốc chỉ được đọc.
    /// </summary>
    private static void ExtractPatchBase(BuildRequest request, PatchMerge merge, string target, Action<LogEntry> log, Action<double>? percent, CancellationToken cancellationToken)
    {
        var package = Path.GetFullPath(request.SdkReferencePackage!);
        var watch = Stopwatch.StartNew();
        long? free = null;
        try
        {
            free = new DriveInfo(DiskSpaceAdvisor.ResolveMountPoint(request.TemporaryFolder) ?? Path.GetPathRoot(Path.GetFullPath(request.TemporaryFolder)) ?? request.TemporaryFolder).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
        }

        if (free is { } available && available < merge.MissingBytes + (256L << 20))
        {
            throw new IOException(Loc.F("Patch.BaseNoSpace", Formatters.Size(merge.MissingBytes), request.TemporaryFolder, Formatters.Size(available)));
        }

        log(new LogEntry(LogLevel.Info, Loc.F("Patch.BaseExtracting", merge.MissingFiles, Formatters.Size(merge.MissingBytes), target)));
        Directory.CreateDirectory(target);
        using (var reader = PackageReader.Open(package, request.Passcode, request.TemporaryFolder, cancellationToken))
        {
            var wanted = reader.Entries.Where(entry => !entry.IsDirectory && merge.MissingPaths.Contains(entry.Path.Replace('\\', '/').TrimStart('/'))).ToList();
            if (wanted.Count > 0)
            {
                var result = reader.Extract(
                    wanted,
                    target,
                    percent == null ? null : new SyncProgress<ExtractProgress>(p => percent(p.DoneBytes * 100.0 / Math.Max(1, p.TotalBytes))),
                    cancellationToken);
                foreach (var warning in result.Warnings.Take(5))
                {
                    log(new LogEntry(LogLevel.Warning, warning));
                }
            }
        }

        PackageReader.ExportSceSys(package, target, request.Passcode, cancellationToken);
        log(new LogEntry(LogLevel.Info, Loc.F("Patch.BaseExtracted", Formatters.Duration(watch.Elapsed))));
    }

    /// <summary>IProgress gọi thẳng (không qua SynchronizationContext như Progress&lt;T&gt;).</summary>
    private sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }

    /// <summary>
    /// Kế hoạch bỏ tệp cho GP5 của SDK Sony — cùng quy tắc với thư mục gương của engine (bộ giả lập DLC khi bỏ, tệp rác), trừ
    /// bộ playgo*, ampr_emu.index và hai module giả lập trong fakelib vì script gốc (fixdss3) tự loại; param.json được script
    /// chuẩn hoá (DRM standard) rồi công cụ áp thêm các sửa đổi tuỳ chọn. Thư mục nguồn chỉ được đọc.
    /// </summary>
    private static SonySdkSourcePlan PlanSonySdkSource(BuildRequest request, string appFolder, Action<LogEntry> log, string? overlayFolder = null)
    {
        // Bản vá ghép thư mục update: tệp cần bỏ có thể nằm ở bên nào cũng được; bảng PlayGo lấy ở bên nào còn playgo-chunk.dat (ưu tiên gói gốc).
        var cleanup = overlayFolder == null
            ? FolderCleanupPaths(request, appFolder)
            : FolderCleanupPaths(request, appFolder).Concat(FolderCleanupPaths(request, overlayFolder)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (overlayFolder != null && !File.Exists(Path.Combine(appFolder, "sce_sys", "playgo-chunk.dat")) && File.Exists(Path.Combine(overlayFolder, "sce_sys", "playgo-chunk.dat")))
        {
            appFolder = overlayFolder;
        }

        var skip = new HashSet<string>(
            cleanup
                .Where(path => !PlayGoCleanup.IsCandidate(path))
                .Where(path => SonySdkProject.SkipReason(path, new HashSet<string>(StringComparer.OrdinalIgnoreCase)) == null),
            StringComparer.OrdinalIgnoreCase);
        LogCleanup(skip.ToList(), Array.Empty<string>(), log);

        var playgo = PlayGoCleanup.ListFolder(appFolder);
        var structure = ReadPlayGoStructure(appFolder, request, log);
        if (playgo.Count > 0)
        {
            // Bảng playgo* của nguồn không bao giờ đưa thẳng vào gói (SDK từ chối); nhiều chunk thì SDK tạo lại theo cấu trúc giữ trong GP5.
            log(new LogEntry(LogLevel.Info, structure == null
                ? Loc.F("Sdk.PlayGoRegenerated", PlayGoCleanup.Describe(playgo))
                : structure.SupportedLanguageMask == 0
                    ? Loc.F("Sdk.PlayGoRebuiltFallback", PlayGoCleanup.Describe(playgo))
                    : Loc.F("Sdk.PlayGoRebuilt", PlayGoCleanup.Describe(playgo), structure.Chunks.Count, structure.Scenarios.Count)));
        }

        return new SonySdkSourcePlan(skip, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), SkipJunk: true, request.ParamPatch, structure);
    }

    /// <summary>
    /// Cấu trúc PlayGo của gói gốc để giữ lại trong GP5 (nhiều chunk = các gói ngôn ngữ thoại). Chỉ 1 chunk / 1 kịch bản thì
    /// null (script gốc đã đúng). Tệp hỏng thì cảnh báo và dùng 1 chunk. Nguồn không còn bảng (hoặc bảng hỏng) và người dùng bật
    /// "PlayGo dự phòng" thì dùng <see cref="SonySdkPlayGo.Fallback"/> (N chunk như engine tích hợp, số kịch bản của nguồn).
    /// </summary>
    private static PlayGoStructure? ReadPlayGoStructure(string appFolder, BuildRequest request, Action<LogEntry> log)
    {
        var sceSys = Path.Combine(appFolder, "sce_sys");
        var structure = SonySdkPlayGo.TryRead(sceSys, out var problem);
        if (structure == null)
        {
            if (problem != null)
            {
                log(new LogEntry(LogLevel.Warning, Loc.F("Sdk.PlayGoStructureInvalid", problem)));
            }

            if (request.SdkPlayGoFallback)
            {
                // Bố cục của script fix6: 100 chunk, mỗi ngôn ngữ trong playgo-scenario.json một chunk có tệp giữ chỗ 1 MiB (máy báo
                // ngôn ngữ "đã cài"), kịch bản/tiêu đề của nguồn, mọi chunk initial.
                var fallback = SonySdkPlayGo.ScriptFallback(sceSys, SonySdkProject.DefaultLanguageOf(Path.Combine(sceSys, "param.json")), out var scenarioWarning, request.PlayGoChunks);
                if (scenarioWarning != null)
                {
                    log(new LogEntry(LogLevel.Warning, Loc.F("Sdk.PlayGoScenarioInvalid", scenarioWarning)));
                }

                log(new LogEntry(LogLevel.Info, Loc.F("Sdk.PlayGoScriptFallback", fallback.Chunks.Count, fallback.LanguagePayloads.Count, fallback.SupportedLanguagesText, fallback.Scenarios.Count)));
                return fallback;
            }

            return null;
        }

        if (structure.IsTrivial)
        {
            return null;
        }

        var languages = string.Join(", ", SonySdkPlayGo.Codes(structure.SupportedLanguageMask));
        var languageChunks = structure.Chunks.Count(chunk => chunk.LanguageMask != structure.SupportedLanguageMask);
        log(new LogEntry(LogLevel.Info, Loc.F("Sdk.PlayGoStructure", structure.Chunks.Count, structure.Scenarios.Count, languageChunks, languages, structure.FileChunks.Count)));
        return structure;
    }

    /// <summary>Bản param.json đã áp các sửa đổi của lượt này (DRM, versionFileUri, attribute3, hạ firmware), null khi không đổi gì.</summary>
    private static byte[]? PatchedParamJson(BuildRequest request, string appFolder, Action<LogEntry> log)
    {
        var paramJson = Path.Combine(appFolder, "sce_sys", "param.json");
        if (!(request.ParamPatch.Any || request.ForceStandardDrm) || !File.Exists(paramJson))
        {
            return null;
        }

        try
        {
            var original = File.ReadAllBytes(paramJson);
            LogDrmOverride(request, original, log);
            if (request.ParamPatch.Any && ParamJsonPatch.Rewrite(original, request.ParamPatch, out var changes) is { } patched)
            {
                foreach (var change in changes)
                {
                    log(new LogEntry(LogLevel.Info, change));
                }

                return patched;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            log(new LogEntry(LogLevel.Warning, Loc.F("Plan.ParamPatchFailed", ex.Message)));
        }

        return null;
    }

    /// <summary>Tạo gói bằng SDK Sony từ thư mục ứng dụng (thư mục nguồn / bản giải nén / ổ gắn), rồi kiểm tra như thường lệ.</summary>
    private static async Task<BuildOutcome> BuildWithSonySdkAsync(
        BuildRequest request,
        SonySdkRuntime runtime,
        string appFolder,
        SonySdkSourcePlan plan,
        ProgressTracker tracker,
        Stopwatch stopwatch,
        Action<LogEntry> log,
        IProgress<BuildProgress>? progress,
        CancellationToken cancellationToken)
    {
        var result = await SonySdkBuilder.BuildAsync(
            runtime,
            request,
            appFolder,
            plan,
            log,
            (phase, percent, detail) => progress?.Report(tracker.Report(phase, percent, detail)),
            cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (result.RemasteredPath != null)
        {
            // Bản vá: gói delta không có header FIH; kiểm tra cấu trúc trên gói đầy đủ đi kèm (.remastered.pkg — như GUI của fix8). Kiểm tra
            // nội dung của LibProsperoPkg không áp dụng được: ảnh của gói remastered tham chiếu dữ liệu nằm trong gói gốc.
            log(new LogEntry(LogLevel.Info, Loc.T("Patch.Verifying")));
            progress?.Report(tracker.EnterPhase(PhaseCatalog.Verify));
            var verification = await Task.Run(() => PackageVerifier.Verify(result.RemasteredPath, request.ImageMode, false, cancellationToken, null), cancellationToken).ConfigureAwait(false);
            LogSdkVersionChange(appFolder, result.RemasteredPath, request.Passcode, log);

            // Người dùng chọn tệp xuất ra: Publishing Tools luôn ghi cả hai, tệp không cần bị xoá SAU khi đã kiểm tra cấu trúc. Hai tệp chuyển
            // đổi qua lại được bằng img_convert (remastered ⇄ patch + gói base), nên không mất gì.
            var keptPath = result.OutputPath;
            if (request.SdkPatchOutput == SdkPatchOutput.UpdateOnly)
            {
                TryDeleteFile(result.RemasteredPath);
                log(new LogEntry(LogLevel.Info, Loc.F("Patch.RemovedFull", Path.GetFileName(result.RemasteredPath))));
            }
            else if (request.SdkPatchOutput == SdkPatchOutput.FullOnly)
            {
                TryDeleteFile(result.OutputPath);
                keptPath = result.RemasteredPath;
                log(new LogEntry(LogLevel.Info, Loc.F("Patch.RemovedUpdate", Path.GetFileName(result.OutputPath))));
            }

            if (request.SdkPatchOutput != SdkPatchOutput.FullOnly)
            {
                log(new LogEntry(LogLevel.Warning, Loc.T("Patch.InstallNote")));
            }

            stopwatch.Stop();
            progress?.Report(tracker.Complete());
            return new BuildOutcome(keptPath, result.Warnings.ToArray(), verification with { Length = new FileInfo(keptPath).Length }, stopwatch.Elapsed);
        }

        var outcome = await VerifyOutputAsync(request, result.OutputPath, result.Warnings.ToArray(), tracker, stopwatch, log, progress, cancellationToken).ConfigureAwait(false);
        LogSdkVersionChange(appFolder, result.OutputPath, request.Passcode, log);
        return outcome;
    }

    /// <summary>Kiểm tra cấu trúc (và SHA-256 nếu bật) rồi nội dung gói vừa tạo; gói hỏng thì ném lỗi thay vì báo thành công.</summary>
    private static async Task<BuildOutcome> VerifyOutputAsync(
        BuildRequest normalized,
        string outputPath,
        string[] warnings,
        ProgressTracker tracker,
        Stopwatch stopwatch,
        Action<LogEntry> log,
        IProgress<BuildProgress>? progress,
        CancellationToken cancellationToken)
    {
        {
            log(new LogEntry(LogLevel.Info, Loc.T(normalized.ComputeSha256 ? "Plan.VerifyingSha" : "Plan.Verifying")));
            progress?.Report(tracker.EnterPhase(normalized.ComputeSha256 ? PhaseCatalog.Sha256 : PhaseCatalog.Verify));

            var verification = await Task.Run(
                () => PackageVerifier.Verify(
                    outputPath,
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

            // Kiểm tra nội dung bằng engine 0.6.8: bản nhanh luôn chạy (chữ ký CNT, bố cục PlayGo, NAPS, inode — chỉ đọc metadata),
            // bản đầy đủ giải nén thử mọi tệp khi người dùng bật. Gói hỏng thì dừng ở đây thay vì báo "thành công".
            if (normalized.FullVerify)
            {
                log(new LogEntry(LogLevel.Info, Loc.T("Plan.VerifyingFull")));
                progress?.Report(tracker.EnterPhase(PhaseCatalog.VerifyFull));
            }

            var contents = await Task.Run(
                () => PackageVerifier.VerifyContents(
                    outputPath,
                    normalized.Passcode,
                    normalized.FullVerify,
                    cancellationToken,
                    normalized.FullVerify ? percent => progress?.Report(tracker.UpdatePhasePercent(percent)) : null),
                cancellationToken).ConfigureAwait(false);
            foreach (var check in contents.Checks)
            {
                log(new LogEntry(LogLevel.Info, Loc.F("Plan.VerifyCheck", check)));
            }

            if (!contents.IsValid)
            {
                foreach (var issue in contents.Issues)
                {
                    log(new LogEntry(LogLevel.Error, Loc.F("Plan.VerifyIssue", issue.Stage, issue.Message)));
                }

                throw new InvalidDataException(Loc.F("Verify.ContentFailed", string.Join("; ", contents.Issues)));
            }

            log(new LogEntry(LogLevel.Success, Loc.F(contents.IsFull ? "Plan.VerifiedFull" : "Plan.VerifiedQuick", contents.Checks.Count, Formatters.Duration(contents.Elapsed))));
            verification = verification with { Contents = contents };

            stopwatch.Stop();
            progress?.Report(tracker.Complete());
            return new BuildOutcome(outputPath, warnings, verification, stopwatch.Elapsed);
        }
    }

    /// <summary>
    /// Nguồn phải có sce_sys/keystone đúng 96 byte — bộ công cụ SDK dừng hẳn khi thiếu (giống build-from-folder.ps1). Kiểm tra
    /// trước khi giải nén/gắn ảnh để khỏi chờ vô ích; ảnh không đọc được ở đây thì để bước tạo GP5 kiểm tra lại.
    /// </summary>
    private static void RequireKeystone(SourceInfo source)
    {
        long? length = null;
        var known = false;
        try
        {
            if (source.IsExFat)
            {
                using var image = ExFatImage.Open(source.Path);
                var entry = image.Find(SonySdkProject.KeystonePath, SourceLocator.ResolveAppRoot(image, source));
                length = entry is { IsDirectory: false } ? entry.Length : null;
                known = true;
            }
            else if (source.IsUfs)
            {
                using var image = UfsImage.Open(source.Path);
                var entry = image.Find(SonySdkProject.KeystonePath, SourceLocator.ResolveAppRoot(image, source));
                length = entry is { IsDirectory: false } ? entry.Length : null;
                known = true;
            }
            else if (!source.IsGp5)
            {
                SonySdkProject.HasValidKeystone(source.Path, out length);
                known = true;
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
        }

        if (known && length != SonySdkProject.KeystoneLength)
        {
            var message = length == null
                ? Loc.F("Sdk.KeystoneMissing", SonySdkProject.KeystonePath)
                : Loc.F("Sdk.KeystoneLength", length, SonySdkProject.KeystoneLength);
            throw new BuildValidationException([new ValidationError(BuildPreparer.FieldSource, message + " " + Loc.T("Sdk.KeystoneHint"))]);
        }
    }

    /// <summary>Ghi chú khi SDK Sony ghi sdkVersion / requiredSystemSoftwareVersion khác với param.json của nguồn.</summary>
    private static void LogSdkVersionChange(string appFolder, string outputPath, string passcode, Action<LogEntry> log)
    {
        try
        {
            var sourceParams = PackageInspector.ParseParamJson(File.ReadAllBytes(Path.Combine(appFolder, "sce_sys", "param.json")));
            var output = PackageInspector.Inspect(outputPath, passcode, CancellationToken.None).Params;
            if (output == null)
            {
                return;
            }

            if (!string.Equals(sourceParams.SdkVersion, output.SdkVersion, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(sourceParams.RequiredSystemSoftwareVersion, output.RequiredSystemSoftwareVersion, StringComparison.OrdinalIgnoreCase))
            {
                log(new LogEntry(LogLevel.Warning, Loc.F(
                    "Sdk.VersionChanged",
                    sourceParams.SdkVersion ?? "—",
                    output.SdkVersion ?? "—",
                    sourceParams.RequiredSystemSoftwareVersion ?? "—",
                    output.RequiredSystemSoftwareVersion ?? "—")));
            }
        }
        catch (Exception)
        {
        }
    }

    /// <summary>Gói cùng Content ID đã có trong thư mục xuất: hỏi ghi đè / giữ bản cũ / huỷ.</summary>
    internal static void ResolveOutputConflict(string outputFolder, string contentId, Func<IReadOnlyList<string>, OutputConflictChoice>? ask, Action<LogEntry> log, CancellationToken cancellationToken = default, string? patchReference = null)
    {
        if (ask == null)
        {
            return;
        }

        // Bản vá ghi ra UPDATE_<contentId>-….pkg: chỉ bản vá cũ cùng tên mới là trùng; gói gốc (thường nằm ngay trong thư mục xuất) không bao giờ bị hỏi ghi đè.
        var patch = !string.IsNullOrWhiteSpace(patchReference);
        var existing = OutputConflict.Find(outputFolder, contentId, patch ? SonySdkBuilder.PatchPrefix : string.Empty, patch ? patchReference : null);
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
    private static SourceMirror? BuildSourceMirror(BuildRequest request, SourceInfo source, Gp5ProjectInfo? project, string sourceFolder, bool ownsFolder, Action<LogEntry> log, out bool required)
    {
        required = false;
        // Dự án GP5: thư mục ứng dụng thật nằm ở rootdir của dự án, không phải thư mục chứa tệp .gp5.
        var appFolder = source.IsGp5 ? project?.RootFolder : sourceFolder;
        if (appFolder == null || !Directory.Exists(appFolder))
        {
            return null;
        }

        var skip = FolderCleanupPaths(request, appFolder).ToList();

        // Giữ bộ playgo*: engine 0.6.8 đọc số khối từ playgo-chunk.dat và dừng cả lượt tạo gói nếu tệp hỏng — bỏ riêng tệp hỏng.
        var invalidPlayGo = request.RemovePlayGoFiles ? Array.Empty<string>() : PlayGoCleanup.CheckFolder(appFolder).Invalid;
        if (invalidPlayGo.Count > 0)
        {
            skip.AddRange(invalidPlayGo);
            log(new LogEntry(LogLevel.Warning, Loc.F("Plan.PlayGoInvalid", PlayGoCleanup.Describe(invalidPlayGo))));
        }

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
        if (PatchedParamJson(request, appFolder, log) is { } patchedParam)
        {
            replace["sce_sys/param.json"] = patchedParam;
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
        var cleanup = skip.Except(junkPaths, StringComparer.OrdinalIgnoreCase).Except(invalidPlayGo, StringComparer.OrdinalIgnoreCase).ToList();

        // Bản giải nén ảnh nằm trong thư mục tạm là của công cụ, không phải nguồn của người dùng: sửa thẳng trên đó, khỏi cần
        // liên kết — nên chạy được cả khi thư mục tạm ở ổ exFAT/FAT32 hay ổ mạng không tạo được liên kết.
        if (ownsFolder)
        {
            if (skip.Count > 0 || replace.Count > 0)
            {
                LogCleanup(cleanup, Array.Empty<string>(), log);
                SourceMirror.ApplyInPlace(appFolder, plan with { Copy = Array.Empty<string>() });
                log(new LogEntry(LogLevel.Info, Loc.F("Plan.AppliedInPlace", appFolder)));
            }

            // Giải nén/sửa trên ổ exFAT/FAT của macOS sinh "._tên" cho từng tệp — dọn trước khi thư viện đóng gói chúng.
            if (SourceMirror.RemoveAppleDoubleArtifacts(appFolder) is var artifacts and > 0)
            {
                log(new LogEntry(LogLevel.Info, Loc.F("Plan.AppleDoubleRemoved", artifacts, appFolder)));
            }

            return null;
        }

        if (!plan.Any && !libraryWrites)
        {
            return null;
        }

        required = true;
        if (!plan.Any)
        {
            log(new LogEntry(LogLevel.Info, Loc.T("Plan.MirrorForWrites")));
        }

        LogCleanup(cleanup, Array.Empty<string>(), log);
        return SourceMirror.CreateInAny(appFolder, MirrorWorkFolders(request.TemporaryFolder), plan, log, force: true);
    }

    /// <summary>
    /// Chỗ dựng gương theo thứ tự thử: thư mục tạm người dùng chọn, rồi thư mục tạm của hệ thống nếu nằm ở ổ khác (ổ hệ thống luôn
    /// là NTFS/APFS nên tạo được liên kết). Gương chỉ gồm liên kết cộng sce_sys nên nhỏ.
    /// </summary>
    public static IReadOnlyList<string> MirrorWorkFolders(string temporaryFolder)
    {
        var folders = new List<string> { temporaryFolder };
        try
        {
            var system = OperatingSystem.IsMacOS()
                ? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Caches", "PSVIETHOA FPKG Builder", "mirror")
                : System.IO.Path.Combine(System.IO.Path.GetTempPath(), "psviethoa-mirror");
            if (!DiskSpaceAdvisor.IsSameVolume(system, temporaryFolder))
            {
                folders.Add(system);
            }
        }
        catch (Exception)
        {
        }

        return folders;
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
        var invalidPlayGo = new List<string>();
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

            if (request.SonySdkActive)
            {
                // SDK Sony: script gốc tự loại playgo*, và bảng playgo-chunk.dat còn cần đọc để giữ cấu trúc chunk của gói gốc.
            }
            else if (request.RemovePlayGoFiles)
            {
                var playgo = PlayGoCleanup.ListImage(image, appRoot);
                paths.AddRange(playgo);
                present.AddRange(playgo);
            }
            else if (PlayGoCleanup.CheckImage(image, appRoot).Invalid is { Count: > 0 } invalid)
            {
                // Giữ bộ playgo* nhưng có tệp hỏng: ẩn riêng tệp đó, engine 0.6.8 dừng khi đọc phải nó.
                paths.AddRange(invalid);
                invalidPlayGo.AddRange(invalid);
                log(new LogEntry(LogLevel.Warning, Loc.F("Plan.PlayGoInvalid", PlayGoCleanup.Describe(invalid))));
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
            invalidPlayGo.Clear();
            emptyFolders.Clear();
            paths.AddRange(StaticCleanupPaths(request));
        }

        LogCleanup(present, emptyFolders, log);
        anyPresent = present.Count > 0 || invalidPlayGo.Count > 0 || emptyFolders.Count > 0;
        return paths.Count == 0 ? null : paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>Ghi nhật ký khi engine sẽ đổi applicationDrmType của nguồn sang "standard" (việc đổi do engine làm trong bộ nhớ).</summary>
    private static void LogDrmOverride(BuildRequest request, byte[] paramJson, Action<LogEntry> log)
    {
        // SDK Sony: DRM được sửa thẳng trong param.json (ParamPatch) và dòng nhật ký đó đã có — tránh ghi hai lần.
        if (request.ForceStandardDrm && request.Kind == PackageKind.Application && !request.ParamPatch.ForceStandardDrm &&
            ParamJsonPatch.ReadDrmType(paramJson) is { } drm && ParamJsonPatch.NeedsDrmRewrite(drm))
        {
            log(new LogEntry(LogLevel.Info, Loc.F("Plan.PatchDrm", drm, ParamJsonPatch.StandardDrm)));
        }
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
        if (!request.ParamPatch.Any && !request.ForceStandardDrm)
        {
            return null;
        }

        try
        {
            if (ReadImageParamJson(source) is not { } paramJson)
            {
                return null;
            }

            LogDrmOverride(request, paramJson, log);
            if (!request.ParamPatch.Any)
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

            // Engine 0.6.8 tự đặt applicationDrmType = "standard" trong bộ nhớ (chỉ gói ứng dụng), không cần sửa param.json.
            ForceStandardApplicationDrm = request.ForceStandardDrm,
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

    private static string FileSystemLabel(string folder) =>
        (DiskSpaceAdvisor.ResolveMountPoint(folder) ?? folder) + " (" + (DiskSpaceAdvisor.FileSystemOf(folder) ?? "?") + ")";

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

    private static void LogPlan(BuildRequest request, SourceInfo source, Gp5ProjectInfo? project, KrakenBackendKind backend, string? publishingToolsPath, bool sonySdk, Action<LogEntry> log)
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
        if (!DiskSpaceAdvisor.IsSameVolume(request.TemporaryFolder, request.OutputFolder))
        {
            log(new LogEntry(LogLevel.Info, Loc.F("Plan.SplitDrives", FileSystemLabel(request.TemporaryFolder), FileSystemLabel(request.OutputFolder))));
        }

        foreach (var folder in new[] { request.TemporaryFolder, request.OutputFolder }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (DiskSpaceAdvisor.IsFat(folder))
            {
                log(new LogEntry(LogLevel.Warning, Loc.F("Plan.FatVolume", folder)));
            }
        }
        log(new LogEntry(LogLevel.Info, Loc.F("Plan.ContentId", request.ContentId)));
        log(new LogEntry(LogLevel.Info, Loc.F("Plan.Version", request.Version)));
        log(new LogEntry(LogLevel.Info, Loc.F("Plan.Kind", request.Kind, request.ImageMode)));

        // SDK Sony: mức nén, PFS, PlayGo, SDK và bản dựng xác định là của Publishing Tools — các tuỳ chọn engine không áp dụng nên không ghi.
        if (sonySdk)
        {
            log(new LogEntry(LogLevel.Info, Loc.T(request.ForceStandardDrm ? "Plan.DrmPolicySdk" : "Plan.DrmPolicyOff")));
            if (request.ClearVersionFileUri)
            {
                log(new LogEntry(LogLevel.Info, Loc.T("Plan.VersionUriPolicy")));
            }

            if (request.ClearPlayGoAttributes)
            {
                log(new LogEntry(LogLevel.Warning, Loc.T("Plan.Attribute3Policy")));
            }

            log(new LogEntry(LogLevel.Info, Loc.T(request.FullVerify ? "Plan.VerifyPolicyFull" : "Plan.VerifyPolicyQuick")));
            log(new LogEntry(LogLevel.Info, Loc.T(request.ComputeSha256 ? "Plan.ShaOn" : "Plan.ShaOff")));
            return;
        }

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
            log(new LogEntry(LogLevel.Info, Loc.F("Plan.RequiredFwSdk", generation.Major)));
        }
        else
        {
            log(new LogEntry(LogLevel.Info, Loc.T("Plan.SdkKeep")));
            log(new LogEntry(LogLevel.Info, Loc.T(request.LowerRequiredFirmware ? "Plan.RequiredFwLower" : "Plan.RequiredFwKeep")));
        }

        log(new LogEntry(LogLevel.Info, Loc.T(request.FullVerify ? "Plan.VerifyPolicyFull" : "Plan.VerifyPolicyQuick")));

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
                try
                {
                    // Ổ exFAT/FAT trên macOS: tên có dấu không xoá được bằng tên liệt kê — xoá từng mục có thử dạng chuẩn hoá khác.
                    RobustDelete.Tree(path);
                    if (!Directory.Exists(path))
                    {
                        return;
                    }
                }
                catch (Exception)
                {
                }

                Thread.Sleep(300);
            }
        }
    }
}
