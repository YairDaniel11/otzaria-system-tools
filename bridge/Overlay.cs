using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Runtime.InteropServices;

namespace OtzariaBridge;

/// <summary>
/// חלונות הנעילה (חוט המגש בלבד):
/// שעון צף — שעה, תאריך עברי ולועזי וסוללה, כי שורת המשימות מוסתרת בנעילה. לחיצה עליו מחזירה לאוצריא.
/// מסך מנוחה — כשאוצריא ממוזערת: מסך מלא עם סמל אוצריא, שעה ותאריך, ולחיצה בכל מקום מחזירה לאוצריא.
/// </summary>
public static class Overlay
{
    private static ClockBar _clock;
    // בעיה #14 סעיף 3: חלון "RestScreen-like" אחד לכל מסך (Screen.AllScreens), כולם מוצגים/מוסתרים יחד
    private static readonly List<RestScreen> _rest = new();

    public static bool RestVisible => _rest.Count > 0 && _rest[0].Visible;
    /// <summary>מסך המנוחה מוצג בגלל חוסר פעילות (ולא בגלל מזעור) — לא להסתיר אותו כשאוצריא לא ממוזערת</summary>
    public static bool RestIdle => _rest.Count > 0 && _rest[0] is { Visible: true, Idle: true };

    public static string HebrewDate(DateTime d)
    {
        try
        {
            var he = new CultureInfo("he-IL");
            he.DateTimeFormat.Calendar = new HebrewCalendar();
            return d.ToString("dddd, d MMMM yyyy", he);
        }
        catch { return d.ToString("dddd d/M/yyyy"); }
    }

    public static void ShowClock() => Tray.Invoke(() =>
    {
        if (!Config.Get("kiosk.clock", true)) { HideClock(); return; }
        _clock ??= new ClockBar();
        _clock.Place();
        if (!_clock.Visible) _clock.Show();
    });

    public static void HideClock() => Tray.Invoke(() => { _clock?.Hide(); });

    public static void ShowRest(IntPtr otzaria, bool idle = false) => Tray.Invoke(() =>
    {
        if (EmergencyForm.IsOpen) return; // חלון ביטול חירום פתוח - לא מכסים אותו
        var screens = Screen.AllScreens;
        // התאמה למספר המסכים (גם אם השתנה: חיבור/ניתוק מסך בזמן ריצה)
        while (_rest.Count < screens.Length) _rest.Add(new RestScreen());
        while (_rest.Count > screens.Length)
        {
            var extra = _rest[^1];
            _rest.RemoveAt(_rest.Count - 1);
            extra.Dispose();
        }
        bool wasVisible = RestVisible;
        foreach (var r in _rest)
        {
            r.Target = otzaria;
            if (!wasVisible) r.Idle = idle;
        }
        if (!wasVisible)
        {
            for (int i = 0; i < _rest.Count; i++)
            {
                _rest[i].Bounds = screens[i].Bounds;
                _rest[i].Show();
            }
            if (_rest.Count > 0) _rest[0].Activate();
        }
    });

    public static void HideRest() => Tray.Invoke(() => { foreach (var r in _rest) if (r.Visible) r.Hide(); });

    private static SideBar _side;

    public static void ShowSideBar() => Tray.Invoke(() =>
    {
        if (!Config.Get("kiosk.sideBar", true)) { HideSideBar(); return; }
        _side ??= new SideBar();
        _side.Rebuild();
    });

    public static void HideSideBar() => Tray.Invoke(() => { _side?.Hide(); });

    private static readonly List<BlackCover> _covers = new();

    /// <summary>
    /// כיסוי שחור על כל מסך שאוצריא אינה עליו (Screen.AllScreens). בלי זה, במחשב עם כמה מסכים המסך השני נשאר שולחן
    /// עבודה חשוף. נקרא בכל סבב של השומר; מתאים את מספר הכיסויים גם כשמסך מתחבר/מתנתק. כבוי: kiosk.coverOtherScreens=false.
    /// </summary>
    public static void SyncCovers(IntPtr otzaria) => Tray.Invoke(() =>
    {
        try
        {
            var screens = Screen.AllScreens;
            // חלון ביטול חירום פתוח / סרגל מנוחה על כל המסכים: לא מכסים (כדי שהחירום תמיד נגיש)
            if (screens.Length < 2 || !Config.Get("kiosk.coverOtherScreens", true) || EmergencyForm.IsOpen || RestVisible) { HideCovers(); return; }
            Screen main = otzaria != IntPtr.Zero ? Screen.FromHandle(otzaria) : Screen.PrimaryScreen;
            var others = screens.Where(sc => sc.DeviceName != main.DeviceName).ToArray();
            while (_covers.Count < others.Length) _covers.Add(new BlackCover());
            while (_covers.Count > others.Length) { var x = _covers[^1]; _covers.RemoveAt(_covers.Count - 1); x.Dispose(); }
            for (int i = 0; i < others.Length; i++)
            {
                var c = _covers[i];
                if (c.Bounds != others[i].Bounds) c.Bounds = others[i].Bounds;
                if (!c.Visible) c.Show();
            }
        }
        catch (Exception ex) { Config.Log("covers: " + ex.Message); }
    });

    public static void HideCovers() { foreach (var c in _covers) if (c.Visible) c.Hide(); }

    public static void HideAll() { HideRest(); HideClock(); HideSideBar(); HideCovers(); }

    private static LockCountdown _countdown;

    /// <summary>אזהרת ספירה-לאחור לפני נעילה מתוזמנת (5 דקות ודקה לפני)</summary>
    public static void ShowCountdown(DateTime target) => Tray.Invoke(() =>
    {
        _countdown ??= new LockCountdown();
        _countdown.SetTarget(target);
        if (!_countdown.Visible) { _countdown.Show(); _countdown.Activate(); }
    });

