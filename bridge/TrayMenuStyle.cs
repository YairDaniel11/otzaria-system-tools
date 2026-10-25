using System.Drawing.Drawing2D;
namespace OtzariaBridge;

/// <summary>עיצוב מודרני לתפריט המגש: משטח לבן מעוגל, אייקון בגופן Fluent/MDL2, הדגשה רכה במעבר עכבר, קווי הפרדה דקים</summary>
internal static class TrayMenuStyle
{
    private static readonly Color Accent = Color.FromArgb(184, 134, 11);

    public static void Apply(ContextMenuStrip menu)
    {
        StyleDropDown(menu, 390);
        foreach (ToolStripItem it in menu.Items) Style(it, 390);
    }

    private static void StyleDropDown(ToolStripDropDown dd, int width)
    {
        dd.Renderer = new MenuRenderer();
        dd.Font = new Font("Segoe UI", 10.5f);
        if (dd is ToolStripDropDownMenu ddm) { ddm.ShowImageMargin = false; ddm.ShowCheckMargin = false; }
        dd.Padding = new Padding(6);
        dd.BackColor = Color.White;
        dd.Opening += (_, _) => Round(dd);
        dd.SizeChanged += (_, _) => Round(dd);
    }

    public static void Style(ToolStripItem it, int width)
    {
        if (it is ToolStripSeparator) { it.Margin = new Padding(8, 4, 8, 4); return; }
        it.AutoSize = false;
        it.Height = 36;
        it.Width = width;
        it.Padding = new Padding(10, 0, 10, 0);
        it.TextAlign = ContentAlignment.MiddleLeft;
        if (it is ToolStripMenuItem m && m.DropDownItems.Count > 0)
        {
            StyleDropDown(m.DropDown, 230);
            using var f = new Font("Segoe UI", 10.5f);
            int w = 190;
            foreach (ToolStripItem c in m.DropDownItems) w = Math.Max(w, TextRenderer.MeasureText(c.Text, f).Width + 100);
            foreach (ToolStripItem c in m.DropDownItems) Style(c, w);
        }
    }

    /// <summary>אייקון MDL2 לפריט (נשמר ב-Tag כמחרוזת glyph)</summary>
    public static void Glyph(ToolStripItem it, string glyph) => it.Tag ??= glyph;

    private static void Round(ToolStripDropDown menu)
    {
        try
        {
            using var p = new GraphicsPath();
            int d = 16; var r = new Rectangle(0, 0, menu.Width, menu.Height);
            p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            menu.Region = new Region(p);
        }
        catch { }
    }

    private static string IconFont()
    {
        foreach (var n in new[] { "Segoe Fluent Icons", "Segoe MDL2 Assets" })
            foreach (var f in FontFamily.Families) if (f.Name == n) return n;
        return "Segoe UI Symbol";
    }

    private sealed class MenuRenderer : ToolStripProfessionalRenderer
    {
        private readonly string _iconFont = IconFont();
        public MenuRenderer() : base(new Colors()) { RoundedEdges = false; }

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            e.Graphics.Clear(Color.White);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var pen = new Pen(Color.FromArgb(214, 218, 226));
            var r = new Rectangle(0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
            using var p = new GraphicsPath();
            int d = 16;
            p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            e.Graphics.DrawPath(pen, p);
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected || !e.Item.Enabled) return;
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new Rectangle(2, 1, e.Item.Width - 5, e.Item.Height - 3);
            using var p = new GraphicsPath(); int d = 12;
            p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            using var b = new SolidBrush(Color.FromArgb(30, Accent));
            g.FillPath(b, p);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            using var pen = new Pen(Color.FromArgb(230, 232, 238));
            int y = e.Item.Height / 2;
            e.Graphics.DrawLine(pen, 12, y, e.Item.Width - 12, y);
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            var it = e.Item; var g = e.Graphics;
            bool rtl = it.RightToLeft == RightToLeft.Yes || (it.Owner?.RightToLeft == RightToLeft.Yes);
            bool header = !it.Enabled && it.Owner != null && it.Owner.Items.IndexOf(it) == 0;
            var col = header ? Color.FromArgb(120, 126, 140) : it.Enabled ? Color.FromArgb(122, 84, 14) : Color.FromArgb(150, 154, 164);
            var font = header ? new Font("Segoe UI", 9f) : it.Font;
            int iconW = 34;
            var tr = rtl ? new Rectangle(14, 0, it.Width - 14 - iconW - 14, it.Height) : new Rectangle(14 + iconW, 0, it.Width - 14 - iconW - 14, it.Height);
            var fl = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | (rtl ? TextFormatFlags.Right | TextFormatFlags.RightToLeft : TextFormatFlags.Left);
            TextRenderer.DrawText(g, it.Text, font, tr, col, fl);
            if (it.Tag is string glyph && glyph.Length > 0 && !header)
            {
                using var f = new Font(_iconFont, 13f);
                var ir = rtl ? new Rectangle(it.Width - 14 - iconW, 0, iconW, it.Height) : new Rectangle(10, 0, iconW, it.Height);
                TextRenderer.DrawText(g, glyph, f, ir, it.Enabled ? Accent : col, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
            if (it is ToolStripMenuItem { Checked: true })
            {
                using var f = new Font(_iconFont, 11f);
                var cr = rtl ? new Rectangle(8, 0, 26, it.Height) : new Rectangle(it.Width - 34, 0, 26, it.Height);
                TextRenderer.DrawText(g, "", f, cr, Accent, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
            if (header) font.Dispose();
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = Color.FromArgb(120, 126, 140);
            base.OnRenderArrow(e);
        }
    }

    private sealed class Colors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Color.White;
        public override Color ImageMarginGradientBegin => Color.White;
        public override Color ImageMarginGradientMiddle => Color.White;
        public override Color ImageMarginGradientEnd => Color.White;
        public override Color MenuBorder => Color.FromArgb(214, 218, 226);
    }
}
