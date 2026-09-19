using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using PsViethoa.FpkgBuilder.App.ViewModels;
using PsViethoa.FpkgBuilder.Core.ExFat;
using PsViethoa.FpkgBuilder.Core.Services;

namespace PsViethoa.FpkgBuilder.App.Views;

/// <summary>
/// Chế độ "Hàng chờ": kéo–thả nhiều thư mục game / ảnh đĩa (hoặc cả ổ dump) để xếp hàng; chặn handler thả của cửa sổ chính
/// trong vùng này. Nhật ký của mục đang chọn tự cuộn xuống cuối.
/// </summary>
public partial class QueueView : UserControl
{
    public QueueView()
    {
        InitializeComponent();
        AddHandler(DragDrop.DragEnterEvent, OnDragOver);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
        QueueLogList.PropertyChanged += (_, e) =>
        {
            if (e.Property == ItemsControl.ItemsSourceProperty)
            {
                HookLog(e.OldValue as INotifyCollectionChanged, e.NewValue as INotifyCollectionChanged);
            }
        };
    }

    private void HookLog(INotifyCollectionChanged? previous, INotifyCollectionChanged? current)
    {
        if (previous != null)
        {
            previous.CollectionChanged -= OnLogChanged;
        }

        if (current != null)
        {
            current.CollectionChanged += OnLogChanged;
        }

        Avalonia.Threading.Dispatcher.UIThread.Post(ScrollLogToEnd, Avalonia.Threading.DispatcherPriority.Background);
    }

    private void OnLogChanged(object? sender, NotifyCollectionChangedEventArgs e) => ScrollLogToEnd();

    private QueueViewModel? ViewModel => DataContext as QueueViewModel;

    /// <summary>Mọi đường dẫn thả vào: thư mục, tệp ảnh/dự án, hoặc thư mục cha của tệp nằm trong thư mục game.</summary>
    public static IReadOnlyList<string> GetDroppedPaths(DragEventArgs e)
    {
        var result = new List<string>();
        var items = e.Data.GetFiles();
        if (items == null)
        {
            return result;
        }

        foreach (var item in items)
        {
            var path = item.TryGetLocalPath();
            if (path == null)
            {
                continue;
            }

            if (item is IStorageFolder || Directory.Exists(path))
            {
                result.Add(path);
                continue;
            }

            if (File.Exists(path) && (SourceLocator.HasGp5Extension(path) || SourceLocator.HasImageExtension(path) || ExFatImage.IsExFatFile(path) || UfsImage.IsUfsFile(path)))
            {
                result.Add(path);
                continue;
            }

            var parent = Path.GetDirectoryName(path);
            if (parent != null && Directory.Exists(Path.Combine(parent, "sce_sys")))
            {
                result.Add(parent);
            }
        }

        return result;
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = ViewModel != null && GetDroppedPaths(e).Count > 0 ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        var paths = GetDroppedPaths(e);
        if (ViewModel != null && paths.Count > 0)
        {
            await ViewModel.AddPathsAsync(paths);
        }
    }

    private void ScrollLogToEnd()
    {
        if (QueueLogList.ItemCount > 0)
        {
            QueueLogList.ScrollIntoView(QueueLogList.ItemCount - 1);
        }
    }
}
