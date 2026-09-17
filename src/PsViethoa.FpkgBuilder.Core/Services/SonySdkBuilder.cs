using System.Diagnostics;
using System.Globalization;
using System.Text;
using LibProsperoPkg;
using PsViethoa.FpkgBuilder.Core.Localization;
using PsViethoa.FpkgBuilder.Core.Models;

namespace PsViethoa.FpkgBuilder.Core.Services;

/// <summary>Gói tạo bằng SDK Sony.</summary>
/// <param name="OutputPath">Tệp .pkg cuối.</param>
/// <param name="ProjectPath">Tệp .gp5 giữ lại cạnh gói (như build-from-folder.ps1).</param>
/// <param name="LogFolder">Thư mục &lt;tên&gt;-build-logs với 01-create-gp5.log, 02-img-create.log, 03-postprocess.log.</param>
public sealed record SonySdkBuildResult(string OutputPath, string ProjectPath, string LogFolder, IReadOnlyList<string> Warnings, SonySdkConversionReport Report);

/// <summary>
/// Tạo gói theo chuẩn của bộ sdk-fpkg729-fix (profile <c>sdk279-plaintext-unsigned-v2</c>), đúng ba bước và đúng tên tệp của
/// <c>build-from-folder.ps1</c>: [1/3] <c>&lt;tên&gt;.gp5</c> + <c>&lt;tên&gt;.playgo-scenario.json</c> trong thư mục xuất, [2/3]
/// Publishing Tools 2.79 đã vá tạo <c>&lt;tên&gt;.sdk-plaintext.pkg</c> (<c>img_create --oformat nwonly</c>), [3/3] chuyển sang gói
/// tương thích LibProsperoPkg (PLAINTEXT_NOAUTH) rồi bỏ gói thô. GP5, scenario và thư mục <c>&lt;tên&gt;-build-logs</c> được giữ lại
/// như bản gốc; <c>.naps_metric.json</c> bị xoá như bản gốc (không -KeepIntermediate).
/// </summary>
public static class SonySdkBuilder
{
    /// <summary>
    /// Lượt tạo gói này dùng được SDK Sony không. Bộ công cụ chỉ hỗ trợ gói ứng dụng (APP/nwonly) dạng lớp ngoài không mã hoá,
    /// dựng từ thư mục (dự án GP5 có sẵn của người dùng đi qua engine tích hợp).
    /// </summary>
    public static bool Supports(BuildRequest request, SourceInfo source, out string? reason)
    {
        reason = request.Kind != PackageKind.Application
            ? Loc.T("Sdk.OnlyApplication")
            : request.ImageMode != OuterImageMode.PlaintextNoAuth
                ? Loc.T("Sdk.OnlyPlaintext")
                : source.IsGp5
                    ? Loc.T("Sdk.NoGp5Source")
                    : null;
        return reason == null;
    }

    /// <summary>Tên tệp gói giống engine tích hợp: &lt;contentId&gt;-A&lt;vv&gt;-V&lt;vv&gt;.pkg.</summary>
    public static string PackageFileName(string contentId, string version)
    {
        var component = ProsperoContentVersion.ParseOrDefault(version).PackageNameComponent;
        return $"{contentId}-A{component}-V{component}.pkg";
    }

    /// <summary>Các tệp phụ mà một lượt SDK để lại cạnh gói: .gp5, .playgo-scenario.json, thư mục -build-logs, .gp5-assets/&lt;tên&gt;.</summary>
    public static IEnumerable<string> Artifacts(string outputFolder, string contentId, string version)
    {
        var stem = Path.GetFileNameWithoutExtension(PackageFileName(contentId, version));
        yield return Path.Combine(outputFolder, stem + ".gp5");
        yield return Path.Combine(outputFolder, stem + ".playgo-scenario.json");
        yield return Path.Combine(outputFolder, stem + "-build-logs");
        yield return Path.Combine(outputFolder, SonySdkProject.AssetsFolderName, stem);
    }

