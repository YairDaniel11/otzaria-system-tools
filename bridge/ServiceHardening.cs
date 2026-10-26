using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Win32.SafeHandles;

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

    /// <summary>
    /// גשרים "אמיתיים" (נתיב זהה) שהשירות לא יצר בעצמו: עלו מ-Run/explorer/המשתמש, ולכן למשתמש יש handle מלא אליהם
    /// (HardenExisting לא מבטל handle קיים). נעילה נאכפת → נהרגים והשירות מעלה גשר משלו (עם DACL מרגע היצירה).
    /// WHY: DACL שנקבע בדיעבד לא סוגר handle שכבר נפתח; רק תהליך שנולד מוגן בטוח.
    /// </summary>
    /// <summary>
    /// פטורים (WHY, באג VM): תהליך השירות עצמו (אותו exe, session 0) לא יהרג בלולאת קריסה; ותהליך מורם מאותו נתיב מהימן
    /// (--unlock/--uninstall בחירום) לא נהרג תוך שניות. מורם = מנהל, ממילא יכול לעקוף. exempt: בדיקה נוספת (למשל מורם)
    /// </summary>
    internal static List<ProcInfo> Untrusted(IEnumerable<ProcInfo> procs, string serviceExe, ISet<int> ownPids, Func<ProcInfo, bool> exempt = null) =>
        procs.Where(p => SamePath(p.Path, serviceExe) && !ownPids.Contains(p.Pid)
            && p.Pid != Environment.ProcessId && p.Session != 0 && !(exempt?.Invoke(p) ?? false)).ToList();

    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr proc, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(IntPtr token, int cls, out int info, int len, out int ret);

    /// <summary>האם הטוקן של התהליך מורם (TokenElevation). כשל → false (נחשב לא מורם, כלומר ניתן להריגה)</summary>
    internal static bool IsElevated(int pid)
    {
        IntPtr h = OpenProcess(0x1000, false, pid), tok = IntPtr.Zero;
        if (h == IntPtr.Zero) return false;
        try
        {
            if (!OpenProcessToken(h, 0x8, out tok)) return false;
            return GetTokenInformation(tok, 20, out int el, 4, out _) && el != 0;
        }
        catch { return false; }
        finally { if (tok != IntPtr.Zero) CloseHandle(tok); CloseHandle(h); }
    }

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
/// אימות צד השרת של ה-pipe (בצד הלקוח), לפני שליחת סיסמה. WHY: תהליך משתמש שיצר את ה-pipe "OtzariaBridgeService" לפני
/// השירות היה מקבל את הסיסמאות בטקסט גלוי. השרת האמיתי = התהליך שה-SCM מדווח כמריץ את השירות הרשום.
/// WHY (באג VM): בדיקה לפי סשן + נתיב תמונה של תהליך השרת נכשלה למשתמש לא-מורם (OpenProcess/ProcessIdToSessionId על תהליך SYSTEM = access denied),
/// ולכן כל קריאה נחסמה. שאילתת סטטוס ב-SCM (SERVICE_QUERY_STATUS) מותרת למשתמש רגיל, ותהליך משתמש אינו יכול להיות ה-pid של השירות.
/// </summary>
internal static class PipePeer
{
    /// <summary>pid שרת ה-pipe חייב להיות זהה ל-pid שה-SCM מדווח לשירות. ערך לא ידוע (0/שלילי) = לא מהימן (fail-closed). לא תלוי בנתיב/סשן</summary>
    internal static bool IsServicePid(long pipeServerPid, long scmServicePid) =>
        pipeServerPid > 0 && scmServicePid > 0 && pipeServerPid == scmServicePid;

    [DllImport("kernel32.dll")] private static extern bool GetNamedPipeServerProcessId(IntPtr pipe, out uint pid);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenSCManager(string machine, string db, uint access);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenService(IntPtr scm, string name, uint access);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool QueryServiceStatusEx(IntPtr svc, int infoLevel, IntPtr buf, int size, out int needed);
    [DllImport("advapi32.dll")] private static extern bool CloseServiceHandle(IntPtr h);

    private const uint SC_MANAGER_CONNECT = 0x1, SERVICE_QUERY_STATUS = 0x4;

    /// <summary>ה-pid של תהליך השירות לפי ה-SCM (0 = לא רץ / אין שירות / אין גישה)</summary>
    internal static uint ServicePidFromScm(string serviceName)
    {
        IntPtr scm = OpenSCManager(null, null, SC_MANAGER_CONNECT);
        if (scm == IntPtr.Zero) return 0;
        try
        {
            IntPtr svc = OpenService(scm, serviceName, SERVICE_QUERY_STATUS);
            if (svc == IntPtr.Zero) return 0;
            IntPtr buf = Marshal.AllocHGlobal(64);   // SERVICE_STATUS_PROCESS: dwCurrentState באופסט 4, dwProcessId באופסט 28
            try
            {
                if (!QueryServiceStatusEx(svc, 0, buf, 64, out _)) return 0;
                if (Marshal.ReadInt32(buf, 4) != 4) return 0;   // לא SERVICE_RUNNING
                return (uint)Marshal.ReadInt32(buf, 28);
            }
            finally { Marshal.FreeHGlobal(buf); CloseServiceHandle(svc); }
        }
        finally { CloseServiceHandle(scm); }
    }

