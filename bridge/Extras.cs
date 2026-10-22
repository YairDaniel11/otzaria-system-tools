using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace OtzariaBridge;

/// <summary>
/// סרגל תוכנות בצד המסך בזמן נעילה: אוצריא והתוכנות התורניות המותרות. לחיצה מעבירה לחזית (או פותחת, אם סגורה).
/// מכווץ לפס צר ומתרחב כשהעכבר מתקרב, כדי לא להסתיר את אוצריא.
/// </summary>
internal sealed class SideBar : Form
{
    private sealed class Item
    {
        public string Name; public bool Running; public Programs.Entry Entry; public Bitmap Icon;
        public List<(IntPtr Handle, string Title)> Windows = new();
    }
    private readonly System.Windows.Forms.Timer _tick = new() { Interval = 150 };
    private List<Item> _items = new();
    private bool _expanded;
    private int _hover = -1;
    private DateTime _lastScan = DateTime.MinValue;
    private int _scanBusy;
    private bool _horizontal;
    private string _edge = "right";
    private Flyout _flyout;

    public SideBar()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        DoubleBuffered = true;
        BackColor = Color.FromArgb(24, 30, 40);
        Opacity = 0.94;
        RightToLeft = RightToLeft.Yes;
        _tick.Tick += (_, _) => Step();
        MouseMove += (_, e) => { _hover = HitTest(e.Location); Invalidate(); };
        MouseLeave += (_, _) => { _hover = -1; Invalidate(); };
        MouseClick += (_, e) => Click_(HitTest(e.Location));
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x08000000 /* NOACTIVATE */ | 0x00000080 /* TOOLWINDOW */ | 0x00000008 /* TOPMOST */;
            return cp;
        }
    }

    private float S => DeviceDpi / 96f;
    private int RowH => (int)(46 * S);                               // אנכי (ימין/שמאל): גובה שורה
    private int ColW => (int)((IconsOnly ? 46 : 92) * S);             // אופקי (למעלה/למטה): רוחב עמודה
    private bool IconsOnly => Config.Get("kiosk.sideBarIconsOnly", false);
    private int FullW => (int)((IconsOnly ? 60 : 196) * S);           // אנכי: עובי הסרגל כשפתוח
    private int FullH => (int)((IconsOnly ? 50 : 74) * S);            // אופקי: עובי הסרגל כשפתוח
    private int StripW => (int)(12 * S);                              // עובי הסרגל כשמכווץ (שני הכיוונים)

    // Programs.Load עלול להריץ סריקת כוננים (עד 8 שניות לכל תוכנה תורנית שלא נמצאה) - אסור על חוט ה-UI,
    // שגם מריץ את hook המקלדת: חסימה שם מאטה/מנתקת את ה-hook ותוקעת את המקלדת והעכבר (נראה כמו "נתקע בנעילה")
    public void Rebuild()
    {
        Task.Run(() =>
        {
            List<Programs.Entry> progs = null;
            try { progs = Programs.Load(); } catch { }
            Tray.Invoke(() => RebuildWith(progs));
        });
    }

    private void RebuildWith(List<Programs.Entry> progs)
    {
        var list = new List<Item> { new Item { Name = Lang.T("אוצריא", "Otzaria"), Running = true, Icon = IconOf(Kiosk.OtzariaPath()) } };
        try { foreach (var e in progs ?? new List<Programs.Entry>()) list.Add(new Item { Name = e.Name, Entry = e, Icon = IconOf(e.Path) }); } catch { }
        foreach (var old in _items) old.Icon?.Dispose();
        _items = list;
        if (_items.Count <= 1) { Hide(); return; }   // אין תוכנות מותרות — אין מה להציג
        Place();
        if (!Visible) Show();
        _tick.Start();
        Scan();
    }

    /// <summary>האייקון המקורי של קובץ ההפעלה (מה-exe עצמו); null אם אין קובץ או שהשליפה נכשלה</summary>
    private static Bitmap IconOf(string path)
    {
        try
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            using var ic = Icon.ExtractAssociatedIcon(path);
            return ic?.ToBitmap();
        }
        catch (Exception ex) { Config.Log("sidebar icon: " + ex.Message); return null; }
    }

    private void Place()
    {
        var b = Screen.PrimaryScreen!.Bounds;
        _edge = Config.Get("kiosk.sideBarPosition", "right");
        _horizontal = _edge is "top" or "bottom";
        int x, y, w, h;
        if (_horizontal)
        {
            w = _items.Count * ColW + (int)(16 * S);
            h = _expanded ? FullH : StripW;
            x = b.Left + (b.Width - w) / 2;
            y = _edge == "top" ? b.Top : b.Bottom - h;
        }
        else
        {
            h = _items.Count * RowH + (int)(16 * S);
            w = _expanded ? FullW : StripW;
            x = _edge == "left" ? b.Left : b.Right - w;
            y = b.Top + (b.Height - h) / 2;
        }
        SetBounds(x, y, w, h);
        Region = new Region(Overlay.Round(new Rectangle(0, 0, Width, Height), (int)(10 * S)));
        Invalidate();
        _flyout?.Hide();   // הגיאומטריה השתנתה (כיווץ/הרחבה או שינוי מיקום) — הרשימה הצפה נפתחת מחדש אם עדיין רלוונטית
    }

    private void Step()
    {
        bool overBar = Bounds.Contains(Cursor.Position);
        bool overFlyout = _flyout is { Visible: true } && _flyout.Bounds.Contains(Cursor.Position);
        bool want = overBar || overFlyout;
        if (want != _expanded) { _expanded = want; Place(); }
        if (!want) _flyout?.Hide();
        else if (overBar) UpdateFlyout(HitTest(PointToClient(Cursor.Position)));
        if ((DateTime.UtcNow - _lastScan).TotalSeconds >= 2) Scan();
    }

    /// <summary>מציג/מסתיר את רשימת החלונות הצפה לפי השורה שמעליה העכבר כרגע (רק לתוכנה עם 2+ חלונות פתוחים)</summary>
    private void UpdateFlyout(int i)
    {
        var it = i >= 0 && i < _items.Count ? _items[i] : null;
        if (it?.Entry == null || it.Windows.Count < 2) { _flyout?.Hide(); return; }
        _flyout ??= new Flyout(h =>
        {
            _flyout.Hide();
            Task.Run(() => { try { Kiosk.BringToFront(h); } catch (Exception ex) { Config.Log("sidebar flyout: " + ex.Message); } });
        });
        _flyout.ShowFor(it.Windows, RectangleToScreen(RowRect(i)), _horizontal, _edge, S);
    }

    private void Scan()
    {
        _lastScan = DateTime.UtcNow;
        if (Interlocked.Exchange(ref _scanBusy, 1) == 1) return;
        var snapshot = _items.ToList();
        Task.Run(() =>
        {
            try
            {
                foreach (var it in snapshot.Where(i => i.Entry != null))
                {
                    var wins = Programs.ExistingWindows(it.Entry);
                    it.Windows = wins;
                    it.Running = wins.Count > 0;
                }
            }
            catch { }
            finally { Interlocked.Exchange(ref _scanBusy, 0); }
            try { BeginInvoke(new Action(Invalidate)); } catch { }
        });
    }

    /// <summary>מלבן השורה/העמודה של פריט i, בקואורדינטות הטופס — לפי כיוון הסרגל</summary>
    private Rectangle RowRect(int i)
    {
        var r = ClientRectangle;
        return _horizontal
            ? new Rectangle((int)(8 * S) + i * ColW, (int)(6 * S), ColW - (int)(4 * S), r.Height - (int)(12 * S))
            : new Rectangle((int)(6 * S), (int)(8 * S) + i * RowH, r.Width - (int)(12 * S), RowH - (int)(4 * S));
    }

    private int HitTest(Point p)
    {
        if (!_expanded) return -1;
        int i = _horizontal ? (p.X - (int)(8 * S)) / ColW : (p.Y - (int)(8 * S)) / RowH;
        return i >= 0 && i < _items.Count ? i : -1;
    }

    private void Click_(int i)
    {
        if (i < 0 || i >= _items.Count) return;
        var it = _items[i];
        Task.Run(() =>
        {
            try
            {
                if (it.Entry == null) Overlay.BringOtzaria(IntPtr.Zero);
                else Programs.Activate(it.Entry);
            }
            catch (Exception ex) { Config.Log("sidebar: " + ex.Message); }
        });
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        var r = ClientRectangle;
        if (!_expanded)
        {
            using var grip = new SolidBrush(Color.FromArgb(120, 150, 190));
            int thin = (int)(4 * S), thick = (int)(36 * S);
            // אנכי: פס אחיזה זקוף במרכז; אופקי: אותו פס מסובב ב-90 מעלות (שוכב)
            var gr = _horizontal
                ? new Rectangle((r.Width - thick) / 2, (r.Height - thin) / 2, thick, thin)
                : new Rectangle((r.Width - thin) / 2, (r.Height - thick) / 2, thin, thick);
            g.FillRectangle(grip, gr);
            return;
        }
        using var f = new Font("Segoe UI", 11f, GraphicsUnit.Point);
        var rtl = new StringFormat(StringFormatFlags.DirectionRightToLeft) { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter };
        for (int i = 0; i < _items.Count; i++) DrawRow(g, f, rtl, i);
    }

    private void DrawRow(Graphics g, Font f, StringFormat rtl, int i)
    {
        var row = RowRect(i);
        if (i == _hover)
            using (var hb = new SolidBrush(Color.FromArgb(70, 100, 150, 220))) g.FillRectangle(hb, row);
        var it = _items[i];
        int isz = (int)(30 * S);
        Rectangle ir;
        if (_horizontal)
            // אופקי: האייקון במרכז העמודה, קרוב לראש העמודה; השם (אם מוצג) מתחתיו
            ir = new Rectangle(row.Left + (row.Width - isz) / 2, row.Top + (int)(4 * S), isz, isz);
        else
            // אנכי: ימין לשמאל — האייקון בצד הימני של השורה, השם משמאלו
            ir = IconsOnly
                ? new Rectangle(row.Left + (row.Width - isz) / 2, row.Top + (row.Height - isz) / 2, isz, isz)
                : new Rectangle(row.Right - isz - (int)(8 * S), row.Top + (row.Height - isz) / 2, isz, isz);
        if (it.Icon != null)
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(it.Icon, ir);
        }
        else
        {
            // אין אייקון: עיגול עם האות הראשונה
            using var ib = new SolidBrush(Color.FromArgb(70, 90, 120));
            g.FillEllipse(ib, ir);
            using var lf = new Font("Segoe UI", 12f, FontStyle.Bold, GraphicsUnit.Point);
            var cf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(string.IsNullOrEmpty(it.Name) ? "?" : it.Name.Substring(0, 1), lf, Brushes.White, ir, cf);
        }
        // נקודת "פתוח" קטנה בפינת האייקון; כשיש 2+ חלונות פתוחים — עם מספר החלונות בתוכה
        bool multi = it.Windows.Count >= 2;
        int ds = (int)((multi ? 13 : 10) * S);
        var dot = new Rectangle(ir.Left - ds / 3, ir.Bottom - ds + ds / 3, ds, ds);
        using (var ring = new SolidBrush(BackColor)) g.FillEllipse(ring, Rectangle.Inflate(dot, (int)(2 * S), (int)(2 * S)));
        using (var db = new SolidBrush(it.Running ? Color.FromArgb(80, 200, 120) : Color.FromArgb(110, 120, 135))) g.FillEllipse(db, dot);
        if (multi)
        {
            using var df = new Font("Segoe UI", 7f, FontStyle.Bold, GraphicsUnit.Point);
            var cf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(Math.Min(it.Windows.Count, 9).ToString(), df, Brushes.White, dot, cf);
        }
        if (!IconsOnly)
        {
            RectangleF tr; StringFormat fmt;
            if (_horizontal)
            {
                tr = new RectangleF(row.Left + 2 * S, ir.Bottom + 3 * S, row.Width - 4 * S, row.Bottom - ir.Bottom - 3 * S);
                fmt = new StringFormat(StringFormatFlags.DirectionRightToLeft) { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Near, Trimming = StringTrimming.EllipsisCharacter };
            }
            else
            {
                tr = new RectangleF(row.Left + 6 * S, row.Top, ir.Left - row.Left - 14 * S, row.Height);
                fmt = rtl;
            }
            g.DrawString(it.Name, f, Brushes.White, tr, fmt);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _tick.Dispose(); foreach (var it in _items) it.Icon?.Dispose(); _flyout?.Dispose(); }
        base.Dispose(disposing);
    }

    /// <summary>
    /// תת-רשימה צפה שמופיעה ליד שורת תוכנה עם כמה חלונות פתוחים (למשל כמה מסמכי Word): כל שורה היא חלון,
    /// ולחיצה עליה מביאה אותו לחזית. תחליף בטוח ל-Alt+Tab, שחסום בכוונה בזמן נעילה: מציג רק חלונות של
    /// תוכנות מהרשימה המותרת, ולא את כל חלונות המערכת.
    /// </summary>
    private sealed class Flyout : Form
    {
        public List<(IntPtr Handle, string Title)> Windows = new();
        private int _hover = -1;
        private float _s = 1f;
        private readonly Action<IntPtr> _activate;

        public Flyout(Action<IntPtr> activate)
        {
            _activate = activate;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            DoubleBuffered = true;
            BackColor = Color.FromArgb(24, 30, 40);
            Opacity = 0.94;
            RightToLeft = RightToLeft.Yes;
            MouseMove += (_, e) => { _hover = RowAt(e.Location); Invalidate(); };
            MouseLeave += (_, _) => { _hover = -1; Invalidate(); };
            MouseClick += (_, e) =>
            {
                int i = RowAt(e.Location);
                if (i >= 0 && i < Windows.Count) _activate(Windows[i].Handle);
            };
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x08000000 /* NOACTIVATE */ | 0x00000080 /* TOOLWINDOW */ | 0x00000008 /* TOPMOST */;
                return cp;
            }
        }

        private int RowH => (int)(34 * _s);

        private int RowAt(Point p)
        {
            int i = (p.Y - (int)(6 * _s)) / RowH;
            return i >= 0 && i < Windows.Count ? i : -1;
        }

        /// <summary>ממקם את הרשימה ליד השורה שבסרגל (anchorScreen, בקואורדינטות מסך), בצד הפנימי של המסך,
        /// ולא חורג מגבולות המסך</summary>
        public void ShowFor(List<(IntPtr Handle, string Title)> windows, Rectangle anchorScreen, bool horizontalBar, string edge, float scale)
        {
            Windows = windows;
            _hover = -1;
            _s = scale;
            var sb = Screen.PrimaryScreen!.Bounds;
            int w = (int)(220 * _s);
            int h = windows.Count * RowH + (int)(12 * _s);
            int x, y;
            if (horizontalBar)
            {
                x = anchorScreen.Left;
                y = edge == "top" ? anchorScreen.Bottom + (int)(4 * _s) : anchorScreen.Top - h - (int)(4 * _s);
            }
            else
            {
                x = edge == "left" ? anchorScreen.Right + (int)(4 * _s) : anchorScreen.Left - w - (int)(4 * _s);
                y = anchorScreen.Top;
            }
            x = Math.Max(sb.Left, Math.Min(x, sb.Right - w));
            y = Math.Max(sb.Top, Math.Min(y, sb.Bottom - h));
            SetBounds(x, y, w, h);
            Region = new Region(Overlay.Round(new Rectangle(0, 0, Width, Height), (int)(8 * _s)));
            if (!Visible) Show();
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            using var f = new Font("Segoe UI", 10f, GraphicsUnit.Point);
            var rtl = new StringFormat(StringFormatFlags.DirectionRightToLeft) { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter };
            for (int i = 0; i < Windows.Count; i++)
            {
                var row = new Rectangle((int)(4 * _s), (int)(6 * _s) + i * RowH, Width - (int)(8 * _s), RowH - (int)(2 * _s));
                if (i == _hover)
                    using (var hb = new SolidBrush(Color.FromArgb(70, 100, 150, 220))) g.FillRectangle(hb, row);
                var tr = new RectangleF(row.Left + 8 * _s, row.Top, row.Width - 16 * _s, row.Height);
                string title = string.IsNullOrWhiteSpace(Windows[i].Title) ? Lang.T("(ללא כותרת)", "(untitled)") : Windows[i].Title;
                g.DrawString(title, f, Brushes.White, tr, rtl);
            }
        }
    }
}

