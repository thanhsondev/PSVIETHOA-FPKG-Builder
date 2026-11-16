using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;

namespace PsViethoa.FpkgBuilder.App.Views;

/// <summary>Thanh bên: logo, các nhóm theo hệ máy và nút ở chân (ngôn ngữ, theme, cập nhật, hướng dẫn, thu gọn).</summary>
public sealed partial class SidebarView : UserControl
{
    public SidebarView()
    {
        AvaloniaXamlLoader.Load(this);
        if (OperatingSystem.IsMacOS())
        {
            // Phím tắt hiện theo hệ: macOS dùng ⌘K (Ctrl+K vẫn chạy).
            this.FindControl<TextBlock>("SearchShortcut")!.Text = "⌘K";
        }

        this.FindControl<Button>("HelpButton")!.Click += (_, _) => (TopLevel.GetTopLevel(this) as MainWindow)?.ShowHelp();
        this.FindControl<Button>("AppearanceButton")!.Click += (_, _) => (TopLevel.GetTopLevel(this) as MainWindow)?.ShowAppearance();
        var search = this.FindControl<TextBox>("NavSearch")!;
        search.KeyDown += (_, e) =>
        {
            if (DataContext is not ViewModels.MainViewModel vm)
            {
                return;
            }

            if (e.Key == Key.Enter)
            {
                vm.OpenFirstNavMatchCommand.Execute(null);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                vm.ClearNavFilterCommand.Execute(null);
                e.Handled = true;
            }
        };
    }

    // ───────── Giáng sinh (MainWindow.Xmas điều khiển) ─────────

    internal Button XmasToggleButton => this.FindControl<Button>("XmasToggle")!;

    internal Button MusicToggleButton => this.FindControl<Button>("MusicToggle")!;

    internal Slider MusicVolumeSlider => this.FindControl<Slider>("MusicVolume")!;

    internal Border VillageHostBorder => this.FindControl<Border>("VillageHost")!;

    internal ScrollViewer NavScrollViewer => this.FindControl<ScrollViewer>("NavScroll")!;

    private IBrush? _rootBackground, _rootBorder;

    /// <summary>
    /// Bật/tắt trang trí của thanh bên: mũ Noel, lời chúc và nền. Theme tối (<paramref name="day"/> = false): xanh thông hơi trong để thấy
    /// nền rặng thông phía sau. Theme sáng: thanh bên sáng theo trang ("ngày tuyết" — lớp xmasday, style trong Shell.axaml). Tắt thì trả
    /// lại dải màu gốc (thanh bên luôn tối).
    /// </summary>
    internal void SetFestive(bool on, bool day, bool snowCaps = true)
    {
        var root = this.FindControl<Border>("SidebarRoot")!;
        _rootBackground ??= root.Background;
        _rootBorder ??= root.BorderBrush;
        root.Background = !on ? _rootBackground : new SolidColorBrush(Color.Parse(day ? "#D9F4FAF7" : "#B80C2216"));
        root.BorderBrush = !on ? _rootBorder : new SolidColorBrush(Color.Parse(day ? "#C9DFD3" : "#1E3D2B"));
        Classes.Set("xmas", on);
        Classes.Set("xmasday", on && day);
        Classes.Set("xmascaps", on && snowCaps);   // tuyết đọng trên nút (tắt được trong cửa sổ Giao diện)
        this.FindControl<Avalonia.Controls.Shapes.Ellipse>("SidebarGlow")!.IsVisible = !on;
        this.FindControl<Control>("Hat")!.IsVisible = on;
        this.FindControl<TextBlock>("XmasGreeting")!.IsVisible = on;
    }

    /// <summary>Ctrl+K: đưa con trỏ vào ô tìm công cụ.</summary>
    public void FocusSearch()
    {
        var search = this.FindControl<TextBox>("NavSearch");
        search?.Focus();
        search?.SelectAll();
    }
}