    /// <summary>הלקוח מאמת את השרת של ה-pipe המחובר. כל כשל (pid/SCM לא ידועים) = לא מהימן (fail-closed)</summary>
    internal static bool ServerIsService(IntPtr pipeHandle)
    {
        try
        {
            if (!GetNamedPipeServerProcessId(pipeHandle, out uint pid) || pid == 0) return false;
            return IsServicePid(pid, ServicePidFromScm(HardLock.ServiceName));
        }
        catch { return false; }
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

    /// <summary>הפסקה מינימלית בין הפעלות כשהסשן סומן "מיידי" (אירוע כניסה/ביטול נעילת מסך)</summary>
    internal static readonly TimeSpan ImmediateRetry = TimeSpan.FromSeconds(1);

    /// <summary>
    /// immediate: אירוע SERVICE_CONTROL_SESSIONCHANGE (SessionLogon/Unlock) — מדלגים על חסד הסשן, כדי שהגשר יעלה מיד אחרי Sign out/כניסה
    /// ולא רק אחרי ההשהיה של Run/הטיימר. WHY (T1): בלי זה יש חלון של שניות עם שולחן עבודה לא נעול אחרי כניסה
    /// </summary>
    internal static bool ShouldLaunch(DateTime now, DateTime sessionSeen, DateTime? lastLaunch, int consecutiveLaunches, bool immediate = false)
    {
        if (immediate) return lastLaunch == null || now - lastLaunch.Value >= ImmediateRetry;
        if (now - sessionSeen < SessionGrace) return false;
        return lastLaunch == null || now - lastLaunch.Value >= RetryAfter(consecutiveLaunches);
    }
}

/// <summary>
/// הגבלת ניסיונות סיסמה לפי לקוח (SID + האם מורם), לא גלובלית, עם backoff מדורג.
/// WHY: ה-lockout הגלובלי איפשר לכל תהליך מקומי לשלוח 5 סיסמאות שגויות ולחסום את המנהל לדקה, שוב ושוב (DoS).
/// עכשיו: אחרי maxFailures כישלונות ההשהיה גדלה אקספוננציאלית (lockSeconds, x2, ... עד maxLockSeconds), ובזמן ההשהיה
/// עדיין מותר "ניסיון בדיקה" אחד בכל probe (רבע מההשהיה, לפחות 2 שניות) — סיסמה נכונה עוברת (לא DoS), וניחוש נכשל מאריך את ההשהיה.
/// מפתח לפי SID חוסם עקיפה על ידי תהליכים חדשים, ומנהל מורם מקבל דלי נפרד. הזמן מונוטוני (TickCount64).
/// </summary>
internal sealed class AttemptLimiter
{
    private readonly int _max;
    private readonly long _lockMs, _maxLockMs, _forgetMs;
    private readonly int _cap;
    private sealed class Entry { public int Fails; public long Until, Last, Probe; }
    private readonly Dictionary<string, Entry> _d = new();

    internal AttemptLimiter(int maxFailures = 5, int lockSeconds = 60, int forgetMinutes = 10, int cap = 256, int maxLockSeconds = 900)
    {
        _max = maxFailures; _lockMs = lockSeconds * 1000L; _forgetMs = forgetMinutes * 60_000L; _cap = cap;
        _maxLockMs = Math.Max(_lockMs, maxLockSeconds * 1000L);
    }

    /// <summary>האם בהשהיה כרגע (קשיח; לבדיקות/תצוגה). הכרעת הבקשה בפועל — CanTry</summary>
    internal bool IsLocked(string key, long nowMs, out int secondsLeft)
    {
        secondsLeft = 0;
        if (key == null || !_d.TryGetValue(key, out var e) || e.Until <= nowMs) return false;
        secondsLeft = (int)Math.Ceiling((e.Until - nowMs) / 1000.0);
        return true;
    }

