using System;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace OtzariaBridge;

/// <summary>משוב 15.10: טאב "כלים" בחלון הראשי — מה שהיה פריטים נפרדים במגש (צילום מסך, תוספים, שפה)</summary>
internal sealed class ToolsForm : Form
{
    public static void Open() => Hub.Show("tools", () => new ToolsForm());

    private ToolsForm()
    {
        Text = Lang.T("כלים", "Tools");
        RightToLeft = Lang.En ? RightToLeft.No : RightToLeft.Yes;
        BackColor = Color.FromArgb(250, 247, 240);
        // רשת של כרטיסים ברוחב שווה (2 עמודות), בסגנון Material
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(18), AutoScroll = true, BackColor = BackColor };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        int n = 0;
        void B(string glyph, string he, string en, string subHe, string subEn, Action a)
        {
            var c = new ToolCard(glyph, Lang.T(he, en), Lang.T(subHe, subEn)) { Dock = DockStyle.Fill, Margin = new Padding(6), Height = 112 };
            c.Click += (_, _) => { try { a(); } catch (Exception ex) { MessageBox.Show(ex.Message, Program.AppName); } };
            if (n % 2 == 0) { grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 124)); }
            grid.Controls.Add(c, n % 2, n / 2);
            n++;
        }
        B("", "צילום מסך", "Screenshot", "Ctrl+Shift+F11", "Ctrl+Shift+F11", () => Shot(false));
        B("", "צילום אזור", "Region screenshot", "Ctrl+Shift+F12", "Ctrl+Shift+F12", () => Shot(true));
        B("", "התקנת תוספים", "Install plugins", "התקן/עדכן את התוספים באוצריא", "Install/update the plugins in Otzaria", () => { Installer.DeployPlugins(); Installer.InstallPluginsInOtzaria(); });
        B("", "תיקיית התוספים", "Plugins folder", "פתח את תיקיית התוספים", "Open the plugins folder", Installer.OpenPluginsFolder);
        B("", Lang.En ? "עברית" : "English", Lang.En ? "עברית" : "English", "החלפת שפת הממשק", "Switch interface language", () => { Lang.Set(Lang.En ? "he" : "en"); MessageBox.Show(Lang.T("השפה תתעדכן בפתיחה הבאה של החלון.", "The language will update next time the window opens."), Program.AppName); });
        Controls.Add(grid);
    }

    /// <summary>כרטיס פעולה: רקע לבן מעוגל, אייקון בעיגול צבעוני, כותרת ושורת הסבר; מגיב ל-hover ולחיצה</summary>
    private sealed class ToolCard : Control
    {
        private readonly string _glyph, _title, _sub;
        private bool _hot, _down;

        public ToolCard(string glyph, string title, string sub)
        {
            _glyph = glyph; _title = title; _sub = sub;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            Cursor = Cursors.Hand; TabStop = true;
            MouseEnter += (_, _) => { _hot = true; Invalidate(); };
            MouseLeave += (_, _) => { _hot = _down = false; Invalidate(); };
            MouseDown += (_, _) => { _down = true; Invalidate(); };
            MouseUp += (_, _) => { _down = false; Invalidate(); };
            KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space) OnClick(EventArgs.Empty); };
            GotFocus += (_, _) => Invalidate(); LostFocus += (_, _) => Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            g.Clear(Parent?.BackColor ?? SystemColors.Control);
            bool rtl = RightToLeft == RightToLeft.Yes;
            var r = new Rectangle(1, 1, Width - 3, Height - 3);
            var accent = Color.FromArgb(184, 134, 11);
            using var path = Round(r, 14);
            using (var fill = new SolidBrush(_down ? Color.FromArgb(250, 240, 214) : _hot ? Color.FromArgb(255, 251, 240) : Color.White)) g.FillPath(fill, path);
            using (var pen = new Pen(_hot || Focused ? accent : Color.FromArgb(222, 225, 232), _hot || Focused ? 1.6f : 1f)) g.DrawPath(pen, path);

            int d = 46, pad = 16;
            var ic = new Rectangle(rtl ? r.Right - pad - d : r.Left + pad, r.Top + (r.Height - d) / 2, d, d);
            using (var b = new SolidBrush(Color.FromArgb(_hot ? 40 : 26, accent))) g.FillEllipse(b, ic);
            string fam = FontFamily("Segoe Fluent Icons", "Segoe MDL2 Assets");
            using (var f = new Font(fam, 18f))
            using (var b = new SolidBrush(accent))
            using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                g.DrawString(_glyph, f, b, ic, sf);

            int tx = rtl ? r.Left + pad : ic.Right + 14;
            var tr = rtl ? new Rectangle(tx, r.Top, ic.Left - 14 - tx, r.Height) : new Rectangle(tx, r.Top, r.Right - pad - tx, r.Height);
            var fmt = new StringFormat { Alignment = rtl ? StringAlignment.Far : StringAlignment.Near, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter };
            if (rtl) fmt.FormatFlags |= StringFormatFlags.DirectionRightToLeft;
            using var tf = new Font("Segoe UI Semibold", 11.5f);
            using var sfnt = new Font("Segoe UI", 9f);
            int th = tf.Height, sh = sfnt.Height * 2;
            int y = r.Top + (r.Height - th - sh) / 2;
            using (var b = new SolidBrush(Color.FromArgb(122, 84, 14))) g.DrawString(_title, tf, b, new Rectangle(tr.X, y, tr.Width, th), fmt);
            using (var b = new SolidBrush(Color.FromArgb(150, 115, 50))) g.DrawString(_sub, sfnt, b, new Rectangle(tr.X, y + th + 2, tr.Width, sh), fmt);
        }

        private static string FontFamily(params string[] names)
        {
            foreach (var n in names)
                foreach (var f in System.Drawing.FontFamily.Families) if (f.Name == n) return n;
            return "Segoe UI Symbol";
        }

        private static System.Drawing.Drawing2D.GraphicsPath Round(Rectangle r, int rad)
        {
            int d = rad * 2;
            var p = new System.Drawing.Drawing2D.GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure(); return p;
        }
    }

    /// <summary>מסתירים את החלון לרגע כדי שלא ייכנס לצילום</summary>
    private void Shot(bool region)
    {
        var top = TopLevelControl as Form;
        top?.Hide();
        SafeTask.Run(() =>
        {
            Thread.Sleep(300);
            try { Tray.Invoke(() => { try { Screenshot.Capture(region); } finally { top?.Show(); } }); }
            catch { Tray.Invoke(() => top?.Show()); }
        });
    }
}
