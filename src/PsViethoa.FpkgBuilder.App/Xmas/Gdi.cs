// Lớp tương thích GDI+ tối thiểu trên nền SkiaSharp — chỉ gồm đúng phần mà bộ hình Giáng sinh (XmasArt, port từ PSVIETHOA STUDIO /
// DiskHealth) dùng: Graphics, SolidBrush, Pen, GraphicsPath, LinearGradientBrush, PathGradientBrush, Color, PointF, RectangleF, biến
// đổi toạ độ, Save/Restore, SetClip. Nhờ vậy XmasArt.cs chép sang gần như nguyên văn mà chạy được cả Windows lẫn macOS/Linux
// (System.Drawing chỉ có trên Windows). Ngữ nghĩa bám GDI+: biến đổi nhân trước (MatrixOrder.Prepend), đa giác/đường tô theo
// FillMode.Alternate (chẵn-lẻ), các Add* nối tiếp trong một hình tự nối bằng đoạn thẳng, góc tính bằng độ theo chiều kim đồng hồ.
using SkiaSharp;

namespace PsViethoa.FpkgBuilder.App.Xmas.Gdi;

/// <summary>Màu ARGB 8 bit như System.Drawing.Color (giá trị ngoài 0..255 được chặn thay vì ném lỗi).</summary>
public readonly struct Color
{
    public byte A { get; }
    public byte R { get; }
    public byte G { get; }
    public byte B { get; }

    private Color(int a, int r, int g, int b)
    {
        A = Clamp(a);
        R = Clamp(r);
        G = Clamp(g);
        B = Clamp(b);
    }

    private static byte Clamp(int v) => (byte)Math.Clamp(v, 0, 255);

    public static Color FromArgb(int r, int g, int b) => new(255, r, g, b);

    public static Color FromArgb(int a, int r, int g, int b) => new(a, r, g, b);

    public static Color FromArgb(int a, Color baseColor) => new(a, baseColor.R, baseColor.G, baseColor.B);

    public static Color White => new(255, 255, 255, 255);

    public static Color Black => new(255, 0, 0, 0);

    /// <summary>GDI+: Transparent = ARGB(0, 255, 255, 255).</summary>
    public static Color Transparent => new(0, 255, 255, 255);

    internal SKColor Sk => new(R, G, B, A);
}

public struct PointF
{
    public float X { get; set; }
    public float Y { get; set; }

    public PointF(float x, float y)
    {
        X = x;
        Y = y;
    }

    internal SKPoint Sk => new(X, Y);
}

public struct RectangleF
{
    public float X { get; set; }
    public float Y { get; set; }
    public float Width { get; set; }
    public float Height { get; set; }

    public RectangleF(float x, float y, float width, float height)
    {
        X = x;
        Y = y;
        Width = width;
        Height = height;
    }

    public float Left => X;
    public float Top => Y;
    public float Right => X + Width;
    public float Bottom => Y + Height;

    internal SKRect Sk => SKRect.Create(X, Y, Width, Height);
}

public struct Rectangle
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }

    public Rectangle(int x, int y, int width, int height)
    {
        X = x;
        Y = y;
        Width = width;
        Height = height;
    }

    public static implicit operator RectangleF(Rectangle r) => new(r.X, r.Y, r.Width, r.Height);
}

public enum SmoothingMode { Invalid = -1, Default, HighSpeed, HighQuality, None, AntiAlias }

public enum PixelOffsetMode { Invalid = -1, Default, HighSpeed, HighQuality, None, Half }

public enum LinearGradientMode { Horizontal, Vertical, ForwardDiagonal, BackwardDiagonal }

public enum CombineMode { Replace, Intersect, Union, Xor, Exclude, Complement }

public enum LineCap { Flat = 0, Square = 1, Round = 2, Triangle = 3 }

public enum LineJoin { Miter, Bevel, Round, MiterClipped }

public enum FillMode { Alternate, Winding }

/// <summary>Cọ vẽ; tạo SKShader (hoặc màu) cho từng lệnh tô.</summary>
public abstract class Brush : IDisposable
{
    /// <summary>Đặt màu / shader vào paint; trả về shader cần giải phóng sau lệnh vẽ (nếu có).</summary>
    internal abstract SKShader? Apply(SKPaint paint);

    public void Dispose() => GC.SuppressFinalize(this);
}

public sealed class SolidBrush : Brush
{
    public SolidBrush(Color color) => Color = color;

    public Color Color { get; set; }

    internal override SKShader? Apply(SKPaint paint)
    {
        paint.Color = Color.Sk;
        return null;
    }
}