    /// <summary>
    /// האם מותר לבדוק סיסמה עכשיו. מחוץ להשהיה — תמיד. בתוך ההשהיה — רק כשחלף מרווח ה-probe מאז הניסיון הכושל האחרון.
    /// WHY (S1 ממצא 5): בעבר הבדיקה עצמה "שרפה" את חלון ה-probe, ולכן תוקף באותו SID שרק קורא ל-verify בלולאה תפס כל חלון
    /// וסיסמה נכונה קיבלה too_many. עכשיו CanTry לא משנה מצב: את החלון צורך רק ניסיון שנכשל בפועל (Fail), והצלחה לא נחסמת ולא צורכת.
    /// (הטיפול בשירות סדרתי תחת _gate, כך שניחושים מקבילים לא עוקפים את המרווח.)
    /// </summary>
    internal bool CanTry(string key, long nowMs, out int secondsLeft)
    {
        secondsLeft = 0;
        if (key == null || !_d.TryGetValue(key, out var e) || e.Until <= nowMs) return true;
        long probe = Math.Max(2000, (e.Until - e.Last) / 4);
        if (nowMs - e.Probe >= probe) return true;
        secondsLeft = (int)Math.Ceiling(Math.Min(e.Until - nowMs, e.Probe + probe - nowMs) / 1000.0);
        return false;
    }

    internal void Fail(string key, long nowMs)
    {
        if (key == null) return;
        Prune(nowMs);
        if (!_d.TryGetValue(key, out var e)) e = new Entry();
        if (e.Last != 0 && nowMs - e.Last > _forgetMs + Math.Max(0, e.Until - e.Last)) e = new Entry(); // ניסיון ישן — מתחילים ספירה חדשה
        e.Fails++; e.Last = nowMs;
        if (e.Fails >= _max)
        {
            int exp = Math.Min(e.Fails - _max, 20);
            e.Until = nowMs + Math.Min(_lockMs << exp, _maxLockMs);
        }
        e.Probe = nowMs; // ניסיון כושל צורך את חלון ה-probe (ר' CanTry)
        _d[key] = e;
    }

    internal void Success(string key) { if (key != null) _d.Remove(key); }

    private void Prune(long nowMs)
    {
        if (_d.Count < _cap) return;
        foreach (var k in _d.Where(kv => kv.Value.Until <= nowMs && nowMs - kv.Value.Last > _forgetMs).Select(kv => kv.Key).ToList())
            _d.Remove(k);
        if (_d.Count >= _cap) _d.Clear(); // הצפה מכוונת בהרבה מפתחות: עדיף לאפס מאשר לגדול ללא גבול
    }
}

/// <summary>
/// הגבלת חיבורי צינור במקביל: סך הכול, לפי PID ולפי SID. מקום שמור (reservedElevated) לחיבורים מורמים בלבד —
/// משתמש רגיל שממלא את המכסה לא חוסם מנהל/התקנה. דוחה (false) מעבר לגבול — הלקוח רואה חיבור שנסגר
/// </summary>
internal sealed class ConnLimiter
{
    private readonly int _total, _perPid, _perSid, _reserved;
    private readonly Dictionary<int, int> _byPid = new();
    private readonly Dictionary<string, int> _bySid = new();
    private int _count;
    private readonly object _g = new();

    internal ConnLimiter(int total = 24, int perPid = 4, int perSid = 8, int reservedElevated = 0)
    { _total = total; _perPid = perPid; _perSid = perSid; _reserved = Math.Min(reservedElevated, Math.Max(0, total - 1)); }

    internal bool TryEnter(int pid, string sid = null, bool elevated = false)
    {
        lock (_g)
        {
            _byPid.TryGetValue(pid, out int n);
            int s = 0;
            if (sid != null) _bySid.TryGetValue(sid, out s);
            int limit = elevated ? _total : _total - _reserved;
            if (_count >= limit || n >= _perPid || (sid != null && !elevated && s >= _perSid)) return false;
            _byPid[pid] = n + 1; _count++;
            if (sid != null) _bySid[sid] = s + 1;
            return true;
        }
    }

