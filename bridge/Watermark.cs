using System.Runtime.InteropServices;

namespace OtzariaBridge;

/// <summary>
/// סמל "אחת שאלתי" קבוע ושקוף-למחצה בפינה השמאלית התחתונה (בדומה לסימן של סינון), מעל כל החלונות,
/// בלי לקבל לחיצות. כבוי כברירת מחדל; הפעלה/כיבוי וגודל מתפריט המגש או /watermark/set.
/// config: watermark.enabled (false), watermark.size (48/72/224).
/// </summary>
internal static class Watermark
{
    private static Form _f;
    public static readonly int[] Sizes = { 112, 152, 192 };

    public static void Init()
    {
        Server.Route("/watermark/get", _ => State());
        Server.Route("/watermark/set", b =>
        {
            if (b.TryGetProperty("enabled", out var e) && e.ValueKind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False)
                Config.Set("watermark.enabled", e.GetBoolean());
            if (b.TryGetProperty("size", out var s) && s.TryGetInt32(out int n) && Sizes.Contains(n)) Config.Set("watermark.size", n);
            if (b.TryGetProperty("corner", out var c) && c.ValueKind == System.Text.Json.JsonValueKind.String && Corners.Contains(c.GetString())) Config.Set("watermark.corner", c.GetString());
            Config.Save();
            Apply();
            return State();
        });
        Apply();
    }

    private static object State() => new { enabled = Config.Get("watermark.enabled", false), size = Config.Get("watermark.size", 112), sizes = Sizes, corner = Config.Get("watermark.corner", "bl") };

    public static void Toggle() { Config.Set("watermark.enabled", !Config.Get("watermark.enabled", false)); Config.Save(); Apply(); }
    public static readonly string[] Corners = { "tl", "tr", "bl", "br" };
    public static void SetCorner(string c) { if (!Corners.Contains(c)) return; Config.Set("watermark.corner", c); Config.Save(); Apply(); }
    public static void SetSize(int n) { Config.Set("watermark.size", n); Config.Save(); Apply(); }

    public static void Apply() => Tray.Invoke(() =>
    {
        _f?.Close(); _f = null;
        if (!Config.Get("watermark.enabled", false)) return;
        int size = Config.Get("watermark.size", 112);
        if (size < 112) { size = 112; Config.Set("watermark.size", 112); Config.Save(); }   // המדרגות: 112/152/192
        if (size == 168 || size == 224) { size = size == 168 ? 152 : 192; Config.Set("watermark.size", size); Config.Save(); }   // הקטנה קלה של בינוני/גדול
        string corner = Config.Get("watermark.corner", "bl");
        /* משוב 3.9: TransparencyKey (מג'נטה) השאיר הילה ורודה ומטושטשת סביב הסמל המוחלק.
           עכשיו: הקטנה איכותית מהאייקון הגדול + חלון שכבתי עם אלפא לכל פיקסל (UpdateLayeredWindow) */
        Bitmap bmp;
        try { bmp = new Bitmap(BrandArt.Sized(size)); }
        catch { return; }
        var wa = Screen.PrimaryScreen!.WorkingArea;
        _f = new ClickThrough { FormBorderStyle = FormBorderStyle.None, ShowInTaskbar = false, TopMost = true,
            StartPosition = FormStartPosition.Manual, Size = new Size(size, size),
            Location = new Point(corner.EndsWith("r") ? wa.Right - size - 12 : wa.Left + 12, corner.StartsWith("t") ? wa.Top + 12 : wa.Bottom - size - 12) };
        _f.Show();
        SetAlphaBitmap(_f, bmp, 110);
        bmp.Dispose();
    });

    [StructLayout(LayoutKind.Sequential)] private struct PT { public int x, y; }
    [StructLayout(LayoutKind.Sequential)] private struct SZ { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] private struct BLEND { public byte op, flags, alpha, fmt; }
    [DllImport("user32.dll")] private static extern bool UpdateLayeredWindow(IntPtr h, IntPtr dst, ref PT pos, ref SZ sz, IntPtr src, ref PT srcPos, int key, ref BLEND bl, int flags);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr o);

    private static void SetAlphaBitmap(Form f, Bitmap bmp, byte opacity)
    {
        IntPtr screen = GetDC(IntPtr.Zero), mem = CreateCompatibleDC(screen), hb = IntPtr.Zero, old = IntPtr.Zero;
        try
        {
            hb = bmp.GetHbitmap(Color.FromArgb(0));
            old = SelectObject(mem, hb);
            var sz = new SZ { cx = bmp.Width, cy = bmp.Height };
            var pos = new PT { x = f.Left, y = f.Top };
            var zero = new PT();
            var bl = new BLEND { op = 0, flags = 0, alpha = opacity, fmt = 1 /* AC_SRC_ALPHA */ };
            UpdateLayeredWindow(f.Handle, screen, ref pos, ref sz, mem, ref zero, 0, ref bl, 2 /* ULW_ALPHA */);
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, screen);
            if (hb != IntPtr.Zero) { SelectObject(mem, old); DeleteObject(hb); }
            DeleteDC(mem);
        }
    }

    private sealed class ClickThrough : Form
    {
        protected override bool ShowWithoutActivation => true;
        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x20 | 0x80000 | 0x80 | 0x08000000; // TRANSPARENT | LAYERED | TOOLWINDOW | NOACTIVATE
                return cp;
            }
        }
    }
}