/// <summary>חלון יומן שינויים מקומי (CHANGELOG מוטמע ב-exe; לא צריך רשת) — כל הגרסאות</summary>
internal sealed class ChangelogForm : Form
{
    private static ChangelogForm _open;

    public static void Open()
    {
        if (_open is { IsDisposed: false }) { _open.Activate(); return; }
        _open = new ChangelogForm();
        _open.Show();
    }

    public static string LoadText()
    {
        try
        {
            using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("CHANGELOG.md");
            if (s == null) return Lang.T("יומן השינויים לא נמצא בקובץ.", "Changelog not found in this build.");
            using var rd = new StreamReader(s, Encoding.UTF8);
            return rd.ReadToEnd();
        }
        catch (Exception ex) { return ex.Message; }
    }

    private ChangelogForm()
    {
        Text = Lang.T("יומן שינויים — " + Program.AppName, "Changelog — " + Program.AppNameEn);
        Icon = Overlay.AppIcon();
        RightToLeft = Lang.En ? RightToLeft.No : RightToLeft.Yes;
        RightToLeftLayout = !Lang.En;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 10f);
        ClientSize = new Size(760, 600);

        string text = LoadText();
        var versions = Regex.Matches(text, @"^## (.+)$", RegexOptions.Multiline).Select(m => m.Groups[1].Value.Trim()).ToList();
        var box = new RichTextBox { Dock = DockStyle.Fill, ReadOnly = true, BackColor = Color.White, BorderStyle = BorderStyle.None, RightToLeft = RightToLeft.Yes, Font = new Font("Segoe UI", 10.5f) };
        var pick = new ComboBox { Dock = DockStyle.Top, DropDownStyle = ComboBoxStyle.DropDownList };
        pick.Items.Add(Lang.T("קפיצה לגרסה…", "Jump to version…"));
        foreach (var v in versions) pick.Items.Add(v);
        pick.SelectedIndex = 0;
        Controls.Add(box);
        Controls.Add(pick);

