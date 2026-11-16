using System.Diagnostics;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PsViethoa.FpkgBuilder.App.Services;
using SkiaSharp;
using GdiColor = PsViethoa.FpkgBuilder.App.Xmas.Gdi.Color;
using GdiPath = PsViethoa.FpkgBuilder.App.Xmas.Gdi.GraphicsPath;
using GdiRect = PsViethoa.FpkgBuilder.App.Xmas.Gdi.Rectangle;
using GdiRectF = PsViethoa.FpkgBuilder.App.Xmas.Gdi.RectangleF;
using GdiSolid = PsViethoa.FpkgBuilder.App.Xmas.Gdi.SolidBrush;
using GdiLinear = PsViethoa.FpkgBuilder.App.Xmas.Gdi.LinearGradientBrush;
using GdiPathGradient = PsViethoa.FpkgBuilder.App.Xmas.Gdi.PathGradientBrush;
using GdiMode = PsViethoa.FpkgBuilder.App.Xmas.Gdi.LinearGradientMode;

namespace PsViethoa.FpkgBuilder.App.Xmas;

/// <summary>
/// Giáng sinh cho MỌI trang (port XmasScenes của Studio 2.1.0): chỗ trống trong khung log / danh sách (ListBox, TreeView, TextBox nhiều
/// dòng ≥ 300×110) có bầu trời đêm, rặng thông, làng tuyết và cây thông + người tuyết ở đáy. Chỉ vẽ HAI cảnh động dùng chung cho cả app
/// (~8 khung/giây theo nhịp chung XmasTicker, nửa nhịp khi app ở nền) cùng quả châu; mỗi khung ghép các ảnh đó thành nền riêng (ImageBrush, nằm SAU chữ). Khung trống →
/// cảnh rõ; khung nhiều chữ → cảnh mờ để không che nội dung. Dò khung mới mỗi giây (trang tạo sau vẫn có); tắt Noel thì trả lại nền gốc.
/// Nền được đặt ở mức ưu tiên Animation nên huỷ là về đúng giá trị cũ (style hoặc giá trị cục bộ).
/// </summary>
internal static class XmasScenes
{
    private const int SceneW = 360, SceneH = 230;

    /// <summary>Hệ số lấy mẫu của cảnh động: 1 ở màn 100 %, 2 ở màn HiDPI (≥ 125 %) — cảnh được vẽ gần đúng cỡ hiển thị.</summary>
    private static int oversample = 2;
    private const int SkyW = 900, SkyH = 1100, BaubleW = 320, BaubleH = 96;
    private const int PanoW = 2400, PanoH = 240;

    private static SKBitmap? village, trees, baubles;

    /// <summary>
    /// Toàn cảnh tĩnh (vẽ 1 lần mỗi biến thể): rặng thông phủ tuyết, dãy nhà mờ, mặt tuyết; bầu trời đêm có sao + trăng + cực quang
    /// (theme tối) hoặc trời ngày xanh băng có mặt trời mờ + bông tuyết (theme sáng).
    /// </summary>
    private static SKImage? panoramaNight, skyNight, panoramaDay, skyDay;

    private static bool day;

    private static SKImage Sky => day ? skyDay ??= BuildSky(true) : skyNight ??= BuildSky(false);

    private static SKImage Panorama => day ? panoramaDay ??= BuildPanorama(true) : panoramaNight ??= BuildPanorama(false);

    /// <summary>Đổi biến thể ngày (theme sáng) / đêm (theme tối): dựng lại phần tĩnh của mọi khung rồi ghép lại ngay.</summary>
    public static void SetDay(bool value)
    {
        if (day == value)
        {
            return;
        }

        day = value;
        foreach (var weak in attached)
        {
            if (weak.TryGetTarget(out var c) && targets.TryGetValue(c, out var t))
            {
                t.Static?.Dispose();
                t.Static = null;
                if (Active && c.IsEffectivelyVisible)
                {
                    Compose(c, t);
                }
            }
        }
    }

    private static readonly Stopwatch clock = Stopwatch.StartNew();
    private static DispatcherTimer? scan;
    private static IDisposable? frame;
    private static Window? owner;
    private static readonly ConditionalWeakTable<Control, Target> targets = new();
    private static readonly List<WeakReference<Control>> attached = [];

    private sealed class Target
    {
        public IDisposable? Override;
        public WriteableBitmap? Bitmap;
        public ImageBrush? BrushA, BrushB;
        public bool UsingB;
        public Visual? Painter;
        public double Opacity = -1;