    public static void HideCountdown() => Tray.Invoke(() => { if (_countdown is { Visible: true }) _countdown.Hide(); });

    private static TempUnlockBanner _tempBanner;
    private static DateTimeOffset _tempDismissedFor = DateTimeOffset.MinValue;

    /// <summary>
    /// באנר "המחשב פתוח זמנית" עם ספירה לאחור עד שהנעילה חוזרת. נקרא בכל טיק (Kiosk.TempTick) כל עוד הפתיחה פעילה.
    /// אם המשתמש הסתיר אותו — לא מוצג שוב עד פתיחה זמנית חדשה (זמן סיום אחר). ההסתרה אינה מבטלת את הפתיחה;
    /// הזמן שנותר ממשיך להופיע בהגדרות מערכת ← נעילה ובסייר הקבצים.
    /// </summary>
    public static void ShowTempUnlock(DateTimeOffset until) => Tray.Invoke(() =>
    {
        if (until == _tempDismissedFor) return;
        _tempBanner ??= new TempUnlockBanner(() => _tempDismissedFor = _tempBanner.Until);
        _tempBanner.SetUntil(until);
        if (!_tempBanner.Visible) _tempBanner.Show(); // בלי Activate: לא גונב פוקוס ממה שהמשתמש עושה
    });

    public static void HideTempUnlock()
    {
        if (_tempBanner == null) return; // נקרא כל 2 שניות; בלי באנר אין מה להעביר לחוט הממשק
        Tray.Invoke(() => { if (_tempBanner is { Visible: true }) _tempBanner.Hide(); });
    }

    [DllImport("user32.dll")] internal static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(IntPtr h);

    internal static void BringOtzaria(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            string path = Kiosk.OtzariaPath();
            string name = string.IsNullOrEmpty(path) ? "otzaria" : Path.GetFileNameWithoutExtension(path);
            hwnd = Process.GetProcessesByName(name).Select(p => p.MainWindowHandle).FirstOrDefault(h => h != IntPtr.Zero);
        }
        if (hwnd == IntPtr.Zero) return;
        Kiosk.BringToFront(hwnd);
    }

    internal static GraphicsPath Round(Rectangle r, int radius)
    {
        var p = new GraphicsPath();
        int d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    internal static Icon AppIcon()
    {
        try { return Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? SystemIcons.Application; }
        catch { return SystemIcons.Application; }
    }
}

/// <summary>שעון צף קטן, תמיד מעל, בלי לקחת פוקוס מאוצריא</summary>
internal sealed class ClockBar : Form
{
    private readonly System.Windows.Forms.Timer _tick = new() { Interval = 1000 };
    private string _time = "", _date = "", _heb = "", _bat = "";

