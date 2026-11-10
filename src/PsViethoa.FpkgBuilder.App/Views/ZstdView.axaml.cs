using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using PsViethoa.FpkgBuilder.App.ViewModels;

namespace PsViethoa.FpkgBuilder.App.Views;

/// <summary>Trang ZSTD PS5 (beta): kéo thả thư mục game vào bất kỳ đâu; nhật ký tự cuộn xuống dòng mới nhất.</summary>
public sealed partial class ZstdView : UserControl
{
    private readonly ListBox _logList;
    private INotifyCollectionChanged? _hookedLog;

    public ZstdView()
    {
        AvaloniaXamlLoader.Load(this);
        _logList = this.FindControl<ListBox>("LogList")!;
        AddHandler(DragDrop.DragEnterEvent, OnDragOver);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
        DataContextChanged += (_, _) => HookLog();
    }

    private ZstdViewModel? ViewModel => DataContext as ZstdViewModel;

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

    private static string? FirstPath(DragEventArgs e) => e.Data.GetFiles()?.Select(item => item.TryGetLocalPath()).OfType<string>().FirstOrDefault();

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = ViewModel is { IsBusy: false } && FirstPath(e) is { } path && Directory.Exists(path) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        if (ViewModel != null && FirstPath(e) is { } path)
        {
            ViewModel.DropPath(path);
        }
    }
}
