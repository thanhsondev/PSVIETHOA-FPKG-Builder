using PsViethoa.FpkgBuilder.App.Xmas.Gdi;

namespace PsViethoa.FpkgBuilder.App.Xmas;

/// <summary>
/// Biến thể "ngày tuyết" cho giao diện SÁNG (riêng bản FPKG Builder; Studio chỉ có theme tối): trời xanh băng nhạt, mặt trời mờ thay
/// cho trăng, bông tuyết thay cho sao, rặng thông xanh lá nhạt phủ tuyết, đồi tuyết trắng. Cùng bố cục với bản đêm để hai theme
/// đổi qua lại vẫn đúng chỗ.
/// </summary>
static partial class XmasArt
{
    /// <summary>Nền Giáng sinh ban ngày cho cả cửa sổ (vẽ một lần): chuyển sắc xanh băng + đốm sáng pastel + rặng thông phủ tuyết.</summary>
    public static void PaintFestiveDay(Graphics g, int width, int height)
    {
        width = Math.Max(1, width); height = Math.Max(1, height);
        var rect = new Rectangle(0, 0, width, height);
        using (var grad = new LinearGradientBrush(rect, Color.FromArgb(234, 244, 240), Color.FromArgb(215, 235, 227), LinearGradientMode.Vertical))
            g.FillRectangle(grad, rect);
        var rnd = new Random(0x5A6F);
        Color[] bokeh = [Color.FromArgb(60, 255, 214, 150), Color.FromArgb(45, 255, 170, 170), Color.FromArgb(55, 170, 225, 200)];
        for (int i = 0; i < 16; i++)
        {
            float bx = rnd.Next(width), by = rnd.Next(height), br = 6 + rnd.Next(26);
            var c = bokeh[rnd.Next(bokeh.Length)];
            using var gp = new GraphicsPath();
            gp.AddEllipse(bx - br, by - br, br * 2, br * 2);
            using var pg = new PathGradientBrush(gp) { CenterColor = c, SurroundColors = [Color.FromArgb(0, c)] };
            g.FillEllipse(pg, bx - br, by - br, br * 2, br * 2);
        }
        DrawBackdropDay(g, width, height);
    }

    /// <summary>Bông tuyết 6 cánh nhỏ (thay sao trên nền sáng): nét trắng có viền xanh xám nhẹ để vẫn thấy trên nền nhạt.</summary>
    public static void Snowflake(Graphics g, float x, float y, float r, int alpha)
    {
        using var edge = new Pen(Color.FromArgb(alpha * 110 / 255, 120, 150, 170), Math.Max(1.2f, r * 0.34f)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        using var arm = new Pen(Color.FromArgb(alpha, 255, 255, 255), Math.Max(0.7f, r * 0.18f)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        foreach (var pen in new[] { edge, arm })
            for (int k = 0; k < 3; k++)
            {
                double a = k * Math.PI / 3 + Math.PI / 2;
                float dx = (float)Math.Cos(a) * r, dy = (float)Math.Sin(a) * r;
                g.DrawLine(pen, x - dx, y - dy, x + dx, y + dy);
            }
    }

    /// <summary>Mặt trời mùa đông mờ góc phải trên, bông tuyết nhỏ ở nửa trên, hai lớp rặng thông xanh lá nhạt phủ tuyết ở đáy — tĩnh.</summary>
    public static void DrawBackdropDay(Graphics g, int width, int height)
    {
        var rnd = new Random(0x4E6F);
        // bông tuyết nhỏ ở nửa trên (cùng vị trí với sao của bản đêm)
        for (int i = 0; i < width * height / 9000; i++)
        {
            float sx = rnd.Next(width), sy = rnd.Next(Math.Max(1, height / 2)), s = 0.6f + (float)rnd.NextDouble() * 1.4f;
            int a = 90 + rnd.Next(110);
            if (i % 3 == 0) Snowflake(g, sx, sy, s * 2.2f, a);
            else
            {
                using var br = new SolidBrush(Color.FromArgb(a, 255, 255, 255));
                using var edge = new Pen(Color.FromArgb(a / 3, 120, 150, 170), 0.6f);
                g.FillEllipse(br, sx, sy, s * 1.4f, s * 1.4f);
                g.DrawEllipse(edge, sx, sy, s * 1.4f, s * 1.4f);
            }
        }
        // mặt trời mờ góc phải trên
        float mr = Math.Min(width, height) * 0.06f, mx = width * 0.86f, my = height * 0.14f;
        using (var gp = new GraphicsPath())
        {
            gp.AddEllipse(mx - mr * 3, my - mr * 3, mr * 6, mr * 6);
            using var pg = new PathGradientBrush(gp) { CenterColor = Color.FromArgb(90, 255, 244, 205), SurroundColors = new[] { Color.FromArgb(0, 255, 244, 205) } };
            g.FillPath(pg, gp);
        }
        using (var sun = new SolidBrush(Color.FromArgb(120, 255, 250, 228))) g.FillEllipse(sun, mx - mr, my - mr, mr * 2, mr * 2);

        // hai lớp rặng thông: lớp xa nhạt + lớp gần đậm hơn, đỉnh có tuyết
        for (int layer = 0; layer < 2; layer++)
        {
            float baseY = height - (layer == 0 ? height * 0.06f : 0);
            float th = height * (layer == 0 ? 0.20f : 0.15f);
            var fill = layer == 0 ? Color.FromArgb(70, 96, 160, 124) : Color.FromArgb(110, 60, 128, 92);
            using var br = new SolidBrush(fill);
            using var snow = new SolidBrush(Color.FromArgb(layer == 0 ? 170 : 210, 255, 255, 255));
            float x = -rnd.Next(30);
            while (x < width + 40)
            {
                float h = th * (0.55f + (float)rnd.NextDouble() * 0.45f), w = h * 0.42f;
                g.FillPolygon(br, new[] { new PointF(x, baseY - h), new PointF(x + w, baseY + 2), new PointF(x - w, baseY + 2) });
                g.FillPolygon(snow, new[] { new PointF(x, baseY - h), new PointF(x + w * 0.22f, baseY - h * 0.78f), new PointF(x - w * 0.22f, baseY - h * 0.78f) });
                x += w * (0.9f + (float)rnd.NextDouble() * 0.7f);
            }
            using var ground = new SolidBrush(Color.FromArgb(layer == 0 ? 150 : 230, 250, 253, 255));
            g.FillRectangle(ground, 0, baseY, width, height - baseY + 1);
        }
    }
}
