using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

namespace OtzariaBridge;

/// <summary>מצביע וסמן טקסט: גודל, צבע/ערכה, איתור המצביע ב-Ctrl, שובל, הסתרה בזמן הקלדה ועובי סמן הטקסט.
/// הנתיבים יושבים תחת /sys/mouse/ כדי שיירשו את הרשאות הנעילה של מקטע "עכבר" (Kiosk.cs).
/// צבע מותאם: הגשר מצייר קובץ ‎.cur‎ של חץ בצבע הנבחר (ב-%LOCALAPPDATA%\OtzariaBridge\cursors) ומחיל אותו כמצביע הרגיל.
/// קובץ חדש בלבד — לא משנה קוד קיים מלבד שורת רישום אחת ב-Server.cs.</summary>
public static class CursorApi
{
    [DllImport("user32.dll", EntryPoint = "SystemParametersInfo")] private static extern bool SpiInt(uint a, uint p, ref int v, uint ini);
    [DllImport("user32.dll", EntryPoint = "SystemParametersInfo")] private static extern bool SpiBool(uint a, uint p, ref bool v, uint ini);
    [DllImport("user32.dll", EntryPoint = "SystemParametersInfo")] private static extern bool SpiSet(uint a, uint p, IntPtr v, uint ini);

    private const uint Persist = 0x01 | 0x02;          // SPIF_UPDATEINIFILE | SPIF_SENDCHANGE
    private const uint SPI_SETCURSORS = 0x0057;
    private const uint SPI_GETMOUSETRAILS = 0x005E, SPI_SETMOUSETRAILS = 0x005D;
    private const uint SPI_GETMOUSESONAR = 0x101C, SPI_SETMOUSESONAR = 0x101D;
    private const uint SPI_GETMOUSEVANISH = 0x1020, SPI_SETMOUSEVANISH = 0x1021;
    private const uint SPI_GETCARETWIDTH = 0x2006, SPI_SETCARETWIDTH = 0x2007;
    private const uint SPI_GETCURSORSIZE = 0x2028, SPI_SETCURSORSIZE = 0x2029; // לא מתועד, בשימוש "הגדרות" של Windows

    private const string CursorsKey = @"Control Panel\Cursors";
    private const string OurKey = @"Software\OtzariaBridge\Cursor";

    /// <summary>תפקידי המצביע ב-Control Panel\Cursors עם שם הקובץ הבסיסי בערכות הקלאסיות של Windows (_r = שחור, _i = הפוך)</summary>
    private static readonly (string Role, string File)[] Roles =
    {
        ("Arrow", "arrow"), ("Help", "help"), ("AppStarting", "wait"), ("Wait", "busy"),
        ("Crosshair", "cross"), ("IBeam", "beam"), ("NWPen", "pen"), ("No", "no"),
        ("SizeNS", "size4"), ("SizeWE", "size3"), ("SizeNWSE", "size1"), ("SizeNESW", "size2"),
        ("SizeAll", "move"), ("UpArrow", "up"),
    };

    public static void Register()
    {
        Server.Route("/sys/mouse/cursor/get", _ => Get());
        Server.Route("/sys/mouse/cursor/set", Set);
    }

    public static object Get()
    {
        int trails = 0, caret = 1;
        bool sonar = false, vanish = false;
        SpiInt(SPI_GETMOUSETRAILS, 0, ref trails, 0);
        SpiBool(SPI_GETMOUSESONAR, 0, ref sonar, 0);
        SpiBool(SPI_GETMOUSEVANISH, 0, ref vanish, 0);
        SpiInt(SPI_GETCARETWIDTH, 0, ref caret, 0);
        var (lvl, style, color) = Current();
        return new
        {
            size = lvl,                                      // 1-15 כמו ב"הגדרות" של Windows
            style,                                           // default | black | inverted | custom
            color,
            sonar,
            trails = trails > 1 ? trails : 0,                // 0 = כבוי, 2-7 אורך
            vanish,
            caretWidth = Math.Clamp(caret, 1, 20),
            blackAvailable = File.Exists(WinCursor("arrow_r.cur")),
            invertedAvailable = File.Exists(WinCursor("arrow_i.cur")),
        };
    }

    public static object Set(JsonElement b)
    {
        bool? sonar = b.BoolOpt("sonar");
        if (sonar.HasValue) SpiSet(SPI_SETMOUSESONAR, 0, new IntPtr(sonar.Value ? 1 : 0), Persist);

        bool? vanish = b.BoolOpt("vanish");
        if (vanish.HasValue) SpiSet(SPI_SETMOUSEVANISH, 0, new IntPtr(vanish.Value ? 1 : 0), Persist);

        double? trails = b.Num("trails");
        if (trails.HasValue)
        {
            int t = (int)Math.Round(trails.Value);
            SpiSet(SPI_SETMOUSETRAILS, (uint)(t < 2 ? 0 : Math.Min(t, 7)), IntPtr.Zero, Persist);
        }

        double? caret = b.Num("caretWidth");
        if (caret.HasValue) SpiSet(SPI_SETCARETWIDTH, 0, new IntPtr((int)Math.Clamp(Math.Round(caret.Value), 1, 20)), Persist);

        double? size = b.Num("size");
        string style = b.TryGetProperty("style", out var st) && st.ValueKind == JsonValueKind.String ? st.GetString() : null;
        string color = b.TryGetProperty("color", out var co) && co.ValueKind == JsonValueKind.String ? co.GetString() : null;

