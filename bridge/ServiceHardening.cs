using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace OtzariaBridge;

// ============================================================================
// לוגיקה מוקשחת של השירות (HardLockService) — בנויה מפונקציות טהורות ככל האפשר, כדי שאפשר לבדוק אותן ב-xunit.
// מדיניות: מקור האמת הוא השירות; זהות לפי נתיב ולא לפי שם תהליך; כשל נסגר (ר' HARDENING_POLICY).
// ============================================================================

/// <summary>תהליך גשר שנמצא בסריקה: מזהה, סשן ונתיב התמונה המלא (null = לא נקרא)</summary>
internal readonly record struct ProcInfo(int Pid, int Session, string Path);

/// <summary>
/// זיהוי "הגשר רץ" לפי נתיב ה-exe של השירות (Program Files, כתיב רק למנהלים) ולא לפי שם.
/// WHY: תהליך בשם OtzariaBridge מנתיב אחר (עותק של משתמש, או תוכנה זדונית שהוסבה) הספיק קודם כדי שהשירות
/// "יחשוב" שהגשר רץ בסשן ולא יפעיל אותו — והנעילה לא נאכפה בסשן.
/// </summary>
internal static class BridgeProcs
{
    internal static bool SamePath(string a, string b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
        try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    /// <summary>הסשנים שבהם רץ הגשר האמיתי (נתיב זהה ל-exe). נתיב לא ידוע / שונה — לא נחשב</summary>
    internal static HashSet<int> SessionsRunning(IEnumerable<ProcInfo> procs, string serviceExe) =>
        procs.Where(p => SamePath(p.Path, serviceExe)).Select(p => p.Session).ToHashSet();

    /// <summary>תהליכים בשם הגשר מנתיב אחר (נתיב ידוע בלבד — תהליך שלא ניתן לקרוא לא נהרג). מחזיקים את ה-mutex ומונעים עלייה של הגשר האמיתי</summary>
    internal static List<ProcInfo> Impostors(IEnumerable<ProcInfo> procs, string serviceExe) =>
        procs.Where(p => !string.IsNullOrEmpty(p.Path) && !SamePath(p.Path, serviceExe)).ToList();

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder name, ref int size);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);

    /// <summary>נתיב ה-exe של תהליך (PROCESS_QUERY_LIMITED_INFORMATION). null אם אין גישה</summary>
    internal static string ImagePath(int pid)
    {
        IntPtr h = OpenProcess(0x1000, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString(0, size) : null;
        }
        finally { CloseHandle(h); }
    }

    /// <summary>סורק תהליכים בשם "OtzariaBridge" (סינון ראשוני בלבד; ההחלטה לפי נתיב)</summary>
    internal static List<ProcInfo> Scan()
    {
        var list = new List<ProcInfo>();
        foreach (var p in Process.GetProcessesByName("OtzariaBridge"))
        {
            using (p)
            {
                try { list.Add(new ProcInfo(p.Id, p.SessionId, ImagePath(p.Id))); } catch { }
            }
        }
        return list;
    }
}

/// <summary>
/// מדיניות הפעלה מחדש של הגשר בנעילה. קודם: 15 שניות חסד אחרי כניסה ו-20 שניות בין ניסיונות — חלון של עד חצי דקה
/// בלי גשר. עכשיו: 3 שניות (השירות סורק כל שנייה). ניסיונות רצופים שלא הצליחו להשאיר גשר רץ נעשים איטיים יותר,
/// כדי שגשר שקורס בעלייה (או mutex תפוס) לא ייצור לולאה חמה.
/// </summary>
internal static class RelaunchPolicy
{
    internal static readonly TimeSpan SessionGrace = TimeSpan.FromSeconds(3);
    internal static readonly TimeSpan FastRetry = TimeSpan.FromSeconds(3);
    internal static readonly TimeSpan SlowRetry = TimeSpan.FromSeconds(10);
    internal const int FastAttempts = 5;

    internal static TimeSpan RetryAfter(int consecutiveLaunches) => consecutiveLaunches < FastAttempts ? FastRetry : SlowRetry;

    internal static bool ShouldLaunch(DateTime now, DateTime sessionSeen, DateTime? lastLaunch, int consecutiveLaunches)
    {
        if (now - sessionSeen < SessionGrace) return false;
        return lastLaunch == null || now - lastLaunch.Value >= RetryAfter(consecutiveLaunches);
    }
}

