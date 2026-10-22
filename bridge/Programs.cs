using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace OtzariaBridge;

/// <summary>
/// תוכנות מותרות לפתיחה מתוך אוצריא גם במצב נעילה (כתבי הקודש, אוצר החכמה, בר אילן, Word ועוד).
/// הרשימה מוגדרת בסיסמה בלבד. הפתיחה היא לפי מזהה מהרשימה — אף פעם לא לפי נתיב חופשי.
/// בזמן שתוכנה מהרשימה בחזית, השומר של הנעילה לא מחזיר את אוצריא לחזית.
/// </summary>
public static class Programs
{
    private const string Key = "kiosk.programs";
    private const string AutoKey = "kiosk.autoTorahPrograms";
    private const string KeywordsKey = "kiosk.autoTorahKeywords";
    internal const int MaxPrograms = 20;
    private const int GraceSeconds = 30;   // [w3] רק ל-GrantForegroundGrace (Hello/סייר), לא ל-Activate

    // מעטפות ומנהלי מערכת שאסור להוסיף לרשימה (עוקף את הנעילה)
    // הזיהוי הוא לפי זהות (ProcessName, שם הקובץ ו-OriginalFilename מה-VersionInfo) — ר' IsBlockedProc / AppTrust.IsBlockedIdentity
    private static readonly HashSet<string> Blocked = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "cmd", "powershell", "pwsh", "wscript", "cscript", "mshta", "regedit",
        "taskmgr", "mmc", "control", "conhost", "wt", "rundll32", "msiexec",
        "powershell_ise", "windowsterminal", "openconsole", "wsl", "bash",
        // [w3] הרחבה: כלי LOLBin, ניהול מערכת, נגישות וחלונות הגדרות
        "wmic", "forfiles", "pcalua", "msbuild", "csc", "installutil", "regsvr32", "certutil", "bitsadmin", "dism",
        "schtasks", "sc", "net", "net1", "reg", "curl", "ftp", "eventvwr", "mstsc", "SystemSettings", "ApplicationFrameHost",
        "osk", "narrator", "magnify", "utilman", "notepad", "mspaint",
        // [r1] דפדפנים (עוקפים את הרשימה: ניווט חופשי/הורדות) וכלי ניהול מערכת
        "msedge", "chrome", "firefox", "iexplore", "brave", "opera", "vivaldi",
        "perfmon", "msconfig", "resmon", "taskschd", "dxdiag", "msinfo32", "compmgmt", "services", "devmgmt", "diskmgmt",
        "lusrmgr", "secpol", "gpedit", "cleanmgr", "psr", "mdsched", "msra", "quickassist", "odbcad32", "dfrgui",
        // [s2] WHY: מאקרו VBA ב-Word (Alt+F11 / מסמך עם מאקרו) מריץ כל תהליך; אלה ה-LOLBins/כלי העזר שנמצאים במיקום מהימן (Windows/Program Files)
        // ולכן עברו את בדיקת המיקום כצאצאים. calc לא נחסם בכוונה (מחשבון ללא יכולת הרצה/פתיחת קבצים; חסימה = false-positive למשתמשים)
        "hh", "msdt", "cmstp", "odbcconf", "wordpad", "write", "charmap", "regasm", "regsvcs", "mavinject", "wusa", "expand",
        "extrac32", "makecab", "ieexec", "infdefaultinstall", "presentationhost", "syncappvpublishingserver", "scriptrunner",
        "jsc", "vbc", "rcsi", "csi", "cdb", "ntsd", "windbg", "iexpress", "sdbinst", "verclsid", "xwizard", "eudcedit",
        "fsquirt", "dccw", "telnet", "tftp", "ssh", "scp", "sftp", "winrs", "pktmon", "netsh", "dnscmd", "fltmc",
    };

    // בעיה #14 סעיף 6: נכתבים בחוט בקשה (Activate) ונקראים בחוט השומר (ShouldYieldTo וכו') —
    // DateTime אינו Interlocked-ידידותי, ולכן ננעלים עם אובייקט נעילה קטן ייעודי
    private static readonly object GraceGate = new();
    private static DateTime _graceUntil = DateTime.MinValue;
    private static DateTime _lastLaunchAt = DateTime.MinValue;
    private static DateTime GraceUntil { get { lock (GraceGate) return _graceUntil; } set { lock (GraceGate) _graceUntil = value; } }
    private static DateTime LastLaunchAt { get { lock (GraceGate) return _lastLaunchAt; } set { lock (GraceGate) _lastLaunchAt = value; } }

    public sealed class Entry
    {
        public string Id = "";
        public string Name = "";
        public string Path = "";
        public string[] Procs = Array.Empty<string>();
        public bool Auto;
        // [w3] נעיצת זהות (נקבעת בעת Add, נבדקת בכל Launch ובהתאמת תהליך): מפרסם Authenticode ו/או SHA-256
        public string Sha256 = "";
        public string Signer = "";
        public bool Pinned => AppTrust.HasPin(Sha256, Signer);
    }

    // מילות מפתח לזיהוי תוכנות תורניות בשמות התוכנות המותקנות (אפשר להרחיב ב-kiosk.autoTorahKeywords)
    private static readonly string[] TorahKeywords =
    {
        "אוצר החכמה", "אוצר הספרים", "בר אילן", "בר-אילן", "כתבי הקודש", "ספריא", "זית",
        "otzar hachochma", "otzar hasfarim", "bar-ilan", "bar ilan", "responsa", "kitvei hakodesh", "zayit",
    };

    private static readonly object AutoGate = new();
    private static List<Entry> _autoCache = new();
    private static DateTime _autoAt = DateTime.MinValue;

    // כשרכיב ההקשחה מותקן, הרשימה והגדרות הזיהוי האוטומטי נקראות מהעותק שלו (config.json כתיב למשתמש המחובר);
    // אחרת — מ-config.json כמו תמיד. ראו HardLock.Policy
    public static bool AutoEnabled => HardLock.Policy?.AutoTorah ?? Config.Get(AutoKey, true);

    private static string[] Keywords => HardLock.Policy?.AutoTorahKeywords ?? Config.GetList(KeywordsKey);

    public static void Register()
    {
        Server.Route("/kiosk/programs", _ => ListResult());
        Server.Route("/kiosk/programs/launch", Launch);
        Server.Route("/kiosk/programs/focus", Launch);   // "העבר לחזית": זהה לפתיחה, ולא פותח עותק נוסף
        Server.Route("/kiosk/programs/add", Add);
        Server.Route("/kiosk/programs/remove", Remove);
        Server.Route("/kiosk/programs/detect", _ => Detect());
        Server.Route("/kiosk/programs/auto", SetAuto);
    }

    // ---------- אחסון ----------

    /// <summary>הרשימה המלאה: ידנית + (אם הזיהוי האוטומטי דולק) תוכנות תורניות שזוהו</summary>
    public static List<Entry> Load()
    {
        var list = LoadManual();
        if (AutoEnabled)
            foreach (var a in DetectAuto())
                if (!list.Any(x => x.Path.Equals(a.Path, StringComparison.OrdinalIgnoreCase))) list.Add(a);
        return list;
    }

    private static List<Entry> LoadManual()
    {
        var list = new List<Entry>();
        foreach (var s in HardLock.Policy?.Programs ?? Config.GetList(Key))
        {
            try
            {
                if (JsonNode.Parse(s) is not JsonObject o) continue;
                string id = (string)o["id"], name = (string)o["name"], path = (string)o["path"];
                if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(path)) continue;
                string[] procs = o["procs"] is JsonArray a
                    ? a.Select(x => x?.ToString() ?? "").Where(x => x.Length > 0).ToArray()
                    : Array.Empty<string>();
                string sha = ((string)o["sha256"] ?? "").Trim().ToLowerInvariant(), signer = ((string)o["signer"] ?? "").Trim();
                if (sha.Length != 0 && !Regex.IsMatch(sha, "^[0-9a-f]{64}$")) sha = "";
                list.Add(new Entry { Id = id, Name = name ?? id, Path = path, Procs = procs, Sha256 = sha, Signer = signer });
            }
            catch { }
        }
        return list;
    }

    private static JsonArray ToJson(List<Entry> list) => new(list.Select(e => (JsonNode)new JsonObject
    {
        ["id"] = e.Id,
        ["name"] = e.Name,
        ["path"] = e.Path,
        ["procs"] = new JsonArray(e.Procs.Select(p => (JsonNode)p).ToArray()),
        ["sha256"] = e.Sha256 ?? "",
        ["signer"] = e.Signer ?? "",
    }).ToArray());

    /// <summary>
    /// שמירת הרשימה הידנית. כשרכיב ההקשחה מותקן היא נשלחת קודם אליו (הוא מקור האמת, והשליחה מאומתת בסיסמה);
    /// אם השליחה נכשלה — לא שומרים גם מקומית, כדי שהשניים לא יתפצלו
    /// </summary>
    private static void Save(List<Entry> list, string password)
    {
        if (HardLock.Available) HardLock.SetPrograms(password, ToJson(list), AutoEnabled, Keywords);
        Config.Set(Key, ToJson(list));
        Config.Save();
    }

    /// <summary>
    /// הגירה לרכיב ההקשחה (בהתקנה/עדכון שלו): אם עוד אין לו רשימה, מעבירים את הרשימה וההגדרות הנוכחיות מ-config.json
    /// </summary>
    internal static void SyncToService(string password)
    {
        var pol = HardLock.Policy;
        if (pol == null || pol.Programs != null) return;
        HardLock.SetPrograms(password, ToJson(LoadManual()), AutoEnabled, Keywords);
    }

    private static string[] ProcNames(Entry e) =>
        e.Procs.Append(System.IO.Path.GetFileNameWithoutExtension(e.Path))
            .Select(x => x.ToLowerInvariant()).Distinct().ToArray();

    // ---------- נקודות קצה ----------

    private static object ListResult() => new
    {
        autoTorah = AutoEnabled,
        programs = Load().Select(e => new
        {
            id = e.Id,
            name = e.Name,
            path = e.Path,
            procs = e.Procs,
            exists = IsShellApp(e.Path) || File.Exists(e.Path),
            running = ExistingWindow(e) != IntPtr.Zero,
            auto = e.Auto,
            pinned = e.Pinned,   // [w3] false = רשומה ישנה בלי נעיצה: תותר רק אם הקובץ במיקום קבוע לא-כתיב; אחרת יש להסיר ולהוסיף מחדש
        }).ToArray()
    };

    private static object SetAuto(JsonElement b)
    {
        Kiosk.RequirePassword(b.Str("password"));
        if (HardLock.Available) HardLock.SetPrograms(b.Str("password"), ToJson(LoadManual()), b.Bool("enabled"), Keywords);
        Config.Set(AutoKey, JsonValue.Create(b.Bool("enabled")));
        Config.Save();
        lock (AutoGate) _autoAt = DateTime.MinValue; // סריקה מחדש
        return ListResult();
    }

    private static object Launch(JsonElement b)
    {
        string id = b.Req("id");
        var e = Load().FirstOrDefault(x => x.Id == id)
                ?? throw BridgeException.NotFound("התוכנה אינה ברשימת התוכנות המותרות");
        return new { opened = true, alreadyRunning = Activate(e) };
    }

    /// <summary>
    /// פותח תוכנה מהרשימה, ואם היא כבר פתוחה (גם מוקטנת) — מחזיר אותה לחזית ולא פותח עותק שני.
    /// מחזיר true אם הייתה פתוחה.
    /// </summary>
    /// <summary>
    /// פותח חלון חסד זמני: השומר (Kiosk.Watch) לא יחזיר את אוצריא לחזית במשך כמה שניות, גם אם
    /// החלון הקדמי הוא תהליך לא-מוכר. לשימוש בפתיחת תוכנה חיצונית מאושרת שלא עוברת דרך Activate
    /// (למשל "הצגה בסייר Windows" ב-FsApi.cs) - בלעדיו השומר היה מחזיר את אוצריא תוך 2 שניות,
    /// עוד לפני שהחלון שנפתח מספיק להיראות בכלל.
    /// </summary>
    internal static void GrantForegroundGrace() => GraceUntil = DateTime.UtcNow.AddSeconds(GraceSeconds);

    // [r1] החסד (30 שניות) חל רק על תהליכים שה-flow שביקש אותו באמת צריך: Hello/אימות (CredentialUIBroker, LogonUI, consent)
    // ו"הצגה בסייר" (explorer) — וגם הם רק אם הנתיב+ACL+חתימת Microsoft אמיתיים. כל תהליך אחר בחזית לא נהנה מהחסד.
    private static readonly HashSet<string> GraceProcs = new(StringComparer.OrdinalIgnoreCase)
        { "CredentialUIBroker", "LogonUI", "consent", "explorer" };

    internal static bool GraceApplies(string procName, string exePath, AppTrust.ITrustEnv env) =>
        GraceProcs.Contains(procName ?? "") && AppTrust.IsTrustedMicrosoft(exePath, env);

    private static bool IsGraceProcess(Process p)
    {
        string exe = null;
        try { exe = p.MainModule?.FileName; } catch { }
        return GraceApplies(p.ProcessName, exe, AppTrust.Env);
    }

    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern int GetPackageFamilyName(IntPtr hProcess, ref uint len, System.Text.StringBuilder name);

    /// <summary>PackageFamilyName של שורת shell:AppsFolder\PFN!App, או null</summary>
    internal static string PfnOfShellPath(string p) =>
        IsShellApp(p) ? p.Substring(@"shell:AppsFolder\".Length).Split('!')[0] : null;

    /// <summary>
    /// [r1] האם תהליך הליבה של UWP (שמארח ApplicationFrameHost) שייך לאפליקציית חבילה מהרשימה, לפי PackageFamilyName
    /// (המזהה כולל hash של המפרסם, לא ניתן לזיוף בשם). תהליך חסום (SystemSettings וכו') לא מתקבל.
    /// </summary>
    internal static bool IsAllowedPackageProcess(uint pid)
    {
        try
        {
            using (var p = Process.GetProcessById((int)pid)) if (IsBlockedProc(p)) return false;
            IntPtr h = OpenProcess(0x1000, false, pid);
            if (h == IntPtr.Zero) return false;
            string pfn;
            try
            {
                uint len = 256;
                var sb = new System.Text.StringBuilder((int)len);
                if (GetPackageFamilyName(h, ref len, sb) != 0) return false;
                pfn = sb.ToString();
            }
            finally { CloseHandle(h); }
            return Load().Any(e => string.Equals(PfnOfShellPath(e.Path), pfn, StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
    }

    // כלי החיתוך של Win11: ה-alias ב-%LOCALAPPDATA%\Microsoft\WindowsApps כתיב למשתמש (אפשר להחליף אותו ב-exe אחר) ולכן אינו מהימן;
    // מפעילים במקומו לפי PackageFamilyName (hash המפרסם של Microsoft קבוע ולא ניתן לזיוף)
    internal const string SnipPfnPath = @"shell:AppsFolder\Microsoft.ScreenSketch_8wekyb3d8bbwe!App";

    internal static bool IsSnipAlias(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        string alias = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "SnippingTool.exe");
        try { return System.IO.Path.GetFullPath(path).Equals(alias, StringComparison.OrdinalIgnoreCase); } catch { return false; }
    }

    /// <summary>
    /// אפליקציית חנות (UWP, כמו פתקים דביקים/שעון) שאין לה קובץ exe שאפשר לאתר - מופעלת לפי מזהה האפליקציה
    /// בתיקיית AppsFolder: shell:AppsFolder\PackageFamilyName!AppId. מזהה תקין בלבד (בלי תווים חריגים)
    /// </summary>
    internal static bool IsShellApp(string p) =>
        p != null && p.StartsWith(@"shell:AppsFolder\", StringComparison.OrdinalIgnoreCase) &&
        Regex.IsMatch(p.Substring(@"shell:AppsFolder\".Length), @"^[A-Za-z0-9.\-]+_[a-z0-9]+![A-Za-z0-9.\-]+$");

    public static bool Activate(Entry e)
    {
        // [r1] alias של כלי החיתוך (כתיב למשתמש) מופעל לפי PackageFamilyName, לא לפי הנתיב
        string target = IsSnipAlias(e.Path) ? SnipPfnPath : e.Path;
        bool shellApp = IsShellApp(target);
        if (!shellApp && !File.Exists(e.Path)) throw BridgeException.NotFound("הקובץ לא נמצא: " + e.Path);
        // [w3] אימות מחדש מול הנעיצה בכל Launch (קובץ שהוחלף/נפרץ לא מופעל). בלי חלון חסד: הזיהוי הוא לפי
        // עץ התהליכים של מה שנפתח (PID+זמן יצירה) ולפי נתיב+נעיצה, לא "כל מה שבחזית ב-30 השניות הקרובות"
        // [r1] TOCTOU: הקובץ נפתח ב-FileShare.Read (נעול מהחלפה/כתיבה), ה-hash מחושב מה-handle, והמנעול מוחזק עד אחרי Process.Start
        AppTrust.LaunchLock fileLock = null;
        try
        {
            if (!shellApp)
            {
                AppTrust.Invalidate(e.Path);
                fileLock = AppTrust.LaunchLock.Open(e.Path);
                if (fileLock == null || !AppTrust.LaunchAllowedLocked(e.Path, fileLock.Sha256, e.Sha256, e.Signer, AppTrust.Env))
                {
                    Config.Log("programs launch חסום: הקובץ אינו תואם לנעיצה/למיקום מהימן (או לא ניתן לנעילה): " + e.Path);
                    throw BridgeException.Forbidden("הקובץ שונה מאז שהתוכנה נוספה (או אינו מאומת). יש להסיר ולהוסיף אותה מחדש בסיסמה");
                }
            }
            return ActivateLocked(e, target, shellApp);
        }
        finally { fileLock?.Dispose(); }
    }

    private static bool ActivateLocked(Entry e, string target, bool shellApp)
    {
        IntPtr h = ExistingWindow(e);
        if (h != IntPtr.Zero)
        {
            Kiosk.BringToFront(h);
            Task.Run(() => { Thread.Sleep(400); Kiosk.BringToFront(h); }); // ניסיון שני: חלק מהתוכנות גוזלות פוקוס בעת הטעינה
            return true;
        }
        LastLaunchAt = DateTime.UtcNow;
        _lastLaunchEntry = e;
        using (var started = Process.Start(new ProcessStartInfo(target)
        {
            UseShellExecute = true,
            WorkingDirectory = shellApp ? null : System.IO.Path.GetDirectoryName(e.Path),
        }))
        {
            if (started != null) RememberLaunched(started);
        }
        Task.Run(() => Focus(e));
        return false;
    }

    /// <summary>חלון ראשי של התוכנה אם היא כבר רצה (כולל חלון מוקטן), אחרת Zero</summary>
    public static IntPtr ExistingWindow(Entry e)
    {
        int session = Process.GetCurrentProcess().SessionId;
        IntPtr found = IntPtr.Zero;
        try
        {
            EnumWindows((h, _) =>
            {
                if (!IsWindowVisible(h) || GetWindow(h, 4 /* GW_OWNER */) != IntPtr.Zero || GetWindowTextLength(h) == 0) return true;
                GetWindowThreadProcessId(h, out uint pid);
                try
                {
                    using var p = Process.GetProcessById((int)pid);
                    if (p.SessionId != session || IsOwnOrBlocked(p)) return true;
                    if (MatchesList(p, new[] { e })) { found = h; return false; }
                }
                catch { }
                return true;
            }, IntPtr.Zero);
        }
        catch { }
        return found;
    }

    /// <summary>
    /// כל החלונות הראשיים הגלויים (עם כותרת) של התוכנה, כולל הכותרת של כל אחד מהם — משמש את סרגל הצד
    /// להציג רשימת חלונות לבחירה (למשל כמה מסמכי Word פתוחים), כתחליף בטוח ל-Alt+Tab שחסום בזמן נעילה.
    /// </summary>
    public static List<(IntPtr Handle, string Title)> ExistingWindows(Entry e)
    {
        int session = Process.GetCurrentProcess().SessionId;
        var found = new List<(IntPtr, string)>();
        try
        {
            EnumWindows((h, _) =>
            {
                if (!IsWindowVisible(h) || GetWindow(h, 4 /* GW_OWNER */) != IntPtr.Zero || GetWindowTextLength(h) == 0) return true;
                GetWindowThreadProcessId(h, out uint pid);
                try
                {
                    using var p = Process.GetProcessById((int)pid);
                    if (p.SessionId != session || IsOwnOrBlocked(p)) return true;
                    if (MatchesList(p, new[] { e })) found.Add((h, WindowTitle(h)));
                }
                catch { }
                return true;
            }, IntPtr.Zero);
        }
        catch { }
        return found;
    }

    /// <summary>כותרת החלון (למשל שם הקובץ הפתוח); מחרוזת ריקה אם אין</summary>
    private static string WindowTitle(IntPtr h)
    {
        int len = GetWindowTextLength(h);
        if (len <= 0) return "";
        var sb = new System.Text.StringBuilder(len + 1);
        GetWindowText(h, sb, sb.Capacity);
        return sb.ToString();
    }

    private static object Add(JsonElement b)
    {
        Kiosk.RequirePassword(b.Str("password"));
        string name = (b.Str("name") ?? "").Trim();
        string path = (b.Str("path") ?? "").Trim().Trim('"');
        if (name.Length == 0 || name.Length > 60) throw BridgeException.BadRequest("יש להזין שם באורך של עד 60 תווים");
        bool shellApp = IsShellApp(path);
        if (!shellApp)
        {
            if (!System.IO.Path.IsPathRooted(path) || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                throw BridgeException.BadRequest("יש להזין נתיב מלא לקובץ הפעלה (exe)");
            if (!File.Exists(path)) throw BridgeException.NotFound("הקובץ לא נמצא: " + path);
            string stem = System.IO.Path.GetFileNameWithoutExtension(path);
            if (Blocked.Contains(stem) || AppTrust.IsBlockedIdentity(null, null, AppTrust.GetOriginalName(path), Blocked))
                throw BridgeException.Forbidden("אי אפשר להוסיף את התוכנה הזו לרשימה");
            // [r1] תוכנה חדשה: מיקום קבוע שרק מנהל כותב אליו (קובץ + כל תיקיות האב), לא נעיצת SHA בלבד — אחרת אפשר להחליף את
            // הקובץ/התיקיה/ספריות שלה (DLL side-loading). alias של כלי החיתוך מומר לאפליקציית חבילה במקום
            if (IsSnipAlias(path)) { path = SnipPfnPath; shellApp = true; }
            else if (!AppTrust.IsAdminOnlyLocation(path))
                throw BridgeException.Forbidden("התוכנה חייבת להיות מותקנת במיקום שרק מנהל יכול לכתוב אליו (למשל Program Files). יש להתקין אותה שם או להקשיח את הרשאות התיקיה");
        }
        else if (string.IsNullOrWhiteSpace(b.Str("procs")))
            throw BridgeException.BadRequest("לאפליקציית חנות חובה לציין שם תהליך (בשדה „תהליכים נוספים”), כדי שהחלון שלה יזוהה כתוכנה מותרת");

        var procs = new List<string>();
        string procsRaw = b.Str("procs") ?? "";
        if (path == SnipPfnPath && string.IsNullOrWhiteSpace(procsRaw)) procsRaw = "SnippingTool, ScreenClippingHost, ScreenSketch";
        foreach (string p in procsRaw.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string n = System.IO.Path.GetFileNameWithoutExtension(p.Trim());
            if (!Regex.IsMatch(n, @"^[\w .\-]{1,60}$")) throw BridgeException.BadRequest("שם תהליך לא תקין: " + p);
            if (Blocked.Contains(n)) throw BridgeException.Forbidden("אי אפשר להוסיף את התהליך " + n);
            procs.Add(n);
        }

        var list = LoadManual();
        if (list.Count >= MaxPrograms) throw BridgeException.BadRequest("אפשר להגדיר עד " + MaxPrograms + " תוכנות");
        if (Load().Any(x => x.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))
            throw BridgeException.Exists("התוכנה כבר ברשימה");

        // [w3] נעיצת הזהות בעת ההוספה (בסיסמה): מפרסם חתום אם יש + SHA-256
        var pin = shellApp ? ("", "") : AppTrust.CreatePin(path);
        list.Add(new Entry
        {
            Id = Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant(),
            Name = name,
            Path = path,
            Procs = procs.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            Sha256 = pin.Item1,
            Signer = pin.Item2,
        });
        Save(list, b.Str("password"));
        return ListResult();
    }

    private static object Remove(JsonElement b)
    {
        Kiosk.RequirePassword(b.Str("password"));
        string id = b.Req("id");
        if (id.StartsWith("auto-", StringComparison.Ordinal))
            throw BridgeException.BadRequest("תוכנה שזוהתה אוטומטית אי אפשר להסיר. כבו את הזיהוי האוטומטי");
        var list = LoadManual();
        if (list.RemoveAll(x => x.Id == id) == 0) throw BridgeException.NotFound("התוכנה אינה ברשימה");
        Save(list, b.Str("password"));
        return ListResult();
    }

    /// <summary>מאתר תוכנות נפוצות להצעה: Word ותוכנות התורה המוכרות (בנתיבים הידועים, ואם לא — בחיפוש בכוננים)</summary>
    private static object Detect()
    {
        var found = new List<object>();
        string word = AppPath("WINWORD.EXE");
        if (word != null) found.Add(new { name = "Word", path = word, procs = "WINWORD" });
        // [w3] Detect הוא הצעה בלבד לבעל הסיסמה (Add מבצע נעיצה): כאן נשאר החיפוש הרחב; אל הרשימה האוטומטית הוא לא נכנס
        foreach (var (name, path, procs) in KnownTorah(trustedOnly: false))
            found.Add(new { name, path, procs = string.Join(", ", procs) });
        foreach (var (name, path, procs) in KnownUtilities())
            found.Add(new { name, path, procs = string.Join(", ", procs) });
        return new { found = found.ToArray() };
    }

    /// <summary>
    /// כלים מובנים של Windows שימושיים בזמן נעילה (למשל לצילום מסך כדי לדווח על תקלה) - להצעה בלבד
    /// כאן ב-Detect(), לא ב-KnownList: אסור שאלה ייכנסו ל-DetectAuto (הזיהוי האוטומטי הוא ל"תוכנה
    /// תורנית" בלבד ודולק כברירת מחדל - תוסף מערכת לא אמור להתווסף לרשימה המותרת בלי אישור מפורש).
    /// נתיבים ידועים בלבד (בלי חיפוש בכוננים) - אלה כלי מערכת במקום קבוע, לא תוכנות שמתקינים לבד.
    /// </summary>
    private static IEnumerable<(string name, string path, string[] procs)> KnownUtilities()
    {
        string sys32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
        string winApps = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps");
        var list = new (string name, string[] procs, string[] candidates)[]
        {
            // "חדש" (צילום אזור) פותח שכבת-גרירה במסך מלא דרך תהליך-עזר נפרד (ScreenClippingHost/ScreenSketch),
            // לא תחת SnippingTool.exe עצמו - בלעדי זה השומר (Kiosk.Watch) לא מזהה את שכבת הגרירה כ"מותר"
            // ומחזיר את אוצריא לחזית תוך רגע, עוד לפני שהמשתמש הספיק לגרור/לבחור אזור (נראה כמו "תקוע בטעינה")
            ("כלי חיתוך (צילום מסך)", new[] { "SnippingTool", "ScreenClippingHost", "ScreenSketch" }, new[] { System.IO.Path.Combine(sys32, "SnippingTool.exe"), System.IO.Path.Combine(winApps, "SnippingTool.exe") }),
        };
        foreach (var u in list)
        {
            string hit = u.candidates.FirstOrDefault(File.Exists);
            // [r1] alias ב-WindowsApps (כתיב למשתמש) מוצע כאפליקציית חבילה לפי PackageFamilyName, לא כנתיב
            if (hit != null && IsSnipAlias(hit)) hit = SnipPfnPath;
            if (hit != null) yield return (u.name, hit, u.procs);
        }
    }

    // ---------- תוכנות תורה מוכרות ----------

    private record Known(string Name, string[] Exes, string[] Procs, Func<IEnumerable<string>> Candidates);

    private static IEnumerable<string> Drives()
    {
        foreach (var d in DriveInfo.GetDrives())
        {
            bool ok = false;
            try { ok = d.IsReady && (d.DriveType == DriveType.Fixed || d.DriveType == DriveType.Removable); } catch { }
            if (ok) yield return d.RootDirectory.FullName;
        }
    }

    /// <summary>#4: כל הכוננים המוכנים (גם רשת/וירטואלי/תקליטור) — לבדיקת נתיבים ידועים בלבד (File.Exists, זול)</summary>
    private static IEnumerable<string> AllDrives()
    {
        foreach (var d in DriveInfo.GetDrives())
        {
            bool ok = false;
            try { ok = d.IsReady; } catch { }
            if (ok) yield return d.RootDirectory.FullName;
        }
    }

    /// <summary>#7: קובץ נייד לפי תבנית שם בתיקיות המשתמש הנפוצות ובשורש הכוננים (עומק 2)</summary>
    private static IEnumerable<string> Portable(string pattern)
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var roots = new[] { "Desktop", "Downloads", "Documents", "" }.Select(x => System.IO.Path.Combine(home, x)).Concat(AllDrives());
        foreach (var r in roots)
        {
            string[] hits;
            try { hits = Directory.Exists(r) ? Directory.GetFiles(r, pattern) : Array.Empty<string>(); } catch { hits = Array.Empty<string>(); }
            foreach (var h in hits) yield return h;
            string[] subs;
            try { subs = Directory.Exists(r) ? Directory.GetDirectories(r) : Array.Empty<string>(); } catch { subs = Array.Empty<string>(); }
            foreach (var sd in subs)
            {
                try { hits = Directory.GetFiles(sd, pattern); } catch { continue; }
                foreach (var h in hits) yield return h;
            }
        }
    }

    private static IEnumerable<string> Under(string dir, string pattern, string tail)
    {
        string[] dirs;
        try { dirs = Directory.Exists(dir) ? Directory.GetDirectories(dir, pattern) : Array.Empty<string>(); } catch { dirs = Array.Empty<string>(); }
        return dirs.Select(x => System.IO.Path.Combine(x, tail));
    }

    private static readonly Known[] KnownList =
    {
        new("בר אילן — שו\"ת", new[] { "RESPONSA.exe" }, new[] { "RESPONSA" }, () =>
            Under(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "ResponsaCD*", "RESPONSA.exe")
            .Concat(Under(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ResponsaCD*", "RESPONSA.exe"))
            .Concat(Drives().SelectMany(r => Under(r, "ResponsaCD*", "RESPONSA.exe")))),
        new("כתבי הקודש", new[] { "כתבי הקודש.exe", "KleiKodesh.exe" }, new[] { "כתבי הקודש", "KleiKodesh" }, () =>
            new[]
            {
                System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KleiKodesh", "כתבי הקודש.exe"),
                System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KleiKodesh", "KleiKodesh.exe"),
                System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "KleiKodesh", "כתבי הקודש.exe"),
            }),
        new("אוצר החכמה", new[] { "otzar.exe", "אוצר החכמה.exe" }, new[] { "otzar", "אוצר החכמה", "OtzarLauncher" }, () =>
            AllDrives().SelectMany(r => new[]
            {
                System.IO.Path.Combine(r, "OtzarApp", "OtzarLocal", "launcher", "bin", "x64", "app", "otzar.exe"),
                System.IO.Path.Combine(r, "אוצר החכמה.exe"),
                System.IO.Path.Combine(r, "OtzarApp", "OtzarLocal", "launcher", "otzar.exe"),
            }).Concat(Portable("אוצר החכמה*.exe"))),
        // התקנה למשתמש בלבד (כמו VS Code/Electron) תחת %LOCALAPPDATA%\Programs — לא ב-Program Files
        new("זית", new[] { "zayit.exe" }, new[] { "zayit" }, () =>
            new[] { System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "zayit", "zayit.exe") }),
        // #6: גשר חבורתא לאוצריא
        new("חבורתא", new[] { "ChavrutaLauncher.exe" }, new[] { "ChavrutaLauncher", "Chavruta" }, () =>
            new[] { System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Chavruta", "ChavrutaLauncher.exe") }),
        // #7: הנקדן אופליין — תוכנה ניידת, איתור לפי שם הקובץ
        new("הנקדן — אוצריא", new[] { "הנקדן - אוצריא - V1.0.exe" }, new[] { "הנקדן - אוצריא - V1.0" }, () => Portable("הנקדן*.exe")),
    };

    private static List<(string name, string path, string[] procs)> _knownCache, _knownCacheTrusted;
    private static DateTime _knownAt = DateTime.MinValue, _knownAtTrusted = DateTime.MinValue;

    /// <summary>
    /// לכל תוכנה מוכרת: קודם הנתיבים הידועים, ואם לא נמצאה — חיפוש לפי שם הקובץ בכוננים (עד עומק 4).
    /// התוצאה נשמרת ל-10 דקות כדי לא לסרוק שוב בכל פתיחה.
    /// [w3] trustedOnly (הזיהוי האוטומטי = מקור אמון): רק מועמדים במיקום קבוע לא-כתיב (Program Files/אוצריא + ACL),
    /// בלי Search, בלי Portable (שולחן עבודה/הורדות/שורשי כוננים/USB) ובלי AppData. false = להצעה בלבד ב-Detect.
    /// </summary>
    private static List<(string name, string path, string[] procs)> KnownTorah(bool trustedOnly = true)
    {
        lock (AutoGate)
        {
            var cache = trustedOnly ? _knownCacheTrusted : _knownCache;
            var at = trustedOnly ? _knownAtTrusted : _knownAt;
            if (cache != null && (DateTime.UtcNow - at).TotalMinutes < 10) return cache;
            var res = new List<(string, string, string[])>();
            foreach (var k in KnownList)
            {
                string hit = null;
                try
                {
                    hit = trustedOnly
                        ? k.Candidates().FirstOrDefault(c => File.Exists(c) && AppTrust.IsTrustedLocation(c))
                        : k.Candidates().FirstOrDefault(File.Exists);
                }
                catch { }
                if (!trustedOnly) hit ??= Search(k.Exes);
                if (hit != null) res.Add((k.Name, hit, k.Procs));
            }
            if (trustedOnly) { _knownCacheTrusted = res; _knownAtTrusted = DateTime.UtcNow; }
            else { _knownCache = res; _knownAt = DateTime.UtcNow; }
            return res;
        }
    }

    private static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
    { "Windows", "$Recycle.Bin", "System Volume Information", "ProgramData", "Recovery", "node_modules", ".git", "AppData" };

    private static string Search(string[] exes)
    {
        var deadline = DateTime.UtcNow.AddSeconds(8);
        var roots = Drives().Concat(new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        });
        foreach (string root in roots)
        {
            var q = new Queue<(string dir, int depth)>();
            q.Enqueue((root, 0));
            while (q.Count > 0 && DateTime.UtcNow < deadline)
            {
                var (dir, depth) = q.Dequeue();
                foreach (string exe in exes)
                {
                    string p = System.IO.Path.Combine(dir, exe);
                    if (File.Exists(p)) return p;
                }
                if (depth >= 4) continue;
                try
                {
                    foreach (string sub in Directory.EnumerateDirectories(dir))
                        if (!SkipDirs.Contains(System.IO.Path.GetFileName(sub))) q.Enqueue((sub, depth + 1));
                }
                catch { }
            }
        }
        return null;
    }

    private static string AppPath(string exe, bool trustedOnly = false)
    {
        foreach (var hive in TrustedHives(trustedOnly))
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using var bk = RegistryKey.OpenBaseKey(hive, view);
                    using var k = bk.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + exe);
                    string v = (k?.GetValue(null) as string)?.Trim().Trim('"');
                    if (!string.IsNullOrEmpty(v) && File.Exists(v) && (!trustedOnly || AppTrust.IsTrustedLocation(v))) return v;
                }
                catch { }
            }
        return null;
    }

    // ---------- זיהוי אוטומטי של תוכנות תורניות ----------

    /// <summary>
    /// [w3] כוורי רישום שמותר לסמוך עליהם כמקור לזיהוי אוטומטי: HKLM בלבד. HKCU (Uninstall, App Paths) כתיב
    /// למשתמש הרגיל — הוא היה יכול להזריק "זית"/"אוצר" שמצביע ל-exe שלו ולקבל הרשאת חזית
    /// </summary>
    internal static RegistryHive[] TrustedHives(bool trustedOnly) =>
        trustedOnly ? new[] { RegistryHive.LocalMachine } : new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser };

    /// <summary>[w3] האם נתיב שנמצא בזיהוי האוטומטי ראוי להתקבל (מיקום קבוע לא-כתיב, לא חסום)</summary>
    internal static bool AutoCandidateOk(string path, AppTrust.ITrustEnv env) =>
        !string.IsNullOrEmpty(path) && !Blocked.Contains(System.IO.Path.GetFileNameWithoutExtension(path)) && env.IsTrustedLocation(path);

    private static List<Entry> DetectAuto()
    {
        lock (AutoGate)
        {
            if ((DateTime.UtcNow - _autoAt).TotalSeconds < 60) return _autoCache;
            var found = new List<Entry>();
            try
            {
                string word = AppPath("WINWORD.EXE", trustedOnly: true);
                if (word != null) AddAuto(found, "Word", word, new[] { "WINWORD" });
                foreach (var (kn, kp, kprocs) in KnownTorah(trustedOnly: true)) AddAuto(found, kn, kp, kprocs);

                var keywords = TorahKeywords.Concat(Keywords).Select(k => k.ToLowerInvariant()).ToArray();
                foreach (var hive in TrustedHives(true))
                    foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                    {
                        try
                        {
                            using var bk = RegistryKey.OpenBaseKey(hive, view);
                            using var root = bk.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                            if (root == null) continue;
                            foreach (string sub in root.GetSubKeyNames())
                            {
                                using var k = root.OpenSubKey(sub);
                                string display = (k?.GetValue("DisplayName") as string)?.Trim();
                                if (string.IsNullOrEmpty(display)) continue;
                                string low = display.ToLowerInvariant();
                                if (!keywords.Any(low.Contains)) continue;
                                string exe = ResolveExe(k);
                                if (exe != null) AddAuto(found, display, exe, Array.Empty<string>());
                            }
                        }
                        catch { }
                    }
            }
            catch (Exception ex) { Config.Log("programs auto-detect: " + ex.Message); }
            _autoCache = found;
            _autoAt = DateTime.UtcNow;
            return _autoCache;
        }
    }

    private static void AddAuto(List<Entry> list, string name, string path, string[] procs)
    {
        if (!AutoCandidateOk(path, AppTrust.Env)) return;   // [w3] גם נתיב שמגיע מהרישום חייב להיות במיקום קבוע לא-כתיב
        if (list.Any(x => x.Path.Equals(path, StringComparison.OrdinalIgnoreCase))) return;
        string hash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(path.ToLowerInvariant()))).Substring(0, 8).ToLowerInvariant();
        list.Add(new Entry { Id = "auto-" + hash, Name = name, Path = path, Procs = procs, Auto = true });
    }

    /// <summary>קובץ ההפעלה של תוכנה מותקנת: לפי DisplayIcon, ואם אין — קובץ exe הגדול בתיקיית ההתקנה</summary>
    private static string ResolveExe(RegistryKey k)
    {
        string icon = k.GetValue("DisplayIcon") as string;
        if (!string.IsNullOrWhiteSpace(icon))
        {
            var m = Regex.Match(icon, "^\\s*\"?([^\",]+\\.exe)", RegexOptions.IgnoreCase);
            if (m.Success && File.Exists(m.Groups[1].Value) && !IsHelperExe(m.Groups[1].Value)) return m.Groups[1].Value;
        }
        string loc = (k.GetValue("InstallLocation") as string)?.Trim().Trim('"');
        if (!string.IsNullOrEmpty(loc) && Directory.Exists(loc))
        {
            try
            {
                return Directory.EnumerateFiles(loc, "*.exe", SearchOption.TopDirectoryOnly)
                    .Where(f => !IsHelperExe(f)).OrderByDescending(f => new FileInfo(f).Length).FirstOrDefault();
            }
            catch { }
        }
        return null;
    }

    private static bool IsHelperExe(string path)
    {
        string n = System.IO.Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
        return n.StartsWith("unins") || n.StartsWith("uninstall") || n.StartsWith("setup") || n.StartsWith("update") || n.StartsWith("vc_redist");
    }

    // ---------- שילוב עם השומר של הנעילה ----------

    // תהליכים שנפתחו דרך הגשר: מזהה + זמן יצירה (מונע PID reuse) + תוקף 10 דקות שנאכף בכל בדיקה (AppTrust.LaunchedSet).
    // נשמרים 10 דקות, כדי לזהות גם תהליכי בן של מפעיל שכבר נסגר (למשל תוכנה שמפעילה תהליך אחר שמציג את החלון).
    private static readonly AppTrust.LaunchedSet Launched = new();
    private static readonly Dictionary<int, (int parent, DateTime at)> ParentCache = new();
    private static readonly object PidGate = new();
    private static Entry _lastLaunchEntry;   // לבדיקת מפרסם של תהליכים חדשים שאחרי הפתיחה (IsNewAfterLaunch)

    private static void RememberLaunched(Process started)
    {
        DateTime start;
        try { start = started.StartTime.ToUniversalTime(); }
        catch { return; }   // אין זמן יצירה = אי אפשר לאמת זהות אחר כך (fail-closed: לא רושמים)
        Launched.Remember(started.Id, start, DateTime.UtcNow);
    }

    private static AppTrust.ProcInfo Probe(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            try { return new AppTrust.ProcInfo(true, p.StartTime.ToUniversalTime()); }
            catch { return new AppTrust.ProcInfo(true, null); }   // חי אבל אין גישה לזמן (מוגבה)
        }
        catch { return new AppTrust.ProcInfo(false, null); }
    }

    private static int ParentOf(int pid)
    {
        lock (PidGate)
            if (ParentCache.TryGetValue(pid, out var c) && DateTime.UtcNow - c.at < TimeSpan.FromSeconds(10)) return c.parent;
        int parent = (int)Kiosk.ParentPidOf((uint)pid);   // [s2] ntdll: זול ובלי תלות ב-WMI; WMI רק כגיבוי
        if (parent == 0)
        try
        {
            using var q = new ManagementObjectSearcher("SELECT ParentProcessId FROM Win32_Process WHERE ProcessId=" + pid);
            foreach (ManagementObject mo in q.Get())
                using (mo) parent = Convert.ToInt32(mo["ParentProcessId"]);
        }
        catch { /* אין גישה ל-WMI */ }
        lock (PidGate) ParentCache[pid] = (parent, DateTime.UtcNow);
        return parent;
    }

    /// <summary>האם התהליך נפתח דרך הגשר, או שהוא תהליך בן (עד 6 דורות) של תהליך שנפתח כך. תוקף 10 דקות, והשוואת זמן יצירה (PID reuse)</summary>
    private static bool IsLaunchedTree(int pid) => Launched.IsTree(pid, Probe, ParentOf, DateTime.UtcNow, ChildTrusted);

    /// <summary>[u2] האם לתהליך יש אב קדמון שנפתח דרך הגשר (כלומר הוא צאצא של תוכנה מותרת, לא עוזר של הגשר עצמו)</summary>
    internal static bool HasLaunchedAncestor(int pid) => Launched.HasLaunchedAncestor(pid, Probe, ParentOf, DateTime.UtcNow);

    /// <summary>[u2] רצה כרגע תוכנה שהופעלה דרך הגשר</summary>
    internal static bool AnyLaunchedAlive() => Launched.AnyAlive(Probe, DateTime.UtcNow);

    internal static bool ChildTrustedPid(int pid) => ChildTrusted(pid);

    /// <summary>
    /// [s2] צאצא של תוכנה מותרת מקבל אמון רק אם הקובץ שלו עובר TreeChildTrusted (מיקום מנהל-בלבד / חבילת Microsoft / חתימה נעוצה).
    /// WHY: בלי זה VBA ב-Word שהריץ קובץ מהמשתמש (או hh/msdt) נחשב "תוכנה מותרת" רק כי אבא שלו מותר.
    /// </summary>
    private static bool ChildTrusted(int pid)
    {
        try
        {
            string exe = Kiosk.ImagePathOf((uint)pid);
            var signers = Load().Select(e => e.Signer).Where(x => !string.IsNullOrEmpty(x)).Distinct().ToList();
            return AppTrust.TreeChildTrusted(exe, signers, Environment.GetFolderPath(Environment.SpecialFolder.Windows), AppTrust.Env);
        }
        catch { return false; }
    }

    /// <summary>תיקיות רחבות מדי כדי להתיר את כל מה שבהן (Windows, Program Files, תיקיית משתמש...)</summary>
    private static bool BroadDir(string dir)
    {
        if (string.IsNullOrEmpty(dir)) return true;
        string d = dir.TrimEnd('\\');
        string root = (System.IO.Path.GetPathRoot(dir) ?? "").TrimEnd('\\');
        if (d.Length <= root.Length) return true;
        string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\');
        if (d.Equals(win, StringComparison.OrdinalIgnoreCase) || d.StartsWith(win + "\\", StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var f in new[]
        {
            Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86,
            Environment.SpecialFolder.CommonApplicationData, Environment.SpecialFolder.UserProfile,
            Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.MyDocuments,
            Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.ApplicationData,
        })
        {
            string x = Environment.GetFolderPath(f).TrimEnd('\\');
            if (x.Length > 0 && d.Equals(x, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>
    /// האם השומר צריך לוותר על החזרת אוצריא לחזית. כן אם: פתיחה טרייה (30 שניות), או שהתהליך בחזית הוא
    /// תוכנה מהרשימה — לפי שם תהליך, לפי קובץ ההפעלה המלא, לפי תיקיית ההתקנה (תהליכי עזר ומפעילים נפרדים),
    /// או לפי עץ התהליכים של מה שנפתח דרך הגשר.
    /// </summary>
    public static bool ShouldYieldTo(uint foregroundPid)
    {
        try
        {
            using var p = Process.GetProcessById((int)foregroundPid);
            // [r1] עוין (דפדפן/LOLBin/כלי ניהול, גם לפי OriginalFilename) נבדק לפני כל "הרשאה": לא חסד, לא עץ-גשר, לא "חדש אחרי הפעלה"
            if (p.Id != Environment.ProcessId && IsBlockedProc(p)) return false;
            if (DateTime.UtcNow < GraceUntil && IsGraceProcess(p)) return true;
            if (MatchesList(p, Load())) return true;
            if (IsLaunchedTree((int)foregroundPid)) return true;
            // תוכנה שהחלון שלה שייך לתהליך אחר מהקובץ שהופעל (מפעיל נפרד, תהליך עזר, מופע יחיד):
            // כל תהליך חדש שנפתח בדקות שאחרי הפתיחה דרך הגשר, שאינו מעטפת/מנהל מערכת/אוצריא/הגשר
            return !IsOwnOrBlocked(p) && IsNewAfterLaunch(p);
        }
        catch { return false; }
    }

    /// <summary>
    /// האם התהליך הוא תוכנה מהרשימה. [w3] לא לפי שם תהליך בלבד: נדרש נתיב קובץ ההפעלה, והוא חייב להתאים לרשומה
    /// (נתיב מלא + נעיצת מפרסם/SHA-256, או מיקום קבוע לא-כתיב); תהליכי עזר — רק במיקום כזה או חתומים באותו מפרסם.
    /// תהליך שאין גישה לנתיב שלו (מוגבה) אינו תואם (fail-closed).
    /// </summary>
    private static bool MatchesList(Process p, IEnumerable<Entry> list)
    {
        string exe = null;
        try { exe = p.MainModule?.FileName; } catch { /* תהליך מוגבה או ללא גישה */ }
        if (string.IsNullOrEmpty(exe)) return false;
        foreach (var e in list)
        {
            string dir = System.IO.Path.GetDirectoryName(e.Path);
            if (AppTrust.MatchExe(exe, e.Path, BroadDir(dir) ? null : dir, ProcNames(e), e.Sha256, e.Signer, AppTrust.Env) != AppTrust.MatchKind.None)
                return true;
        }
        return false;
    }

    /// <summary>אוצריא, הגשר, מעטפות ומנהלי מערכת: לעולם לא "תוכנה שנפתחה"</summary>
    private static bool IsOwnOrBlocked(Process p)
    {
        if (p.Id == Environment.ProcessId) return true;
        string n = p.ProcessName;
        if (IsBlockedProc(p)) return true;
        string otz = System.IO.Path.GetFileNameWithoutExtension(Kiosk.OtzariaPath() ?? "otzaria");
        return n.Equals(otz, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>שם קובץ/תהליך (בלי סיומת) שאסור להוסיף לרשימה ולא להגדיר כאוצריא — לשימוש גם באימות שבשירות</summary>
    internal static bool IsBlockedName(string stem) => !string.IsNullOrEmpty(stem) && Blocked.Contains(stem);

    /// <summary>שם תהליך תקין לרשימה (אותו כלל כמו בהוספה דרך הגשר)</summary>
    internal static bool IsValidProcName(string n) =>
        !string.IsNullOrEmpty(n) && Regex.IsMatch(n, @"^[\w .\-]{1,60}$") && !Blocked.Contains(n);

    // מעטפות פקודה, מסופים ומארחי סקריפטים: חלון כזה בחזית בזמן נעילה נסגר בידי השומר.
    // הגשר מפעיל בעצמו cmd/powershell לפקודות עזר, אבל תמיד בלי חלון (CreateNoWindow), ולכן הם אף פעם לא בחזית
    // [w3] כל Blocked נסגר כשהוא בחזית, חוץ מ: explorer (נהרג ב-Watch בנפרד ויש חלון צילום), msiexec (מתקינים),
    // ApplicationFrameHost (מארח כל אפליקציות ה-UWP המותרות, הריגתו סוגרת גם אותן — נחסם רק כ"לא מותר" בחזית)
    private static readonly HashSet<string> ShellHosts = new(
        Blocked.Where(x => !x.Equals("explorer", StringComparison.OrdinalIgnoreCase) &&
                           !x.Equals("msiexec", StringComparison.OrdinalIgnoreCase) &&
                           !x.Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase)),
        StringComparer.OrdinalIgnoreCase);

    /// <summary>[w3] זהות חסומה לפי שם תהליך + שם הקובץ + OriginalFilename (שינוי שם cmd/powershell לא עוקף)</summary>
    internal static bool IsBlockedProc(Process p) => IdentityIn(p, Blocked);

    private static bool IdentityIn(Process p, ISet<string> set)
    {
        string n = "", exe = null;
        try { n = p.ProcessName; } catch { return false; }
        try { exe = p.MainModule?.FileName; } catch { /* מוגבה: נשארים עם שם התהליך */ }
        return AppTrust.IsBlockedIdentity(n, exe, AppTrust.GetOriginalName(exe), set);
    }

    /// <summary>האם התהליך הוא מעטפת פקודה/מסוף/מארח סקריפטים (לפי זהות, לא לפי שם בלבד)</summary>
    public static bool IsShellHost(Process p)
    {
        try { return IdentityIn(p, ShellHosts); }
        catch { return false; }
    }

    /// <summary>
    /// האם התהליך בחזית הוא תוכנה מותרת. כמו ShouldYieldTo, אבל מעטפות, מארחי סקריפטים ומנהלי מערכת (Blocked)
    /// לעולם אינם "מותרים" — גם בחלון החסד שאחרי פתיחת תוכנה, וגם כשהם תהליך בן של תוכנה מהרשימה
    /// (למשל חלון שנפתח מתוך דיאלוג פתיחת קובץ של תוכנה מותרת).
    /// </summary>
    public static bool IsAllowedForeground(uint foregroundPid)
    {
        try
        {
            using var p = Process.GetProcessById((int)foregroundPid);
            if (p.Id != Environment.ProcessId && IsBlockedProc(p)) return false;
        }
        catch { /* תהליך שנסגר: ההחלטה נשארת ל-ShouldYieldTo */ }
        return ShouldYieldTo(foregroundPid);
    }

    private static bool StartedAfterLaunch(Process p)
    {
        var last = LastLaunchAt;
        if (last == DateTime.MinValue) return false;
        try { return p.StartTime.ToUniversalTime() >= last.AddSeconds(-3); }
        catch { return false; } // תהליך מוגבה: אין גישה לזמן ההתחלה
    }

    /// <summary>
    /// תהליך שהתחיל בין 3 שניות לפני הפתיחה דרך הגשר לבין 90 שניות אחריה. הורחב פעם ל-10 דקות והוחזר:
    /// זה לא פתר את בעיית כלי החיתוך, והרחבה כזו מתירה כל תהליך שנפתח אחרי תוכנה מותרת (דפדפן מקישור וכו').
    /// [w3] בנוסף: קובץ ההפעלה שלו חייב להיות במיקום קבוע לא-כתיב או חתום באותו מפרסם של התוכנה שנפתחה.
    /// </summary>
    private static bool IsNewAfterLaunch(Process p)
    {
        if (!StartedAfterLaunch(p)) return false;
        try { if (p.StartTime.ToUniversalTime() > LastLaunchAt.AddSeconds(90)) return false; }
        catch { return false; }
        string exe = null;
        try { exe = p.MainModule?.FileName; } catch { }
        if (string.IsNullOrEmpty(exe)) return false;
        // [r1] "כל מיקום קבוע" לא מספיק (אחרת כל exe של Windows/Program Files שנפתח תוך 90 שניות מתקבל): נדרשת חתימה/נעיצה של הרשומה
        var le = _lastLaunchEntry;
        if (le == null) return false;
        if (le.Path != null && exe.Equals(le.Path, StringComparison.OrdinalIgnoreCase)) return AppTrust.LaunchAllowed(exe, le.Sha256, le.Signer, AppTrust.Env);
        string signer = !string.IsNullOrEmpty(le.Signer) ? le.Signer : AutoSigner(le);
        if (string.IsNullOrEmpty(signer))
            return AppTrust.IsMicrosoftPackage(exe, AppTrust.Env) && IsShellApp(le.Path) && PackageMatchesEntry(exe, le);
        var s = AppTrust.Env.Sig(exe);
        return s.Valid && string.Equals(s.Subject, signer, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>רשומה אוטומטית (בלי נעיצה) במיקום מהימן: המפרסם הוא החתימה התקפה של הקובץ שלה</summary>
    private static string AutoSigner(Entry le)
    {
        if (le.Path == null || IsShellApp(le.Path) || !AppTrust.Env.IsTrustedLocation(le.Path)) return "";
        var s = AppTrust.Env.Sig(le.Path);
        return s.Valid ? s.Subject : "";
    }

    /// <summary>תהליך עזר בחבילת Microsoft של אפליקציית החנות שנפתחה (למשל ScreenClippingHost): שם התהליך מרשימת Procs של הרשומה</summary>
    private static bool PackageMatchesEntry(string exe, Entry le) =>
        ProcNames(le).Contains(System.IO.Path.GetFileNameWithoutExtension(exe).ToLowerInvariant());

    private delegate bool EnumWindowsProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr l);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr h);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr h, System.Text.StringBuilder s, int max);

    /// <summary>
    /// מחפש חלון ראשי גלוי (עם כותרת, בלי בעלים) של תהליך שהוא התוכנה שנפתחה: מהרשימה, או חדש אחרי הפתיחה
    /// (כולל תהליכי בן). כך מוצאים את החלון גם כשהוא לא שייך לתהליך עם שם הקובץ שהופעל.
    /// </summary>
    private static IntPtr FindLaunchedWindow(int session, Entry e)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h) || GetWindow(h, 4 /* GW_OWNER */) != IntPtr.Zero || GetWindowTextLength(h) == 0) return true;
            GetWindowThreadProcessId(h, out uint pid);
            try
            {
                using var p = Process.GetProcessById((int)pid);
                if (p.SessionId != session || IsOwnOrBlocked(p)) return true;
                if (MatchesList(p, new[] { e }) || (StartedAfterLaunch(p) && (IsNewAfterLaunch(p) || IsLaunchedTree((int)pid))))
                {
                    found = h;
                    return false;
                }
            }
            catch { /* תהליך נסגר או ללא גישה */ }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>מביא את חלון התוכנה שנפתחה לחזית (עד כ-20 שניות; תוכנות כבדות נפתחות לאט)</summary>
    private static void Focus(Entry e)
    {
        var names = ProcNames(e);
        int session = Process.GetCurrentProcess().SessionId;
        for (int i = 0; i < 40; i++)
        {
            Thread.Sleep(500);
            try
            {
                foreach (string name in names)
                    foreach (var p in Process.GetProcessesByName(name))
                    {
                        using (p)
                        {
                            if (p.SessionId != session) continue;
                            IntPtr h = p.MainWindowHandle;
                            if (h == IntPtr.Zero) continue;
                            Kiosk.BringToFront(h);
                            return;
                        }
                    }
                // החלון שייך לתהליך אחר מהקובץ שהופעל (מפעיל נפרד): מחפשים לפי החלונות הגלויים
                IntPtr w = FindLaunchedWindow(session, e);
                if (w != IntPtr.Zero)
                {
                    Kiosk.BringToFront(w);
                    Thread.Sleep(600);
                    Kiosk.BringToFront(w);
                    Config.Log("programs focus: החלון נמצא לפי תהליך חדש ולא לפי שם הקובץ (" + e.Name + ")");
                    return;
                }
            }
            catch (Exception ex) { Config.Log("programs focus: " + ex.Message); }
        }
    }
}
