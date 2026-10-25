using System.Drawing;

namespace OtzariaBridge;

/// <summary>סמל "אחת שאלתי" כתמונה (סבב B2) — נטען פעם אחת מאייקון היישום (assets/icon.ico, 256px) ומשמש את שומר המסך.</summary>
internal static class BrandArt
{
    private static Bitmap _big;
    public static Bitmap Big
    {
        get
        {
            if (_big != null) return _big;
            /* משוב 3.9: האייקון 256px הוגדל וסובב בשומר המסך ויצא מטושטש. עכשיו מקור 1024px מוטמע */
            try
            {
                using var st = typeof(BrandArt).Assembly.GetManifestResourceStream("brand-1024.png");
                _big = st != null ? new Bitmap(st) : new Icon(Overlay.AppIcon(), 256, 256).ToBitmap();
            }
            catch { _big = new Bitmap(1, 1); }
            return _big;
        }
    }

    private static readonly Dictionary<int, Bitmap> _sized = new();
    /// <summary>הסמל מוקטן מראש לגודל המדויק בפיקסלים (הקטנה איכותית אחת) — ציור 1:1 בלי שינוי גודל = חד</summary>
    public static Bitmap Sized(int px)
    {
        px = Math.Max(8, px);
        lock (_sized)
        {
            if (_sized.TryGetValue(px, out var b)) return b;
            b = new Bitmap(px, px, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(b))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                g.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
                using var ia = new System.Drawing.Imaging.ImageAttributes();
                ia.SetWrapMode(System.Drawing.Drawing2D.WrapMode.TileFlipXY);
                g.DrawImage(Big, new Rectangle(0, 0, px, px), 0, 0, Big.Width, Big.Height, GraphicsUnit.Pixel, ia);
            }
            if (_sized.Count > 8) _sized.Clear();
            _sized[px] = b;
            return b;
        }
    }
}
