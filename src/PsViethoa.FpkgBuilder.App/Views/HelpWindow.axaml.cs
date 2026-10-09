using Avalonia.Controls;
using Avalonia.Interactivity;

namespace PsViethoa.FpkgBuilder.App.Views;

public partial class HelpWindow : Window
{
    public HelpWindow()
    {
        InitializeComponent();
        VersionText.Text = $"{AppInfo.Name} {AppInfo.Version} · {Core.Localization.Loc.F("App.Credits", Core.Services.BuildEngine.LibraryVersion)}";
        Opened += (_, _) => FitToScreen();
    }

    /// <summary>Màn hình thấp hơn cửa sổ (laptop 1366×768): thu cho vừa vùng làm việc và đưa trọn vào màn hình.</summary>
    private void FitToScreen()
    {
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen?.WorkingArea is not { } work)
        {
            return;
        }

        // Tỉ lệ của màn hình (macOS báo WorkingArea theo point), không phải RenderScaling — xem MainWindow.OnOpened.
        var scale = screen.Scaling <= 0 ? 1 : screen.Scaling;
        var maxWidth = work.Width / scale - 24;
        var maxHeight = work.Height / scale - 24;
        MinWidth = Math.Min(MinWidth, maxWidth);
        MinHeight = Math.Min(MinHeight, maxHeight);
        Width = Math.Min(Width, maxWidth);
        Height = Math.Min(Height, maxHeight);
        var x = Math.Clamp(Position.X, work.X, Math.Max(work.X, work.Right - (int)(Width * scale)));
        var y = Math.Clamp(Position.Y, work.Y, Math.Max(work.Y, work.Bottom - (int)(Height * scale)));
        Position = new Avalonia.PixelPoint(x, y);
    }

    /// <summary>Cuộn nội dung hướng dẫn xuống cuối (dùng khi chụp màn hình kiểm thử).</summary>
    public void ScrollToEnd()
    {
        HelpScroll.ScrollToEnd();
        HelpScroll.UpdateLayout();
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();

    private async void OpenAuthor_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            await Launcher.LaunchUriAsync(new Uri(AppInfo.AuthorUrl));
        }
        catch (Exception)
        {
        }
    }

    protected override void OnKeyDown(Avalonia.Input.KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Avalonia.Input.Key.Escape)
        {
            e.Handled = true;
            Close();
        }
    }
}
