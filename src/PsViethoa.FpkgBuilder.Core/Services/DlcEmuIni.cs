using PsViethoa.FpkgBuilder.Core.ExFat;

namespace PsViethoa.FpkgBuilder.Core.Services;

/// <summary>Một mục DLC trong dlc_emu.ini.</summary>
/// <param name="MountPoint">mount_point của khối (ví dụ <c>/app0/addcont0</c>): nơi game tìm dữ liệu riêng của DLC, null khi không ghi.</param>
/// <param name="Section">Tên khối trong ini (PSAC = additional content; PSAL/PSCONS… là loại khác, không đóng thành gói DLC).</param>
public sealed record DlcEmuEntry(string ContentId, string DownloadStatus, string? MountPoint = null, string Section = "PSAC")
{
    /// <summary>Khối additional content (hoặc ini cũ không ghi tên khối).</summary>
    public bool IsAdditionalContent => Section.Length == 0 || Section.Equals("PSAC", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Thư mục dữ liệu riêng của DLC trong bản dump: mount_point <c>/app0/xyz</c> → <c>&lt;nguồn&gt;/xyz</c>. Null khi không có
    /// mount_point dưới /app0, thư mục không tồn tại hoặc rỗng.
    /// </summary>
    public string? DataFolderIn(string? sourceFolder)
    {
        if (string.IsNullOrWhiteSpace(sourceFolder) || string.IsNullOrWhiteSpace(MountPoint))
        {
            return null;
        }

        var mount = MountPoint.Trim().Replace('\\', '/').TrimEnd('/');
        const string prefix = "/app0/";
        if (!mount.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || mount.Length == prefix.Length || mount.Contains("..", StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            var folder = Path.GetFullPath(Path.Combine(sourceFolder, mount[prefix.Length..].Replace('/', Path.DirectorySeparatorChar)));
            return Directory.Exists(folder) && Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Any() ? folder : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>DLC chỉ cần quyền sở hữu, không có dữ liệu kèm theo — tạo được gói DLC rỗng để cài.</summary>
    public bool NoExtraData => string.Equals(DownloadStatus, "NO_EXTRA_DATA", StringComparison.OrdinalIgnoreCase);

    /// <summary>Phần đuôi của Content ID (nhãn ngắn để đặt tên gói).</summary>
    public string Label => ContentId.Length > 20 ? ContentId[20..] : ContentId;
}

/// <summary>
/// Đọc <c>dlc_emu.ini</c> của một game (mỗi game một danh sách riêng). Định dạng của drakmor/dlc_emu: nhiều khối
/// <c>[PSAC]</c>, mỗi khối có <c>content_id</c> và <c>download_status</c>.
/// </summary>
public static class DlcEmuIni
{
    private const long MaxBytes = 64 * 1024;

    /// <summary>Đọc từ thư mục nguồn hoặc ảnh (.exfat/.ffpfsc); trả về danh sách rỗng khi không có tệp.</summary>
    public static IReadOnlyList<DlcEmuEntry> Read(string sourcePath, CancellationToken cancellationToken)
    {
        try
        {
            if (SourceLocator.Detect(sourcePath) == SourceKind.ExFatImage)
            {
                using var image = ExFatImage.Open(sourcePath);
                var source = SourceLocator.Resolve(sourcePath);
                var appRoot = SourceLocator.ResolveAppRoot(image, source);
                var entry = image.Find(DlcEmuInspector.ConfigName, appRoot);
                if (entry == null || entry.IsDirectory || entry.Length > MaxBytes)
                {
                    return Array.Empty<DlcEmuEntry>();
                }

                using var stream = image.OpenRead(entry);
                return Parse(new StreamReader(stream).ReadToEnd());
            }

            var path = File.Exists(sourcePath) && sourcePath.EndsWith(".ini", StringComparison.OrdinalIgnoreCase)
                ? sourcePath
                : Path.Combine(sourcePath, DlcEmuInspector.ConfigName);
            return File.Exists(path) ? Parse(File.ReadAllText(path)) : Array.Empty<DlcEmuEntry>();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return Array.Empty<DlcEmuEntry>();
        }
    }

    /// <summary>Phân tích nội dung ini (bỏ qua khối thiếu content_id, bỏ trùng lặp, giữ nguyên thứ tự).</summary>
    public static IReadOnlyList<DlcEmuEntry> Parse(string text)
    {
        var entries = new List<DlcEmuEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? contentId = null;
        var status = string.Empty;
        string? mountPoint = null;
        var section = string.Empty;

        void Flush()
        {
            if (!string.IsNullOrWhiteSpace(contentId) && seen.Add(contentId))
            {
                entries.Add(new DlcEmuEntry(contentId.Trim(), status.Trim(), string.IsNullOrWhiteSpace(mountPoint) ? null : mountPoint.Trim(), section));
            }

            contentId = null;
            status = string.Empty;
            mountPoint = null;
        }

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] is ';' or '#')
            {
                continue;
            }

            if (line[0] == '[')
            {
                Flush();
                section = line.Trim('[', ']', ' ');
                continue;
            }

            var eq = line.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }

            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();
            if (key.Equals("content_id", StringComparison.OrdinalIgnoreCase))
            {
                contentId = value;
            }
            else if (key.Equals("download_status", StringComparison.OrdinalIgnoreCase))
            {
                status = value;
            }
            else if (key.Equals("mount_point", StringComparison.OrdinalIgnoreCase))
            {
                mountPoint = value;
            }
        }

        Flush();
        return entries;
    }
}
