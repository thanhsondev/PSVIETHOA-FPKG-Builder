using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;

namespace PsViethoa.FpkgBuilder.App.Services;

/// <summary>
/// Chuyển động ngắn, có mục đích (160–320 ms, ease-out): trang mới hiện lên, huy hiệu bật lên khi đổi nhóm. Tắt hẳn khi người dùng
/// tắt "Hiệu ứng chuyển động" hoặc khi chụp màn hình tự động (ảnh không bắt được khung hình dở dang).
/// </summary>
public static class Motion
{
    /// <summary>Người dùng tắt hiệu ứng (cài đặt) hoặc đang chụp màn hình tự động.</summary>
    public static bool Suppressed { get; set; }

    public static bool Enabled => !Suppressed;

    /// <summary>Nội dung vào khung nhìn: mờ → rõ và trồi lên vài điểm ảnh.</summary>
    public static void Enter(Visual? target, double rise = 12, int milliseconds = 240)
    {
        if (target == null)
        {
            return;
        }

        target.Opacity = 1;
        if (!Enabled)
        {
            return;
        }

        // Animation của TranslateTransform.Y phải chạy trên chính control: TransformAnimator của Avalonia tự tìm transform trong
        // RenderTransform. Chạy trên đối tượng TranslateTransform sẽ ném InvalidCastException trong Task — Task đó không ai đọc,
        // bị bộ thu gom rác ném lại và hiện thành "Lỗi bất ngờ" lúc nào đó sau này (ví dụ ngay khi đóng gói xong).
        EnsureTranslate(target);
        Observe(new Animation
        {
            Duration = TimeSpan.FromMilliseconds(milliseconds),
            Easing = new CubicEaseOut(),
            FillMode = FillMode.None,
            Children =
            {
                new KeyFrame { Cue = new Cue(0), Setters = { new Setter(Visual.OpacityProperty, 0d), new Setter(TranslateTransform.YProperty, rise) } },
                new KeyFrame { Cue = new Cue(1), Setters = { new Setter(Visual.OpacityProperty, 1d), new Setter(TranslateTransform.YProperty, 0d) } },
            },
        }.RunAsync(target));
    }

    /// <summary>Bật lên kiểu lò xo từ 70 %.</summary>
    public static void Pop(Visual? target, double from = 0.7, int milliseconds = 320)
    {
        if (target == null || !Enabled)
        {
            return;
        }

        EnsureScale(target);
        var animation = new Animation
        {
            Duration = TimeSpan.FromMilliseconds(milliseconds),
            Easing = new BackEaseOut(),
            Children =
            {
                new KeyFrame { Cue = new Cue(0), Setters = { new Setter(ScaleTransform.ScaleXProperty, from), new Setter(ScaleTransform.ScaleYProperty, from) } },
                new KeyFrame { Cue = new Cue(1), Setters = { new Setter(ScaleTransform.ScaleXProperty, 1d), new Setter(ScaleTransform.ScaleYProperty, 1d) } },
            },
        };
        Observe(animation.RunAsync(target));
    }

    /// <summary>Hiệu ứng chỉ để trang trí: lỗi của nó được ghi chẩn đoán rồi bỏ qua, không bao giờ thành lỗi của ứng dụng.</summary>
    private static void Observe(Task task)
    {
        if (task.IsCompleted)
        {
            if (task.IsFaulted)
            {
                DebugLog.Write("Motion: " + task.Exception?.GetBaseException().Message);
            }

            return;
        }

        task.ContinueWith(
            t => DebugLog.Write("Motion: " + t.Exception?.GetBaseException().Message),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static TranslateTransform EnsureTranslate(Visual target)
    {
        switch (target.RenderTransform)
        {
            case TranslateTransform translate:
                return translate;
            case TransformGroup group when group.Children.OfType<TranslateTransform>().FirstOrDefault() is { } inner:
                return inner;
        }

        var fresh = new TranslateTransform();
        target.RenderTransform = fresh;
        return fresh;
    }

    private static ScaleTransform EnsureScale(Visual target)
    {
        if (target.RenderTransform is ScaleTransform scale)
        {
            return scale;
        }

        target.RenderTransformOrigin = RelativePoint.Center;
        var fresh = new ScaleTransform(1, 1);
        target.RenderTransform = fresh;
        return fresh;
    }
}
