using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace PsViethoa.FpkgBuilder.App.Views;

/// <summary>Hộp nhập một dòng chữ (đổi tên, tạo thư mục). <c>validate</c> trả về thông báo lỗi hoặc null khi hợp lệ.</summary>
public sealed partial class InputDialog : Window
{
    private Func<string, string?>? _validate;
    private string? _result;

    public InputDialog() => AvaloniaXamlLoader.Load(this);

    public static async Task<string?> ShowAsync(Window owner, string title, string message, string value, string okLabel, Func<string, string?>? validate = null)
    {
        var dialog = new InputDialog { Title = title, _validate = validate };
        dialog.FindControl<TextBlock>("TitleText")!.Text = title;
        var messageText = dialog.FindControl<TextBlock>("MessageText")!;
        messageText.Text = message;
        messageText.IsVisible = message.Length > 0;
        dialog.FindControl<Button>("OkButton")!.Content = okLabel;
        var box = dialog.FindControl<TextBox>("ValueBox")!;
        box.Text = value;
        dialog.Opened += (_, _) =>
        {
            box.Focus();
            var dot = value.LastIndexOf('.');
            box.SelectionStart = 0;
            box.SelectionEnd = dot > 0 ? dot : value.Length;
        };
        await dialog.ShowDialog(owner);
        return dialog._result;
    }

    private void Ok_Click(object? sender, RoutedEventArgs e)
    {
        var text = this.FindControl<TextBox>("ValueBox")!.Text?.Trim() ?? string.Empty;
        var error = text.Length == 0 ? string.Empty : _validate?.Invoke(text);
        if (error != null)
        {
            var errorText = this.FindControl<TextBlock>("ErrorText")!;
            errorText.Text = error;
            errorText.IsVisible = error.Length > 0;
            return;
        }

        _result = text;
        Close();
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close();
}
