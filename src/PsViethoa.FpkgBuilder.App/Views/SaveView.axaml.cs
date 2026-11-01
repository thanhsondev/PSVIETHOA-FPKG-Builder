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
/// Trang Resign save: cả trang nhận tệp / thư mục kéo thả (chặn handler thả của cửa sổ chính), bấm vào vùng thả để chọn tệp, Delete bỏ
/// mục chọn, Enter / nhấp đúp mở vị trí. Nhật ký tự cuộn xuống dòng mới nhất.
/// </summary>
public sealed partial class SaveView : UserControl
{
    private readonly ListBox _logList;
    private readonly Border _dropZone;
    private INotifyCollectionChanged? _hookedLog;

    public SaveView()
    {
        AvaloniaXamlLoader.Load(this);
        _logList = this.FindControl<ListBox>("LogList")!;
        _dropZone = this.FindControl<Border>("DropZone")!;
        AddHandler(DragDrop.DragEnterEvent, OnDragOver);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        AddHandler(DragDrop.DropEvent, OnDrop);
        _dropZone.PointerReleased += OnDropZoneReleased;
        DataContextChanged += (_, _) => HookLog();

        // Vùng thả hẹp: hai nút "Thêm tệp / Thêm thư mục" xuống hàng dưới, chữ hướng dẫn được cả chiều ngang.
        var dropButtons = this.FindControl<StackPanel>("DropButtons")!;
        _dropZone.SizeChanged += (_, e) =>
        {
            var narrow = e.NewSize.Width < 600;
            Grid.SetRow(dropButtons, narrow ? 1 : 0);
            Grid.SetColumn(dropButtons, narrow ? 1 : 2);
            Grid.SetColumnSpan(dropButtons, narrow ? 2 : 1);
            dropButtons.HorizontalAlignment = narrow ? Avalonia.Layout.HorizontalAlignment.Left : Avalonia.Layout.HorizontalAlignment.Stretch;
            dropButtons.Margin = narrow ? new Thickness(0, 10, 0, 0) : new Thickness(12, 0, 0, 0);
        };
    }

    private SaveViewModel? ViewModel => DataContext as SaveViewModel;

    private void HookLog()
    {
        if (_hookedLog != null)
        {
            _hookedLog.CollectionChanged -= OnLogChanged;
        }

        _hookedLog = ViewModel?.VisibleLog;
        if (_hookedLog != null)
        {
            _hookedLog.CollectionChanged += OnLogChanged;
        }
    }

    private void OnLogChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        Dispatcher.UIThread.Post(() =>
        {
            if (_logList.ItemCount > 0)
            {
                _logList.ScrollIntoView(_logList.ItemCount - 1);
            }
        }, DispatcherPriority.Background);

    private static List<string> DroppedPaths(DragEventArgs e) =>
        e.Data.GetFiles()?.Select(item => item.TryGetLocalPath()).OfType<string>().ToList() ?? new List<string>();

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        var accept = ViewModel is { IsBusy: false } && DroppedPaths(e).Count > 0;
        e.DragEffects = accept ? DragDropEffects.Copy : DragDropEffects.None;
        if (ViewModel != null)
        {
            ViewModel.IsDropTarget = accept;
        }

        e.Handled = true;
    }

    private void OnDragLeave(object? sender, DragEventArgs e)
    {
        if (ViewModel != null)
        {
            ViewModel.IsDropTarget = false;
        }
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        if (ViewModel == null)
        {
            return;
        }

        ViewModel.IsDropTarget = false;
        ViewModel.AddPaths(DroppedPaths(e));
    }

    /// <summary>Bấm vào khoảng trống của vùng thả (không phải nút) thì mở hộp chọn tệp.</summary>
    private void OnDropZoneReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Left || e.Source is Visual visual && visual.FindAncestorOfType<Button>(includeSelf: true) != null)
        {
            return;
        }

        ViewModel?.AddFilesCommand.Execute(null);
    }

    private void QueueList_DoubleTapped(object? sender, TappedEventArgs e) => ViewModel?.OpenLocationCommand.Execute(null);

    private void QueueList_KeyDown(object? sender, KeyEventArgs e)
    {
        if (ViewModel == null)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Delete or Key.Back when ViewModel.RemoveSelectedCommand.CanExecute(null):
                ViewModel.RemoveSelectedCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Enter:
                ViewModel.OpenLocationCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }
}