    public const string DdsConverterFileName = "prospero-dds2png.exe";

    public static async Task<SonySdkBuildResult> BuildAsync(
        SonySdkRuntime runtime,
        BuildRequest request,
        string appFolder,
        SonySdkSourcePlan plan,
        Action<LogEntry> log,
        Action<BuildPhase, double, string?> progress,
        CancellationToken cancellationToken)
    {
        // Môi trường chạy: Wine (tạo WINEPREFIX lần đầu) hoặc Visual C++ runtime trên Windows.
        progress(PhaseCatalog.SdkRuntime, 0, null);
        if (runtime.UsesWine)
        {
            log(new LogEntry(LogLevel.Info, Loc.F("Sdk.RunVia", runtime.WinePath!)));
            await Task.Run(() => SonySdkToolchain.ClearQuarantine(runtime), cancellationToken).ConfigureAwait(false);
            await SonySdkRunner.EnsureWinePrefixAsync(runtime, log, cancellationToken).ConfigureAwait(false);
        }
        else if (!SonySdkToolchain.VcRuntimeInstalled)
        {
            log(new LogEntry(LogLevel.Warning, Loc.T("Sdk.VcRuntimeInstalling")));
            var installed = await Task.Run(
                () => SonySdkToolchain.InstallVcRuntime(message => log(new LogEntry(LogLevel.Info, message)), TimeSpan.FromMinutes(10)),
                cancellationToken).ConfigureAwait(false);
            if (!installed)
            {
                throw new InvalidOperationException(Loc.T("Sdk.VcRuntimeMissing"));
            }
        }

        progress(PhaseCatalog.SdkRuntime, 100, null);
        cancellationToken.ThrowIfCancellationRequested();

        // Tên tệp như build-from-folder.ps1: mọi thứ nằm cạnh gói cuối trong thư mục xuất, gốc tên = tên gói.
        var finalPath = Path.Combine(request.OutputFolder, PackageFileName(request.ContentId, request.Version));
        var stem = Path.GetFileNameWithoutExtension(finalPath);
        var projectPath = Path.Combine(request.OutputFolder, stem + ".gp5");
        var scenarioPath = Path.Combine(request.OutputFolder, stem + ".playgo-scenario.json");
        var rawPath = Path.Combine(request.OutputFolder, stem + ".sdk-plaintext.pkg");
        var metricPath = rawPath + ".naps_metric.json";
        var logFolder = Path.Combine(request.OutputFolder, stem + "-build-logs");
        var assetsFolder = Path.Combine(request.OutputFolder, SonySdkProject.AssetsFolderName, stem);
        var warnings = new List<string>();

        // Publishing Tools không mở được đường dẫn có ký tự ngoài ASCII (dòng lệnh ANSI, src_path trong GP5): thư mục nguồn và
        // thư mục xuất (chứa cả .gp5-assets) đi qua bí danh ASCII khi cần; đường dẫn thật ghi trong nhật ký.
        using var aliases = SonySdkPathAliases.Create(SonySdkPathAliases.DefaultBases(request.TemporaryFolder, request.OutputFolder), log);
        var realSource = SonySdkProject.RealPath(appFolder);
        var realOutput = SonySdkProject.RealPath(request.OutputFolder);
        aliases.Add(realSource, "src");
        aliases.Add(realOutput, "out");

        // KHÔNG giải liên kết (RealPath) cho từng tệp ở đây: 95 000 tệp × mở handle từng thành phần đường dẫn trên ổ USB là hàng phút.
        // Thư mục gốc đã được giải sẵn, mọi đường dẫn tệp do bộ duyệt sinh ra đều nằm dưới gốc đó nên chỉ cần thay tiền tố bí danh.
        string ToolPath(string path) => runtime.ToolPath(aliases.Map(path));
        var realRawPath = SonySdkProject.RealPath(rawPath);

        // pic*.dds không có PNG: bộ chuyển đổi của bộ công cụ (prospero-dds2png.exe, qua Wine trên macOS) khôi phục PNG.
        var converterPath = Path.Combine(runtime.ToolkitRoot, DdsConverterFileName);
        Action<string, string, bool>? converter = File.Exists(converterPath)
            ? (dds, png, preserveAlpha) =>
            {
                var arguments = new List<string> { ToolPath(dds), ToolPath(png) };
                if (preserveAlpha)
                {
                    arguments.Add("--preserve-alpha");
                }

                var (exit, output) = SonySdkRunner.RunToolAsync(runtime, converterPath, arguments, cancellationToken).GetAwaiter().GetResult();
                if (exit != 0)
                {
                    throw new InvalidOperationException(Loc.F("Sdk.DdsConvertFailed", Path.GetFileName(dds), exit, output.Trim()));
                }
            }
            : null;
        if (aliases.Count > 0)
        {
            log(new LogEntry(LogLevel.Info, Loc.T("Sdk.AliasNote")));
        }

        try
        {
            foreach (var stale in new[] { projectPath, scenarioPath, rawPath, metricPath })
            {
                DeleteQuietly(stale);
            }

            // Như -Force của build-from-folder.ps1: bỏ .gp5-assets/<tên> của lần trước rồi tạo lại.
            BuildEngine.TryDeleteDirectory(assetsFolder);
            Directory.CreateDirectory(logFolder);

            // [1/3] GP5 + scenario mặc định
            progress(PhaseCatalog.SdkProject, 0, null);
            var project = await Task.Run(
                () => SonySdkProject.Create(
                    appFolder,
                    projectPath,
                    request.Passcode,
                    ToolPath,
                    plan,
                    cancellationToken,
                    count => progress(PhaseCatalog.SdkProject, 0, Loc.F("Sdk.Listed", count.ToString("N0", CultureInfo.CurrentCulture))),
                    converter),
                cancellationToken).ConfigureAwait(false);
            WriteLog(Path.Combine(logFolder, "01-create-gp5.log"), project.Report);

            // Tên Publishing Tools từ chối ("invalid attribute value dst_path"): dừng ngay với danh sách tệp thay vì để SDK tự dừng sau vài phút.
            if (project.UnsupportedPaths is { Count: > 0 } unsupported)
            {
                throw new InvalidDataException(Loc.F("Sdk.PathUnsupported", unsupported.Count, string.Join(", ", unsupported.Take(8)) + (unsupported.Count > 8 ? ", …" : string.Empty)));
            }

            log(new LogEntry(LogLevel.Info, Loc.F("Sdk.Step1", project.FileCount, Formatters.Size(project.TotalBytes), project.ProjectPath)));
            log(new LogEntry(LogLevel.Info, Loc.F("Sdk.ParamNormalized", Path.GetRelativePath(request.OutputFolder, project.ParamJsonPath), project.ParamChanges.Count == 0 ? Loc.T("Sdk.ParamUnchanged") : string.Join(" · ", project.ParamChanges))));
            foreach (var png in project.RecoveredPngs)
            {
                log(new LogEntry(LogLevel.Info, Loc.F("Sdk.PngRecovered", png.Destination, png.DdsName)));
            }

            if (plan.PlayGo is { IsTrivial: false } structure)
            {
                if (structure.SupportedLanguageMask != 0)
                {
                    log(new LogEntry(LogLevel.Info, Loc.F("Sdk.PlayGoMapped", structure.Chunks.Count, project.PlayGoMappedFiles, project.FileCount - 1 - project.PlayGoMappedFiles)));
                }

                log(new LogEntry(LogLevel.Info, Loc.T(project.ScenarioFromSource ? "Sdk.PlayGoScenarioOriginal" : "Sdk.PlayGoScenarioGenerated")));
            }

            if (project.Excluded.Count > 0)
            {
                log(new LogEntry(LogLevel.Info, Loc.F("Sdk.Skipped", project.Excluded.Count, Summarize(project.Excluded))));
            }

            if (project.SkippedByApp.Count > 0)
            {
                log(new LogEntry(LogLevel.Info, Loc.F("Sdk.SkippedByApp", project.SkippedByApp.Count, Summarize(project.SkippedByApp))));
            }

            progress(PhaseCatalog.SdkProject, 100, null);

            // Windows Defender quét mỗi tệp ở lần mở đầu tiên (~11 ms) trong khi Publishing Tools mở tuần tự từng tệp: mở trước song song
            // (chỉ đọc) để kết quả quét có sẵn trong bộ nhớ đệm — không đổi GP5 hay gói, chỉ rút ngắn pha kiểm tra tệp của SDK.
            if (request.SdkPrescan && SonySdkPrescan.Applicable && project.SourceFiles is { Count: > 0 } sourceFiles)
            {
                progress(PhaseCatalog.SdkPrescan, 0, null);
                var parallelism = SonySdkPrescan.DefaultParallelism;
                var prescan = await Task.Run(
                    () => SonySdkPrescan.Run(
                        sourceFiles,
                        parallelism,
                        (done, total) => progress(PhaseCatalog.SdkPrescan, done * 100.0 / Math.Max(1, total), Loc.F("Sdk.PrescanProgress", done.ToString("N0", CultureInfo.CurrentCulture), total.ToString("N0", CultureInfo.CurrentCulture))),
                        cancellationToken),
                    cancellationToken).ConfigureAwait(false);
                log(new LogEntry(LogLevel.Info, Loc.F("Sdk.Prescanned", prescan.Files.ToString("N0", CultureInfo.CurrentCulture), Formatters.Duration(prescan.Elapsed), prescan.FilesPerSecond.ToString("N0", CultureInfo.CurrentCulture), parallelism, prescan.Failed)));
                progress(PhaseCatalog.SdkPrescan, 100, null);
                cancellationToken.ThrowIfCancellationRequested();
            }

            // [2/3] Gói thô từ Publishing Tools
            log(new LogEntry(LogLevel.Info, Loc.T("Sdk.Step2")));
            progress(PhaseCatalog.SdkImage, 0, null);
            var imageStarted = DateTime.UtcNow;
            var imageWatch = Stopwatch.StartNew();
            var imageLog = Path.Combine(logFolder, "02-img-create.log");
            SonySdkImageResult image;
            try
            {
                image = await SonySdkRunner.CreateImageAsync(
                    runtime,
                    ToolPath,
                    project.ProjectPath,
                    realRawPath,
                    compressionLevel: request.SdkCompressionLevel,
                    entry =>
                    {
                        if (entry.Level == LogLevel.Warning && entry.Message.StartsWith("SDK: ", StringComparison.Ordinal))
                        {
                            warnings.Add(entry.Message);
                        }

                        log(entry);
                    },
                    sdkProgress =>
                    {
                        var written = sdkProgress.BytesWritten > 0 ? Loc.F("Sdk.Written", Formatters.Size(sdkProgress.BytesWritten)) : null;
                        progress(PhaseCatalog.SdkImage, sdkProgress.Percent, written);
                    },
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                var levelArgument = request.SdkCompressionLevel is { } level ? "--compression_level " + level.ToString(CultureInfo.InvariantCulture) + " " : string.Empty;
                WriteStageLog(imageLog, "img_create --oformat nwonly " + levelArgument + Quote(project.ProjectPath) + " " + Quote(rawPath), imageStarted, DateTime.UtcNow, "cancelled");
                throw;
            }

            WriteStageLog(imageLog, image.CommandLine, imageStarted, DateTime.UtcNow, image.ExitCode.ToString(CultureInfo.InvariantCulture), image.Errors.ToArray());
            if (!image.Succeeded)
            {
                throw new InvalidOperationException(Loc.F("Sdk.ImageFailed", SonySdkRunner.Describe(image)));
            }

            log(new LogEntry(LogLevel.Info, Loc.F("Sdk.Step2Done", Formatters.Size(new FileInfo(rawPath).Length), Formatters.Duration(imageWatch.Elapsed))));
            cancellationToken.ThrowIfCancellationRequested();

            // [3/3] Chuyển sang gói tương thích LibProsperoPkg (sửa tại chỗ rồi đổi tên — cùng kết quả với copy + sửa của script gốc)
            log(new LogEntry(LogLevel.Info, Loc.T("Sdk.Step3")));
            progress(PhaseCatalog.SdkConvert, 0, null);
            var convertStarted = DateTime.UtcNow;
            var convertLog = Path.Combine(logFolder, "03-postprocess.log");
            var convertCommand = "SonySdkConverter (postprocess-sdk279-plaintext.py port) " + Quote(rawPath) + " " + Quote(finalPath);
            SonySdkConversionReport report;
            try
            {
                report = await Task.Run(
                    () => SonySdkConverter.ConvertInPlace(rawPath, (percent, _) => progress(PhaseCatalog.SdkConvert, percent, null), cancellationToken),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                WriteStageLog(convertLog, convertCommand, convertStarted, DateTime.UtcNow, ex is OperationCanceledException ? "cancelled" : "1", ex.Message);
                throw;
            }

            File.Move(rawPath, finalPath, overwrite: true);
            DeleteQuietly(metricPath);
            WriteStageLog(
                convertLog,
                convertCommand,
                convertStarted,
                DateTime.UtcNow,
                "0",
                $"Created {finalPath}",
                $"Marked {report.OuterBlocks} outer-PFS blocks plaintext/no-auth; cleared {report.ClearedEncryptionFlags} CNT encryption flag(s).",
                $"Rebuilt {report.CntHeaderWrapBytes}-byte deterministic CNT RSA-3072 header wrap.",
                $"Repaired {report.PlayGoCrcBlocks} changed PlayGo mount-image CRC block(s).");
            log(new LogEntry(LogLevel.Info, Loc.F("Sdk.Step3Done", report.OuterBlocks, report.ClearedEncryptionFlags, report.CntHeaderWrapBytes, report.PlayGoCrcBlocks)));
            log(new LogEntry(LogLevel.Info, Loc.F("Sdk.Artifacts", Path.GetFileName(projectPath), Path.GetFileName(logFolder), SonySdkProject.AssetsFolderName + "/" + stem)));
            progress(PhaseCatalog.SdkConvert, 100, null);
            return new SonySdkBuildResult(finalPath, projectPath, logFolder, warnings, report);
        }
        catch (Exception)
        {
            // Gói thô dở dang bỏ đi; GP5 và nhật ký giữ lại để tra lỗi (script gốc cũng để lại).
            DeleteQuietly(rawPath);
            DeleteQuietly(metricPath);
            throw;
        }
    }

    private static string Quote(string value) => "\"" + value + "\"";

    private static string Summarize(IReadOnlyList<string> items) =>
        string.Join(", ", items.Take(12)) + (items.Count > 12 ? ", …" : string.Empty);

    /// <summary>02-img-create.log / 03-postprocess.log với đúng các khoá của build-from-folder.ps1 (command, started_utc, finished_utc, elapsed, exit_code).</summary>
    private static void WriteStageLog(string path, string command, DateTime started, DateTime finished, string exitCode, params string[] extra)
    {
        var builder = new StringBuilder();
        builder.Append("command=").Append(command).Append('\n');
        builder.Append("started_utc=").Append(started.ToString("o", CultureInfo.InvariantCulture)).Append('\n');
        builder.Append("finished_utc=").Append(finished.ToString("o", CultureInfo.InvariantCulture)).Append('\n');
        builder.Append("elapsed=").Append((finished - started).ToString("c", CultureInfo.InvariantCulture)).Append('\n');
        builder.Append("exit_code=").Append(exitCode).Append('\n');
        foreach (var line in extra)
        {
            builder.Append(line).Append('\n');
        }

        WriteLog(path, builder.ToString());
    }

    private static void WriteLog(string path, string content)
    {
        try
        {
            File.WriteAllText(path, content, new UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