        foreach (string raw in text.Replace("\r", "").Split('\n'))
        {
            string line = raw;
            Font f = box.Font; Color c = Color.Black;
            if (line.StartsWith("## ")) { line = line[3..]; f = new Font("Segoe UI Semibold", 14f); c = Color.FromArgb(30, 80, 160); }
            else if (line.StartsWith("### ")) { line = line[4..]; f = new Font("Segoe UI Semibold", 11.5f); }
            else if (line.StartsWith("# ")) { line = line[2..]; f = new Font("Segoe UI Semibold", 17f); }
            else if (line.TrimStart().StartsWith("- ")) line = "• " + line.TrimStart()[2..];
            else if (line.Trim() == "---") continue;
            line = line.Replace("**", "").Replace("`", "");
            box.SelectionFont = f; box.SelectionColor = c;
            box.AppendText(line + "\n");
        }
        pick.SelectedIndexChanged += (_, _) =>
        {
            if (pick.SelectedIndex <= 0) return;
            int i = box.Text.IndexOf(versions[pick.SelectedIndex - 1].Replace("**", "").Replace("`", ""), StringComparison.Ordinal);
            if (i >= 0) { box.SelectionStart = i; box.SelectionLength = 0; box.ScrollToCaret(); }
        };
        box.SelectionStart = 0; box.ScrollToCaret();
    }
}

