using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using PsViethoa.FpkgBuilder.Core.Localization;
using PsViethoa.FpkgBuilder.Core.Saves;

namespace PsViethoa.FpkgBuilder.App.Views;

/// <summary>Một save PS5 của user khác trong danh sách chọn.</summary>
public sealed record TransferChoice(GarlicSave Save, string Title, string User);

/// <summary>Chọn một save PS5 của user khác để bốc về, ký lại và chép sang user đang nhập.</summary>
public sealed partial class TransferSaveWindow : Window
{
    private GarlicSave? _selected;

    public TransferSaveWindow() => AvaloniaXamlLoader.Load(this);

    public static async Task<GarlicSave?> PickAsync(Window owner, IReadOnlyList<GarlicSave> saves, IReadOnlyDictionary<string, string> names, string targetUser)
    {
        var dialog = new TransferSaveWindow();
        var list = dialog.FindControl<ListBox>("SaveList")!;
        dialog.FindControl<TextBlock>("HintText")!.Text = Loc.F("Save.Transfer.Hint", targetUser);
        list.ItemsSource = saves.Select(save =>
            {
                var user = names.TryGetValue(save.Uid.ToLowerInvariant(), out var name) ? $"{name} · {save.Uid}" : save.Uid;
                return new TransferChoice(save, $"{save.TitleId} / {save.SaveName}", user);
            })
            .ToList();
        list.SelectedIndex = 0;
        await dialog.ShowDialog(owner);
        return dialog._selected;
    }

    private void Run_Click(object? sender, RoutedEventArgs e)
    {
        if (this.FindControl<ListBox>("SaveList")!.SelectedItem is TransferChoice choice)
        {
            _selected = choice.Save;
            Close();
        }
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close();

    private void List_DoubleTapped(object? sender, TappedEventArgs e) => Run_Click(sender, e);
}
