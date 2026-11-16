using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace PsViethoa.FpkgBuilder.App.Views;

/// <summary>Tuỳ chỉnh giao diện: DataContext là MainViewModel, mọi lựa chọn áp dụng và lưu ngay — nút Đóng chỉ đóng cửa sổ.</summary>
public sealed partial class AppearanceWindow : Window
{
    public AppearanceWindow()
    {
        AvaloniaXamlLoader.Load(this);
        this.FindControl<Button>("CloseButton")!.Click += (_, _) => Close();
    }
}
