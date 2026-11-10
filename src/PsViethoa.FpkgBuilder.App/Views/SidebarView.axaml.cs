using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace PsViethoa.FpkgBuilder.App.Views;

/// <summary>Thanh bên: logo, các nhóm theo hệ máy và nút ở chân (ngôn ngữ, theme, cập nhật, hướng dẫn, thu gọn).</summary>
public sealed partial class SidebarView : UserControl
{
    public SidebarView()
    {
        AvaloniaXamlLoader.Load(this);
        this.FindControl<Button>("HelpButton")!.Click += (_, _) => (TopLevel.GetTopLevel(this) as MainWindow)?.ShowHelp();
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

    /// <summary>Ctrl+K: đưa con trỏ vào ô tìm công cụ.</summary>
    public void FocusSearch()
    {
        var search = this.FindControl<TextBox>("NavSearch");
        search?.Focus();
        search?.SelectAll();
    }
}
