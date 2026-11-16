using CommunityToolkit.Mvvm.Input;
using PsViethoa.FpkgBuilder.App.Services;
using PsViethoa.FpkgBuilder.Core.Localization;

namespace PsViethoa.FpkgBuilder.App.ViewModels;

/// <summary>Giáng sinh (nút Noel / Nhạc ở thanh bên) và nút ủng hộ nhóm của bản GitHub (psviethoa.com / Ko-fi theo ngôn ngữ).</summary>
public partial class MainViewModel
{
    /// <summary>Cài đặt dùng chung — MainWindow.Xmas đọc/ghi XmasTheme, XmasMusic, XmasVolume.</summary>
    internal AppSettings XmasSettings => _settings;

    /// <summary>Lưu ngay (bật/tắt Noel, nhạc, âm lượng) để tắt app đột ngột vẫn giữ lựa chọn.</summary>
    internal void SaveXmasSettings() => SettingsService.Save(_settings);

    /// <summary>Mở trang ủng hộ nhóm bằng trình duyệt mặc định: tiếng Việt → psviethoa.com/donate, tiếng Anh → Ko-fi.</summary>
    [RelayCommand]
    private Task OpenDonate() => _dialogs.OpenUriAsync(Loc.Current.Language == Loc.Vietnamese ? AppInfo.DonateUrlVi : AppInfo.KofiUrl);
}