    public ClockBar()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(28, 32, 38);
        ForeColor = Color.White;
        Opacity = 0.92;
        DoubleBuffered = true;
        Size = new Size(ScaleX(250), ScaleX(64));
        Cursor = Cursors.Hand;
        RightToLeft = RightToLeft.Yes;
        _tick.Tick += (_, _) => Refresh_();
        // 7 לחיצות מהירות (עד 3 שניות בין לחיצה ללחיצה) פותחות את חלון קוד השחזור - דרך מילוט שלא תלויה
        // במקלדת/ב-hook (בחלק מהמחשבים F12 לא מגיע כמות שהוא). לחיצה בודדת ממשיכה להחזיר לאוצריא
        int clicks = 0; var lastClick = DateTime.MinValue;
        Click += (_, _) =>
        {
            var now = DateTime.UtcNow;
            clicks = (now - lastClick).TotalSeconds <= 3 ? clicks + 1 : 1;
            lastClick = now;
            if (clicks >= 7) { clicks = 0; EmergencyForm.Open(); return; }
            Overlay.BringOtzaria(IntPtr.Zero);
        };
        Refresh_();
        _tick.Start();
    }

    private int ScaleX(int v) => (int)Math.Round(v * DeviceDpi / 96.0);

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x08000000 /* WS_EX_NOACTIVATE */ | 0x00000080 /* WS_EX_TOOLWINDOW */ | 0x00000008 /* WS_EX_TOPMOST */;
            return cp;
        }
    }

    public void Place()
    {
        var wa = Screen.PrimaryScreen!.Bounds;
        int m = ScaleX(12);
        string pos = Config.Get("kiosk.clockPosition", "bottom-left");
        int x = pos.EndsWith("right") ? wa.Right - Width - m : wa.Left + m;
        int y = pos.StartsWith("top") ? wa.Top + m : wa.Bottom - Height - m;
        Location = new Point(x, y);
        Region = new Region(Overlay.Round(new Rectangle(0, 0, Width, Height), ScaleX(12)));
    }

    private void Refresh_()
    {
        var now = DateTime.Now;
        _time = now.ToString("HH:mm");
        _date = now.ToString("d/M/yyyy");
        _heb = Overlay.HebrewDate(now);
        _bat = "";
        try
        {
            var ps = SystemInformation.PowerStatus;
            if (ps.BatteryChargeStatus != BatteryChargeStatus.NoSystemBattery && ps.BatteryChargeStatus != BatteryChargeStatus.Unknown)
                _bat = "🔋 " + (int)Math.Round(ps.BatteryLifePercent * 100) + "%" + (ps.PowerLineStatus == PowerLineStatus.Online ? " ⚡" : "");
        }
        catch { }
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var big = new Font("Segoe UI Semibold", 20f, GraphicsUnit.Point);
        using var small = new Font("Segoe UI", 9f, GraphicsUnit.Point);
        using var dim = new SolidBrush(Color.FromArgb(200, 220, 225, 232));
        var r = ClientRectangle;
        int pad = ScaleX(12);
        var timeSize = g.MeasureString(_time, big);
        g.DrawString(_time, big, Brushes.White, r.Right - pad - timeSize.Width, (r.Height - timeSize.Height) / 2f);
        var sf = new StringFormat(StringFormatFlags.DirectionRightToLeft) { Alignment = StringAlignment.Near };
        var textRect = new RectangleF(pad, ScaleX(8), r.Width - pad * 2 - timeSize.Width - ScaleX(6), ScaleX(22));
        g.DrawString(_heb, small, Brushes.White, textRect, sf);
        textRect.Y += ScaleX(24);
        g.DrawString(_date + (_bat.Length > 0 ? "   " + _bat : ""), small, dim, textRect, sf);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _tick.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>
/// מסך מנוחה כשאוצריא ממוזערת: "לימוד ללא הפרעות", פסוק, שעה ותאריך עברי, ונר הנצחה מרצד עם נוסח ההנצחה.
/// הכול נע לאט (גם הבלוק המרכזי) כדי לא לצרוב את המסך. לחיצה או מקש מחזירים לאוצריא.
/// </summary>
internal sealed class RestScreen : Form
{
    private readonly System.Windows.Forms.Timer _tick = new() { Interval = 40 };
    private readonly Icon _icon = Overlay.AppIcon();
    private readonly Bitmap _logo;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Random _rnd = new();
    private readonly Particle[] _parts = new Particle[70];
    private const string Letters = "אבגדהוזחטיכלמנסעפצקרשת";
    private Rectangle _btn;
    public IntPtr Target;
    public bool Idle;
    private Point? _firstMove;

    private struct Particle { public float X, Y, Size, Speed, Phase; public char Ch; }

    public RestScreen()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        DoubleBuffered = true;
        KeyPreview = true;
        BackColor = Color.FromArgb(14, 20, 30);
        Cursor = Cursors.Hand;
        try { _logo = new Icon(_icon, 256, 256).ToBitmap(); } catch { _logo = SystemIcons.Application.ToBitmap(); }
        for (int i = 0; i < _parts.Length; i++) _parts[i] = NewParticle(true);
        _tick.Tick += (_, _) => Invalidate();
        MouseClick += (_, _) => Back();
        // שומר מסך מחוסר פעילות נסגר גם בהזזת עכבר (מעבר לסף קטן, כדי שרעד לא יסגור)
        MouseMove += (_, e) =>
        {
            if (!Idle || Target == IntPtr.Zero) return;
            if (_firstMove == null) { _firstMove = e.Location; return; }
            if (Math.Abs(e.X - _firstMove.Value.X) + Math.Abs(e.Y - _firstMove.Value.Y) > 24) Back();
        };
        KeyDown += (_, _) => Back();
    }

    private Particle NewParticle(bool anywhere) => new()
    {
        X = (float)_rnd.NextDouble(),
        Y = anywhere ? (float)_rnd.NextDouble() : 1.05f,
        Size = 0.6f + (float)_rnd.NextDouble() * 1.4f,
        Speed = 0.004f + (float)_rnd.NextDouble() * 0.012f,
        Phase = (float)(_rnd.NextDouble() * Math.PI * 2),
        Ch = _rnd.Next(5) == 0 ? Letters[_rnd.Next(Letters.Length)] : '\0',
    };

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        _firstMove = null;
        if (Visible) _tick.Start(); else _tick.Stop();
    }

    private void Back()
    {
        // לחיצה/מקש בכל אחד מהמסכים מחזירים לאוצריא ומסתירים את כל מסכי המנוחה יחד (ריבוי מסכים)
        Overlay.HideRest();
        Overlay.BringOtzaria(Target);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var r = ClientRectangle;
        float t = (float)_clock.Elapsed.TotalSeconds;
        float s = DeviceDpi / 96f;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

        // רקע: לילה עמוק וחם, משתנה לאט מאוד
        float k = (float)(Math.Sin(t / 22.0) * 0.5 + 0.5);
        Color c1 = Mix(Color.FromArgb(22, 26, 44), Color.FromArgb(30, 24, 40), k);
        Color c2 = Mix(Color.FromArgb(8, 9, 16), Color.FromArgb(12, 10, 14), k);
        using (var bg = new LinearGradientBrush(r, c1, c2, 90f)) g.FillRectangle(bg, r);

        // כוכבים עדינים בלבד (בלי אותיות), מנצנצים ועולים לאט מאוד
        for (int i = 0; i < _parts.Length; i++)
        {
            ref var p = ref _parts[i];
            p.Y -= p.Speed * 0.012f;
            if (p.Y < -0.05f) { p = NewParticle(false); p.Ch = '\0'; }
            float px = p.X * r.Width, py = p.Y * r.Height;
            int a = (int)(25 + 55 * (Math.Sin(t * 0.6f + p.Phase) * 0.5 + 0.5));
            float d = p.Size * 2.2f * s;
            using var sb = new SolidBrush(Color.FromArgb(a, 225, 215, 190));
            g.FillEllipse(sb, px, py, d, d);
        }

        // כל התוכן נע לאט מאוד (מניעת צריבה)
        float dx = (float)Math.Sin(t * 0.04) * r.Width * 0.04f;
        float dy = (float)Math.Sin(t * 0.07) * r.Height * 0.025f;
        float cx = r.Width / 2f + dx;
        var rtl = new StringFormat(StringFormatFlags.DirectionRightToLeft) { Alignment = StringAlignment.Center };
        var center = new StringFormat { Alignment = StringAlignment.Center };
        Color gold = Color.FromArgb(232, 200, 140);
        using var goldB = new SolidBrush(gold);
        using var dim = new SolidBrush(Color.FromArgb(170, 220, 214, 200));
        using var headF = new Font("David", 40f, FontStyle.Bold, GraphicsUnit.Point);
        using var verseF = new Font("David", 22f, GraphicsUnit.Point);
        using var timeF = new Font("Segoe UI Light", 64f, GraphicsUnit.Point);
        using var dateF = new Font("David", 20f, GraphicsUnit.Point);
        using var hintF = new Font("Segoe UI", 11f, GraphicsUnit.Point);
        // זמני היום בעמודה בצד (kiosk.restZmanimSide: right/left), והקדשה אישית אופציונלית (kiosk.dedicationText)
        // בעמודה בצד הנגדי - כדי ששתיהן לא יתנגשו בשעה במרכז. העמודות לא זזות עם dx (רק dy), והתוכן
        // במרכז מוגבל לרוחב שנשאר בין שתי השוליים.
        var zmItems = CachedZmanim();
        bool zmLeft = Config.Get("kiosk.restZmanimSide", "right") == "left";
        string dedication = Config.Get("kiosk.dedicationText", "");
        bool hasDedication = !string.IsNullOrWhiteSpace(dedication);
        bool hasSideColumn = zmItems.Count > 0 || hasDedication;
        float colW = hasSideColumn ? Math.Min(340 * s, r.Width * 0.24f) : 0;
        float colMargin = 28 * s;
        float W = hasSideColumn ? Math.Max(r.Width * 0.4f, r.Width - 2 * (colW + colMargin * 2) - 2 * Math.Abs(dx)) : r.Width;
        RectangleF Line(float y, float h) => new(cx - W / 2f, y, W, h);

        float y = r.Height * 0.09f + dy;
        g.DrawString(Lang.T("לימוד ללא הפרעות", "Learning without distractions"), headF, goldB, Line(y, 70 * s), rtl);
        y += 70 * s;
        // קו מעוטר דק
        using (var pen = new Pen(Color.FromArgb(120, gold), 1.2f * s))
        {
            g.DrawLine(pen, cx - 150 * s, y, cx - 12 * s, y);
            g.DrawLine(pen, cx + 12 * s, y, cx + 150 * s, y);
            g.FillEllipse(goldB, cx - 4 * s, y - 4 * s, 8 * s, 8 * s);
        }
        y += 14 * s;
        g.DrawString("וְהָגִיתָ בּוֹ יוֹמָם וָלַיְלָה", verseF, dim, Line(y, 40 * s), rtl);
        y += 58 * s;

        var now = DateTime.Now;
        g.DrawString(now.ToString("HH:mm"), timeF, Brushes.White, Line(y, 110 * s), center);
        y += 108 * s;
        // שם מחשב/בית כנסת (קיוסק.venueName) - אופציונלי, ישירות מתחת לשעון. ריק = לא תופס מקום בכלל
        string venue = Config.Get("kiosk.venueName", "");
        if (!string.IsNullOrWhiteSpace(venue))
        {
            using (var venueF = new Font(UiFamily, 20f, GraphicsUnit.Point))
                g.DrawString(venue, venueF, dim, Line(y, 36 * s), center);
            y += 36 * s;
        }
        // #2: התאריך הלועזי באותה שורה עם העברי (לא בשורה נפרדת שנופלת מתחת להקדשה)
        g.DrawString(Overlay.HebrewDate(now) + "   ·   " + now.ToString("d/M/yyyy"), dateF, Brushes.White, Line(y, 40 * s), rtl);
        if (Target == IntPtr.Zero && Idle)
        {
            y += 34 * s;
            g.DrawString(Lang.T("אוצריא לא נמצאה במחשב. לביטול הנעילה: Ctrl+Alt+Shift+F12", "Otzaria was not found. To unlock: Ctrl+Alt+Shift+F12"), hintF, dim, Line(y, 30 * s), rtl);
        }
        if (zmItems.Count > 0)
            DrawZmanimColumn(g, zmItems, zmLeft ? colMargin : r.Width - colMargin - colW,
                r.Height * 0.08f + dy, colW, r.Height - 70 * s - (r.Height * 0.08f + dy), s, gold);
        if (hasDedication)
            DrawDedicationColumn(g, dedication, zmLeft ? r.Width - colMargin - colW : colMargin,
                r.Height * 0.08f + dy, colW, r.Height - 70 * s - (r.Height * 0.08f + dy), s, gold, t);

        // רמז קטן בתחתית; אין צורך בכפתור — כל לחיצה חוזרת
        _btn = Rectangle.Empty;
        g.DrawString(Lang.T("לחיצה או מקש כלשהו — חזרה לאוצריא", "Click or press any key to return to Otzaria"), hintF, dim,
            Line(r.Height - 40 * s, 30 * s), rtl);
    }

    /// <summary>מפרק את שורות הזמנים ("א · ב\nג") לפריטים — פריט אחד לכל שורה בעמודה</summary>
    // OnPaint רץ כ-25 פעמים בשנייה; חישוב הזמנים (JSON, KosherJava, קריאות config) בכל פריים היה כבד, וחריגה שם
    // הפילה את כל הציור (מסך לבן עם X אדום). מחשבים פעם ב-30 שניות, וכשל מחזיר רשימה ריקה במקום לקרוס
    private static List<string> _zmCache = new();
    private static DateTime _zmAt = DateTime.MinValue;

    private static List<string> CachedZmanim()
    {
        if ((DateTime.UtcNow - _zmAt).TotalSeconds >= 30)
        {
            try { _zmCache = ZmanimItems(Zmanim.Sections(DateTime.Now)); }
            catch (Exception ex) { Config.Log("zmanim: " + ex.Message); _zmCache = new List<string>(); }
            _zmAt = DateTime.UtcNow;
        }
        return _zmCache;
    }

    internal static List<string> ZmanimItems(string[] sections)
    {
        var items = new List<string>();
        foreach (var sec in sections)
        {
            if (items.Count > 0) items.Add(""); // רווח בין קבוצות
            foreach (var ln in sec.Split('\n'))
                foreach (var part in ln.Split('·'))
                {
                    string x = part.Trim();
                    if (x.Length > 0) items.Add(x);
                }
        }
        return items;
    }

    /// <summary>
    /// גופן ברירת המחדל של ממשק Windows (Segoe UI בווינדוס 10/11, בכל שפה) - קיים ומכסה עברית אצל כולם, בניגוד
    /// ל-"David"/"Segoe UI Semibold" שאינם מובטחים בכל התקנה (ובהיעדרם הופיעו ריבועים ליד הטקסט)
    /// </summary>
    private static readonly string UiFamily = ResolveUiFamily();

    private static string ResolveUiFamily()
    {
        try { using var f = SystemFonts.MessageBoxFont; return f.FontFamily.Name; }
        catch { return "Segoe UI"; }
    }

    /// <summary>עמודת זמנים עם רקע עדין; הגופן קטן עד שהכול נכנס בגובה הזמין (לעולם לא גולש למטה)</summary>
    private static void DrawZmanimColumn(Graphics g, List<string> items, float x, float y, float w, float maxH, float s, Color gold)
    {
        float size = 18f; float lineH; int blank = items.Count(i => i.Length == 0); // #1: גופן ברור יותר
        Font f;
        while (true)
        {
            // גופן ברירת המחדל של Windows (ר' UiFamily) - לא שמות family שאינם מובטחים בכל התקנה
            f = new Font(UiFamily, size, FontStyle.Bold, GraphicsUnit.Point);
            lineH = f.GetHeight(g) * 1.25f;
            float need = 24 * s + lineH * (items.Count - blank) + lineH * 0.5f * blank;
            if (need <= maxH || size <= 11f) { maxH = Math.Min(maxH, need); break; }
            f.Dispose(); size -= 1f;
        }
        using (f)
        using (var bg = new SolidBrush(Color.FromArgb(70, 10, 12, 22)))
        using (var border = new Pen(Color.FromArgb(70, gold), 1f * s))
        using (var path = Overlay.Round(new Rectangle((int)x, (int)y, (int)w, (int)maxH), (int)(14 * s)))
        using (var goldB = new SolidBrush(gold))
        {
            g.FillPath(bg, path);
            g.DrawPath(border, path);
            var fmt = new StringFormat(StringFormatFlags.DirectionRightToLeft) { Alignment = StringAlignment.Near, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.DirectionRightToLeft | StringFormatFlags.NoWrap };
            float yy = y + 12 * s, bottom = y + maxH - 10 * s;
            foreach (var it in items)
            {
                if (it.Length == 0) { yy += lineH * 0.5f; continue; }
                if (yy + lineH > bottom + 1) break;
                g.DrawString(it, f, goldB, new RectangleF(x + 14 * s, yy, w - 28 * s, lineH), fmt);
                yy += lineH;
            }
        }
    }

    /// <summary>
    /// עמודת הקדשה אישית (אופציונלי, kiosk.dedicationText) + נר מרצד, בעמודה בצד הנגדי לזמני היום -
    /// באותה מסגרת כמו עמודת הזמנים (רקע/מסגרת זהים, Overlay.Round) כדי שהשתיים ייראו כזוג סימטרי.
    /// שלא כמו ההקדשה הקבועה שהוסרה בעבר, הטקסט כאן תמיד מגיע מהגדרות המנהל - אין ברירת מחדל.
    /// </summary>
    private static void DrawDedicationColumn(Graphics g, string text, float x, float y, float w, float maxH, float s, Color gold, float t)
    {
        using var bg = new SolidBrush(Color.FromArgb(70, 10, 12, 22));
        using var border = new Pen(Color.FromArgb(70, gold), 1f * s);
        using var path = Overlay.Round(new Rectangle((int)x, (int)y, (int)w, (int)maxH), (int)(14 * s));
        g.FillPath(bg, path);
        g.DrawPath(border, path);

        var center = new StringFormat(StringFormatFlags.DirectionRightToLeft) { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Near };
        using var dim = new SolidBrush(Color.FromArgb(205, 225, 218, 200));
        float textTop = y + 16 * s, textH = maxH * 0.5f;
        using (var dedF = new Font(UiFamily, 16f, GraphicsUnit.Point))
            g.DrawString(text, dedF, dim, new RectangleF(x + 12 * s, textTop, w - 24 * s, textH), center);

        // נר מרצד בחצי התחתון של העמודה - נדנוד איטי (כל הלהבה) + ריצוד מהיר (גודל/שקיפות), בנפרד
        float cx = x + w / 2f;
        float candleW = Math.Min(24f, w * 0.16f) * s;
        float candleH = Math.Min(90f * s, maxH * 0.32f);
        float baseY = y + maxH - 14 * s;
        float bodyTop = baseY - candleH;

        float flick = (float)(Math.Sin(t * 9.1) * 0.5 + Math.Sin(t * 13.7 + 1.3) * 0.3 + Math.Sin(t * 5.3 + 2.1) * 0.2);
        float sway = (float)Math.Sin(t * 0.8) * 2.5f * s;
        float flameH = (28f + flick * 6f) * s;
        float flameW = (13f + flick * 2.5f) * s;
        float flameX = cx + sway;
        float flameY = bodyTop - flameH + 4 * s;

        int haloA = Math.Max(0, (int)(55 + flick * 20));
        using (var halo = new SolidBrush(Color.FromArgb(haloA, gold)))
            g.FillEllipse(halo, flameX - flameW * 1.8f, flameY - flameH * 0.6f, flameW * 3.6f, flameH * 2.2f);

        using (var wickP = new Pen(Color.FromArgb(200, 60, 45, 30), 1.4f * s))
            g.DrawLine(wickP, cx, bodyTop, flameX, flameY + flameH * 0.7f);

        // PathGradientBrush חייב להיבנות מ-GraphicsPath שכבר יש בו נקודות - בניה מנתיב ריק זורקת
        // חריגה (GDI+ generic error) שמפילה את כל OnPaint ומשאירה את מסך המנוחה לבן עם X אדום
        using var flamePath = new GraphicsPath();
        flamePath.AddEllipse(flameX - flameW / 2f, flameY, flameW, flameH);
        using (var flameB = new PathGradientBrush(flamePath))
        {
            flameB.CenterColor = Color.FromArgb(255, 255, 244, 214);
            flameB.SurroundColors = new[] { Color.FromArgb(0, gold.R, gold.G, gold.B) };
            g.FillPath(flameB, flamePath);
        }
        using (var flameCore = new SolidBrush(Color.FromArgb(230, 255, 170, 60)))
            g.FillEllipse(flameCore, flameX - flameW * 0.22f, flameY + flameH * 0.3f, flameW * 0.44f, flameH * 0.5f);

        using (var bodyB = new SolidBrush(Color.FromArgb(235, 232, 220, 195)))
            g.FillRectangle(bodyB, cx - candleW / 2f, bodyTop, candleW, candleH);
        using (var topB = new SolidBrush(Color.FromArgb(235, 240, 230, 205)))
            g.FillEllipse(topB, cx - candleW / 2f, bodyTop - 4 * s, candleW, 8 * s);
        using (var standB = new SolidBrush(Color.FromArgb(220, 180, 150, 90)))
            g.FillRectangle(standB, cx - candleW * 0.7f, baseY, candleW * 1.4f, 10 * s);
    }

    private static Color Mix(Color a, Color b, float k) =>
        Color.FromArgb((int)(a.R + (b.R - a.R) * k), (int)(a.G + (b.G - a.G) * k), (int)(a.B + (b.B - a.B) * k));

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _tick.Dispose(); _icon.Dispose(); _logo.Dispose(); }
        base.Dispose(disposing);
    }
}

