// סבב D: D2 — שומר מסך גם בלי מצב נעילה (ברירת מחדל: מופעל); D3 — כיסוי מיידי בעלייה עד שאוצריא מוכנה.
// בנוי רק על מסך המנוחה הקיים (Overlay.ShowRest) — בלי החלפת shell ובלי לחסום קלט: לחיצה/מקש סוגרים,
// Ctrl+Alt+Shift+F12 נשאר, והכיסוי בעלייה נסגר לבד אחרי 60 שניות גם אם אוצריא לא עלתה (זהירות מ-#8).
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace OtzariaBridge;

public static class Saver
{
    private static System.Threading.Timer _t;
    private static bool _shownByUs;
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();

    public static void Start()
    {
        try { BootCover(); } catch (Exception ex) { Config.Log("boot cover: " + ex.Message); }
        _t ??= new System.Threading.Timer(_ => Tick(), null, 5000, 5000);
    }

    private static void Tick()
    {
        try
        {
            if (Kiosk.IsActive) { _shownByUs = false; return; } // בנעילה: Kiosk.Watch מנהל את המסך
            if (_shownByUs && !Overlay.RestVisible) _shownByUs = false;
            if (!Config.Get("kiosk.saverEnabled", true) || !Config.Get("kiosk.saverWhenUnlocked", true)) return;
            int min = Config.Get("kiosk.restIdleMinutes", 1);
            if (min <= 0 || Overlay.RestVisible) return;
            if (Kiosk.IdleMs() < min * 60000L) return;
            // לחיצה חוזרת לחלון שהיה בחזית (לא בהכרח אוצריא — הנעילה לא פעילה)
            Overlay.ShowRest(GetForegroundWindow(), idle: true);
            _shownByUs = true;
        }
        catch (Exception ex) { Config.Log("saver: " + ex.Message); }
    }

    /// <summary>D3: בעלייה במצב נעילה — מסך המנוחה מיד, עד שלאוצריא יש חלון (Kiosk.Watch מסתיר אותו אז), או 60 שניות.</summary>
    private static void BootCover()
    {
        // #8: אותם תנאים בדיוק כמו BootGuard — רק נעילה מאושרת, ולעולם לא במצב בטוח
        if (System.Windows.Forms.SystemInformation.BootMode != System.Windows.Forms.BootMode.Normal) return;
        if (!Kiosk.IsActive || !Config.Get("kiosk.confirmed", false) || !Config.Get("kiosk.bootCover", true)) return;
        // #8: מתג הגנה — אם הכיסוי בעלייה הקודמת לא הסתיים (קריסה/כיבוי באמצעו), מכבים אותו לצמיתות
        if (Config.Get("kiosk.bootCoverRunning", false))
        {
            Config.Set("kiosk.bootCover", false); Config.Set("kiosk.bootCoverRunning", false); Config.Save();
            Config.Log("boot cover: לא הסתיים בעלייה הקודמת — כובה אוטומטית (הגנה מ-#8)");
            return;
        }
        // אוצריא חייבת להיות קיימת, אחרת אין למה לחכות
        string otzPath = Kiosk.OtzariaPath();
        if (string.IsNullOrEmpty(otzPath) || !File.Exists(otzPath)) return;
        if (Environment.TickCount64 > 5 * 60_000) return; // רק סמוך להדלקה, לא בהפעלה ידנית של הגשר
        if (OtzariaWindow() != IntPtr.Zero) return;
        Config.Set("kiosk.bootCoverRunning", true); Config.Save();
        Overlay.ShowRest(IntPtr.Zero);
        new Thread(() =>
        {
            try { for (int i = 0; i < 120 && OtzariaWindow() == IntPtr.Zero; i++) Thread.Sleep(500); Thread.Sleep(1500); }
            catch { }
            finally
            {
                // תמיד נסגר — גם בשגיאה. הכיסוי לא נוגע ב-shell/explorer ולא חוסם קלט; לחיצה/מקש סוגרים אותו מיד
                try { Overlay.HideRest(); } catch { }
                try { Config.Set("kiosk.bootCoverRunning", false); Config.Save(); } catch { }
            }
        }) { IsBackground = true }.Start();
    }

    private static IntPtr OtzariaWindow()
    {
        string path = Kiosk.OtzariaPath();
        string name = string.IsNullOrEmpty(path) ? "otzaria" : Path.GetFileNameWithoutExtension(path);
        return Process.GetProcessesByName(name).Select(p => p.MainWindowHandle).FirstOrDefault(h => h != IntPtr.Zero);
    }
}
