using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace SFVaultBridge.Addin;

/// <summary>Ribbon icons drawn in code: flat, transparent background, readable on Inventor's dark and light ribbons.</summary>
internal static class Icons
{
    private sealed class PictureConverter : AxHost
    {
        private PictureConverter() : base("") { }
        public static stdole.IPictureDisp ToPicture(Image image) => (stdole.IPictureDisp)GetIPictureDispFromPicture(image);
    }

    private static readonly Color Ink = ColorTranslator.FromHtml("#37474F");
    private static readonly Color Paper = ColorTranslator.FromHtml("#F4F6F8");
    private static readonly Color Green = ColorTranslator.FromHtml("#2E9E44");
    private static readonly Color Red = ColorTranslator.FromHtml("#C62828");

    public static stdole.IPictureDisp ToPicture(Image image) => PictureConverter.ToPicture(image);

    public static (stdole.IPictureDisp Small, stdole.IPictureDisp Large) Pair(Func<int, Bitmap> draw) =>
        (ToPicture(draw(16)), ToPicture(draw(32)));

    private static Bitmap Canvas(int size, out Graphics g)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        g.Clear(Color.Transparent);
        return bmp;
    }

    public static Bitmap Dot(int size, Color fill)
    {
        var bmp = Canvas(size, out var g);
        using (g)
        {
            var rect = new RectangleF(1.5f, 1.5f, size - 3f, size - 3f);
            using (var b = new SolidBrush(fill)) g.FillEllipse(b, rect);
            using var pen = new Pen(Color.FromArgb(fill.R * 6 / 10, fill.G * 6 / 10, fill.B * 6 / 10), size >= 32 ? 1.5f : 1f);
            g.DrawEllipse(pen, rect);
        }
        return bmp;
    }

    /// <summary>Spreadsheet grid: light cells, dark grid lines, green header row.</summary>
    public static Bitmap Spreadsheet(int size)
    {
        var bmp = Canvas(size, out var g);
        using (g)
        {
            var m = size >= 32 ? 3f : 1.5f;
            var box = new RectangleF(m, m, size - 2 * m, size - 2 * m);
            var header = box.Height * (size >= 32 ? 0.25f : 0.3f);
            var line = size >= 32 ? 1.25f : 1f;
            using (var b = new SolidBrush(Paper)) g.FillRectangle(b, box);
            using (var b = new SolidBrush(Green)) g.FillRectangle(b, box.X, box.Y, box.Width, header);
            using var pen = new Pen(Ink, line);
            var cols = size >= 32 ? 3 : 2;
            var rows = size >= 32 ? 4 : 3;
            for (var c = 1; c < cols; c++)
            {
                var x = box.X + box.Width * c / cols;
                g.DrawLine(pen, x, box.Y, x, box.Bottom);
            }
            for (var r = 1; r < rows; r++)
            {
                var y = r == 1 ? box.Y + header : box.Y + header + (box.Height - header) * (r - 1) / (rows - 1);
                g.DrawLine(pen, box.X, y, box.Right, y);
            }
            g.DrawRectangle(pen, box.X, box.Y, box.Width, box.Height);
        }
        return bmp;
    }

    /// <summary>Page with a folded top-right corner and a red diagonal band (reading DRAFT on the 32 px icon).</summary>
    public static Bitmap DraftPage(int size)
    {
        var bmp = Canvas(size, out var g);
        using (g)
        {
            var mx = size >= 32 ? 5f : 2.5f;
            var my = size >= 32 ? 2.5f : 1.5f;
            var l = mx; var t = my; var r = size - mx; var b = size - my;
            var fold = (r - l) * 0.32f;
            var page = new[] { new PointF(l, t), new PointF(r - fold, t), new PointF(r, t + fold), new PointF(r, b), new PointF(l, b) };
            using (var br = new SolidBrush(Paper)) g.FillPolygon(br, page);

            using (var clip = new GraphicsPath())
            {
                clip.AddPolygon(page);
                var saved = g.Save();
                g.SetClip(clip);
                g.TranslateTransform(size / 2f, size * 0.58f);
                g.RotateTransform(-38f);
                var bandH = size >= 32 ? 9f : 5f;
                using (var br = new SolidBrush(Red)) g.FillRectangle(br, -size, -bandH / 2, size * 2f, bandH);
                if (size >= 32)
                {
                    using var font = new Font("Arial", 6.5f, FontStyle.Bold, GraphicsUnit.Pixel);
                    using var white = new SolidBrush(Color.White);
                    using var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                    g.DrawString("DRAFT", font, white, new RectangleF(-size / 2f, -bandH / 2, size, bandH), fmt);
                }
                g.Restore(saved);
            }

            using var pen = new Pen(Ink, size >= 32 ? 1.25f : 1f) { LineJoin = LineJoin.Round };
            g.DrawPolygon(pen, page);
            var foldTri = new[] { new PointF(r - fold, t), new PointF(r - fold, t + fold), new PointF(r, t + fold) };
            using (var br = new SolidBrush(Color.FromArgb(200, 176, 190, 197))) g.FillPolygon(br, foldTri);
            g.DrawPolygon(pen, foldTri);
        }
        return bmp;
    }
}