/// <summary>חלון קוד שחזור (Ctrl+Alt+Shift+F12) לביטול חירום של הנעילה</summary>
internal sealed class EmergencyForm : Form
{
    private static EmergencyForm _open;

    public static void Open()
    {
        // לא רק Kiosk.IsActive (דגל מקומי ב-config.json): כשרכיב ההקשחה מותקן הוא מקור האמת למצב
        // הנעילה, ועלול לאכוף נעילה גם כש-config.json המקומי לא מסונכרן (למשל אחרי קריסה/הפעלה
        // מחדש) - חלון ביטול החירום חייב להיות זמין בכל מצב שבו המשתמש בפועל נעול, לא רק לפי הדגל המקומי
        if (!Kiosk.IsActive && !HardLock.ServiceActive)
        {
            Config.Log("emergency: הצירוף זוהה אבל הנעילה אינה פעילה (מקומי וגם שירות) - החלון לא נפתח");
            return;
        }
        Config.Log("emergency: פותח חלון קוד שחזור");
        if (_open is { IsDisposed: false }) { _open.Activate(); return; }
        // מסך המנוחה (מסך מלא TopMost) היה מכסה את החלון: מסתירים אותו, ו-ShowRest לא יציג אותו שוב כל עוד החלון פתוח
        Overlay.HideRest();
        _open = new EmergencyForm();
        _open.Show();
        Kiosk.BringToFront(_open.Handle);
        // BringToFront מחזיר ל-NOTOPMOST; מחזירים TopMost כדי שלא יסתתר מאחורי שכבות הנעילה
        _open.TopMost = true;
    }

