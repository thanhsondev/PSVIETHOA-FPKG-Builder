using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using PsViethoa.FpkgBuilder.App.Services;
using PsViethoa.FpkgBuilder.App.Xmas.Gdi;
using SkiaSharp;
using Color = PsViethoa.FpkgBuilder.App.Xmas.Gdi.Color;

namespace PsViethoa.FpkgBuilder.App.Xmas;

/// <summary>Hàm phụ GDI+ mà bộ hình DiskHealth dùng (port từ Studio).</summary>
internal static class Draw
{
    public static GraphicsPath Round(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 0) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public static void Fill(Graphics g, RectangleF r, float radius, Color c)
    {
        using var p = Round(r, radius);
        using var b = new SolidBrush(c);
        g.FillPath(b, p);
    }
}

static partial class XmasArt
{
    /// <summary>Nền Giáng sinh như DiskHealth: chuyển sắc xanh thông + đốm sáng mờ + trăng, sao, rặng thông phủ tuyết (vẽ một lần).</summary>
    public static void PaintFestive(Graphics g, int width, int height)
    {
        width = Math.Max(1, width); height = Math.Max(1, height);
        var rect = new Rectangle(0, 0, width, height);
        using (var grad = new LinearGradientBrush(rect, Color.FromArgb(16, 48, 32), Color.FromArgb(9, 28, 20), LinearGradientMode.Vertical))
            g.FillRectangle(grad, rect);
        var rnd = new Random(0x5A6F);
        Color[] bokeh = [Color.FromArgb(70, 226, 183, 92), Color.FromArgb(55, 220, 70, 70), Color.FromArgb(50, 120, 210, 140)];
        for (int i = 0; i < 16; i++)
        {
            float bx = rnd.Next(width), by = rnd.Next(height), br = 6 + rnd.Next(26);
            var c = bokeh[rnd.Next(bokeh.Length)];
            using var gp = new GraphicsPath();
            gp.AddEllipse(bx - br, by - br, br * 2, br * 2);
            using var pg = new PathGradientBrush(gp) { CenterColor = c, SurroundColors = [Color.FromArgb(0, c)] };
            g.FillEllipse(pg, bx - br, by - br, br * 2, br * 2);
        }
        DrawBackdrop(g, width, height);
    }
}

/// <summary>Vẽ bằng lớp Gdi (Skia) thẳng vào bộ đệm của bitmap — không chép qua lại.</summary>
internal static class XmasBitmap
{
    public static WriteableBitmap Create(int pixelWidth, int pixelHeight, double scale) =>
        new(new PixelSize(pixelWidth, pixelHeight), new Vector(96 * scale, 96 * scale), PixelFormat.Bgra8888, AlphaFormat.Premul);

    /// <summary>Xoá trong suốt, phóng theo <paramref name="scale"/> (DPI / lấy mẫu dày) rồi gọi hàm vẽ.</summary>
    public static void Paint(WriteableBitmap bitmap, float scale, Action<Graphics> paint)
    {
        using var fb = bitmap.Lock();
        var info = new SKImageInfo(fb.Size.Width, fb.Size.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info, fb.Address, fb.RowBytes);
        if (surface == null)
        {
            return;
        }

        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        canvas.Scale(scale);
        using var g = new Graphics(canvas);
        paint(g);
        canvas.Flush();
    }

    /// <summary>Ảnh Skia dùng chung (cảnh làng / cây thông / quả châu) — vẽ ở <paramref name="scale"/> lần kích thước thiết kế.</summary>
    public static void Paint(SKBitmap bitmap, float scale, Action<Graphics> paint)
    {
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        canvas.Scale(scale);
        using var g = new Graphics(canvas);
        paint(g);
        canvas.Flush();
    }
}

