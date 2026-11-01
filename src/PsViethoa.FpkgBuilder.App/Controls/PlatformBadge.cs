using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Styling;
using PsViethoa.FpkgBuilder.App.Services;
using PsViethoa.FpkgBuilder.App.ViewModels.Shell;

namespace PsViethoa.FpkgBuilder.App.Controls;

/// <summary>
/// Huy hiệu hệ máy (PS5 · PS4 · FTP · Save) cho nhóm trên thanh bên và đầu trang. Vẽ bằng vector, gợi hệ máy bằng màu nhận diện và
/// chữ / biểu tượng — không dùng logo thương hiệu. Rê chuột: phóng nhẹ + quầng sáng; <see cref="Pop"/> khi mở trang của nhóm;
/// <see cref="IsBusy"/>: quầng sáng nhịp chậm khi nhóm đang chạy việc nền.
/// </summary>
public sealed class PlatformBadge : Panel
{
    public static readonly StyledProperty<Platform> PlatformProperty =
        AvaloniaProperty.Register<PlatformBadge, Platform>(nameof(Platform));

    public static readonly StyledProperty<double> SizeProperty =
        AvaloniaProperty.Register<PlatformBadge, double>(nameof(Size), 24);

    public static readonly StyledProperty<bool> InteractiveProperty =
        AvaloniaProperty.Register<PlatformBadge, bool>(nameof(Interactive), true);

    public static readonly StyledProperty<bool> IsBusyProperty =
        AvaloniaProperty.Register<PlatformBadge, bool>(nameof(IsBusy));

    private readonly Border _glow = new() { Opacity = 0, IsHitTestVisible = false };
    private readonly Border _tile = new() { BorderThickness = new Thickness(1), RenderTransformOrigin = RelativePoint.Center };
    private CancellationTokenSource? _pulse;