/// <summary>
/// Chuyển sắc tuyến tính theo góc như GDI+ (isAngleScaleable = false): đường chuyển sắc đi qua tâm khung theo hướng góc, hai đầu là
/// hình chiếu của các góc khung — 0° trái → phải, 90° trên → dưới.
/// </summary>
public sealed class LinearGradientBrush : Brush
{
    private readonly RectangleF _rect;
    private readonly Color _c1, _c2;
    private readonly float _angle;

    public LinearGradientBrush(RectangleF rect, Color color1, Color color2, float angle)
    {
        _rect = rect;
        _c1 = color1;
        _c2 = color2;
        _angle = angle;
    }

    public LinearGradientBrush(Rectangle rect, Color color1, Color color2, float angle)
        : this((RectangleF)rect, color1, color2, angle)
    {
    }

    public LinearGradientBrush(RectangleF rect, Color color1, Color color2, LinearGradientMode mode)
        : this(rect, color1, color2, mode switch
        {
            LinearGradientMode.Vertical => 90f,
            LinearGradientMode.ForwardDiagonal => 45f,
            LinearGradientMode.BackwardDiagonal => 135f,
            _ => 0f,
        })
    {
    }

    public LinearGradientBrush(Rectangle rect, Color color1, Color color2, LinearGradientMode mode)
        : this((RectangleF)rect, color1, color2, mode)
    {
    }

    internal override SKShader? Apply(SKPaint paint)
    {
        double a = _angle * Math.PI / 180;
        float dx = (float)Math.Cos(a), dy = (float)Math.Sin(a);
        float cx = _rect.X + _rect.Width / 2, cy = _rect.Y + _rect.Height / 2;
        float half = (Math.Abs(_rect.Width * dx) + Math.Abs(_rect.Height * dy)) / 2;
        if (half < 1e-4f)
        {
            paint.Color = _c1.Sk;
            return null;
        }

        var shader = SKShader.CreateLinearGradient(
            new SKPoint(cx - dx * half, cy - dy * half), new SKPoint(cx + dx * half, cy + dy * half),
            [_c1.Sk, _c2.Sk], null, SKShaderTileMode.Clamp);
        paint.Shader = shader;
        return shader;
    }
}

/// <summary>
/// Chuyển sắc theo đường viền (bộ hình chỉ dùng với hình tròn / elip): CenterColor ở CenterPoint, SurroundColors[0] ở mép.
/// Tâm lệch → gradient hai điểm (tiêu điểm r = 0 tới vòng mép), đúng kiểu nội suy theo tia của GDI+.
/// </summary>
public sealed class PathGradientBrush : Brush
{
    private readonly RectangleF _bounds;
    private PointF? _center;

    public PathGradientBrush(GraphicsPath path) => _bounds = path.GetBounds();

    public Color CenterColor { get; set; } = Color.White;

    public Color[] SurroundColors { get; set; } = [Color.White];

    public PointF CenterPoint
    {
        get => _center ?? new PointF(_bounds.X + _bounds.Width / 2, _bounds.Y + _bounds.Height / 2);
        set => _center = value;
    }

    internal override SKShader? Apply(SKPaint paint)
    {
        float rx = _bounds.Width / 2, ry = _bounds.Height / 2;
        if (rx <= 0 || ry <= 0)
        {
            return null;
        }

        var surround = SurroundColors.Length > 0 ? SurroundColors[0] : CenterColor;
        float cx = _bounds.X + rx, cy = _bounds.Y + ry;
        // Làm việc trong không gian đường tròn bán kính rx rồi kéo giãn theo trục y cho elip.
        float sy = ry / rx;
        var local = SKMatrix.CreateScale(1, sy, cx, cy);
        var focus = CenterPoint;
        var f = new SKPoint(focus.X, cy + (focus.Y - cy) / sy);
        SKColor[] colors = [CenterColor.Sk, surround.Sk];
        SKShader shader = Math.Abs(f.X - cx) < 0.01f && Math.Abs(f.Y - cy) < 0.01f
            ? SKShader.CreateRadialGradient(new SKPoint(cx, cy), rx, colors, null, SKShaderTileMode.Clamp, local)
            : SKShader.CreateTwoPointConicalGradient(f, 0, new SKPoint(cx, cy), rx, colors, null, SKShaderTileMode.Clamp, local);
        paint.Shader = shader;
        return shader;
    }
}

public sealed class Pen : IDisposable
{
    public Pen(Color color, float width = 1f)
    {
        Color = color;
        Width = width;
    }