    public static bool IsOpen => _open is { IsDisposed: false, Visible: true };

    private EmergencyForm()
    {
        Text = Lang.T("ביטול חירום של הנעילה", "Emergency unlock");
        Icon = Overlay.AppIcon();
        TopMost = true;
        RightToLeft = Lang.En ? RightToLeft.No : RightToLeft.Yes;
        RightToLeftLayout = !Lang.En;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 10.5f);
        ClientSize = new Size(420, 190);
        var info = new Label { Text = Lang.T("הזינו את קוד השחזור (12 תווים) כדי לבטל את הנעילה כולה.", "Enter the 12-character recovery code to turn off lock mode completely."), Dock = DockStyle.Top, Height = 56, Padding = new Padding(12, 12, 12, 0) };
        var code = new TextBox { Dock = DockStyle.Top, Font = new Font("Consolas", 14f), TextAlign = HorizontalAlignment.Center, CharacterCasing = CharacterCasing.Upper, RightToLeft = RightToLeft.No };
        var msg = new Label { Dock = DockStyle.Top, Height = 28, ForeColor = Color.Firebrick, TextAlign = ContentAlignment.MiddleCenter };
        var ok = new Button { Text = Lang.T("בטל נעילה", "Unlock"), Dock = DockStyle.Bottom, Height = 40 };
        Controls.AddRange(new Control[] { ok, msg, code, info });
        AcceptButton = ok;
        ok.Click += (_, _) =>
        {
            if (Kiosk.EmergencyRelease(code.Text)) { Close(); return; }
            msg.Text = Lang.T("הקוד שגוי", "Wrong code");
            code.SelectAll();
        };
        Shown += (_, _) => code.Focus();
    }
}

