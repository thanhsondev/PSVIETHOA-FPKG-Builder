using LibProsperoPkg;
using PsViethoa.FpkgBuilder.Core.Localization;
using PsViethoa.FpkgBuilder.Core.Models;

namespace PsViethoa.FpkgBuilder.Core.Services;

/// <summary>Kết quả tạo một gói DLC: đường dẫn tệp (null khi lỗi) và thông báo lỗi nếu có.</summary>
public sealed record DlcBuildResult(DlcEmuEntry Entry, string? OutputPath, long Size, string? Error)
{
    public bool Success => OutputPath != null;
}

/// <summary>
/// Tạo các gói DLC mở khoá quyền sở hữu từ danh sách trong dlc_emu.ini của game.
/// <para>
/// Bộ giả lập DLC chỉ dùng được khi chạy bản dump qua ShadowMount+; cài từ gói thì không có ai nạp nó (và một số game còn
/// đứng ở màn hình splash vì nó). Với DLC dạng <c>NO_EXTRA_DATA</c> — toàn bộ nội dung đã nằm sẵn trong game, chỉ thiếu quyền —
/// có thể tạo mỗi DLC một gói rỗng rồi cài như gói thường; PS5 cấp quyền theo đường chính thức, không cần giả lập.
/// </para>
/// <para>
/// Gói được tạo ở dạng <see cref="ProsperoPackageMode.AdditionalContentData"/> với thư mục dữ liệu rỗng. Dạng "entitlement
/// only" (PSAL, content_type 0x22) đúng chuẩn hơn nhưng theo tài liệu thư viện nó là CNT trần — không có FIH, không có PFS
/// ngoài/trong, không IMAGE_KEY — nên trình cài debug của PS5 từ chối. Dạng AC có dữ liệu cho ra gói FIH giống gói game nên
/// cài được, vẫn mang đúng giấy phép RIF của Content ID đó.
/// </para>
/// </summary>
public static class DlcPackageBuilder
{
    /// <summary>
    /// Tạo gói cho MỌI mục additional content của dlc_emu.ini (như PS5 DLC Converter của Lapy): NO_EXTRA_DATA → gói quyền rỗng; mục có
    /// dữ liệu riêng (INSTALLED…) → đóng kèm thư mục <c>mount_point</c> của nó trong bản dump (<paramref name="sourceFolder"/>) khi
    /// có, không thì vẫn tạo gói quyền kèm cảnh báo (nhiều game để dữ liệu DLC sẵn trong game, chỉ thiếu quyền — The Last of Us Part I).
    /// </summary>
    public static IReadOnlyList<DlcBuildResult> BuildAll(
        IEnumerable<DlcEmuEntry> entries,
        string outputFolder,
        string temporaryFolder,
        string? gameTitle,
        Action<LogEntry>? log,
        CancellationToken cancellationToken,
        Func<IReadOnlyList<string>, OutputConflictChoice>? onOutputConflict = null,
        string? sourceFolder = null)
    {
        var results = new List<DlcBuildResult>();
        Directory.CreateDirectory(outputFolder);
        Directory.CreateDirectory(temporaryFolder);

        var list = entries as IReadOnlyList<DlcEmuEntry> ?? entries.ToList();
        if (onOutputConflict != null)
        {
            var existing = list.SelectMany(e => OutputConflict.Find(outputFolder, e.ContentId)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (existing.Length > 0 && !OutputConflict.Apply(existing, onOutputConflict(existing), (from, to) =>
                    log?.Invoke(new LogEntry(LogLevel.Info, Loc.F("Plan.OutputKept", Path.GetFileName(from), to == null ? "—" : Path.GetFileName(to))))))
            {
                throw new OperationCanceledException();
            }
        }

        entries = list;

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!entry.IsAdditionalContent)
            {
                var skipped = Loc.F("Dlc.SkippedSection", entry.ContentId, entry.Section);
                log?.Invoke(new LogEntry(LogLevel.Info, skipped));
                results.Add(new DlcBuildResult(entry, null, 0, skipped));
                continue;
            }

            string? dataFolder = null;
            if (!entry.NoExtraData)
            {
                dataFolder = entry.DataFolderIn(sourceFolder);
                log?.Invoke(dataFolder != null
                    ? new LogEntry(LogLevel.Info, Loc.F("Dlc.WithData", entry.ContentId, entry.DownloadStatus, dataFolder))
                    : new LogEntry(LogLevel.Warning, Loc.F("Dlc.NoDataFolder", entry.ContentId, entry.DownloadStatus, entry.MountPoint ?? "—")));
            }

            results.Add(BuildOne(entry, outputFolder, temporaryFolder, gameTitle, log, cancellationToken, dataFolder));
        }

