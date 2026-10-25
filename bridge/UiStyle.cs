using System.Drawing.Drawing2D;
namespace OtzariaBridge;

/// <summary>עיצוב משותף לחלונות הגשר: רקע חמים, כרטיסים לבנים מעוגלים, כפתורים שטוחים בגוון זהוב</summary>
internal static class UiStyle
{
    public static readonly Color Gold = Color.FromArgb(184, 134, 11);
    public static readonly Color GoldText = Color.FromArgb(122, 84, 14);
    public static readonly Color Page = Color.FromArgb(250, 247, 240);
    public static readonly Color Line = Color.FromArgb(230, 220, 196);

    /// <summary>כפתור שטוח: מסגרת זהובה כשפעיל, אפור כשלא; primary = מלא בזהב</summary>
    public static void Button(Button b, bool primary = false)
    {
        b.FlatStyle = FlatStyle.Flat;
        b.AutoSize = true;
        b.MinimumSize = new Size(150, 38);
        b.Padding = new Padding(12, 5, 12, 5);
        b.Margin = new Padding(0, 0, 8, 8);
        b.FlatAppearance.BorderSize = 1;
        void Paint()
        {
            bool on = b.Enabled;
            b.BackColor = !on ? Color.FromArgb(232, 232, 232) : primary ? Gold : Color.White;
            b.ForeColor = !on ? Color.FromArgb(150, 150, 150) : primary ? Color.White : GoldText;
            b.FlatAppearance.BorderColor = !on ? Color.FromArgb(214, 214, 214) : Gold;
            b.Cursor = on ? Cursors.Hand : Cursors.Default;
        }
        b.EnabledChanged += (_, _) => Paint();
        Paint();
    }

    /// <summary>מיישם את הסגנון האחיד על טופס קיים: רקע חמים, כפתורים שטוחים זהובים, קישורים בצבע זהב (כפתורים שכבר עוצבו מדולגים)</summary>
    public static void Theme(Control root)
    {
        if (root is Form) root.BackColor = Page;
        foreach (Control c in root.Controls)
        {
            if (c is Button b) { if (b.FlatStyle != FlatStyle.Flat) Button(b, primary: b.DialogResult == DialogResult.OK || b.Text == "אשר"); }
            else if (c is LinkLabel l) { l.LinkColor = Gold; l.ActiveLinkColor = GoldText; l.VisitedLinkColor = Gold; }
            else if (c is Label lb && lb.BackColor == Color.White) lb.BackColor = Color.Transparent;
            else if (c is PictureBox) c.BackColor = Color.Transparent;
            if (c.BackColor == Color.White && c is Panel && c is not Card) c.BackColor = Page;
            Theme(c);
        }
    }

    /// <summary>כרטיס לבן מעוגל שעוטף פקד (או כמה) עם ריווח פנימי</summary>
    public sealed class Card : Panel
    {
        public Card()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Padding = new Padding(12);
            Margin = new Padding(0, 0, 0, 10);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent?.BackColor ?? Page);
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            using var p = new GraphicsPath(); int d = 20;
            p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            using (var b = new SolidBrush(Color.White)) g.FillPath(b, p);
            using (var pen = new Pen(Line)) g.DrawPath(pen, p);
        }
    }
}
