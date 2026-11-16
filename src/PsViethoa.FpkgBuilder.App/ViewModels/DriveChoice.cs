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

    public long TotalBytes { get; init; }

    /// <summary>Nhãn ổ (vd "DATA", "PS5"); trống thì hiện định dạng (NTFS, exFAT…).</summary>
    public string Label { get; init; } = string.Empty;

    /// <summary>"DATA (D:)" như This PC của Windows; trên macOS/Linux chỉ tên ổ.</summary>
    public string Title => OperatingSystem.IsWindows() && Label.Length > 0 ? $"{Label} ({Letter})" : Letter;

    /// <summary>"171 GB trống / 1,8 TB".</summary>
    public string Capacity { get; init; } = string.Empty;

    /// <summary>Phần trăm đã dùng (0..100) cho thanh mức đầy.</summary>
    public double UsedPercent => TotalBytes > 0 ? Math.Clamp(100.0 * (TotalBytes - FreeBytes) / TotalBytes, 0, 100) : 0;

    /// <summary>Màu thanh như Explorer: xanh dương; đỏ khi đã dùng trên 90 % (sắp đầy).</summary>
    public Avalonia.Media.IBrush UsageBrush => UsedPercent > 90 ? Danger : Normal;

    private static readonly Avalonia.Media.IBrush Normal = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#26A0DA"));
    private static readonly Avalonia.Media.IBrush Danger = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#DA2626"));

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
                    TotalBytes = drive.TotalSize,
                    Label = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? drive.DriveFormat : drive.VolumeLabel.Trim(),
                    Capacity = Loc.F("Drive.Capacity", free, Formatters.Size(drive.TotalSize)),
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