        /// <summary>Phần tĩnh đã ghép sẵn theo cỡ khung (trời + toàn cảnh, độ đục đầy đủ) — mỗi nhịp chỉ chép lại thay vì phóng ảnh lớn.</summary>
        public SKImage? Static;
        public int StaticW, StaticH;

        /// <summary>Bố cục lần ghép trước có dùng cây thông / quả châu không (cảnh không ai dùng thì khỏi vẽ).</summary>
        public bool UsesTrees = true, UsesBaubles = true;
    }

    public static bool Active { get; private set; }

    public static void Start(Window window)
    {
        owner = window;
        Active = true;
        try
        {
            EnsureSceneBitmaps();
            _ = Sky;
            _ = Panorama;
            RenderScenes();
        }
        catch (Exception ex)
        {
            DebugLog.Write("XmasScenes: " + ex.Message);
        }

        if (scan == null)
        {
            scan = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
            scan.Tick += (_, _) => Scan();
        }

        XmasTicker.Attach(window);
        if (!Motion.Suppressed && frame == null)
        {
            frame = XmasTicker.Subscribe(Tick, XmasTicker.DivisorFor(8));
        }

        scan.Start();
        Scan();
    }

    public static void Stop()
    {
        Active = false;
        frame?.Dispose();
        frame = null;
        scan?.Stop();
        foreach (var weak in attached)
        {
            if (!weak.TryGetTarget(out var c) || !targets.TryGetValue(c, out var t))
            {
                continue;
            }

            t.Override?.Dispose();
            t.Static?.Dispose();
            t.Static = null;
            c.SizeChanged -= OnSize;
            targets.Remove(c);
        }

        attached.Clear();
    }

    private static SKBitmap NewScene(int w, int h) => new(new SKImageInfo(w * oversample, h * oversample, SKColorType.Bgra8888, SKAlphaType.Premul));

    private static void EnsureSceneBitmaps()
    {
        int want = owner?.RenderScaling > 1.1 ? 2 : 1;
        if (village != null && want == oversample)
        {
            return;
        }

        oversample = want;
        village?.Dispose();
        trees?.Dispose();
        baubles?.Dispose();
        village = NewScene(SceneW, SceneH);
        trees = NewScene(SceneW, SceneH);
        baubles = NewScene(BaubleW, BaubleH);
    }

    private static void Tick()
    {
        if (owner == null || owner.WindowState == WindowState.Minimized || Motion.Suppressed)
        {
            return;
        }

        bool any = false, needTrees = false, needBaubles = false;
        foreach (var weak in attached)
        {
            if (weak.TryGetTarget(out var c) && c.IsEffectivelyVisible && targets.TryGetValue(c, out var t))
            {
                any = true;
                needTrees |= t.UsesTrees;
                needBaubles |= t.UsesBaubles;
            }
        }

        if (!any)
        {
            return;
        }

        EnsureSceneBitmaps();
        RenderScenes(needTrees, needBaubles);
        foreach (var weak in attached)
        {
            if (weak.TryGetTarget(out var c) && c.IsEffectivelyVisible && targets.TryGetValue(c, out var t))
            {
                Compose(c, t);
            }
        }
    }

    private static void RenderScenes(bool withTrees = true, bool withBaubles = true)
    {
        double t = clock.Elapsed.TotalSeconds;
        XmasBitmap.Paint(village!, oversample, g => XmasArt.DrawVillageScene(g, new GdiRectF(4, 0, SceneW - 8, SceneH), t));
        if (withTrees)
        {
            XmasBitmap.Paint(trees!, oversample, g => XmasArt.DrawTreeScene(g, new GdiRectF(4, 8, SceneW - 8, SceneH - 8), t));
        }

        if (withBaubles)
        {
            XmasBitmap.Paint(baubles!, oversample, g => XmasArt.DrawOrnaments(g, new GdiRectF(0, 0, BaubleW, BaubleH), t, 0.85f));
        }
    }

