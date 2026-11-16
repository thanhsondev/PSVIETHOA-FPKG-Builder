using CommunityToolkit.Mvvm.ComponentModel;
using PsViethoa.FpkgBuilder.Core.Localization;
using PsViethoa.FpkgBuilder.Core.Services;

namespace PsViethoa.FpkgBuilder.App.ViewModels;

/// <summary>Một ổ đĩa để xả / trích tệp vào (chip "C: 38 GB" ở ô thư mục xuất của Giải nén gói và FPKG trên PS5).</summary>
public sealed partial class DriveChoice : ObservableObject
{
    public required string Root { get; init; }

    /// <summary>"C:" trên Windows; tên ổ (thư mục trong /Volumes) trên macOS.</summary>
    public required string Letter { get; init; }

    /// <summary>"38,1 GB trống".</summary>
    public required string Detail { get; init; }

    /// <summary>Chỉ dung lượng trống, gọn cho chip một dòng: "38,1 GB".</summary>
    public required string Free { get; init; }

    public long FreeBytes { get; init; }

    public string Tip { get; init; } = string.Empty;

    [ObservableProperty] private bool _isSelected;

    public bool Matches(string? folder) =>
        !string.IsNullOrWhiteSpace(folder) && string.Equals(Trim(Root), Trim(folder), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static string Trim(string path) => path.Length > 1 ? path.TrimEnd('\\', '/') : path;

    /// <summary>
    /// Các ổ ghi được: Windows lấy ổ cố định / tháo rời / mạng; macOS ổ hệ thống và các ổ trong /Volumes; Linux "/" và /media, /mnt,
    /// /run/media (bỏ hàng chục điểm gắn hệ thống). Chạy ở luồng nền.
    /// <para>macOS / Linux: ổ hệ thống ("/") chỉ đọc (macOS) hoặc chỉ root ghi được (Linux), nên chip của nó trỏ vào thư mục người dùng
    /// (~) và mang tên ổ thật ("Macintosh HD") / "Thư mục nhà". Ổ gắn chỉ đọc (NTFS trên macOS, ảnh .dmg, CD) và ổ ẩn của macOS
    /// (Recovery, cờ nobrowse) bị bỏ — không trích được vào đó.</para>
    /// </summary>
    public static Task<List<DriveChoice>> ListAsync() => Task.Run(() =>
    {
        var list = new List<DriveChoice>();
        var mounts = OperatingSystem.IsWindows() ? null : MountOptions();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var drive in DiskSpaceAdvisor.AllDrives(fresh: true))
        {
            try
            {
                if (!drive.IsReady || drive.DriveType is not (DriveType.Fixed or DriveType.Removable or DriveType.Network))
                {
                    continue;
                }

                var root = drive.RootDirectory.FullName;
                var target = root;
                string letter;
                if (OperatingSystem.IsWindows())
                {
                    letter = drive.Name.TrimEnd('\\');
                }
                else if (root == "/")
                {
                    if (string.IsNullOrEmpty(home) || !Directory.Exists(home))
                    {
                        continue;
                    }

                    target = home;
                    letter = OperatingSystem.IsMacOS() ? MacSystemVolumeName() : Loc.T("Drive.Home");
                }
                else if (root.StartsWith("/Volumes/", StringComparison.Ordinal) || root.StartsWith("/media/", StringComparison.Ordinal)
                         || root.StartsWith("/mnt/", StringComparison.Ordinal) || root.StartsWith("/run/media/", StringComparison.Ordinal))
                {
                    if (mounts != null && mounts.TryGetValue(root.TrimEnd('/'), out var options) && Hidden(options))
                    {
                        continue;
                    }

                    letter = Path.GetFileName(root.TrimEnd('/'));
                }
                else
                {
                    continue;
                }

                var label = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? drive.DriveFormat : drive.VolumeLabel;
                var free = Formatters.Size(drive.AvailableFreeSpace);
                list.Add(new DriveChoice
                {
                    Root = target,
                    Letter = letter,
                    Free = free,
                    FreeBytes = drive.AvailableFreeSpace,
                    Detail = Loc.F("Drive.Free", free),
                    Tip = $"{target} · {label} · {drive.DriveFormat} · {free} / {Formatters.Size(drive.TotalSize)}",
                });
            }
            catch (Exception)
            {
            }
        }

        // Thử giao diện máy nhiều ổ: PSVIETHOA_FAKE_DRIVES=N thêm N ổ ảo (chỉ dùng khi chạy thử).
        if (int.TryParse(Environment.GetEnvironmentVariable("PSVIETHOA_FAKE_DRIVES"), out var fake) && fake > 0)
        {
            var rnd = new Random(7);
            var used = list.Select(d => d.Letter).ToHashSet(StringComparer.OrdinalIgnoreCase);
            for (char ch = 'E'; ch <= 'Z' && fake > 0; ch++)
            {
                if (used.Contains(ch + ":")) continue;
                var root = Path.Combine(Path.GetTempPath(), "psvh-fake-drives", ch.ToString());
                Directory.CreateDirectory(root);
                long freeBytes = (long)(rnd.Next(20, 3800) * 1e9);
                var freeText = Formatters.Size(freeBytes);
                list.Add(new DriveChoice { Root = root, Letter = ch + ":", Free = freeText, FreeBytes = freeBytes, Detail = Loc.F("Drive.Free", freeText), Tip = root + " (ổ ảo để thử giao diện)" });
                fake--;
            }
        }

        return list;
    });

