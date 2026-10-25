using System.Linq;
using System.Collections.Generic;
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
    /// <summary>הפסוק בשומר המסך: פסוק מותאם אישית (kiosk.restVerseText) או ברירת המחדל</summary>
    internal static string VerseText()
    {
        string v = Config.Get("kiosk.restVerseText", "").Trim();
        return v.Length > 0 ? v : "שִׁבְתִּי בְּבֵית ה' כָּל יְמֵי חַיַּי";
    }

    private static ClockBar _clock;
    // בעיה #14 סעיף 3: חלון "RestScreen-like" אחד לכל מסך (Screen.AllScreens), כולם מוצגים/מוסתרים יחד
    private static readonly List<RestScreen> _rest = new();

    public static bool RestVisible => _rest.Any(r => r.Visible);
    /// <summary>מסך המנוחה מוצג בגלל חוסר פעילות (ולא בגלל מזעור) — לא להסתיר אותו כשאוצריא לא ממוזערת</summary>
    public static bool RestIdle => _rest.Any(r => r is { Visible: true, Idle: true });

    /// <summary>
    /// המסך שעליו חלון ביטול החירום פתוח (null = סגור). רק הוא נשאר בלי כיסוי, כדי שהחירום תמיד נגיש
    /// וכל שאר המסכים נשארים מכוסים (מינימום חשיפה). חוט הממשק בלבד.
    /// </summary>
    private static string EmergencyScreen()
    {
        try
        {
            var f = Application.OpenForms.OfType<EmergencyForm>().FirstOrDefault(x => x.Visible && !x.IsDisposed);
            return f == null ? null : Screen.FromControl(f).DeviceName;
        }
        catch { return null; }
    }

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
        var screens = Screen.AllScreens;
        string skip = EmergencyScreen(); // חלון חירום פתוח: מכסים את כל המסכים חוץ מהמסך שלו (לא מכסים את החלון)
        // התאמה למספר המסכים (גם אם השתנה: חיבור/ניתוק מסך בזמן ריצה)
        while (_rest.Count < screens.Length) _rest.Add(new RestScreen());
        while (_rest.Count > screens.Length)
        {
            var extra = _rest[^1];
            _rest.RemoveAt(_rest.Count - 1);
            extra.Dispose();
        }
        bool wasVisible = RestVisible;
        bool shown = false;
        for (int i = 0; i < _rest.Count; i++)
        {
            var r = _rest[i];
            r.Target = otzaria;
            if (screens[i].DeviceName == skip) { if (r.Visible) r.Hide(); continue; }
            if (r.Visible) continue;
            if (!wasVisible && !r.Visible) r.Idle = idle;
            r.Bounds = screens[i].Bounds;
            r.Show();
            shown = true;
        }
        if (shown) { var first = _rest.FirstOrDefault(x => x.Visible); first?.Activate(); }
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
            // מסך מנוחה כבר מכסה את המסכים (חוץ ממסך החירום, אם פתוח): אין צורך בכיסוי נוסף
            if (screens.Length < 2 || !Config.Get("kiosk.coverOtherScreens", true) || RestVisible) { HideCovers(); return; }
            string skip = EmergencyScreen(); // מסך חלון החירום נשאר חשוף, כל השאר מכוסים
            Screen main = otzaria != IntPtr.Zero ? Screen.FromHandle(otzaria) : Screen.PrimaryScreen;
            var others = screens.Where(sc => sc.DeviceName != main.DeviceName && sc.DeviceName != skip).ToArray();
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
            // לפי נתיב התמונה ולא שם בלבד (Kiosk.FindOtzariaProcess)
            hwnd = Kiosk.FindOtzariaProcess(Process.GetCurrentProcess().SessionId)?.MainWindowHandle ?? IntPtr.Zero;
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
        // [x2] WHY: Form.TopMost=true גורם ל-WinForms להפעיל/למקד את החלון בהצגה גם עם NOACTIVATE (גניבת פוקוס מהקלדה באוצריא).
        // TopMost נשאר רק דרך WS_EX_TOPMOST ב-CreateParams
        StartPosition = FormStartPosition.Manual;
        BackColor = Theme.Bar;
        ForeColor = Theme.BarText;
        Opacity = 0.92;
        DoubleBuffered = true;
        Size = new Size(ScaleX(250), ScaleX(64));
        Cursor = Cursors.Hand;
        RightToLeft = RightToLeft.Yes;
        _tick.Tick += (_, _) => Refresh_();
        // 7 לחיצות מהירות (עד 3 שניות בין לחיצה ללחיצה) פותחות את חלון קוד השחזור - דרך מילוט שלא תלויה
        // במקלדת/ב-hook (בחלק מהמחשבים F12 לא מגיע כמות שהוא). לחיצה בודדת ממשיכה כרגיל
        int clicks = 0; var lastClick = DateTime.MinValue;
        MouseClick += (_, e) =>
        {
            var now = DateTime.UtcNow;
            clicks = (now - lastClick).TotalSeconds <= 3 ? clicks + 1 : 1;
            lastClick = now;
            if (clicks >= 7) { clicks = 0; EmergencyForm.Open(); return; }
            if (!BarProgramClick(e.Location)) Overlay.BringOtzaria(IntPtr.Zero); // D4
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
        if (IsBar)
        {
            // #60: שורת משימות מלאה ברוחב המסך (לוגו, סלוגן, הנצחה, שעון ותאריך)
            Size = new Size(wa.Width, ScaleX(40));
            Location = new Point(wa.Left, pos == "bar-top" ? wa.Top : wa.Bottom - Height);
            Region = null;
            return;
        }
        Size = new Size(ScaleX(250), ScaleX(64));
        int x = pos.EndsWith("right") ? wa.Right - Width - m : wa.Left + m;
        int y = pos.StartsWith("top") ? wa.Top + m : wa.Bottom - Height - m;
        Location = new Point(x, y);
        Region = new Region(Overlay.Round(new Rectangle(0, 0, Width, Height), ScaleX(12)));
    }

    private void Refresh_()
    {
        var now = DateTime.Now;
        _time = now.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
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
        // #56: מעבר בהיר/כהה באוצריא — הצבעים נקבעו רק בבנאי, ולכן השורה נשארה בערכת הנושא הישנה
        if (BackColor != Theme.Bar || ForeColor != Theme.BarText) { BackColor = Theme.Bar; ForeColor = Theme.BarText; }
        var g = e.Graphics;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var big = new Font("Segoe UI Semibold", 20f, GraphicsUnit.Point);
        using var small = new Font("Segoe UI", 9f, GraphicsUnit.Point);
        using var dim = new SolidBrush(Color.FromArgb(200, ForeColor));
        using var fore = new SolidBrush(ForeColor);
        var r = ClientRectangle;
        int pad = ScaleX(12);
        if (IsBar) { PaintBar(g, r, pad, big, small, fore, dim); return; }
        var timeSize = g.MeasureString(_time, big);
        g.DrawString(_time, big, fore, r.Right - pad - timeSize.Width, (r.Height - timeSize.Height) / 2f);
        var sf = new StringFormat(StringFormatFlags.DirectionRightToLeft) { Alignment = StringAlignment.Near };
        var textRect = new RectangleF(pad, ScaleX(8), r.Width - pad * 2 - timeSize.Width - ScaleX(6), ScaleX(22));
        g.DrawString(_heb, small, fore, textRect, sf);
        textRect.Y += ScaleX(24);
        g.DrawString(_date + (_bat.Length > 0 ? "   " + _bat : ""), small, dim, textRect, sf);
    }

    private static bool IsBar => Config.Get("kiosk.clockPosition", "bottom-left").StartsWith("bar-");
    private Bitmap _logo;

    /// #60: שורת המשימות של הנעילה. מימין: לוגו וסלוגן; במרכז: נוסח ההנצחה; משמאל: שעה, תאריך עברי ולועזי, סוללה.
    /// התאריך העברי מהחישוב הפנימי (Overlay.HebrewDate) — רכיב "תאריכון" רק אחרי תשובה ב-#61.
    private void PaintBar(Graphics g, Rectangle r, int pad, Font big, Font small, Brush fore, Brush dim)
    {
        var rtl = new StringFormat(StringFormatFlags.DirectionRightToLeft) { LineAlignment = StringAlignment.Center, Alignment = StringAlignment.Near };
        var mid = new StringFormat { LineAlignment = StringAlignment.Center, Alignment = StringAlignment.Center };
        using var accent = new SolidBrush(Theme.Accent(Color.FromArgb(232, 200, 140)));
        using var mf = new Font("Segoe UI Semibold", 12f, GraphicsUnit.Point);
        int h = r.Height, logo = h - ScaleX(10);
        try { _logo ??= new Icon(AppIcon(), 64, 64).ToBitmap(); g.DrawImage(_logo, r.Right - pad - logo, (h - logo) / 2, logo, logo); } catch { }
        float right = r.Right - pad - logo - ScaleX(8);
        var brand = new RectangleF(right - ScaleX(480), 0, ScaleX(480), h);
        g.DrawString(Lang.T(Program.AppFullName, Program.AppFullNameEn), mf, fore, brand, rtl);
        // D4: תוכנות מותרות בשורה (כשהסרגל הצדדי כבוי, או לפי kiosk.barPrograms)
        var bsz = g.MeasureString(Lang.T(Program.AppFullName, Program.AppFullNameEn), mf);
        PaintBarPrograms(g, (int)(right - bsz.Width - ScaleX(24)), h);
        string left = _time + "   " + _heb + "   " + _date + (_bat.Length > 0 ? "   " + _bat : "");
        var sz = g.MeasureString(left, mf);
        g.DrawString(left, mf, fore, new RectangleF(pad, 0, sz.Width + ScaleX(8), h), new StringFormat { LineAlignment = StringAlignment.Center });
    }
    private static Icon AppIcon() => Overlay.AppIcon();

    // ===== D4: התוכנות המותרות בתוך שורת המשימות של הנעילה =====
    private List<(Programs.Entry entry, Bitmap icon, string name)> _progs;
    private DateTime _progsAt;
    private readonly List<(Rectangle rect, Programs.Entry entry)> _hits = new();

    private static bool ShowBarPrograms => Config.Get("kiosk.barPrograms", !Config.Get("kiosk.sideBar", true));

    private void PaintBarPrograms(Graphics g, int startRight, int h)
    {
        _hits.Clear();
        if (!ShowBarPrograms) return;
        try
        {
            if (_progs == null || (DateTime.Now - _progsAt).TotalSeconds > 30)
            {
                if (_progs != null) foreach (var p in _progs) p.icon?.Dispose();
                _progs = Programs.Load().Select(e => (e, SideBar.IconOf(e.Path), SideBarNames.Fix(e.Name, e.Path))).ToList();
                _progsAt = DateTime.Now;
            }
            int sz = h - ScaleX(12), gap = ScaleX(6), x = startRight;
            using var f = new Font("Segoe UI", 9f, GraphicsUnit.Point);
            using var b = new SolidBrush(ForeColor);
            _more.Clear(); _moreRect = Rectangle.Empty;
            for (int i = 0; i < _progs.Count; i++)
            {
                var (entry, icon, name) = _progs[i];
                x -= sz;
                if (x - sz - gap < ScaleX(420) && i < _progs.Count - 1)
                {   // אין מקום לכולן: כפתור "עוד" שפותח תפריט עם שאר התוכנות
                    _moreRect = new Rectangle(x, (h - sz) / 2, sz, sz);
                    using var mf = new Font("Segoe UI", 14f, FontStyle.Bold, GraphicsUnit.Point);
                    g.DrawString("⋯", mf, b, _moreRect, new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center });
                    for (int j = i; j < _progs.Count; j++) _more.Add((_progs[j].Item1, _progs[j].Item3));
                    break;
                }
                if (x < ScaleX(420)) break; // לא לדרוס את השעה והתאריך משמאל
                var rc = new Rectangle(x, (h - sz) / 2, sz, sz);
                if (icon != null) g.DrawImage(icon, rc);
                else g.DrawString(name.Length > 0 ? name[..1] : "?", f, b, rc, new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center });
                _hits.Add((rc, entry));
                x -= gap;
            }
        }
        catch (Exception ex) { Config.Log("bar programs: " + ex.Message); }
    }

    private readonly List<(Programs.Entry entry, string name)> _more = new();
    private Rectangle _moreRect = Rectangle.Empty;

    private bool BarProgramClick(Point p)
    {
        if (!_moreRect.IsEmpty && _moreRect.Contains(p) && _more.Count > 0)
        {
            var m = new ContextMenuStrip { RightToLeft = RightToLeft.Yes };
            foreach (var (entry, name) in _more.ToList())
            {
                var e = entry;
                m.Items.Add(name, SideBar.IconOf(e.Path), (_, _) => { try { Programs.Activate(e); } catch (Exception ex) { Config.Log("bar more: " + ex.Message); } });
            }
            m.Show(this, new Point(_moreRect.Left, _moreRect.Top), ToolStripDropDownDirection.AboveLeft);
            return true;
        }
        foreach (var (rect, entry) in _hits)
            if (rect.Contains(p)) { try { Programs.Activate(entry); } catch (Exception ex) { Config.Log("bar program: " + ex.Message); } return true; }
        return false;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _tick.Dispose(); _logo?.Dispose(); }
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
        try { using var big = new Icon(_icon, 256, 256); _logo = big.ToBitmap(); } catch { _logo = SystemIcons.Application.ToBitmap(); }
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

    protected override void OnPaint(PaintEventArgs e) =>
        Render(e.Graphics, ClientRectangle, (float)_clock.Elapsed.TotalSeconds, DeviceDpi / 96f);

    /// <summary>ציור מסך המנוחה — גם לתמונת מסך הנעילה של Windows (LockScreenImage)</summary>
    // משוב 15.10: חלק שנכשל (חריגה בציור/בחישוב) לא מוחק את כל שאר המסך — נרשם ביומן פעם אחת לכל חלק
    private static readonly HashSet<string> _partErrLogged = new();
    private static void SafePart(string name, Action a)
    {
        try { a(); }
        catch (Exception ex) { lock (_partErrLogged) if (_partErrLogged.Add(name)) Config.Log("rest screen [" + name + "]: " + ex); }
    }

    internal bool StillImage; // תמונת מסך נעילה: בלי שורת "לחיצה חוזרת"
    internal void Render(Graphics g, Rectangle r, float t, float s)
    {
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
            // משוב 15.10: בלי תזוזה — רק נצנוץ במקום
            if (p.Y < -0.05f) { p = NewParticle(false); p.Ch = '\0'; }
            float px = p.X * r.Width, py = p.Y * r.Height;
            int a = (int)(25 + 55 * (Math.Sin(t * 0.6f + p.Phase) * 0.5 + 0.5));
            float d = p.Size * 2.2f * s;
            using var sb = new SolidBrush(Color.FromArgb(a, 225, 215, 190));
            g.FillEllipse(sb, px, py, d, d);
        }

        // כל התוכן נע לאט מאוד (מניעת צריבה)
        // משוב 15.10: התוכן לא זז כלל (רק החותמת מרחפת) — dx/dy נשארים 0
        float dx = 0f, dy = 0f;
        float cx = r.Width / 2f + dx;
        var rtl = new StringFormat(StringFormatFlags.DirectionRightToLeft) { Alignment = StringAlignment.Center };
        var center = new StringFormat { Alignment = StringAlignment.Center };
        Color gold = ReadableColor.OnDark(Theme.Accent(Color.FromArgb(232, 200, 140)));
        using var goldB = new SolidBrush(gold);
        using var dim = new SolidBrush(Color.FromArgb(170, 220, 214, 200));
        using var headF = new Font("David", 40f, FontStyle.Bold, GraphicsUnit.Point);
        using var verseF = new Font("David", 34f, FontStyle.Bold, GraphicsUnit.Point);
        using var timeF = new Font("Segoe UI Light", 64f, GraphicsUnit.Point);
        using var dateF = new Font("David", 20f, GraphicsUnit.Point);
        using var dedF = new Font("David", 17f, GraphicsUnit.Point);
        using var dedNameF = new Font("David", 21f, FontStyle.Bold, GraphicsUnit.Point);
        using var hintF = new Font("Segoe UI", 11f, GraphicsUnit.Point);
        // זמני היום בעמודה בצד (kiosk.restZmanimSide: right/left), כדי שלא יתנגשו בהקדשה ובשעה במרכז.
        // העמודה לא זזה עם dx (רק dy), והתוכן במרכז מוגבל לרוחב שנשאר בין שתי השוליים.
        // #57: בחירת תוכן — full (הכל), zmanim (בלי פסוק), clock (שעה ותאריך בלבד), verse (פסוק בלי זמנים)
        string rc = Config.Get("kiosk.restContent", "full");
        // משוב 15.10: מתג נפרד לכל פריט; בלי הגדרה חדשה — לפי restContent הישן
        bool shVerse = Config.Get("kiosk.restShowVerse", rc is "full" or "verse");
        bool shClock = Config.Get("kiosk.restShowClock", true);
        bool shZm = Config.Get("kiosk.restShowZmanim", rc is "full" or "zmanim");
        bool shLogo = Config.Get("kiosk.restShowLogo", true);
        bool shDed = Config.Get("kiosk.restShowDedication", true);
        List<string> zmItems = new();
        // CachedZmanim: מחושב פעם ב-30 שניות ולא בכל פריים (OnPaint ~25 פעמים בשנייה)
        if (shZm) SafePart("zmanim-calc", () => zmItems = CachedZmanim());
        bool zmLeft = Config.Get("kiosk.restZmanimSide", "right") == "left";
        // d992415 (יאיר) + משוב 15.10 — "גם וגם": סגנון ההקדשה הוא בחירת המשתמש (kiosk.restDedicationStyle):
        // "fixed" (ברירת מחדל) — ההקדשה בגבולות קבועים בתחתית הצד שמול הזמנים; "column" — עמודת הקדשה של יאיר עם נר.
        // הנר מרצד רק אם kiosk.restCandleFlicker (ברירת מחדל: כבוי — שומר מסך בלי תזוזה חוץ מהחותמת).
        bool dedColumn = Config.Get("kiosk.restDedicationStyle", "fixed") == "column";
        bool candleFlicker = Config.Get("kiosk.restCandleFlicker", true);
        // טקסט אחד לשני הסגנונות: הנוסח המותאם (kiosk.restDedicationText); אם ריק — ההקדשה האישית של יאיר (kiosk.dedicationText); אחרת הקבועה
        string dedication = Config.Get("kiosk.restDedicationText", "").Trim();
        if (dedication.Length == 0) dedication = Config.Get("kiosk.dedicationText", "").Trim();
        if (dedication.Length == 0) dedication = "לעילוי נשמת הנערה הצדקנית\nאסתר גיטה דינר ע\"ה\nבת רבי מיכאל שלום הלוי שיבדלט\"א\nתנצב\"ה";
        bool hasDedication = dedColumn && shDed;
        bool hasSideColumn = zmItems.Count > 0 || hasDedication;
        float colW = hasSideColumn ? Math.Min(380 * s, r.Width * 0.26f) : 0;
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
        SafePart("verse", () =>
        {
        // סבב B: הפסוק של "אחת שאלתי" (תהלים כז, ד)
        if (shVerse)
        {
            /* משוב 3.9: השורה הייתה רחבה מהמלבן ו"אבקש" נחתך — מקטינים את הגופן עד שהשורה נכנסת */
            const string v1 = "אַחַת שָׁאַלְתִּי מֵאֵת ה' אוֹתָהּ אֲבַקֵּשׁ";
            var vf = verseF;
            for (int k = 0; k < 12 && g.MeasureString(v1, vf).Width > W * 0.96f; k++) vf = new Font(vf.FontFamily, vf.Size * 0.92f, vf.Style, vf.Unit);
            var noWrap = (StringFormat)rtl.Clone(); noWrap.FormatFlags |= StringFormatFlags.NoWrap;
            g.DrawString(v1, vf, goldB, Line(y, 56 * s), noWrap);
            g.DrawString(Overlay.VerseText(), verseF, goldB, Line(y + 54 * s, 56 * s), rtl);
            y += 54 * s;
        }
        y += 72 * s;

        });
        SafePart("logo", () =>
        {
        // B2 (סבב B, 3.7.1): סמל "אחת שאלתי" גדול במרכז, מרחף ומסתובב לאט (חסכוני — ציור מטמון אחד, בלי אנימציה כבדה)
        float icoSize = Math.Min(r.Height * 0.26f, 260 * s);
        float bob = (float)Math.Sin(t * 0.5) * 8 * s;
        float tilt = (float)Math.Sin(t * 0.25) * 6f;
        if (shLogo)
        {
            /* משוב 3.9: בלי סיבוב ובלי שינוי גודל בזמן ציור (שניהם טשטשו) — תמונה מוכנה בגודל המדויק, במיקום שלם בפיקסלים */
            int px = (int)Math.Round(icoSize);
            g.DrawImageUnscaled(BrandArt.Sized(px), (int)Math.Round(cx - px / 2f), (int)Math.Round(y + bob));
            _ = tilt;
            y += icoSize + 20 * s;
        }

        });
        SafePart("clock", () =>
        {
        var now = DateTime.Now;
        if (shClock)
        {
            g.DrawString(now.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture), timeF, Brushes.White, Line(y, 110 * s), center);
            y += 108 * s;
        }
        // שם מחשב/בית כנסת (קיוסק.venueName) - אופציונלי, ישירות מתחת לשעון. ריק = לא תופס מקום בכלל
        string venue = Config.Get("kiosk.venueName", "");
        if (!string.IsNullOrWhiteSpace(venue))
        {
            using (var venueF = new Font(UiFamily, 20f, GraphicsUnit.Point))
                g.DrawString(venue, venueF, dim, Line(y, 36 * s), center);
            y += 36 * s;
        }
        // #2: התאריך הלועזי באותה שורה עם העברי (לא בשורה נפרדת שנופלת מתחת להקדשה)
        if (shClock) g.DrawString(Overlay.HebrewDate(now) + "   ·   " + now.ToString("d/M/yyyy"), dateF, Brushes.White, Line(y, 40 * s), rtl);
        if (Target == IntPtr.Zero && Idle)
        {
            y += 34 * s;
            g.DrawString(Lang.T("אוצריא לא נמצאה במחשב. לביטול הנעילה: Ctrl+Alt+Shift+F12", "Otzaria was not found. To unlock: Ctrl+Alt+Shift+F12"), hintF, dim, Line(y, 30 * s), rtl);
        }
        });
        SafePart("zmanim", () =>
        {
        if (zmItems.Count > 0)
            DrawZmanimColumn(g, zmItems, zmLeft ? colMargin : r.Width - colMargin - colW,
                r.Height * 0.08f, colW, r.Height - 70 * s - r.Height * 0.08f, s, gold);

        });
        SafePart("dedication", () =>
        {
        // B2: ההקדשה בפינה השמאלית-התחתונה (לא במרכז)
        // משוב 15.10: ההקדשה בצד שמול עמודת הזמנים, בגבולות קבועים (רוחב העמודה הצדדית בלבד) — אין חפיפה
        float dedW = zmItems.Count > 0 ? Math.Max(colW + colMargin, Math.Min(420 * s, (r.Width - W) / 2f - 12 * s)) : Math.Min(520 * s, r.Width * 0.4f);
        float dedX = zmItems.Count > 0 && !zmLeft ? 24 * s : (zmItems.Count > 0 ? r.Width - 24 * s - dedW : 24 * s);
        var ded = new RectangleF(dedX, r.Height - 215 * s, dedW, 175 * s);
        var dedFmt = new StringFormat(StringFormatFlags.DirectionRightToLeft) { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        // משוב 15.10: כיתוב אישי (kiosk.restNoteText) בריבוע ממוסגר, בצד שמול הזמנים, מעל ההקדשה
        string note = Config.Get("kiosk.restNoteText", "").Trim();
        float noteBottom = r.Height * 0.08f;
        if (note.Length > 0)
        {
            using var noteF = new Font("David", 18f, GraphicsUnit.Point);
            float pad = 16 * s, top = r.Height * 0.08f, maxBottom = ded.Y - 16 * s;
            var fmtN = new StringFormat(StringFormatFlags.DirectionRightToLeft) { Alignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisWord };
            float inner = ded.Width - 2 * pad;
            float th = Math.Min(g.MeasureString(note, noteF, (int)inner, fmtN).Height, maxBottom - top - 2 * pad);
            if (th > 10 * s)
            {
                var box = new Rectangle((int)ded.X, (int)top, (int)ded.Width, (int)(th + 2 * pad));
                using var path = Overlay.Round(box, (int)(14 * s));
                using var bgN = new SolidBrush(Color.FromArgb(70, 10, 12, 22));
                using var pen = new Pen(Color.FromArgb(150, gold), 1.4f * s);
                g.FillPath(bgN, path); g.DrawPath(pen, path);
                g.SetClip(path);
                g.DrawString(note, noteF, Brushes.White, new RectangleF(box.X + pad, box.Y + pad, inner, th), fmtN);
                g.ResetClip();
                noteBottom = box.Bottom + 16 * s;
            }
        }
        if (hasDedication)
        {
            // עמודת ההקדשה של יאיר, באותו צד ובאותם גבולות קבועים של ההקדשה (מתחת לכיתוב האישי), עם חיתוך
            float colBottom = r.Height - 70 * s;
            if (colBottom - noteBottom > 120 * s)
            {
                var colR = new RectangleF(ded.X, noteBottom, ded.Width, colBottom - noteBottom);
                g.SetClip(colR);
                DrawDedicationColumn(g, dedication, colR.X, colR.Y, colR.Width, colR.Height, s, gold, candleFlicker ? t : 0f);
                g.ResetClip();
            }
        }
        else if (shDed)
        {
            // נוסח מותאם (kiosk.restDedicationText): שורה 1 קטנה, שורה 2 מודגשת, השאר קטן. ריק = הקבוע
            string custom = Config.Get("kiosk.restDedicationText", "").Trim();
            string[] dl = custom.Length > 0
                ? custom.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(4).ToArray()
                : new[] { "לעילוי נשמת הנערה הצדקנית", "אסתר גיטה דינר ע\"ה", "בת רבי מיכאל שלום הלוי שיבדלט\"א", "תנצב\"ה" };
            // שורת "· תנצב"ה" / "· ת.נ.צ.ב.ה" שבסוף שורה — יורדת לשורה משלה; כל שורה ממורכזת ומוקטנת אם רחבה מהמסגרת (לא נחתכת)
            var split = new List<string>();
            foreach (var line in dl)
            {
                int dot = line.LastIndexOf(" · ", StringComparison.Ordinal);
                string tail = dot > 0 ? line[(dot + 3)..].Trim() : "";
                if (dot > 0 && (tail.Replace(".", "").Replace("\"", "").Replace("'", "") is "תנצבה" or "תנצבה" || tail.StartsWith("ת.נ.צ.ב", StringComparison.Ordinal) || tail.StartsWith("תנצב", StringComparison.Ordinal)))
                { split.Add(line[..dot].Trim()); split.Add(tail); }
                else split.Add(line);
            }
            dl = split.Take(5).ToArray();
            float totalH = 0; foreach (var _ in dl) totalH += 34 * s;
            float dyy = ded.Bottom - Math.Max(totalH, 0) + 4 * s;
            g.SetClip(new RectangleF(ded.X - 4 * s, dyy - 6 * s, ded.Width + 8 * s, totalH + 8 * s));
            for (int i = 0; i < dl.Length; i++)
            {
                bool bold = dl.Length > 1 ? i == 1 : true;
                float h = 34 * s;
                var baseFont = bold ? dedNameF : dedF;
                float wText = g.MeasureString(dl[i], baseFont).Width;
                float k = wText > ded.Width ? ded.Width / wText : 1f;
                using var fit = k < 1f ? new Font(baseFont.FontFamily, Math.Max(9f, baseFont.Size * k * 0.97f), baseFont.Style, GraphicsUnit.Point) : null;
                g.DrawString(dl[i], fit ?? baseFont, bold ? goldB : dim, new RectangleF(ded.X, dyy, ded.Width, h), dedFmt);
                dyy += h;
            }
            g.ResetClip();
        }

        });
        // רמז קטן בתחתית; אין צורך בכפתור — כל לחיצה חוזרת
        _btn = Rectangle.Empty;
        if (!StillImage) g.DrawString(Lang.T("לחיצה או מקש כלשהו — חזרה לאוצריא", "Click or press any key to return to Otzaria"), hintF, dim,
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

    /// <summary>עמודת זמנים עם רקע עדין. הגופן קטן עד שכל השורות נכנסות בגובה וברוחב; אם גם בגופן
    /// המינימלי לא נכנס — מתחלקים לשתי תת-עמודות. כך לא נחתך אף זמן (באג #59: "לא רואים את כל הזמנים").</summary>
    private static void DrawZmanimColumn(Graphics g, List<string> items, float x, float y, float w, float maxH, float s, Color gold)
    {
        float pad = 14 * s, gap = 10 * s, lineH = 0; int cols = 1;
        Font f = null;
        float Need(int c, float lh)
        {
            // גובה העמודה הגבוהה מבין c תת-עמודות (שורה ריקה = חצי שורה)
            float best = 0; int per = (int)Math.Ceiling(items.Count / (double)c);
            for (int k = 0; k < c; k++)
            {
                float h = 0;
                foreach (var it in items.Skip(k * per).Take(per)) h += it.Length == 0 ? lh * 0.5f : lh;
                best = Math.Max(best, h);
            }
            return 24 * s + best;
        }
        // כל זמן בשורה משלו: קודם עמודה אחת בגופן הגדול ביותר שנכנס (עד 22pt, לא פחות מ-13pt); רק אם לא נכנס — שתי תת-עמודות
        bool chosen = false;
        for (int pass = 1; pass <= 2 && !chosen; pass++)
        {
            cols = pass;
            for (float size = 22f; size >= (pass == 1 ? 13f : 9f); size -= 1f)
            {
                f?.Dispose();
                f = new Font(UiFamily, size, FontStyle.Bold, GraphicsUnit.Point); // גופן ברירת המחדל של Windows — קיים ומכסה עברית (כולל גרש/גרשיים) אצל כולם
                lineH = f.GetHeight(g) * 1.2f;
                float widest = 0;
                foreach (var it in items) if (it.Length > 0) widest = Math.Max(widest, g.MeasureString(it, f).Width);
                float colInner = (w - 2 * pad - gap * (cols - 1)) / cols;
                if (widest <= colInner && Need(cols, lineH) <= maxH) { chosen = true; break; }
            }
        }
        if (!chosen) cols = 2;
        maxH = Math.Min(maxH, Need(cols, lineH));
        using (f)
        using (var bg = new SolidBrush(Color.FromArgb(70, 10, 12, 22)))
        using (var border = new Pen(Color.FromArgb(150, gold), 1.4f * s)) // משוב 15.10: מסגרת דקה ברורה
        using (var path = Overlay.Round(new Rectangle((int)x, (int)y, (int)w, (int)maxH), (int)(14 * s)))
        using (var goldB = new SolidBrush(gold))
        {
            g.FillPath(bg, path);
            g.DrawPath(border, path);
            g.SetClip(path); // שום טקסט לא זולג מחוץ למסגרת
            var fmt = new StringFormat(StringFormatFlags.DirectionRightToLeft) { Alignment = StringAlignment.Near, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.DirectionRightToLeft | StringFormatFlags.NoWrap };
            int per = (int)Math.Ceiling(items.Count / (double)cols);
            float colW = (w - 2 * pad - gap * (cols - 1)) / cols;
            for (int k = 0; k < cols; k++)
            {
                // RTL: תת-העמודה הראשונה בצד ימין
                float cx0 = x + w - pad - (k + 1) * colW - k * gap;
                float yy = y + 12 * s;
                foreach (var it in items.Skip(k * per).Take(per))
                {
                    if (it.Length == 0) { yy += lineH * 0.5f; continue; }
                    g.DrawString(it, f, goldB, new RectangleF(cx0, yy, colW, lineH), fmt);
                    yy += lineH;
                }
            }
            g.ResetClip();
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
        float candleW = Math.Min(48f, w * 0.30f) * s;   // הנר גדול פי 2
        float candleH = Math.Max(40f * s, Math.Min(180f * s, maxH * 0.5f - 80f * s));
        float baseY = y + maxH - 14 * s;
        float bodyTop = baseY - candleH;

        float flick = (float)(Math.Sin(t * 9.1) * 0.5 + Math.Sin(t * 13.7 + 1.3) * 0.3 + Math.Sin(t * 5.3 + 2.1) * 0.2);
        float sway = (float)(Math.Sin(t * 0.8) * 2.5 + Math.Sin(t * 6.3) * 1.5) * s;
        float flameH = (28f + flick * 8f) * 1.5f * s;   // הלהבה גדולה פי 1.5, וריצוד בולט יותר
        float flameW = (13f + flick * 3.5f) * 1.5f * s;
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
        SafeTask.Run(() =>
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
        ShowInTaskbar = false;   // [x2] בלי Form.TopMost (גניבת פוקוס): WS_EX_TOPMOST ב-CreateParams
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
        ShowInTaskbar = false;   // [x2] בלי Form.TopMost (גניבת פוקוס): WS_EX_TOPMOST ב-CreateParams
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.Black;
    }

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
}