/// <summary>
/// איסוף יומנים לדיווח באג. שום דבר לא נשלח אוטומטית: נוצר קובץ zip בשולחן העבודה (בלי סיסמאות ומפתחות),
/// ונפתח בדפדפן דף "בעיה חדשה" ב-GitHub עם פרטים בסיסיים. המשתמשת מצרפת את הקובץ בגרירה ושולחת.
/// </summary>
internal static class Diagnostics_
{
    public const string IssuesUrl = "https://github.com/YairDaniel11/otzaria-system-tools/issues/new";
    private static readonly Regex Secret = new("(hash|salt|key|secret|password|token|recovery|client|auth)", RegexOptions.IgnoreCase);

    public static void Report()
    {
        if (Kiosk.IsActive)
        {
            MessageBox.Show(Lang.T("בזמן נעילה אי אפשר לפתוח דיווח (הוא פותח דפדפן). בטלו את הנעילה ונסו שוב.", "Reporting is unavailable while lock mode is on (it opens a browser). Turn off lock mode and try again."),
                Lang.T("דיווח באג", "Report a bug"));
            return;
        }
        string consent = Lang.T(
            "ייווצר קובץ zip בשולחן העבודה עם יומן הגשר והגדרות (בלי סיסמאות, קודים ומפתחות), ויפתח דף דיווח ב-GitHub.\nשום דבר לא נשלח אוטומטית: אתם מצרפים את הקובץ ושולחים בעצמכם.\n\nלהמשיך?",
            "A zip with the bridge log and settings (no passwords, codes or keys) will be created on your desktop, and a GitHub issue page will open.\nNothing is sent automatically: you attach the file and submit it yourself.\n\nContinue?");
        if (MessageBox.Show(consent, Lang.T("דיווח באג", "Report a bug"), MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button1, Lang.En ? 0 : MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign) != DialogResult.Yes) return;
        try
        {
            string zip = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                "otzaria-bridge-report-" + DateTime.Now.ToString("yyyyMMdd-HHmm") + ".zip");
            using (var z = ZipFile.Open(zip, ZipArchiveMode.Create))
            {
                string log = Path.Combine(Config.Dir, "bridge.log");
                if (File.Exists(log))
                {
                    using var fs = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    long keep = 600_000; if (fs.Length > keep) fs.Seek(-keep, SeekOrigin.End);
                    using var entry = z.CreateEntry("bridge.log").Open();
                    fs.CopyTo(entry);
                }
                AddText(z, "config.sanitized.json", Sanitize(File.Exists(Config.FilePath) ? File.ReadAllText(Config.FilePath) : "{}"));
                AddText(z, "info.txt", Info());
            }
            Process.Start(new ProcessStartInfo(IssuesUrl + "?title=" + Uri.EscapeDataString("דיווח מהגשר " + Program.Version) +
                "&body=" + Uri.EscapeDataString("**מה קרה?**\n\n\n**מה ציפיתי שיקרה?**\n\n\n(צרפו כאן את קובץ ה-zip מהשולחן העבודה בגרירה)\n\n" + Info())) { UseShellExecute = true });
            Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + zip + "\"") { UseShellExecute = true });
        }
        catch (Exception ex) { MessageBox.Show(ex.Message); }
    }

    private static void AddText(ZipArchive z, string name, string text)
    {
        using var w = new StreamWriter(z.CreateEntry(name).Open(), new UTF8Encoding(false));
        w.Write(text);
    }

    private static string Info() =>
        "גרסת גשר: " + Program.Version + "\nמערכת: " + Environment.OSVersion + "\nנעילה פעילה: " + Kiosk.IsActive +
        "\nשפת תצוגה: " + (Lang.En ? "en" : "he") + "\n";

    /// <summary>מסיר כל ערך שהמפתח שלו נראה כמו סוד</summary>
    private static string Sanitize(string json)
    {
        try
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(json);
            Strip(node);
            // TypeInfoResolver מפורש הכרחי - ר' הערה ב-HardLock.cs.Save
            return node?.ToJsonString(new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
            }) ?? "{}";
        }
        catch { return "{}"; }
    }

    private static void Strip(System.Text.Json.Nodes.JsonNode n)
    {
        if (n is System.Text.Json.Nodes.JsonObject o)
        {
            foreach (var k in o.Select(x => x.Key).ToList())
            {
                if (Secret.IsMatch(k)) o[k] = "***"; else Strip(o[k]);
            }
        }
        else if (n is System.Text.Json.Nodes.JsonArray a) foreach (var x in a) Strip(x);
    }
}
