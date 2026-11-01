using Avalonia.Controls;
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
    }
}
