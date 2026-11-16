using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using PsViethoa.FpkgBuilder.Core.Localization;
using PsViethoa.FpkgBuilder.Core.Models;
using PsViethoa.FpkgBuilder.Core.Services;

namespace PsViethoa.FpkgBuilder.App.ViewModels;

/// <summary>
/// Giải nén gói · thư mục xuất: chọn ổ / thư mục gốc, ứng dụng tự tạo "&lt;Tên game&gt; &lt;PPSAxxxxx&gt;" bên trong (như PSVIETHOA
/// STUDIO 2.1.0). Tắt "Tự tạo thư mục" thì xả thẳng vào đúng thư mục chọn / gõ.
/// </summary>
public sealed partial class ExtractionViewModel
{
    private string _baseFolder = string.Empty;

    public ObservableCollection<DriveChoice> Drives { get; } = new();

    /// <summary>Thư mục gốc người dùng chọn (vd "E:\"); trống = cạnh tệp .pkg.</summary>
    public string BaseFolder
    {
        get => _baseFolder;
        private set
        {
            value = (value ?? string.Empty).Trim();
            if (_baseFolder == value)
            {
                return;
            }

            _baseFolder = value;
            _settings.ExtractBaseFolder = value;
            OnPropertyChanged(nameof(BaseFolder));
            OnPropertyChanged(nameof(IsNextToPackage));
            OnPropertyChanged(nameof(IsNextToPackageActive));
            SyncDriveSelection();
            NotifyFolderTexts();
        }
    }

    public bool IsNextToPackage => BaseFolder.Length == 0;

    /// <summary>Chip "Cạnh tệp gói" sáng khi đang tự tạo thư mục và chưa chọn ổ.</summary>
    public bool IsNextToPackageActive => AutoOutputFolder && IsNextToPackage;

    /// <summary>Tên thư mục game tự tạo, vd "Hades PPSA03355"; trống khi chưa mở gói.</summary>
    public string GameFolderName => _info == null ? string.Empty : GameFolderFor(_info);

    /// <summary>Dòng giải thích dưới ô Thư mục xuất: sẽ tạo thư mục nào, ở đâu.</summary>
    public string FolderPlanText
    {
        get
        {
            if (!AutoOutputFolder)
            {
                return Loc.T("Extract.FolderPlan.Direct");
            }

            var where = IsNextToPackage ? Loc.T("Extract.FolderPlan.NextTo") : Loc.F("Extract.FolderPlan.In", BaseFolder);
            return _info == null
                ? Loc.F("Extract.FolderPlan.Pending", where)
                : Loc.F("Extract.FolderPlan.Ready", GameFolderName, where);
        }
    }

    [RelayCommand]
    private void PickDrive(DriveChoice? drive)
    {
        if (drive == null || IsBusy)
        {
            return;
        }

        UseBaseFolder(drive.Root);
    }

    [RelayCommand]
    private void NextToPackage()
    {
        if (!IsBusy)
        {
            UseBaseFolder(string.Empty);
        }
    }

    [RelayCommand]
    private Task RefreshDrivesAsync() => LoadDrivesAsync();

    /// <summary>Đặt thư mục gốc rồi bật lại chế độ tự tạo thư mục game.</summary>
    private void UseBaseFolder(string folder)
    {
        BaseFolder = folder;
        if (!AutoOutputFolder)
        {
            AutoOutputFolder = true; // OnAutoOutputFolderChanged gọi SuggestOutputFolder
        }
        else
        {
            SuggestOutputFolder(_loadedPath ?? PackagePath.Trim());
        }

        NotifyFolderTexts();
    }

    /// <summary>Thư mục xuất tự đặt: &lt;gốc&gt;/&lt;Tên game&gt; &lt;ID&gt;; gốc trống = cạnh tệp gói.</summary>
    private string AutoOutputFor(string packagePath)
    {
        try
        {
            var root = BaseFolder.Length > 0 ? BaseFolder : Path.GetDirectoryName(packagePath) ?? string.Empty;
            if (root.Length == 0)
            {
                return string.Empty;
            }

            var name = _info != null && string.Equals(_info.Path, packagePath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
                ? GameFolderFor(_info)
                : string.Empty;
            if (name.Length == 0)
            {
                // Chưa đọc được thông tin gói: giữ cách đặt cũ, đổi sang tên game ngay khi gói mở xong.
                name = Path.GetFileNameWithoutExtension(packagePath) + "-extract";
            }

            return Path.Combine(root, name);
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    /// <summary>"&lt;Tên game&gt; &lt;PPSAxxxxx&gt;" — tên từ param.json, ID là ký tự 7..16 của Content ID.</summary>
    public static string GameFolderFor(PackageInfo info)
    {
        var id = Core.Services.GameFolderName.TitleIdOf(info.ContentId);
        var title = info.Title;
        if (string.IsNullOrWhiteSpace(title))
        {
            title = id.Length > 0 ? string.Empty : info.ContentId ?? Path.GetFileNameWithoutExtension(info.Path);
        }

        return Core.Services.GameFolderName.For(title, id);
    }

    private async Task LoadDrivesAsync()
    {
        var found = await DriveChoice.ListAsync();
        Drives.Clear();
        foreach (var drive in found)
        {
            Drives.Add(drive);
        }

        SyncDriveSelection();
    }

    private void SyncDriveSelection()
    {
        foreach (var drive in Drives)
        {
            drive.IsSelected = AutoOutputFolder && drive.Matches(BaseFolder);
        }
    }

    private void NotifyFolderTexts()
    {
        OnPropertyChanged(nameof(IsNextToPackageActive));
        SyncDriveSelection();
        OnPropertyChanged(nameof(GameFolderName));
        OnPropertyChanged(nameof(FolderPlanText));
    }

    private void RefreshFolderLanguage()
    {
        NotifyFolderTexts();
        _ = LoadDrivesAsync();
    }
}
