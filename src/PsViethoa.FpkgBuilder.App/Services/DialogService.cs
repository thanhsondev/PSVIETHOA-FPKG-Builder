using Avalonia.Controls;
using Avalonia.Platform.Storage;
using PsViethoa.FpkgBuilder.App.Views;
using PsViethoa.FpkgBuilder.Core.Services;
using PsViethoa.FpkgBuilder.Core.Localization;

namespace PsViethoa.FpkgBuilder.App.Services;

/// <summary>Hộp thoại chọn tệp/thư mục, thông báo, clipboard và mở thư mục — đa nền tảng qua Avalonia.</summary>
public sealed class DialogService
{
    private readonly Window _owner;

    public DialogService(Window owner)
    {
        _owner = owner;
    }

    public async Task<string?> PickFolderAsync(string title, string? initialDirectory)
    {
        var provider = _owner.StorageProvider;
        if (!provider.CanPickFolder)
        {
            return null;
        }

        var options = new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            SuggestedStartLocation = await TryGetFolderAsync(provider, initialDirectory),
        };

        var result = await provider.OpenFolderPickerAsync(options);
        return result.Count > 0 ? result[0].TryGetLocalPath() : null;
    }

    /// <summary>Chọn nhiều thư mục một lúc (hệ điều hành không hỗ trợ thì trả về rỗng).</summary>
    public async Task<IReadOnlyList<string>> PickFoldersAsync(string title, string? initialDirectory)
    {
        var provider = _owner.StorageProvider;
        if (!provider.CanPickFolder)
        {
            return Array.Empty<string>();
        }

        var result = await provider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = true,
            SuggestedStartLocation = await TryGetFolderAsync(provider, initialDirectory),
        });
        return result.Select(item => item.TryGetLocalPath()).OfType<string>().ToList();
    }

    /// <summary>Chọn nhiều tệp một lúc.</summary>
    public async Task<IReadOnlyList<string>> PickFilesAsync(string title, string? initialDirectory, params FilePickerFileType[] fileTypes)
    {
        var provider = _owner.StorageProvider;
        if (!provider.CanOpen)
        {
            return Array.Empty<string>();
        }

        var result = await provider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = true,
            FileTypeFilter = fileTypes.Length > 0 ? fileTypes : null,
            SuggestedStartLocation = await TryGetFolderAsync(provider, initialDirectory),
        });
        return result.Select(item => item.TryGetLocalPath()).OfType<string>().ToList();
    }

    /// <summary>Cửa sổ chủ của các hộp thoại (cho hộp thoại riêng của từng trang).</summary>
    public Window Owner => _owner;

    /// <summary>Bộ lọc "Gói PKG (*.pkg)" + "Mọi tệp" (nhãn theo ngôn ngữ đang dùng).</summary>
    public static FilePickerFileType[] PkgFiles =>
    [
        new FilePickerFileType(Loc.T("Extract.PkgFilter")) { Patterns = ["*.pkg"] },
        new FilePickerFileType(Loc.T("Pick.AllFiles")) { Patterns = ["*"] },
    ];

    /// <summary>Bộ lọc "param.sfo (*.sfo)" + "Mọi tệp".</summary>
    public static FilePickerFileType[] SfoFiles =>
    [
        new FilePickerFileType(Loc.T("Ps4.SfoEdit.Filter")) { Patterns = ["*.sfo", "param.sfo"] },
        new FilePickerFileType(Loc.T("Pick.AllFiles")) { Patterns = ["*"] },
    ];

    public async Task<string?> PickFileAsync(string title, string? initialDirectory, params FilePickerFileType[] fileTypes)
    {
        var provider = _owner.StorageProvider;
        if (!provider.CanOpen)
        {
            return null;
        }

        var options = new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = fileTypes.Length > 0 ? fileTypes : null,
            SuggestedStartLocation = await TryGetFolderAsync(provider, initialDirectory),
        };

        var result = await provider.OpenFilePickerAsync(options);
        return result.Count > 0 ? result[0].TryGetLocalPath() : null;
    }

    public async Task<string?> SaveFileAsync(string title, string suggestedName, string defaultExtension, params FilePickerFileType[] fileTypes)
    {
        var provider = _owner.StorageProvider;
        if (!provider.CanSave)
        {
            return null;
        }

        var options = new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            DefaultExtension = defaultExtension,
            ShowOverwritePrompt = true,
            FileTypeChoices = fileTypes.Length > 0 ? fileTypes : null,
        };

        var result = await provider.SaveFilePickerAsync(options);
        return result?.TryGetLocalPath();
    }

    public async Task SetClipboardAsync(string text)
    {
        var clipboard = TopLevel.GetTopLevel(_owner)?.Clipboard;
        if (clipboard != null)
        {
            await clipboard.SetTextAsync(text);
        }
    }

    public async Task<bool> OpenFolderAsync(string path)
    {
        try
        {
            if (!System.IO.Directory.Exists(path))
            {
                return false;
            }

            // Trên macOS/Windows/Linux dùng lệnh hệ thống trước (Finder/Explorer/xdg-open) — ILauncher của Avalonia
            // không mở được thư mục trên macOS trong một số trường hợp; đường dẫn được truyền qua ArgumentList nên
            // khoảng trắng hay ký tự Unicode đều an toàn.
            if (TryOpenWithShell(path))
            {
                return true;
            }

            var launcher = TopLevel.GetTopLevel(_owner)?.Launcher;
            if (launcher != null)
            {
                return await launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(path));
            }
        }
        catch (Exception)
        {
        }

        return false;
    }

    /// <summary>Mở tệp/thư mục/URL bằng lệnh của hệ điều hành: open (macOS), explorer.exe (Windows), xdg-open (Linux).</summary>
    private static bool TryOpenWithShell(string target)
    {
        try
        {
            var start = new System.Diagnostics.ProcessStartInfo { UseShellExecute = false, CreateNoWindow = true };
            if (OperatingSystem.IsMacOS())
            {
                start.FileName = "/usr/bin/open";
                start.ArgumentList.Add(target);
            }
            else if (OperatingSystem.IsWindows())
            {
                start.FileName = "explorer.exe";
                start.ArgumentList.Add(target);
            }
            else
            {
                start.FileName = "xdg-open";
                start.ArgumentList.Add(target);
            }

            using var process = System.Diagnostics.Process.Start(start);
            if (process == null)
            {
                return false;
            }

            // "open"/xdg-open thoát ngay; explorer.exe có thể trả mã khác 0 dù đã mở — chỉ coi là thất bại khi không chạy được.
            if (!OperatingSystem.IsWindows() && process.WaitForExit(3000) && process.ExitCode != 0)
            {
                return false;
            }

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Mở một tệp bằng ứng dụng mặc định của hệ điều hành.</summary>
    public async Task<bool> OpenFileAsync(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            if (TryOpenWithShell(path))
            {
                return true;
            }

            var launcher = TopLevel.GetTopLevel(_owner)?.Launcher;
            if (launcher != null)
            {
                return await launcher.LaunchFileInfoAsync(new FileInfo(path));
            }
        }
        catch (Exception)
        {
        }

        return false;
    }

    public async Task<bool> OpenUriAsync(string uri)
    {
        try
        {
            var launcher = TopLevel.GetTopLevel(_owner)?.Launcher;
            if (launcher != null && await launcher.LaunchUriAsync(new Uri(uri)))
            {
                return true;
            }

            return TryOpenWithShell(uri);
        }
        catch (Exception)
        {
        }

        return false;
    }

    public Task ShowErrorAsync(string title, string message) =>
        MessageDialog.ShowAsync(_owner, title, message, MessageDialog.Kind.Error, Loc.T("Common.Close"), null);

    public Task ShowInfoAsync(string title, string message) =>
        MessageDialog.ShowAsync(_owner, title, message, MessageDialog.Kind.Info, Loc.T("Common.Close"), null);

    public Task<bool> ConfirmAsync(string title, string message, string confirmLabel, string? cancelLabel = null, bool destructive = false) =>
        MessageDialog.ShowAsync(_owner, title, message, destructive ? MessageDialog.Kind.Danger : MessageDialog.Kind.Question, confirmLabel, cancelLabel);

    /// <summary>Hỏi cách xử lý khi thư mục xuất đã có gói cùng tên: ghi đè, giữ bản cũ (đổi tên nó) hoặc huỷ.</summary>
    public async Task<OutputConflictChoice> AskOutputConflictAsync(string title, string message, string overwriteLabel, string keepLabel, string cancelLabel)
    {
        var result = await MessageDialog.ShowChoiceAsync(_owner, title, message, overwriteLabel, keepLabel, cancelLabel);
        return result switch
        {
            MessageDialog.ConfirmResult => OutputConflictChoice.Overwrite,
            MessageDialog.AltResult => OutputConflictChoice.KeepExisting,
            _ => OutputConflictChoice.Cancel,
        };
    }

    private static async Task<IStorageFolder?> TryGetFolderAsync(IStorageProvider provider, string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !System.IO.Directory.Exists(path))
        {
            return null;
        }

        try
        {
            return await provider.TryGetFolderFromPathAsync(path);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