/// <summary>
/// Một vùng vẽ Giáng sinh trong Avalonia: vẽ (lớp Gdi trên Skia) thẳng vào WriteableBitmap theo nhịp riêng của từng vùng (dây đèn 12
/// hình/giây, làng 8, xe tuần lộc 25 chỉ lúc đang bay) — mọi vùng chạy trên nhịp chung <see cref="XmasTicker"/>. Fps = 0 → tĩnh (vẽ lại
/// khi đổi cỡ / đổi DPI). Tự dừng khi ẩn (kể cả khi phần tử cha bị ẩn) / thu nhỏ cửa sổ; app ở nền thì giảm nửa nhịp. Không nhận chuột.
/// </summary>
internal sealed class XmasSurface : Control
{
    public delegate void Painter(Graphics g, float width, float height, double t);

    private readonly Painter _paint;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private IDisposable? _tick;
    private readonly DispatcherTimer _resize;
    private WriteableBitmap? _bitmap;
    private double _scale = 1;
    private int _fps;
    private bool _paused;
    private bool _blank;
    private Window? _window;

    /// <summary>Gọi trước mỗi khung; trả false = không cần vẽ (vd xe tuần lộc đang nghỉ) → giữ khung trống.</summary>
    public Func<double, bool>? ShouldDraw { get; set; }

    public XmasSurface(Painter paint, int fps)
    {
        _paint = paint;
        _fps = fps;
        IsHitTestVisible = false;
        Focusable = false;
        _resize = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(120) };
        _resize.Tick += (_, _) =>
        {
            _resize.Stop();
            Allocate();
            Paint();
        };
    }

    public bool Paused
    {
        get => _paused;
        set
        {
            _paused = value;
            Sync();
            if (!value && _bitmap != null)
            {
                Paint();
            }
        }
    }

    public int Fps
    {
        get => _fps;
        set
        {
            _fps = value;
            _tick?.Dispose();
            _tick = null;
            Sync();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _window = TopLevel.GetTopLevel(this) as Window;
        if (_window != null)
        {
            _window.PropertyChanged += OnWindowPropertyChanged;
            _window.ScalingChanged += OnScalingChanged;
            XmasTicker.Attach(_window);
        }

        Allocate();
        Paint();
        Sync();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _tick?.Dispose();
        _tick = null;
        if (_window != null)
        {
            _window.PropertyChanged -= OnWindowPropertyChanged;
            _window.ScalingChanged -= OnScalingChanged;
            _window = null;
        }
    }

    private void OnScalingChanged(object? sender, EventArgs e)
    {
        _resize.Stop();
        _resize.Start();
    }

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Window.WindowStateProperty)
        {
            Sync();
        }
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        _resize.Stop();
        _resize.Start();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty)
        {
            Sync();
        }
    }

    private void Sync()
    {
        bool run = _fps > 0 && !_paused && IsVisible && _window != null && _window.WindowState != WindowState.Minimized && !Motion.Suppressed;
        if (run && _tick == null)
        {
            _tick = XmasTicker.Subscribe(OnTick, XmasTicker.DivisorFor(_fps));
        }
        else if (!run && _tick != null)
        {
            _tick.Dispose();
            _tick = null;
        }
    }

    private void Allocate()
    {
        _scale = _window?.RenderScaling is > 0 and var s ? s : 1;
        int pw = (int)Math.Ceiling(Bounds.Width * _scale), ph = (int)Math.Ceiling(Bounds.Height * _scale);
        if (pw < 2 || ph < 2)
        {
            _bitmap?.Dispose();
            _bitmap = null;
            InvalidateVisual();
            return;
        }

        if (_bitmap != null && _bitmap.PixelSize.Width == pw && _bitmap.PixelSize.Height == ph)
        {
            return;
        }

        _bitmap?.Dispose();
        _bitmap = XmasBitmap.Create(pw, ph, _scale);
        _blank = false;
        InvalidateVisual();
    }

    /// <summary>Chỉ ghi lệnh vẽ ảnh; mỗi khung chỉ cập nhật điểm ảnh trong WriteableBitmap rồi báo vẽ lại.</summary>
    public override void Render(Avalonia.Media.DrawingContext context)
    {
        if (_bitmap != null)
        {
            context.DrawImage(_bitmap, new Rect(Bounds.Size));
        }
    }

    /// <summary>Nhịp chung: chỉ vẽ khi thật sự đang hiện (phần tử cha có thể đang ẩn, vd làng tuyết khi thanh bên hết chỗ).</summary>
    private void OnTick()
    {
        if (IsEffectivelyVisible)
        {
            Paint();
        }
    }

    public void Paint()
    {
        if (_bitmap == null)
        {
            return;
        }

        double t = _clock.Elapsed.TotalSeconds;
        bool draw = ShouldDraw?.Invoke(t) ?? true;
        if (!draw && _blank)
        {
            return;      // đang nghỉ và khung đã trống: không đụng tới bitmap
        }

        try
        {
            float w = (float)Bounds.Width, h = (float)Bounds.Height;
            XmasBitmap.Paint(_bitmap, (float)_scale, g =>
            {
                if (draw)
                {
                    _paint(g, w, h, t);
                }
            });
            _blank = !draw;
            InvalidateVisual();
        }
        catch (Exception ex)
        {
            DebugLog.Write("Xmas: " + ex.Message);
        }
    }
}

