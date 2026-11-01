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
/// Trang PS4 PKG: kéo thả tệp .pkg / thư mục vào bất kỳ đâu (thả lên ô base thì vào ô base), phím ↑/↓ ở ô phiên bản tăng/giảm 0.01,
/// rời ô thì chuẩn hoá dạng NN.NN. Nhật ký tự cuộn xuống dòng mới nhất.
/// </summary>
public sealed partial class Ps4View : UserControl
{
    private readonly ListBox _logList;
    private readonly TextBox _versionBox;
    private INotifyCollectionChanged? _hookedLog;

    public Ps4View()
    {
        AvaloniaXamlLoader.Load(this);
        _logList = this.FindControl<ListBox>("LogList")!;
        _versionBox = this.FindControl<TextBox>("VersionBox")!;
        AddHandler(DragDrop.DragEnterEvent, OnDragOver);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
        _versionBox.AddHandler(KeyDownEvent, OnVersionKeyDown, RoutingStrategies.Tunnel);
        _versionBox.LostFocus += (_, _) => ViewModel?.NormalizeVersion();
        DataContextChanged += (_, _) => HookLog();

        // Form hẹp (cửa sổ nhỏ): nút chọn PKG / thư mục chỉ còn biểu tượng để ô đường dẫn không bị bóp còn vài ký tự.
        var form = this.FindControl<StackPanel>("SettingsForm")!;
        form.SizeChanged += (_, e) => form.Classes.Set("compact", e.NewSize.Width < 560);
    }

    private Ps4ViewModel? ViewModel => DataContext as Ps4ViewModel;

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

    private void OnVersionKeyDown(object? sender, KeyEventArgs e)
    {
        if (ViewModel == null || e.Key is not (Key.Up or Key.Down))
        {
            return;
        }

        if (e.Key == Key.Up)
        {
            ViewModel.VersionUpCommand.Execute(null);
        }
        else
        {
            ViewModel.VersionDownCommand.Execute(null);
        }

        e.Handled = true;
    }

    private static string? FirstPath(DragEventArgs e) => e.Data.GetFiles()?.Select(item => item.TryGetLocalPath()).OfType<string>().FirstOrDefault();

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = ViewModel is { IsBusy: false } && FirstPath(e) != null ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        if (ViewModel == null || FirstPath(e) is not { } path)
        {
            return;
        }

        var toBase = e.Source is Visual visual && visual.FindAncestorOfType<TextBox>(includeSelf: true)?.Name == "BaseBox";
        ViewModel.DropPath(path, toBase);
    }
}
