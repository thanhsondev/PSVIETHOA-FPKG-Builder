using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using PsViethoa.FpkgBuilder.App.Services;
using Path = Avalonia.Controls.Shapes.Path;

namespace PsViethoa.FpkgBuilder.App.Xmas;

/// <summary>
/// Tuyết rơi (theme tối: hạt trắng mờ; theme sáng: hạt trắng viền xanh xám): MỘT lớp vẽ (Render) thay vì hàng trăm phần tử; 25 khung/giây theo nhịp chung <see cref="XmasTicker"/> (12,5 khi app ở
/// nền), dừng hẳn khi thu nhỏ hoặc tắt chế độ Giáng sinh. Không nhận chuột (IsHitTestVisible = false).
/// </summary>
internal sealed class SnowLayer : Control
{
    private struct Flake { public double X, Y, R, Speed, Drift, Phase, Alpha; }

    private readonly Random _rng = new(2512);
    private Flake[] _flakes = [];
    private IDisposable? _tick;
    private readonly Stopwatch _clock = new();
    private double _last;
    private readonly IImmutableBrush[] _brushes;
    private readonly IImmutableBrush[] _dayBrushes;
    private readonly ImmutablePen _dayEdge = new(new ImmutableSolidColorBrush(Color.FromArgb(0x60, 0x6E, 0x87, 0x9C)), 0.7);
    private bool _day;
    private Window? _window;
    private bool _paused;

    public SnowLayer()
    {
        IsHitTestVisible = false;
        Focusable = false;
        _brushes = Enumerable.Range(0, 6).Select(i => (IImmutableBrush)new ImmutableSolidColorBrush(Color.FromArgb((byte)(70 + i * 30), 0xF1, 0xF5, 0xFF))).ToArray();
        _dayBrushes = Enumerable.Range(0, 6).Select(i => (IImmutableBrush)new ImmutableSolidColorBrush(Color.FromArgb((byte)(150 + i * 21), 0xFF, 0xFF, 0xFF))).ToArray();
    }

    /// <summary>Theme sáng: hạt trắng đặc hơn có viền xanh xám rất nhẹ để vẫn thấy trên nền nhạt.</summary>
    public bool Day
    {
        get => _day;
        set
        {
            _day = value;
            InvalidateVisual();
        }
    }

    public bool Paused
    {
        get => _paused;
        set
        {
            _paused = value;
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
            XmasTicker.Attach(_window);
        }

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
            _window = null;
        }
    }

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Window.WindowStateProperty)
        {
            Sync();
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty)
        {
            Sync();
        }
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        Seed();
    }

    private void Sync()
    {
        bool run = !_paused && IsVisible && _window != null && _window.WindowState != WindowState.Minimized && !Motion.Suppressed;
        if (run && _tick == null)
        {
            _clock.Restart();
            _last = 0;
            _tick = XmasTicker.Subscribe(Step, 1);
        }
        else if (!run && _tick != null)
        {
            _tick.Dispose();
            _tick = null;
        }
    }

    private void Seed()
    {
        double w = Bounds.Width, h = Bounds.Height;
        if (w < 10 || h < 10)
        {
            return;
        }

        int n = (int)Math.Clamp(w * h / 16000, 30, 90);
        _flakes = new Flake[n];
        for (int i = 0; i < n; i++)
        {
            _flakes[i] = NewFlake(w, h, _rng.NextDouble() * h);
        }

        InvalidateVisual();
    }

    private Flake NewFlake(double w, double h, double y)
    {
        double depth = _rng.NextDouble();            // xa → nhỏ, chậm, mờ
        return new Flake
        {
            X = _rng.NextDouble() * w, Y = y, R = 0.8 + depth * 2.2, Speed = 14 + depth * 38, Drift = 6 + _rng.NextDouble() * 14,
            Phase = _rng.NextDouble() * Math.PI * 2, Alpha = depth,
        };
    }

    private void Step()
    {
        double now = _clock.Elapsed.TotalSeconds, dt = Math.Min(now - _last, 0.1);
        _last = now;
        double w = Bounds.Width, h = Bounds.Height;
        for (int i = 0; i < _flakes.Length; i++)
        {
            ref var f = ref _flakes[i];
            f.Y += f.Speed * dt;
            if (f.Y - f.R > h)
            {
                f = NewFlake(w, h, -4);
            }
        }

        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        double t = _clock.Elapsed.TotalSeconds;
        foreach (var f in _flakes)
        {
            double x = f.X + Math.Sin(t * 0.6 + f.Phase) * f.Drift;
            int k = Math.Min(_brushes.Length - 1, (int)(f.Alpha * _brushes.Length));
            if (_day)
            {
                context.DrawEllipse(_dayBrushes[k], _dayEdge, new Point(x, f.Y), f.R + 0.3, f.R + 0.3);
            }
            else
            {
                context.DrawEllipse(_brushes[k], null, new Point(x, f.Y), f.R, f.R);
            }
        }
    }
}

/// <summary>Mũ Noel nhỏ đội chéo trên góc logo (thiết kế 30×26, vành mũ ở y 18–24).</summary>
internal sealed class SantaHat : Canvas
{
    public SantaHat()
    {
        Width = 30;
        Height = 26;
        IsHitTestVisible = false;
        var red = new Path
        {
            Data = Geometry.Parse("M3,20 C6,9 14,2 24,3 C21,7 22,12 26,15 L27,20 Z"),
            Fill = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Color.FromRgb(0xEF, 0x44, 0x44), 0), new GradientStop(Color.FromRgb(0xB9, 0x1C, 0x1C), 1) },
            },
        };
        var brim = new Rectangle { Width = 28, Height = 6, RadiusX = 3, RadiusY = 3, Fill = Brushes.WhiteSmoke };
        SetLeft(brim, 1);
        SetTop(brim, 18);
        var pom = new Ellipse { Width = 7, Height = 7, Fill = Brushes.White };
        SetLeft(pom, 21);
        SetTop(pom, 0);
        Children.Add(red);
        Children.Add(brim);
        Children.Add(pom);
        // Xoay / thu quanh tâm vành mũ (15, 21): vành nằm đúng chỗ đặt, nghiêng -12°, chóp + quả bông rủ sang phải.
        RenderTransformOrigin = new RelativePoint(15, 21, RelativeUnit.Absolute);
        RenderTransform = new TransformGroup { Children = { new ScaleTransform(0.8, 0.8), new RotateTransform(-12) } };
    }
}
