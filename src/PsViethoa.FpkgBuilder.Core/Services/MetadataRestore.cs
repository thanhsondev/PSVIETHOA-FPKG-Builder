using LibProsperoPkg.PKG;
using PsViethoa.FpkgBuilder.Core.Localization;
using PsViethoa.FpkgBuilder.Core.Models;

namespace PsViethoa.FpkgBuilder.Core.Services;

/// <summary>
/// Metadata khôi phục online của một lượt tạo gói: thư mục làm việc trong thư mục tạm chứa bản sao <c>sce_sys</c> của nguồn đã được
/// khôi phục (<see cref="Root"/>/sce_sys/…) và danh sách tệp thật sự đổi. Khi đóng gói, các tệp này đè lên (hoặc thêm vào) sce_sys của
/// nguồn — nguồn không bao giờ bị ghi. Dispose xoá thư mục làm việc (kể cả gói _sc.pkg đã tải).
/// </summary>
public sealed class MetadataOverlay : IDisposable
{
    private readonly string _workspace;

    internal MetadataOverlay(string workspace, string root, IReadOnlyList<string> files)
    {
        _workspace = workspace;
        Root = root;
        Files = files;
    }

    /// <summary>Thư mục giữ vai trò thư mục ứng dụng (chứa sce_sys đầy đủ: bản sao của nguồn + tệp đã khôi phục).</summary>
    public string Root { get; }

    /// <summary>sce_sys đầy đủ sau khi khôi phục.</summary>
    public string SceSysFolder => Path.Combine(Root, "sce_sys");

    /// <summary>Tệp đã khôi phục, đường dẫn trong gói ("sce_sys/param.json"…).</summary>
    public IReadOnlyList<string> Files { get; }

    /// <summary>Tệp đã khôi phục <paramref name="relative"/> nằm ở đâu trên đĩa.</summary>
    public string FullPath(string relative) => Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>Có khôi phục tệp này không (không phân biệt hoa thường).</summary>
    public bool Contains(string relative) => Files.Contains(relative.Replace('\\', '/'), StringComparer.OrdinalIgnoreCase);

    public void Dispose() => BuildEngine.TryDeleteDirectory(_workspace);
}

/// <summary>
/// Tuỳ chọn "Khôi phục metadata online" (engine fpkg-gui 0.6.12, <see cref="ProsperoMetadataRecovery"/>): tải đúng mảnh metadata
/// <c>*_sc.pkg</c> của Content ID và phiên bản của bản dump từ máy chủ Sony (theo <c>versionFileUri</c> trong param.json, hoặc URL
/// người dùng nhập), kiểm tra SHA-256 của manifest, Content ID, phiên bản, titleId, rồi khôi phục các tệp sce_sys mà bản dump làm mất
/// hay làm hỏng: param.json (giữ sdkVersion, requiredSystemSoftwareVersion, applicationDrmType của bản dump), icon/ảnh, bảng PlayGo
/// (chunk thoại theo ngôn ngữ), playgo-scenario.json, changeinfo, npbind, trophy2/uds UCP khi bản trên máy chủ không bị niêm phong.
/// <para>
/// Thư viện ghi thẳng vào thư mục nó được trỏ tới (kèm bản lưu cạnh thư mục đó), nên công cụ chép sce_sys của nguồn vào thư mục tạm
/// và cho thư viện làm việc trên bản sao — thư mục game của người dùng chỉ được đọc. Lỗi mạng hay máy chủ không có đúng phiên bản thì
/// chỉ cảnh báo: gói vẫn được tạo bằng metadata của bản dump.
/// </para>
/// </summary>
public static class MetadataRestore
{
    /// <summary>sce_sys lớn hơn mức này thì không chép (chắc chắn không phải metadata thông thường).</summary>
    public const long MaxSceSysBytes = 2L * 1024 * 1024 * 1024;

    /// <summary>
    /// Tạo HttpClient cho thư viện; null = HttpClient ghim CA của Sony do thư viện tự tạo. Chỉ kiểm thử thay giá trị này (máy chủ giả);
    /// mã của ứng dụng không bao giờ đặt.
    /// </summary>
    public static Func<HttpClient?> ClientFactory { get; set; } = () => null;

