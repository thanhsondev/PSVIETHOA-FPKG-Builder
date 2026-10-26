using PsViethoa.FpkgBuilder.Core.ExFat;

namespace PsViethoa.FpkgBuilder.Core.Services;

/// <summary>Dấu vết DLC emu (drakmor/dlc_emu) trong nguồn: tệp cấu hình dlc_emu.ini và các module thay thế trong fakelib.</summary>
public sealed record DlcEmuInfo(bool Config, IReadOnlyList<string> Paths)
{
    public static readonly DlcEmuInfo Empty = new(false, Array.Empty<string>());

    public bool Present => Paths.Count > 0;
}

/// <summary>
/// Tìm bộ giả lập DLC của Drakmor trong nguồn: <c>dlc_emu.ini</c> ở gốc ứng dụng và các module thay thế
/// (<c>libSceAppContent</c>, <c>libSceNpEntitlementAccess</c>, <c>libSceGameUpdate</c>) trong <c>fakelib</c>/<c>fakelib2</c>.
/// <para>
/// Bộ này mở khoá DLC cho bản dump chạy qua ShadowMount+ (SM+ gắn fakelib vào common/lib của sandbox). Khi cài từ gói thì
/// không có SM+ làm việc đó; cộng đồng báo một số game (Stellar Blade) đứng ở màn hình splash cho tới khi xoá các tệp này.
/// Ứng dụng phát hiện để cảnh báo và cho phép loại khỏi gói khi người dùng chọn.
/// </para>
/// </summary>
public static partial class DlcEmuInspector
{
    public const string ConfigName = "dlc_emu.ini";

    private static readonly string[] FakeLibFolders = { "fakelib", "fakelib2" };

    private static readonly string[] ModuleNames =
    {
        "libSceAppContent",
        "libSceNpEntitlementAccess",
        "libSceGameUpdate",
    };

    private static readonly string[] ModuleExtensions = { ".sprx", ".prx" };

    /// <summary>Các đường dẫn (tương đối thư mục ứng dụng) bị loại khỏi gói khi bỏ DLC emu.</summary>
    public static IEnumerable<string> Candidates()
    {
        yield return ConfigName;
        foreach (var folder in FakeLibFolders)
        {
            foreach (var module in ModuleNames)
            {
                foreach (var extension in ModuleExtensions)
                {
                    yield return folder + "/" + module + extension;
                }
            }
        }
    }

    public static DlcEmuInfo ScanFolder(string sourceFolder, CancellationToken cancellationToken)
    {
        try
        {
            var found = new List<string>();
            var config = false;
            foreach (var relative in Candidates())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!File.Exists(Path.Combine(sourceFolder, relative.Replace('/', Path.DirectorySeparatorChar))))
                {
                    continue;
                }

                found.Add(relative);
                config |= relative == ConfigName;
            }