    public Color Color { get; set; }
    public float Width { get; set; }
    public LineCap StartCap { get; set; } = LineCap.Flat;
    public LineCap EndCap { get; set; } = LineCap.Flat;
    public LineJoin LineJoin { get; set; } = LineJoin.Miter;

    internal void Apply(SKPaint paint)
    {
        paint.Style = SKPaintStyle.Stroke;
        paint.Color = Color.Sk;
        paint.StrokeWidth = Width;
        // Skia chỉ có một kiểu đầu nét cho cả hai đầu: đầu nào tròn thì dùng tròn.
        paint.StrokeCap = StartCap == LineCap.Round || EndCap == LineCap.Round ? SKStrokeCap.Round
            : StartCap == LineCap.Square || EndCap == LineCap.Square ? SKStrokeCap.Square
            : SKStrokeCap.Butt;
        paint.StrokeJoin = LineJoin switch
        {
            LineJoin.Round => SKStrokeJoin.Round,
            LineJoin.Bevel => SKStrokeJoin.Bevel,
            _ => SKStrokeJoin.Miter,
        };
    }

    public void Dispose() => GC.SuppressFinalize(this);
}

/// <summary>Đường hình học như GDI+: các Add* liên tiếp nối với nhau trong cùng một hình cho tới CloseFigure.</summary>
public sealed class GraphicsPath : IDisposable
{
    private bool _open;

    internal SKPath Path { get; } = new();

    public FillMode FillMode { get; set; } = FillMode.Alternate;

    private void Begin(float x, float y)
    {
        if (!_open)
        {
            Path.MoveTo(x, y);
            _open = true;
            return;
        }

        var last = Path.LastPoint;
        if (Math.Abs(last.X - x) > 1e-4f || Math.Abs(last.Y - y) > 1e-4f)
        {
            Path.LineTo(x, y);
        }
    }

    public void AddBezier(float x1, float y1, float x2, float y2, float x3, float y3, float x4, float y4)
    {
        Begin(x1, y1);
        Path.CubicTo(x2, y2, x3, y3, x4, y4);
    }

    public void AddBezier(PointF p1, PointF p2, PointF p3, PointF p4) => AddBezier(p1.X, p1.Y, p2.X, p2.Y, p3.X, p3.Y, p4.X, p4.Y);

    public void AddLine(float x1, float y1, float x2, float y2)
    {
        Begin(x1, y1);
        Path.LineTo(x2, y2);
    }

    public void AddLine(PointF p1, PointF p2) => AddLine(p1.X, p1.Y, p2.X, p2.Y);

    public void AddArc(float x, float y, float width, float height, float startAngle, float sweepAngle)
    {
        Path.ArcTo(SKRect.Create(x, y, width, height), startAngle, sweepAngle, !_open);
        _open = true;
    }

    public void AddEllipse(float x, float y, float width, float height)
    {
        _open = false;
        Path.AddOval(SKRect.Create(x, y, width, height));
    }

    public void AddRectangle(RectangleF rect)
    {
        _open = false;
        Path.AddRect(rect.Sk);
    }

    public void StartFigure() => _open = false;

    public void CloseFigure()
    {
        if (_open)
        {
            Path.Close();
        }

        _open = false;
    }

    public RectangleF GetBounds()
    {
        var b = Path.Bounds;
        return new RectangleF(b.Left, b.Top, b.Width, b.Height);
    }

    public void Dispose() => Path.Dispose();
}

public sealed class GraphicsState
{
    internal GraphicsState(int count) => Count = count;

    internal int Count { get; }
}

/// <summary>Bề mặt vẽ kiểu System.Drawing.Graphics bọc một SKCanvas (luôn khử răng cưa).</summary>
public sealed class Graphics : IDisposable
{
    private readonly SKCanvas _canvas;

    public Graphics(SKCanvas canvas) => _canvas = canvas;

    public SmoothingMode SmoothingMode { get; set; } = SmoothingMode.AntiAlias;

    public PixelOffsetMode PixelOffsetMode { get; set; } = PixelOffsetMode.HighQuality;

    public GraphicsState Save() => new(_canvas.Save());

    public void Restore(GraphicsState state) => _canvas.RestoreToCount(state.Count);

    public void TranslateTransform(float dx, float dy) => _canvas.Translate(dx, dy);

    public void RotateTransform(float angle) => _canvas.RotateDegrees(angle);

    public void ScaleTransform(float sx, float sy) => _canvas.Scale(sx, sy);

    public void Clear(Color color) => _canvas.Clear(color.Sk);