    /// <summary>
    /// Bầu trời tĩnh (vẽ 1 lần mỗi biến thể). Đêm: xanh thông #12342C ở đỉnh nhạt dần, sao nhiều cỡ + sao 4 cánh, trăng có quầng, vệt cực
    /// quang xanh mờ. Ngày: xanh băng nhạt, bông tuyết thay sao, mặt trời mờ thay trăng, không cực quang.
    /// </summary>
    private static SKImage BuildSky(bool dayMode)
    {
        if (dayMode)
        {
            return BuildDaySky();
        }

        using var bmp = new SKBitmap(new SKImageInfo(SkyW, SkyH, SKColorType.Bgra8888, SKAlphaType.Premul));
        XmasBitmap.Paint(bmp, 1, g =>
        {
            using (var top = new GdiLinear(new GdiRect(0, 0, SkyW, SkyH), GdiColor.FromArgb(255, 18, 52, 44), GdiColor.FromArgb(0, 18, 52, 44), GdiMode.Vertical))
                g.FillRectangle(top, 0, 0, SkyW, SkyH);
            // cực quang: vài dải cong mờ xanh ngọc / xanh lá
            var auroraColors = new[] { GdiColor.FromArgb(34, 74, 222, 160), GdiColor.FromArgb(26, 56, 189, 248), GdiColor.FromArgb(22, 134, 239, 172) };
            for (int band = 0; band < 3; band++)
            {
                using var path = new GdiPath();
                float y0 = SkyH * (0.10f + band * 0.07f);
                path.AddBezier(-40, y0 + 60, SkyW * 0.3f, y0 - 50, SkyW * 0.6f, y0 + 90, SkyW + 40, y0);
                path.AddLine(SkyW + 40, y0, SkyW + 40, y0 + 46);
                path.AddBezier(SkyW + 40, y0 + 46, SkyW * 0.6f, y0 + 130, SkyW * 0.3f, y0 - 6, -40, y0 + 104);
                path.CloseFigure();
                using var br = new GdiSolid(auroraColors[band]);
                g.FillPath(br, path);
            }
            var rnd = new Random(2412);
            for (int i = 0; i < 260; i++)
            {
                float x = rnd.Next(SkyW), y = rnd.Next((int)(SkyH * 0.85f)), r = 0.6f + (float)rnd.NextDouble() * 1.6f;
                using var br = new GdiSolid(GdiColor.FromArgb(70 + rnd.Next(150), 255, 250, 228));
                g.FillEllipse(br, x - r, y - r, r * 2, r * 2);
            }
            for (int i = 0; i < 26; i++)
                XmasArt.Sparkle(g, rnd.Next(SkyW), rnd.Next((int)(SkyH * 0.7f)), 3 + (float)rnd.NextDouble() * 4, GdiColor.FromArgb(150 + rnd.Next(90), 255, 244, 200));
            // trăng góc phải trên + quầng
            float mr = 34, mx = SkyW * 0.82f, my = SkyH * 0.08f;
            using (var glow = new GdiPath())
            {
                glow.AddEllipse(mx - mr * 3.2f, my - mr * 3.2f, mr * 6.4f, mr * 6.4f);
                using var pg = new GdiPathGradient(glow) { CenterColor = GdiColor.FromArgb(70, 255, 246, 210), SurroundColors = [GdiColor.FromArgb(0, 255, 246, 210)] };
                g.FillPath(pg, glow);
            }
            using (var moon = new GdiSolid(GdiColor.FromArgb(225, 255, 248, 222))) g.FillEllipse(moon, mx - mr, my - mr, mr * 2, mr * 2);
            using (var crater = new GdiSolid(GdiColor.FromArgb(40, 200, 190, 160)))
            {
                g.FillEllipse(crater, mx - 12, my - 8, 12, 10); g.FillEllipse(crater, mx + 6, my + 6, 9, 8); g.FillEllipse(crater, mx - 4, my + 14, 6, 5);
            }
        });
        return SKImage.FromBitmap(bmp);
    }

