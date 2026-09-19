using System.Runtime.InteropServices;
using PsViethoa.FpkgBuilder.Core.Models;

namespace PsViethoa.FpkgBuilder.Core.Services;

/// <summary>Ước lượng dung lượng trống cần thiết và xác định ổ đĩa của đường dẫn.</summary>
public static partial class DiskSpaceAdvisor
{
    /// <summary>
    /// Ảnh trung gian ở đỉnh xấp xỉ dung lượng ảnh trong đã nén (đo thực tế ≈ 0.63–1.0 lần nguồn tuỳ dữ liệu);
    /// dự phòng 1.1 lần cho trường hợp dữ liệu không nén được.
    /// </summary>
    public const double TemporaryFactor = 1.1;

    /// <summary>Tệp .pkg cuối cùng nhỏ hơn hoặc xấp xỉ dung lượng nguồn (dự phòng 1.1 lần).</summary>
    public const double OutputFactor = 1.1;

    public sealed record VolumeInfo(string MountPoint, long FreeBytes, long TotalBytes);

    /// <summary>Dữ liệu giải nén từ ảnh exFAT (nếu có) chiếm thêm dung lượng trên ổ tạm.</summary>
    public const double StagingFactor = 1.02;

    /// <param name="sonySdk">
    /// Tạo bằng SDK Sony: Publishing Tools ghi thẳng gói vào thư mục xuất, không có ảnh trung gian trong thư mục tạm — ổ tạm chỉ
    /// cần chỗ cho bản giải nén ảnh (nếu có).
    /// </param>
    public static DiskSpaceReport Check(string outputFolder, string temporaryFolder, long sourceBytes, long stagingBytes = 0, bool sonySdk = false)
    {
        var output = Probe(outputFolder);
        var temporary = Probe(temporaryFolder);
        var needTemporary = (sonySdk ? 0 : (long)(sourceBytes * TemporaryFactor)) + (long)(stagingBytes * StagingFactor);
        var needOutput = (long)(sourceBytes * OutputFactor);

        var sameVolume = output != null && temporary != null &&
                         string.Equals(output.MountPoint, temporary.MountPoint, PathComparison);

        bool sufficient;
        string summary;
        if (output == null || temporary == null)
        {
            sufficient = true;
            summary = Localization.Loc.T("Disk.Unknown");
        }
        else if (sameVolume)
        {
            var need = needTemporary + needOutput;
            sufficient = output.FreeBytes >= need;
            summary = Localization.Loc.F(sufficient ? "Disk.SameOk" : "Disk.SameLow", output.MountPoint, Formatters.Size(output.FreeBytes), Formatters.Size(need));
        }
        else
        {
            var okTemp = temporary.FreeBytes >= needTemporary;
            var okOut = output.FreeBytes >= needOutput;
            sufficient = okTemp && okOut;
            summary = sufficient
                ? Localization.Loc.F("Disk.SplitOk", temporary.MountPoint, Formatters.Size(temporary.FreeBytes), Formatters.Size(needTemporary), output.MountPoint, Formatters.Size(output.FreeBytes), Formatters.Size(needOutput))
                : !okTemp
                    ? Localization.Loc.F("Disk.TempLow", temporary.MountPoint, Formatters.Size(temporary.FreeBytes), Formatters.Size(needTemporary))
                    : Localization.Loc.F("Disk.OutputLow", output.MountPoint, Formatters.Size(output.FreeBytes), Formatters.Size(needOutput));
        }

        // FAT/FAT32 không chứa được tệp từ 4 GiB: ảnh trong ở thư mục tạm và tệp .pkg của game thật hầu như luôn vượt mức đó.
        var fatVolumes = new[] { temporary?.MountPoint, output?.MountPoint }
            .Where(mount => mount != null && IsFat(mount))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (fatVolumes.Length > 0)
        {
            summary += " " + Localization.Loc.F("Disk.Fat", string.Join(", ", fatVolumes!));
            if (sourceBytes >= FatMaxFileBytes)
            {
                sufficient = false;
            }
        }

        return new DiskSpaceReport(
            output?.MountPoint ?? "?",
            output?.FreeBytes ?? -1,
            temporary?.MountPoint ?? "?",
            temporary?.FreeBytes ?? -1,
            needTemporary,
            needOutput,
            sameVolume,
            sufficient,
            summary);
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>Tìm điểm gắn (mount point) chứa đường dẫn – dùng để so sánh "cùng ổ đĩa".</summary>
    public static string? ResolveMountPoint(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            string? best = null;
            foreach (var drive in SafeDrives())
            {
                var root = drive.Name;
                if (IsPrefix(root, full) && (best == null || root.Length > best.Length))
                {
                    best = root;
                }
            }

            return best ?? Path.GetPathRoot(full);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Tệp lớn nhất FAT32 chứa được (4 GiB − 1 byte).</summary>
    public const long FatMaxFileBytes = 4L * 1024 * 1024 * 1024 - 1;

    /// <summary>Tên hệ thống tệp của ổ chứa đường dẫn ("NTFS", "exFAT", "FAT32", "apfs", "msdos"…; null nếu không đọc được).</summary>
    public static string? FileSystemOf(string path)
    {
        try
        {
            var mount = ResolveMountPoint(ExistingAncestor(path));
            return mount == null ? null : new DriveInfo(mount).DriveFormat;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Ổ FAT/FAT32 (Windows "FAT32", macOS "msdos") — giới hạn mỗi tệp dưới 4 GiB.</summary>
    public static bool IsFat(string path) =>
        FileSystemOf(path) is { } format &&
        (format.StartsWith("FAT", StringComparison.OrdinalIgnoreCase) || format.Equals("msdos", StringComparison.OrdinalIgnoreCase) || format.Equals("vfat", StringComparison.OrdinalIgnoreCase));

    private static string ExistingAncestor(string path)
    {
        var existing = Path.GetFullPath(path);
        while (!Directory.Exists(existing))
        {
            var parent = Path.GetDirectoryName(existing);
            if (string.IsNullOrEmpty(parent) || parent == existing)
            {
                break;
            }

            existing = parent;
        }

        return existing;
    }

    public static bool IsSameVolume(string a, string b)
    {
        var first = ResolveMountPoint(a);
        var second = ResolveMountPoint(b);
        return first != null && second != null && string.Equals(first, second, PathComparison);
    }

    /// <summary>Đọc dung lượng trống của ổ đĩa chứa đường dẫn (đường dẫn có thể chưa tồn tại).</summary>
    public static VolumeInfo? Probe(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            var full = Path.GetFullPath(path);
            var existing = full;
            while (!Directory.Exists(existing))
            {
                var parent = Path.GetDirectoryName(existing);
                if (string.IsNullOrEmpty(parent) || parent == existing)
                {
                    break;
                }

                existing = parent;
            }

            var mount = ResolveMountPoint(existing) ?? existing;

            if (OperatingSystem.IsWindows() && TryGetWindowsFreeSpace(existing, out var free, out var total))
            {
                return new VolumeInfo(mount, free, total);
            }

            var drive = new DriveInfo(existing);
            return new VolumeInfo(mount, drive.AvailableFreeSpace, drive.TotalSize);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static readonly object DrivesGate = new();
    private static DriveInfo[]? _drivesCache;
    private static long _drivesCacheAt;

    /// <summary>
    /// DriveInfo.GetDrives() tuần tự hoá + nhớ 2 giây. Trên macOS .NET gọi getmntinfo(), hàm dùng chung một vùng đệm tĩnh: hai luồng
    /// gọi cùng lúc (hàng chờ chạy song song nhiều game) làm tiến trình sập với AccessViolationException — lỗi không bắt được.
    /// </summary>
    internal static DriveInfo[] AllDrives(bool fresh = false)
    {
        lock (DrivesGate)
        {
            var now = Environment.TickCount64;
            if (fresh || _drivesCache == null || now - _drivesCacheAt > 2000)
            {
                _drivesCache = DriveInfo.GetDrives();
                _drivesCacheAt = now;
            }

            return _drivesCache;
        }
    }

    private static IEnumerable<DriveInfo> SafeDrives()
    {
        DriveInfo[] drives;
        try
        {
            drives = AllDrives();
        }
        catch (Exception)
        {
            yield break;
        }

        foreach (var drive in drives)
        {
            bool ready;
            try
            {
                ready = drive.IsReady;
            }
            catch (Exception)
            {
                ready = false;
            }

            if (ready)
            {
                yield return drive;
            }
        }
    }

    private static bool IsPrefix(string root, string fullPath)
    {
        var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (normalizedRoot.Length == 0)
        {
            return true; // "/" chứa mọi đường dẫn
        }

        if (!fullPath.StartsWith(normalizedRoot, PathComparison))
        {
            return false;
        }

        return fullPath.Length == normalizedRoot.Length ||
               fullPath[normalizedRoot.Length] == Path.DirectorySeparatorChar ||
               fullPath[normalizedRoot.Length] == Path.AltDirectorySeparatorChar;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetDiskFreeSpaceEx(string directory, out ulong freeBytesAvailable, out ulong totalBytes, out ulong totalFreeBytes);

    private static bool TryGetWindowsFreeSpace(string directory, out long free, out long total)
    {
        free = 0;
        total = 0;
        try
        {
            if (GetDiskFreeSpaceEx(directory, out var available, out var totalBytes, out _))
            {
                free = (long)Math.Min(available, long.MaxValue);
                total = (long)Math.Min(totalBytes, long.MaxValue);
                return true;
            }
        }
        catch (Exception)
        {
        }

        return false;
    }
}
