using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace OtzariaBridge;

/// <summary>
/// שומר מהיר (כל 300ms, בנוסף ל-Watch שרץ כל 2 שניות): סוגר חלונות חורגים שנפתחים מתוך כלי החיתוך המאושר -
/// "שמירה בשם" (PickerHost, מאפשר לדפדף בכל המחשב), חלונית "שתף" (חלון ApplicationFrameWindow בתוך explorer),
/// תפריט התחל/חיפוש/מעבר-משימות - ומסתיר את שורת המשימות כש-explorer חי בזמן הצילום.
/// </summary>
public static partial class Kiosk
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);

    private static System.Threading.Timer _fastGuard;
    private static int _fastBusy;

    private static void StartFastGuard() => _fastGuard ??= new System.Threading.Timer(_ => FastGuard(), null, 0, 300);

    private static bool _taskbarHidden;

    private static void StopFastGuard()
    {
        _fastGuard?.Dispose();
        _fastGuard = null;
        // שחרור הנעילה חייב להחזיר את שורת המשימות שהוסתרה (אחרת היא נעלמת גם אחרי הביטול)
        if (_taskbarHidden) SetTaskbars(true);
    }

    private static void SetTaskbars(bool show)
    {
        foreach (string cls in new[] { "Shell_TrayWnd", "Shell_SecondaryTrayWnd" })
        {
            IntPtr h = IntPtr.Zero;
            while ((h = FindWindowEx(IntPtr.Zero, h, cls, null)) != IntPtr.Zero) ShowWindow(h, show ? 5 /* SW_SHOW */ : 0 /* SW_HIDE */);
        }
        _taskbarHidden = !show;
    }

    /// <summary>[w3] מחזיר את חלון אוצריא לחזית (חלופה כשאי אפשר להרוג תהליך מוגבה, וגם תגובה מהירה לתהליך לא מותר)</summary>
    private static void ReturnOtzariaToFront(int session)
    {
        try
        {
            string path = OtzariaPath();
            string on = string.IsNullOrEmpty(path) ? "otzaria" : System.IO.Path.GetFileNameWithoutExtension(path);
            foreach (var o in Process.GetProcessesByName(on))
                using (o)
                {
                    if (o.SessionId != session || o.MainWindowHandle == IntPtr.Zero) continue;
                    if (!SetForegroundWindow(o.MainWindowHandle)) SwitchToThisWindow(o.MainWindowHandle, true);
                    return;
                }
        }
        catch { }
    }

    private static void FastGuard()
    {
        if (!IsActive || Interlocked.Exchange(ref _fastBusy, 1) == 1) return;
        try
        {
            if (_snipSeen != DateTime.MinValue) SetTaskbars(false); // explorer חי בזמן הצילום - שורת המשימות לא תוצג
            else if (_taskbarHidden) SetTaskbars(true);             // הצילום הסתיים: מחזירים (explorer יעלה מחדש/יימחק כרגיל)

            IntPtr fg = GetForegroundWindow();
            if (fg == IntPtr.Zero) return;
            GetWindowThreadProcessId(fg, out uint pid);
            if (pid == 0 || pid == (uint)Environment.ProcessId) return;
            using var p = Process.GetProcessById((int)pid);
            if (p.SessionId != Process.GetCurrentProcess().SessionId) return;

            string name = p.ProcessName;
            var sb = new StringBuilder(128);
            GetClassName(fg, sb, sb.Capacity);
            string cls = sb.ToString();

            bool closeIt =
                name.Equals("PickerHost", StringComparison.OrdinalIgnoreCase) ||   // [s2] WHY: בלי תלות במחלקה (#32770 בלבד החמיץ חלונות אחרים של PickerHost)
                (name.Equals("explorer", StringComparison.OrdinalIgnoreCase) &&
                 cls is "ApplicationFrameWindow" or "XamlExplorerHostIslandWindow" or "CabinetWClass" or "#32770"); // [r1] #32770 = חלון Run (Win+R/הפעלה) של explorer, גם בזמן צילום
            if (closeIt)
            {
                PostMessage(fg, 0x0010 /* WM_CLOSE */, IntPtr.Zero, IntPtr.Zero);
                LogThrottled("fast:" + name + cls, "watchdog: נסגר חלון חסום בנעילה (" + name + " / " + cls + ")", 30);
                Tray.Notify(Program.AppName, Lang.T("החלון הזה חסום בזמן נעילה", "This window is blocked while locked"));
            }
            else if (name is "StartMenuExperienceHost" or "SearchHost")
            {
                // [w3] כשל הריגה (AccessDenied) לא נבלע: נרשם ליומן, ואוצריא מוחזרת לחזית כחלופה
                if (AppTrust.KillLogged(p, "fast-" + name))
                    LogThrottled("fast:" + name, "watchdog: " + name + " נסגר (תפריט התחל/חיפוש חסומים בנעילה)", 30);
                else ReturnOtzariaToFront(p.SessionId);
            }
            else if (!IsOtzariaPid(pid) && !(_snipSeen != DateTime.MinValue && ShellPolicy.IsSnipOverlayClass(cls)))
            {
                // [r1] בזמן צילום מאושר (עד 120 שניות) הפטור הוא רק למחלקות חלון של שכבת הצילום; כל השאר נסרק כרגיל
                // [w3] תגובה תוך 300ms (ולא 2 שניות) לכל תהליך בחזית שאינו מותר: מעטפת/LOLBin (לפי OriginalFilename ונתיב) נהרגת,
                // כל תהליך אחר שאינו ברשימה/בעץ שנפתח דרך הגשר — אוצריא חוזרת לחזית מיד.
                // לא פועלים בזמן צילום מסך (שכבת הצילום מתארחת ב-explorer) — שם נשאר Watch הרגיל.
                if (Programs.IsShellHost(p))
                {
                    if (!AppTrust.KillLogged(p, "fast-shell")) ReturnOtzariaToFront(p.SessionId);
                    else LogThrottled("fastshell:" + name, "watchdog: חלון " + name + " נסגר (זיהוי לפי זהות הקובץ)", 30);
                }
                else if (!Programs.IsAllowedForeground(pid))
                {
                    LogThrottled("fastfg:" + name, "watchdog(fast): מחזיר את אוצריא לחזית מהתהליך " + name, 60);
                    ReturnOtzariaToFront(p.SessionId);
                }
            }
        }
        catch { /* חלון/תהליך נסגר באמצע */ }
        finally { Interlocked.Exchange(ref _fastBusy, 0); }
    }
}