    internal void Leave(int pid, string sid = null)
    {
        lock (_g)
        {
            if (!_byPid.TryGetValue(pid, out int n)) return;
            if (n <= 1) _byPid.Remove(pid); else _byPid[pid] = n - 1;
            _count--;
            if (sid != null && _bySid.TryGetValue(sid, out int s)) { if (s <= 1) _bySid.Remove(sid); else _bySid[sid] = s - 1; }
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

    /// <summary>
    /// מכריח DOTNET_BUNDLE_EXTRACT_BASE_DIR לתיקיה שכתיבה אליה רק למנהלים (אחרי הסינון). WHY: חילוץ הספריות הנייטיב של ה-single-file
    /// הולך ל-%TEMP%\.net — כתיב למשתמש, והחלפת DLL שם = הזרקת קוד לגשר. dirReady=false (התיקיה עוד לא הוכנה/לא מחולצת בשירות)
    /// → לא נוגעים (אחרת הגשר לא היה עולה). לא ניתן לבטל את החילוץ: ה-publish פולט אז 6 DLL נפרדים (WPF) וההתקנה מעתיקה exe בלבד
    /// </summary>
    internal static List<string> WithBundleDir(List<string> filtered, string dir, bool dirReady)
    {
        if (!dirReady || string.IsNullOrEmpty(dir)) return filtered;
        var l = filtered.Where(e => !e.StartsWith("DOTNET_BUNDLE_EXTRACT_BASE_DIR=", StringComparison.OrdinalIgnoreCase)).ToList();
        l.Add("DOTNET_BUNDLE_EXTRACT_BASE_DIR=" + dir);
        return l;
    }

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

    /// <summary>
    /// SDDL ל-TokenDefaultDacl (חוטים/אובייקטים חדשים של התהליך נולדים מוקשחים). כבוי כברירת מחדל (UseTokenDefaultDacl):
    /// ה-DACL הזה חל גם על תהליכי-בן של הגשר (אוצריא ותוכנות מותרות), שיהפכו בלתי-ניתנים לסגירה/גישה למשתמש, ועל אובייקטי
    /// סנכרון משותפים — דורש בדיקה בסשן חי לפני הפעלה. חוטים חדשים מוקשחים ממילא ע"י HardenThreads כל שנייה
    /// </summary>
    internal const bool UseTokenDefaultDacl = false;
    internal static string TokenDefaultSddl() => Sddl(UserProcessMask, true);

    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool SetTokenInformation(IntPtr token, int cls, IntPtr info, int len);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetSecurityDescriptorDacl(IntPtr sd, out bool present, out IntPtr dacl, out bool defaulted);

    /// <summary>מגדיר TokenDefaultDacl (TOKEN_ADJUST_DEFAULT) בטוקן הנתון. נקרא רק כש-UseTokenDefaultDacl</summary>
    internal static bool SetTokenDefault(IntPtr token, string sddl)
    {
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor(sddl, 1, out IntPtr sd, IntPtr.Zero)) return false;
        IntPtr buf = IntPtr.Zero;
        try
        {
            if (!GetSecurityDescriptorDacl(sd, out bool present, out IntPtr dacl, out _) || !present) return false;
            buf = Marshal.AllocHGlobal(IntPtr.Size);
            Marshal.WriteIntPtr(buf, dacl);
            return SetTokenInformation(token, 6 /*TokenDefaultDacl*/, buf, IntPtr.Size);
        }
        finally { if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf); LocalFree(sd); }
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
    /// S1 ממצא 1: הסריקה רצה בטיימר ייעודי כל ThreadHardenIntervalMs (היה שנייה בתוך WatchBridge), ולכן חוט חדש חשוף עד ~200ms.
    /// החלון לא נסגר לגמרי: סגירה מלאה דורשת TokenDefaultDacl (UseTokenDefaultDacl) שנשאר כבוי מאחורי דגל עד בדיקה בסשן חי.
    /// </summary>
    internal const int ThreadHardenIntervalMs = 200;
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
    // גיבוב ה-exe נתפס פעם אחת בעליית הגשר (לפני שמשתמש הספיק להחליף את הקובץ בנתיב אחרי rename של התמונה הרצה).
    // WHY: חישוב הגיבוב בלחיצת "התקן" היה מאמת את הקובץ המוחלף עצמו ולא את מה שרץ
    private static Task<string> _self;
    internal static void CaptureSelf()
    {
        string p = Environment.ProcessPath;
        _self ??= Task.Run(() => { try { return Sha256File(p!); } catch { return null; } });
    }
    internal static string SelfSha256() { CaptureSelf(); return _self.Result; }

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
        // WHY (U3): "מחוץ ל-Program Files = כתיב למשתמש" שגוי לשני הכיוונים (C:\Tools בהרשאת מנהלים בלבד, או Program Files עם ACL רופף נבדק לפי שם).
        // בודקים DACL בפועל של הקובץ ושל התיקייה (החלפה/שינוי שם). לא ניתן לקרוא ACL / נתיב לא קיים = כתיב (fail-closed: נדרש hash)
        return AclAllowsUserWrite(full);
    }

    private static readonly System.Security.AccessControl.FileSystemRights WriteRights =
        System.Security.AccessControl.FileSystemRights.WriteData | System.Security.AccessControl.FileSystemRights.AppendData |
        System.Security.AccessControl.FileSystemRights.Delete | System.Security.AccessControl.FileSystemRights.DeleteSubdirectoriesAndFiles |
        System.Security.AccessControl.FileSystemRights.ChangePermissions | System.Security.AccessControl.FileSystemRights.TakeOwnership;