    /// <summary>
    /// Khôi phục metadata cho thư mục ứng dụng <paramref name="appFolder"/> (chỉ đọc). Trả về null khi không khôi phục được gì (đã ghi
    /// nhật ký lý do). Huỷ thì ném OperationCanceledException như mọi pha khác.
    /// </summary>
    public static async Task<MetadataOverlay?> PrepareAsync(string appFolder, string temporaryFolder, string? sourceUrl, Action<LogEntry> log, CancellationToken cancellationToken)
    {
        var sceSys = Path.Combine(appFolder, "sce_sys");
        if (!File.Exists(Path.Combine(sceSys, "param.json")))
        {
            log(new LogEntry(LogLevel.Warning, Loc.T("Meta.NoParam")));
            return null;
        }

        var workspace = Path.Combine(temporaryFolder, "metadata-" + Guid.NewGuid().ToString("N")[..12]);
        var root = Path.Combine(workspace, "game");
        var completed = false;
        try
        {
            // Thư viện chỉ kiểm tra liên kết từ tệp đích lên tới thư mục làm việc, nên liên kết phía trên (/var → /private/var của
            // macOS) không ảnh hưởng; bản lưu và gói _sc.pkg nó tải về nằm cạnh "game", tức vẫn trong thư mục làm việc.
            Directory.CreateDirectory(workspace);
            var copied = CopySceSys(sceSys, Path.Combine(root, "sce_sys"), cancellationToken);
            log(new LogEntry(LogLevel.Info, Loc.F("Meta.Start", string.IsNullOrWhiteSpace(sourceUrl) ? "versionFileUri" : sourceUrl.Trim(), copied)));

            var client = ClientFactory();
            var result = await ProsperoMetadataRecovery.RecoverAsync(
                new ProsperoMetadataRecoveryOptions
                {
                    SourceFolder = root,
                    SourceUrl = string.IsNullOrWhiteSpace(sourceUrl) ? null : sourceUrl.Trim(),
                    // Giữ sdkVersion / requiredSystemSoftwareVersion / applicationDrmType của bản dump (mặc định của fpkg-gui).
                    RestoreOriginalRuntimeSettings = false,
                },
                message => log(LogEntry.FromLibrary(message)),
                cancellationToken,
                client).ConfigureAwait(false);

            var restored = result.RestoredFiles.Select(path => path.Replace('\\', '/')).ToList();
            if (result.SkippedFiles.Count > 0)
            {
                log(new LogEntry(LogLevel.Info, Loc.F("Meta.Skipped", result.SkippedFiles.Count, string.Join("; ", result.SkippedFiles))));
            }

            if (restored.Count == 0)
            {
                log(new LogEntry(LogLevel.Info, Loc.T("Meta.NothingChanged")));
                return null;
            }

            log(new LogEntry(LogLevel.Success, Loc.F("Meta.Restored", restored.Count, string.Join(", ", restored))));
            completed = true;
            return new MetadataOverlay(workspace, root, restored);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException or
                                       System.Text.Json.JsonException or System.Xml.XmlException or UriFormatException or TimeoutException or
                                       OperationCanceledException or InvalidOperationException or ArgumentException or
                                       System.Security.Authentication.AuthenticationException or System.Security.Cryptography.CryptographicException)
        {
            // HttpClient hết thời gian chờ cũng ném TaskCanceledException (OperationCanceledException) dù không ai huỷ.
            log(new LogEntry(LogLevel.Warning, Loc.F("Meta.Failed", ex.Message)));
            return null;
        }
        finally
        {
            if (!completed)
            {
                BuildEngine.TryDeleteDirectory(workspace);
            }
        }
    }

    /// <summary>Chép sce_sys (bỏ tệp/thư mục rác, không đi theo liên kết) và trả về số tệp đã chép.</summary>
    private static int CopySceSys(string source, string target, CancellationToken cancellationToken)
    {
        var count = 0;
        long total = 0;
        foreach (var file in new DirectoryInfo(source).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(source, file.FullName);
            if (JunkFileFinder.IsJunkFileName(file.Name) || relative.Split(Path.DirectorySeparatorChar).SkipLast(1).Any(part => JunkFileFinder.IsJunkDirectoryName(part)))
            {
                continue;
            }

            total += file.Length;
            if (total > MaxSceSysBytes)
            {
                throw new InvalidDataException(Loc.F("Meta.SceSysTooLarge", Formatters.Size(MaxSceSysBytes)));
            }

            var destination = Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file.FullName, destination, overwrite: false);
            count++;
        }

        return count;
    }
}