    /// <summary>Trời ngày (theme sáng): xanh băng đậm dần lên đỉnh, bông tuyết rải rác, mặt trời mùa đông mờ góc phải trên.</summary>
    private static SKImage BuildDaySky()
    {
        using var bmp = new SKBitmap(new SKImageInfo(SkyW, SkyH, SKColorType.Bgra8888, SKAlphaType.Premul));
        XmasBitmap.Paint(bmp, 1, g =>
        {
            using (var top = new GdiLinear(new GdiRect(0, 0, SkyW, SkyH), GdiColor.FromArgb(255, 205, 230, 240), GdiColor.FromArgb(0, 234, 244, 240), GdiMode.Vertical))
                g.FillRectangle(top, 0, 0, SkyW, SkyH);
            var rnd = new Random(2412);
            for (int i = 0; i < 120; i++)
            {
                float x = rnd.Next(SkyW), y = rnd.Next((int)(SkyH * 0.85f)), r = 1.2f + (float)rnd.NextDouble() * 2.4f;
                XmasArt.Snowflake(g, x, y, r, 120 + rnd.Next(120));
            }
            float mr = 34, mx = SkyW * 0.82f, my = SkyH * 0.08f;
            using (var glow = new GdiPath())
            {
                glow.AddEllipse(mx - mr * 3.2f, my - mr * 3.2f, mr * 6.4f, mr * 6.4f);
                using var pg = new GdiPathGradient(glow) { CenterColor = GdiColor.FromArgb(120, 255, 246, 214), SurroundColors = [GdiColor.FromArgb(0, 255, 246, 214)] };
                g.FillPath(pg, glow);
            }
            using (var sun = new GdiSolid(GdiColor.FromArgb(200, 255, 251, 232))) g.FillEllipse(sun, mx - mr, my - mr, mr * 2, mr * 2);
        });
        return SKImage.FromBitmap(bmp);
    }

    private static SKImage BuildPanorama(bool dayMode)
    {
        using var bmp = new SKBitmap(new SKImageInfo(PanoW, PanoH, SKColorType.Bgra8888, SKAlphaType.Premul));
        XmasBitmap.Paint(bmp, 1, g =>
        {
            // trời: đậm dần xuống chân núi để hoà với nền khung
            using (var skyBrush = dayMode
                       ? new GdiLinear(new GdiRect(0, 0, PanoW, PanoH), GdiColor.FromArgb(0, 234, 244, 240), GdiColor.FromArgb(150, 215, 235, 227), GdiMode.Vertical)
                       : new GdiLinear(new GdiRect(0, 0, PanoW, PanoH), GdiColor.FromArgb(0, 12, 34, 24), GdiColor.FromArgb(150, 12, 40, 26), GdiMode.Vertical))
                g.FillRectangle(skyBrush, 0, 0, PanoW, PanoH);
            if (dayMode) XmasArt.DrawBackdropDay(g, PanoW, PanoH);
            else XmasArt.DrawBackdrop(g, PanoW, PanoH);
            XmasArt.DrawSkyline(g, new GdiRectF(0, PanoH - 58, PanoW, 46), 7);
            // mặt tuyết phía trước: dải lượn sóng sáng
            using var snow = new GdiPath();
            snow.AddLine(0, PanoH, 0, PanoH - 16);
            for (int x = 0; x <= PanoW; x += 60)
                snow.AddBezier(x, PanoH - 16 - (x / 60 % 2 == 0 ? 0 : 5), x + 20, PanoH - 24, x + 40, PanoH - 10, x + 60, PanoH - 16 - ((x / 60 + 1) % 2 == 0 ? 0 : 5));
            snow.AddLine(PanoW, PanoH - 16, PanoW, PanoH);
            snow.CloseFigure();
            using (var white = new GdiLinear(new GdiRect(0, PanoH - 26, PanoW, 26), GdiColor.FromArgb(235, 238, 246, 255), GdiColor.FromArgb(255, 205, 220, 238), GdiMode.Vertical))
                g.FillPath(white, snow);
            var rnd = new Random(77);
            for (int i = 0; i < 160; i++)
                XmasArt.Sparkle(g, rnd.Next(PanoW), PanoH - 4 - rnd.Next(14), 1.2f + (float)rnd.NextDouble() * 1.8f, GdiColor.FromArgb(120 + rnd.Next(120), 255, 255, 255));
        });
        return SKImage.FromBitmap(bmp);
    }

    /// <summary>Tìm khung log / danh sách đang hiện đủ lớn và gắn cảnh; cập nhật độ mờ theo lượng nội dung.</summary>
    private static void Scan()
    {
        if (!Active || owner == null || owner.WindowState == WindowState.Minimized)
        {
            return;
        }

        try
        {
            Walk(owner);
            attached.RemoveAll(w => !w.TryGetTarget(out _));
            foreach (var weak in attached)
            {
                if (weak.TryGetTarget(out var c) && c.IsEffectivelyVisible && targets.TryGetValue(c, out var t)
                    && (t.Bitmap == null || Math.Abs(OpacityFor(c) - t.Opacity) > 0.01 || Motion.Suppressed))
                {
                    Compose(c, t);
                }
            }
        }
        catch (Exception ex)
        {
            DebugLog.Write("XmasScenes: " + ex.Message);
        }
    }