    /// <summary>האם ה-DACL של הקובץ או של תיקייתו מתיר (Allow) כתיבה/מחיקה לזהות שאינה SYSTEM/Administrators/TrustedInstaller ושייכת לטוקן הנוכחי או ל-Users/Everyone</summary>
    internal static bool AclAllowsUserWrite(string full)
    {
        try
        {
            string dir = Path.GetDirectoryName(full);
            if (!File.Exists(full) || string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return true;
            var me = System.Security.Principal.WindowsIdentity.GetCurrent();
            var mine = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (me.User != null) mine.Add(me.User.Value);
            if (me.Groups != null) foreach (var g in me.Groups) mine.Add(g.Value);
            foreach (string wk in new[] { "S-1-1-0", "S-1-5-11", "S-1-5-32-545", "S-1-5-4" }) mine.Add(wk);   // Everyone, Authenticated Users, Users, Interactive
            var trusted = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "S-1-5-18", "S-1-5-32-544", "S-1-3-0" };
            var acls = new List<System.Security.AccessControl.AuthorizationRuleCollection>
            {
                new FileInfo(full).GetAccessControl().GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier)),
                new DirectoryInfo(dir).GetAccessControl().GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier)),
            };
            foreach (var rules in acls)
                foreach (System.Security.AccessControl.FileSystemAccessRule r in rules)
                {
                    if (r.AccessControlType != System.Security.AccessControl.AccessControlType.Allow || (r.FileSystemRights & WriteRights) == 0) continue;
                    string sid = r.IdentityReference.Value;
                    if (trusted.Contains(sid) || sid.StartsWith("S-1-5-80-", StringComparison.Ordinal)) continue;   // TrustedInstaller
                    if (mine.Contains(sid)) return true;
                }
            return false;
        }
        catch { return true; }
    }

    /// <summary>גרסת ה-exe (FileVersion) או null אם אין/לא קריא/לא מספר גרסה</summary>
    internal static Version FileVer(string path)
    {
        try { return UpdateApi.Parse(System.Diagnostics.FileVersionInfo.GetVersionInfo(path).FileVersion); }
        catch { return null; }
    }

    /// <summary>
    /// true כשהיעד קיים עם גרסה קריאה והמקור נמוך ממנה, או שגרסת המקור לא קריאה (fail-closed).
    /// יעד חסר/פגום (בלי גרסה קריאה): מותר, אחרת אי אפשר לתקן התקנה שבורה. גרסה שווה: מותר (תיקון/התקנה מחדש).
    /// </summary>
    internal static bool IsDowngrade(string src, string dst)
    {
        if (!File.Exists(dst)) return false;
        var d = FileVer(dst);
        if (d == null) return false;
        var s = FileVer(src);
        return s == null || s < d;
    }

    /// <summary>
    /// מעתיק src ל-dst רק אם מותר. null = הצלחה; אחרת סיבת הסירוב. dst נכתב דרך קובץ זמני ו-Move (לא נשאר קובץ חלקי).
    /// כללים: אותו נתיב — אין מה להעתיק; מקור מחוץ למיקום כתיב-משתמש — מותר; אחרת נדרש expectedSha256 תואם.
    /// </summary>
    internal static string CopyVerified(string src, string dst, string expectedSha256, bool allowDowngrade = false)
    {
        if (BridgeProcs.SamePath(src, dst)) return null;
        // WHY (S4): בלי מונוטוניות, מי שמחזיק ב-exe ישן (חתום-hash תקף/נתיב מערכת) היה מחזיר את שירות SYSTEM לגרסה פגיעה.
        // allowDowngrade דורש אישור מפורש של הקורא (אף קורא קיים לא מעביר אותו).
        if (!allowDowngrade && IsDowngrade(src, dst)) return "ה-exe המותקן חדש יותר מזה שהתבקש להתקנה — לא מורידים גרסה בשירות (נדרש אישור מפורש)";
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

// ============================================================================
// S1: הקשחות שירות נוספות (QA עוין). לוגיקה טהורה ככל האפשר, לבדיקה ב-tests\OtzariaBridge.Tests\S1Tests.cs
// ============================================================================

/// <summary>מדיניות סיסמה אחת לשירות ולגשר (WHY: בשירות היה מינימום 6 מול 10 בגשר, ומי שפונה ישירות ל-pipe עקף את המינימום)</summary>
internal static class PasswordPolicy
{
    internal const int MinLength = 10;
    internal static bool IsValid(string pw) => pw != null && pw.Length >= MinLength;
}

/// <summary>
/// מפתח דלי ניסיונות סיסמה. WHY (S1 ממצא 5): תוקף באותו SID עם סקריפט גולמי לא יכול לשרוף את הדלי של הגשר המוגן שהשירות עצמו הפעיל
/// (תהליך עם DACL, נתיב = ServiceExe, PID ברשימת הגשרים של השירות). מנהל מורם: דלי נפרד כמו קודם.
/// </summary>
internal static class ClientBuckets
{
    internal static string Key(string sid, bool admin, bool ownBridge) =>
        (sid ?? "unknown") + (admin ? "|elevated" : "") + (ownBridge ? "|bridge" : "");
}

internal enum StateLoad { Missing, Ok, Corrupt }

/// <summary>
/// שמירה/טעינה אטומית של hardlock.json. WHY (S1 ממצא 6): Save בלי Flush ובלי גיבוי, ו-Load שבולע חריגה והופך קובץ פגום ל"לא מוגדר" (= פתוח).
/// עכשיו: כתיבה לקובץ זמני + Flush(true) + גיבוי של הקובץ התקין האחרון (.bak) + החלפה; קובץ קיים ופגום ללא גיבוי = Corrupt (נשארים נעולים).
/// </summary>
internal static class StateStore
{
    internal static string BackupPath(string path) => path + ".bak";

    private static JsonObject TryParse(string path)
    {
        try { return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject : null; }
        catch { return null; }
    }

    internal static JsonObject Read(string path, out StateLoad status, out bool restoredFromBackup)
    {
        restoredFromBackup = false;
        string bak = BackupPath(path);
        bool mainExists = File.Exists(path), bakExists = File.Exists(bak);
        var main = TryParse(path);
        if (main != null) { status = StateLoad.Ok; return main; }
        var b = TryParse(bak);
        if (b != null) { status = StateLoad.Ok; restoredFromBackup = true; return b; }
        status = (mainExists || bakExists) ? StateLoad.Corrupt : StateLoad.Missing;
        return null;
    }

    internal static void WriteAtomic(string path, string json)
    {
        string dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        string tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var bytes = new UTF8Encoding(false).GetBytes(json);
            fs.Write(bytes, 0, bytes.Length);
            fs.Flush(true); // לדיסק, לא רק למטמון מערכת ההפעלה
        }
        if (TryParse(path) != null) File.Copy(path, BackupPath(path), true); // מגבים רק קובץ תקין (לא דורסים גיבוי טוב בפגום)
        File.Move(tmp, path, true);
    }
}