/// <summary>
/// הגבלת ניסיונות סיסמה לפי לקוח (SID + האם מורם), לא גלובלית.
/// WHY: ה-lockout הגלובלי איפשר לכל תהליך מקומי לשלוח 5 סיסמאות שגויות ולחסום את המנהל לדקה, שוב ושוב (DoS).
/// מפתח לפי SID חוסם עקיפה על ידי תהליכים חדשים (PID חדש לא מאפס), ומנהל מורם מקבל דלי נפרד מהמשתמש הלא-מורם.
/// הזמן מונוטוני (TickCount64) — העברת שעון לא מאפסת חסימה.
/// </summary>
internal sealed class AttemptLimiter
{
    private readonly int _max;
    private readonly long _lockMs, _forgetMs;
    private readonly int _cap;
    private readonly Dictionary<string, (int fails, long until, long last)> _d = new();

    internal AttemptLimiter(int maxFailures = 5, int lockSeconds = 60, int forgetMinutes = 10, int cap = 256)
    {
        _max = maxFailures; _lockMs = lockSeconds * 1000L; _forgetMs = forgetMinutes * 60_000L; _cap = cap;
    }

    internal bool IsLocked(string key, long nowMs, out int secondsLeft)
    {
        secondsLeft = 0;
        if (key == null || !_d.TryGetValue(key, out var e) || e.until <= nowMs) return false;
        secondsLeft = (int)Math.Ceiling((e.until - nowMs) / 1000.0);
        return true;
    }

    internal void Fail(string key, long nowMs)
    {
        if (key == null) return;
        Prune(nowMs);
        _d.TryGetValue(key, out var e);
        if (e.last != 0 && nowMs - e.last > _forgetMs) e = default; // ניסיון ישן — מתחילים ספירה חדשה
        e.fails++; e.last = nowMs;
        if (e.fails >= _max) { e.fails = 0; e.until = nowMs + _lockMs; }
        _d[key] = e;
    }

    internal void Success(string key) { if (key != null) _d.Remove(key); }

    private void Prune(long nowMs)
    {
        if (_d.Count < _cap) return;
        foreach (var k in _d.Where(kv => kv.Value.until <= nowMs && nowMs - kv.Value.last > _forgetMs).Select(kv => kv.Key).ToList())
            _d.Remove(k);
        if (_d.Count >= _cap) _d.Clear(); // הצפה מכוונת בהרבה מפתחות: עדיף לאפס מאשר לגדול ללא גבול
    }
}

/// <summary>הגבלת חיבורי צינור במקביל: סך הכול ולפי PID. דוחה (false) מעבר לגבול — הלקוח רואה חיבור שנסגר</summary>
internal sealed class ConnLimiter
{
    private readonly int _total, _perPid;
    private readonly Dictionary<int, int> _byPid = new();
    private int _count;
    private readonly object _g = new();

    internal ConnLimiter(int total = 24, int perPid = 4) { _total = total; _perPid = perPid; }

    internal bool TryEnter(int pid)
    {
        lock (_g)
        {
            _byPid.TryGetValue(pid, out int n);
            if (_count >= _total || n >= _perPid) return false;
            _byPid[pid] = n + 1; _count++;
            return true;
        }
    }

    internal void Leave(int pid)
    {
        lock (_g)
        {
            if (!_byPid.TryGetValue(pid, out int n)) return;
            if (n <= 1) _byPid.Remove(pid); else _byPid[pid] = n - 1;
            _count--;
        }
    }
}

/// <summary>
/// סינון בלוק הסביבה של תהליך המשתמש שהשירות מפעיל. CreateEnvironmentBlock בונה את הבלוק מ-HKCU\Environment —
/// שהמשתמש כותב אליו. DOTNET_STARTUP_HOOKS / COR_PROFILER / DOTNET_* אחרים היו מזריקים קוד ל-.NET של הגשר
/// (גם עם StartupHookSupport=false מסננים, כשכבה נוספת: COR_PROFILER אינו מושפע ממנו).
/// </summary>
internal static class EnvBlock
{
    private static readonly string[] BlockedPrefixes = { "DOTNET_", "COMPlus_", "COR_", "CORECLR_" };