            return found.Count == 0 ? DlcEmuInfo.Empty : new DlcEmuInfo(config, found);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return DlcEmuInfo.Empty;
        }
    }

    public static DlcEmuInfo ScanImage(ExFatImage image, ExFatEntry appRoot, CancellationToken cancellationToken)
    {
        try
        {
            var found = new List<string>();
            var config = false;
            foreach (var relative in Candidates())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = image.Find(relative, appRoot);
                if (entry == null || entry.IsDirectory)
                {
                    continue;
                }

                found.Add(relative);
                config |= relative == ConfigName;
            }

            return found.Count == 0 ? DlcEmuInfo.Empty : new DlcEmuInfo(config, found);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            return DlcEmuInfo.Empty;
        }
    }

    public static string Describe(DlcEmuInfo info) => info.Present ? string.Join(", ", info.Paths) : "—";

    /// <summary>
    /// Thư mục gắn DLC (<c>mount_point</c> dưới <c>/app0</c> trong dlc_emu.ini) của nguồn. DLC-emu không tự tạo thư mục này (README của
    /// drakmor/dlc_emu: "DLC-emu does not create the directory") nên nó phải có trong gói dù rỗng — bản dump thường để rỗng vì nội dung
    /// DLC đã nằm sẵn trong game. <paramref name="overlayFolder"/>: thư mục update của bản vá (ini của nó được ưu tiên, thư mục tìm ở cả
    /// hai bên). Đường dẫn trả về dùng đúng tên trên đĩa (a/b).
    /// </summary>
    public static DlcEmuMounts FindMounts(string appFolder, string? overlayFolder = null)
    {
        var iniFolder = overlayFolder != null && File.Exists(Path.Combine(overlayFolder, ConfigName)) ? overlayFolder : appFolder;
        var existing = new List<string>();
        var missing = new List<string>();
        foreach (var entry in DlcEmuIni.Read(iniFolder, CancellationToken.None))
        {
            if (RelativeMountPoint(entry.MountPoint) is not { } relative)
            {
                continue;
            }

            var actual = ResolveDirectory(appFolder, relative) ?? (overlayFolder == null ? null : ResolveDirectory(overlayFolder, relative));
            var list = actual == null ? missing : existing;
            var path = actual ?? relative;
            if (!list.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                list.Add(path);
            }
        }

        // Mục gắn được không ghi mount_point thì DLC-emu dùng mặc định /app0/addcontN: giữ mọi thư mục addcontN ở gốc game.
        foreach (var folder in new[] { appFolder, overlayFolder })
        {
            if (folder == null || !Directory.Exists(folder))
            {
                continue;
            }

            try
            {
                foreach (var directory in new DirectoryInfo(folder).EnumerateDirectories())
                {
                    if (DefaultMountName().IsMatch(directory.Name) && !existing.Contains(directory.Name, StringComparer.OrdinalIgnoreCase))
                    {
                        existing.Add(directory.Name);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
            }
        }

        return new DlcEmuMounts(existing, missing);
    }

    [System.Text.RegularExpressions.GeneratedRegex("^addcont[0-9]+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex DefaultMountName();

    /// <summary><c>/app0/addcont0</c> → <c>addcont0</c>; null khi không nằm dưới /app0 hoặc không hợp lệ (rỗng, "..", ký tự lạ).</summary>
    public static string? RelativeMountPoint(string? mountPoint)
    {
        if (string.IsNullOrWhiteSpace(mountPoint))
        {
            return null;
        }

        var mount = mountPoint.Trim().Replace('\\', '/').TrimEnd('/');
        const string prefix = "/app0/";
        if (!mount.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var parts = mount[prefix.Length..].Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 0 || parts.Any(part => part is "." or ".." || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            ? null
            : string.Join('/', parts);
    }

    /// <summary>Thư mục <paramref name="relative"/> trong <paramref name="root"/> với đúng hoa thường trên đĩa; null khi không có.</summary>
    private static string? ResolveDirectory(string root, string relative)
    {
        try
        {
            var current = new DirectoryInfo(root);
            var names = new List<string>();
            foreach (var part in relative.Split('/'))
            {
                var children = current.EnumerateDirectories().ToList();
                var match = children.FirstOrDefault(child => child.Name == part) ??
                            children.FirstOrDefault(child => string.Equals(child.Name, part, StringComparison.OrdinalIgnoreCase));
                if (match == null)
                {
                    return null;
                }

                names.Add(match.Name);
                current = match;
            }

            return string.Join('/', names);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }
}

/// <summary>Thư mục gắn DLC của dlc_emu.ini: có trong nguồn (<paramref name="Existing"/>) và bị thiếu (<paramref name="Missing"/>).</summary>
public sealed record DlcEmuMounts(IReadOnlyList<string> Existing, IReadOnlyList<string> Missing)
{
    public static readonly DlcEmuMounts None = new(Array.Empty<string>(), Array.Empty<string>());
}