    /// <summary>Ổ chỉ đọc hoặc ẩn (macOS: Recovery, Preboot… có cờ nobrowse): không hiện thành chip.</summary>
    private static bool Hidden(IReadOnlyCollection<string> options) =>
        options.Contains("ro") || options.Contains("read-only") || options.Contains("nobrowse");

    /// <summary>
    /// Điểm gắn → tuỳ chọn gắn. Linux đọc /proc/mounts ("ro", "rw"…; khoảng trắng viết \040); macOS chạy <c>/sbin/mount</c>
    /// ("/dev/disk3s3 on /Volumes/Recovery (apfs, local, journaled, nobrowse)"). Lỗi thì trả rỗng (không lọc gì).
    /// </summary>
    private static Dictionary<string, HashSet<string>> MountOptions()
    {
        var result = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        try
        {
            if (OperatingSystem.IsLinux() && File.Exists("/proc/mounts"))
            {
                foreach (var line in File.ReadLines("/proc/mounts"))
                {
                    var parts = line.Split(' ');
                    if (parts.Length >= 4)
                    {
                        var point = parts[1].Replace("\\040", " ", StringComparison.Ordinal).TrimEnd('/');
                        result[point] = parts[3].Split(',').ToHashSet(StringComparer.Ordinal);
                    }
                }
            }
            else if (OperatingSystem.IsMacOS() && File.Exists("/sbin/mount"))
            {
                using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("/sbin/mount")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
                if (process == null)
                {
                    return result;
                }

                var output = process.StandardOutput.ReadToEnd();
                process.WaitForExit(3000);
                foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    int on = line.IndexOf(" on ", StringComparison.Ordinal), open = line.LastIndexOf(" (", StringComparison.Ordinal);
                    if (on > 0 && open > on && line.EndsWith(')'))
                    {
                        var point = line[(on + 4)..open].TrimEnd('/');
                        result[point] = line[(open + 2)..^1].Split(',', StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal);
                    }
                }
            }
        }
        catch (Exception)
        {
        }

        return result;
    }

    /// <summary>Tên ổ hệ thống của macOS như Finder hiện ("Macintosh HD"): liên kết trong /Volumes trỏ về "/".</summary>
    private static string MacSystemVolumeName()
    {
        try
        {
            foreach (var entry in new DirectoryInfo("/Volumes").EnumerateFileSystemInfos())
            {
                if (entry.LinkTarget is "/")
                {
                    return entry.Name;
                }
            }
        }
        catch (Exception)
        {
        }

        return "Macintosh HD";
    }
}
