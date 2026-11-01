using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PsViethoa.FpkgBuilder.App.ViewModels;

namespace PsViethoa.FpkgBuilder.App.Views;

/// <summary>
/// Trang FTP PS5: kéo tệp từ khung máy tính sang khung PS5 (tải lên) hoặc ngược lại (tải về), thả lên một thư mục để truyền thẳng
/// vào thư mục đó; kéo từ Finder/Explorer vào khung PS5 để tải lên, vào khung máy tính để mở thư mục đó. Kéo tệp PS5 thả lên thư mục
/// PS5 khác để di chuyển. Phím: Enter mở / truyền, Delete xoá, F2 đổi tên, F5 làm mới, Backspace lên thư mục cha.
/// </summary>
public sealed partial class FtpView : UserControl
{
    private const string LocalFormat = "psviethoa/ftp-local";
    private const string RemoteFormat = "psviethoa/ftp-remote";

    private readonly ListBox _localList;
    private readonly ListBox _remoteList;
    private readonly ListBox _logList;
    private readonly Border _localPane;
    private readonly Border _remotePane;
    private Point? _dragStart;
    private ListBox? _dragSource;
    private PointerPressedEventArgs? _dragPress;
    private INotifyCollectionChanged? _hookedLog;

    public FtpView()
    {
        AvaloniaXamlLoader.Load(this);
        _localList = this.FindControl<ListBox>("LocalList")!;
        _remoteList = this.FindControl<ListBox>("RemoteList")!;
        _logList = this.FindControl<ListBox>("FtpLogList")!;
        _localPane = this.FindControl<Border>("LocalPane")!;
        _remotePane = this.FindControl<Border>("RemotePane")!;

        _localList.SelectionChanged += (_, _) => Sync(_localList, ViewModel?.SelectedLocal);
        _remoteList.SelectionChanged += (_, _) => Sync(_remoteList, ViewModel?.SelectedRemote);
        _localList.DoubleTapped += (_, e) => Activate(_localList, e, remote: false);
        _remoteList.DoubleTapped += (_, e) => Activate(_remoteList, e, remote: true);
        _localList.KeyDown += (_, e) => OnListKey(e, remote: false);
        _remoteList.KeyDown += (_, e) => OnListKey(e, remote: true);

        foreach (var list in new[] { _localList, _remoteList })
        {
            list.AddHandler(PointerPressedEvent, OnListPointerPressed, RoutingStrategies.Tunnel);
            list.AddHandler(PointerMovedEvent, OnListPointerMoved, RoutingStrategies.Tunnel);
            list.AddHandler(PointerReleasedEvent, (_, _) => ResetDrag(), RoutingStrategies.Tunnel);
        }

        foreach (var pane in new[] { _localPane, _remotePane })
        {
            pane.AddHandler(DragDrop.DragEnterEvent, OnDragOver);
            pane.AddHandler(DragDrop.DragOverEvent, OnDragOver);
            pane.AddHandler(DragDrop.DragLeaveEvent, (_, _) => pane.Classes.Remove("droptarget"));
            pane.AddHandler(DragDrop.DropEvent, OnDrop);
        }

        this.FindControl<TextBox>("LocalPathBox")!.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                ViewModel?.LocalGoCommand.Execute(null);
                e.Handled = true;
            }
        };
        this.FindControl<TextBox>("RemotePathBox")!.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                ViewModel?.RemoteGoCommand.Execute(null);
                e.Handled = true;
            }
        };
        DataContextChanged += (_, _) => HookLog();
    }

    /// <summary>Chiều cao tối thiểu của trang: thêm chỗ cho khung nhật ký lệnh FTP khi mở.</summary>
    private void UpdatePageMinHeight()
    {
        var scroll = this.FindControl<ScrollViewer>("PageScroll");
        if (scroll != null)
        {
            Controls.ViewportFill.SetMinContentHeight(scroll, ViewModel?.ShowLog == true ? 840 : 680);
        }
    }

    private FtpViewModel? ViewModel => DataContext as FtpViewModel;

    private void HookLog()
    {
        if (_hookedLog != null)
        {
            _hookedLog.CollectionChanged -= OnLogChanged;
        }

        _hookedLog = ViewModel?.LogLines;
        if (_hookedLog != null)
        {
            _hookedLog.CollectionChanged += OnLogChanged;
        }

        if (ViewModel is { } vm)
        {
            vm.PropertyChanged -= OnViewModelPropertyChanged;
            vm.PropertyChanged += OnViewModelPropertyChanged;
        }

        UpdatePageMinHeight();
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FtpViewModel.ShowLog))
        {
            UpdatePageMinHeight();
        }
    }

    private void OnLogChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        Dispatcher.UIThread.Post(() =>
        {
            if (_logList.IsVisible && _logList.ItemCount > 0)
            {
                _logList.ScrollIntoView(_logList.ItemCount - 1);
            }
        }, DispatcherPriority.Background);

    private static void Sync(ListBox list, ICollection<FtpFileRow>? target)
    {
        if (target == null)
        {
            return;
        }

        target.Clear();
        foreach (var row in list.SelectedItems?.OfType<FtpFileRow>() ?? [])
        {
            target.Add(row);
        }
    }

    private static FtpFileRow? RowAt(object? source) =>
        (source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext as FtpFileRow;

    private async void Activate(ListBox list, TappedEventArgs e, bool remote)
    {
        if (ViewModel == null || RowAt(e.Source) is not { } row)
        {
            return;
        }

        if (remote)
        {
            await ViewModel.RemoteActivateAsync(row);
        }
        else
        {
            ViewModel.LocalActivate(row);
        }
    }

    private async void OnListKey(KeyEventArgs e, bool remote)
    {
        var vm = ViewModel;
        if (vm == null)
        {
            return;
        }

        e.Handled = true;
        switch (e.Key)
        {
            case Key.Enter when remote:
                if (vm.SelectedRemote.Count == 1 && vm.SelectedRemote[0].IsDirectory)
                {
                    await vm.RemoteActivateAsync(vm.SelectedRemote[0]);
                }
                else
                {
                    await vm.DownloadSelectedCommand.ExecuteAsync(null);
                }

                break;
            case Key.Enter:
                if (vm.SelectedLocal.Count == 1 && vm.SelectedLocal[0].IsDirectory)
                {
                    vm.LocalActivate(vm.SelectedLocal[0]);
                }
                else
                {
                    vm.UploadSelectedCommand.Execute(null);
                }

                break;
            case Key.Delete:
                await (remote ? vm.RemoteDeleteCommand : vm.LocalDeleteCommand).ExecuteAsync(null);
                break;
            case Key.F2:
                await (remote ? vm.RemoteRenameCommand : vm.LocalRenameCommand).ExecuteAsync(null);
                break;
            case Key.F5:
                if (remote)
                {
                    await vm.RemoteRefreshCommand.ExecuteAsync(null);
                }
                else
                {
                    vm.LocalRefreshCommand.Execute(null);
                }

                break;
            case Key.Back:
                if (remote)
                {
                    await vm.RemoteUpCommand.ExecuteAsync(null);
                }
                else
                {
                    vm.LocalUpCommand.Execute(null);
                }

                break;
            default:
                e.Handled = false;
                break;
        }
    }

    // ── Kéo từ một khung ────────────────────────────────────────────────────────────────────────────────────────────

    private void OnListPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is ListBox list && e.GetCurrentPoint(list).Properties.IsLeftButtonPressed && RowAt(e.Source) is { IsParent: false })
        {
            _dragStart = e.GetPosition(list);
            _dragSource = list;
            _dragPress = e;
        }
    }

    private void ResetDrag()
    {
        _dragStart = null;
        _dragSource = null;
        _dragPress = null;
    }

    private async void OnListPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragStart is not { } start || _dragSource == null || _dragPress == null || sender != _dragSource)
        {
            return;
        }

        var position = e.GetPosition(_dragSource);
        if (Math.Abs(position.X - start.X) < 6 && Math.Abs(position.Y - start.Y) < 6)
        {
            return;
        }

        var remote = _dragSource == _remoteList;
        var rows = _dragSource.SelectedItems?.OfType<FtpFileRow>().Where(r => !r.IsParent).ToList() ?? [];
        if (RowAt(_dragPress.Source) is { IsParent: false } pressed && !rows.Contains(pressed))
        {
            rows = [pressed];
        }

        var press = _dragPress;
        ResetDrag();
        if (rows.Count == 0)
        {
            return;
        }

        var data = new DataObject();
        data.Set(remote ? RemoteFormat : LocalFormat, rows.Select(r => r.FullPath).ToArray());
        await DragDrop.DoDragDrop(press, data, remote ? DragDropEffects.Copy | DragDropEffects.Move : DragDropEffects.Copy);
    }

    // ── Thả vào một khung ───────────────────────────────────────────────────────────────────────────────────────────

    private static string[]? Internal(DragEventArgs e, string format) => e.Data.Contains(format) ? e.Data.Get(format) as string[] : null;

    private static List<string> OsPaths(DragEventArgs e) => e.Data.GetFiles()?.Select(item => item.TryGetLocalPath()).OfType<string>().ToList() ?? [];

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        if (sender is not Border pane || ViewModel is not { } vm)
        {
            return;
        }

        var toRemote = pane == _remotePane;
        var local = Internal(e, LocalFormat);
        var remote = Internal(e, RemoteFormat);
        var os = local == null && remote == null ? OsPaths(e) : [];
        var accept = toRemote
            ? vm.IsConnected && (local != null || os.Count > 0 || (remote != null && RowAt(e.Source) is { IsDirectory: true }))
            : remote != null || os.Count > 0;
        e.DragEffects = accept ? (toRemote && remote != null ? DragDropEffects.Move : DragDropEffects.Copy) : DragDropEffects.None;
        if (accept)
        {
            pane.Classes.Add("droptarget");
        }
        else
        {
            pane.Classes.Remove("droptarget");
        }

        e.Handled = true;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        if (sender is not Border pane || ViewModel is not { } vm)
        {
            return;
        }

        pane.Classes.Remove("droptarget");
        var target = RowAt(e.Source) is { IsDirectory: true, IsParent: false } folder ? folder : null;
        var local = Internal(e, LocalFormat);
        var remote = Internal(e, RemoteFormat);
        if (pane == _remotePane)
        {
            if (local != null)
            {
                vm.UploadPaths(local, target?.FullPath);
            }
            else if (remote != null && target != null)
            {
                await vm.MoveRemoteAsync(vm.RemoteRows.Where(r => remote.Contains(r.FullPath)).ToList(), target.FullPath);
            }
            else if (OsPaths(e) is { Count: > 0 } paths)
            {
                vm.UploadPaths(paths, target?.FullPath);
            }

            return;
        }

        if (remote != null)
        {
            var rows = vm.RemoteRows.Where(r => remote.Contains(r.FullPath)).ToList();
            await vm.DownloadRowsAsync(rows, target?.FullPath);
        }
        else if (OsPaths(e) is { Count: > 0 } paths)
        {
            // Thả từ Finder/Explorer vào khung máy tính: mở thư mục đó (hoặc thư mục chứa tệp).
            var first = paths[0];
            vm.LoadLocal(Directory.Exists(first) ? first : Path.GetDirectoryName(first) ?? first);
        }
    }
}
