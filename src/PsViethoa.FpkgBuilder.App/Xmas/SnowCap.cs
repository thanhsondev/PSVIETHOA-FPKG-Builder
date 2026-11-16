using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using PsViethoa.FpkgBuilder.App.Xmas.Gdi;

namespace PsViethoa.FpkgBuilder.App.Xmas;

/// <summary>
/// Lớp tuyết đọng trên mép trên một ô (ô công cụ / ô tìm kiếm ở thanh bên khi bật Noel): dải trắng lượn sóng vài px có giọt rủ —
/// hình <see cref="XmasArt.CardSnow"/> của DiskHealth. Tĩnh: mỗi cỡ (bề rộng điểm ảnh × kiểu × dày/mỏng) chỉ vẽ MỘT lần rồi dùng chung
/// cho mọi ô cùng cỡ, không vẽ lại theo khung hình. Đặt ở đầu ô với Margin âm (tuyết nhô lên trên mép).
/// </summary>
internal sealed class SnowCap : Control
{
    /// <summary>Khoảng nhô lên trên mép ô (px) — mép ô nằm ở y = Lift trong khung của lớp tuyết.</summary>
    public const double Lift = 5;

    public static readonly StyledProperty<bool> ThickProperty = AvaloniaProperty.Register<SnowCap, bool>(nameof(Thick));

    public static readonly StyledProperty<double> RadiusProperty = AvaloniaProperty.Register<SnowCap, double>(nameof(Radius), 8);

    private static readonly Dictionary<(int W, int H, int Seed, bool Thick, double Radius), WriteableBitmap> cache = [];

    static SnowCap()
    {
        AffectsRender<SnowCap>(ThickProperty, RadiusProperty);
        IsHitTestVisibleProperty.OverrideDefaultValue<SnowCap>(false);
        HeightProperty.OverrideDefaultValue<SnowCap>(14.0);
    }

    /// <summary>Ô đang chọn: tuyết dày hơn chút.</summary>
    public bool Thick
    {
        get => GetValue(ThickProperty);
        set => SetValue(ThickProperty, value);
    }

    /// <summary>Bán kính bo góc của ô bên dưới (tuyết thu vào trong hai góc bo).</summary>
    public double Radius
    {
        get => GetValue(RadiusProperty);
        set => SetValue(RadiusProperty, value);
    }

    private int Seed => (int)((uint)GetHashCode() % 4);

    public override void Render(Avalonia.Media.DrawingContext context)
    {
        double w = Bounds.Width, h = Bounds.Height;
        if (w < 8 || h < 4)
        {
            return;
        }

        double scale = TopLevel.GetTopLevel(this)?.RenderScaling is > 0 and var s ? s : 1;
        int pw = (int)Math.Ceiling(w * scale), ph = (int)Math.Ceiling(h * scale);
        var key = (pw, ph, Seed, Thick, Radius);
        if (!cache.TryGetValue(key, out var bitmap))
        {
            if (cache.Count > 64)
            {
                foreach (var old in cache.Values)
                {
                    old.Dispose();
                }

                cache.Clear();
            }

            bitmap = XmasBitmap.Create(pw, ph, scale);
            bool thick = Thick;
            float radius = (float)Radius, top = (float)Lift, width = (float)w;
            int seed = Seed;
            XmasBitmap.Paint(bitmap, (float)scale, g =>
            {
                if (thick)
                {
                    // dày hơn: phóng theo chiều dọc quanh mép ô
                    g.TranslateTransform(0, top);
                    g.ScaleTransform(1, 1.45f);
                    g.TranslateTransform(0, -top);
                }

                XmasArt.CardSnow(g, new RectangleF(0, top, width, 20), radius, seed);
            });
            cache[key] = bitmap;
        }

        context.DrawImage(bitmap, new Rect(0, 0, w, h));
    }
}