        return results;
    }

    public static DlcBuildResult BuildOne(
        DlcEmuEntry entry,
        string outputFolder,
        string temporaryFolder,
        string? gameTitle,
        Action<LogEntry>? log,
        CancellationToken cancellationToken,
        string? dataFolder = null)
    {
        var titleId = ContentIdHelper.TitleIdOf(entry.ContentId);
        if (titleId == null)
        {
            var invalid = Loc.F("Dlc.BadContentId", entry.ContentId);
            log?.Invoke(new LogEntry(LogLevel.Error, invalid));
            return new DlcBuildResult(entry, null, 0, invalid);
        }

        var staging = Path.Combine(temporaryFolder, "dlc-" + entry.Label);
        SourceMirror? mirror = null;
        try
        {
            Directory.CreateDirectory(staging);
            var packageSource = staging;
            if (dataFolder != null)
            {
                // Dữ liệu riêng của DLC: thư viện đọc qua thư mục gương (liên kết), param.json do thư viện tạo nằm trong gương —
                // thư mục dữ liệu của người dùng không bị ghi thêm gì.
                mirror = SourceMirror.Create(dataFolder, temporaryFolder, new MirrorPlan(Array.Empty<string>(), new Dictionary<string, byte[]>(), new Dictionary<string, string>(), new[] { "sce_sys" }), log ?? (_ => { }), force: true)
                         ?? throw new IOException(Loc.F("Dlc.MirrorFailed", dataFolder));
                packageSource = mirror.Path;
            }

            var options = new ProsperoBuildOptions
            {
                Mode = ProsperoPackageMode.AdditionalContentData,
                OutputFormat = ProsperoOutputFormat.DebugImage,
                SourceFolder = packageSource,
                OutputFolder = outputFolder,
                TemporaryDirectory = temporaryFolder,
                ContentId = entry.ContentId,
                PrimaryId = entry.ContentId,
                TitleId = titleId,
                Title = string.IsNullOrWhiteSpace(gameTitle) ? entry.Label : gameTitle + " — " + entry.Label,
                Version = "01.00",
                Passcode = new string('0', 32),
                GenerateParamJsonIfMissing = true,
                DeterministicBuild = true,
                CancellationToken = cancellationToken,
            };

            var result = ProsperoPackageBuilder.Build(options, _ => { });
            var size = new FileInfo(result.OutputPath).Length;
            log?.Invoke(new LogEntry(LogLevel.Info, Loc.F("Dlc.Built", entry.ContentId, Formatters.Size(size))));
            return new DlcBuildResult(entry, result.OutputPath, size, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            log?.Invoke(new LogEntry(LogLevel.Error, Loc.F("Dlc.Failed", entry.ContentId, ex.Message)));
            return new DlcBuildResult(entry, null, 0, ex.Message);
        }
        finally
        {
            mirror?.Dispose();
            try
            {
                if (Directory.Exists(staging))
                {
                    Directory.Delete(staging, recursive: true);
                }
            }
            catch (Exception)
            {
            }
        }
    }
}
