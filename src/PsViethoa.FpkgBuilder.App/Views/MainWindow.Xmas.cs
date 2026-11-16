using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using PsViethoa.FpkgBuilder.App.Services;
using PsViethoa.FpkgBuilder.App.ViewModels;
using PsViethoa.FpkgBuilder.App.Xmas;
using PsViethoa.FpkgBuilder.Core.Localization;
using GdiRectF = PsViethoa.FpkgBuilder.App.Xmas.Gdi.RectangleF;

namespace PsViethoa.FpkgBuilder.App.Views;

/// <summary>
/// Giáng sinh (port từ PSVIETHOA STUDIO 2.1.0, cùng bộ hình với DiskHealth): nền xanh thông + trăng + rặng thông phủ tuyết, dây đèn chớp
/// mép trên, quả châu đung đưa ở vùng tiêu đề, ông già Noel cưỡi xe tuần lộc bay ngang 30–65 giây một lần, tuyết rơi, mũ Noel trên logo,
/// lời chúc, cảnh làng / cây thông trong chỗ trống các khung log / danh sách, làng tuyết cuối thanh bên (khi còn chỗ), nhạc nền.
/// Mọi lớp không nhận chuột; tự dừng khi thu nhỏ; nút Noel tắt hết và trả lại bảng màu cũ.
/// Theo theme người dùng chọn: theme TỐI = đêm Giáng sinh như Studio (nền xanh thông, trăng, sao); theme SÁNG = "ngày tuyết" (trời xanh
/// băng, mặt trời mờ, bông tuyết, thẻ trắng hơi trong, khung log sáng) — đổi theme khi đang bật Noel thì mọi lớp cập nhật ngay.
/// </summary>
public partial class MainWindow
{
    private XmasSurface? _xmasBack, _xmasGarland, _xmasSleigh, _xmasOrnaments, _xmasVillage;
    private SnowLayer? _xmasSnow;
    private readonly SleighFlight _flight = new();
    private readonly Dictionary<(string Key, bool Dark), Color> _paletteOriginal = [];
    private bool _xmasOn, _xmasReady;
    private DispatcherTimer? _volumeSave;

    /// <summary>
    /// Bảng màu đêm xanh thông (giống Studio / DiskHealth) cho theme TỐI: thẻ xanh thông hơi trong để thấy nền phía sau, ô nhập và khung
    /// log / danh sách xanh đậm #0B2117, nút phụ #173A28. Đổi màu các brush dùng chung tại chỗ (mọi chỗ dùng DynamicResource đổi theo
    /// ngay), tắt Noel thì trả màu cũ.
    /// </summary>
    private static readonly (string Key, string Festive)[] FestivePaletteDark =
    [
        ("BgBrush", "#0A1F15"), ("BgElevatedBrush", "#D80C2216"), ("HeaderBrush", "#0B2117"),
        ("CardBrush", "#E3112C1E"), ("CardHoverBrush", "#E8163424"), ("MutedBrush", "#173A28"), ("MutedHoverBrush", "#1E4831"),
        ("BorderBrush", "#2D5C42"), ("BorderStrongBrush", "#2F6448"), ("DividerBrush", "#1E3D2B"),
        ("FgMutedBrush", "#8FA3BF"),
        ("InputBrush", "#0B2117"), ("InputHoverBrush", "#0E281C"), ("InputFocusBrush", "#0A1F15"),
        ("InputBorderBrush", "#2D5C42"), ("InputBorderHoverBrush", "#3D7356"),
        ("LogBgBrush", "#0B2117"), ("ScrimBrush", "#B30A1F15"),
    ];

