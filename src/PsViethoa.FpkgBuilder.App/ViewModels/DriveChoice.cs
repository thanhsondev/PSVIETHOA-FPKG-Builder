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
    /// Các ổ ghi được: Windows lấy ổ cố định / tháo rời / mạng; macOS chỉ "/" và các ổ trong /Volumes; Linux "/" và /media, /mnt, /run/media
    /// (bỏ hàng chục điểm gắn hệ thống). Chạy ở luồng nền.
    /// </summary>
    public static Task<List<DriveChoice>> ListAsync() => Task.Run(() =>
    {
        var list = new List<DriveChoice>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady || drive.DriveType is not (DriveType.Fixed or DriveType.Removable or DriveType.Network))
                {
                    continue;
                }

                var root = drive.RootDirectory.FullName;
                string letter;
                if (OperatingSystem.IsWindows())
                {
                    letter = drive.Name.TrimEnd('\\');
                }
                else if (root == "/")
                {
                    letter = "/";
                }
                else if (root.StartsWith("/Volumes/", StringComparison.Ordinal) || root.StartsWith("/media/", StringComparison.Ordinal)
                         || root.StartsWith("/mnt/", StringComparison.Ordinal) || root.StartsWith("/run/media/", StringComparison.Ordinal))
                {
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
                    Root = root,
                    Letter = letter,
                    Free = free,
                    FreeBytes = drive.AvailableFreeSpace,
                    Detail = Loc.F("Drive.Free", free),
                    Tip = $"{root} · {label} · {drive.DriveFormat} · {free} / {Formatters.Size(drive.TotalSize)}",
                });
            }
            catch (Exception)
            {
            }
        }

        return list;
    });
}
