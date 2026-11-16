// Port từ PSVIETHOA STUDIO 2.1.0 (PkgStudio.App/XmasArt.cs, gốc DiskHealth) — cùng tác giả, cùng bộ hình Giáng sinh để các app đồng bộ.
// Mã vẽ giữ gần như nguyên văn; System.Drawing được thay bằng lớp tương thích Xmas.Gdi chạy trên SkiaSharp (Windows · macOS · Linux).
using PsViethoa.FpkgBuilder.App.Xmas.Gdi;

namespace PsViethoa.FpkgBuilder.App.Xmas;

/// <summary>
/// Hình vẽ Giáng sinh (vector GDI+, không cần ảnh): xe trượt ông già Noel + tuần lộc bay, cảnh cây thông + người tuyết
/// có đèn nhấp nháy, rặng thông xa trên nền. Mọi hàm nhận thời gian t (giây) để tự chuyển động.
/// </summary>
static partial class XmasArt
{
    public static Color Mix(Color a, Color b, float t) => Color.FromArgb(
        (int)(a.A + (b.A - a.A) * t), (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));

    static readonly Color Gold = Color.FromArgb(240, 196, 88), GoldDark = Color.FromArgb(176, 128, 40);
    static readonly Color SantaRed = Color.FromArgb(214, 36, 44), SantaRedDark = Color.FromArgb(150, 18, 28);
    static readonly Color Fur = Color.FromArgb(250, 250, 248), Skin = Color.FromArgb(250, 208, 180);
    static readonly Color Deer = Color.FromArgb(150, 98, 56), DeerDark = Color.FromArgb(104, 64, 34), DeerLight = Color.FromArgb(214, 176, 130);
    static readonly Color[] LightColors =
    {
        Color.FromArgb(255, 70, 70), Color.FromArgb(255, 210, 80), Color.FromArgb(90, 170, 255), Color.FromArgb(110, 230, 120), Color.FromArgb(255, 130, 220),
    };

    // ================================================================ xe trượt + tuần lộc

    /// <summary>Chiều dài cả đoàn (đơn vị thiết kế) — từ đuôi xe tới mũi tuần lộc đầu.</summary>
    public const float SleighLength = 300;

    /// <summary>
    /// Vẽ ông già Noel trên xe trượt kéo bởi 2 tuần lộc (con đầu mũi đỏ). (x, y) = giữa xe trượt; scale = pixel / đơn vị;
    /// right = bay sang phải. Trả về toạ độ màn hình của đuôi xe (để rắc bụi sao).
    /// </summary>
    public static PointF DrawSleigh(Graphics g, float x, float y, float scale, float tilt, bool right, double t)
    {
        var st = g.Save();
        g.TranslateTransform(x, y);
        g.RotateTransform(right ? tilt : -tilt);
        g.ScaleTransform(right ? scale : -scale, scale);

        // dây cương + dây kéo vẽ trước để nằm dưới tuần lộc
        float bob1 = (float)Math.Sin(t * 7.5) * 3, bob2 = (float)Math.Sin(t * 7.5 + 1.3) * 3;
        PointF d1 = new(120, -6 + bob1), d2 = new(215, -10 + bob2);
        using (var trace = new Pen(Color.FromArgb(230, Gold), 2f))
        {
            g.DrawBezier(trace, 44, -2, 70, 6, 85, 4, d1.X - 22, d1.Y + 2);
            g.DrawBezier(trace, d1.X + 18, d1.Y - 10, d1.X + 45, d1.Y - 2, d2.X - 50, d2.Y + 4, d2.X - 22, d2.Y + 2);
        }
        using (var rein = new Pen(Color.FromArgb(200, 120, 40, 30), 1.4f))
            g.DrawBezier(rein, 14, -34, 50, -12, 90, -26, d1.X + 22, d1.Y - 14);

        DrawReindeer(g, d1.X, d1.Y, t, 0f, rudolph: false);
        DrawReindeer(g, d2.X, d2.Y, t, 1.3f, rudolph: true);
        DrawSleighBody(g, t);
        g.Restore(st);

        // đuôi xe (-58, 10) đổi ra toạ độ ngoài: lật (nếu bay trái) → xoay → dời
        float tx = (right ? -58 : 58) * scale, ty = 10 * scale;
        double a = (right ? tilt : -tilt) * Math.PI / 180;
        return new PointF(x + (float)(tx * Math.Cos(a) - ty * Math.Sin(a)), y + (float)(tx * Math.Sin(a) + ty * Math.Cos(a)));
    }

