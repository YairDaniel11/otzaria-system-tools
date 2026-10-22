using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace OtzariaBridge;

/// <summary>
/// הקשחת מעטפת (סוכן w4): החלטות טהורות (ניתנות לבדיקה) על מקשים וחלונות, ושני תהליכונים ייעודיים עם לולאת הודעות
/// משלהם: (1) hook מקלדת/עכבר עם watchdog קצר, (2) שומר חלונות מבוסס WinEvent + סריקת EnumWindows.
/// WHY: ה-hook ישב בתהליכון ה-UI של המגש (כל עיכוב שם = Windows מסירה את ה-hook בשקט), והשומר בדק רק את חלון החזית
/// כל 2 שניות, כך שחלון מערכת/דפדפן שנפתח וננעל בחזית לשניות בודדות לא נסגר.
/// </summary>
internal static class KioskKeys
{
    internal const uint VkTab = 0x09, VkEsc = 0x1B, VkSpace = 0x20, VkF1 = 0x70, VkF4 = 0x73, VkF10 = 0x79,
        VkF8 = 0x77, VkF11 = 0x7A, VkApps = 0x5D, VkO = 0x4F, VkP = 0x50, VkLWin = 0x5B, VkRWin = 0x5C;

    // FgDialogUnsafe [r1]: בדיאלוג הקבצים בחזית, תיבת השם מכילה כרגע ערך אסור (DialogGuard) - Enter לא עובר (אחרת הדיאלוג נסגר/מנווט לפני הסריקה)
    internal readonly record struct Ctx(bool Alt, bool Ctrl, bool Shift, bool Win,
        bool FgOtzaria, bool FgAllowed, bool FgBrowser, bool FgFileDialog, bool FgDialogUnsafe = false);

    internal const uint VkReturn = 0x0D;

    /// <summary>
    /// [r1] מקשי מדיה שפותחים תוכנה/דפדפן: דפדפן (0xA6-0xAC: אחורה/קדימה/רענון/עצור/חיפוש/מועדפים/בית), דואר (0xB4), בחירת מדיה (0xB5),
    /// והפעלת תוכנה 1/2 (0xB6-0xB7). עוצמת קול והפעלה/עצירה (0xAD-0xB3) נשארים מותרים: לא פותחים כלום, וחסימתם היא false-positive.
    /// </summary>
    internal static bool IsLaunchMediaKey(uint vk) => (vk >= 0xA6 && vk <= 0xAC) || (vk >= 0xB4 && vk <= 0xB7);

    /// <summary>
    /// [r1] AltGr = Ctrl (שמולבש ע"י Windows) + Alt ימני, בלי Alt שמאלי. Ctrl+Alt+O אמיתי דורש Alt שמאלי (או Alt כללי ללא ימני בלבד),
    /// אחרת AltGr+O (אות בפריסות אירופיות) היה מחזיר לאוצריא.
    /// </summary>
    internal static bool IsReturnHotkey(uint vk, bool down, bool ctrl, bool altHeld, bool lAlt, bool rAlt) =>
        down && vk == VkO && ctrl && altHeld && !(rAlt && !lAlt);

    /// <summary>האם לבלוע את המקש. Alt נקבע ע"י הקורא כ-"דגל ה-hook או מצב אסינכרוני של MENU/LMENU/RMENU"</summary>
    internal static bool ShouldBlock(uint vk, in Ctx c)
    {
        if (vk is VkLWin or VkRWin) return true;                    // Win בפני עצמו
        if (IsLaunchMediaKey(vk)) return true;                       // מקשי דפדפן/דואר/אפליקציה בכל תוכנה
        if (vk == VkReturn && c.FgFileDialog && c.FgDialogUnsafe) return true; // Enter על ערך אסור בדיאלוג קבצים
        if (c.Win) return true;                                      // Win+כל מקש
        if (c.Alt && vk is VkTab or VkEsc or VkSpace) return true;   // Alt+Tab / Alt+Shift+Tab / Alt+Esc / Alt+Space
        // [s2] WHY: Alt+F11 (עורך VBA) / Alt+F8 (רשימת מאקרו) ב-Word/Excel = הרצת קוד חופשי שעוקפת כל רשימה. נחסמים תמיד (גם Alt+Shift+F11 - עורך סקריפטים)
        if (c.Alt && vk is VkF11 or VkF8) return true;
        if (c.Alt && vk == VkF4) return !c.FgAllowed;                // Alt+F4 מותר רק בתוכנה מותרת (כדי לסגור אותה)
        if (c.Ctrl && vk == VkEsc) return true;                      // Ctrl+Esc ו-Ctrl+Shift+Esc (מנהל משימות)
        bool legit = c.FgOtzaria || c.FgAllowed;
        // תפריט הקשר: מקש התפריט ו-Shift+F10 נחסמים מחוץ לתוכנות לגיטימיות, ותמיד בדיאלוג קבצים
        if ((c.FgFileDialog || !legit) && (vk == VkApps || (c.Shift && vk == VkF10))) return true;
        if (!legit && vk == VkF1) return true;                       // עזרה פותחת דפדפן/חלונות מערכת
        if (c.FgBrowser && c.Ctrl && !c.Alt && vk is VkO or VkP) return true; // פתיחת קובץ / הדפסה בדפדפן מותר-ברשימה
        return false;
    }
}

public enum ShellAction { Keep, Close, Kill }