/// <summary>Ông già Noel cưỡi xe tuần lộc bay ngang dải trên cửa sổ, rắc bụi sao (như DiskHealth) — 30–65 giây một lần.</summary>
internal sealed class SleighFlight
{
    private sealed class Spark { public float X, Y, Vx, Vy, Age, Life, Size; public Color C; }

    private readonly List<Spark> sparks = [];
    private bool flying, right;
    private double start, next = 6, last;

    public void FlyNow() => next = 0;

    /// <summary>Có cần vẽ khung này không (đang bay hoặc còn bụi sao).</summary>
    public bool Active(double t)
    {
        if (!flying && t >= next) { flying = true; right = Random.Shared.Next(2) == 0; start = t; }
        return flying || sparks.Count > 0;
    }

    public void Paint(Graphics g, float w, float h, double t)
    {
        float dt = (float)Math.Clamp(t - last, 0, 0.1); last = t;
        float k = dt * 30;
        float sc = Math.Clamp(h / 190f, 0.6f, 1.2f) * 0.85f;
        if (flying)
        {
            float L = XmasArt.SleighLength * sc, span = w + 2 * L;
            double dur = span / Math.Max(170f, w / 8f);
            float p = (float)((t - start) / dur);
            if (p >= 1) { flying = false; next = t + 30 + Random.Shared.NextDouble() * 35; }
            else
            {
                float Y(float q) => h * 0.42f + MathF.Sin(q * MathF.PI * 2.2f) * h * 0.12f + (1 - q) * h * 0.1f;
                float x = right ? -L + p * span : w + L - p * span, y = Y(p);
                float tilt = (float)(Math.Atan2(Y(p + 0.01f) - y, span * 0.01f) * 180 / Math.PI);
                var tail = XmasArt.DrawSleigh(g, x, y, sc, tilt, right, t);
                var rnd = Random.Shared;
                for (int i = 0; i < 2 && sparks.Count < 140; i++)
                    sparks.Add(new Spark
                    {
                        X = tail.X + rnd.Next(-4, 5), Y = tail.Y + rnd.Next(-6, 7),
                        Vx = (right ? -1 : 1) * (float)rnd.NextDouble() * 0.8f, Vy = (float)rnd.NextDouble() * 0.6f - 0.2f,
                        Life = 0.8f + (float)rnd.NextDouble() * 0.9f, Size = (2.5f + (float)rnd.NextDouble() * 3.5f) * sc,
                        C = rnd.Next(3) == 0 ? Color.White : Color.FromArgb(255, 222, 130),
                    });
            }
        }
        foreach (var s in sparks) { s.Age += dt; s.X += s.Vx * k; s.Y += s.Vy * k; s.Vy += 0.03f * k; }
        sparks.RemoveAll(s => s.Age >= s.Life);
        foreach (var s in sparks)
        {
            float tt = s.Age / s.Life;
            int a = (int)(240 * (1 - tt));
            if (a > 0) XmasArt.Sparkle(g, s.X, s.Y, s.Size * (1 - tt * 0.5f), Color.FromArgb(a, s.C));
        }
    }
}