/// <summary>
/// כלים שהשירות הורג כשהנעילה נאכפת. WHY (S1 ממצא 2): חסימת taskmgr/regedit דרך HKU\SID\...\Policies\System היא מפתח שהמשתמש בעלים שלו
/// ויכול לשים עליו Deny ל-SYSTEM (הכתיבה נכשלת בשקט). ההריגה בשירות היא שכבה שלא תלויה במפתח. תהליך מורם (מנהל שהזדהה ב-UAC) נחסך: מסלול חירום.
/// נתיב מ-System32/SysWOW64/%windir% בלבד: חתימה לא נבדקת כי התיקיות האלה כתיבות למנהלים בלבד.
/// </summary>
internal static class ToolGuard
{
    internal static readonly string[] Names = { "taskmgr", "regedit", "mmc" };

    /// <summary>שמות מקוריים (VERSIONINFO OriginalFilename) של הכלים. WHY (U1): העתקה של taskmgr/regedit/mmc בשם אחר ובנתיב כתיב עקפה את הזיהוי לפי שם+נתיב</summary>
    internal static readonly string[] OriginalNames = { "taskmgr.exe", "regedit.exe", "mmc.exe" };

    internal static bool ShouldKill(string name, string imagePath, bool elevated, string windowsDir, string originalFilename = null)
    {
        if (elevated || string.IsNullOrEmpty(imagePath) || string.IsNullOrEmpty(windowsDir)) return false;
        // זהות לפי המטא-דאטה של הקובץ: מכל נתיב ובכל שם (העתק שנקרא calc.exe נהרג גם הוא)
        if (!string.IsNullOrEmpty(originalFilename) && OriginalNames.Contains(originalFilename.Trim(), StringComparer.OrdinalIgnoreCase)) return true;
        if (!Names.Contains(name, StringComparer.OrdinalIgnoreCase)) return false;
        string exe = name + ".exe";
        foreach (string sub in new[] { "System32", "SysWOW64", "" })
            if (BridgeProcs.SamePath(imagePath, Path.Combine(windowsDir, sub, exe))) return true;
        return false;
    }
}

/// <summary>
/// אכיפת אתחול (Safe Mode/WinRE/bcd) ללא תלות בנעילה "נאכפת". WHY (S1 ממצא 3): בזמן פתיחה זמנית/מחוץ לחלון תזמון Safe Mode ו-WinRE היו נפתחים,
/// ואתחול ל-Safe Mode בחלון כזה עוקף את הגשר. עכשיו: כל עוד השירות Configured (או מצבו פגום) ההקשחה נשארת. שחרור רק בהסרה מלאה (uninstall בסיסמה).
/// </summary>
internal static class BootPolicy
{
    internal static bool ShouldHarden(bool configured, bool uninstalling) => configured && !uninstalling;
}

