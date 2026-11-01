using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using CommunityToolkit.Mvvm.ComponentModel;
using PsViethoa.FpkgBuilder.Core.Localization;
using PsViethoa.FpkgBuilder.Core.Ps4;
using PsViethoa.FpkgBuilder.Core.Services;

namespace PsViethoa.FpkgBuilder.App.Views;

/// <summary>Một tệp trong PKG trên hộp chọn tệp xả.</summary>
public sealed partial class Ps4PickerRow : ObservableObject
{
    public Ps4PickerRow(Ps4PackageReader.PackageFileInfo file, bool selected)
    {
        Path = file.Path;
        IsMetadata = file.IsMetadata;
        SizeText = Formatters.Size(file.Bytes);
        _selected = selected;
    }

    public string Path { get; }

    public bool IsMetadata { get; }

    public string Area => Loc.T(IsMetadata ? "Ps4.Picker.AreaMeta" : "Ps4.Picker.AreaData");

    public string SizeText { get; }

    [ObservableProperty] private bool _selected;
}

/// <summary>Chọn từng tệp để xả (lọc theo tên, chọn phần đang hiện, xả tất cả). Như FilePickerWindow của bản Studio.</summary>
public sealed partial class Ps4FilePickerWindow : Window
{
    private readonly List<Ps4PickerRow> _rows = new();
    private readonly ListBox _list;
    private readonly TextBox _search;
    private readonly TextBlock _count;
    private (bool Accepted, string[]? Paths) _result;

    public Ps4FilePickerWindow()
    {
        AvaloniaXamlLoader.Load(this);
        _list = this.FindControl<ListBox>("FileList")!;
        _search = this.FindControl<TextBox>("SearchBox")!;
        _count = this.FindControl<TextBlock>("CountText")!;
        _search.TextChanged += (_, _) => ApplyFilter();
    }

    /// <summary>Accepted=false: huỷ. Paths=null: xả toàn bộ.</summary>
    public static async Task<(bool Accepted, string[]? Paths)> PickAsync(Window owner, IReadOnlyList<Ps4PackageReader.PackageFileInfo> files, IReadOnlyCollection<string>? previous)
    {
        var dialog = new Ps4FilePickerWindow();
        var selected = previous == null ? null : new HashSet<string>(previous, StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            var row = new Ps4PickerRow(file, selected?.Contains(file.Path) == true);
            row.PropertyChanged += (_, _) => dialog.UpdateCount();
            dialog._rows.Add(row);
        }

        dialog.ApplyFilter();
        await dialog.ShowDialog(owner);
        return dialog._result;
    }

    private IEnumerable<Ps4PickerRow> Visible()
    {
        var filter = _search.Text?.Trim() ?? string.Empty;
        return filter.Length == 0 ? _rows : _rows.Where(r => r.Path.Contains(filter, StringComparison.OrdinalIgnoreCase));
    }

    private void ApplyFilter()
    {
        _list.ItemsSource = Visible().ToList();
        UpdateCount();
    }

    private void UpdateCount() =>
        _count.Text = Loc.F("Ps4.Picker.Count", _rows.Count(r => r.Selected).ToString("N0"), _rows.Count.ToString("N0"));

    private void SelectVisible_Click(object? sender, RoutedEventArgs e)
    {
        foreach (var row in Visible())
        {
            row.Selected = true;
        }
    }

    private void Clear_Click(object? sender, RoutedEventArgs e)
    {
        foreach (var row in _rows)
        {
            row.Selected = false;
        }
    }

    private void All_Click(object? sender, RoutedEventArgs e)
    {
        _result = (true, null);
        Close();
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close();

    private void Accept_Click(object? sender, RoutedEventArgs e)
    {
        var chosen = _rows.Where(r => r.Selected).Select(r => r.Path).ToArray();
        if (chosen.Length == 0)
        {
            _count.Text = Loc.T("Ps4.Picker.NoneChosen");
            return;
        }

        _result = (true, chosen);
        Close();
    }
}