    internal static bool IsBlocked(string name) =>
        BlockedPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    /// <summary>רשומות "NAME=VALUE" בלי אלה החסומות. רשומות "=C:=..." (שמות שמתחילים ב-=) נשמרות</summary>
    internal static List<string> Filter(IEnumerable<string> entries) =>
        entries.Where(e =>
        {
            int eq = e.IndexOf('=', 1 < e.Length && e[0] == '=' ? 1 : 0);
            string name = eq > 0 ? e[..eq] : e;
            return !IsBlocked(name);
        }).ToList();

    /// <summary>מפענח בלוק סביבה בפורמט Unicode (מחרוזות מסתיימות ב-NUL, סיום בריקה)</summary>
    internal static List<string> Parse(IntPtr block)
    {
        var list = new List<string>();
        if (block == IntPtr.Zero) return list;
        IntPtr p = block;
        while (true)
        {
            string s = Marshal.PtrToStringUni(p) ?? "";
            if (s.Length == 0) break;
            list.Add(s);
            p += (s.Length + 1) * 2;
        }
        return list;
    }

    /// <summary>בונה בלוק Unicode בזיכרון לא מנוהל (לשחרר ב-FreeHGlobal)</summary>
    internal static IntPtr Build(IEnumerable<string> entries)
    {
        string joined = string.Concat(entries.Select(e => e + "\0")) + "\0";
        if (joined == "\0") joined = "\0\0"; // בלוק ריק חוקי = שני NUL
        return Marshal.StringToHGlobalUni(joined + "\0");
    }
}

/// <summary>
/// DACL על תהליך הגשר שהשירות מפעיל: המשתמש (Authenticated Users) לא מקבל PROCESS_TERMINATE, VM_WRITE,
/// VM_OPERATION, CREATE_THREAD, DUP_HANDLE, SUSPEND_RESUME, SET_INFORMATION, WRITE_DAC, WRITE_OWNER.
/// נשארים רק QUERY_LIMITED/QUERY_INFORMATION/SYNCHRONIZE/READ_CONTROL (מנהל המשימות עדיין מציג את התהליך).
/// SYSTEM ומנהלים (מורמים) — שליטה מלאה, כדי שתמיד יהיה מסלול חירום (מנהל מורם, sc stop, הסרה).
/// WHY: בלי זה משתמש רגיל סוגר את הגשר ב-Task Manager/taskkill או מזריק אליו קוד, והנעילה בסשן נעלמת עד שהשירות מפעיל מחדש.
/// </summary>
internal static class ProcessDacl
{
    // PROCESS_QUERY_INFORMATION 0x400 | PROCESS_QUERY_LIMITED_INFORMATION 0x1000 | READ_CONTROL 0x20000 | SYNCHRONIZE 0x100000
    internal const uint UserProcessMask = 0x400 | 0x1000 | 0x20000 | 0x100000;
    // THREAD_QUERY_LIMITED_INFORMATION 0x800 | READ_CONTROL | SYNCHRONIZE (בלי TERMINATE/SUSPEND_RESUME/SET_CONTEXT...)
    internal const uint UserThreadMask = 0x800 | 0x20000 | 0x100000;

    /// <summary>הרשאות שאסור שיופיעו אף פעם במסכה של משתמש רגיל על התהליך</summary>
    internal const uint ForbiddenProcessBits =
        0x0001 /*TERMINATE*/ | 0x0002 /*CREATE_THREAD*/ | 0x0008 /*VM_OPERATION*/ | 0x0020 /*VM_WRITE*/ | 0x0040 /*DUP_HANDLE*/ |
        0x0080 /*CREATE_PROCESS*/ | 0x0100 /*SET_QUOTA*/ | 0x0200 /*SET_INFORMATION*/ | 0x0800 /*SUSPEND_RESUME*/ |
        0x40000 /*WRITE_DAC*/ | 0x80000 /*WRITE_OWNER*/ | 0x10000 /*DELETE*/;

