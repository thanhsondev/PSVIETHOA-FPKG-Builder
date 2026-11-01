using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace PsViethoa.FpkgBuilder.App.Controls;

/// <summary>
/// Trang dạng lưới Auto/* đặt trong ScrollViewer: nội dung cao bằng khung nhìn khi cửa sổ đủ cao (hàng * lấp phần còn lại như
/// bình thường), còn khi cửa sổ thấp hơn <c>MinContentHeight</c> thì nội dung giữ đúng chiều cao đó và trang cuộn được — thay vì
/// các khung bị ép mất chữ. Đặt trên ScrollViewer: <c>c:ViewportFill.MinContentHeight="720"</c>.
/// </summary>
/// <remarks>
/// ScrollViewer đo nội dung với chiều cao vô hạn, nên không thể chỉ dùng MinHeight: hàng * sẽ thành Auto và danh sách bên trong
/// (nhật ký, hàng đợi) mất ảo hoá. Ở đây chiều cao được đặt cụ thể theo khung nhìn.
/// </remarks>
public static class ViewportFill
{
    public static readonly AttachedProperty<double> MinContentHeightProperty =
        AvaloniaProperty.RegisterAttached<ScrollViewer, double>("MinContentHeight", typeof(ViewportFill), double.NaN);

    private static readonly ConditionalWeakTable<Control, object> Hooked = new();

    static ViewportFill()
    {
        MinContentHeightProperty.Changed.AddClassHandler<ScrollViewer>(OnMinContentHeightChanged);
    }

    public static double GetMinContentHeight(ScrollViewer element) => element.GetValue(MinContentHeightProperty);

    public static void SetMinContentHeight(ScrollViewer element, double value) => element.SetValue(MinContentHeightProperty, value);

    private static void OnMinContentHeightChanged(ScrollViewer scroll, AvaloniaPropertyChangedEventArgs e)
    {
        scroll.SizeChanged -= OnScrollSizeChanged;
        scroll.SizeChanged += OnScrollSizeChanged;
        Update(scroll);
    }

    private static void OnScrollSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (sender is ScrollViewer scroll)
        {
            Update(scroll);
        }
    }

    private static void Update(ScrollViewer scroll)
    {
        var min = GetMinContentHeight(scroll);
        if (double.IsNaN(min) || scroll.Content is not Control content || scroll.Bounds.Height <= 0)
        {
            return;
        }

        if (!Hooked.TryGetValue(content, out _))
        {
            // Nhật ký / hàng đợi tự cuộn tới dòng mới bằng ScrollIntoView; yêu cầu "đưa vào tầm nhìn" đó lan lên tới trang và kéo cả trang
            // trôi xuống. Yêu cầu xuất phát từ bên trong một danh sách chỉ được cuộn danh sách đó; ô nhập, nút… vẫn kéo trang như thường.
            Hooked.Add(content, new object());
            content.AddHandler(Control.RequestBringIntoViewEvent, (_, e) =>
            {
                if (e.TargetObject is Visual target && target.FindAncestorOfType<ListBox>(includeSelf: true) != null)
                {
                    e.Handled = true;
                }
            });

            // Yêu cầu "đưa vào tầm nhìn" trong lần bố cục đầu (trước khi bộ chặn trên được gắn) có thể đã đẩy trang xuống: trang mở ra
            // luôn bắt đầu từ đầu.
            Avalonia.Threading.Dispatcher.UIThread.Post(() => scroll.Offset = default, Avalonia.Threading.DispatcherPriority.Loaded);
        }

        var viewport = scroll.Bounds.Height - scroll.Padding.Top - scroll.Padding.Bottom - content.Margin.Top - content.Margin.Bottom;
        var height = Math.Max(viewport, min);
        if (double.IsNaN(content.Height) || Math.Abs(content.Height - height) > 0.5)
        {
            content.Height = height;
        }
    }
}