/// <summary>
/// אזהרת ספירה-לאחור לפני נעילה מתוזמנת: פס לרוחב המסך בשליש התחתון (לא חלון-דיאלוג מרובע),
/// כדי שיהיה בולט בלי לחסום את כל המסך. מוצגת 5 דקות ודקה לפני תחילת החלון.
/// "אישור" מאשר וסוגר; "ביטול הנעילה" מחליף את תוכן הפס לשדה סיסמה, ובסיסמה נכונה מדלג
/// על הנעילה הקרובה (לא פותחים חלון נפרד — נשאר אותו פס).
/// </summary>
internal sealed class LockCountdown : Form
{
    // פלטת הצבעים החמה/זהב כמו שאר ההגדרות (סייר קבצים/הגדרות מערכת) - לא הכחול הקר שהיה כאן קודם
    private static readonly Color BgCream = Color.FromArgb(244, 237, 224);
    private static readonly Color TextBrown = Color.FromArgb(61, 46, 31);
    private static readonly Color GoldBorder = Color.FromArgb(138, 106, 58);
    private static readonly Color BtnPrimary = Color.FromArgb(138, 106, 58);
    private static readonly Color BtnSecondary = Color.FromArgb(232, 220, 200);

    private readonly System.Windows.Forms.Timer _tick = new() { Interval = 1000 };
    private readonly Label _msg = new() { Dock = DockStyle.Top, TextAlign = ContentAlignment.MiddleCenter, ForeColor = TextBrown };
    private readonly Label _err = new() { AutoSize = true, ForeColor = Color.FromArgb(150, 54, 44) };
    private readonly TextBox _pw = new() { UseSystemPasswordChar = true, Width = 220 };
    private readonly Button _ok, _cancel, _confirm, _back;
    private readonly FlowLayoutPanel _normalRow, _pwRow;
    private DateTime _target;
    private bool _pwMode;

