using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using PsViethoa.FpkgBuilder.App.Services;
using PsViethoa.FpkgBuilder.App.ViewModels;
using PsViethoa.FpkgBuilder.App.ViewModels.Shell;
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
        // Cửa sổ hẹp (MacBook 13", laptop 1366×768): chip "Khoá debug" chỉ còn biểu tượng để tên ứng dụng không bị đè.
        SizeChanged += (_, e) => UpdateHeaderDensity(e.NewSize.Width);
        Closing += OnClosing;

        // Cột phải trang tạo gói PS5: nhật ký luôn giữ tối thiểu LogMinHeight, phần dư thì cuộn (xem UpdateRightColumn).
        // Tự cuộn nhật ký chỉ cuộn bên trong ô nhật ký: chặn yêu cầu "đưa vào tầm nhìn" lan ra cột ngoài, nếu không cột phải tự trôi
        // xuống mỗi khi có dòng log mới và che khung trạng thái.
        LogCard.AddHandler(RequestBringIntoViewEvent, (_, e) => e.Handled = true, Avalonia.Interactivity.RoutingStrategies.Bubble);
        RightScroll.SizeChanged += (_, _) => UpdateRightColumn();
        ControlCard.SizeChanged += (_, e) =>
        {
            UpdateRightColumn();
            OpenOutputText.IsVisible = e.NewSize.Width >= 470;
        };
        PresetGrid.SizeChanged += (_, e) => LayoutPresets(e.NewSize.Width < 580);
        ResultCard.SizeChanged += (_, _) => UpdateRightColumn();
        ResultCard.PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty)
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(UpdateRightColumn, Avalonia.Threading.DispatcherPriority.Loaded);
            }
        };
    }

    private bool? _presetsTwoRows;

    /// <summary>Bốn ô preset một hàng; cột hẹp (dưới 580 px) thì xếp 2×2 để tên và mô tả không bị cắt.</summary>
    private void LayoutPresets(bool twoRows)
    {
        if (_presetsTwoRows == twoRows)
        {
            return;
        }

        _presetsTwoRows = twoRows;
        PresetGrid.ColumnDefinitions = new ColumnDefinitions(twoRows ? "*,6,*" : "*,6,*,6,*,6,*");
        PresetGrid.RowDefinitions = new RowDefinitions(twoRows ? "Auto,6,Auto" : "Auto");
        var index = 0;
        foreach (var preset in PresetGrid.Children.OfType<RadioButton>())
        {
            Grid.SetColumn(preset, twoRows ? index % 2 * 2 : index * 2);
            Grid.SetRow(preset, twoRows ? index / 2 * 2 : 0);
            index++;
        }
    }

    /// <summary>Chiều cao tối thiểu của ô nhật ký ở trang tạo gói PS5.</summary>
    private const double LogMinHeight = 280;

    /// <summary>
    /// Cột phải là lưới Auto/Auto/* nằm trong ScrollViewer: lưới cao bằng khung nhìn khi đủ chỗ (nhật ký lấp phần còn lại), còn khi
    /// bảng điều khiển + khung kết quả chiếm gần hết thì lưới cao hơn khung nhìn để nhật ký vẫn còn LogMinHeight — cuộn xuống để xem.
    /// </summary>
    private void UpdateRightColumn()
    {
        var viewport = RightScroll.Bounds.Height;
        if (viewport <= 0)
        {
            return;
        }

        var above = ControlCard.Bounds.Height + (ResultCard.IsVisible ? ResultCard.Bounds.Height + ResultCard.Margin.Top : 0);
        var height = Math.Max(viewport, above + LogCard.Margin.Top + LogMinHeight);
        if (double.IsNaN(RightColumn.Height) || Math.Abs(RightColumn.Height - height) > 0.5)
        {
            RightColumn.Height = height;
        }
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    private void AttachViewModel()
    {
        if (_attached != null)
        {
            _attached.LogAppended -= OnLogAppended;
            _attached.FocusFieldRequested -= OnFocusFieldRequested;
            _attached.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _attached = ViewModel;
        if (_attached != null)
        {
            _attached.LogAppended += OnLogAppended;
            _attached.FocusFieldRequested += OnFocusFieldRequested;
            _attached.PropertyChanged += OnViewModelPropertyChanged;
            _lastPlatform = _attached.PagePlatform;
        }
    }

    private Platform? _lastPlatform;

    /// <summary>Đổi trang: nội dung mới mờ dần hiện lên và trồi lên vài điểm ảnh; sang nhóm khác thì huy hiệu đầu trang bật lên.</summary>
    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.CurrentPage) || ViewModel is not { } vm)
        {
            return;
        }

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            Control? page = vm.CurrentPage switch
            {
                AppPage.Ps5Build or AppPage.Ps5Update => Ps5BuildPage,
                AppPage.Ps5Extract => Ps5ExtractPage,
                AppPage.Ps5Queue => Ps5QueuePage,
                AppPage.Ftp => FtpPage,
                AppPage.SaveResign => SavePage,
                _ => Ps4Page,
            };
            Motion.Enter(page, 14, 260);
            Motion.Enter(PageHeaderText, 6, 200);
            if (_lastPlatform != vm.PagePlatform)
            {
                PageBadge.Pop();
            }

            _lastPlatform = vm.PagePlatform;
        }, Avalonia.Threading.DispatcherPriority.Loaded);
    }

    private const double CompactHeaderWidth = 1240;

    /// <summary>Dưới mức này thanh bên tự thu gọn (chỉ biểu tượng) để trang Tạo gói vẫn đủ hai cột.</summary>
    private const double NarrowSidebarWidth = 1200;

    private void UpdateHeaderDensity(double width)
    {
        var full = width >= CompactHeaderWidth;
        KeysReadyText.IsVisible = full;
        KeysMissingText.IsVisible = full;
        if (ViewModel != null)
        {
            ViewModel.SidebarNarrow = width < NarrowSidebarWidth;
        }
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        if (ViewModel == null)
        {
            return;
        }

        var (width, height) = ViewModel.SavedWindowSize;
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        var area = screen?.WorkingArea;
        if (area != null)
        {
            // Vừa trọn vùng làm việc của màn hình (trừ thanh menu/Dock/taskbar) trên mọi hệ điều hành: màn hình nhỏ hơn mức tối thiểu
            // (MacBook 13", laptop 1366×768) thì hạ luôn mức tối thiểu, rồi căn giữa lại để không phần nào nằm ngoài màn hình.
            // Đổi đơn vị bằng tỉ lệ CỦA MÀN HÌNH, không phải RenderScaling: macOS báo WorkingArea theo point (Scaling = 1) trong khi
            // RenderScaling của màn Retina là 2 — chia cho 2 làm cửa sổ chỉ còn nửa màn hình (đo 2026-10-10: 1512 pt → 732).
            var scale = screen!.Scaling <= 0 ? 1 : screen.Scaling;
            var maxWidth = area.Value.Width / scale - 24;
            var maxHeight = area.Value.Height / scale - 24;
            MinWidth = Math.Min(MinWidth, maxWidth);
            MinHeight = Math.Min(MinHeight, maxHeight);
            Width = Math.Clamp(width, MinWidth, maxWidth);
            Height = Math.Clamp(height, MinHeight, maxHeight);
            var x = area.Value.X + (int)Math.Round((area.Value.Width - Width * scale) / 2);
            var y = area.Value.Y + (int)Math.Round((area.Value.Height - Height * scale) / 2);
            Position = new Avalonia.PixelPoint(Math.Max(area.Value.X, x), Math.Max(area.Value.Y, y));
        }

        // Chụp giao diện ra tệp PNG rồi thoát (phục vụ tài liệu/kiểm thử): PSVIETHOA_SCREENSHOT=/duong/dan.png
        var screenshotPath = Environment.GetEnvironmentVariable("PSVIETHOA_SCREENSHOT");
        if (!string.IsNullOrWhiteSpace(screenshotPath))
        {
            // Ảnh chụp không được bắt khung hình dở dang của hiệu ứng.
            Motion.Suppressed = true;
            _ = CaptureAndExitAsync(screenshotPath);
        }

        // Mở thẳng một trang khi khởi động (chụp màn hình/kiểm thử): PSVIETHOA_PAGE=Ps4Patch|Ftp|SaveResign|… (tên AppPage)
        if (Enum.TryParse<AppPage>(Environment.GetEnvironmentVariable("PSVIETHOA_PAGE"), ignoreCase: true, out var startPage))
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (ViewModel != null)
                {
                    ViewModel.CurrentPage = startPage;

                    // Trang ZSTD: PSVIETHOA_ZSTD_SOURCE / _OUTPUT / _PATCH = đường dẫn (kèm PSVIETHOA_ZSTD_AUTOSTART=1 để chạy rồi chụp).
                    if (Environment.GetEnvironmentVariable("PSVIETHOA_ZSTD_SOURCE") is { Length: > 0 } zstdSource)
                    {
                        ViewModel.Zstd.SourcePath = zstdSource;
                    }

                    if (Environment.GetEnvironmentVariable("PSVIETHOA_ZSTD_OUTPUT") is { Length: > 0 } zstdOutput)
                    {
                        ViewModel.Zstd.OutputFolder = zstdOutput;
                    }

                    if (Environment.GetEnvironmentVariable("PSVIETHOA_ZSTD_PATCH") is { Length: > 0 } zstdPatch)
                    {
                        ViewModel.Zstd.PatchFolder = zstdPatch;
                    }

                    // Điền sẵn nguồn PS4 để chụp phần xem trước game: PSVIETHOA_PS4_SOURCE=/đường/dẫn, PSVIETHOA_PS4_BASE=/đường/dẫn
                    if (Environment.GetEnvironmentVariable("PSVIETHOA_PS4_SOURCE") is { Length: > 0 } ps4Source)
                    {
                        ViewModel.Ps4.SourcePath = ps4Source;
                    }

                    if (Environment.GetEnvironmentVariable("PSVIETHOA_PS4_BASE") is { Length: > 0 } ps4Base)
                    {
                        ViewModel.Ps4.BasePath = ps4Base;
                    }

                    if (Environment.GetEnvironmentVariable("PSVIETHOA_PS4_SFO") == "1")
                    {
                        ViewModel.Ps4.ShowSfoEditor = true;
                    }

                    // Thêm save vào hàng đợi Resign (chụp màn hình / kiểm thử): PSVIETHOA_SAVE_ADD=đường/dẫn1;đường/dẫn2
                    if (Environment.GetEnvironmentVariable("PSVIETHOA_SAVE_ADD") is { Length: > 0 } saves)
                    {
                        ViewModel.Save.AddPaths(saves.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                    }

                    // Tự kết nối FTP (chụp màn hình / kiểm thử): PSVIETHOA_FTP_CONNECT=host:port; PSVIETHOA_FTP_UPLOAD=/đường/dẫn tải lên ngay
                    if (Environment.GetEnvironmentVariable("PSVIETHOA_FTP_CONNECT") is { Length: > 0 } ftp)
                    {
                        var parts = ftp.Split(':');
                        ViewModel.Ftp.Host = parts[0];
                        ViewModel.Ftp.Port = parts.Length > 1 ? parts[1] : "2121";
                        ViewModel.Ftp.EnsureLoaded();
                        _ = ConnectFtpForTestAsync(ViewModel.Ftp);
                    }
                }
            }, Avalonia.Threading.DispatcherPriority.Background);
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

        // Khối bản vá khi khởi động (chụp màn hình/kiểm thử): PSVIETHOA_SOURCE=thư mục nguồn, PSVIETHOA_PATCH_REF=gói gốc .pkg
        var hookSource = Environment.GetEnvironmentVariable("PSVIETHOA_SOURCE");
        if (ViewModel != null && !string.IsNullOrWhiteSpace(hookSource))
        {
            ViewModel.SetSource(hookSource);
        }

        var patchReference = Environment.GetEnvironmentVariable("PSVIETHOA_PATCH_REF");
        if (ViewModel != null && !string.IsNullOrWhiteSpace(patchReference))
        {
            ViewModel.IsUpdateMode = true;
            ViewModel.ReferencePackagePath = patchReference;
            // PSVIETHOA_PATCH_BASE=thư mục game gốc (tuỳ chọn).
            ViewModel.PatchBaseFolder = Environment.GetEnvironmentVariable("PSVIETHOA_PATCH_BASE") ?? string.Empty;
        }

        // Tab Update khi khởi động (chụp màn hình/kiểm thử): PSVIETHOA_MODE=update, PSVIETHOA_UPDATE_KIND=0|1|2, PSVIETHOA_PATCH_BASE=thư mục game gốc
        if (ViewModel != null && string.Equals(Environment.GetEnvironmentVariable("PSVIETHOA_MODE"), "update", StringComparison.OrdinalIgnoreCase))
        {
            ViewModel.IsUpdateMode = true;
            ViewModel.PatchBaseFolder = Environment.GetEnvironmentVariable("PSVIETHOA_PATCH_BASE") ?? string.Empty;
            if (int.TryParse(Environment.GetEnvironmentVariable("PSVIETHOA_UPDATE_KIND"), out var kind) && kind is >= 0 and <= 2)
            {
                ViewModel.UpdateKind = kind;
            }
        }

        // PSVIETHOA_BUILD_START=<giây>: tự bấm nút tạo gói sau chừng đó giây (kiểm thử đầu-cuối tab Tạo gói / Tạo gói Update).
        if (ViewModel != null && double.TryParse(Environment.GetEnvironmentVariable("PSVIETHOA_BUILD_START"), System.Globalization.CultureInfo.InvariantCulture, out var startDelay))
        {
            var viewModel = ViewModel;
            Avalonia.Threading.DispatcherTimer.RunOnce(() =>
            {
                if (viewModel.BuildCommand.CanExecute(null))
                {
                    viewModel.BuildCommand.Execute(null);
                }
            }, TimeSpan.FromSeconds(Math.Clamp(startDelay, 1, 120)));
        }

        // Chế độ hàng chờ khi khởi động (chụp màn hình/kiểm thử): PSVIETHOA_MODE=queue, PSVIETHOA_QUEUE_ADD=nguồn1;nguồn2, PSVIETHOA_QUEUE_START=1
        if (ViewModel != null && string.Equals(Environment.GetEnvironmentVariable("PSVIETHOA_MODE"), "queue", StringComparison.OrdinalIgnoreCase))
        {
            ViewModel.IsQueueMode = true;
            var queueAdd = Environment.GetEnvironmentVariable("PSVIETHOA_QUEUE_ADD");
            if (!string.IsNullOrWhiteSpace(queueAdd))
            {
                var queueOwner = ViewModel;
                var autoStart = Environment.GetEnvironmentVariable("PSVIETHOA_QUEUE_START") == "1";
                Avalonia.Threading.DispatcherTimer.RunOnce(async () =>
                {
                    await queueOwner.Queue.AddPathsAsync(queueAdd.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                    if (autoStart)
                    {
                        queueOwner.Queue.StartCommand.Execute(null);
                    }
                }, TimeSpan.FromMilliseconds(300));
            }
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

            // Cuộn cột phải xuống cuối: nhật ký phải còn chỗ và xem được khi khung kết quả đang hiện.
            RightScroll.ScrollToEnd();
            await Task.Delay(TimeSpan.FromSeconds(0.6));
            Capture(prefix + "-done-log.png");
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

        // PSVIETHOA_SCREENSHOT_SCALE=<hệ số>: tỉ lệ ảnh chụp (mặc định = tỉ lệ màn hình). RenderTargetBitmap của Avalonia 11.3 vẽ sai
        // nội dung trong Border có BoxShadow khi DPI khác 96, nên ảnh cho tài liệu chụp ở 1× rồi phóng nếu cần.
        var scale = double.TryParse(Environment.GetEnvironmentVariable("PSVIETHOA_SCREENSHOT_SCALE"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var requested) && requested > 0
            ? requested
            : RenderScaling <= 0 ? 1 : RenderScaling;
        var size = new Avalonia.PixelSize((int)(Bounds.Width * scale), (int)(Bounds.Height * scale));
        using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(size, new Avalonia.Vector(96 * scale, 96 * scale));
        bitmap.Render(this);
        bitmap.Save(path);
    }

    private static async Task ConnectFtpForTestAsync(ViewModels.FtpViewModel ftp)
    {
        await ftp.ConnectCommand.ExecuteAsync(null);
        if (Environment.GetEnvironmentVariable("PSVIETHOA_FTP_LOCAL") is { Length: > 0 } local)
        {
            ftp.LoadLocal(local);
        }

        if (Environment.GetEnvironmentVariable("PSVIETHOA_FTP_REMOTE") is { Length: > 0 } remote)
        {
            await ftp.LoadRemoteAsync(remote);
        }

        if (Environment.GetEnvironmentVariable("PSVIETHOA_FTP_UPLOAD") is { Length: > 0 } upload)
        {
            ftp.UploadPaths(upload.Split(';'), null);
        }

        // PSVIETHOA_FTP_DOWNLOAD=/thư/mục/trên/ps5: tải cả thư mục về thư mục máy tính đang mở.
        if (Environment.GetEnvironmentVariable("PSVIETHOA_FTP_DOWNLOAD") is { Length: > 0 } download)
        {
            var name = Core.Ftp.Ps5Ftp.FileName(download);
            await ftp.DownloadRowsAsync([new ViewModels.FtpFileRow(name, download, true, 0, null, string.Empty)], null);
        }
    }

    private async Task CaptureAndExitAsync(string path)
    {
        try
        {
            // PSVIETHOA_SCREENSHOT_DELAY=<giây>: chờ lâu hơn để chụp lúc đang chạy (mặc định 2,5 s).
            var delay = double.TryParse(Environment.GetEnvironmentVariable("PSVIETHOA_SCREENSHOT_DELAY"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var seconds) ? seconds : 2.5;
            await Task.Delay(TimeSpan.FromSeconds(delay));

            // Chạy tác vụ của trang rồi chụp khi xong (ảnh "đóng gói thành công" cho tài liệu):
            // PSVIETHOA_PS4_AUTOSTART=1 (tác vụ PS4 của trang đang mở), PSVIETHOA_SAVE_START=1 (resign hàng đợi save).
            if (ViewModel != null && Environment.GetEnvironmentVariable("PSVIETHOA_PS4_AUTOSTART") == "1")
            {
                await ViewModel.Ps4.StartCommand.ExecuteAsync(null);
                await Task.Delay(TimeSpan.FromSeconds(1.5));
            }

            if (ViewModel != null && Environment.GetEnvironmentVariable("PSVIETHOA_ZSTD_AUTOSTART") == "1")
            {
                ViewModel.Zstd.SkipConfirm = true;
                await ViewModel.Zstd.StartCommand.ExecuteAsync(null);
                await Task.Delay(TimeSpan.FromSeconds(1.5));
            }

            if (ViewModel != null && Environment.GetEnvironmentVariable("PSVIETHOA_SAVE_START") == "1")
            {
                await ViewModel.Save.StartCommand.ExecuteAsync(null);
                await Task.Delay(TimeSpan.FromSeconds(1.5));
            }
            if (Environment.GetEnvironmentVariable("PSVIETHOA_SHOW_HELP") == "1")
            {
                // Chụp cửa sổ Hướng dẫn thay vì cửa sổ chính.
                var help = new HelpWindow();
                help.Show(this);
                await Task.Delay(TimeSpan.FromSeconds(1.5));
                if (Environment.GetEnvironmentVariable("PSVIETHOA_SCROLL") is { } helpScroll)
                {
                    if (helpScroll == "end")
                    {
                        help.ScrollToEnd();
                    }
                    else if (double.TryParse(helpScroll, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var helpOffset))
                    {
                        help.ScrollTo(helpOffset);
                    }

                    await Task.Delay(300);
                }

                var scale = double.TryParse(Environment.GetEnvironmentVariable("PSVIETHOA_SCREENSHOT_SCALE"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var requested) && requested > 0
                    ? requested
                    : help.RenderScaling <= 0 ? 1 : help.RenderScaling;
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

    public void ShowHelp()
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

            if (File.Exists(path) && (SourceLocator.HasGp5Extension(path) || SourceLocator.HasImageExtension(path) || ExFatImage.IsExFatFile(path) || UfsImage.IsUfsFile(path) || PfsContainer.IsContainer(path)))
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
        if (ViewModel is { IsQueueMode: true })
        {
            e.DragEffects = QueueView.GetDroppedPaths(e).Count > 0 ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
            return;
        }

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
        if (ViewModel is { IsQueueMode: true })
        {
            e.DragEffects = QueueView.GetDroppedPaths(e).Count > 0 ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
            return;
        }

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
        if (ViewModel is { IsQueueMode: true } queueOwner)
        {
            var paths = QueueView.GetDroppedPaths(e);
            if (paths.Count > 0)
            {
                e.Handled = true;
                _ = queueOwner.Queue.AddPathsAsync(paths);
            }

            return;
        }

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