internal static class ShellPolicy
{
    // תהליכים שהחלון שלהם חוסם את הנעילה: Kill (ל-ApplicationFrameHost רק סגירת החלון, כי הוא מארח גם את כלי החיתוך המאושר)
    private static readonly Dictionary<string, ShellAction> Hostile = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SystemSettings"] = ShellAction.Kill, ["ApplicationFrameHost"] = ShellAction.Close, ["osk"] = ShellAction.Kill,
        ["narrator"] = ShellAction.Kill, ["magnify"] = ShellAction.Kill, ["utilman"] = ShellAction.Kill,
        ["regedit"] = ShellAction.Kill, ["mmc"] = ShellAction.Kill, ["control"] = ShellAction.Kill,
        ["rundll32"] = ShellAction.Kill, ["eventvwr"] = ShellAction.Kill, ["mstsc"] = ShellAction.Kill,
        ["taskmgr"] = ShellAction.Kill, ["WerFault"] = ShellAction.Kill, ["WerFaultSecure"] = ShellAction.Kill,
        ["ShellExperienceHost"] = ShellAction.Kill, // התראות toast וחלוניות פעולה (לחיצה על toast פותחת תוכנה/קישור)
        ["msedge"] = ShellAction.Kill, ["chrome"] = ShellAction.Kill, ["firefox"] = ShellAction.Kill,
        ["iexplore"] = ShellAction.Kill, ["brave"] = ShellAction.Kill, ["opera"] = ShellAction.Kill,
        ["vivaldi"] = ShellAction.Kill, ["cmd"] = ShellAction.Kill, ["powershell"] = ShellAction.Kill,
        ["pwsh"] = ShellAction.Kill, ["wt"] = ShellAction.Kill, ["WindowsTerminal"] = ShellAction.Kill,
        ["conhost"] = ShellAction.Kill, ["mshta"] = ShellAction.Kill, ["wscript"] = ShellAction.Kill,
        ["cscript"] = ShellAction.Kill, ["msconfig"] = ShellAction.Kill, ["perfmon"] = ShellAction.Kill,
        ["compmgmt"] = ShellAction.Kill, ["services"] = ShellAction.Kill, ["devmgmt"] = ShellAction.Kill,
        // [r1] כלי ניהול מערכת נוספים (חלק מהם רצים תחת mmc, חלק כתהליך עצמאי)
        ["resmon"] = ShellAction.Kill, ["taskschd"] = ShellAction.Kill, ["dxdiag"] = ShellAction.Kill, ["msinfo32"] = ShellAction.Kill,
        ["diskmgmt"] = ShellAction.Kill, ["lusrmgr"] = ShellAction.Kill, ["secpol"] = ShellAction.Kill, ["gpedit"] = ShellAction.Kill,
        ["cleanmgr"] = ShellAction.Kill, ["psr"] = ShellAction.Kill, ["mdsched"] = ShellAction.Kill, ["msra"] = ShellAction.Kill,
        ["quickassist"] = ShellAction.Kill, ["odbcad32"] = ShellAction.Kill, ["dfrgui"] = ShellAction.Kill,
        // [s2] צאצאי VBA/LOLBins (ר' Programs.Blocked) + PickerHost: כל חלון שלו (לא רק #32770) נסגר, בלי תלות במחלקה
        ["hh"] = ShellAction.Kill, ["msdt"] = ShellAction.Kill, ["cmstp"] = ShellAction.Kill, ["odbcconf"] = ShellAction.Kill,
        ["wordpad"] = ShellAction.Kill, ["write"] = ShellAction.Kill, ["charmap"] = ShellAction.Kill, ["regsvr32"] = ShellAction.Kill,
        ["installutil"] = ShellAction.Kill, ["msbuild"] = ShellAction.Kill, ["regasm"] = ShellAction.Kill, ["regsvcs"] = ShellAction.Kill,
        ["mavinject"] = ShellAction.Kill, ["presentationhost"] = ShellAction.Kill, ["ieexec"] = ShellAction.Kill,
        ["notepad"] = ShellAction.Kill, ["mspaint"] = ShellAction.Kill, ["PickerHost"] = ShellAction.Kill,
    };

    /// <summary>[r1] שמות שמסומנים עוינים (לשימוש הקורא שרוצה לבדוק לפני כל "אמון" אחר)</summary>
    internal static bool IsHostileName(string name) => Hostile.ContainsKey(Stem(name));

    // רכיבי מערכת שחלונותיהם לגיטימיים (IME, מסך כניסה, UAC...). נשארים רק אם הנתיב+ACL+חתימת Microsoft תקינים (IsTrustedSystem),
    // או שהנתיב לא קריא (תהליך מוגבה מאיתנו = לא ניתן לזייף ממשתמש רגיל)
    private static readonly HashSet<string> Critical = new(StringComparer.OrdinalIgnoreCase)
    {
        "csrss", "dwm", "winlogon", "LogonUI", "consent", "fontdrvhost", "ctfmon", "TextInputHost", "sihost",
        "taskhostw", "RuntimeBroker", "svchost", "LockApp", "SecurityHealthSystray", "CredentialUIBroker",
        "smartscreen", "InputApp", "WindowsInternal.ComposableShell.Experiences.TextInput.InputApp",
        // [r1] מקלדת מגע/IME/הגדרות-תיווך/מארחי COM: רק עם נתיב+חתימה
        "TabTip", "ShellHost", "ImeBroker", "dllhost", "SystemSettingsBroker",
    };

    // נטפלים בהם במקום אחר (Watch / KioskGuard): לא נוגעים כאן — אבל רק אם הנתיב+חתימה אמיתיים (אחרת התחזות)
    private static readonly HashSet<string> HandledElsewhere = new(StringComparer.OrdinalIgnoreCase)
        { "explorer", "StartMenuExperienceHost", "SearchHost" };   // [s2] PickerHost הוצא: נסגר/נהרג בכל מחלקה (Hostile)

    // [r1] explorer אינו עוד "Keep" גורף: רק חלונות המעטפת עצמם (שולחן עבודה/שורת משימות). כל השאר (Run #32770, CabinetWClass...) נסגרים
    private static readonly HashSet<string> ExplorerShellClasses = new(StringComparer.Ordinal)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "NotifyIconOverflowWindow",
        "TopLevelWindowForOverflowXamlIsland", "SysShadow", "tooltips_class32",
    };

    /// <summary>
    /// [r1] מחלקות חלון של שכבת הצילום (Win11 "חדש"): גשרי הרכבה/CoreWindow שמצייר הצילום. בזמן צילום מאושר (120 שניות) רק הן פטורות;
    /// #32770 (Run/דיאלוגים), CabinetWClass, ApplicationFrameWindow נשארים באכיפה. הרשימה לא אומתה מול Windows חי (ר' הדוח).
    /// </summary>
    internal static bool IsSnipOverlayClass(string cls) =>
        !string.IsNullOrEmpty(cls) && (cls.StartsWith("ScreenClip", StringComparison.OrdinalIgnoreCase) ||
        cls is "Windows.UI.Core.CoreWindow" or "Microsoft.UI.Content.DesktopChildSiteBridge" or
               "Windows.UI.Composition.DesktopWindowContentBridge" or "Windows.UI.Input.InputSite.WindowClass");

    // [r1] תהליכים שה-Close שלהם אינו מסלים ל-Kill: ApplicationFrameHost מארח גם אפליקציות UWP מותרות; explorer נטפל ב-Watch
    /// <summary>[s2] WHY: פטור מהריגה לפי שם בלבד אפשר לזייף (explorer.exe בתיקיה כתיבה = בלתי ניתן להריגה). הפטור רק כשהנתיב+חתימה אמיתיים</summary>
    internal static bool NeverKill(string name, bool trustedSystem) => trustedSystem && NeverKill(name);

    internal static bool NeverKill(string name)
    {
        string n = Stem(name);
        return n.Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase) || n.Equals("explorer", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>[r1] האם ההחלטה על השם הזה תלויה באמון (נתיב+חתימה) — רק אז משלמים על בדיקת חתימה</summary>
    internal static bool NeedsTrust(string name)
    {
        string n = Stem(name);
        return HandledElsewhere.Contains(n) || Critical.Contains(n) || n.Equals("ShellExperienceHost", StringComparison.OrdinalIgnoreCase) ||
               n.Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase);   // [s2] נדרש לחישוב NeverKill לפי אמון
    }

    /// <summary>
    /// [r1] רכיב מערכת אמיתי: נתיב במיקום קבוע לא-כתיב (ACL) + חתימת Microsoft תקפה. לא "שם + תחת C:\Windows" (Windows\Temp כתיב).
    /// חבילות (SystemApps/WindowsApps של Microsoft) נבדקות גם לפי שם-חבילה+חתימה, בלי תלות ב-ACL לא קריא.
    /// </summary>
    internal static bool IsTrustedSystem(string imagePath, AppTrust.ITrustEnv env) =>
        !string.IsNullOrEmpty(imagePath) && (AppTrust.IsTrustedMicrosoft(imagePath, env) || AppTrust.IsMicrosoftPackage(imagePath, env));

    internal static string Stem(string name)
    {
        if (string.IsNullOrEmpty(name)) return "";
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }

    internal static bool IsUnder(string path, string root)
    {
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(root)) return false;
        root = root.TrimEnd('\\') + "\\";
        return path.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// החלטה לגבי חלון של תהליך שאינו אוצריא/הגשר/תוכנה מותרת (את אלה בודק הקורא קודם).
    /// trusted = IsTrustedSystem (נתיב+ACL+חתימה); pathUnknown = אין גישה לנתיב (מוגבה מאיתנו); cls = מחלקת החלון (לכללי explorer/צילום).
    /// </summary>
    internal static ShellAction Decide(string name, bool trusted, bool pathUnknown, bool snipInProgress, string cls = null)
    {
        string n = Stem(name);
        if (n.Length == 0) return ShellAction.Keep;
        if (HandledElsewhere.Contains(n) && (trusted || pathUnknown))
        {
            if (!n.Equals("explorer", StringComparison.OrdinalIgnoreCase)) return ShellAction.Keep;   // PickerHost/תפריט התחל/חיפוש: KioskGuard
            if (ExplorerShellClasses.Contains(cls ?? "")) return ShellAction.Keep;
            if (snipInProgress && IsSnipOverlayClass(cls)) return ShellAction.Keep;
            return ShellAction.Close;   // Run (#32770), CabinetWClass וכו' — גם בזמן צילום
        }
        if (Critical.Contains(n) && (trusted || pathUnknown)) return ShellAction.Keep;
        if (snipInProgress && n.Equals("ShellExperienceHost", StringComparison.OrdinalIgnoreCase) && (trusted || pathUnknown) && IsSnipOverlayClass(cls))
            return ShellAction.Keep;
        if (Hostile.TryGetValue(n, out var a)) return a;
        return ShellAction.Close; // כל תהליך חזית אחר שאינו ברשימה (גם מתחזה בשם מערכת): סגירה, ואם לא נסגר — Kill בסריקה הבאה
    }
}