    private static void Walk(Visual node)
    {
        foreach (var child in node.GetVisualChildren())
        {
            if (!child.IsVisible)
            {
                continue;
            }

            if (child is Control c && Eligible(c))
            {
                if (!targets.TryGetValue(c, out _))
                {
                    Attach(c);
                }

                continue;                 // không đi sâu vào trong khung đã gắn
            }

            if (child is ComboBox or Popup)
            {
                continue;
            }

            Walk(child);
        }
    }

    private static bool Eligible(Control c) =>
        c.Bounds.Width >= 300 && c.Bounds.Height >= 110 &&
        (c is ListBox or TreeView || c is TextBox { AcceptsReturn: true });

    private static void Attach(Control c)
    {
        // Mọi khung cùng một bố cục như Studio: làng tuyết bên phải, cây thông + người tuyết bên trái.
        var t = new Target();
        targets.Add(c, t);
        attached.Add(new WeakReference<Control>(c));
        c.SizeChanged += OnSize;
        Compose(c, t);
    }

    private static void OnSize(object? sender, SizeChangedEventArgs e)
    {
        if (sender is Control c && targets.TryGetValue(c, out var t) && c.IsEffectivelyVisible)
        {
            Compose(c, t);
        }
    }

    /// <summary>Cảnh rõ khi khung trống, mờ dần khi nội dung nhiều.</summary>
    private static double OpacityFor(Control c) => c switch
    {
        TextBox tb when string.IsNullOrEmpty(tb.Text) => 0.95,
        TextBox tb => tb.Text!.Count(ch => ch == '\n') < 5 ? 0.7 : 0.38,
        ItemsControl ic when ic.ItemCount == 0 => 0.95,
        ItemsControl ic => ic.ItemCount < 6 ? 0.7 : 0.38,
        _ => 0.6,
    };

