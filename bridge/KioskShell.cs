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
        VkApps = 0x5D, VkO = 0x4F, VkP = 0x50, VkLWin = 0x5B, VkRWin = 0x5C;

    internal readonly record struct Ctx(bool Alt, bool Ctrl, bool Shift, bool Win,
        bool FgOtzaria, bool FgAllowed, bool FgBrowser, bool FgFileDialog);

    /// <summary>האם לבלוע את המקש. Alt נקבע ע"י הקורא כ-"דגל ה-hook או מצב אסינכרוני של MENU/LMENU/RMENU"</summary>
    internal static bool ShouldBlock(uint vk, in Ctx c)
    {
        if (vk is VkLWin or VkRWin) return true;                    // Win בפני עצמו
        if (c.Win) return true;                                      // Win+כל מקש
        if (c.Alt && vk is VkTab or VkEsc or VkSpace) return true;   // Alt+Tab / Alt+Shift+Tab / Alt+Esc / Alt+Space
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
    };

    // רכיבי מערכת שחלונותיהם לגיטימיים (IME, מסך כניסה, UAC...). נשארים רק אם הנתיב בתוך Windows (או שלא קריא = תהליך מוגבה)
    private static readonly HashSet<string> Critical = new(StringComparer.OrdinalIgnoreCase)
    {
        "csrss", "dwm", "winlogon", "LogonUI", "consent", "fontdrvhost", "ctfmon", "TextInputHost", "sihost",
        "taskhostw", "RuntimeBroker", "svchost", "LockApp", "SecurityHealthSystray", "CredentialUIBroker",
        "smartscreen", "InputApp", "WindowsInternal.ComposableShell.Experiences.TextInput.InputApp",
    };

    // נטפלים בהם במקום אחר (Watch / KioskGuard) או בזמן צילום מאושר: לא נוגעים כאן
    private static readonly HashSet<string> HandledElsewhere = new(StringComparer.OrdinalIgnoreCase)
        { "explorer", "PickerHost", "StartMenuExperienceHost", "SearchHost" };

    private static readonly HashSet<string> SnipHosts = new(StringComparer.OrdinalIgnoreCase)
        { "ApplicationFrameHost", "ShellExperienceHost" };

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

    /// <summary>החלטה לגבי תהליך שיש לו חלון עליון גלוי ושאינו אוצריא/הגשר/תוכנה מותרת (את אלה בודק הקורא קודם)</summary>
    internal static ShellAction Decide(string name, string imagePath, string windir, bool snipInProgress)
    {
        string n = Stem(name);
        if (n.Length == 0) return ShellAction.Keep;
        if (HandledElsewhere.Contains(n)) return ShellAction.Keep;
        if (Critical.Contains(n) && (string.IsNullOrEmpty(imagePath) || IsUnder(imagePath, windir))) return ShellAction.Keep;
        if (snipInProgress && SnipHosts.Contains(n)) return ShellAction.Keep;
        if (Hostile.TryGetValue(n, out var a)) return a;
        return ShellAction.Close; // כל תהליך חזית אחר שאינו ברשימה: סגירה, ואם לא נסגר — Kill בסריקה הבאה
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

    private static void HookThreadMain(ManualResetEventSlim ready)
    {
        _hookThreadId = GetCurrentThreadId();
        PeekMessage(out _, IntPtr.Zero, 0, 0, 0); // יוצר את תור ההודעות של התהליכון לפני שמישהו שולח לו
        _hookProc = HookCallback;
        _mouseProc = MouseCallback;
        InstallCore();
        ready.Set();
        while (GetMessage(out var m, IntPtr.Zero, 0, 0) > 0)
        {
            if (m.message == MsgReinstall) InstallCore();
            else if (m.message == MsgPing) Volatile.Write(ref _pingAck, Environment.TickCount64);
            else if (m.message == MsgStop) break;
            else { TranslateMessage(ref m); DispatchMessage(ref m); }
        }
        if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
        if (_mouseHook != IntPtr.Zero) UnhookWindowsHookEx(_mouseHook);
        _hook = IntPtr.Zero; _mouseHook = IntPtr.Zero;
    }

    /// <summary>התקנה חדשה לפני הסרת הישנה (בלי חלון בלי הגנה). רץ רק בתהליכון ה-hook</summary>
    private static void InstallCore()
    {
        IntPtr mod = GetModuleHandle(null);
        IntPtr nk = SetWindowsHookEx(13 /* WH_KEYBOARD_LL */, _hookProc, mod, 0);
        if (nk == IntPtr.Zero) LogThrottled("hookfail", "keyboard hook failed: " + Marshal.GetLastWin32Error(), 30);
        else
        {
            var old = _hook; _hook = nk;
            if (old != IntPtr.Zero) UnhookWindowsHookEx(old);
            if (!_hookLogged) { _hookLogged = true; Config.Log("keyboard hook installed (dedicated thread)"); }
        }
        IntPtr nm = SetWindowsHookEx(14 /* WH_MOUSE_LL */, _mouseProc, mod, 0);
        if (nm != IntPtr.Zero)
        {
            var old = _mouseHook; _mouseHook = nm;
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
    private sealed record PidInfo(long At, bool Skip, ShellAction Act, string Name);
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

    private static void StopShellGuard()
    {
        Thread t;
        lock (HookGate) { t = _shellThread; _shellThread = null; _shellStop = true; }
        try { t?.Join(2000); } catch { }
        _pidCache.Clear(); _closeSeen.Clear();
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
            long lastSweep = 0;
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
            }
        }
        finally { foreach (var h in hooks) UnhookWinEvent(h); }
    }

    private static bool IsOtzariaImage(uint pid, string path)
    {
        string otz = OtzariaPath();
        if (!string.IsNullOrEmpty(path) && !string.IsNullOrEmpty(otz))
            return path.Equals(Path.GetFullPath(otz), StringComparison.OrdinalIgnoreCase); // זהות לפי נתיב, לא שם
        return IsOtzariaPid(pid); // נתיב לא קריא/לא הוגדר: נפילה לשם (תהליך מוגבה בלבד)
    }

    private static PidInfo Classify(uint pid, int session)
    {
        long now = Environment.TickCount64;
        if (_pidCache.TryGetValue(pid, out var c) && now - c.At < 1500) return c;
        PidInfo r;
        try
        {
            using var p = Process.GetProcessById((int)pid);
            if (p.SessionId != session) r = new PidInfo(now, true, ShellAction.Keep, "");
            else
            {
                string name = p.ProcessName, path = ImagePath(pid);
                if (pid == (uint)Environment.ProcessId || IsOtzariaImage(pid, path) || Programs.IsAllowedForeground(pid))
                    r = new PidInfo(now, true, ShellAction.Keep, name);
                else
                {
                    bool snip = _snipSeen != DateTime.MinValue && (DateTime.UtcNow - _snipSeen).TotalSeconds < 120;
                    var act = ShellPolicy.Decide(name, path, WinDir, snip);
                    r = new PidInfo(now, act == ShellAction.Keep, act, name);
                }
            }
        }
        catch { r = new PidInfo(now, true, ShellAction.Keep, ""); }
        _pidCache[pid] = r;
        return r;
    }

    private static void SweepWindows()
    {
        int session = Process.GetCurrentProcess().SessionId;
        UpdateForegroundAllowedCacheIfChanged();
        var victims = new Dictionary<uint, (PidInfo info, List<IntPtr> wins)>();
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h)) return true;
            if (DwmGetWindowAttribute(h, 14 /* DWMWA_CLOAKED */, out int cl, 4) == 0 && cl != 0) return true;
            if (!GetWindowRect(h, out var rc) || rc.Right <= rc.Left || rc.Bottom <= rc.Top) return true;
            GetWindowThreadProcessId(h, out uint pid);
            if (pid == 0) return true;
            var info = Classify(pid, session);
            if (info.Skip) return true;
            if (!victims.TryGetValue(pid, out var v)) victims[pid] = v = (info, new List<IntPtr>());
            v.wins.Add(h);
            return true;
        }, IntPtr.Zero);

        long now = Environment.TickCount64;
        foreach (var (pid, (info, wins)) in victims)
        {
            bool kill = info.Act == ShellAction.Kill;
            if (!kill)
            {
                // Close: קודם סגירה מנומסת; אם החלון עדיין שם אחרי >=600ms — Kill
                if (_closeSeen.TryGetValue(pid, out long first) && now - first >= 600) kill = true;
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
            LogThrottled("shell:" + info.Name, "shell guard: חלון " + info.Name + (kill ? " נהרג" : " נסגר") + " (תוכנה שאינה ברשימה)", 30);
            if (LogOnce("shellnote", 10)) Tray.Notify(Program.AppName, Lang.T("התוכנה הזו חסומה בזמן נעילה", "This program is blocked while locked"));
        }
        // ניקוי רשומות סגירה של תהליכים שנעלמו
        foreach (var k in _closeSeen.Keys) if (!victims.ContainsKey(k)) _closeSeen.TryRemove(k, out _);
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