public static partial class Kiosk
{
    // ---------- P/Invoke ----------
    [StructLayout(LayoutKind.Sequential)] private struct NativeMsg { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct MSLLHOOKSTRUCT { public NativePoint pt; public uint mouseData, flags, time; public IntPtr extra; }

    [DllImport("user32.dll")] private static extern int GetMessage(out NativeMsg m, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool PeekMessage(out NativeMsg m, IntPtr hwnd, uint min, uint max, uint remove);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref NativeMsg m);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref NativeMsg m);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint tid, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern uint MsgWaitForMultipleObjectsEx(uint n, IntPtr[] h, uint ms, uint mask, uint flags);
    private delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr mod, WinEventProc proc, uint pid, uint tid, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
    private delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(NativePoint p);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr h, uint flags);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int val, int size);
    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageName(IntPtr h, uint flags, StringBuilder sb, ref int size);

    private const uint WmApp = 0x8000, WmQuit = 0x0012, MsgReinstall = WmApp + 1, MsgPing = WmApp + 2, MsgStop = WmApp + 3;

    private static string ImagePath(uint pid)
    {
        IntPtr h = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            int n = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref n) ? sb.ToString() : null;
        }
        finally { CloseHandle(h); }
    }

    // ---------- hook מקלדת/עכבר בתהליכון ייעודי ----------

    private static Thread _hookThread;
    private static uint _hookThreadId;
    private static IntPtr _mouseHook = IntPtr.Zero;
    private static LowLevelKeyboardProc _mouseProc;
    private static System.Threading.Timer _hookWatchdog;
    private static long _pingSent, _pingAck;
    private static readonly object HookGate = new();

    /// <summary>מתקין את ה-hook בתהליכון ייעודי (בלי תלות בתהליכון ה-UI). מחזיר אם ההתקנה הצליחה (ערך ההחזר נבדק)</summary>
    private static bool InstallHook()
    {
        lock (HookGate)
        {
            _hookWatchdog ??= new System.Threading.Timer(_ => HookWatchdogTick(), null, 3000, 3000);
            if (_hookThread is { IsAlive: true } && _hook != IntPtr.Zero) return true;
            return StartHookThread();
        }
    }

    private static bool StartHookThread()
    {
        var ready = new ManualResetEventSlim(false);
        var t = new Thread(() => HookThreadMain(ready)) { IsBackground = true, Name = "KioskKeyHook" };
        t.SetApartmentState(ApartmentState.STA);
        _hookThread = t;
        t.Start();
        ready.Wait(2000);
        return _hook != IntPtr.Zero;
    }

    /// <summary>
    /// [r1] ידיות ה-hook שייכות לחוט שיצר אותן (מצב מקומי, לא שדות סטטיים משותפים): חוט ישן שהתעורר אחרי hang משחרר רק את שלו
    /// ולא את ה-hook של החוט החדש. השדות הסטטיים (_hook/_mouseHook) מתעדכנים רק אם החוט הוא החוט הפעיל.
    /// </summary>
    private sealed class HookHandles { public IntPtr Kb, Mouse; }

    private static void HookThreadMain(ManualResetEventSlim ready)
    {
        var mine = new HookHandles();
        uint tid = GetCurrentThreadId();
        if (ReferenceEquals(_hookThread, Thread.CurrentThread)) _hookThreadId = tid; // בלי lock: StartHookThread מחזיק HookGate וממתין ל-ready
        PeekMessage(out _, IntPtr.Zero, 0, 0, 0); // יוצר את תור ההודעות של התהליכון לפני שמישהו שולח לו
        // הנציגים נשמרים בשדות סטטיים ולא מוחלפים (נציג שנאסף ע"י ה-GC בזמן ש-hook ישן עדיין מותקן = קריסה)
        _hookProc ??= HookCallback;
        _mouseProc ??= MouseCallback;
        InstallCore(mine);
        ready.Set();
        while (GetMessage(out var m, IntPtr.Zero, 0, 0) > 0)
        {
            if (m.message == MsgReinstall) InstallCore(mine);
            else if (m.message == MsgPing) Volatile.Write(ref _pingAck, Environment.TickCount64);
            else if (m.message == MsgStop) break;
            else { TranslateMessage(ref m); DispatchMessage(ref m); }
        }
        if (mine.Kb != IntPtr.Zero) UnhookWindowsHookEx(mine.Kb);
        if (mine.Mouse != IntPtr.Zero) UnhookWindowsHookEx(mine.Mouse);
        // מאפסים את הסטטי רק אם הוא עדיין שלי (החוט החדש כבר עדכן אותו)
        if (_hook == mine.Kb) _hook = IntPtr.Zero;
        if (_mouseHook == mine.Mouse) _mouseHook = IntPtr.Zero;
    }

    /// <summary>התקנה חדשה לפני הסרת הישנה (בלי חלון בלי הגנה). רץ רק בתהליכון ה-hook, על הידיות שלו</summary>
    private static void InstallCore(HookHandles mine)
    {
        IntPtr mod = GetModuleHandle(null);
        IntPtr nk = SetWindowsHookEx(13 /* WH_KEYBOARD_LL */, _hookProc, mod, 0);
        if (nk == IntPtr.Zero) LogThrottled("hookfail", "keyboard hook failed: " + Marshal.GetLastWin32Error(), 30);
        else
        {
            var old = mine.Kb; mine.Kb = nk;
            if (ReferenceEquals(_hookThread, Thread.CurrentThread)) _hook = nk;
            if (old != IntPtr.Zero) UnhookWindowsHookEx(old);
            if (!_hookLogged) { _hookLogged = true; Config.Log("keyboard hook installed (dedicated thread)"); }
        }
        IntPtr nm = SetWindowsHookEx(14 /* WH_MOUSE_LL */, _mouseProc, mod, 0);
        if (nm != IntPtr.Zero)
        {
            var old = mine.Mouse; mine.Mouse = nm;
            if (ReferenceEquals(_hookThread, Thread.CurrentThread)) _mouseHook = nm;
            if (old != IntPtr.Zero) UnhookWindowsHookEx(old);
        }
        else LogThrottled("mousehookfail", "mouse hook failed: " + Marshal.GetLastWin32Error(), 30);
    }

    /// <summary>
    /// כל ~3 שניות: מוודא שהתהליכון חי ומגיב (ping), ואם לא — מרים חדש; אחרת מתקין hook מחדש
    /// (Windows מסירה בשקט hook שחרג מ-LowLevelHooksTimeout ואין API לשאול אם הוא עדיין חי)
    /// </summary>
    private static void HookWatchdogTick()
    {
        try
        {
            if (!IsActive) return;
            lock (HookGate)
            {
                bool dead = _hookThread == null || !_hookThread.IsAlive;
                long sent = Volatile.Read(ref _pingSent), ack = Volatile.Read(ref _pingAck);
                bool hung = !dead && sent != 0 && ack < sent; // ping קודם לא נענה בתוך מחזור שלם
                if (dead || hung || _hook == IntPtr.Zero)
                {
                    if (hung) { PostThreadMessage(_hookThreadId, MsgStop, IntPtr.Zero, IntPtr.Zero); LogThrottled("hookhung", "keyboard hook: התהליכון לא הגיב, מרים חדש", 30); }
                    Volatile.Write(ref _pingSent, 0); Volatile.Write(ref _pingAck, 0);
                    StartHookThread();
                    return;
                }
                PostThreadMessage(_hookThreadId, MsgReinstall, IntPtr.Zero, IntPtr.Zero);
                Volatile.Write(ref _pingSent, Environment.TickCount64);
                PostThreadMessage(_hookThreadId, MsgPing, IntPtr.Zero, IntPtr.Zero);
            }
        }
        catch (Exception ex) { Config.Log("hook watchdog: " + ex.Message); }
    }

    private static void RemoveHook()
    {
        Thread t;
        lock (HookGate)
        {
            _hookWatchdog?.Dispose(); _hookWatchdog = null;
            t = _hookThread; _hookThread = null;
            if (t is { IsAlive: true }) PostThreadMessage(_hookThreadId, MsgStop, IntPtr.Zero, IntPtr.Zero);
        }
        try { t?.Join(2000); } catch { }
    }

    private static IntPtr MouseCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            long msg = (long)wParam;
            // כפתור ימני בדיאלוג קבצים: תפריט ההקשר (פתיחה/מאפיינים/הפעלה כמנהל) עוקף את בדיקת הסיומת
            if ((msg == 0x204 || msg == 0x205) && DialogGuard.ShouldSwallowMouse(Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam).pt.X, Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam).pt.Y))
                return (IntPtr)1;
        }
        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    internal static IntPtr RootWindowAt(int x, int y)
    {
        IntPtr w = WindowFromPoint(new NativePoint { X = x, Y = y });
        return w == IntPtr.Zero ? IntPtr.Zero : GetAncestor(w, 2 /* GA_ROOT */);
    }

    internal static IntPtr ForegroundRoot()
    {
        IntPtr f = GetForegroundWindow();
        return f == IntPtr.Zero ? IntPtr.Zero : GetAncestor(f, 2);
    }

    // ---------- שומר חלונות: WinEvent + סריקת כל החלונות העליונים ----------

    private static Thread _shellThread;
    private static volatile bool _shellStop;
    private static long _shellDirtyAt;
    private static WinEventProc _shellProc;
    private static readonly ConcurrentDictionary<uint, PidInfo> _pidCache = new();
    private static readonly ConcurrentDictionary<uint, long> _closeSeen = new();
    private static readonly string WinDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    private static void StartShellGuard()
    {
        lock (HookGate)
        {
            if (_shellThread is { IsAlive: true }) return;
            _shellStop = false;
            _shellThread = new Thread(ShellGuardMain) { IsBackground = true, Name = "KioskShellGuard" };
            _shellThread.SetApartmentState(ApartmentState.STA);
            _shellThread.Start();
        }
    }

    // keepBaseline: פתיחה זמנית (Release(keepStrict)) ואז Apply. WHY (u2): איפוס baseline בכל עצירה גרם לכך שתהליכים בלי חלון שנוצרו בזמן הפתיחה
    // נרשמו כ"היו קיימים" ומוּתרו לתמיד. בפתיחה זמנית ה-baseline הקודם נשמר, ותהליך חדש שנוצר בחלון הפתוח נבחן כרגיל.
    // מחיר (false-positive): תוכנת רקע שעלתה בזמן הפתיחה הזמנית ואינה מהימנה תיסגר בנעילה מחדש. שחרור מלא מאפס (נעילה חדשה = baseline חדש)
    private static void StopShellGuard(bool keepBaseline = false)
    {
        Thread t;
        lock (HookGate) { t = _shellThread; _shellThread = null; _shellStop = true; }
        try { t?.Join(2000); } catch { }
        _pidCache.Clear(); _closeSeen.Clear();
        if (!keepBaseline) { _procSeen.Clear(); _procBaselineDone = false; }
    }

    private static void ShellGuardMain()
    {
        var hooks = new List<IntPtr>();
        try
        {
            _shellProc = (_, ev, _, obj, _, _, _) =>
            {
                if (ev != 0x0003 && obj != 0) return; // FOREGROUND תמיד; SHOW/NAMECHANGE רק לחלון עצמו
                Volatile.Write(ref _shellDirtyAt, Environment.TickCount64);
            };
            foreach (uint ev in new uint[] { 0x0003 /* FOREGROUND */, 0x8002 /* SHOW */, 0x800C /* NAMECHANGE */, 0x8018 /* UNCLOAKED */ })
            {
                IntPtr h = SetWinEventHook(ev, ev, IntPtr.Zero, _shellProc, 0, 0, 0x0002 /* OUTOFCONTEXT|SKIPOWNPROCESS=2 */);
                if (h != IntPtr.Zero) hooks.Add(h);
            }
            if (hooks.Count == 0) Config.Log("shell guard: SetWinEventHook נכשל, נשארת סריקה תקופתית בלבד");
            long lastSweep = 0, lastProc = 0;
            while (!_shellStop)
            {
                MsgWaitForMultipleObjectsEx(0, null, 100, 0x04FF /* QS_ALLINPUT */, 0x4 /* MWMO_INPUTAVAILABLE */);
                while (PeekMessage(out var m, IntPtr.Zero, 0, 0, 1)) { TranslateMessage(ref m); DispatchMessage(ref m); }
                long now = Environment.TickCount64;
                bool dirty = Volatile.Read(ref _shellDirtyAt) > lastSweep;
                if ((dirty && now - lastSweep >= 60) || now - lastSweep >= 300)
                {
                    lastSweep = now;
                    try { if (IsActive) SweepWindows(); } catch (Exception ex) { LogThrottled("sweep", "shell sweep: " + ex.Message, 30); }
                }
                if (now - lastProc >= 1000)
                {
                    lastProc = now;
                    try { if (IsActive) SweepProcesses(); } catch (Exception ex) { LogThrottled("procsweep", "process sweep: " + ex.Message, 30); }
                }
            }
        }
        finally { foreach (var h in hooks) UnhookWinEvent(h); }
    }

    private static bool IsOtzariaImage(uint pid, string path)
    {
        string otz = OtzariaPath();
        if (!string.IsNullOrEmpty(path) && !string.IsNullOrEmpty(otz))
            return path.Equals(Path.GetFullPath(otz), StringComparison.OrdinalIgnoreCase) && OtzariaImageVerified(otz); // [u2] נתיב + SHA/מיקום מהימן, לא נתיב בלבד
        return IsOtzariaPid(pid); // נתיב לא קריא/לא הוגדר: נפילה לשם (תהליך מוגבה בלבד)
    }

    // [r1] PidInfo לפי תהליך (נתיב + אמון מערכת); ההחלטה (Decide) לפי חלון, כי explorer/ShellExperienceHost תלויים במחלקת החלון
    private sealed record PidInfo(long At, bool Skip, string Name, bool Trusted, bool PathUnknown);

    private static PidInfo Classify(uint pid, int session)
    {
        long now = Environment.TickCount64;
        if (_pidCache.TryGetValue(pid, out var c) && now - c.At < 1500) return c;
        PidInfo r;
        try
        {
            using var p = Process.GetProcessById((int)pid);
            if (p.SessionId != session) r = new PidInfo(now, true, "", false, false);
            else
            {
                string name = p.ProcessName, path = ImagePath(pid);
                // [r1] עוין (דפדפן/LOLBin/כלי ניהול) נבדק קודם: לא נפטר גם אם הוא "חדש אחרי הפעלה"/בעץ הגשר (IsAllowedForeground בודק זהות חסומה ראשון)
                bool hostile = ShellPolicy.IsHostileName(name);
                if (pid == (uint)Environment.ProcessId || (!hostile && (IsOtzariaImage(pid, path) || Programs.IsAllowedForeground(pid))))
                    r = new PidInfo(now, true, name, false, false);
                else
                {
                    bool trusted = ShellPolicy.NeedsTrust(name) && ShellPolicy.IsTrustedSystem(path, AppTrust.Env);
                    r = new PidInfo(now, false, name, trusted, string.IsNullOrEmpty(path));
                }
            }
        }
        catch { r = new PidInfo(now, true, "", false, false); }
        _pidCache[pid] = r;
        return r;
    }

    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumProc cb, IntPtr l);

    /// <summary>[r1] PID של תהליך הליבה של UWP שמתארח בחלון ApplicationFrameHost (חלון הילד Windows.UI.Core.CoreWindow), או 0</summary>
    private static uint HostedCorePid(IntPtr frame, uint hostPid)
    {
        uint found = 0;
        var sb = new StringBuilder(64);
        EnumChildWindows(frame, (ch, _) =>
        {
            GetWindowThreadProcessId(ch, out uint cp);
            if (cp == 0 || cp == hostPid) return true;
            sb.Clear();
            if (GetClassName(ch, sb, sb.Capacity) > 0 && sb.ToString() == "Windows.UI.Core.CoreWindow") { found = cp; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    private static void SweepWindows()
    {
        int session = Process.GetCurrentProcess().SessionId;
        UpdateForegroundAllowedCacheIfChanged();
        bool snip = _snipSeen != DateTime.MinValue && (DateTime.UtcNow - _snipSeen).TotalSeconds < 120;
        var victims = new Dictionary<uint, (string name, ShellAction act, List<IntPtr> wins, bool trusted)>();
        var cb = new StringBuilder(128);
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h)) return true;
            if (DwmGetWindowAttribute(h, 14 /* DWMWA_CLOAKED */, out int cl, 4) == 0 && cl != 0) return true;
            if (!GetWindowRect(h, out var rc) || rc.Right <= rc.Left || rc.Bottom <= rc.Top) return true;
            GetWindowThreadProcessId(h, out uint pid);
            if (pid == 0) return true;
            var info = Classify(pid, session);
            if (info.Skip) return true;
            cb.Clear();
            GetClassName(h, cb, cb.Capacity);
            var act = ShellPolicy.Decide(info.Name, info.Trusted, info.PathUnknown, snip, cb.ToString());
            if (act == ShellAction.Keep) return true;
            // [r1] ApplicationFrameHost מותר כשתהליך הליבה של ה-UWP שהוא מארח הוא אפליקציית חבילה מהרשימה (לפי PackageFamilyName)
            if (info.Name.Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase))
            {
                uint core = HostedCorePid(h, pid);
                if (core != 0 && Programs.IsAllowedPackageProcess(core)) return true;
            }
            if (!victims.TryGetValue(pid, out var v)) victims[pid] = v = (info.Name, act, new List<IntPtr>(), info.Trusted);
            if (act == ShellAction.Kill) v.act = ShellAction.Kill;
            v.wins.Add(h);
            victims[pid] = v;
            return true;
        }, IntPtr.Zero);

        long now = Environment.TickCount64;
        foreach (var (pid, (name, act, wins, trustedSys)) in victims)
        {
            bool kill = act == ShellAction.Kill;
            if (!kill)
            {
                // Close: קודם סגירה מנומסת; אם החלון עדיין שם אחרי >=600ms — Kill.
                // [r1] חריג: ApplicationFrameHost/explorer — רק סגירה (WM_CLOSE בכל סריקה), בלי הסלמה: הריגתם סוגרת גם UWP מותרים/את המעטפת
                if (!ShellPolicy.NeverKill(name, trustedSys) && _closeSeen.TryGetValue(pid, out long first) && now - first >= 600) kill = true;
                else
                {
                    _closeSeen.TryAdd(pid, now);
                    foreach (var w in wins) PostMessage(w, 0x0010 /* WM_CLOSE */, IntPtr.Zero, IntPtr.Zero);
                }
            }
            if (kill)
            {
                try { using var p = Process.GetProcessById((int)pid); p.Kill(true); } catch { }
                _closeSeen.TryRemove(pid, out _);
                _pidCache.TryRemove(pid, out _);
            }
            LogThrottled("shell:" + name, "shell guard: חלון " + name + (kill ? " נהרג" : " נסגר") + " (תוכנה שאינה ברשימה)", 30);
            if (LogOnce("shellnote", 10)) Tray.Notify(Program.AppName, Lang.T("התוכנה הזו חסומה בזמן נעילה", "This program is blocked while locked"));
        }
        // ניקוי רשומות סגירה של תהליכים שנעלמו
        foreach (var k in _closeSeen.Keys) if (!victims.ContainsKey(k)) _closeSeen.TryRemove(k, out _);
    }

    // ---------- [s2] סריקת תהליכים (לא רק חלונות) ----------
    // WHY: SweepWindows רואה רק חלונות גלויים בגודל>0; תהליך בלי חלון / חלון מוסתר / cloaked / בגודל 0 לא נראה, ו-Watch הורג רק explorer/Taskmgr.
    // כאן: תהליך *חדש* בסשן (שלא היה קיים כשהשומר עלה) שהוא בשם/זהות חסומה, או שהקובץ שלו לא במיקום מנהל-בלבד ואינו תוכנה מותרת - נהרג, גם בלי חלון.
    // false-positive: תוכנות רקע של המשתמש שעולות אחרי הנעילה מ-AppData (OneDrive/Teams/עדכונים) נהרגות; מה שרץ לפני הנעילה לא נוגעים בו (baseline)

    [DllImport("ntdll.dll")] private static extern int NtQueryInformationProcess(IntPtr h, int cls, ref PROCESS_BASIC_INFORMATION pbi, int len, out int ret);
    [StructLayout(LayoutKind.Sequential)] private struct PROCESS_BASIC_INFORMATION { public IntPtr Exit, PebBase, Affinity, BasePriority, Pid, ParentPid; }

    /// <summary>PID האב לפי ntdll (0 אם לא ידוע). האב עלול להיות PID ממוחזר; הקורא משווה זמני יצירה</summary>
    internal static uint ParentPidOf(uint pid)
    {
        IntPtr h = OpenProcess(0x1000, false, pid);
        if (h == IntPtr.Zero) return 0;
        try
        {
            var pbi = new PROCESS_BASIC_INFORMATION();
            return NtQueryInformationProcess(h, 0, ref pbi, Marshal.SizeOf<PROCESS_BASIC_INFORMATION>(), out _) == 0 ? (uint)(long)pbi.ParentPid : 0;
        }
        catch { return 0; }
        finally { CloseHandle(h); }
    }

    internal static string ImagePathOf(uint pid) => ImagePath(pid);

    /// <summary>
    /// החלטה טהורה: האם להרוג תהליך חדש בסשן.
    /// [u2] WHY: זהות חסומה נבדקת *לפני* פטור-עץ. עוזר של הגשר עצמו (ילד שלא עובר בשורש מ-Programs.Launched) פטור; צאצא של תוכנה מותרת
    /// (launchedDescendant: Word => DDEAUTO/OLE => wscript/powershell -w hidden) לא, וגם לא-חסום נדרש TreeChildTrusted (או רכיב מערכת חתום Microsoft).
    /// </summary>
    internal static bool ShouldKillProcess(bool bridgeHelper, bool launchedDescendant, bool childTrusted, bool trustedSystem,
                                           bool otzaria, bool grandfathered, bool blockedIdentity, bool locationTrusted, bool allowedByList)
    {
        if (otzaria || grandfathered) return false;
        bool helper = bridgeHelper && !launchedDescendant;
        if (blockedIdentity) return !helper;
        if (helper) return false;
        if (launchedDescendant) return !(childTrusted || trustedSystem);
        if (allowedByList) return false;
        return !locationTrusted;
    }

    /// <summary>תאימות: הצורה הישנה (selfTree = עוזר גשר, בלי צאצאי תוכנה מותרת)</summary>
    internal static bool ShouldKillProcess(bool selfTree, bool otzaria, bool grandfathered, bool blockedIdentity, bool locationTrusted, bool allowedByList) =>
        ShouldKillProcess(selfTree, false, false, false, otzaria, grandfathered, blockedIdentity, locationTrusted, allowedByList);

    /// <summary>שמות שלעולם לא נהרגים בסריקת תהליכים (נטפלים במקום אחר / מארחים משותפים)</summary>
    private static readonly HashSet<string> ProcSweepExempt = new(StringComparer.OrdinalIgnoreCase)
        { "explorer", "msiexec", "ApplicationFrameHost", "conhost", "openconsole" };

    /// <summary>
    /// [u2] WHY: פטור לפי שם בלבד ניתן לזיוף (conhost.exe/explorer.exe בתיקיה כתיבה). הפטור רק כשהנתיב מנהל-בלבד + חתימת Microsoft (IsTrustedSystem)
    /// </summary>
    internal static bool ProcSweepExemptFor(string name, string path, AppTrust.ITrustEnv env) =>
        ProcSweepExempt.Contains(ShellPolicy.Stem(name)) && ShellPolicy.IsTrustedSystem(path, env);

    private static readonly ConcurrentDictionary<(int pid, long start), bool> _procSeen = new();
    private static bool _procBaselineDone;

    private static bool IsSelfDescendant(uint pid, long startTicks)
    {
        uint cur = pid; long curStart = startTicks;
        for (int i = 0; i < 8 && cur > 4; i++)
        {
            uint par = ParentPidOf(cur);
            if (par <= 4) return false;
            if (par == (uint)Environment.ProcessId) return true;
            try
            {
                using var pp = Process.GetProcessById((int)par);
                long ps = pp.StartTime.ToUniversalTime().Ticks;
                if (ps > curStart) return false;   // PID ממוחזר: האב "צעיר" מהבן
                cur = par; curStart = ps;
            }
            catch { return false; }
        }
        return false;
    }

    private static void SweepProcesses()
    {
        int session = Process.GetCurrentProcess().SessionId;
        string otz = null;
        try { otz = OtzariaPath(); } catch { }
        var alive = new HashSet<(int, long)>();
        bool baseline = !_procBaselineDone;
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    if (p.SessionId != session || p.Id == Environment.ProcessId || p.Id <= 4) continue;
                    long start = p.StartTime.ToUniversalTime().Ticks;
                    var key = (p.Id, start);
                    alive.Add(key);
                    if (baseline) { _procSeen[key] = true; continue; }   // כל מה שרץ כשהשומר עלה: לא נוגעים
                    if (_procSeen.ContainsKey(key)) continue;
                    string path = ImagePath((uint)p.Id);
                    if (string.IsNullOrEmpty(path)) continue;            // מוגבה/מוגן: לא נשמר, ינוסה שוב (הצעה בדוח: ניטור בשירות)
                    string name = p.ProcessName;
                    if (ProcSweepExemptFor(name, path, AppTrust.Env)) { _procSeen[key] = true; continue; }
                    bool selfTree = IsSelfDescendant((uint)p.Id, start);
                    bool launchedDesc = Programs.HasLaunchedAncestor(p.Id);   // [u2] צאצא של תוכנה שהגשר הפעיל: לא "עוזר גשר"
                    bool childTrusted = launchedDesc && Programs.ChildTrustedPid(p.Id);
                    bool isOtz = !string.IsNullOrEmpty(otz) && SamePath(path, otz) && OtzariaImageVerified(otz);   // [u2]
                    bool blocked = Programs.IsBlockedProc(p);
                    bool locTrusted = AppTrust.IsAdminOnlyLocation(path) || AppTrust.IsMicrosoftPackage(path, AppTrust.Env);
                    bool byList = !blocked && !locTrusted && !selfTree && !isOtz && Programs.IsAllowedForeground((uint)p.Id);
                    // [u2] false-positive: OneDrive/Teams/מעדכנים חתומי Microsoft מ-AppData, רק כשרצה תוכנה מהימנה; נרשם ביומן (throttle) כדי שיהיה ניתן לביקורת
                    bool msApp = !blocked && !isOtz && AppTrust.IsMicrosoftUserApp(path, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Programs.AnyLaunchedAlive(), AppTrust.Env);
                    if (msApp) LogThrottled("procsweep-msapp:" + name, "process sweep: " + name + " פטור (אפליקציית Microsoft חתומה, תוכנה מהימנה רצה): " + path, 300);
                    bool sysTrusted = ShellPolicy.IsTrustedSystem(path, AppTrust.Env);
                    if (!msApp && ShouldKillProcess(selfTree, launchedDesc, childTrusted, sysTrusted, isOtz, false, blocked, locTrusted, byList))
                    {
                        bool ok = AppTrust.KillLogged(p, "procsweep");
                        LogThrottled("procsweep:" + name, "process sweep: " + name + (ok ? " נהרג" : " לא ניתן להרוג") + " (תהליך לא מהימן/חסום בלי חלון): " + path, 30);
                        if (ok) _procSeen[key] = false;
                    }
                    else _procSeen[key] = true;
                }
                catch { /* תהליך נסגר באמצע */ }
            }
        }
        _procBaselineDone = true;
        foreach (var k in _procSeen.Keys) if (!alive.Contains(k)) _procSeen.TryRemove(k, out _);
    }

    private static readonly object NoteGate = new();
    private static readonly Dictionary<string, long> _noteAt = new();
    private static bool LogOnce(string key, int seconds)
    {
        lock (NoteGate)
        {
            long now = Environment.TickCount64;
            if (_noteAt.TryGetValue(key, out long t) && now - t < seconds * 1000L) return false;
            _noteAt[key] = now;
            return true;
        }
    }

    private static uint _lastFgPidForCache;
    private static void UpdateForegroundAllowedCacheIfChanged()
    {
        GetWindowThreadProcessId(GetForegroundWindow(), out uint pid);
        if (pid == _lastFgPidForCache) return;
        _lastFgPidForCache = pid;
        UpdateForegroundAllowedCache();
    }

    // ---------- התראות toast: חסימה במדיניות ----------
    private const string ToastPolicyKey = @"Software\Policies\Microsoft\Windows\CurrentVersion\PushNotifications";

    private static void SetToastPolicy(bool on)
    {
        try
        {
            if (on)
            {
                using var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(ToastPolicyKey);
                if (k.GetValue("NoToastApplicationNotification") == null)
                {
                    k.SetValue("NoToastApplicationNotification", 1, Microsoft.Win32.RegistryValueKind.DWord);
                    Config.Set("kiosk.toastPolicySet", true);
                }
            }
            else if (Config.Get("kiosk.toastPolicySet", false))
            {
                using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(ToastPolicyKey, true);
                k?.DeleteValue("NoToastApplicationNotification", false);
                Config.Set("kiosk.toastPolicySet", false);
            }
        }
        catch (Exception ex) { Config.Log("kiosk toast policy: " + ex.Message); }
    }
}