    public LockCountdown()
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false; TopMost = true;
        BackColor = BgCream;
        RightToLeft = Lang.En ? RightToLeft.No : RightToLeft.Yes;
        Font = new Font("Segoe UI", 11f);

        _msg.Font = new Font("Segoe UI Semibold", 15f);
        _pw.Font = new Font("Segoe UI", 11f);

        _ok = Btn(Lang.T("אישור", "OK"), primary: true);
        _cancel = Btn(Lang.T("ביטול הנעילה", "Cancel lock"), primary: false);
        _confirm = Btn(Lang.T("בטל נעילה", "Unlock"), primary: true);
        _back = Btn(Lang.T("חזרה", "Back"), primary: false);

        _normalRow = Row(_ok, _cancel);
        _pwRow = Row(_pw, _confirm, _back);
        _pwRow.Controls.Add(_err);

        Controls.Add(_msg);
        Controls.Add(_normalRow);
        Controls.Add(_pwRow);

        _ok.Click += (_, _) => Hide();
        _cancel.Click += (_, _) => PwMode(true);
        _back.Click += (_, _) => PwMode(false);
        _confirm.Click += (_, _) => DoCancel();
        _pw.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; DoCancel(); } };
        _tick.Tick += (_, _) => Upd();
    }

    // בניגוד לשאר ה-overlays (שעון/סרגל צד) - זה טופס שצריך לקבל הקלדה בפועל (שדה הסיסמה), אז
    // WS_EX_NOACTIVATE קבוע היה מונע מהחלון להיות מופעל בכלל - שום טקסט לא היה מגיע לשדה, לעולם.
    // לכן ה-flag מוסר/מוחזר דינמית ב-PwMode: נשאר כש"לא גונב פוקוס" רק כל עוד זה תצוגת ספירה פסיבית.
    protected override bool ShowWithoutActivation => !_pwMode;

    protected override CreateParams CreateParams
    {
        get { var cp = base.CreateParams; if (!_pwMode) cp.ExStyle |= 0x08000000 /* WS_EX_NOACTIVATE */; return cp; }
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);
    private const int GwlExStyle = -20;
    private const long WsExNoActivate = 0x08000000;

    /// <summary>מוסיף/מסיר את WS_EX_NOACTIVATE על ה-handle החי (CreateParams נקרא רק ביצירה, לא אחר כך)</summary>
    private void SetNoActivate(bool on)
    {
        if (!IsHandleCreated) return;
        long cur = GetWindowLongPtr(Handle, GwlExStyle).ToInt64();
        long next = on ? (cur | WsExNoActivate) : (cur & ~WsExNoActivate);
        if (next != cur) SetWindowLongPtr(Handle, GwlExStyle, new IntPtr(next));
    }

    private int ScaleX(int v) => (int)Math.Round(v * DeviceDpi / 96.0);

    private Button Btn(string text, bool primary) => new()
    {
        Text = text, AutoSize = true, FlatStyle = FlatStyle.Flat,
        BackColor = primary ? BtnPrimary : BtnSecondary,
        ForeColor = primary ? Color.White : TextBrown, Font = new Font("Segoe UI Semibold", 10.5f),
        Padding = new Padding(ScaleX(10), ScaleX(6), ScaleX(10), ScaleX(6)),
        FlatAppearance = { BorderSize = 0 },
    };

    /// <summary>שורת כפתורים/שדות ממורכזת אופקית (לא Dock — ממוקמת ידנית ב-CenterRows כדי לא להימתח לרוחב מלא)</summary>
    private FlowLayoutPanel Row(params Control[] controls)
    {
        var p = new FlowLayoutPanel
        {
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false,
            FlowDirection = Lang.En ? FlowDirection.LeftToRight : FlowDirection.RightToLeft,
        };
        p.Controls.AddRange(controls);
        return p;
    }

    public void SetTarget(DateTime target)
    {
        _target = target;
        Place();
        PwMode(false);
        if (!_tick.Enabled) _tick.Start();
    }

    /// <summary>פאנל ברוחב שני-שליש מהמסך, ממורכז אופקית, בתוך השליש התחתון אנכית</summary>
    private void Place()
    {
        var b = Screen.PrimaryScreen!.Bounds;
        Width = b.Width * 2 / 3;
        Height = ScaleX(150);
        _msg.Height = ScaleX(46);
        int thirdTop = b.Top + b.Height * 2 / 3;
        int thirdHeight = b.Bottom - thirdTop;
        Location = new Point(b.Left + (b.Width - Width) / 2, thirdTop + Math.Max(0, (thirdHeight - Height) / 2));
        CenterRows();
    }

    /// <summary>מסגרת זהב דקה - בלי זה פאנל קרם ממורכז (לא ברוחב מלא) לא נקרא כ"כרטיס" על שולחן העבודה</summary>
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(GoldBorder, 2);
        e.Graphics.DrawRectangle(pen, 1, 1, Width - 3, Height - 3);
    }

    private void CenterRows()
    {
        int y = _msg.Bottom + ScaleX(14);
        _normalRow.Location = new Point((Width - _normalRow.Width) / 2, y);
        _pwRow.Location = new Point((Width - _pwRow.Width) / 2, y);
    }

    private void PwMode(bool on)
    {
        _pwMode = on;
        _normalRow.Visible = !on;
        _pwRow.Visible = on;
        _err.Text = "";
        SetNoActivate(!on); // מצב סיסמה: מסירים את NOACTIVATE כדי שהחלון יוכל בכלל לקבל פוקוס מקלדת
        if (on)
        {
            _msg.Text = Lang.T("הקלידו את סיסמת הנעילה לביטול", "Enter the lock password to cancel");
            _pw.Text = "";
            Kiosk.BringToFront(Handle); // לא מספיק להסיר NOACTIVATE - צריך גם לדרוש בפועל את החזית
            _pw.Focus();
        }
        else Upd(); // מחזיר את טקסט הספירה הרגיל
        CenterRows();
    }

    private void Upd()
    {
        var left = _target - DateTime.Now;
        if (left <= TimeSpan.Zero) { _tick.Stop(); Hide(); return; }
        if (_pwMode) return; // לא דורסים את הנחיית הסיסמה בכל שנייה
        int m = (int)left.TotalMinutes, s = left.Seconds;
        _msg.Text = Lang.T($"המחשב ננעל בעוד {m}:{s:00} לאוצריא בלבד", $"The computer locks to Otzaria only in {m}:{s:00}");
    }

    private void DoCancel()
    {
        // הדילוג עובר דרך השירות ועשוי לדרוש Windows Hello (עד כ-75 שניות) — חוט נפרד כדי שהבאנר לא יקפא
        string pw = _pw.Text;
        _confirm.Enabled = _back.Enabled = false;
        _err.Text = Lang.T("מאמת…", "Verifying…");
        CenterRows();
        Task.Run(() =>
        {
            Exception error = null;
            try { Kiosk.ScheduleSkipCurrent(pw); } catch (Exception ex) { error = ex; }
            try
            {
                BeginInvoke(new Action(() =>
                {
                    _confirm.Enabled = _back.Enabled = true;
                    if (error != null)
                    {
                        _err.Text = error.Message;
                        _pw.SelectAll(); _pw.Focus(); CenterRows();
                        return;
                    }
                    _tick.Stop();
                    Hide();
                }));
            }
            catch { }
        });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _tick.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>
/// באנר "המחשב פתוח זמנית": אותה משפחה חזותית כמו אזהרת הנעילה המתוזמנת (LockCountdown) — פס כהה לרוחב המסך
/// בשליש התחתון, מותאם DPI, בעברית מימין לשמאל — עם ספירה לאחור חיה עד שהנעילה חוזרת.
/// לא גונב פוקוס (ShowWithoutActivation + WS_EX_NOACTIVATE), לא מופיע בשורת המשימות ותמיד מעל.
/// "הסתרה" מסתירה רק את הבאנר; הפתיחה הזמנית ממשיכה, והזמן שנותר מוצג בהגדרות מערכת ← נעילה ובסייר הקבצים.
/// </summary>
internal sealed class TempUnlockBanner : Form
{
    private readonly System.Windows.Forms.Timer _tick = new() { Interval = 1000 };
    private readonly Label _title = new() { Dock = DockStyle.Top, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Color.White };
    private readonly Label _left = new() { Dock = DockStyle.Top, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Color.FromArgb(220, 225, 232) };
    private readonly Button _hide;
    private readonly FlowLayoutPanel _row;
    private readonly Action _onDismiss;

    public DateTimeOffset Until { get; private set; }

    public TempUnlockBanner(Action onDismiss)
    {
        _onDismiss = onDismiss;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false; TopMost = true;
        BackColor = Color.FromArgb(24, 28, 34);
        RightToLeft = Lang.En ? RightToLeft.No : RightToLeft.Yes;
        Font = new Font("Segoe UI", 11f);

        _title.Font = new Font("Segoe UI Semibold", 15f);
        _left.Font = new Font("Segoe UI", 12f);
        _title.Text = Lang.T("המחשב פתוח זמנית", "The computer is temporarily unlocked");

        _hide = new Button
        {
            Text = Lang.T("הסתרה", "Hide"), AutoSize = true, FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(48, 53, 61), ForeColor = Color.White, Font = new Font("Segoe UI Semibold", 10.5f),
            Padding = new Padding(ScaleX(10), ScaleX(6), ScaleX(10), ScaleX(6)),
            FlatAppearance = { BorderSize = 0 },
        };
        _row = new FlowLayoutPanel
        {
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false,
            FlowDirection = Lang.En ? FlowDirection.LeftToRight : FlowDirection.RightToLeft,
        };
        _row.Controls.Add(_hide);

        // סדר ההוספה: Dock=Top האחרון שנוסף נמצא למעלה, ולכן הכותרת נוספת אחרונה
        Controls.Add(_row);
        Controls.Add(_left);
        Controls.Add(_title);

        _hide.Click += (_, _) => { _onDismiss?.Invoke(); Hide(); };
        _tick.Tick += (_, _) => Upd();
    }

    protected override bool ShowWithoutActivation => true; // לא גונב פוקוס ממה שהמשתמש עושה

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x08000000 /* WS_EX_NOACTIVATE */ | 0x00000080 /* WS_EX_TOOLWINDOW */ | 0x00000008 /* WS_EX_TOPMOST */;
            return cp;
        }
    }

    private int ScaleX(int v) => (int)Math.Round(v * DeviceDpi / 96.0);

    public void SetUntil(DateTimeOffset until)
    {
        bool changed = until != Until;
        Until = until;
        if (changed || !Visible) Place();
        Upd();
        if (!_tick.Enabled) _tick.Start();
    }

    /// <summary>פס לרוחב מלא של המסך, ממורכז אנכית בתוך השליש התחתון (כמו LockCountdown)</summary>
    private void Place()
    {
        var b = Screen.PrimaryScreen!.Bounds;
        Width = b.Width;
        Height = ScaleX(140);
        _title.Height = ScaleX(44);
        _left.Height = ScaleX(30);
        int thirdTop = b.Top + b.Height * 2 / 3;
        int thirdHeight = b.Bottom - thirdTop;
        Location = new Point(b.Left, thirdTop + Math.Max(0, (thirdHeight - Height) / 2));
        _row.Location = new Point((Width - _row.Width) / 2, _left.Bottom + ScaleX(10));
    }

    private void Upd()
    {
        int secs = TempUnlock.SecondsLeft(Until, DateTimeOffset.Now);
        if (secs <= 0) { _tick.Stop(); Hide(); return; }
        string t = TempUnlock.Format(secs);
        _left.Text = Lang.T($"הנעילה תחזור בעוד {t}", $"The lock returns in {t}");
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _tick.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>מסך שחור ללא פעילות, למסכים נוספים בנעילה (לא לוקח פוקוס, לא ניתן לבחירה)</summary>
internal sealed class BlackCover : Form
{
    public BlackCover()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.Black;
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x08000000 /* WS_EX_NOACTIVATE */ | 0x00000080 /* WS_EX_TOOLWINDOW */;
            return cp;
        }
    }
}