/// <summary>סודות משורת הפקודה (S1 ממצא 4): שורת פקודה נחשפת לכל משתמש (Task Manager/WMI/היסטוריה); מעדיפים stdin/הקלדה מוסתרת</summary>
internal static class CliSecret
{
    /// <summary>הערך שאחרי הדגל בשורת הפקודה, או null אם אין / "-" (stdin) / הערך הבא הוא דגל</summary>
    internal static string InlineValue(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        if (i < 0 || i + 1 >= args.Length) return null;
        string v = args[i + 1];
        return v == "-" || v.StartsWith("--") ? null : v;
    }

    [DllImport("kernel32.dll")] private static extern IntPtr GetStdHandle(int n);
    [DllImport("kernel32.dll")] private static extern uint GetFileType(IntPtr h);
    [DllImport("kernel32.dll")] private static extern bool GetConsoleMode(IntPtr h, out uint mode);
    [DllImport("kernel32.dll")] private static extern bool SetConsoleMode(IntPtr h, uint mode);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr sa, uint disp, uint flags, IntPtr tmpl);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ReadConsoleW(IntPtr h, char[] buf, uint n, out uint read, IntPtr reserved);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);

    /// <summary>קורא סוד: stdin כשהוא מופנה (pipe/קובץ), אחרת הקלדה בלי הד מהקונסול (CONIN$). null = לא התקבל</summary>
    internal static string ReadSecret(string prompt)
    {
        try
        {
            IntPtr h = GetStdHandle(-10);
            uint ft = h == IntPtr.Zero || h == new IntPtr(-1) ? 0 : GetFileType(h);
            if (ft == 1 || ft == 3) // DISK / PIPE
            {
                using var sr = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
                return sr.ReadLine()?.TrimEnd('\r');
            }
            Console.Write(prompt);
            IntPtr con = CreateFileW("CONIN$", 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (con == IntPtr.Zero || con == new IntPtr(-1)) return null;
            try
            {
                bool hadMode = GetConsoleMode(con, out uint mode);
                if (hadMode) SetConsoleMode(con, mode & ~0x4u); // בלי ECHO
                var buf = new char[512];
                bool ok = ReadConsoleW(con, buf, (uint)buf.Length, out uint n, IntPtr.Zero);
                if (hadMode) SetConsoleMode(con, mode);
                Console.WriteLine();
                return ok ? new string(buf, 0, (int)n).TrimEnd('\r', '\n') : null;
            }
            finally { CloseHandle(con); }
        }
        catch { return null; }
    }

    /// <summary>ערך מהדגל (עם אזהרה כשמופיע בשורת הפקודה: תאימות לאחור), אחרת stdin/הקלדה. לעולם לא null</summary>
    internal static string Resolve(string[] args, string name, string prompt, string warning)
    {
        string inline = InlineValue(args, name);
        if (inline != null) { Console.WriteLine(warning); return inline; }
        return ReadSecret(prompt) ?? "";
    }
}

/// <summary>
/// טוקני שחרור חירום חד-פעמיים (challenge-response), בזיכרון השירות בלבד. WHY (U1): בעבר "הוכחת" קוד השחזור בקובץ הסימון (emergency-release.req,
/// בתיקיית המשתמש) הייתה שווה ל-hash השמור, ולכן מי שקרא את ה-hash מ-config.json יכל לכתוב קובץ סימון ולשחרר את הנעילה (pass-the-hash).
/// עכשיו: השירות מנפיק טוקן אקראי רק אחרי אימות קוד השחזור עצמו בפייפ, הקובץ מכיל רק את הטוקן, והגשר צורך אותו מול השירות (תפוגה קצרה, שימוש יחיד).
/// </summary>
internal sealed class RecoveryTokens
{
    internal const long TtlMs = 120_000;
    internal const int Max = 8;
    private readonly List<(string tok, long exp)> _l = new();

    internal string Mint(long nowMs)
    {
        Prune(nowMs);
        if (_l.Count >= Max) _l.RemoveAt(0);
        string t = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
        _l.Add((t, nowMs + TtlMs));
        return t;
    }

    /// <summary>צורך טוקן: true פעם אחת בלבד, ורק לפני התפוגה. השוואה בזמן קבוע</summary>
    internal bool Consume(string token, long nowMs)
    {
        Prune(nowMs);
        if (string.IsNullOrEmpty(token) || token.Length > 100) return false;
        byte[] want = Encoding.UTF8.GetBytes(token);
        int found = -1;
        for (int i = 0; i < _l.Count; i++)
            if (CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(_l[i].tok), want)) found = i;
        if (found < 0) return false;
        _l.RemoveAt(found);
        return true;
    }

    private void Prune(long nowMs) => _l.RemoveAll(x => x.exp <= nowMs);
}

