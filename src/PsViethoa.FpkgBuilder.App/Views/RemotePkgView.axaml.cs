using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PsViethoa.FpkgBuilder.App.ViewModels;

namespace PsViethoa.FpkgBuilder.App.Views;

/// <summary>
/// Trang FPKG trên PS5: Enter ở ô IP thì quét, Enter ở ô lọc thì lọc ngay, Space / nháy đúp tích tệp đang chọn, Esc dừng việc đang chạy,
/// "Mở rộng" ẩn cột game để danh sách tệp rộng hết cỡ; nhật ký tự cuộn xuống dòng mới nhất (gộp, ưu tiên Background).
/// </summary>
public sealed partial class RemotePkgView : UserControl
{
    private readonly ListBox _logList;
    private readonly ListBox _fileList;
    private readonly Grid _body;
    private readonly Border _gamesCard;
    private RemotePkgViewModel? _hooked;
    private bool _scrollQueued;

    public RemotePkgView()
    {
        AvaloniaXamlLoader.Load(this);
        _logList = this.FindControl<ListBox>("LogList")!;
        _fileList = this.FindControl<ListBox>("FileList")!;
        _body = this.FindControl<Grid>("BodyGrid")!;
        _gamesCard = this.FindControl<Border>("GamesCard")!;
        _fileList.AddHandler(KeyDownEvent, OnFileListKeyDown, RoutingStrategies.Tunnel);
        _fileList.DoubleTapped += OnFileListDoubleTapped;
        this.FindControl<TextBox>("FileFilterBox")!.AddHandler(KeyDownEvent, OnFilterKeyDown, RoutingStrategies.Tunnel);
        this.FindControl<TextBox>("HostBox")!.AddHandler(KeyDownEvent, OnHostKeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, OnViewKeyDown, RoutingStrategies.Tunnel);
        DataContextChanged += (_, _) => Hook();
    }

    private RemotePkgViewModel? ViewModel => DataContext as RemotePkgViewModel;

    private void Hook()
    {
        if (_hooked != null)
        {
            _hooked.LogEntries.CollectionChanged -= OnLogChanged;
            _hooked.PropertyChanged -= OnViewModelChanged;
        }

        _hooked = ViewModel;
        if (_hooked == null)
        {
            return;
        }

        _hooked.LogEntries.CollectionChanged += OnLogChanged;
        _hooked.PropertyChanged += OnViewModelChanged;
        ApplyExpanded(_hooked.IsExpanded);

        // Kiểm thử tự chạy (PSVIETHOA_REMOTEPKG_TEST): đợi cửa sổ dựng xong rồi quét → mở → trích.
        var vm = _hooked;
        DispatcherTimer.RunOnce(() => _ = vm.RunSelfTestIfRequestedAsync(), TimeSpan.FromSeconds(1.5));
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RemotePkgViewModel.IsExpanded) && ViewModel is { } vm)
        {
            ApplyExpanded(vm.IsExpanded);
        }
    }

    /// <summary>Mở rộng: ẩn cột game (bề rộng 0) để danh sách tệp chiếm hết chiều ngang.</summary>
    private void ApplyExpanded(bool expanded)
    {
        var columns = _body.ColumnDefinitions;
        columns[0].MinWidth = expanded ? 0 : 320;
        columns[0].Width = expanded ? new GridLength(0) : new GridLength(0.75, GridUnitType.Star);
        columns[1].Width = new GridLength(expanded ? 0 : 14);
        _gamesCard.IsVisible = !expanded;
    }

    private void OnLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_scrollQueued)
        {
            return;
        }

        _scrollQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _scrollQueued = false;
            if (_logList.ItemCount > 0)
            {
                _logList.ScrollIntoView(_logList.ItemCount - 1);
            }
        }, DispatcherPriority.Background);
    }

    private List<RemoteFileRow> SelectedRows() => _fileList.SelectedItems?.OfType<RemoteFileRow>().ToList() ?? new List<RemoteFileRow>();

    private void OnFileListKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space && e.KeyModifiers == KeyModifiers.None && ViewModel is { } vm)
        {
            vm.ToggleChecked(SelectedRows());
            e.Handled = true;
        }
    }

    private void OnFileListDoubleTapped(object? sender, TappedEventArgs e)
    {
        // Nháy đúp lên chính ô tích thì ô tích đã tự đảo hai lần — không đảo thêm.
        if (e.Source is Visual visual && visual.FindAncestorOfType<CheckBox>(includeSelf: true) != null)
        {
            return;
        }

        if (ViewModel is { } vm && _fileList.SelectedItem is RemoteFileRow row)
        {
            vm.ToggleChecked([row]);
        }
    }

    private void OnFilterKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && ViewModel is { } vm)
        {
            vm.ApplyFileFilter();
            e.Handled = true;
        }
    }

    private void OnHostKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && ViewModel is { } vm && vm.ScanCommand.CanExecute(null))
        {
            vm.ScanCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnViewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && ViewModel is { IsBusy: true } vm)
        {
            vm.CancelCommand.Execute(null);
            e.Handled = true;
        }
    }
}
