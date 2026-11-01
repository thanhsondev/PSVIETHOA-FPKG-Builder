using Avalonia;
using Avalonia.Threading;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using PsViethoa.FpkgBuilder.App.Services;
using PsViethoa.FpkgBuilder.App.ViewModels;
using PsViethoa.FpkgBuilder.App.Views;

namespace PsViethoa.FpkgBuilder.App;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (DebugLog.Enabled)
        {
            // LogToTrace() chỉ ghi vào System.Diagnostics.Trace (không có sink mặc định): khi PSVIETHOA_DEBUG=1 đưa cảnh báo
            // binding/XAML của Avalonia ra stderr để kiểm tra giao diện bằng các hook chụp màn hình.
            System.Diagnostics.Trace.Listeners.Add(new System.Diagnostics.TextWriterTraceListener(Console.Error));
        }

        InstallExceptionSafetyNet();

        // Bản sao lưu save và log PS4 nằm chung thư mục dữ liệu của ứng dụng (cạnh settings.json).
        Core.Saves.SaveResigner.BackupRoot = System.IO.Path.Combine(SettingsService.Directory, "save-backups");
        Core.Ps4.Ps4JobRunner.LogRoot = System.IO.Path.Combine(SettingsService.Directory, "ps4-logs");

        var settings = SettingsService.Load();
        Core.Localization.Loc.Current.SetLanguage(settings.Language);
        RequestedThemeVariant = settings.Theme == "Light" ? ThemeVariant.Light : ThemeVariant.Dark;

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow();
            var viewModel = new MainViewModel(settings, new DialogService(window));
            window.DataContext = viewModel;
            desktop.MainWindow = window;
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Lưới an toàn: lỗi không bắt được trên luồng giao diện (ví dụ khi trích gói rất lớn) được ghi vào error.log và
    /// đưa vào nhật ký thay vì làm sập ứng dụng; lỗi Task không quan sát cũng được ghi lại.
    /// </summary>
    private void InstallExceptionSafetyNet()
    {
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            CrashLog.Write(e.Exception);
            DebugLog.Write("Unhandled UI exception: " + e.Exception);
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow.DataContext: MainViewModel viewModel })
            {
                viewModel.ReportUnhandledException(e.Exception);
                e.Handled = true;
            }
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            CrashLog.Write(e.Exception);
            e.SetObserved();
        };

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception exception)
            {
                CrashLog.Write(exception);
            }
        };
    }

    public static void ApplyTheme(bool dark)
    {
        if (Current != null)
        {
            Current.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
        }
    }
}