    /// <summary>
    /// Bảng màu "ngày tuyết" cho theme SÁNG: nền xanh băng nhạt, thẻ trắng hơi trong viền xanh thông nhạt, khung log / danh sách trắng ngà
    /// xanh băng (chữ log đổi sang màu tối để đọc rõ), chữ giữ màu tối của theme sáng.
    /// </summary>
    private static readonly (string Key, string Festive)[] FestivePaletteLight =
    [
        ("BgBrush", "#EAF4F0"), ("BgElevatedBrush", "#E6F3F8F5"),
        ("CardBrush", "#E6FFFFFF"), ("CardHoverBrush", "#F0FFFFFF"), ("MutedBrush", "#DCEDE5"), ("MutedHoverBrush", "#CDE4D8"),
        ("BorderBrush", "#B7D5C6"), ("BorderStrongBrush", "#93BBA7"), ("DividerBrush", "#D3E6DC"),
        ("InputBrush", "#FAFDFB"), ("InputHoverBrush", "#F3FAF6"), ("InputFocusBrush", "#FFFFFF"),
        ("InputBorderBrush", "#B7D5C6"), ("InputBorderHoverBrush", "#93BBA7"),
        ("LogBgBrush", "#F4FAF7"), ("LogFgBrush", "#1E293B"), ("LogTimeBrush", "#64748B"),
        ("LogWarningBrush", "#B45309"), ("LogErrorBrush", "#DC2626"), ("LogSuccessBrush", "#15803D"),
        ("ScrimBrush", "#B3EAF4F0"),
    ];

    /// <summary>Theme đang hiện là sáng → biến thể "ngày tuyết".</summary>
    private bool XmasDay => ActualThemeVariant == ThemeVariant.Light;

    private void InitXmas()
    {
        if (_xmasReady || ViewModel is not { } vm)
        {
            return;
        }

        XmasMusic.Volume = vm.XmasVolume;
        Sidebar.MusicVolumeSlider.Value = Math.Clamp(vm.XmasVolume, 0, 1);
        Sidebar.XmasToggleButton.Click += XmasToggle_Click;
        Sidebar.MusicToggleButton.Click += MusicToggle_Click;
        Sidebar.MusicToggleButton.AddHandler(PointerWheelChangedEvent, MusicToggle_Wheel, RoutingStrategies.Tunnel | RoutingStrategies.Bubble);
        Sidebar.MusicVolumeSlider.PropertyChanged += (_, e) =>
        {
            if (e.Property == RangeBase.ValueProperty && ViewModel is { } model)
            {
                model.XmasVolume = Sidebar.MusicVolumeSlider.Value;
            }
        };

        // Linux không có trình phát nhạc nền: ẩn nút Nhạc và thanh âm lượng thay vì để một nút bấm không có tác dụng.
        Sidebar.MusicToggleButton.IsVisible = XmasMusic.Supported;
        Sidebar.MusicVolumeSlider.IsVisible = XmasMusic.Supported;
        Sidebar.NavScrollViewer.SizeChanged += (_, _) => FitVillage();
        Sidebar.NavScrollViewer.PropertyChanged += (_, e) =>
        {
            if (e.Property == ScrollViewer.ExtentProperty || e.Property == ScrollViewer.ViewportProperty)
            {
                FitVillage();
            }
        };
        vm.PropertyChanged += OnAppearanceChanged;
        XmasMusic.StateChanged += () => Dispatcher.UIThread.Post(UpdateXmasButtons);
        Loc.Current.LanguageChanged += (_, _) => Dispatcher.UIThread.Post(UpdateXmasButtons);
        ActualThemeVariantChanged += (_, _) =>
        {
            if (_xmasOn)
            {
                ApplyXmas(true);
            }
        };
        Closed += (_, _) => XmasMusic.Shutdown();
        _xmasReady = true;

        ApplyXmas(vm.XmasTheme);

        // Nhạc tự phát khi mở app (trừ lúc chạy thử / chụp màn hình: PSVIETHOA_NO_MUSIC=1 hoặc chế độ chụp tự động). Tắt "Hiệu ứng
        // chuyển động" không tắt nhạc — đó là nút Nhạc riêng.
        if (vm.XmasMusicOn && XmasMusic.Supported && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PSVIETHOA_SCREENSHOT"))
            && Environment.GetEnvironmentVariable("PSVIETHOA_NO_MUSIC") != "1")
        {
            XmasMusic.StartDelayed(vm.XmasVolume);
        }

        UpdateXmasButtons();
    }