    static void DrawSleighBody(Graphics g, double t)
    {
        // bao quà sau lưng
        using (var sack = new GraphicsPath())
        {
            sack.AddBezier(-56, -8, -66, -30, -58, -52, -40, -50);
            sack.AddBezier(-40, -50, -26, -48, -22, -30, -24, -8);
            sack.CloseFigure();
            using var br = new LinearGradientBrush(new RectangleF(-66, -52, 46, 46), Color.FromArgb(150, 110, 70), Color.FromArgb(100, 70, 40), 30f);
            g.FillPath(br, sack);
        }
        Box(g, -52, -60, 12, 12, Color.FromArgb(60, 140, 230), Gold);
        Box(g, -40, -64, 13, 14, Color.FromArgb(70, 180, 90), Color.FromArgb(230, 60, 60));
        using (var tie = new Pen(Color.FromArgb(120, 80, 45), 2.2f)) g.DrawArc(tie, -54, -52, 26, 8, 0, 180);

        // ông già Noel
        float wave = (float)Math.Sin(t * 5) * 14;
        using (var coat = new LinearGradientBrush(new RectangleF(-24, -46, 40, 44), SantaRed, SantaRedDark, 90f))
            g.FillEllipse(coat, -24, -46, 38, 44);
        // tay vẫy (tay trái phía sau) + tay cầm cương
        Arm(g, -10, -36, -150 + wave, 18);
        Arm(g, 4, -36, 35, 16);
        // đầu
        using (var face = new SolidBrush(Skin)) g.FillEllipse(face, -11, -63, 20, 19);
        using (var fur = new SolidBrush(Fur))
        {
            // râu + ria
            using var beard = new GraphicsPath();
            beard.AddBezier(-12, -52, -14, -36, 0, -30, 10, -40);
            beard.AddBezier(10, -40, 12, -46, 11, -50, 9, -52);
            beard.CloseFigure();
            g.FillPath(fur, beard);
            g.FillEllipse(fur, -4, -53, 8, 4);
            g.FillEllipse(fur, 2, -53, 8, 4);
        }
        using (var eye = new SolidBrush(Color.FromArgb(40, 30, 30))) g.FillEllipse(eye, 1.5f, -56.8f, 2.8f, 2.8f);
        using (var nose = new SolidBrush(Color.FromArgb(235, 140, 130))) g.FillEllipse(nose, 6, -55, 4, 3.5f);
        using (var cheek = new SolidBrush(Color.FromArgb(110, 240, 110, 110))) g.FillEllipse(cheek, -1, -54, 5, 3);
        // mũ: chóp đung đưa ra sau
        float sway = (float)Math.Sin(t * 6) * 3;
        using (var hat = new GraphicsPath())
        {
            hat.AddBezier(-12, -62, -10, -74, -20, -80, -30 + sway, -72);
            hat.AddBezier(-30 + sway, -72, -18, -74, 0, -76, 10, -62);
            hat.CloseFigure();
            using var br = new SolidBrush(SantaRed);
            g.FillPath(br, hat);
        }
        using (var fur = new SolidBrush(Fur))
        {
            Draw.Fill(g, new RectangleF(-13, -66, 25, 6), 3, Fur);
            g.FillEllipse(fur, -35 + sway, -76, 9, 9);
        }

        // thân xe: đỏ, viền vàng, đuôi cong cao
        using (var body = new GraphicsPath())
        {
            body.AddBezier(-60, -26, -66, -10, -60, 12, -48, 16);
            body.AddLine(-48, 16, 34, 16);
            body.AddBezier(34, 16, 50, 16, 56, 0, 48, -12);
            body.AddBezier(48, -12, 40, -6, 26, -4, 14, -6);
            body.AddLine(14, -6, -36, -8);
            body.AddBezier(-36, -8, -46, -12, -50, -24, -60, -26);
            body.CloseFigure();
            using var br = new LinearGradientBrush(new RectangleF(-66, -28, 124, 46), Color.FromArgb(230, 50, 50), Color.FromArgb(140, 16, 24), 90f);
            g.FillPath(br, body);
            using var trim = new Pen(Gold, 2.4f) { LineJoin = LineJoin.Round };
            g.DrawPath(trim, body);
        }
        using (var swirl = new Pen(Color.FromArgb(220, Gold), 1.6f))
        {
            g.DrawBezier(swirl, -40, 6, -30, -4, -16, 10, -6, 2);
            g.DrawBezier(swirl, -6, 2, 4, -6, 18, 8, 28, 0);
        }
        // càng trượt
        using (var run = new Pen(Gold, 3f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            g.DrawLine(run, -36, 16, -36, 26);
            g.DrawLine(run, 22, 16, 22, 26);
            g.DrawLine(run, -54, 27, 44, 27);
            g.DrawBezier(run, 44, 27, 62, 27, 66, 10, 56, 8);
        }
    }

    static void Arm(Graphics g, float sx, float sy, float angleDeg, float len)
    {
        double a = angleDeg * Math.PI / 180;
        float ex = sx + (float)Math.Cos(a) * len, ey = sy + (float)Math.Sin(a) * len;
        using (var sleeve = new Pen(SantaRed, 8) { StartCap = LineCap.Round, EndCap = LineCap.Flat })
            g.DrawLine(sleeve, sx, sy, ex, ey);
        using var cuff = new SolidBrush(Fur);
        g.FillEllipse(cuff, ex - 4.5f, ey - 4.5f, 9, 9);
        using var mitt = new SolidBrush(Color.FromArgb(40, 120, 70));
        g.FillEllipse(mitt, ex + (float)Math.Cos(a) * 4 - 3.5f, ey + (float)Math.Sin(a) * 4 - 3.5f, 7, 7);
    }

    static void Box(Graphics g, float x, float y, float w, float h, Color c, Color ribbon)
    {
        using (var br = new SolidBrush(c)) g.FillRectangle(br, x, y, w, h);
        using var rb = new SolidBrush(ribbon);
        g.FillRectangle(rb, x + w / 2 - 1.2f, y, 2.4f, h);
        g.FillRectangle(rb, x, y + h / 2 - 1.2f, w, 2.4f);
        using var bow = new Pen(ribbon, 1.6f);
        g.DrawEllipse(bow, x + w / 2 - 4, y - 3, 4, 3);
        g.DrawEllipse(bow, x + w / 2, y - 3, 4, 3);
    }

    /// <summary>Một con tuần lộc đang phi trên không, gốc toạ độ ở giữa thân, quay mặt sang phải.</summary>
    static void DrawReindeer(Graphics g, float x, float y, double t, float phase, bool rudolph)
    {
        var st = g.Save();
        g.TranslateTransform(x, y);
        double ph = t * 7.5 + phase;

        // chân: phi nước đại — chân trước duỗi ra trước, chân sau đạp ra sau
        void Leg(float hx, float hy, double baseA, double swing, bool front, Color c)
        {
            double a1 = baseA + Math.Sin(ph + swing) * 0.55;
            double a2 = a1 + (front ? -0.9 : 0.8) * (0.6 + 0.4 * Math.Sin(ph + swing + 0.8));
            float kx = hx + (float)Math.Sin(a1) * 12, ky = hy + (float)Math.Cos(a1) * 12;
            float fx = kx + (float)Math.Sin(a2) * 12, fy = ky + (float)Math.Cos(a2) * 12;
            using var p = new Pen(c, 4.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            g.DrawLines(p, new[] { new PointF(hx, hy), new PointF(kx, ky), new PointF(fx, fy) });
            using var hoof = new SolidBrush(Color.FromArgb(60, 40, 30));
            g.FillEllipse(hoof, fx - 2.6f, fy - 2.6f, 5.2f, 5.2f);
        }
        Leg(-16, 4, -0.7, 0.6, false, DeerDark);   // chân xa vẽ trước (tối hơn)
        Leg(16, 4, 0.9, 0.6, true, DeerDark);

        using (var tail = new SolidBrush(Fur)) g.FillEllipse(tail, -32, -12, 9, 7);
        using (var body = new LinearGradientBrush(new RectangleF(-28, -13, 56, 26), Deer, DeerDark, 90f))
            g.FillEllipse(body, -28, -13, 56, 26);
        using (var belly = new SolidBrush(Color.FromArgb(150, DeerLight))) g.FillEllipse(belly, -18, 2, 34, 9);

        Leg(-12, 5, -0.7, 0, false, Deer);
        Leg(20, 5, 0.9, 0, true, Deer);

        // cổ + đầu (gật nhẹ theo nhịp phi)
        float nod = (float)Math.Sin(ph) * 2;
        using (var neck = new Pen(Deer, 11) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawLine(neck, 16, -4, 30, -22 + nod);
        // vòng cổ đỏ + chuông vàng
        using (var collar = new Pen(Color.FromArgb(200, 30, 40), 3.4f)) g.DrawLine(collar, 18, -14, 26, -6);
        using (var bell = new SolidBrush(Gold)) g.FillEllipse(bell, 22, -8, 5, 5);

        // gạc
        using (var antler = new Pen(Color.FromArgb(222, 196, 150), 2.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
        {
            float ay = -32 + nod;
            g.DrawLines(antler, new[] { new PointF(30, ay), new PointF(26, ay - 10), new PointF(18, ay - 16) });
            g.DrawLine(antler, 27, ay - 8, 33, ay - 15);
            g.DrawLine(antler, 22, ay - 13, 21, ay - 20);
            using var far = new Pen(Color.FromArgb(180, 196, 166, 120), 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLines(far, new[] { new PointF(34, ay), new PointF(33, ay - 11), new PointF(27, ay - 19) });
            g.DrawLine(far, 33, ay - 8, 39, ay - 13);
        }
        using (var head = new SolidBrush(Deer))
        {
            g.FillEllipse(head, 25, -33 + nod, 18, 14);
            g.FillEllipse(head, 35, -29 + nod, 13, 9);   // mõm
        }
        using (var ear = new SolidBrush(DeerDark)) g.FillEllipse(ear, 22, -35 + nod, 8, 5);
        using (var eye = new SolidBrush(Color.FromArgb(30, 20, 20))) g.FillEllipse(eye, 34, -29 + nod, 3, 3);

        if (rudolph)
        {
            // mũi đỏ phát sáng, nhịp sáng theo thời gian
            float glow = 9 + (float)(Math.Sin(t * 4) * 0.5 + 0.5) * 7;
            using (var gp = new GraphicsPath())
            {
                gp.AddEllipse(48 - glow, -25 + nod - glow, glow * 2, glow * 2);
                using var pg = new PathGradientBrush(gp) { CenterColor = Color.FromArgb(170, 255, 60, 50), SurroundColors = new[] { Color.FromArgb(0, 255, 60, 50) } };
                g.FillPath(pg, gp);
            }
            using var nose = new SolidBrush(Color.FromArgb(255, 40, 40));
            g.FillEllipse(nose, 44.5f, -28.5f + nod, 7, 7);
            using var hi = new SolidBrush(Color.FromArgb(220, 255, 210, 200));
            g.FillEllipse(hi, 46, -27.5f + nod, 2.4f, 2.4f);
        }
        else
        {
            using var nose = new SolidBrush(Color.FromArgb(60, 36, 26));
            g.FillEllipse(nose, 45.5f, -27 + nod, 4.5f, 4.5f);
        }
        g.Restore(st);
    }

    // ================================================================ cảnh cây thông + người tuyết

    /// <summary>Cảnh tuyết: đồi tuyết, 3 cây thông có đèn nhấp nháy + ngôi sao, quà, người tuyết vẫy khăn. Vẽ vừa khung r.</summary>
    public static void DrawTreeScene(Graphics g, RectangleF r, double t, int alpha = 255)
    {
        float H = r.Height, W = r.Width;
        float groundY = r.Bottom - H * 0.16f;

        // đồi tuyết phía sau
        using (var hill = new GraphicsPath())
        {
            hill.AddBezier(r.Left, r.Bottom, r.Left + W * 0.02f, groundY, r.Left + W * 0.04f, groundY - H * 0.05f, r.Left + W * 0.1f, groundY - H * 0.06f);
            hill.AddBezier(r.Left + W * 0.1f, groundY - H * 0.06f, r.Left + W * 0.35f, groundY - H * 0.14f, r.Left + W * 0.6f, groundY + H * 0.02f, r.Right - W * 0.1f, groundY - H * 0.07f);
            hill.AddBezier(r.Right - W * 0.1f, groundY - H * 0.07f, r.Right - W * 0.04f, groundY - H * 0.06f, r.Right - W * 0.02f, groundY, r.Right, r.Bottom);
            hill.CloseFigure();
            using var br = new LinearGradientBrush(new RectangleF(r.Left, groundY - H * 0.16f, W, H * 0.34f),
                Color.FromArgb(alpha, 236, 244, 252), Color.FromArgb(alpha, 190, 210, 230), 90f);
            g.FillPath(br, hill);
        }

        float unit = Math.Min(H / 200f, W / 330f);
        Tree(g, r.Left + W * 0.12f, groundY, unit * 92, t, 0, alpha, star: false);
        Tree(g, r.Left + W * 0.40f, groundY + 2, unit * 170, t, 3, alpha, star: true);
        Tree(g, r.Left + W * 0.88f, groundY - 2, unit * 118, t, 7, alpha, star: false);

        // quà dưới gốc cây lớn
        float bx = r.Left + W * 0.40f + unit * 28, by = groundY - unit * 4;
        Box(g, bx, by - unit * 16, unit * 18, unit * 16, Color.FromArgb(alpha, 210, 40, 50), Color.FromArgb(alpha, Gold));
        Box(g, bx + unit * 20, by - unit * 11, unit * 14, unit * 11, Color.FromArgb(alpha, 60, 130, 220), Color.FromArgb(alpha, 250, 250, 250));
        Box(g, r.Left + W * 0.40f - unit * 44, by - unit * 12, unit * 15, unit * 12, Color.FromArgb(alpha, 120, 70, 200), Color.FromArgb(alpha, Gold));

        Snowman(g, r.Left + W * 0.67f, groundY + unit * 4, unit, t, alpha);

        // lớp tuyết sát đáy phía trước
        using (var front = new GraphicsPath())
        {
            front.AddBezier(r.Left, r.Bottom, r.Left + W * 0.01f, groundY + H * 0.08f, r.Left + W * 0.03f, groundY + H * 0.05f, r.Left + W * 0.08f, groundY + H * 0.04f);
            front.AddBezier(r.Left + W * 0.08f, groundY + H * 0.04f, r.Left + W * 0.3f, groundY - H * 0.01f, r.Left + W * 0.55f, groundY + H * 0.09f, r.Right - W * 0.08f, groundY + H * 0.03f);
            front.AddBezier(r.Right - W * 0.08f, groundY + H * 0.03f, r.Right - W * 0.03f, groundY + H * 0.04f, r.Right - W * 0.01f, groundY + H * 0.08f, r.Right, r.Bottom);
            front.CloseFigure();
            using var br = new SolidBrush(Color.FromArgb(alpha, 246, 250, 255));
            g.FillPath(br, front);
        }
    }

    // ================================================================ làng tuyết: nhà cửa + cây thông + người tuyết

    /// <summary>
    /// Làng Giáng sinh về đêm (vẽ vừa khung r): hai ngôi nhà mái phủ tuyết, cửa sổ vàng ấm lập loè, ống khói toả khói,
    /// đèn dây chớp dọc mái; cây thông lớn có sao + cây nhỏ, quà, người tuyết vẫy tay, cột đèn đường, hàng rào.
    /// </summary>
    public static void DrawVillageScene(Graphics g, RectangleF r, double t, int alpha = 255)
    {
        float H = r.Height, W = r.Width;
        float u = Math.Min(H / 210f, W / 330f);
        // khung thiết kế 330×210 đơn vị, căn giữa theo chiều ngang
        float ox = r.Left + (W - 330 * u) / 2, groundY = r.Bottom - 30 * u;
        float X(float v) => ox + v * u;

        // đồi tuyết phía sau
        using (var hill = new GraphicsPath())
        {
            hill.AddBezier(r.Left, groundY + 6 * u, r.Left + W * 0.18f, groundY - 26 * u, r.Left + W * 0.42f, groundY - 4 * u, r.Left + W * 0.58f, groundY - 14 * u);
            hill.AddBezier(r.Left + W * 0.58f, groundY - 14 * u, r.Left + W * 0.74f, groundY - 24 * u, r.Left + W * 0.9f, groundY - 6 * u, r.Right, groundY - 10 * u);
            hill.AddLine(r.Right, groundY - 10 * u, r.Right, r.Bottom);
            hill.AddLine(r.Right, r.Bottom, r.Left, r.Bottom);
            hill.CloseFigure();
            using var br = new LinearGradientBrush(new RectangleF(r.Left, groundY - 28 * u, W, 60 * u),
                Color.FromArgb(alpha * 235 / 255, 214, 228, 244), Color.FromArgb(alpha, 168, 192, 222), 90f);
            g.FillPath(br, hill);
        }

        // cây nhỏ phía sau hai bên
        Tree(g, X(14), groundY - 4 * u, u * 62, t, 5, alpha, star: false);
        Tree(g, X(318), groundY - 6 * u, u * 70, t, 9, alpha, star: false);

        // hai ngôi nhà
        House(g, X(78), groundY, 104 * u, 58 * u, 42 * u, Color.FromArgb(178, 64, 52), Color.FromArgb(120, 38, 34), t, 1, alpha, door: true);
        House(g, X(262), groundY + 2 * u, 82 * u, 46 * u, 34 * u, Color.FromArgb(70, 110, 160), Color.FromArgb(42, 70, 112), t, 4, alpha, door: false);

        // cây thông lớn giữa làng
        Tree(g, X(170), groundY + 4 * u, u * 150, t, 3, alpha, star: true);
        float gy = groundY + 3 * u;
        Box(g, X(178), gy - 15 * u, 17 * u, 15 * u, Color.FromArgb(alpha, 210, 40, 50), Color.FromArgb(alpha, Gold));
        Box(g, X(197), gy - 10 * u, 13 * u, 10 * u, Color.FromArgb(alpha, 60, 130, 220), Color.FromArgb(alpha, 250, 250, 250));
        Box(g, X(146), gy - 12 * u, 15 * u, 12 * u, Color.FromArgb(alpha, 120, 70, 200), Color.FromArgb(alpha, Gold));

        // cột đèn đường
        LampPost(g, X(126), groundY + 6 * u, u, t, alpha);

        // hàng rào gỗ trước nhà xanh
        using (var fence = new SolidBrush(Color.FromArgb(alpha, 120, 84, 56)))
        using (var cap = new SolidBrush(Color.FromArgb(alpha, 246, 250, 255)))
        {
            for (int i = 0; i < 6; i++)
            {
                float fx = X(232 + i * 12);
                g.FillRectangle(fence, fx, groundY + 2 * u, 4 * u, 14 * u);
                g.FillEllipse(cap, fx - 1 * u, groundY + 0.5f * u, 6 * u, 3.5f * u);
            }
            g.FillRectangle(fence, X(230), groundY + 7 * u, 66 * u, 2.5f * u);
        }

        Snowman(g, X(212), groundY + 16 * u, u * 0.72f, t, alpha);

        // lớp tuyết sát đáy phía trước
        using (var front = new GraphicsPath())
        {
            float fy = r.Bottom - 12 * u;
            front.AddBezier(r.Left, fy, r.Left + W * 0.25f, fy - 8 * u, r.Left + W * 0.5f, fy + 6 * u, r.Left + W * 0.75f, fy - 4 * u);
            front.AddBezier(r.Left + W * 0.75f, fy - 4 * u, r.Left + W * 0.85f, fy - 7 * u, r.Left + W * 0.95f, fy - 2 * u, r.Right, fy - 3 * u);
            front.AddLine(r.Right, fy - 3 * u, r.Right, r.Bottom);
            front.AddLine(r.Right, r.Bottom, r.Left, r.Bottom);
            front.CloseFigure();
            using var br = new SolidBrush(Color.FromArgb(alpha, 246, 250, 255));
            g.FillPath(br, front);
        }
        // lấp lánh trên tuyết
        var rnd = new Random(77);
        for (int i = 0; i < 14; i++)
        {
            float sx = r.Left + (float)rnd.NextDouble() * W, sy = r.Bottom - (float)rnd.NextDouble() * 12 * u;
            float tw = (float)(Math.Sin(t * 3 + i * 2.1) * 0.5 + 0.5);
            if (tw > 0.6f) Sparkle(g, sx, sy, (1.5f + 2.5f * tw) * u, Color.FromArgb((int)(220 * tw * alpha / 255), 255, 255, 255));
        }
    }

    /// <summary>Ngôi nhà gỗ: (cx, baseY) = giữa chân tường; mái phủ tuyết, ống khói + khói, cửa sổ sáng ấm, đèn dây dọc mái.</summary>
    static void House(Graphics g, float cx, float baseY, float w, float bodyH, float roofH, Color wall, Color wallDark,
                      double t, int seed, int alpha, bool door)
    {
        float u = w / 104f;
        float left = cx - w / 2, top = baseY - bodyH, ov = 8 * u, ridge = top - roofH;
        float RoofY(float x) => ridge + Math.Min(1f, Math.Abs(x - cx) / (w / 2 + ov)) * roofH;

        // ống khói (vẽ trước để mái che chân) + khói
        float chx = cx + w * 0.24f, chw = 11 * u, chTop = RoofY(chx) - 15 * u;
        using (var br = new SolidBrush(Color.FromArgb(alpha, 120, 60, 50))) g.FillRectangle(br, chx - chw / 2, chTop, chw, RoofY(chx) - chTop + 6 * u);
        using (var br = new SolidBrush(Color.FromArgb(alpha, 92, 44, 38))) g.FillRectangle(br, chx - chw / 2 - 1.5f * u, chTop, chw + 3 * u, 3 * u);
        Draw.Fill(g, new RectangleF(chx - chw / 2 - 2 * u, chTop - 3 * u, chw + 4 * u, 4 * u), 2 * u, Color.FromArgb(alpha, 248, 251, 255));
        for (int i = 0; i < 5; i++)
        {
            double ph = (t * 0.28 + i / 5.0 + seed * 0.13) % 1.0;
            float py = chTop - 4 * u - (float)ph * 46 * u;
            float px = chx + (float)(Math.Sin(ph * 5 + i + seed) * 5 * ph + ph * 10) * u;
            float pr = (3 + (float)ph * 8) * u;
            int pa = (int)((1 - ph) * (ph < 0.12 ? ph / 0.12 : 1) * 95 * alpha / 255);
            using var smoke = new SolidBrush(Color.FromArgb(pa, 225, 230, 238));
            g.FillEllipse(smoke, px - pr, py - pr, pr * 2, pr * 2);
        }

        // tường gỗ
        var body = new RectangleF(left, top, w, bodyH);
        using (var br = new LinearGradientBrush(body, Color.FromArgb(alpha, wall), Color.FromArgb(alpha, wallDark), 0f)) g.FillRectangle(br, body);
        using (var plank = new Pen(Color.FromArgb(alpha * 50 / 255, 0, 0, 0), Math.Max(0.6f, 0.8f * u)))
            for (float y = top + 7 * u; y < baseY - 2 * u; y += 7 * u) g.DrawLine(plank, left, y, left + w, y);

        // cửa sổ sáng ấm, lập loè như ánh lò sưởi
        void Window(float wx, float wy, float ww, float wh, int k)
        {
            float f = 0.82f + 0.18f * (float)(Math.Sin(t * 6.1 + k * 1.7 + seed) * Math.Sin(t * 2.3 + k + seed * 2));
            float gr = Math.Max(ww, wh) * 1.5f;
            using (var gp = new GraphicsPath())
            {
                gp.AddEllipse(wx + ww / 2 - gr, wy + wh / 2 - gr, gr * 2, gr * 2);
                using var pg = new PathGradientBrush(gp) { CenterColor = Color.FromArgb((int)(120 * f * alpha / 255), 255, 196, 90), SurroundColors = new[] { Color.FromArgb(0, 255, 196, 90) } };
                g.FillPath(pg, gp);
            }
            var wr = new RectangleF(wx, wy, ww, wh);
            using (var br = new LinearGradientBrush(wr, Color.FromArgb(alpha, 255, (int)(226 * f), (int)(130 * f)), Color.FromArgb(alpha, 250, (int)(170 * f), 60), 90f))
                g.FillRectangle(br, wr);
            using (var frame = new Pen(Color.FromArgb(alpha, 92, 54, 34), Math.Max(1f, 1.6f * u)))
            {
                g.DrawRectangle(frame, wx, wy, ww, wh);
                g.DrawLine(frame, wx + ww / 2, wy, wx + ww / 2, wy + wh);
                g.DrawLine(frame, wx, wy + wh / 2, wx + ww, wy + wh / 2);
            }
            Draw.Fill(g, new RectangleF(wx - 2 * u, wy + wh - 0.5f * u, ww + 4 * u, 3 * u), 1.5f * u, Color.FromArgb(alpha, 248, 251, 255));
        }
        float winW = 20 * u, winH = 17 * u, winY = top + bodyH * 0.24f;
        if (door)
        {
            Window(left + w * 0.10f, winY, winW, winH, 0);
            Window(left + w * 0.70f, winY, winW, winH, 1);
            // cửa ra vào + vòng nguyệt quế
            float dw = 19 * u, dh = 31 * u, dx = cx - dw / 2 + 2 * u, dy = baseY - dh;
            using (var br = new SolidBrush(Color.FromArgb(alpha, 96, 56, 34))) g.FillRectangle(br, dx, dy, dw, dh);
            using (var pen = new Pen(Color.FromArgb(alpha, 70, 38, 22), Math.Max(1f, 1.2f * u))) g.DrawRectangle(pen, dx, dy, dw, dh);
            using (var knob = new SolidBrush(Color.FromArgb(alpha, Gold))) g.FillEllipse(knob, dx + dw - 5 * u, dy + dh * 0.55f, 2.6f * u, 2.6f * u);
            float wcx = dx + dw / 2, wcy = dy + 9 * u, wr = 6 * u;
            using (var ring = new Pen(Color.FromArgb(alpha, 40, 130, 64), 3.2f * u)) g.DrawEllipse(ring, wcx - wr, wcy - wr, wr * 2, wr * 2);
            for (int i = 0; i < 6; i++)
            {
                double a = i * Math.PI / 3 + 0.3;
                using var berry = new SolidBrush(Color.FromArgb(alpha, 220, 40, 50));
                g.FillEllipse(berry, wcx + (float)Math.Cos(a) * wr - 1.1f * u, wcy + (float)Math.Sin(a) * wr - 1.1f * u, 2.2f * u, 2.2f * u);
            }
            using (var bow = new SolidBrush(Color.FromArgb(alpha, 214, 36, 44)))
                g.FillPolygon(bow, new[] { new PointF(wcx, wcy + wr), new PointF(wcx - 4 * u, wcy + wr - 3 * u), new PointF(wcx - 4 * u, wcy + wr + 3 * u),
                                           new PointF(wcx, wcy + wr), new PointF(wcx + 4 * u, wcy + wr - 3 * u), new PointF(wcx + 4 * u, wcy + wr + 3 * u) });
        }
        else
        {
            Window(left + w * 0.14f, winY, winW, winH, 0);
            Window(left + w * 0.62f, winY, winW, winH, 1);
        }

        // mái + lớp tuyết dày
        var roof = new[] { new PointF(left - ov, top + 1), new PointF(cx, ridge), new PointF(left + w + ov, top + 1) };
        using (var br = new LinearGradientBrush(new RectangleF(left - ov, ridge, w + 2 * ov, roofH + 2), Color.FromArgb(alpha, 74, 46, 40), Color.FromArgb(alpha, 48, 30, 28), 90f))
            g.FillPolygon(br, roof);
        using (var snowPath = new GraphicsPath())
        {
            float k = 0.78f;
            PointF a = new(cx - (w / 2 + ov) * k, ridge + roofH * k), b = new(cx + (w / 2 + ov) * k, ridge + roofH * k);
            snowPath.AddLine(new PointF(cx, ridge - 3.5f * u), new PointF(left - ov - 2 * u, top + 2 * u));
            snowPath.AddBezier(new PointF(left - ov - 2 * u, top + 2 * u), new PointF(left - ov + 6 * u, top + 6 * u), new PointF(a.X - 4 * u, a.Y + 7 * u), a);
            snowPath.AddBezier(a, new PointF(cx - w * 0.15f, a.Y + 6 * u), new PointF(cx + w * 0.15f, b.Y - 3 * u), b);
            snowPath.AddBezier(b, new PointF(b.X + 4 * u, b.Y + 7 * u), new PointF(left + w + ov - 6 * u, top + 6 * u), new PointF(left + w + ov + 2 * u, top + 2 * u));
            snowPath.CloseFigure();
            using var br = new LinearGradientBrush(new RectangleF(left - ov, ridge - 4 * u, w + 2 * ov, roofH + 10 * u), Color.FromArgb(alpha, 255, 255, 255), Color.FromArgb(alpha, 214, 228, 244), 90f);
            g.FillPath(br, snowPath);
        }
        // nhũ băng dưới mái
        using (var ice = new SolidBrush(Color.FromArgb(alpha * 200 / 255, 220, 236, 252)))
            for (int i = 0; i < 7; i++)
            {
                float ix = left + w * (0.06f + i * 0.145f), il = (3 + (i * 7 + seed) % 5) * u;
                g.FillPolygon(ice, new[] { new PointF(ix - 1.4f * u, top + 2 * u), new PointF(ix + 1.4f * u, top + 2 * u), new PointF(ix, top + 2 * u + il) });
            }

        // đèn dây dọc hai mép mái, chạy đuổi nhau
        int n = 13;
        for (int i = 0; i < n; i++)
        {
            float p = i / (float)(n - 1);
            PointF e1 = new(left - ov + 2 * u, top + 3 * u), e2 = new(left + w + ov - 2 * u, top + 3 * u);
            float bx = e1.X + (e2.X - e1.X) * p;
            float by = top + 4.5f * u + (i % 2) * 1.2f * u;
            var c = LightColors[(i + seed) % LightColors.Length];
            double blink = Math.Sin(t * 4.2 - i * 0.9 + seed);
            Bulb(g, bx, by, Math.Max(1.3f, 2.1f * u), c, blink > 0 ? (float)(0.6 + 0.4 * blink) : 0.15f, alpha);
        }
    }

    /// <summary>Cột đèn đường kiểu cổ, quấn ruy băng đỏ, toả quầng sáng vàng.</summary>
    static void LampPost(Graphics g, float x, float baseY, float u, double t, int alpha)
    {
        float h = 62 * u, top = baseY - h;
        using (var pole = new Pen(Color.FromArgb(alpha, 40, 44, 52), Math.Max(1.4f, 3f * u))) g.DrawLine(pole, x, baseY, x, top + 6 * u);
        using (var rib = new Pen(Color.FromArgb(alpha, 210, 40, 50), Math.Max(1f, 2f * u)))
            for (int i = 0; i < 6; i++) g.DrawLine(rib, x - 1.6f * u, baseY - 8 * u - i * 8 * u, x + 1.6f * u, baseY - 12 * u - i * 8 * u);
        float f = 0.9f + 0.1f * (float)Math.Sin(t * 5.3);
        float gr = 22 * u;
        using (var gp = new GraphicsPath())
        {
            gp.AddEllipse(x - gr, top - gr + 2 * u, gr * 2, gr * 2);
            using var pg = new PathGradientBrush(gp) { CenterColor = Color.FromArgb((int)(130 * f * alpha / 255), 255, 214, 120), SurroundColors = new[] { Color.FromArgb(0, 255, 214, 120) } };
            g.FillPath(pg, gp);
        }
        using (var lamp = new SolidBrush(Color.FromArgb(alpha, 255, 232, 160))) g.FillRectangle(lamp, x - 4 * u, top - 3 * u, 8 * u, 9 * u);
        using (var frame = new Pen(Color.FromArgb(alpha, 40, 44, 52), Math.Max(1f, 1.4f * u)))
        {
            g.DrawRectangle(frame, x - 4 * u, top - 3 * u, 8 * u, 9 * u);
            g.DrawLine(frame, x - 6 * u, top - 3 * u, x + 6 * u, top - 3 * u);
        }
        Draw.Fill(g, new RectangleF(x - 6.5f * u, top - 6 * u, 13 * u, 3.5f * u), 1.7f * u, Color.FromArgb(alpha, 248, 251, 255));
    }

    /// <summary>
    /// Dây đèn treo võng ngang mép trên (toạ độ: y0 = điểm treo, từ x0 đến x1). Bóng nhiều màu chớp theo kiểu đuổi nhau + lấp lánh ngẫu nhiên.
    /// </summary>
    public static void DrawGarland(Graphics g, float x0, float x1, float y0, float scale, double t)
    {
        float span = 120 * scale, sag = 14 * scale, r = Math.Max(1.8f, 3.6f * scale);
        int segs = Math.Max(1, (int)Math.Round((x1 - x0) / span));
        span = (x1 - x0) / segs;
        using var wire = new Pen(Color.FromArgb(200, 34, 52, 36), Math.Max(1f, 1.3f * scale));
        int idx = 0;
        for (int s = 0; s < segs; s++)
        {
            float a = x0 + s * span, b = a + span;
            g.DrawBezier(wire, a, y0, a + span * 0.3f, y0 + sag * 1.33f, b - span * 0.3f, y0 + sag * 1.33f, b, y0);
            int per = 5;
            for (int i = 0; i < per; i++)
            {
                float p = (i + 0.5f) / per;
                float bx = Bez(a, a + span * 0.3f, b - span * 0.3f, b, p);
                float by = Bez(y0, y0 + sag * 1.33f, y0 + sag * 1.33f, y0, p);
                var c = LightColors[idx % LightColors.Length];
                // đuổi nhau theo nhóm 3 bóng + mỗi bóng thỉnh thoảng loé
                double chase = Math.Sin(t * 3.4 - idx * 1.05);
                double twinkle = Math.Sin(t * 9.7 + idx * 4.3);
                float on = chase > -0.15 ? (float)(0.62 + 0.38 * chase) : 0.16f;
                if (twinkle > 0.97) on = 1f;
                // đuôi bóng (cổ đui) + bóng hơi nghiêng theo dây
                using (var socket = new SolidBrush(Color.FromArgb(230, 40, 56, 40)))
                    g.FillRectangle(socket, bx - r * 0.5f, by - r * 0.4f, r, r * 0.9f);
                Bulb(g, bx, by + r * 1.1f, r, c, on, 255);
                idx++;
            }
        }
        // móc treo
        using var hook = new SolidBrush(Color.FromArgb(230, 34, 52, 36));
        for (int s = 0; s <= segs; s++) g.FillEllipse(hook, x0 + s * span - 2 * scale, y0 - 2 * scale, 4 * scale, 4 * scale);
    }

    // ================================================================ quả châu treo + dãy nhà mờ

    static readonly (Color Hi, Color Lo)[] BaubleColors =
    {
        (Color.FromArgb(255, 110, 110), Color.FromArgb(150, 16, 28)),   // đỏ
        (Color.FromArgb(255, 230, 140), Color.FromArgb(176, 120, 30)),  // vàng
        (Color.FromArgb(140, 200, 255), Color.FromArgb(30, 70, 160)),   // xanh dương
        (Color.FromArgb(245, 248, 255), Color.FromArgb(130, 140, 160)), // bạc
        (Color.FromArgb(130, 230, 160), Color.FromArgb(20, 110, 60)),   // xanh lá
    };

    /// <summary>
    /// Các quả châu Noel bóng loáng treo bằng ruy băng từ mép trên khung r, đung đưa nhẹ; chuỗi đèn nhỏ võng giữa các dây treo,
    /// cành nhựa ruồi ở góc. Vẽ vừa trong r (không tràn ra chữ xung quanh).
    /// </summary>
    public static void DrawOrnaments(Graphics g, RectangleF r, double t, float scale)
    {
        int n = Math.Clamp((int)(r.Width / (46 * scale)), 2, 7);
        float step = r.Width / n;
        float maxR = Math.Min(13 * scale, step * 0.32f);
        var anchors = new PointF[n];
        var rnd = new Random(31);
        for (int i = 0; i < n; i++)
        {
            float ax = r.Left + step * (i + 0.5f);
            float len = (0.30f + 0.48f * (float)rnd.NextDouble()) * (r.Height - maxR * 2.4f);
            float rad = maxR * (0.72f + 0.28f * (float)rnd.NextDouble());
            double swing = Math.Sin(t * (1.1 + i * 0.13) + i * 1.7) * 0.10;   // ~±6°
            float bx = ax + (float)Math.Sin(swing) * len, by = r.Top + (float)Math.Cos(swing) * len;
            anchors[i] = new PointF(ax, r.Top);
            using (var rib = new Pen(Color.FromArgb(220, 214, 168, 70), Math.Max(1f, 1.2f * scale))) g.DrawLine(rib, ax, r.Top, bx, by - rad);
            Bauble(g, bx, by, rad, BaubleColors[(i * 2 + 1) % BaubleColors.Length], (float)swing, t + i);
        }
        // chuỗi đèn nhỏ võng giữa các điểm treo
        int idx = 0;
        for (int i = 0; i + 1 < n; i++)
        {
            PointF a = anchors[i], b = anchors[i + 1];
            float sag = Math.Min(10 * scale, r.Height * 0.12f);
            using (var wire = new Pen(Color.FromArgb(150, 40, 70, 46), Math.Max(1f, scale)))
                g.DrawBezier(wire, a.X, a.Y + 1, a.X + (b.X - a.X) * 0.3f, a.Y + sag * 1.33f, b.X - (b.X - a.X) * 0.3f, b.Y + sag * 1.33f, b.X, b.Y + 1);
            for (int k = 1; k <= 3; k++)
            {
                float p = k / 4f;
                float lx = Bez(a.X, a.X + (b.X - a.X) * 0.3f, b.X - (b.X - a.X) * 0.3f, b.X, p);
                float ly = Bez(a.Y + 1, a.Y + sag * 1.33f, b.Y + sag * 1.33f, b.Y + 1, p);
                double blink = Math.Sin(t * 3.1 - idx * 1.2);
                Bulb(g, lx, ly + 2 * scale, Math.Max(1.3f, 2.2f * scale), LightColors[idx % LightColors.Length], blink > -0.1 ? (float)(0.6 + 0.4 * blink) : 0.15f, 255);
                idx++;
            }
        }
        // nơ đỏ ở mỗi điểm treo
        foreach (var a in anchors) Bow(g, a.X, a.Y + 2 * scale, 5 * scale);
    }

    static void Bauble(Graphics g, float x, float y, float r, (Color Hi, Color Lo) c, float swing, double t)
    {
        // quầng sáng phản chiếu mờ
        using (var gp = new GraphicsPath())
        {
            gp.AddEllipse(x - r * 1.9f, y - r * 1.9f, r * 3.8f, r * 3.8f);
            using var pg = new PathGradientBrush(gp) { CenterColor = Color.FromArgb(55, c.Hi), SurroundColors = new[] { Color.FromArgb(0, c.Hi) } };
            g.FillPath(pg, gp);
        }
        // nắp kim loại
        using (var cap = new LinearGradientBrush(new RectangleF(x - r * 0.3f, y - r * 1.25f, r * 0.6f, r * 0.4f), Color.FromArgb(250, 230, 160), Color.FromArgb(170, 130, 50), 0f))
            g.FillRectangle(cap, x - r * 0.28f, y - r * 1.22f, r * 0.56f, r * 0.34f);
        // thân cầu bóng: tâm sáng lệch trái-trên
        using (var gp = new GraphicsPath())
        {
            gp.AddEllipse(x - r, y - r, r * 2, r * 2);
            using var pg = new PathGradientBrush(gp)
            {
                CenterPoint = new PointF(x - r * 0.35f, y - r * 0.4f),
                CenterColor = c.Hi,
                SurroundColors = new[] { c.Lo },
            };
            g.FillPath(pg, gp);
        }
        // sọc trang trí + điểm bóng loáng di chuyển nhẹ theo nhịp đung đưa
        using (var band = new Pen(Color.FromArgb(150, 255, 240, 200), Math.Max(1f, r * 0.12f)))
            g.DrawArc(band, x - r * 0.98f, y - r * 0.35f, r * 1.96f, r * 0.7f, 10, 160);
        float shine = (float)(0.75 + 0.25 * Math.Sin(t * 2.4));
        using (var hi = new SolidBrush(Color.FromArgb((int)(220 * shine), 255, 255, 255)))
            g.FillEllipse(hi, x - r * 0.55f + swing * r, y - r * 0.62f, r * 0.42f, r * 0.3f);
        using (var hi2 = new SolidBrush(Color.FromArgb((int)(140 * shine), 255, 255, 255)))
            g.FillEllipse(hi2, x - r * 0.18f + swing * r, y - r * 0.72f, r * 0.14f, r * 0.12f);
    }

    static void Bow(Graphics g, float x, float y, float s)
    {
        using var red = new SolidBrush(Color.FromArgb(235, 214, 36, 44));
        g.FillPolygon(red, new[] { new PointF(x, y), new PointF(x - s, y - s * 0.6f), new PointF(x - s, y + s * 0.6f) });
        g.FillPolygon(red, new[] { new PointF(x, y), new PointF(x + s, y - s * 0.6f), new PointF(x + s, y + s * 0.6f) });
        using var knot = new SolidBrush(Color.FromArgb(240, 170, 20, 30));
        g.FillEllipse(knot, x - s * 0.3f, y - s * 0.3f, s * 0.6f, s * 0.6f);
    }

    /// <summary>Dãy nhà + cây thông dạng bóng mờ phủ tuyết dưới đáy khung r, cửa sổ vàng ấm (tĩnh) — làm nền cho biểu đồ.</summary>
    public static void DrawSkyline(Graphics g, RectangleF r, int seed = 12)
    {
        var rnd = new Random(seed);
        float baseY = r.Bottom, H = r.Height;
        using var body = new SolidBrush(Color.FromArgb(60, 4, 22, 14));
        using var snow = new SolidBrush(Color.FromArgb(60, 236, 246, 255));
        using var win = new SolidBrush(Color.FromArgb(120, 255, 200, 100));
        using var winGlow = new SolidBrush(Color.FromArgb(28, 255, 200, 100));
        float x = r.Left - rnd.Next(20);
        while (x < r.Right)
        {
            if (rnd.Next(100) < 45)
            {
                // ngôi nhà
                float w = H * (0.55f + (float)rnd.NextDouble() * 0.5f), bh = H * (0.38f + (float)rnd.NextDouble() * 0.25f), rh = bh * 0.7f;
                float top = baseY - bh;
                g.FillRectangle(body, x, top, w, bh);
                var roof = new[] { new PointF(x - w * 0.08f, top), new PointF(x + w / 2, top - rh), new PointF(x + w * 1.08f, top) };
                g.FillPolygon(body, roof);
                g.FillPolygon(snow, new[] { roof[0], roof[1], roof[2], new PointF(x + w * 0.8f, top + bh * 0.05f), new PointF(x + w * 0.2f, top + bh * 0.05f) });
                int cols = Math.Max(1, (int)(w / (H * 0.22f)));
                for (int c = 0; c < cols; c++)
                {
                    if (rnd.Next(100) < 25) continue;
                    float ww = H * 0.09f, wx = x + w * (c + 0.5f) / cols - ww / 2, wy = top + bh * 0.3f;
                    g.FillEllipse(winGlow, wx - ww, wy - ww, ww * 3, ww * 3);
                    g.FillRectangle(win, wx, wy, ww, ww * 1.1f);
                }
                x += w * (1.15f + (float)rnd.NextDouble() * 0.5f);
            }
            else
            {
                // cây thông
                float h = H * (0.5f + (float)rnd.NextDouble() * 0.5f), w = h * 0.36f;
                g.FillPolygon(body, new[] { new PointF(x + w, baseY - h), new PointF(x + w * 2, baseY), new PointF(x, baseY) });
                g.FillPolygon(snow, new[] { new PointF(x + w, baseY - h), new PointF(x + w * 1.25f, baseY - h * 0.75f), new PointF(x + w * 0.75f, baseY - h * 0.75f) });
                x += w * (1.6f + (float)rnd.NextDouble() * 0.8f);
            }
        }
        g.FillRectangle(snow, r.Left, baseY - 2, r.Width, 2);
    }

    // ================================================================ trang trí thẻ (tĩnh)

    /// <summary>Lớp tuyết mỏng đọng dọc mép trên một thẻ bo góc (vài chỗ dày, có giọt chảy).</summary>
    public static void CardSnow(Graphics g, RectangleF card, float radius, int seed)
    {
        var rnd = new Random(seed * 7919 + 13);
        float x0 = card.X + radius * 0.6f, x1 = card.Right - radius * 0.6f, y = card.Y;
        using var path = new GraphicsPath();
        int n = Math.Max(4, (int)((x1 - x0) / 34));
        float step = (x1 - x0) / n;
        path.AddLine(x0, y + 1.5f, x0, y);
        for (int i = 0; i < n; i++)
        {
            float a = x0 + i * step, b = a + step, lift = 1.5f + (float)rnd.NextDouble() * 3.2f;
            path.AddBezier(a, y, a + step * 0.3f, y - lift, b - step * 0.3f, y - lift, b, y);
        }
        path.AddLine(x1, y, x1, y + 1.5f);
        for (int i = n; i > 0; i--)
        {
            float b = x0 + i * step, a = b - step;
            float drip = rnd.Next(100) < 30 ? 3.5f + (float)rnd.NextDouble() * 3f : 1.2f;
            path.AddBezier(b, y + 1.5f, b - step * 0.35f, y + 1.5f + drip, a + step * 0.35f, y + 1.5f + drip, a, y + 1.5f);
        }
        path.CloseFigure();
        using var br = new LinearGradientBrush(new RectangleF(card.X, y - 6, card.Width, 14), Color.FromArgb(250, 255, 255, 255), Color.FromArgb(230, 206, 222, 240), 90f);
        g.FillPath(br, path);
    }

    static readonly (Color Box, Color Ribbon)[] GiftColors =
    {
        (Color.FromArgb(214, 40, 52), Color.FromArgb(246, 206, 96)),
        (Color.FromArgb(38, 140, 78), Color.FromArgb(246, 246, 246)),
        (Color.FromArgb(52, 112, 210), Color.FromArgb(246, 206, 96)),
        (Color.FromArgb(150, 80, 200), Color.FromArgb(246, 246, 246)),
        (Color.FromArgb(236, 170, 40), Color.FromArgb(214, 40, 52)),
    };

    /// <summary>Hộp quà nhỏ (biểu tượng thẻ phân vùng), màu theo chỉ số.</summary>
    public static void GiftIcon(Graphics g, RectangleF r, int index)
    {
        var (box, rib) = GiftColors[((index % GiftColors.Length) + GiftColors.Length) % GiftColors.Length];
        float lidH = r.Height * 0.28f;
        var body = new RectangleF(r.X + r.Width * 0.08f, r.Y + lidH + r.Height * 0.12f, r.Width * 0.84f, r.Height - lidH - r.Height * 0.12f);
        var lid = new RectangleF(r.X, r.Y + r.Height * 0.12f, r.Width, lidH);
        using (var br = new LinearGradientBrush(body, XmasArt.Mix(box, Color.White, 0.15f), XmasArt.Mix(box, Color.Black, 0.25f), 90f)) g.FillRectangle(br, body);
        using (var br = new SolidBrush(XmasArt.Mix(box, Color.White, 0.08f))) g.FillRectangle(br, lid);
        using (var br = new SolidBrush(rib))
        {
            float rw = r.Width * 0.18f;
            g.FillRectangle(br, r.X + r.Width / 2 - rw / 2, lid.Y, rw, r.Bottom - lid.Y);
            g.FillRectangle(br, lid.X, lid.Y + lidH * 0.35f, lid.Width, lidH * 0.3f);
            float cx = r.X + r.Width / 2, by = lid.Y;
            g.FillEllipse(br, cx - r.Width * 0.36f, by - r.Height * 0.2f, r.Width * 0.34f, r.Height * 0.24f);
            g.FillEllipse(br, cx + r.Width * 0.02f, by - r.Height * 0.2f, r.Width * 0.34f, r.Height * 0.24f);
        }
        using var shade = new Pen(Color.FromArgb(70, 0, 0, 0), 1f);
        g.DrawRectangle(shade, body.X, body.Y, body.Width, body.Height);
    }

    /// <summary>Màu kẹo cho thanh ở trạng thái bình thường (Giáng sinh). Cam/đỏ dành riêng cho cảnh báo nên không có trong bảng.</summary>
    public static readonly Color CandyMint = Color.FromArgb(38, 178, 112), CandyIce = Color.FromArgb(64, 150, 236);
    static readonly Color[] CandyPalette =
    {
        Color.FromArgb(232, 84, 156),   // hồng dâu
        Color.FromArgb(150, 96, 226),   // tím nho
        Color.FromArgb(24, 176, 186),   // xanh ngọc
        Color.FromArgb(124, 196, 44),   // chanh
        Color.FromArgb(70, 160, 240),   // xanh băng
    };
    /// <summary>Màu kẹo theo khoá (vd chữ cái ổ) — mỗi thanh một màu, ổn định giữa các lần vẽ.</summary>
    public static Color Candy(int key) => CandyPalette[((key % CandyPalette.Length) + CandyPalette.Length) % CandyPalette.Length];

    /// <summary>Sọc chéo kiểu kẹo gậy phủ lên phần đã tô của thanh dung lượng (giữ màu gốc của thanh) + ngôi sao nhỏ ở đầu.</summary>
    public static void CandyBar(Graphics g, RectangleF fill)
    {
        if (fill.Width < 4) return;
        var st = g.Save();
        using (var clip = Draw.Round(fill, fill.Height / 2)) g.SetClip(clip, CombineMode.Intersect);
        using (var stripe = new SolidBrush(Color.FromArgb(120, 255, 255, 255)))
        {
            float sw = fill.Height * 0.9f;
            for (float x = fill.X - fill.Height; x < fill.Right; x += sw * 2)
                g.FillPolygon(stripe, new[] { new PointF(x, fill.Bottom), new PointF(x + sw, fill.Bottom), new PointF(x + sw + fill.Height, fill.Y), new PointF(x + fill.Height, fill.Y) });
        }
        using (var gloss = new LinearGradientBrush(fill, Color.FromArgb(90, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 90f))
            g.FillRectangle(gloss, fill.X, fill.Y, fill.Width, fill.Height / 2);
        g.Restore(st);
        Star(g, fill.Right - fill.Height * 0.2f, fill.Y + fill.Height / 2, fill.Height * 0.95f, 0.6);
    }

    /// <summary>Cây thông 3 tầng có tuyết đọng, dây đèn nhấp nháy; (cx, baseY) = gốc thân; h = chiều cao.</summary>
    static void Tree(Graphics g, float cx, float baseY, float h, double t, int seed, int alpha, bool star)
    {
        float trunkW = h * 0.09f, trunkH = h * 0.1f;
        using (var trunk = new SolidBrush(Color.FromArgb(alpha, 110, 70, 40)))
            g.FillRectangle(trunk, cx - trunkW / 2, baseY - trunkH, trunkW, trunkH);
        float top = baseY - h;
        // tầng: từ dưới lên, mỗi tầng hẹp dần
        var tiers = new[] { (y0: 0.30f, y1: 0.90f, w: 0.40f), (y0: 0.16f, y1: 0.66f, w: 0.31f), (y0: 0.0f, y1: 0.42f, w: 0.22f) };
        int li = 0;
        for (int k = 0; k < tiers.Length; k++)
        {
            var (y0, y1, wf) = tiers[k];
            float ty = top + h * y0, by = top + h * y1, hw = h * wf;
            using var tier = new GraphicsPath();
            tier.AddLine(cx, ty, cx + hw, by);
            // mép dưới lượn sóng
            int waves = 4;
            for (int i = 0; i < waves; i++)
            {
                float xa = cx + hw - 2 * hw * i / waves, xb = cx + hw - 2 * hw * (i + 1) / waves;
                tier.AddBezier(xa, by, xa - hw / waves * 0.3f, by + h * 0.035f, xb + hw / waves * 0.3f, by + h * 0.035f, xb, by);
            }
            tier.CloseFigure();
            using (var br = new LinearGradientBrush(new RectangleF(cx - hw, ty - 1, hw * 2, by - ty + h * 0.05f),
                       Color.FromArgb(alpha, 46, 140, 74), Color.FromArgb(alpha, 18, 82, 46), 90f))
                g.FillPath(br, tier);
            // tuyết đọng mép dưới
            using (var snow = new Pen(Color.FromArgb(alpha * 235 / 255, 248, 252, 255), Math.Max(1.5f, h * 0.022f)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                for (int i = 0; i < 4; i++)
                {
                    float xa = cx + hw - 2 * hw * i / 4, xb = cx + hw - 2 * hw * (i + 1) / 4;
                    g.DrawBezier(snow, xa, by, xa - hw / 4 * 0.3f, by + h * 0.03f, xb + hw / 4 * 0.3f, by + h * 0.03f, xb, by);
                }
            }
            // dây đèn: đường cong chéo qua tầng
            float gy0 = ty + (by - ty) * 0.55f, gy1 = by - (by - ty) * 0.12f;
            using (var wire = new Pen(Color.FromArgb(alpha * 120 / 255, 30, 40, 30), 1f))
                g.DrawBezier(wire, cx - hw * 0.62f, gy0, cx - hw * 0.2f, gy1 + h * 0.03f, cx + hw * 0.3f, gy1, cx + hw * 0.82f, gy1 - (by - ty) * 0.05f);
            int n = 4 + k;
            for (int i = 0; i < n; i++)
            {
                float u = (i + 0.5f) / n;
                float lx = Bez(cx - hw * 0.62f, cx - hw * 0.2f, cx + hw * 0.3f, cx + hw * 0.82f, u);
                float ly = Bez(gy0, gy1 + h * 0.03f, gy1, gy1 - (by - ty) * 0.05f, u);
                var c = LightColors[(li + seed) % LightColors.Length];
                double blink = Math.Sin(t * 2.6 + li * 1.9 + seed);
                Bulb(g, lx, ly, Math.Max(1.6f, h * 0.018f), c, blink > -0.2 ? (float)(0.55 + 0.45 * blink) : 0.15f, alpha);
                li++;
            }
        }
        if (star) Star(g, cx, top - h * 0.01f, h * 0.075f, t, alpha);
    }

    static float Bez(float a, float b, float c, float d, float u)
    {
        float v = 1 - u;
        return v * v * v * a + 3 * v * v * u * b + 3 * v * u * u * c + u * u * u * d;
    }

    static void Bulb(Graphics g, float x, float y, float r, Color c, float on, int alpha)
    {
        if (on > 0.4f)
        {
            float gr = r * 4.2f;
            using var gp = new GraphicsPath();
            gp.AddEllipse(x - gr, y - gr, gr * 2, gr * 2);
            using var pg = new PathGradientBrush(gp) { CenterColor = Color.FromArgb((int)(150 * on * alpha / 255), c), SurroundColors = new[] { Color.FromArgb(0, c) } };
            g.FillPath(pg, gp);
        }
        var body = XmasArt.Mix(Color.FromArgb(70, 50, 40), c, Math.Clamp(on, 0, 1));
        using var br = new SolidBrush(Color.FromArgb(alpha, body));
        g.FillEllipse(br, x - r, y - r, r * 2, r * 2);
        if (on > 0.5f)
        {
            using var hi = new SolidBrush(Color.FromArgb((int)(200 * on * alpha / 255), 255, 255, 255));
            g.FillEllipse(hi, x - r * 0.45f, y - r * 0.55f, r * 0.8f, r * 0.8f);
        }
    }

    /// <summary>Ngôi sao vàng 5 cánh trên ngọn, toả sáng phập phồng.</summary>
    public static void Star(Graphics g, float x, float y, float r, double t, int alpha = 255)
    {
        float pulse = (float)(Math.Sin(t * 2.2) * 0.5 + 0.5);
        float gr = r * (2.4f + pulse * 1.2f);
        using (var gp = new GraphicsPath())
        {
            gp.AddEllipse(x - gr, y - gr, gr * 2, gr * 2);
            using var pg = new PathGradientBrush(gp) { CenterColor = Color.FromArgb((int)((120 + 80 * pulse) * alpha / 255), 255, 220, 120), SurroundColors = new[] { Color.FromArgb(0, 255, 220, 120) } };
            g.FillPath(pg, gp);
        }
        var pts = new PointF[10];
        for (int i = 0; i < 10; i++)
        {
            double a = -Math.PI / 2 + i * Math.PI / 5;
            float rr = i % 2 == 0 ? r : r * 0.45f;
            pts[i] = new PointF(x + (float)Math.Cos(a) * rr, y + (float)Math.Sin(a) * rr);
        }
        using var br = new LinearGradientBrush(new RectangleF(x - r, y - r, r * 2, r * 2), Color.FromArgb(alpha, 255, 236, 150), Color.FromArgb(alpha, 230, 170, 40), 90f);
        g.FillPolygon(br, pts);
        using var edge = new Pen(Color.FromArgb(alpha * 200 / 255, GoldDark), Math.Max(0.8f, r * 0.08f));
        g.DrawPolygon(edge, pts);
    }

    static void Snowman(Graphics g, float cx, float baseY, float u, double t, int alpha)
    {
        float r1 = u * 22, r2 = u * 15, r3 = u * 11;
        float y1 = baseY - r1, y2 = y1 - r1 - r2 + u * 5, y3 = y2 - r2 - r3 + u * 4;
        // tay cành cây (một tay vẫy)
        float wave = (float)Math.Sin(t * 3) * 0.35f;
        using (var stick = new Pen(Color.FromArgb(alpha, 110, 72, 40), Math.Max(1.4f, u * 2.2f)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            g.DrawLine(stick, cx - r2 * 0.8f, y2, cx - r2 * 2.0f, y2 + u * 6);
            double a = -0.75 + wave;
            float ex = cx + r2 * 0.8f + (float)Math.Cos(a) * u * 20, ey = y2 + (float)Math.Sin(a) * u * 20;
            g.DrawLine(stick, cx + r2 * 0.8f, y2, ex, ey);
            g.DrawLine(stick, ex - (float)Math.Cos(a) * u * 6, ey - (float)Math.Sin(a) * u * 6, ex - (float)Math.Cos(a) * u * 6 + u * 4, ey - (float)Math.Sin(a) * u * 6 - u * 5);
        }
        foreach (var (y, r) in new[] { (y1, r1), (y2, r2), (y3, r3) })
        {
            using var gp = new GraphicsPath();
            gp.AddEllipse(cx - r, y - r, r * 2, r * 2);
            using var pg = new PathGradientBrush(gp)
            {
                CenterPoint = new PointF(cx - r * 0.3f, y - r * 0.35f),
                CenterColor = Color.FromArgb(alpha, 255, 255, 255),
                SurroundColors = new[] { Color.FromArgb(alpha, 196, 214, 232) },
            };
            g.FillPath(pg, gp);
        }
        using (var coal = new SolidBrush(Color.FromArgb(alpha, 40, 40, 46)))
        {
            g.FillEllipse(coal, cx - r3 * 0.45f, y3 - r3 * 0.3f, u * 2.6f, u * 2.6f);
            g.FillEllipse(coal, cx + r3 * 0.15f, y3 - r3 * 0.3f, u * 2.6f, u * 2.6f);
            for (int i = 0; i < 3; i++) g.FillEllipse(coal, cx - u * 1.4f, y2 - r2 * 0.4f + i * r2 * 0.4f, u * 2.8f, u * 2.8f);
            // miệng cười
            for (int i = 0; i < 5; i++)
            {
                double a = Math.PI * (0.2 + 0.15 * i);
                g.FillEllipse(coal, cx + (float)Math.Cos(a) * r3 * 0.5f - u * 0.8f, y3 + (float)Math.Sin(a) * r3 * 0.45f - u * 0.6f, u * 1.6f, u * 1.6f);
            }
        }
        // mũi cà rốt
        using (var carrot = new SolidBrush(Color.FromArgb(alpha, 245, 130, 40)))
            g.FillPolygon(carrot, new[] { new PointF(cx + u * 0.5f, y3 - u * 1.5f), new PointF(cx + u * 0.5f, y3 + u * 1.5f), new PointF(cx + u * 9, y3 + u * 0.6f) });
        // khăn đỏ, đuôi khăn bay
        float flap = (float)Math.Sin(t * 4) * u * 3;
        using (var scarf = new SolidBrush(Color.FromArgb(alpha, 210, 40, 50)))
        {
            Draw.Fill(g, new RectangleF(cx - r3 * 0.95f, y3 + r3 * 0.7f, r3 * 1.9f, u * 5), u * 2.5f, Color.FromArgb(alpha, 210, 40, 50));
            g.FillPolygon(scarf, new[]
            {
                new PointF(cx - r3 * 0.6f, y3 + r3 * 0.9f), new PointF(cx - r3 * 0.2f, y3 + r3 * 0.9f),
                new PointF(cx - r3 * 0.5f + flap, y3 + r3 * 0.9f + u * 13), new PointF(cx - r3 * 0.95f + flap, y3 + r3 * 0.9f + u * 12),
            });
        }
        // mũ đen
        using (var hat = new SolidBrush(Color.FromArgb(alpha, 34, 34, 40)))
        {
            g.FillRectangle(hat, cx - r3 * 0.95f, y3 - r3 * 0.9f, r3 * 1.9f, u * 2.5f);
            g.FillRectangle(hat, cx - r3 * 0.6f, y3 - r3 * 0.9f - u * 11, r3 * 1.2f, u * 11);
        }
        using (var band = new SolidBrush(Color.FromArgb(alpha, 200, 40, 50)))
            g.FillRectangle(band, cx - r3 * 0.6f, y3 - r3 * 0.9f - u * 3.5f, r3 * 1.2f, u * 2.2f);
    }

    // ================================================================ nền xa (tĩnh)

    /// <summary>Trăng, sao nhỏ và rặng thông xa phủ tuyết ở đáy nền — tĩnh, vẽ một lần cùng nền.</summary>
    public static void DrawBackdrop(Graphics g, int width, int height)
    {
        var rnd = new Random(0x4E6F);
        // sao nhỏ ở nửa trên
        for (int i = 0; i < width * height / 9000; i++)
        {
            float sx = rnd.Next(width), sy = rnd.Next(Math.Max(1, height / 2)), s = 0.6f + (float)rnd.NextDouble() * 1.4f;
            using var br = new SolidBrush(Color.FromArgb(40 + rnd.Next(80), 255, 250, 225));
            g.FillEllipse(br, sx, sy, s, s);
        }
        // trăng mờ góc phải trên
        float mr = Math.Min(width, height) * 0.06f, mx = width * 0.86f, my = height * 0.14f;
        using (var gp = new GraphicsPath())
        {
            gp.AddEllipse(mx - mr * 3, my - mr * 3, mr * 6, mr * 6);
            using var pg = new PathGradientBrush(gp) { CenterColor = Color.FromArgb(50, 255, 246, 210), SurroundColors = new[] { Color.FromArgb(0, 255, 246, 210) } };
            g.FillPath(pg, gp);
        }
        using (var moon = new SolidBrush(Color.FromArgb(70, 255, 248, 220))) g.FillEllipse(moon, mx - mr, my - mr, mr * 2, mr * 2);

        // hai lớp rặng thông: lớp xa nhạt + lớp gần đậm hơn, đỉnh có tuyết
        for (int layer = 0; layer < 2; layer++)
        {
            float baseY = height - (layer == 0 ? height * 0.06f : 0);
            float th = height * (layer == 0 ? 0.20f : 0.15f);
            var fill = layer == 0 ? Color.FromArgb(90, 30, 82, 56) : Color.FromArgb(150, 10, 40, 26);
            using var br = new SolidBrush(fill);
            using var snow = new SolidBrush(Color.FromArgb(layer == 0 ? 40 : 60, 235, 245, 255));
            float x = -rnd.Next(30);
            while (x < width + 40)
            {
                float h = th * (0.55f + (float)rnd.NextDouble() * 0.45f), w = h * 0.42f;
                g.FillPolygon(br, new[] { new PointF(x, baseY - h), new PointF(x + w, baseY + 2), new PointF(x - w, baseY + 2) });
                g.FillPolygon(snow, new[] { new PointF(x, baseY - h), new PointF(x + w * 0.22f, baseY - h * 0.78f), new PointF(x - w * 0.22f, baseY - h * 0.78f) });
                x += w * (0.9f + (float)rnd.NextDouble() * 0.7f);
            }
            using var ground = new SolidBrush(Color.FromArgb(layer == 0 ? 30 : 55, 230, 240, 250));
            g.FillRectangle(ground, 0, baseY, width, height - baseY + 1);
        }
    }

    /// <summary>Lấp lánh 4 cánh (bụi sao sau xe trượt, vệt chuột Giáng sinh).</summary>
    public static void Sparkle(Graphics g, float x, float y, float s, Color c)
    {
        using var br = new SolidBrush(c);
        var pts = new[]
        {
            new PointF(x, y - s), new PointF(x + s * 0.22f, y - s * 0.22f), new PointF(x + s, y), new PointF(x + s * 0.22f, y + s * 0.22f),
            new PointF(x, y + s), new PointF(x - s * 0.22f, y + s * 0.22f), new PointF(x - s, y), new PointF(x - s * 0.22f, y - s * 0.22f),
        };
        g.FillPolygon(br, pts);
    }
}
