using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using PsViethoa.FpkgBuilder.App.ViewModels;
using PsViethoa.FpkgBuilder.Core.Services;
using PsViethoa.FpkgBuilder.Core.ExFat;

namespace PsViethoa.FpkgBuilder.App.Views;

public partial class MainWindow : Window
{
    private bool _closeConfirmed;
    private MainViewModel? _attached;

    public MainWindow()
    {
        InitializeComponent();

        AddHandler(DragDrop.DragEnterEvent, OnDragEnter);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        AddHandler(DragDrop.DropEvent, OnDrop);

        DataContextChanged += (_, _) => AttachViewModel();
        Opened += OnOpened;
        Closing += OnClosing;
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    private void AttachViewModel()
    {
        if (_attached != null)
        {
            _attached.LogAppended -= OnLogAppended;
            _attached.FocusFieldRequested -= OnFocusFieldRequested;
        }

        _attached = ViewModel;
        if (_attached != null)
        {
            _attached.LogAppended += OnLogAppended;
            _attached.FocusFieldRequested += OnFocusFieldRequested;
        }
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        if (ViewModel == null)
        {
            return;
        }

        var (width, height) = ViewModel.SavedWindowSize;
        var area = Screens.ScreenFromWindow(this)?.WorkingArea ?? Screens.Primary?.WorkingArea;
        if (area != null)
        {
            var scale = RenderScaling <= 0 ? 1 : RenderScaling;
            var maxWidth = Math.Max(MinWidth, area.Value.Width / scale - 24);
            var maxHeight = Math.Max(MinHeight, area.Value.Height / scale - 24);
            Width = Math.Clamp(width, MinWidth, maxWidth);
            Height = Math.Clamp(height, MinHeight, maxHeight);
        }

        // Chụp giao diện ra tệp PNG rồi thoát (phục vụ tài liệu/kiểm thử): PSVIETHOA_SCREENSHOT=/duong/dan.png
        var screenshotPath = Environment.GetEnvironmentVariable("PSVIETHOA_SCREENSHOT");
        if (!string.IsNullOrWhiteSpace(screenshotPath))
        {
            _ = CaptureAndExitAsync(screenshotPath);
        }

        // Đổi ngôn ngữ lúc chạy trước khi chụp (kiểm thử binding {l:T}): PSVIETHOA_SWITCH_LANG=en|vi
        var switchLanguage = Environment.GetEnvironmentVariable("PSVIETHOA_SWITCH_LANG");
        if (!string.IsNullOrWhiteSpace(switchLanguage) && ViewModel != null)
        {
            var target = switchLanguage.Trim().ToLowerInvariant();
            Avalonia.Threading.DispatcherTimer.RunOnce(() =>
            {
                if (target == "en")
                {
                    ViewModel.LanguageEn = true;
                }
                else
                {
                    ViewModel.LanguageVi = true;
                }
            }, TimeSpan.FromSeconds(1));
        }

        // Chế độ giải nén khi khởi động (chụp màn hình/kiểm thử): PSVIETHOA_MODE=extract, PSVIETHOA_EXTRACT_PKG=/duong/dan.pkg
        if (ViewModel != null && string.Equals(Environment.GetEnvironmentVariable("PSVIETHOA_MODE"), "extract", StringComparison.OrdinalIgnoreCase))
        {
            ViewModel.IsExtractMode = true;
        }

        var extractPackage = Environment.GetEnvironmentVariable("PSVIETHOA_EXTRACT_PKG");
        if (ViewModel != null && !string.IsNullOrWhiteSpace(extractPackage))
        {
            ViewModel.IsExtractMode = true;
            ViewModel.Extraction.LoadPackage(extractPackage);
        }

        // Tự chạy một lần tạo gói, chụp màn hình giữa chừng và khi xong rồi thoát: PSVIETHOA_AUTOBUILD=/duong/dan/prefix
        var autoBuild = Environment.GetEnvironmentVariable("PSVIETHOA_AUTOBUILD");
        if (!string.IsNullOrWhiteSpace(autoBuild))
        {
            _ = AutoBuildAndExitAsync(autoBuild);
        }
    }

    private async Task AutoBuildAndExitAsync(string prefix)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(2.5));
            if (ViewModel == null)
            {
                return;
            }