    /// <summary>SDDL: מוגן (P) — SYSTEM ומנהלים GA; משתמשים מאומתים — מסכה מצומצמת (hardened) או GA (relaxed)</summary>
    internal static string Sddl(uint userMask, bool hardened, bool includeAdmins = true) =>
        "D:P(A;;GA;;;SY)" + (includeAdmins ? "(A;;GA;;;BA)" : "") +
        // OWNER RIGHTS: בלי ה-ACE הזה הבעלים (המשתמש) מקבל WRITE_DAC/READ_CONTROL במובלע ויכול לכתוב מחדש את ה-DACL ולהרוג
        (hardened ? "(A;;RC;;;OW)" : "") +
        "(A;;" + (hardened ? "0x" + userMask.ToString("X") : "GA") + ";;;AU)";

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string sddl, uint rev, out IntPtr sd, IntPtr size);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool SetKernelObjectSecurity(IntPtr h, uint info, IntPtr sd);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr p);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenThread(uint access, bool inherit, int tid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);

    [StructLayout(LayoutKind.Sequential)]
    internal struct SECURITY_ATTRIBUTES { public int nLength; public IntPtr lpSecurityDescriptor; public bool bInheritHandle; }

    /// <summary>מקצה SECURITY_ATTRIBUTES (ו-SD) לשימוש ב-CreateProcess*. לשחרר ב-FreeAttributes</summary>
    internal static IntPtr AllocAttributes(string sddl, out IntPtr sd)
    {
        sd = IntPtr.Zero;
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor(sddl, 1, out sd, IntPtr.Zero)) return IntPtr.Zero;
        var sa = new SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(), lpSecurityDescriptor = sd, bInheritHandle = false };
        IntPtr p = Marshal.AllocHGlobal(sa.nLength);
        Marshal.StructureToPtr(sa, p, false);
        return p;
    }

    internal static void FreeAttributes(IntPtr sa, IntPtr sd)
    {
        if (sa != IntPtr.Zero) Marshal.FreeHGlobal(sa);
        if (sd != IntPtr.Zero) LocalFree(sd);
    }

    private static bool SetDacl(IntPtr handle, string sddl)
    {
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor(sddl, 1, out IntPtr sd, IntPtr.Zero)) return false;
        try { return SetKernelObjectSecurity(handle, 0x4 /*DACL_SECURITY_INFORMATION*/, sd); }
        finally { LocalFree(sd); }
    }

    /// <summary>הגשר על עצמו (הקשחה או שחרור). ה-pseudo handle נושא את כל ההרשאות, ולכן אין תלות ב-DACL הנוכחי</summary>
    internal static bool ApplyToSelf(bool hardened) => SetDacl(GetCurrentProcess(), Sddl(UserProcessMask, hardened));

    /// <summary>השירות (SYSTEM) מקשיח תהליך גשר קיים (למשל כזה שעלה מ-Run של המשתמש) ואת כל החוטים שלו</summary>
    internal static bool HardenExisting(int pid)
    {
        bool ok = false;
        IntPtr h = OpenProcess(0x40000 /*WRITE_DAC*/, false, pid);
        if (h != IntPtr.Zero)
        {
            try { ok = SetDacl(h, Sddl(UserProcessMask, true)); }
            finally { CloseHandle(h); }
        }
        return ok;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct THREADENTRY32 { public uint dwSize, cntUsage, th32ThreadID, th32OwnerProcessID; public int tpBasePri, tpDeltaPri; public uint dwFlags; }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll")] private static extern bool Thread32First(IntPtr snap, ref THREADENTRY32 e);
    [DllImport("kernel32.dll")] private static extern bool Thread32Next(IntPtr snap, ref THREADENTRY32 e);

    /// <summary>
    /// מקשיח את חוטי התהליכים הנתונים (THREAD_TERMINATE/SUSPEND_RESUME/SET_CONTEXT חסומים למשתמש). WHY: ה-DACL של חוט חדש
    /// נגזר מ-DefaultDacl של הטוקן ולא מה-DACL של התהליך — משתמש היה מקפיא/הורג חוט בודד של הגשר (למשל חוט הנעילה).
    /// הסריקה חוזרת כל שנייה, ולכן חוט חדש חשוף עד שנייה אחת לכל היותר.
    /// </summary>
    internal static void HardenThreads(ISet<int> pids)
    {
        if (pids.Count == 0) return;
        IntPtr snap = CreateToolhelp32Snapshot(0x4 /*TH32CS_SNAPTHREAD*/, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return;
        try
        {
            string sddl = Sddl(UserThreadMask, true);
            var e = new THREADENTRY32 { dwSize = (uint)Marshal.SizeOf<THREADENTRY32>() };
            if (!Thread32First(snap, ref e)) return;
            do
            {
                if (!pids.Contains((int)e.th32OwnerProcessID)) continue;
                IntPtr th = OpenThread(0x40000 /*WRITE_DAC*/, false, (int)e.th32ThreadID);
                if (th == IntPtr.Zero) continue;
                try { SetDacl(th, sddl); } finally { CloseHandle(th); }
            } while (Thread32Next(snap, ref e));
        }
        finally { CloseHandle(snap); }
    }
}

/// <summary>
/// אימות ה-exe לפני התקנה מוגברת. WHY: התקנת השירות רצה כמנהל ומעתיקה את ה-exe אל Program Files; בלי בדיקה, משתמש
/// שמחליף את הקובץ ב-%LOCALAPPDATA% (הוא הבעלים שלו) בין לחיצת "התקן" לבין ההעתקה היה מקבל קוד שרץ כ-SYSTEM.
/// הגשר הלא-מורם מחשב SHA-256 של ה-exe שלו ומעביר ל-<c>--src-sha256</c>; התהליך המורם קורא את הקובץ פעם אחת לזיכרון,
/// מוודא שהגיבוב זהה, וכותב לדיסק את אותם בתים בדיוק (אין חלון בין "אימות" ל"העתקה").
/// מגבלה: אם הקובץ הוחלף לפני שה-UAC הופעל, התהליך המורם הוא כבר הקובץ המוחלף ואינו מאמת את עצמו;
/// הגנה מלאה דורשת חתימת Authenticode (לא קיימת בגרסאות הנוכחיות).
/// </summary>
internal static class InstallTrust
{
    internal static string Sha256(byte[] data) => Convert.ToHexString(SHA256.HashData(data));

    internal static string Sha256File(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(fs));
    }

    /// <summary>האם הנתיב אינו תחת Program Files / Windows (כלומר משתמש רגיל יכול לכתוב אליו)</summary>
    internal static bool IsUserWritableLocation(string path)
    {
        string full;
        try { full = Path.GetFullPath(path); } catch { return true; }
        foreach (var f in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.Windows })
        {
            string root = Environment.GetFolderPath(f);
            if (root.Length == 0) continue;
            root = Path.GetFullPath(root).TrimEnd('\\') + "\\";
            if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    /// <summary>
    /// מעתיק src ל-dst רק אם מותר. null = הצלחה; אחרת סיבת הסירוב. dst נכתב דרך קובץ זמני ו-Move (לא נשאר קובץ חלקי).
    /// כללים: אותו נתיב — אין מה להעתיק; מקור מחוץ למיקום כתיב-משתמש — מותר; אחרת נדרש expectedSha256 תואם.
    /// </summary>
    internal static string CopyVerified(string src, string dst, string expectedSha256)
    {
        if (BridgeProcs.SamePath(src, dst)) return null;
        byte[] data;
        try { data = File.ReadAllBytes(src); }
        catch (Exception ex) { return "קריאת ה-exe נכשלה: " + ex.Message; }
        string actual = Sha256(data);
        bool expectedOk = !string.IsNullOrEmpty(expectedSha256) && string.Equals(expectedSha256, actual, StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(expectedSha256) && !expectedOk)
            return "ה-exe השתנה מאז שהבקשה נשלחה (גיבוב לא תואם) — ההתקנה נעצרה";
        if (IsUserWritableLocation(src) && !expectedOk)
            return "ה-exe נמצא בנתיב שמשתמש רגיל כותב אליו ולא התקבל גיבוב לאימות — ההתקנה נעצרה";
        string tmp = dst + ".new";
        try
        {
            File.WriteAllBytes(tmp, data);
            // exe שרץ אי אפשר לדרוס אבל אפשר לשנות לו שם: מזיזים הצידה, ואז ממקמים את החדש
            string old = dst + ".old";
            if (File.Exists(dst))
            {
                try { File.Delete(old); } catch { }
                try { File.Move(dst, old, true); } catch { }
            }
            File.Move(tmp, dst, true);
            try { File.Delete(old); } catch { } // נכשל כשהישן עדיין רץ — יימחק בהתקנה הבאה
        }
        catch (Exception ex)
        {
            try { File.Delete(tmp); } catch { }
            return "העתקת ה-exe נכשלה: " + ex.Message;
        }
        return null;
    }
}
