using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
namespace OtzariaBridge;

/// <summary>#60: צילום מסך מהגשר — עובד גם בנעילה (קיצורי Windows חסומים). פריט במגש + Ctrl+Shift+F11 (מסך מלא),
/// Ctrl+Shift+F12 (אזור). נשמר ל"צילומי מסך" בשולחן העבודה, או בתיקייה המותרת הראשונה כשהסייר מוגבל.</summary>
public static class Screenshot
{
    private const int IdFull = 0x6A01, IdRegion = 0x6A02, IdRescue = 0x6A03, MOD_ALT_ = 1, WM_HOTKEY = 0x312;
    private const uint MOD_CONTROL = 2, MOD_SHIFT = 4, MOD_NOREPEAT = 0x4000;
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr h, int id, uint mod, uint vk);
    private static HotkeyWindow _win;

    public static void Init()
    {
        try
        {
            _win = new HotkeyWindow();
            bool a = RegisterHotKey(_win.Handle, IdFull, MOD_CONTROL | MOD_SHIFT | MOD_NOREPEAT, (uint)Keys.F11);
            bool b = RegisterHotKey(_win.Handle, IdRegion, MOD_CONTROL | MOD_SHIFT | MOD_NOREPEAT, (uint)Keys.F12);
            // #8: Ctrl+Alt+Shift+F12 כשהנעילה כבויה — שחזור הסייר (בנעילה פעילה ה-hook תופס לפני)
            RegisterHotKey(_win.Handle, IdRescue, MOD_CONTROL | MOD_SHIFT | MOD_ALT_ | MOD_NOREPEAT, (uint)Keys.F12);
            if (!a || !b) Config.Log("screenshot: רישום קיצור נכשל (תפוס?) full=" + a + " region=" + b);
        }
        catch (Exception ex) { Config.Log("screenshot init: " + ex.Message); }
    }

    public static string Folder()
    {
        string desk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Lang.T("צילומי מסך", "Screenshots"));
        string[] roots = null;
        try { roots = FxPolicy.SaveRootsOrNull(); } catch { }
        if (roots == null || roots.Length == 0 || PathPolicy.IsAllowed(desk, roots)) return desk;
        return Path.Combine(roots[0], Lang.T("צילומי מסך", "Screenshots"));
    }

    /// <summary>מחזיר נתיב הקובץ שנשמר, או null (ביטול/שגיאה)</summary>
    public static string Capture(bool region)
    {
        try
        {
            Rectangle vs = SystemInformation.VirtualScreen;
            using var full = new Bitmap(vs.Width, vs.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(full)) g.CopyFromScreen(vs.Location, Point.Empty, vs.Size);
            Bitmap shot = full;
            if (region)
            {
                Rectangle r;
                using (var f = new RegionForm(full, vs)) { if (f.ShowDialog() != DialogResult.OK) return null; var sel = f.Sel; if (sel.Width < 4 || sel.Height < 4) return null; r = sel; }
                shot = full.Clone(r, full.PixelFormat);
            }
            string dir = Folder();
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "screenshot-" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + ".png");
            shot.Save(path, ImageFormat.Png);
            if (!ReferenceEquals(shot, full)) shot.Dispose();
            Tray.Notify(Lang.T("צילום מסך", "Screenshot"), Lang.T("נשמר: ", "Saved: ") + path, ToolTipIcon.Info);
            return path;
        }
        catch (Exception ex) { Config.Log("screenshot: " + ex.Message); Tray.Notify(Lang.T("צילום מסך", "Screenshot"), Lang.T("הצילום נכשל", "Capture failed")); return null; }
    }

    private sealed class HotkeyWindow : NativeWindow
    {
        public HotkeyWindow() => CreateHandle(new CreateParams());
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY && (int)m.WParam == IdRescue)
            {
                if (Kiosk.RestoreShellIfUnlocked()) Tray.Notify(Lang.T("שחזור", "Restore"), Lang.T("הסייר ושולחן העבודה שוחזרו", "Explorer and desktop restored"), ToolTipIcon.Info);
                else Tray.Invoke(EmergencyForm.Open);
            }
            else if (m.Msg == WM_HOTKEY) Capture(region: (int)m.WParam == IdRegion);
            base.WndProc(ref m);
        }
    }

    private sealed class RegionForm : Form
    {
        private readonly Bitmap _img; private Point _start; private bool _drag;
        public Rectangle Sel;
        public RegionForm(Bitmap img, Rectangle vs)
        {
            _img = img; FormBorderStyle = FormBorderStyle.None; StartPosition = FormStartPosition.Manual; Bounds = vs;
            TopMost = true; ShowInTaskbar = false; DoubleBuffered = true; Cursor = Cursors.Cross; KeyPreview = true;
            KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; } };
            MouseDown += (_, e) => { _start = e.Location; _drag = true; };
            MouseMove += (_, e) => { if (_drag) { Sel = Rect(_start, e.Location); Invalidate(); } };
            MouseUp += (_, e) => { _drag = false; Sel = Rect(_start, e.Location); DialogResult = DialogResult.OK; };
        }
        private static Rectangle Rect(Point a, Point b) => Rectangle.FromLTRB(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.DrawImageUnscaled(_img, 0, 0);
            using var dim = new SolidBrush(Color.FromArgb(110, 0, 0, 0));
            using var reg = new Region(ClientRectangle); reg.Exclude(Sel);
            e.Graphics.FillRegion(dim, reg);
            if (Sel.Width > 0) using (var pen = new Pen(Color.DeepSkyBlue, 2)) e.Graphics.DrawRectangle(pen, Sel);
        }
    }
}