            var build = ViewModel.BuildCommand.ExecuteAsync(null);
            await Task.Delay(TimeSpan.FromSeconds(2.2));
            Capture(prefix + "-building.png");
            await build;
            await Task.Delay(TimeSpan.FromSeconds(1.5));
            Capture(prefix + "-done.png");
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
        }
        finally
        {
            _closeConfirmed = true;
            Close();
        }
    }

    private void Capture(string path)
    {
        var scrollHook = Environment.GetEnvironmentVariable("PSVIETHOA_SCROLL");

        // Chế độ giải nén cuộn cột thông tin gói; chế độ tạo gói cuộn cột tuỳ chọn.
        var scroll = ViewModel?.IsExtractMode == true
            ? Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(this).OfType<ScrollViewer>().FirstOrDefault(viewer => viewer.Name == "InfoScroll") ?? SettingsScroll
            : SettingsScroll;
        if (scrollHook == "end")
        {
            scroll.ScrollToEnd();
            scroll.UpdateLayout();
        }
        else if (double.TryParse(scrollHook, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var offset))
        {
            scroll.Offset = new Avalonia.Vector(0, offset);
            scroll.UpdateLayout();
        }

        var scale = RenderScaling <= 0 ? 1 : RenderScaling;
        var size = new Avalonia.PixelSize((int)(Bounds.Width * scale), (int)(Bounds.Height * scale));
        using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(size, new Avalonia.Vector(96 * scale, 96 * scale));
        bitmap.Render(this);
        bitmap.Save(path);
    }

    private async Task CaptureAndExitAsync(string path)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(2.5));
            if (Environment.GetEnvironmentVariable("PSVIETHOA_SHOW_HELP") == "1")
            {
                // Chụp cửa sổ Hướng dẫn thay vì cửa sổ chính.
                var help = new HelpWindow();
                help.Show(this);
                await Task.Delay(TimeSpan.FromSeconds(1.5));
                if (Environment.GetEnvironmentVariable("PSVIETHOA_SCROLL") == "end")
                {
                    help.ScrollToEnd();
                    await Task.Delay(300);
                }

                var scale = help.RenderScaling <= 0 ? 1 : help.RenderScaling;
                var size = new Avalonia.PixelSize((int)(help.Bounds.Width * scale), (int)(help.Bounds.Height * scale));
                using var helpBitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(size, new Avalonia.Vector(96 * scale, 96 * scale));
                helpBitmap.Render(help);
                helpBitmap.Save(path);
                help.Close();
                return;
            }

            Capture(path);
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
        }
        finally
        {
            _closeConfirmed = true;
            Close();
        }
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closeConfirmed || ViewModel == null)
        {
            return;
        }

        e.Cancel = true;
        ViewModel.RememberWindowSize(Width, Height);
        if (await ViewModel.ConfirmCloseAsync())
        {
            _closeConfirmed = true;
            Close();
        }
    }

    private void OnLogAppended(object? sender, EventArgs e)
    {
        if (ViewModel is { AutoScrollLog: true } && LogList.ItemCount > 0)
        {
            LogList.ScrollIntoView(LogList.ItemCount - 1);
        }
    }

    private void OnFocusFieldRequested(object? sender, string field)
    {
        if (field == MainViewModel.FocusCancelButton)
        {
            // Nút "Tạo gói" biến mất khi bắt đầu build; chuyển tiêu điểm sang nút Hủy để cột cấu hình không tự cuộn.
            Avalonia.Threading.Dispatcher.UIThread.Post(() => CancelButton.Focus(), Avalonia.Threading.DispatcherPriority.Background);
            return;
        }

        Control? target = field switch
        {
            BuildPreparer.FieldSource => SourceBox,
            BuildPreparer.FieldOutput => OutputBox,
            BuildPreparer.FieldContentId => ContentIdBox,
            BuildPreparer.FieldVersion => VersionBox,
            BuildPreparer.FieldTemporary => TempBox,
            BuildPreparer.FieldPasscode => PasscodeBox,
            BuildPreparer.FieldThreads => ThreadsBox,
            BuildPreparer.FieldPlayGo => PlayGoBox,
            BuildPreparer.FieldSdk => SdkCombo,
            BuildPreparer.FieldPublishingTools => DllBox,
            BuildPreparer.FieldKrakenLevel => BackendCombo,
            BuildPreparer.FieldExFat => ExFatCombo,
            _ => null,
        };

        if (target == null)
        {
            return;
        }

        var advancedFields = new[]
        {
            BuildPreparer.FieldTemporary, BuildPreparer.FieldPasscode, BuildPreparer.FieldThreads, BuildPreparer.FieldPlayGo,
            BuildPreparer.FieldSdk, BuildPreparer.FieldPublishingTools, BuildPreparer.FieldKrakenLevel, BuildPreparer.FieldExFat,
        };
        if (ViewModel != null && advancedFields.Contains(field))
        {
            ViewModel.AdvancedExpanded = true;
        }

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            target.BringIntoView();
            target.Focus();
            if (target is TextBox textBox)
            {
                textBox.SelectAll();
            }
        }, Avalonia.Threading.DispatcherPriority.Background);
    }

    private void Help_Click(object? sender, RoutedEventArgs e) => ShowHelp();

    private void ShowHelp()
    {
        var help = new HelpWindow();
        help.ShowDialog(this);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled)
        {
            return;
        }

        if (e.Key == Key.F1)
        {
            e.Handled = true;
            ShowHelp();
        }
    }

    // ===================== Kéo–thả =====================

    /// <summary>Thư mục ứng dụng, tệp ảnh .exfat/.ffpfsc/.ffpkg, tệp dự án .gp5, hoặc tệp nằm trong thư mục ứng dụng (lấy thư mục cha).</summary>
    private static string? GetDroppedSource(DragEventArgs e)
    {
        var items = e.Data.GetFiles();
        if (items == null)
        {
            return null;
        }

        foreach (var item in items)
        {
            var path = item.TryGetLocalPath();
            if (path == null)
            {
                continue;
            }

            if (item is IStorageFolder || Directory.Exists(path))
            {
                return path;
            }

            if (File.Exists(path) && (SourceLocator.HasGp5Extension(path) || SourceLocator.HasImageExtension(path) || ExFatImage.IsExFatFile(path) || UfsImage.IsUfsFile(path)))
            {
                return path;
            }

            var parent = Path.GetDirectoryName(path);
            if (parent != null && Directory.Exists(Path.Combine(parent, "sce_sys")))
            {
                return parent;
            }
        }

        return null;
    }

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        if (ViewModel is { IsBuilding: false } && GetDroppedSource(e) != null)
        {
            e.DragEffects = DragDropEffects.Copy;
            DropOverlay.IsVisible = true;
        }
        else
        {
            e.DragEffects = DragDropEffects.None;
        }

        e.Handled = true;
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = ViewModel is { IsBuilding: false } && GetDroppedSource(e) != null
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDragLeave(object? sender, DragEventArgs e)
    {
        DropOverlay.IsVisible = false;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        DropOverlay.IsVisible = false;
        if (ViewModel is not { IsBuilding: false })
        {
            return;
        }

        var source = GetDroppedSource(e);
        if (source != null)
        {
            ViewModel.SetSource(source);
            e.Handled = true;
        }
    }
}