        if (size.HasValue || style != null || color != null)
        {
            var (curLvl, curStyle, curColor) = Current();
            int lvl = size.HasValue ? (int)Math.Clamp(Math.Round(size.Value), 1, 15) : curLvl;
            style = style is "default" or "black" or "inverted" or "custom" ? style : curStyle;
            color = NormColor(color ?? curColor);
            ApplyScheme(style, color, 32 + (lvl - 1) * 16);
        }
        return Get();
    }

    private static (int lvl, string style, string color) Current()
    {
        int size = 32;
        if (!SpiInt(SPI_GETCURSORSIZE, 0, ref size, 0) || size < 32)
        {
            try { using var k = Registry.CurrentUser.OpenSubKey(CursorsKey); size = Convert.ToInt32(k?.GetValue("CursorBaseSize", 32) ?? 32); }
            catch { size = 32; }
        }
        string style = "default", color = "#2F6FDE";
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(OurKey);
            style = k?.GetValue("Style") as string ?? style;
            color = k?.GetValue("Color") as string ?? color;
        }
        catch { }
        return (Math.Clamp((size - 32) / 16 + 1, 1, 15), style, color);
    }

    private static string WinCursor(string f) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Cursors", f);

    private static string NormColor(string c)
    {
        c = (c ?? "").Trim();
        if (!c.StartsWith("#")) c = "#" + c;
        return System.Text.RegularExpressions.Regex.IsMatch(c, "^#[0-9a-fA-F]{6}$") ? c.ToUpperInvariant() : "#2F6FDE";
    }

    private static void ApplyScheme(string style, string color, int px)
    {
        using (var k = Registry.CurrentUser.CreateSubKey(CursorsKey))
        {
            k.SetValue("CursorBaseSize", px, RegistryValueKind.DWord);
            foreach (var (role, file) in Roles)
            {
                string val = "";                                   // ריק = מצביע ברירת המחדל של Windows
                if (style == "black" || style == "inverted")
                {
                    var p = WinCursor(file + (style == "black" ? "_r.cur" : "_i.cur"));
                    if (File.Exists(p)) val = p;
                }
                k.SetValue(role, val, RegistryValueKind.ExpandString);
            }
            if (style == "custom") k.SetValue("Arrow", BuildArrow(color, px), RegistryValueKind.ExpandString);
            k.SetValue("", style == "default" ? "" : "Otzaria " + style, RegistryValueKind.String);
        }
        using (var o = Registry.CurrentUser.CreateSubKey(OurKey))
        {
            o.SetValue("Style", style);
            o.SetValue("Color", color);
        }
        SpiSet(SPI_SETCURSORSIZE, 0, new IntPtr(px), Persist);
        SpiSet(SPI_SETCURSORS, 0, IntPtr.Zero, Persist);
    }

    /// <summary>מצייר חץ בצבע הנבחר עם קו מתאר מנוגד ושומר כקובץ ‎.cur‎ (DIB של 32 ביט, נקודה חמה בקצה)</summary>
    private static string BuildArrow(string hex, int px)
    {
        px = Math.Clamp(px, 32, 256);
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OtzariaBridge", "cursors");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "arrow-" + hex.TrimStart('#').ToLowerInvariant() + "-" + px + ".cur");
        if (File.Exists(path)) return path;

        var fill = ColorTranslator.FromHtml(hex);
        double lum = (0.299 * fill.R + 0.587 * fill.G + 0.114 * fill.B) / 255;
        var edge = lum > 0.6 ? Color.Black : Color.White;

        float s = px / 32f, m = 1.2f * s;
        PointF P(float x, float y) => new(m + x * s, m + y * s);
        var poly = new[] { P(0, 0), P(0, 21), P(5, 16.5f), P(8.5f, 24.5f), P(12, 23), P(8.5f, 15.5f), P(15, 15.5f) };

        using var bmp = new Bitmap(px, px, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var shadow = new SolidBrush(Color.FromArgb(70, 0, 0, 0));
            g.TranslateTransform(0.8f * s, 1.2f * s); g.FillPolygon(shadow, poly); g.ResetTransform();
            using var br = new SolidBrush(fill);
            g.FillPolygon(br, poly);
            using var pen = new Pen(edge, Math.Max(1.4f, 1.3f * s)) { LineJoin = LineJoin.Round };
            g.DrawPolygon(pen, poly);
        }

        int maskStride = ((px + 31) / 32) * 4;
        int imgBytes = 40 + px * px * 4 + maskStride * px;
        using var fs = File.Create(path);
        using var w = new BinaryWriter(fs);
        w.Write((ushort)0); w.Write((ushort)2); w.Write((ushort)1);                        // ICONDIR: סוג 2 = סמן
        w.Write((byte)(px >= 256 ? 0 : px)); w.Write((byte)(px >= 256 ? 0 : px));
        w.Write((byte)0); w.Write((byte)0);
        ushort hot = (ushort)Math.Round(m); w.Write(hot); w.Write(hot);                    // נקודה חמה בקצה החץ
        w.Write((uint)imgBytes); w.Write((uint)22);
        w.Write(40); w.Write(px); w.Write(px * 2); w.Write((ushort)1); w.Write((ushort)32); // BITMAPINFOHEADER
        w.Write(0); w.Write(0); w.Write(0); w.Write(0); w.Write(0); w.Write(0);
        for (int y = px - 1; y >= 0; y--)
            for (int x = 0; x < px; x++)
            {
                var c = bmp.GetPixel(x, y);
                w.Write(c.B); w.Write(c.G); w.Write(c.R); w.Write(c.A);
            }
        w.Write(new byte[maskStride * px]);                                               // מסכת AND ריקה (השקיפות באלפא)
        return path;
    }
}