    private void BuildXmasLayers()
    {
        if (_xmasBack != null)
        {
            return;
        }

        _xmasBack = new XmasSurface((g, w, h, t) =>
        {
            if (XmasDay) XmasArt.PaintFestiveDay(g, (int)Math.Ceiling(w), (int)Math.Ceiling(h));
            else XmasArt.PaintFestive(g, (int)Math.Ceiling(w), (int)Math.Ceiling(h));
        }, 0);
        XmasBackHost.Children.Add(_xmasBack);

        _xmasGarland = new XmasSurface((g, w, h, t) => XmasArt.DrawGarland(g, 0, w, 2, 1f, t), 12) { Height = 36, VerticalAlignment = VerticalAlignment.Top };
        _xmasSleigh = new XmasSurface(_flight.Paint, 30) { Height = 200, VerticalAlignment = VerticalAlignment.Top, ShouldDraw = _flight.Active };
        _xmasSnow = new SnowLayer();
        XmasFrontHost.Children.Add(_xmasSnow);
        XmasFrontHost.Children.Add(_xmasGarland);
        XmasFrontHost.Children.Add(_xmasSleigh);

        _xmasOrnaments = new XmasSurface((g, w, h, t) => XmasArt.DrawOrnaments(g, new GdiRectF(0, 0, w, h), t, 1f), 12);
        OrnamentHost.Children.Add(_xmasOrnaments);
        _xmasVillage = new XmasSurface((g, w, h, t) => XmasArt.DrawVillageScene(g, new GdiRectF(4, 0, w - 8, h), t), 8);
        Sidebar.VillageHostBorder.Child = _xmasVillage;
    }

