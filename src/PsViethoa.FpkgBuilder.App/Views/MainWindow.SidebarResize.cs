using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Input;

namespace PsViethoa.FpkgBuilder.App.Views;

/// <summary>
/// Kéo mép phải thanh bên để đổi bề rộng (190–560 px): hẹp thì lưới công cụ thành một cột, rộng thì hai cột; nháy đúp = tự động.
/// Bề rộng lưu vào cài đặt. Trong lúc kéo tắt hiệu ứng trượt bề rộng để thanh bên bám theo chuột.
/// </summary>
public partial class MainWindow
{
    private bool _sidebarDragging;
    private Transitions? _sidebarTransitions;

    private void SetupSidebarResize()
    {
        SidebarGrip.PointerPressed += (_, e) =>
        {
            if (ViewModel == null || !e.GetCurrentPoint(SidebarGrip).Properties.IsLeftButtonPressed)
            {
                return;
            }

            if (e.ClickCount >= 2)
            {
                ViewModel.ResetSidebarWidth();
                e.Handled = true;
                return;
            }

            _sidebarDragging = true;
            _sidebarTransitions = Sidebar.Transitions;
            Sidebar.Transitions = null;
            e.Pointer.Capture(SidebarGrip);
            e.Handled = true;
        };
        SidebarGrip.PointerMoved += (_, e) =>
        {
            if (!_sidebarDragging || ViewModel == null)
            {
                return;
            }

            ViewModel.ResizeSidebar(e.GetPosition(this).X);
            e.Handled = true;
        };
        SidebarGrip.PointerReleased += (_, e) => EndSidebarDrag(e.Pointer);
        SidebarGrip.PointerCaptureLost += (_, _) => EndSidebarDrag(null);
    }

    private void EndSidebarDrag(IPointer? pointer)
    {
        if (!_sidebarDragging)
        {
            return;
        }

        _sidebarDragging = false;
        pointer?.Capture(null);
        Sidebar.Transitions = _sidebarTransitions;
    }
}