/// <summary>סיבוב יומן השירות: מעל המגבלה הקובץ עובר ל-.1 (דורס .1 קודם) במקום להימחק. WHY (U1): מחיקה הייתה מאבדת את הראיות בדיוק כשיש הרבה אירועים</summary>
internal static class LogRotator
{
    internal static void RotateIfLarge(string path, long maxBytes)
    {
        try
        {
            if (File.Exists(path) && new FileInfo(path).Length > maxBytes) File.Move(path, path + ".1", true);
        }
        catch { }
    }
}

/// <summary>
/// הגבלת כתיבה ליומן: אותה שורה לא נכתבת שוב תוך חלון קצר, וסך השורות בדקה מוגבל. WHY (U1): קריאות פגומות/לולאת שגיאות מילאו את היומן
/// (ודחקו החוצה את הראיות בסיבוב)
/// </summary>
internal sealed class LogThrottle
{
    private readonly long _dupMs;
    private readonly int _perMinute;
    private string _last;
    private long _lastAt, _winStart;
    private int _inWin, _dropped;

    internal LogThrottle(long dupWindowMs = 10_000, int perMinute = 60) { _dupMs = dupWindowMs; _perMinute = perMinute; }

    /// <summary>true = לכתוב. suppressed: כמה שורות נבלעו מאז הכתיבה הקודמת (לציון בשורה הבאה)</summary>
    internal bool Allow(string line, long nowMs, out int suppressed)
    {
        suppressed = 0;
        if (nowMs - _winStart >= 60_000) { _winStart = nowMs; _inWin = 0; }
        if (line == _last && nowMs - _lastAt < _dupMs) { _dropped++; return false; }
        if (_inWin >= _perMinute) { _dropped++; return false; }
        _inWin++;
        _last = line; _lastAt = nowMs;
        suppressed = _dropped; _dropped = 0;
        return true;
    }
}

/// <summary>
/// הגדרות התאוששות השירות ב-SCM.
/// WHY: reset=86400 עם 3 פעולות restart בלבד גרר שאחרי הכשל הרביעי ביממה אין פעולה והשירות נשאר למטה (נעילה נעלמת).
/// עכשיו: חלון איפוס קצר (15 דקות) ושרשרת restart ארוכה שהפעולה האחרונה בה חוזרת (SCM חוזר על הפעולה האחרונה),
/// ו-failureflag כדי שגם יציאה עם קוד שגיאה (לא רק קריסה) תפעיל התאוששות.
/// </summary>
internal static class ServiceRecovery
{
    internal const int ResetSeconds = 900;
    internal const string Actions = "restart/5000/restart/5000/restart/10000/restart/30000/restart/60000";

    internal static string[] FailureArgs(string service) => new[]
    {
        $"failure {service} reset= {ResetSeconds} actions= {Actions}",
        $"failureflag {service} 1",
    };
}

/// <summary>
/// backoff מעריכי להגבלת ניסיונות סיסמה בגשר (מצב מקומי, כשהשירות לא זמין). מצב טהור + נשמר ב-Config.
/// WHY: קודם 5 ניסיונות = דקה בזיכרון בלבד ואיפוס באתחול. עכשיו הספירה והמועד נשמרים בהגדרות (חתומות ע"י ConfigSeal כשהשירות פעיל),
/// וההשהיה גדלה 1,2,4,... דקות עד שעה. סיסמה נכונה מאפסת. השעה נחתכת לתקרה כך ששעון שגוי לא נועל מעבר לשעה.
/// </summary>
internal static class BridgeBackoff
{
    internal const int Threshold = 5, BaseSeconds = 60, CapSeconds = 3600, ForgetHours = 24;

    internal static int LockSeconds(int fails) =>
        fails < Threshold ? 0 : (int)Math.Min((long)BaseSeconds << Math.Min(fails - Threshold, 20), CapSeconds);

    /// <summary>מועד סיום ההשהיה (UTC ticks) לאחר ספירת כשלונות; 0 = אין. חתוך לתקרה ביחס ל-now</summary>
    internal static long ClampUntil(long untilTicks, long nowTicks) =>
        untilTicks <= nowTicks ? 0 : Math.Min(untilTicks, nowTicks + TimeSpan.TicksPerSecond * CapSeconds);

    /// <summary>ספירה ישנה (יותר מ-ForgetHours מהכשל האחרון) נשכחת</summary>
    internal static int EffectiveFails(int fails, long lastTicks, long nowTicks) =>
        lastTicks > 0 && nowTicks - lastTicks > TimeSpan.TicksPerHour * ForgetHours ? 0 : Math.Max(0, fails);
}