    private void ApplyXmas(bool on)
    {
        _xmasOn = on;
        if (on)
        {
            BuildXmasLayers();
        }

        bool day = XmasDay;
        var vm = ViewModel;
        // Từng hiệu ứng bật / tắt riêng trong cửa sổ Giao diện; nền + màu Giáng sinh đi theo nút Noel.
        bool snow = on && vm?.XmasSnow != false, lights = on && vm?.XmasLights != false, sleigh = on && vm?.XmasSleigh != false;
        bool scenes = on && vm?.XmasScenes != false, caps = on && vm?.XmasSnowCaps != false;
        XmasBackHost.IsVisible = on;
        XmasFrontHost.IsVisible = on;
        OrnamentHost.IsVisible = lights;
        Sidebar.SetFestive(on, day, caps);
        ApplyWindowIcon(on);
        if (_xmasBack != null)
        {
            _xmasBack.Paused = !on;
            if (on) _xmasBack.Paint();   // nền tĩnh: vẽ lại ngay theo biến thể ngày / đêm
        }

        if (_xmasSnow != null)
        {
            _xmasSnow.IsVisible = snow;
            _xmasSnow.Paused = !snow;
            _xmasSnow.Day = day;
        }

        XmasScenes.SetDay(day);
        if (_xmasSleigh != null)
        {
            _xmasSleigh.IsVisible = sleigh;
            _xmasSleigh.Paused = !sleigh;
        }

        if (_xmasGarland != null)
        {
            _xmasGarland.IsVisible = lights;
            _xmasGarland.Paused = !lights;
        }

        if (_xmasOrnaments != null) _xmasOrnaments.Paused = !lights;
        if (_xmasVillage != null) _xmasVillage.Paused = !scenes;
        Recolor(on);
        FitVillage();
        Dispatcher.UIThread.Post(FitVillage, DispatcherPriority.Background);   // sau lượt bố cục đầu tiên

        // Cảnh làng / cây thông + người tuyết trong chỗ trống của mọi khung log / danh sách (ảnh dùng chung, 8 khung/giây).
        if (!scenes)
        {
            XmasScenes.Stop();
        }
        else if (!XmasScenes.Active)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (_xmasOn && !XmasScenes.Active)
                {
                    XmasScenes.Start(this);
                }
            }, DispatcherPriority.Loaded);
        }

        UpdateXmasButtons();
    }

    /// <summary>Icon cửa sổ / thanh tác vụ (.ico nhiều cỡ cho nét): bật Noel = icon Giáng sinh (Assets/app.ico), tắt = icon cũ (Assets/classic/app.ico).</summary>
    private void ApplyWindowIcon(bool xmas)
    {
        try
        {
            using var stream = AssetLoader.Open(new Uri(xmas
                ? "avares://PsViethoa.FpkgBuilder.App/Assets/app.ico"
                : "avares://PsViethoa.FpkgBuilder.App/Assets/classic/app.ico"));
            Icon = new WindowIcon(stream);
        }
        catch (Exception ex)
        {
            DebugLog.Write("Xmas icon: " + ex.Message);
        }
    }

    /// <summary>Đổi màu tại chỗ các brush dùng chung của CẢ HAI theme (tối = đêm xanh thông, sáng = ngày tuyết); tắt Noel thì trả màu cũ.</summary>
    private void Recolor(bool festive)
    {
        if (Application.Current is not { } app)
        {
            return;
        }

        foreach (var (palette, variant) in new[] { (FestivePaletteDark, ThemeVariant.Dark), (FestivePaletteLight, ThemeVariant.Light) })
        {
            bool dark = variant == ThemeVariant.Dark;
            foreach (var (key, hex) in palette)
            {
                if (!app.TryGetResource(key, variant, out var value) || value is not SolidColorBrush brush)
                {
                    continue;
                }

                if (!_paletteOriginal.ContainsKey((key, dark)))
                {
                    _paletteOriginal[(key, dark)] = brush.Color;
                }

                var color = festive ? Color.Parse(hex) : _paletteOriginal[(key, dark)];
                if (brush.Color != color)
                {
                    brush.Color = color;
                }
            }
        }
    }

    /// <summary>Làng tuyết chỉ hiện khi danh sách công cụ còn chỗ (không bắt thanh bên phải cuộn thêm).</summary>
    private void FitVillage()
    {
        if (!_xmasReady)
        {
            return;
        }

        var nav = Sidebar.NavScrollViewer;
        var host = Sidebar.VillageHostBorder;
        double needed = host.Height + host.Margin.Top + host.Margin.Bottom;
        // Extent của ScrollViewer luôn ≥ khung nhìn (nội dung giãn hết chỗ) → đo chiều cao tự nhiên của danh sách công cụ.
        double content = nav.Content is Control list ? list.DesiredSize.Height : nav.Extent.Height;
        double room = nav.Viewport.Height - content + (host.IsVisible ? host.Bounds.Height + host.Margin.Top + host.Margin.Bottom : 0);
        bool show = _xmasOn && ViewModel is { IsSidebarCompact: false, XmasScenes: true } && room >= needed;
        if (host.IsVisible != show)
        {
            host.IsVisible = show;
        }
    }

    private void UpdateXmasButtons()
    {
        if (!_xmasReady)
        {
            return;
        }

        Sidebar.XmasToggleButton.Classes.Set("on", _xmasOn);
        Sidebar.MusicToggleButton.Classes.Set("on", XmasMusic.Playing);
        var tip = Loc.F("Xmas.MusicTip", Loc.T(XmasMusic.Playing ? "Xmas.MusicOn" : "Xmas.MusicOff"), XmasMusic.Volume.ToString("P0", CultureInfo.CurrentCulture));
        if (XmasMusic.LastError.Length > 0)
        {
            tip += "\n" + Loc.F("Xmas.MusicError", XmasMusic.LastError);
        }

        ToolTip.SetTip(Sidebar.MusicToggleButton, tip);
    }

    /// <summary>Theo dõi cửa sổ Giao diện / nút ở thanh bên: Noel, từng hiệu ứng, chuyển động, nhạc, âm lượng, cỡ thanh bên.</summary>
    private void OnAppearanceChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (ViewModel is not { } vm)
        {
            return;
        }

        switch (e.PropertyName)
        {
            case nameof(MainViewModel.XmasTheme):
                if (vm.XmasTheme != _xmasOn)
                {
                    ApplyXmas(vm.XmasTheme);
                    if (vm.XmasTheme && vm.XmasSleigh)
                    {
                        _flight.FlyNow();
                    }
                }

                break;
            case nameof(MainViewModel.XmasSnow) or nameof(MainViewModel.XmasLights) or nameof(MainViewModel.XmasSleigh)
                or nameof(MainViewModel.XmasScenes) or nameof(MainViewModel.XmasSnowCaps):
                if (_xmasOn)
                {
                    ApplyXmas(true);
                    if (e.PropertyName == nameof(MainViewModel.XmasSleigh) && vm.XmasSleigh)
                    {
                        _flight.FlyNow();
                    }
                }

                break;
            case nameof(MainViewModel.AnimationsEnabled):
                if (_xmasOn)
                {
                    // Các lớp đọc Motion.Suppressed khi (bỏ) dừng: dừng hết rồi bật lại để áp dụng ngay (tắt = hình đứng yên).
                    XmasScenes.Stop();
                    ApplyXmas(true);
                }

                break;
            case nameof(MainViewModel.XmasMusicOn):
                SetMusic(vm.XmasMusicOn);
                break;
            case nameof(MainViewModel.XmasVolume):
                MusicVolume_Changed(vm.XmasVolume);
                break;
            case nameof(MainViewModel.IsSidebarCompact) or nameof(MainViewModel.SidebarWidth) or nameof(MainViewModel.SidebarScale)
                or nameof(MainViewModel.ShowNavList) or nameof(MainViewModel.ShowSystemHost):
                Dispatcher.UIThread.Post(FitVillage, DispatcherPriority.Background);
                break;
        }
    }

    private void XmasToggle_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm)
        {
            vm.XmasTheme = !_xmasOn;
        }
    }

    private void MusicToggle_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm)
        {
            return;
        }

        var target = !XmasMusic.Playing;
        if (vm.XmasMusicOn == target)
        {
            SetMusic(target);   // lựa chọn không đổi (vd lần phát trước lỗi): thử lại
        }
        else
        {
            vm.XmasMusicOn = target;   // OnAppearanceChanged → SetMusic
        }
    }

    private async void SetMusic(bool on)
    {
        try
        {
            if (on && !XmasMusic.Playing)
            {
                await XmasMusic.PlayAsync();
            }
            else if (!on && XmasMusic.Playing)
            {
                XmasMusic.Stop();
            }
        }
        catch (Exception ex)
        {
            DebugLog.Write("Xmas music: " + ex.Message);
        }

        UpdateXmasButtons();
    }

    private void MusicToggle_Wheel(object? sender, PointerWheelEventArgs e)
    {
        Sidebar.MusicVolumeSlider.Value = Math.Clamp(Sidebar.MusicVolumeSlider.Value + (e.Delta.Y > 0 ? 0.05 : -0.05), 0, 1);
        e.Handled = true;
    }

    /// <summary>Âm lượng đổi (thanh trượt ở thanh bên hoặc trong cửa sổ Giao diện): đồng bộ hai thanh, đổi âm lượng đang phát, lưu gom.</summary>
    private void MusicVolume_Changed(double value)
    {
        if (!_xmasReady || ViewModel is not { } vm)
        {
            return;
        }

        if (Math.Abs(Sidebar.MusicVolumeSlider.Value - value) > 0.001)
        {
            Sidebar.MusicVolumeSlider.Value = value;
        }

        XmasMusic.Volume = value;
        if (_volumeSave == null)
        {
            _volumeSave = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
            _volumeSave.Tick += (_, _) =>
            {
                _volumeSave.Stop();
                (DataContext as MainViewModel)?.SaveXmasSettings();
            };
        }

        _volumeSave.Stop();
        _volumeSave.Start();
        UpdateXmasButtons();
    }
}