    /// <summary>Ghép nền cho một khung: màu nền gốc + (trời, quả châu, toàn cảnh, làng, cây thông) theo độ mờ, rồi báo vẽ lại.</summary>
    private static void Compose(Control c, Target t)
    {
        if (owner == null || village == null || trees == null || baubles == null)
        {
            return;
        }

        double w = c.Bounds.Width, h = c.Bounds.Height, op = OpacityFor(c);
        if (w < 2 || h < 2)
        {
            return;
        }

        double scale = owner.RenderScaling > 0 ? owner.RenderScaling : 1;
        int pw = (int)Math.Ceiling(w * scale), ph = (int)Math.Ceiling(h * scale);
        bool fresh = false;
        if (t.Bitmap == null || t.Bitmap.PixelSize.Width != pw || t.Bitmap.PixelSize.Height != ph)
        {
            var old = t.Bitmap;
            t.Bitmap = XmasBitmap.Create(pw, ph, scale);
            t.BrushA = new ImageBrush(t.Bitmap) { Stretch = Stretch.Fill };
            t.BrushB = new ImageBrush(t.Bitmap) { Stretch = Stretch.Fill };
            t.Painter = null;
            fresh = true;
            old?.Dispose();
        }

        t.Opacity = op;

        // Màu nền gốc của khung (giá trị style / cục bộ, bỏ qua lớp Animation của chính mình).
        SKColor baseColor = SKColors.Transparent;
        var baseBrush = c.GetBaseValue(TemplatedControl.BackgroundProperty);
        if (baseBrush.HasValue && baseBrush.Value is ISolidColorBrush solid && solid.Color.A > 0)
        {
            var col = solid.Color;
            baseColor = new SKColor(col.R, col.G, col.B, (byte)Math.Round(col.A * Math.Clamp(solid.Opacity, 0, 1)));
        }

        // Bố cục (như Studio): trời phủ cả khung, toàn cảnh trải đáy, làng tuyết bên phải, cây thông + người tuyết bên trái, quả châu
        // treo ở đỉnh khi khung cao và còn trống.
        double pnh = Math.Min(h * 0.92, Math.Max(96, Math.Min(w * 0.16, 200)));
        double vh = Math.Min(h * 0.9, Math.Max(pnh * 1.15, Math.Min(h * 0.55, 240)));
        double vw = vh * SceneW / SceneH;
        if (vw > w * 0.6)
        {
            vw = w * 0.6;
            vh = vw * SceneH / SceneW;
        }

        double th = vh * 0.82, tw = th * SceneW / SceneH;
        t.UsesTrees = w - vw - w * 0.03 > tw + w * 0.06;
        t.UsesBaubles = h >= 300 && op >= 0.9;   // chỉ treo quả châu khi khung còn trống (không đè dòng log đầu)

        if (t.Static == null || t.StaticW != pw || t.StaticH != ph)
        {
            t.Static?.Dispose();
            t.Static = BuildStatic(w, h, pw, ph, scale, pnh);
            t.StaticW = pw;
            t.StaticH = ph;
        }

        using (var fb = t.Bitmap.Lock())
        {
            var info = new SKImageInfo(fb.Size.Width, fb.Size.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var surface = SKSurface.Create(info, fb.Address, fb.RowBytes);
            if (surface == null)
            {
                return;
            }

            var canvas = surface.Canvas;
            canvas.Clear(baseColor);
            using var layer = new SKPaint { Color = new SKColor(255, 255, 255, (byte)Math.Round(op * 255)) };
            canvas.SaveLayer(layer);
            if (t.Static != null)
            {
                canvas.DrawImage(t.Static, 0, 0);
            }

            canvas.Scale((float)scale);
            using var paint = new SKPaint { FilterQuality = SKFilterQuality.Low, IsAntialias = true };
            if (t.UsesBaubles)
            {
                double bw = Math.Min(w * 0.8, 340), bh = bw * BaubleH / BaubleW;
                canvas.DrawBitmap(baubles, SKRect.Create((float)((w - bw) / 2 - w * 0.08), 0, (float)bw, (float)bh), paint);
            }

            canvas.DrawBitmap(village, SKRect.Create((float)(w - vw - w * 0.03), (float)(h - vh), (float)vw, (float)vh), paint);
            if (t.UsesTrees)
            {
                canvas.DrawBitmap(trees, SKRect.Create((float)(w * 0.03), (float)(h - th), (float)tw, (float)th), paint);
            }

            canvas.Restore();
            canvas.Flush();
        }

        if (fresh || t.Override == null)
        {
            t.Override?.Dispose();
            t.UsingB = false;
            t.Override = c.SetValue(TemplatedControl.BackgroundProperty, t.BrushA, BindingPriority.Animation);
            return;
        }

        // Ảnh đổi điểm ảnh tại chỗ: báo vẽ lại phần tử trong template đang tô nền bằng ảnh này; không tìm được thì đổi sang cọ thứ hai.
        t.Painter ??= c.GetVisualDescendants().FirstOrDefault(v => v is Border b && (ReferenceEquals(b.Background, t.BrushA) || ReferenceEquals(b.Background, t.BrushB))
                                                                  || v is Panel p && (ReferenceEquals(p.Background, t.BrushA) || ReferenceEquals(p.Background, t.BrushB)));
        if (t.Painter != null)
        {
            t.Painter.InvalidateVisual();
            return;
        }

        t.UsingB = !t.UsingB;
        t.Override?.Dispose();
        t.Override = c.SetValue(TemplatedControl.BackgroundProperty, t.UsingB ? t.BrushB : t.BrushA, BindingPriority.Animation);
    }

    /// <summary>Trời đêm (cắt góc phải trên, không méo, trăng luôn ở góc) + toàn cảnh rặng thông trải đáy — ghép một lần theo cỡ khung.</summary>
    private static SKImage? BuildStatic(double w, double h, int pw, int ph, double scale, double pnh)
    {
        using var surface = SKSurface.Create(new SKImageInfo(pw, ph, SKColorType.Bgra8888, SKAlphaType.Premul));
        if (surface == null)
        {
            return null;
        }

        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        canvas.Scale((float)scale);
        using var paint = new SKPaint { FilterQuality = SKFilterQuality.Medium, IsAntialias = true };
        double skyScale = Math.Max(Math.Max(w / SkyW, h / SkyH), 0.55);
        int cw = (int)Math.Min(SkyW, w / skyScale), ch = (int)Math.Min(SkyH, h / skyScale);
        canvas.DrawImage(Sky, SKRect.Create(SkyW - Math.Max(1, cw), 0, Math.Max(1, cw), Math.Max(1, ch)), SKRect.Create(0, 0, (float)w, (float)h), paint);
        int cropW = (int)Math.Min(PanoW, Math.Max(40, PanoH * (w / pnh)));
        canvas.DrawImage(Panorama, SKRect.Create(0, 0, cropW, PanoH), SKRect.Create(0, (float)(h - pnh), (float)w, (float)pnh), paint);
        canvas.Flush();
        return surface.Snapshot();
    }
}