    public void SetClip(GraphicsPath path, CombineMode mode = CombineMode.Replace)
    {
        // Bộ hình chỉ dùng Intersect bên trong Save/Restore; Replace cũng coi như Intersect (Skia không nới vùng cắt được).
        _canvas.ClipPath(path.Path, mode == CombineMode.Exclude ? SKClipOperation.Difference : SKClipOperation.Intersect, true);
    }

    // ───── tô ─────

    private void Fill(Brush brush, Action<SKPaint> draw)
    {
        using var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
        using var shader = brush.Apply(paint);
        if (shader == null && paint.Color.Alpha == 0)
        {
            return;
        }

        draw(paint);
    }

    private void Stroke(Pen pen, Action<SKPaint> draw)
    {
        if (pen.Color.A == 0)
        {
            return;
        }

        using var paint = new SKPaint { IsAntialias = true };
        pen.Apply(paint);
        draw(paint);
    }

    private static SKPath Polygon(PointF[] points, bool close)
    {
        var path = new SKPath { FillType = SKPathFillType.EvenOdd };
        if (points.Length == 0)
        {
            return path;
        }

        path.MoveTo(points[0].X, points[0].Y);
        for (int i = 1; i < points.Length; i++)
        {
            path.LineTo(points[i].X, points[i].Y);
        }

        if (close)
        {
            path.Close();
        }

        return path;
    }

    public void FillPath(Brush brush, GraphicsPath path)
    {
        path.Path.FillType = path.FillMode == FillMode.Alternate ? SKPathFillType.EvenOdd : SKPathFillType.Winding;
        Fill(brush, p => _canvas.DrawPath(path.Path, p));
    }

    public void FillEllipse(Brush brush, float x, float y, float width, float height) =>
        Fill(brush, p => _canvas.DrawOval(SKRect.Create(x, y, width, height), p));

    public void FillRectangle(Brush brush, float x, float y, float width, float height) =>
        Fill(brush, p => _canvas.DrawRect(SKRect.Create(x, y, width, height), p));

    public void FillRectangle(Brush brush, RectangleF rect) => FillRectangle(brush, rect.X, rect.Y, rect.Width, rect.Height);

    public void FillRectangle(Brush brush, Rectangle rect) => FillRectangle(brush, rect.X, rect.Y, rect.Width, rect.Height);

    public void FillPolygon(Brush brush, PointF[] points)
    {
        using var path = Polygon(points, true);
        Fill(brush, p => _canvas.DrawPath(path, p));
    }

    // ───── nét ─────

    public void DrawPath(Pen pen, GraphicsPath path) => Stroke(pen, p => _canvas.DrawPath(path.Path, p));

    public void DrawLine(Pen pen, float x1, float y1, float x2, float y2) => Stroke(pen, p => _canvas.DrawLine(x1, y1, x2, y2, p));

    public void DrawLine(Pen pen, PointF a, PointF b) => DrawLine(pen, a.X, a.Y, b.X, b.Y);

    public void DrawLines(Pen pen, PointF[] points)
    {
        using var path = Polygon(points, false);
        Stroke(pen, p => _canvas.DrawPath(path, p));
    }

    public void DrawPolygon(Pen pen, PointF[] points)
    {
        using var path = Polygon(points, true);
        Stroke(pen, p => _canvas.DrawPath(path, p));
    }

    public void DrawBezier(Pen pen, float x1, float y1, float x2, float y2, float x3, float y3, float x4, float y4)
    {
        using var path = new SKPath();
        path.MoveTo(x1, y1);
        path.CubicTo(x2, y2, x3, y3, x4, y4);
        Stroke(pen, p => _canvas.DrawPath(path, p));
    }

    public void DrawBezier(Pen pen, PointF p1, PointF p2, PointF p3, PointF p4) => DrawBezier(pen, p1.X, p1.Y, p2.X, p2.Y, p3.X, p3.Y, p4.X, p4.Y);

    public void DrawEllipse(Pen pen, float x, float y, float width, float height) =>
        Stroke(pen, p => _canvas.DrawOval(SKRect.Create(x, y, width, height), p));

    public void DrawRectangle(Pen pen, float x, float y, float width, float height) =>
        Stroke(pen, p => _canvas.DrawRect(SKRect.Create(x, y, width, height), p));

    public void DrawArc(Pen pen, float x, float y, float width, float height, float startAngle, float sweepAngle) =>
        Stroke(pen, p => _canvas.DrawArc(SKRect.Create(x, y, width, height), startAngle, sweepAngle, false, p));

    public void Dispose()
    {
    }
}