    public PlatformBadge()
    {
        _glow.Transitions = [new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(180) }];
        _tile.Transitions =
        [
            new TransformOperationsTransition { Property = RenderTransformProperty, Duration = TimeSpan.FromMilliseconds(180), Easing = new CubicEaseOut() },
        ];
        Children.Add(_glow);
        Children.Add(_tile);
        Build();
    }

    public Platform Platform
    {
        get => GetValue(PlatformProperty);
        set => SetValue(PlatformProperty, value);
    }

    public double Size
    {
        get => GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    /// <summary>Có hiệu ứng rê chuột (huy hiệu ở đầu trang thì không).</summary>
    public bool Interactive
    {
        get => GetValue(InteractiveProperty);
        set => SetValue(InteractiveProperty, value);
    }

    public bool IsBusy
    {
        get => GetValue(IsBusyProperty);
        set => SetValue(IsBusyProperty, value);
    }

    /// <summary>Màu của hệ máy: (đỉnh dải màu, đáy dải màu, màu chữ, màu nhấn).</summary>
    public static (Color Top, Color Bottom, Color Ink, Color Accent) Palette(Platform platform) => platform switch
    {
        Platform.Ps5 => (Color.Parse("#FFFFFF"), Color.Parse("#C7D5EE"), Color.Parse("#1D4ED8"), Color.Parse("#60A5FA")),
        Platform.Ps4 => (Color.Parse("#818CF8"), Color.Parse("#3730A3"), Colors.White, Color.Parse("#A5B4FC")),
        Platform.Ftp => (Color.Parse("#2DD4BF"), Color.Parse("#0F766E"), Colors.White, Color.Parse("#5EEAD4")),
        Platform.Save => (Color.Parse("#FCD34D"), Color.Parse("#B45309"), Color.Parse("#2A1700"), Color.Parse("#FCD34D")),
        _ => (Color.Parse("#64748B"), Color.Parse("#1E293B"), Colors.White, Color.Parse("#94A3B8")),
    };

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == PlatformProperty || change.Property == SizeProperty)
        {
            Build();
        }
        else if (change.Property == IsBusyProperty)
        {
            Pulse(change.GetNewValue<bool>());
        }
    }

    protected override void OnPointerEntered(PointerEventArgs e)
    {
        base.OnPointerEntered(e);
        Hover(true);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        Hover(false);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _pulse?.Cancel();
        _pulse = null;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (IsBusy)
        {
            Pulse(true);
        }
    }

    private void Build()
    {
        var (top, bottom, ink, accent) = Palette(Platform);
        var size = Size;
        Width = Height = size;
        var radius = new CornerRadius(size * 0.3);
        _tile.CornerRadius = radius;
        _tile.Background = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0.4, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(top, 0), new GradientStop(bottom, 1) },
        };
        // Ô PS5 nền trắng: viền xanh để không chìm vào nền gần trắng của giao diện sáng; các ô màu giữ viền trắng mờ.
        _tile.BorderBrush = new SolidColorBrush(Platform == Platform.Ps5 ? Color.FromArgb(130, 0x1D, 0x4E, 0xD8) : Color.FromArgb(70, 255, 255, 255));
        _glow.CornerRadius = radius;
        _glow.Background = new SolidColorBrush(accent, 0.01);
        _glow.BoxShadow = new BoxShadows(new BoxShadow { Blur = size * 0.7, Spread = 1, Color = Color.FromArgb(200, accent.R, accent.G, accent.B) });

        var inkBrush = new SolidColorBrush(ink);
        _tile.Child = Platform switch
        {
            Platform.Ps5 or Platform.Ps4 => new TextBlock
            {
                Text = Platform == Platform.Ps5 ? "PS5" : "PS4",
                Foreground = inkBrush,
                FontWeight = FontWeight.Black,
                FontStyle = FontStyle.Italic,
                FontSize = size * 0.36,
                LetterSpacing = -0.3,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
            _ => new Avalonia.Controls.Shapes.Path
            {
                Data = Application.Current?.TryGetResource(Platform == Platform.Ftp ? "IconTransfer" : "IconSave", null, out var geometry) == true ? geometry as Geometry : null,
                Fill = inkBrush,
                Stretch = Stretch.Uniform,
                Width = size * 0.52,
                Height = size * 0.52,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
    }

    private void Hover(bool on)
    {
        if (!Interactive || !Motion.Enabled)
        {
            return;
        }

        _tile.RenderTransform = TransformOperations.Parse(on ? "scale(1.14)" : "scale(1)");
        if (_pulse == null)
        {
            _glow.Opacity = on ? 0.85 : 0;
        }
    }

    /// <summary>Bật lên kiểu lò xo kèm chớp sáng — khi mở một trang của nhóm này.</summary>
    public void Pop()
    {
        if (!Motion.Enabled)
        {
            return;
        }

        Motion.Pop(_glow, 0.6);
        Motion.Pop(_tile.Child as Visual, 0.6);
        if (_pulse == null)
        {
            _ = new Animation
            {
                Duration = TimeSpan.FromMilliseconds(700),
                Easing = new CubicEaseOut(),
                Children =
                {
                    new KeyFrame { Cue = new Cue(0), Setters = { new Setter(OpacityProperty, 0.95d) } },
                    new KeyFrame { Cue = new Cue(1), Setters = { new Setter(OpacityProperty, 0d) } },
                },
            }.RunAsync(_glow);
        }
    }

    private void Pulse(bool on)
    {
        _pulse?.Cancel();
        _pulse = null;
        if (!on || !Motion.Enabled || VisualRoot == null)
        {
            _glow.Opacity = 0;
            return;
        }

        _pulse = new CancellationTokenSource();
        _ = new Animation
        {
            Duration = TimeSpan.FromMilliseconds(950),
            IterationCount = IterationCount.Infinite,
            PlaybackDirection = PlaybackDirection.Alternate,
            Easing = new SineEaseInOut(),
            Children =
            {
                new KeyFrame { Cue = new Cue(0), Setters = { new Setter(OpacityProperty, 0.2d) } },
                new KeyFrame { Cue = new Cue(1), Setters = { new Setter(OpacityProperty, 0.95d) } },
            },
        }.RunAsync(_glow, _pulse.Token);
    }
}
