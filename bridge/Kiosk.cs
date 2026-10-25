using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace OtzariaBridge;

/// <summary>
/// מצב נעילה (קיוסק).
/// נעילה רכה: סגירת explorer, חסימת מקשים, מעקב שמחזיר את אוצריא לחזית, חסימת מנהל המשימות.
/// נעילה קפדנית: כבויה כברירת מחדל; מופעלת רק כאשר הבקשה כוללת strict:true במפורש.
/// </summary>
public static partial class Kiosk
{
    private const string PoliciesKey = @"Software\Microsoft\Windows\CurrentVersion\Policies\System";
    private const string WinlogonKey = @"Software\Microsoft\Windows NT\CurrentVersion\Winlogon";

    private static readonly object Gate = new();
    private static int _failures;
    private static DateTime _lockoutUntil = DateTime.MinValue;
    private static System.Threading.Timer _watchdog;
    private static IntPtr _hook = IntPtr.Zero;
    private static LowLevelKeyboardProc _hookProc;

    public static bool IsActive => Config.Get("kiosk.active", false);
    // כשהשירות מחזיק את הסיסמה אין עותק מקומי (ר' PurgeLocalPassword), ולכן "יש סיסמה" כולל גם את השירות
    private static bool HasPassword => !string.IsNullOrEmpty(Config.Get<string>("kiosk.passwordHash", null)) || HardLock.Available;
    internal const int MinPasswordLength = 10;

    /// <summary>השירות מותקן אבל ה-pipe לא עונה. כשל סגור: אסור ליפול ל-hash מקומי (config.json כתיב למשתמש)</summary>
    internal static bool ServiceDown()
    {
        try { dynamic st = HardLock.Status(); return st.installed == true && st.running != true; }
        catch { return false; }
    }

    /// <summary>
    /// מוחק את עותק ה-hash/salt של סיסמת הנעילה מ-config.json כשהשירות (SYSTEM) מחזיק אותה: עותק קריא-כתיב למשתמש
    /// מאפשר ניחוש לא מקוון או החלפה. נקרא רק אחרי שהשירות אישר (Available) - אז ההגירה בהתקנה כבר קרתה.
    /// </summary>
    private static void PurgeLocalPassword()
    {
        try
        {
            // WHY (U1): גם hash/salt של קוד השחזור. עותק קריא-כתיב שלהם איפשר ניחוש לא מקוון, ובגרסה הקודמת גם שחרור חירום ב-pass-the-hash
            // (ההוכחה בקובץ הסימון הייתה שווה ל-hash). נמחק רק כשהשירות מאשר שהוא מחזיק קוד שחזור (שירות ישן: נשאר, כדי לא לאבד את מסלול החירום)
            bool recovery = (!string.IsNullOrEmpty(Config.Get<string>("kiosk.recoveryHash", null)) || !string.IsNullOrEmpty(Config.Get<string>("kiosk.recoverySalt", null)))
                            && HardLock.ServiceHasRecovery();
            bool password = !string.IsNullOrEmpty(Config.Get<string>("kiosk.passwordHash", null)) || !string.IsNullOrEmpty(Config.Get<string>("kiosk.salt", null));
            if (!password && !recovery) return;
            if (password) { Config.Remove("kiosk.passwordHash"); Config.Remove("kiosk.salt"); }
            if (recovery) { Config.Remove("kiosk.recoveryHash"); Config.Remove("kiosk.recoverySalt"); }
            Config.Save();
            Config.Log("kiosk: עותק hash הסיסמה" + (recovery ? " וקוד השחזור" : "") + " המקומי הוסר (השירות מקור האמת)");
        }
        catch (Exception ex) { Config.Log("purge local password: " + ex.Message); }
    }

    // ---------- חתימת מפתחות האבטחה (ConfigSeal) ----------
    // WHY (T1): config.json כתיב למשתמש. כשהשירות פעיל, מפתחות readOnly/openSections/allowOpenExtensions/allowExcelDocs/strict/blockTaskManager
    // חייבים להיות חתומים (HMAC במפתח של השירות). חתימה חסרה/לא תואמת => ערכי ברירת מחדל מחמירים. בלי שירות/שירות ישן: תאימות לאחור (Intact=true).
    private static bool _sealOk = true;
    private static DateTime _sealAt = DateTime.MinValue;

    /// <summary>האם מפתחות האבטחה ב-config.json תואמים את החתימה (מטמון 2 שניות; מתאפס אחרי חתימה)</summary>
    internal static bool SecurityIntact()
    {
        if (DateTime.UtcNow - _sealAt < TimeSpan.FromSeconds(2)) return _sealOk;
        bool ok;
        try { ok = ConfigSeal.SecurityIntact(Config.Snapshot()); } catch { ok = false; }
        _sealOk = ok; _sealAt = DateTime.UtcNow;
        return ok;
    }

    /// <summary>חותם את מצב מפתחות האבטחה הנוכחי ושומר. נקרא אחרי כל שינוי לגיטימי של המפתחות. כשל חתימה: נשאר לא-חתום (fail-closed רק כשהשירות נאכף)</summary>
    internal static void SealConfig()
    {
        try
        {
            var snap = Config.Snapshot();
            ConfigSeal.Seal(snap);
            if (snap[ConfigSeal.SealKey] is JsonNode m) { Config.Set(ConfigSeal.SealKey, m.DeepClone()); Config.Save(); }
        }
        catch (Exception ex) { Config.Log("config seal: " + ex.Message); }
        _sealAt = DateTime.MinValue;
    }

    /// <summary>ערך מפתח אבטחה בוליאני: ההגדרה כשהחתימה תקינה, אחרת ברירת המחדל המחמירה</summary>
    private static bool SecBool(string key, bool fallback, bool strictDefault) =>
        SecurityIntact() ? Config.Get(key, fallback) : strictDefault;

    public static string[] AllowedExtensions => SecurityIntact() ? Config.GetList("kiosk.allowOpenExtensions") : Array.Empty<string>();

    /// <summary>
    /// בסיס קבוע (תמיד מותר, לא ניתן להסרה דרך ההגדרות) לרשימת הסיומות ש-DialogGuard מתיר לבחור/לפתוח
    /// בדיאלוגי Open/Save מקוריים של תוכנות חיצוניות מאושרות (Word וכו') בזמן שמגבלה כלשהי פעילה.
    /// נשאר קטן בכוונה: רק סוגי המסמכים שהפרויקט עצמו כבר מתייחס אליהם כ"ספרים"/מסמכים רלוונטיים
    /// (ר' docs/ROADMAP.md, docs/SDK-998-OPPORTUNITIES.md — "קבצי PDF/Word"). מנהל יכול להוסיף עוד
    /// דרך אותה הגדרה קיימת (kiosk.allowOpenExtensions) שמשמשת גם את /fs/open הפנימי.
    /// </summary>
    public static readonly string[] DialogGuardBaselineExtensions = { "docx", "pdf" };

    /// <summary>האיחוד בפועל שנאכף ב-DialogGuard: הבסיס הקבוע + מה שהמנהל הוסיף דרך ההגדרות</summary>
    public static HashSet<string> DialogGuardAllowedExtensions()
    {
        var set = new HashSet<string>(DialogGuardBaselineExtensions, StringComparer.OrdinalIgnoreCase);
        foreach (string x in AllowedExtensions) set.Add(x.TrimStart('.'));
        return set;
    }

    /// <summary>במצב readOnly (ברירת מחדל כבוי) שינוי קבצים חסום בזמן נעילה</summary>
    public static bool ReadOnly => SecBool("kiosk.readOnly", false, true);

    /// <summary>
    /// מדיניות מסמכי Office בסייר הקבצים: ברירת מחדל - רק Word (docx/doc) ניתן לצפייה/פתיחה.
    /// גיליונות Excel (xlsx) מותרים רק כשהמנהל מפעיל כאן; מצגות PowerPoint (pptx) חסומות תמיד, בלי אפשרות הפעלה.
    /// האכיפה מתבצעת בתוסף סייר הקבצים עצמו (file-explorer/js/app.js), שקורא את הערך הזה מ-Status().
    /// </summary>
    public static bool AllowExcelDocs => SecBool("kiosk.allowExcelDocs", false, false);

    /// <summary>חוסם שינוי קבצים בזמן נעילה רק כאשר הופעל מצב readOnly</summary>
    public static void EnsureFileWriteAllowed()
    {
        if (IsActive && ReadOnly) throw BridgeException.Locked("שינוי קבצים חסום במצב נעילה");
    }

    // ---------- אילו הגדרות פתוחות בזמן נעילה ----------

    /// <summary>כל ההגדרות שאפשר לפתוח או לחסום בנעילה. רשת, בלוטות', מצב טיסה, USB ושעה חסומים כברירת מחדל</summary>
    public static readonly string[] AllSections =
        { "audio", "display", "energy", "keyboard", "mouse", "printer", "time", "wifi", "bluetooth", "airplane", "usb", "power" };
    private static readonly string[] DefaultOpenSections = { "audio", "display", "energy", "keyboard", "mouse", "printer" };

    public static string[] OpenSections =>
        !SecurityIntact() ? Array.Empty<string>() :   // חתימה לא תקינה: שום הגדרה לא פתוחה
        Config.HasList("kiosk.openSections") ? Config.GetList("kiosk.openSections") : DefaultOpenSections;

    /// <summary>שער לכל נקודות הקצה של ההגדרות: בזמן נעילה הגדרה שלא נפתחה חסומה גם בגשר, לא רק מוסתרת בתוסף</summary>
    public static void EnsureSectionAllowed(string section)
    {
        if (IsActive && !OpenSections.Contains(section, StringComparer.OrdinalIgnoreCase))
            throw BridgeException.Locked("ההגדרה הזו סגורה במצב נעילה");
    }

    private static readonly (string prefix, string section)[] SectionRoutes =
    {
        ("/sys/audio/", "audio"), ("/sys/display/", "display"), ("/sys/energy/", "energy"),
        ("/sys/keyboard/", "keyboard"), ("/sys/mouse/", "mouse"), ("/sys/printers/", "printer"),
        ("/sys/time/", "time"), ("/sys/wifi/", "wifi"), ("/sys/bluetooth/", "bluetooth"),
        ("/sys/airplane/", "airplane"), ("/sys/usb/", "usb"),
    };

    /// <summary>לאיזו הגדרה שייכת פעולת שינוי. קריאות (get, list וכו') פתוחות תמיד; null = לא שייך להגדרה</summary>
    public static string SectionOf(string path, JsonElement body)
    {
        string p = path.ToLowerInvariant();
        if (p.EndsWith("/get") || p.EndsWith("/list") || p.EndsWith("/devices") || p.EndsWith("/networks")) return null;
        if (p == "/sys/radios/set") return body.Str("kind", "wifi").ToLowerInvariant() == "bluetooth" ? "bluetooth" : "wifi";
        foreach (var (prefix, section) in SectionRoutes)
            if (p.StartsWith(prefix)) return section;
        return null;
    }

    /// <summary>מעבר לרשת אחרת, הדלקת רדיו והצמדת בלוטות' (בעיה #1): חסומים בנעילה אלא אם ההגדרה נפתחה</summary>
    public static void EnsureNetworkAllowed(string section = "wifi") => EnsureSectionAllowed(section);

    /// <summary>
    /// האם המשתמש הנוכחי חבר בקבוצת ה-Administrators המקומית.
    /// זו "נקודת תורפה": משתמש עם הרשאות מנהל יכול לבטל את הנעילה (לעצור את הגשר,
    /// לשנות רישום/מדיניות, או להיכנס כמנהל) — ולכן מומלץ שעמדת לימוד תרוץ בחשבון רגיל.
    /// מזהה חברות בקבוצה גם כאשר הטוקן מסונן על ידי UAC (deny-only group עדיין מופיעה).
    /// </summary>
    public static bool UserIsAdministrator()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            return id.Groups != null && id.Groups.Any(g => g == admins);
        }
        catch { return false; }
    }

    /// <summary>אזהרות על מצב הקשחה חלש, להצגה למי שמפעיל את הנעילה</summary>
    private static string[] Warnings()
    {
        var w = new List<string>();
        if (UserIsAdministrator()) w.Add("admin");
        return w.ToArray();
    }

    public static void Register()
    {
        Server.Route("/kiosk/status", _ => Status());
        Server.Route("/kiosk/set-password", SetPassword);
        Server.Route("/kiosk/enable", Enable);
        Server.Route("/kiosk/disable", Disable);
        Server.Route("/kiosk/settings", Settings);
        Server.Route("/kiosk/temp-unlock", TempUnlockStart);
        Server.Route("/kiosk/temp-unlock/end", TempUnlockEnd);
        Programs.Register();
    }

    // ---------- סיסמה ----------
    // החישוב הקריפטוגרפי הטהור (Hash/Matches/נורמליזציה/קוד שחזור) משותף ל-Kiosk ול-HardLockService
    // דרך Secret.cs, כדי ששני המקומות לא יתפצלו בלי ששמים לב. כאן רק קריאה/כתיבה מ-Config.

    private static bool Matches(string secret, string hashKey, string saltKey)
    {
        string stored = Config.Get<string>(hashKey, null), salt = Config.Get<string>(saltKey, null);
        if (!Secret.Matches(secret, stored, salt)) return false;
        // WHY (T1): גיבוב ישן (200k) משודרג לפורמט הנוכחי בכניסה מוצלחת, כשהסוד הגלוי בידינו (אותו salt)
        if (Secret.NeedsUpgrade(stored))
            try { Config.Set(hashKey, Secret.Hash(secret, Convert.FromBase64String(salt))); Config.Save(); }
            catch (Exception ex) { Config.Log("upgrade hash: " + ex.Message); }
        return true;
    }

    private static void StoreSecret(string secret, string hashKey, string saltKey)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        Config.Set(saltKey, Convert.ToBase64String(salt));
        Config.Set(hashKey, Secret.Hash(secret, salt));
    }

    /// <summary>בודק סיסמה או קוד שחזור, עם השהיה אחרי 5 ניסיונות שגויים</summary>
    private static void Verify(string password, bool allowRecovery)
    {
        lock (Gate)
        {
            ThrowIfLockedOut();
            // כשרכיב ההקשחה מותקן — הוא מקור האמת לסיסמה (עריכת config.json לא עוזרת)
            if (HardLock.Available) { HardLock.Verify(password, allowRecovery); ConfigSeal.NoteVerified(password); PurgeLocalPassword(); return; }
            // fail-closed: שירות מותקן שלא עונה לא מחזיר אותנו ל-hash מקומי
            if (ServiceDown()) throw new BridgeException("unsupported", "רכיב ההקשחה לא מגיב ולכן אי אפשר לאמת סיסמה כרגע. נסו שוב בעוד רגע (או קוד שחזור בקיצור החירום)", 503);
            bool ok = Matches(password, "kiosk.passwordHash", "kiosk.salt") ||
                      (allowRecovery && Matches(Secret.NormalizeRecovery(password), "kiosk.recoveryHash", "kiosk.recoverySalt"));
            if (ok)
            {
                ResetBackoff();
                return;
            }
            Fail("סיסמה שגויה");
        }
    }

    /// <summary>השהיה אחרי 5 ניסיונות שגויים — משותפת לסיסמת הנעילה ולסיסמת הפתיחה הזמנית. נקרא תחת Gate</summary>
    private static void ThrowIfLockedOut()
    {
        LoadBackoff();
        if (DateTime.UtcNow < _lockoutUntil)
        {
            int secs = (int)Math.Ceiling((_lockoutUntil - DateTime.UtcNow).TotalSeconds);
            throw new BridgeException("wrong_password", $"יותר מדי ניסיונות שגויים. נסו שוב בעוד {secs} שניות", 429);
        }
    }

    private static bool _backoffLoaded;
    private static long _lastFailTicks;

    /// <summary>טוען מצב backoff שנשמר (שורד אתחול). WHY: קודם הכל היה בזיכרון ואתחול איפס את ההשהיה. נקרא תחת Gate</summary>
    private static void LoadBackoff()
    {
        if (_backoffLoaded) return;
        _backoffLoaded = true;
        try
        {
            long now = DateTime.UtcNow.Ticks;
            _lastFailTicks = Config.Get<long>("kiosk.bo.last", 0);
            _failures = BridgeBackoff.EffectiveFails(Config.Get<int>("kiosk.bo.fails", 0), _lastFailTicks, now);
            long until = BridgeBackoff.ClampUntil(Config.Get<long>("kiosk.bo.until", 0), now);
            _lockoutUntil = until > 0 ? new DateTime(until, DateTimeKind.Utc) : DateTime.MinValue;
        }
        catch (Exception ex) { Config.Log("backoff load: " + ex.Message); }
    }

    private static void SaveBackoff()
    {
        try
        {
            Config.Set("kiosk.bo.fails", _failures);
            Config.Set("kiosk.bo.last", _lastFailTicks);
            Config.Set("kiosk.bo.until", _lockoutUntil == DateTime.MinValue ? 0L : _lockoutUntil.Ticks);
            Config.Save();
        }
        catch (Exception ex) { Config.Log("backoff save: " + ex.Message); }
    }

    /// <summary>הצלחה: מאפסת (גם בקובץ). [x1] נקודת חיבור מ-Verify/VerifyTempSecret</summary>
    private static void ResetBackoff()
    {
        LoadBackoff();
        if (_failures == 0 && _lockoutUntil == DateTime.MinValue) return;
        _failures = 0; _lockoutUntil = DateTime.MinValue; _lastFailTicks = 0;
        SaveBackoff();
    }

    /// <summary>רושם ניסיון שגוי (מ-5: השהיה 1,2,4,... דקות עד שעה, נשמרת) וזורק. נקרא תחת Gate. קוד שחזור/--unlock לא עוברים כאן</summary>
    private static void Fail(string message)
    {
        LoadBackoff();
        _failures++;
        _lastFailTicks = DateTime.UtcNow.Ticks;
        int lockSecs = BridgeBackoff.LockSeconds(_failures);
        if (lockSecs > 0) _lockoutUntil = DateTime.UtcNow.AddSeconds(lockSecs);
        SaveBackoff();
        throw new BridgeException("wrong_password", message, 401);
    }

    /// <summary>אימות סיסמה לשימוש מודולים אחרים (בלי קוד שחזור)</summary>
    internal static void RequirePassword(string password) => Verify(password, allowRecovery: false);

    /// <summary>כמו RequirePassword, אבל מקבל גם את קוד השחזור (כדי שלא ננעל מחוץ למדיניות סייר הקבצים)</summary>
    internal static void RequirePasswordOrRecovery(string password) => Verify(password, allowRecovery: true);

    /// <summary>האם הוגדרה סיסמת נעילה (בקובץ ההגדרות או ברכיב ההקשחה)</summary>
    internal static bool PasswordConfigured => HasPassword || HardLock.Available;

    [StructLayout(LayoutKind.Sequential)] private struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }
    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LASTINPUTINFO lii);
    private static long IdleMs()
    {
        var lii = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        return GetLastInputInfo(ref lii) ? unchecked((uint)Environment.TickCount - lii.dwTime) : 0;
    }

    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
    [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool IsHungAppWindow(IntPtr hWnd);

    /// <summary>
    /// מביא חלון לחזית באמת: משחזר חלון מוקטן, ועוקף את חסימת Windows להעברת פוקוס מתהליך רקע
    /// (חיבור חוטי הקלט + לחיצת Alt שקטה + הצמדה זמנית למעלה). בלי זה התוכנה נפתחת מאחור ולא ניתן להשתמש בה.
    /// </summary>
    internal static void BringToFront(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) return;
        if (IsIconic(hwnd)) ShowWindow(hwnd, 9 /* SW_RESTORE */);
        else ShowWindow(hwnd, 5 /* SW_SHOW */);
        uint cur = GetCurrentThreadId();
        IntPtr fg = GetForegroundWindow();
        uint fgThread = fg == IntPtr.Zero ? 0 : GetWindowThreadProcessId(fg, out _);
        uint target = GetWindowThreadProcessId(hwnd, out _);
        bool a1 = false, a2 = false;
        try
        {
            // לא מתחברים לחוט של חלון תקוע: זה היה עלול לתקוע גם את הגשר
            if (fgThread != 0 && fgThread != cur && !IsHungAppWindow(fg)) a1 = AttachThreadInput(cur, fgThread, true);
            if (target != 0 && target != cur && !IsHungAppWindow(hwnd)) a2 = AttachThreadInput(cur, target, true);
            // Alt למטה ולמעלה: Windows מתירה אז החלפת חזית. מקש לא מוקצה (0xE8) באמצע מונע מהתוכנה להפעיל את תפריט Alt שלה
            keybd_event(0x12, 0, 0, UIntPtr.Zero);
            keybd_event(0xE8, 0, 0, UIntPtr.Zero);
            keybd_event(0xE8, 0, 2, UIntPtr.Zero);
            keybd_event(0x12, 0, 2, UIntPtr.Zero);
            SetWindowPos(hwnd, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0040); // TOPMOST, בלי שינוי גודל
            SetWindowPos(hwnd, new IntPtr(-2), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0040); // חזרה ל-NOTOPMOST (נשאר בחזית)
            BringWindowToTop(hwnd);
            if (!SetForegroundWindow(hwnd)) SwitchToThisWindow(hwnd, true);
        }
        catch (Exception ex) { Config.Log("bring to front: " + ex.Message); }
        finally
        {
            if (a1) AttachThreadInput(cur, fgThread, false);
            if (a2) AttachThreadInput(cur, target, false);
        }
    }

    // ---------- נקודות קצה ----------

    private static object Status() => new
    {
        active = IsActive,
        hasPassword = HasPassword,
        strict = SecBool("kiosk.strict", false, true),
        autoStartBridge = Installer.HasAutoStart(),
        otzariaPath = OtzariaPath() ?? "",
        blockTaskManager = SecBool("kiosk.blockTaskManager", true, true),
        allowOpenExtensions = AllowedExtensions,
        readOnly = ReadOnly,
        allowExcelDocs = AllowExcelDocs,
        clock = Config.Get("kiosk.clock", true),
        clockPosition = Config.Get("kiosk.clockPosition", "bottom-left"),
        restScreen = Config.Get("kiosk.restScreen", true),
        restIdleMinutes = Config.Get("kiosk.restIdleMinutes", 0),
        restZmanimSide = Config.Get("kiosk.restZmanimSide", "right"),
        zmanimLocation = Config.Get("kiosk.zmanimLocation", ""),
        zmanimCity = Config.Get("kiosk.zmanimCity", ""),
        zmanimElevation = Config.Get("kiosk.zmanimElevation", 0.0),
        zmanimFallbackEngine = Config.Get("kiosk.zmanimFallbackEngine", "noaa"),
        zmanimShow = Config.HasList("kiosk.zmanimShow") ? Config.GetList("kiosk.zmanimShow") : null,
        zmanimDiaspora = Config.Get("kiosk.zmanimDiaspora", false),
        candleLightingMinutes = Config.Get("kiosk.candleLightingMinutes", 18),
        showParsha = Config.Get("kiosk.showParsha", false),
        showDafYomi = Config.Get("kiosk.showDafYomi", false),
        yahrzeitMonth = Config.Get("kiosk.yahrzeitMonth", ""),
        yahrzeitDay = Config.Get("kiosk.yahrzeitDay", 0),
        yahrzeitLabel = Config.Get("kiosk.yahrzeitLabel", ""),
        venueName = Config.Get("kiosk.venueName", ""),
        dedicationText = Config.Get("kiosk.dedicationText", ""),
        fullscreenOtzaria = Config.Get("kiosk.fullscreenOtzaria", true),
        sideBar = Config.Get("kiosk.sideBar", true),
        sideBarIconsOnly = Config.Get("kiosk.sideBarIconsOnly", false),
        sideBarPosition = Config.Get("kiosk.sideBarPosition", "right"),
        resetOnShutdown = Config.Get("kiosk.resetOnShutdown", false),
        openSections = OpenSections,
        allSections = AllSections,
        warnings = Warnings(),
        schedule = new { enabled = ScheduleEnabled, start = ScheduleStart, end = ScheduleEnd, warn = ScheduleWarn },
        activatedBy = Config.Get<string>("kiosk.activatedBy", "manual"),
        twoFactor = TwoFactor.Enabled,
        twoFactorAvailable = TwoFactor.Available(),
        tempUnlock = TempUnlockStatus(),
        hard = HardLock.Status(),
    };

    // ---------- תזמון נעילה לפי שעות ----------

    public static bool ScheduleEnabled => Config.Get("kiosk.schedule.enabled", false);
    public static string ScheduleStart => Config.Get<string>("kiosk.schedule.start", "20:00");
    public static string ScheduleEnd => Config.Get<string>("kiosk.schedule.end", "02:00");

    public static bool ScheduleWarn => Config.Get("kiosk.schedule.warn", true);

    private static System.Threading.Timer _schedTimer;
    private static DateTime _warnOccurrence = DateTime.MinValue;
    private static bool _warned5, _warned1;

    /// <summary>
    /// מתזמן את הנעילה לפי חלון שעות, ומציג אזהרת ספירה-לאחור לפני שהנעילה נכנסת.
    /// התזמון עצמו (הפעלה/ביטול) רץ רק כשרכיב ההקשחה אינו זמין — כשהוא מותקן הוא מקור האמת
    /// ומתזמן בעצמו (HardLockService.Tick). האזהרה מוצגת תמיד בחשבון המשתמש, כי זו הצגה למשתמש.
    /// </summary>
    public static void StartScheduler()
    {
        _schedTimer ??= new System.Threading.Timer(
            _ => { try { WarnTick(); ScheduleTick(); } catch (Exception ex) { Config.Log("schedule: " + ex.Message); } },
            null, 3000, 15000);
        // פתיחה זמנית: טיימר קצר יותר, כדי שהנעילה תחזור בזמן ושהבאנר יוצג/יוסתר מיד
        _tempTimer ??= new System.Threading.Timer(
            _ => { try { TempTick(); } catch (Exception ex) { Config.Log("temp unlock: " + ex.Message); } },
            null, 3000, 2000);
    }

    /// <summary>הזמן הקרוב שבו חלון הנעילה מתחיל (מהיום אם עוד לא עבר, אחרת מחר)</summary>
    private static DateTime NextStart()
    {
        var t = Sched.ParseTime(ScheduleStart) ?? TimeSpan.FromHours(20);
        var now = DateTime.Now;
        var today = now.Date + t;
        return today > now ? today : today.AddDays(1);
    }

    /// <summary>עד מתי לדלג על נעילה מתוזמנת (אחרי "ביטול הנעילה" באזהרה)</summary>
    // מושווה כ-DateTimeOffset (רגע מוחלט) ולא כשעון קיר, כדי שמעבר שעון קיץ/חורף לא יקצר או יאריך את הדילוג
    private static DateTimeOffset SkipUntil()
    {
        var s = Config.Get<string>("kiosk.schedule.skipUntil", null);
        return DateTimeOffset.TryParse(s, null, System.Globalization.DateTimeStyles.RoundtripKind, out var d) ? d : DateTimeOffset.MinValue;
    }

    private static bool Skipping => DateTimeOffset.Now < SkipUntil();

    /// <summary>
    /// "ביטול הנעילה" מתוך אזהרת הספירה-לאחור (אחרי אימות סיסמה): מדלג על חלון הנעילה הקרוב
    /// עד סופו, כך שהמחשב לא יינעל הפעם. החלון הבא (מחר) יחול כרגיל.
    /// </summary>
    public static void ScheduleSkipCurrent(string password)
    {
        RequirePassword(password);
        DateTime start = NextStart();
        var st = Sched.ParseTime(ScheduleStart) ?? TimeSpan.FromHours(20);
        var en = Sched.ParseTime(ScheduleEnd) ?? TimeSpan.FromHours(2);
        DateTime end = start.Date + en;
        if (en <= st) end = end.AddDays(1); // חלון שחוצה חצות
        Config.Set("kiosk.schedule.skipUntil", end.ToString("o"));
        Config.Save();
        if (HardLock.Available)
            try { HardLock.Skip(password, end.ToString("o")); } catch (Exception ex) { Config.Log("hardlock skip: " + ex.Message); }
        Overlay.HideCountdown();
        Config.Log("schedule: הנעילה הקרובה בוטלה ידנית עד " + end);
    }

    private static void WarnTick()
    {
        // בזמן פתיחה זמנית הנעילה לא תיכנס עד סוף הפתיחה, ולכן אזהרת "המחשב ננעל בעוד..." הייתה מטעה
        if (!ScheduleEnabled || !ScheduleWarn || IsActive || Skipping || TempUnlockSecondsLeft() > 0) { Overlay.HideCountdown(); return; }
        DateTime start = NextStart();
        if (start != _warnOccurrence) { _warnOccurrence = start; _warned5 = _warned1 = false; }
        double secs = (start - DateTime.Now).TotalSeconds;
        if (secs <= 300 && secs > 60 && !_warned5) { _warned5 = true; Overlay.ShowCountdown(start); }
        else if (secs <= 60 && secs > 0 && !_warned1) { _warned1 = true; Overlay.ShowCountdown(start); }
    }

    private static void ScheduleTick()
    {
        if (HardLock.Available) return; // כשיש שירות — הוא המתזמן
        // תזמון כבוי, או דילוג ידני, נחשבים כמו "מחוץ לחלון"
        bool inWindow = ScheduleEnabled && !Skipping && Sched.InWindow(ScheduleStart, ScheduleEnd, DateTime.Now);
        // פתיחה זמנית ממתינה: הנעילה שוחררה רק עד סוף הפתיחה, ו-TempTick הוא שמחזיר אותה (או לא, אם החלון הסתיים)
        if (TempUnlockPendingLocal) return;
        if (inWindow && !IsActive)
        {
            if (!HasPassword) { LogThrottled("schedNoPw", "schedule: אין סיסמת נעילה — התזמון לא הופעל", 3600); return; }
            string otz = OtzariaPath();
            if (string.IsNullOrEmpty(otz) || !File.Exists(otz))
            { LogThrottled("schedNoOtz", "schedule: אוצריא לא נמצאה — התזמון לא הופעל", 3600); return; }
            Config.Log($"schedule: בתוך חלון הנעילה ({ScheduleStart}–{ScheduleEnd}) — הנעילה מופעלת");
            Config.Set("kiosk.activatedBy", "schedule");
            Config.Set("kiosk.active", true);
            Config.Set("kiosk.confirmed", true);
            Config.Save();
            try { Apply(); } catch (Exception ex) { Config.Log("schedule apply: " + ex.Message); Release(); }
        }
        else if (!inWindow && IsActive && Config.Get<string>("kiosk.activatedBy", "manual") == "schedule")
        {
            Config.Log("schedule: מחוץ לחלון הנעילה — הנעילה מבוטלת אוטומטית");
            Release();
        }
    }

    // ---------- פתיחה זמנית: כל הנעילה נפתחת לזמן קצוב, בסיסמה נפרדת מסיסמת הנעילה ----------
    // מטרה: מנהל/הורה יכול לאפשר למישהו להשתמש במחשב לזמן קצר, בלי לגלות לו את סיסמת הנעילה.
    // הסוד נשמר כמו סיסמת הנעילה (PBKDF2 דרך Secret.cs) ב-kiosk.tempUnlockHash/kiosk.tempUnlockSalt, ומשוכפל
    // לרכיב ההקשחה (tempUnlockHash/tempUnlockSalt ב-hardlock.json) כשהוא מותקן. הוא לעולם אינו מתקבל כסיסמת
    // הנעילה, וסיסמת הנעילה/קוד השחזור אינם מתקבלים כאן — אלה סודות נפרדים.
    // תנאי קבוע: האימות הדו-שלבי (Windows Hello) פעיל. סוד משני שאפשר למסור לאחרים מקובל רק כשזהות המנהל
    // עצמה מאומתת חזק. אם האימות הדו-שלבי כובה (או הפתיחה כובתה) — הפתיחה "רדומה": אי אפשר להשתמש בה,
    // ופתיחה שרצה באותו רגע מסתיימת והנעילה חוזרת. הסיסמה, המשך והמתג נשמרים, וחוזרים לפעול כשמפעילים
    // שוב את האימות הדו-שלבי.
    // מקור האמת: כשרכיב ההקשחה מותקן — זמן הסיום נשמר רק אצלו (hardlock.json, לא כתיב למשתמש), והוא שמחזיר את
    // הנעילה בתום הזמן (HardLockService.Tick). בלי רכיב הקשחה — הגשר עצמו (TempTick). זו נקודת תורפה ידועה,
    // כמו בשאר המצב בלי רכיב הקשחה: config.json כתיב למשתמש, ומי שהמחשב פתוח לפניו יכול לערוך אותו.

    private const string KTuHash = "kiosk.tempUnlockHash", KTuSalt = "kiosk.tempUnlockSalt",
        KTuEnabled = "kiosk.tempUnlockEnabled", KTuMinutes = "kiosk.tempUnlockMinutes", KTuUntil = "kiosk.tempUnlockUntil";

    private static readonly object TuGate = new();
    private static System.Threading.Timer _tempTimer;
    private static DateTimeOffset? _tuUntil;   // בלי רכיב הקשחה: זמן הסיום (הזיכרון קובע בזמן ריצה)
    private static bool _tuLoaded;

    private static bool TempUnlockEnabledLocal => Config.Get(KTuEnabled, false);
    private static int TempUnlockMinutesLocal => TempUnlock.ClampMinutes(Config.Get(KTuMinutes, TempUnlock.DefaultMinutes));
    private static bool TempUnlockConfiguredLocal => !string.IsNullOrEmpty(Config.Get<string>(KTuHash, null));

    /// <summary>
    /// זמן הסיום המקומי. נטען מ-config.json פעם אחת (כדי לשרוד הפעלה מחדש של הגשר), ומאז הזיכרון קובע —
    /// עריכת הקובץ בזמן ריצה לא מאריכה את הפתיחה. ערך רחוק מ-60 דקות נחשב פג (TempUnlock.SecondsLeft).
    /// </summary>
    private static DateTimeOffset? LocalTempUntil
    {
        get
        {
            lock (TuGate)
            {
                if (!_tuLoaded) { _tuLoaded = true; _tuUntil = TempUnlock.ParseUntil(Config.Get<string>(KTuUntil, null)); }
                return _tuUntil;
            }
        }
    }

    /// <summary>פתיחה זמנית מקומית שהנעילה עוד לא הוחזרה ממנה (גם אם הזמן כבר עבר — TempTick יחזיר אותה)</summary>
    private static bool TempUnlockPendingLocal => LocalTempUntil != null;

    /// <summary>תמונת מצב אחידה: מרכיב ההקשחה כשהוא מותקן (מקור האמת), אחרת מ-config.json</summary>
    private sealed record TempView(bool Enabled, bool Configured, int Minutes, bool TwoFactor, DateTimeOffset? Until, bool Service, bool ServiceOutdated)
    {
        public bool Usable => Enabled && Configured && TwoFactor && !ServiceOutdated;
        public int SecondsLeft(DateTimeOffset now) => Usable ? TempUnlock.SecondsLeft(Until, now) : 0;
    }

    private static TempView CurrentTemp()
    {
        if (HardLock.Available)
        {
            var t = HardLock.Temp;
            if (t != null) return new TempView(t.Enabled, t.Configured, t.Minutes, t.TwoFactor, t.Until, true, false);
            // רכיב הקשחה ישן שאינו מכיר פתיחה זמנית: אי אפשר להשתמש בה עד שיעודכן (אחרת הוא היה נועל מחדש תוך שניות)
            return new TempView(TempUnlockEnabledLocal, TempUnlockConfiguredLocal, TempUnlockMinutesLocal, HardLock.ServiceTwoFactor, null, true, true);
        }
        return new TempView(TempUnlockEnabledLocal, TempUnlockConfiguredLocal, TempUnlockMinutesLocal, TwoFactor.Enabled, LocalTempUntil, false, false);
    }

    /// <summary>כמה שניות נשארו לפתיחה הזמנית (0 = לא פעילה)</summary>
    public static int TempUnlockSecondsLeft() => CurrentTemp().SecondsLeft(DateTimeOffset.Now);

    private static object TempUnlockStatus()
    {
        var v = CurrentTemp();
        int left = v.SecondsLeft(DateTimeOffset.Now);
        return new
        {
            enabled = v.Enabled,
            configured = v.Configured,
            minutes = v.Minutes,
            maxMinutes = TempUnlock.MaxMinutes,
            twoFactorOn = v.TwoFactor,
            usable = v.Usable,
            active = left > 0,
            secondsLeft = left,
            until = left > 0 ? v.Until.Value.ToString("o") : "",
            service = v.Service,
            serviceOutdated = v.ServiceOutdated,
        };
    }

    /// <summary>
    /// כל 2 שניות: מציג/מסתיר את באנר הספירה לאחור, ובלי רכיב הקשחה גם מחזיר את הנעילה בתום הזמן
    /// (או מיד, אם הפתיחה הפכה לרדומה כי האימות הדו-שלבי כובה). עם רכיב הקשחה הוא שמחזיר את הנעילה,
    /// והגשר ננעל דרך HardLock.StartSync.
    /// </summary>
    private static void TempTick()
    {
        var now = DateTimeOffset.Now;
        var lu = LocalTempUntil;
        if (lu != null)
        {
            bool usableLocal = TempUnlockEnabledLocal && TempUnlockConfiguredLocal && TwoFactor.Enabled;
            if (!usableLocal) EndTempUnlockLocal("הפתיחה הזמנית כובתה או שהאימות הדו-שלבי כובה");
            else if (TempUnlock.SecondsLeft(lu, now) == 0) EndTempUnlockLocal("הזמן הסתיים");
        }
        var v = CurrentTemp();
        if (v.SecondsLeft(now) > 0) Overlay.ShowTempUnlock(v.Until.Value);
        else Overlay.HideTempUnlock();
    }

    /// <summary>
    /// סיום פתיחה זמנית מקומית. relock: מחזיר את הנעילה — נעילה ידנית תמיד, ונעילה שהתזמון הפעיל רק אם
    /// עדיין בתוך חלון התזמון. בלי relock (הפעלה/ביטול ידני בסיסמה, קוד שחזור) רק מנקה.
    /// </summary>
    private static void EndTempUnlockLocal(string reason, bool relock = true)
    {
        _ = LocalTempUntil;
        lock (TuGate)
        {
            if (_tuUntil == null) return;
            _tuUntil = null;
        }
        Config.Set(KTuUntil, "");
        Overlay.HideTempUnlock();
        bool bySchedule = Config.Get<string>("kiosk.activatedBy", "manual") == "schedule";
        bool inWindow = ScheduleEnabled && !Skipping && Sched.InWindow(ScheduleStart, ScheduleEnd, DateTime.Now);
        if (relock && !IsActive && (!bySchedule || inWindow))
        {
            Config.Log("temp unlock: " + reason + " — הנעילה חוזרת");
            Config.Set("kiosk.active", true);
            Config.Set("kiosk.confirmed", true);
            Config.Save();
            // כשל סגור: גם אם חלק מההחלה נכשל, מצב הנעילה נשאר פעיל והשומר ממשיך (כמו ForceActivate)
            try { Apply(); } catch (Exception ex) { Config.Log("temp unlock relock: " + ex.Message); }
            return;
        }
        Config.Save();
        Config.Log("temp unlock: " + reason + (relock && bySchedule && !inWindow ? " — חלון התזמון הסתיים בינתיים, המחשב נשאר פתוח" : ""));
    }

    /// <summary>בודק את סיסמת הפתיחה הזמנית בלבד (לא את סיסמת הנעילה), עם אותה השהיה אחרי 5 ניסיונות שגויים</summary>
    private static void VerifyTempSecret(string secret)
    {
        lock (Gate)
        {
            ThrowIfLockedOut();
            if (Matches(secret, KTuHash, KTuSalt)) { ResetBackoff(); return; }
            Fail("סיסמת הפתיחה הזמנית שגויה");
        }
    }

    /// <summary>POST /kiosk/temp-unlock {password}: הסיסמה כאן היא סיסמת הפתיחה הזמנית, לא סיסמת הנעילה</summary>
    private static object TempUnlockStart(JsonElement b)
    {
        string secret = b.Str("password") ?? "";
        if (secret.Length == 0) throw BridgeException.BadRequest("יש להקליד את סיסמת הפתיחה הזמנית");
        var v = CurrentTemp();
        if (v.ServiceOutdated)
            throw new BridgeException("unsupported", "רכיב ההקשחה צריך עדכון כדי לתמוך בפתיחה זמנית (נעילה ← נעילה מוקשחת ← עדכון)", 503);
        if (HardLock.Available)
        {
            // השירות מאמת מול הגיבוב שלו, בודק שהפתיחה והאימות הדו-שלבי פעילים ושהמחשב נעול, וקובע את זמן הסיום אצלו
            HardLock.TempUnlockStart(secret);
            if (IsActive) Release(keepStrict: true); // בלי להמתין לסנכרון (עד 5 שניות)
        }
        else
        {
            if (!v.TwoFactor) throw new BridgeException("forbidden", "פתיחה זמנית פועלת רק כשהאימות הדו-שלבי (Windows Hello) מופעל", 403);
            if (!v.Enabled || !v.Configured) throw BridgeException.BadRequest("הפתיחה הזמנית כבויה או שלא הוגדרה לה סיסמה. אפשר להפעיל אותה בהגדרות הנעילה");
            VerifyTempSecret(secret);
            if (!IsActive) throw BridgeException.BadRequest("המחשב אינו נעול כרגע");
            var until = TempUnlock.Until(DateTimeOffset.Now, v.Minutes);
            lock (TuGate) { _tuLoaded = true; _tuUntil = until; }
            Config.Set(KTuUntil, until.ToString("o"));
            Release(keepStrict: true); // שומר גם את config.json
        }
        Config.Log($"temp unlock: המחשב נפתח זמנית ל-{v.Minutes} דקות" + (v.Service ? " (לפי רכיב ההקשחה)" : ""));
        try { TempTick(); } catch (Exception ex) { Config.Log("temp unlock: " + ex.Message); }
        return Status();
    }

    /// <summary>POST /kiosk/temp-unlock/end: סיום מוקדם. רק מחזיר את הנעילה (הכיוון הבטוח), ולכן אינו דורש סיסמה</summary>
    private static object TempUnlockEnd(JsonElement _)
    {
        if (HardLock.Available)
        {
            HardLock.TempUnlockEnd();
            if (HardLock.ServiceActive && !IsActive) ForceActivate(null);
        }
        else EndTempUnlockLocal("הפתיחה הזמנית הסתיימה ידנית");
        try { TempTick(); } catch (Exception ex) { Config.Log("temp unlock: " + ex.Message); }
        return Status();
    }

    /// <summary>
    /// שדות הפתיחה הזמנית ב-/kiosk/settings (אחרי שהסיסמה הראשית ו-Hello כבר אומתו):
    /// tempUnlockEnabled, tempUnlockMinutes (1–60), tempUnlockPassword (ריק = ללא שינוי).
    /// הפעלה (מכבוי לדולק) נדחית כשהאימות הדו-שלבי כבוי; מתג שכבר דלוק נשמר כמו שהוא (רדום).
    /// </summary>
    private static void ApplyTempUnlockSettings(JsonElement b)
    {
        string secret = b.Str("tempUnlockPassword") ?? "";
        bool hasEn = b.Has("tempUnlockEnabled"), hasMin = b.Has("tempUnlockMinutes"), hasSecret = secret.Length > 0;
        if (!hasEn && !hasMin && !hasSecret) return;
        var cur = CurrentTemp();
        bool want = hasEn ? b.Bool("tempUnlockEnabled") : cur.Enabled;
        int minutes = hasMin ? TempUnlock.ClampMinutes(b.Long("tempUnlockMinutes", TempUnlock.DefaultMinutes)) : cur.Minutes;
        // הטופס שולח את כל השדות בכל שמירה; בלי שינוי לא נוגעים בכלום (גם לא ברכיב הקשחה ישן שאינו מכיר אותם)
        if (want == cur.Enabled && minutes == cur.Minutes && !hasSecret) return;
        if (cur.ServiceOutdated)
            throw new BridgeException("unsupported", "רכיב ההקשחה צריך עדכון כדי לתמוך בפתיחה זמנית (נעילה ← נעילה מוקשחת ← עדכון)", 503);
        if (hasSecret)
        {
            if (secret.Length < TempUnlock.MinSecretLength)
                throw BridgeException.BadRequest($"סיסמת הפתיחה הזמנית חייבת להכיל לפחות {TempUnlock.MinSecretLength} תווים");
            if (Matches(secret, "kiosk.passwordHash", "kiosk.salt") || IsRecoveryCode(secret))
                throw BridgeException.BadRequest("סיסמת הפתיחה הזמנית חייבת להיות שונה מסיסמת הנעילה ומקוד השחזור");
        }
        if (want && !cur.Enabled)
        {
            if (!TwoFactorOn)
                throw BridgeException.BadRequest("פתיחה זמנית דורשת אימות דו-שלבי (Windows Hello) פעיל. הפעילו קודם את האימות הדו-שלבי ונסו שוב.");
            if (!hasSecret && !cur.Configured) throw BridgeException.BadRequest("יש לבחור סיסמת פתיחה זמנית לפני ההפעלה");
        }
        string hash = null, salt = null;
        if (hasSecret)
        {
            byte[] sb = RandomNumberGenerator.GetBytes(16);
            salt = Convert.ToBase64String(sb);
            hash = Secret.Hash(secret, sb);
        }
        // כשרכיב ההקשחה מותקן נשמר קודם אצלו (מקור האמת); כישלון שם עוצר גם את השמירה המקומית
        if (HardLock.Available) HardLock.SetTempUnlock(b.Str("password"), want, minutes, hash, salt);
        Config.Set(KTuEnabled, want);
        Config.Set(KTuMinutes, minutes);
        if (hasSecret) { Config.Set(KTuHash, hash); Config.Set(KTuSalt, salt); }
    }

    /// <summary>האימות הדו-שלבי בפועל: של רכיב ההקשחה כשהוא מותקן (config.json כתיב למשתמש), אחרת של הגשר</summary>
    private static bool TwoFactorOn => HardLock.Available ? HardLock.ServiceTwoFactor : TwoFactor.Enabled;

    /// <summary>
    /// הגירה לרכיב ההקשחה (בהתקנה/עדכון שלו): אם עוד אין לו סיסמת פתיחה זמנית, מעבירים את הגיבוב מ-config.json.
    /// המתג עובר דלוק רק אם האימות הדו-שלבי כבר פעיל בשירות (אחרת השירות היה דוחה את ההפעלה).
    /// </summary>
    internal static void SyncTempUnlockToService(string password)
    {
        var t = HardLock.Temp;
        if (t == null || t.Configured || !TempUnlockConfiguredLocal) return;
        HardLock.SetTempUnlock(password, TempUnlockEnabledLocal && HardLock.ServiceTwoFactor, TempUnlockMinutesLocal,
            Config.Get<string>(KTuHash, null), Config.Get<string>(KTuSalt, null));
    }

    /// <summary>האם הטקסט שהוקלד הוא קוד השחזור (פטור מאימות דו-שלבי בביטול חירום) — internal כדי ש-FxPolicy ישתמש באותו כלל</summary>
    internal static bool IsRecoveryCode(string s)
    {
        string norm = Secret.NormalizeRecovery(s);
        // WHY (U1): כשהשירות מותקן העותק המקומי של ה-hash נמחק (או לא אמין: config.json כתיב למשתמש), והשירות הוא מקור האמת
        if (HardLock.Available)
        {
            bool? r = null;
            try { r = HardLock.VerifyRecoveryOnly(norm); } catch { }
            if (r != null) return r == true;
            return ServiceStopped() && LocalRecoveryMatches(norm); // שירות מופסק בפועל: מצב חירום מקומי (כמו VerifyEmergencyCode)
        }
        return Matches(norm, "kiosk.recoveryHash", "kiosk.recoverySalt");
    }

    /// <summary>רכיב ההקשחה אומר "נעול" (למשל אחרי עריכת config.json או בחשבון Windows אחר) — נועלים גם כאן</summary>
    public static void ForceActivate(string servicePath)
    {
        // WHY (T1): kiosk.otzariaPath חלק מקבוצת החתימה; עם שירות הנתיב ממילא נקרא מהשירות, ושינוי בלי חתימה מחדש היה שובר את החתימה
        if (!HardLock.Available && string.IsNullOrEmpty(OtzariaPath()) && !string.IsNullOrEmpty(servicePath) && File.Exists(servicePath))
            Config.Set("kiosk.otzariaPath", servicePath);
        string otz = OtzariaPath();
        if (string.IsNullOrEmpty(otz) || !File.Exists(otz))
        {
            // ביקורת #30 ממצא 4: לא נכשלים פתוח. הנעילה מוחלת גם בלי אוצריא; מסך המנוחה מוצג
            // עם הודעה, והיציאה היא בסיסמה או בקוד השחזור (Ctrl+Alt+Shift+F12) כרגיל.
            LogThrottled("noOtz", "hardlock: אוצריא לא נמצאה בחשבון הזה — הנעילה מוחלת בכל זאת", 300);
        }
        Config.Log("hardlock: הנעילה הוחלה לפי רכיב ההקשחה");
        Config.Set("kiosk.active", true);
        Config.Set("kiosk.confirmed", true);
        Config.Save();
        try { Apply(); } catch (Exception ex) { Config.Log("hardlock apply: " + ex.Message); }
    }

    /// <summary>
    /// הנעילה בוטלה ברכיב ההקשחה (למשל בקוד שחזור) — משחררים גם כאן. temporary: זו פתיחה זמנית,
    /// ולכן "נעילה קפדנית" נשמרת כדי שתחזור כמו שהייתה כשהשירות ינעל שוב בתום הזמן.
    /// </summary>
    public static void ReleaseFromService(bool temporary = false)
    {
        Config.Log(temporary ? "hardlock: פתיחה זמנית לפי רכיב ההקשחה" : "hardlock: הנעילה בוטלה ברכיב ההקשחה");
        Release(keepStrict: temporary);
    }

    private static object SetPassword(JsonElement b)
    {
        string newPassword = b.Str("newPassword") ?? "";
        if (newPassword.Length < MinPasswordLength) throw BridgeException.BadRequest($"הסיסמה חייבת להכיל לפחות {MinPasswordLength} תווים");
        // סיסמת הנעילה וסיסמת הפתיחה הזמנית חייבות להיות שונות, אחרת מי שקיבל את הזמנית היה יודע גם את הראשית
        if (Matches(newPassword, KTuHash, KTuSalt)) throw BridgeException.BadRequest("סיסמת הנעילה חייבת להיות שונה מסיסמת הפתיחה הזמנית");
        string recovery = null;
        bool svc = HardLock.Available;
        if (!svc && ServiceDown()) throw new BridgeException("unsupported", "רכיב ההקשחה לא מגיב; אי אפשר לשנות סיסמה כרגע", 503);
        if (HasPassword)
        {
            // שינוי סיסמה קיימת: אימות הישנה, וסנכרון לרכיב ההקשחה אם מותקן. קוד השחזור הקיים נשמר
            Verify(b.Str("oldPassword"), allowRecovery: true);
            if (svc) HardLock.SetPassword(b.Str("oldPassword"), newPassword);
        }
        else
        {
            // הגדרה ראשונה: יוצרים קוד שחזור חד-פעמי
            recovery = Secret.NewRecoveryCode();
            StoreSecret(recovery, "kiosk.recoveryHash", "kiosk.recoverySalt");
        }
        if (svc) PurgeLocalPassword(); // השירות מחזיק את ה-hash; לא שומרים עותק מקומי
        else StoreSecret(newPassword, "kiosk.passwordHash", "kiosk.salt");
        Config.Save();
        return recovery == null ? new { ok = true } : new { ok = true, recoveryCode = recovery };
    }

    private static object Enable(JsonElement b)
    {
        if (!HasPassword) throw BridgeException.BadRequest("יש להגדיר סיסמה לפני הפעלת הנעילה");
        Verify(b.Str("password"), allowRecovery: false);

        bool strict = b.Bool("strict", false); // כבוי כברירת מחדל
        // נעילה קשיחה (קפדנית או מוקשחת בשירות) בחשבון מנהל ניתנת לעקיפה מלאה: מסרבים, אלא אם המפעיל אישר במפורש
        if (UserIsAdministrator() && (strict || HardLock.Available || HardLock.ServiceInstalled()) && !b.Bool("acceptAdminRisk", false))
            throw new BridgeException("admin_account", "חשבון הלימוד הוא חשבון מנהל, ונעילה קשיחה בו ניתנת לעקיפה (עצירת הגשר/השירות, רישום, מדיניות). הפעילו את הנעילה בחשבון משתמש רגיל, או אשרו במפורש (acceptAdminRisk) על אחריותכם.", 403);
        bool blockTm = b.Bool("blockTaskManager", true);
        string otz = OtzariaPath();
        if (string.IsNullOrEmpty(otz) || !File.Exists(otz))
            throw BridgeException.BadRequest("לא נמצאה תוכנת אוצריא. יש להגדיר otzariaPath בהגדרות הנעילה");
        // WHY: אוצריא בתיקיה כתיבה למשתמש (למשל LocalAppData\Programs) ניתנת להחלפה בקובץ אחר, והנעילה היא שמפעילה אותה.
        // בנעילה קשיחה מסרבים, אלא אם המפעיל אישר במפורש (acceptWritableOtzaria) - ואז הנעיצה (SHA256) מגנה מפני החלפה
        if ((strict || HardLock.Available || HardLock.ServiceInstalled()) && !AppTrust.IsTrustedLocation(otz) && (!b.Bool("acceptWritableOtzaria", false) || HardLock.ServiceInstalled() && !HardLock.Available))   // [t1] עם שירות פעיל: אישור + נעיצה בשירות (בסיסמה); שירות מותקן שלא עונה: סירוב
            throw new BridgeException("otzaria_untrusted_location", "אוצריא מותקנת בתיקיה שמשתמש רגיל יכול לשנות (" + otz + "), ולכן אפשר להחליף אותה בתוכנה אחרת. התקינו אותה ב-Program Files, או אשרו במפורש (acceptWritableOtzaria) - הנעילה תנעץ את הקובץ הנוכחי ותסרב להפעיל קובץ ששונה.", 403);

        // הפעלה ידנית בסיסמה בזמן פתיחה זמנית מסיימת אותה (ברכיב ההקשחה — "enable" מנקה אותה שם)
        EndTempUnlockLocal("הנעילה הופעלה ידנית", relock: false);
        Config.Set("kiosk.strict", strict);
        Config.Set("kiosk.blockTaskManager", blockTm);
        Config.Set("kiosk.active", true);
        Config.Set("kiosk.activatedBy", "manual"); // נעילה ידנית לא תבוטל אוטומטית בסוף חלון התזמון
        PinOtzaria(otz); // נעיצת SHA256 של אוצריא בהפעלה (מאומתת לפני כל הפעלה במיקום כתיב)
        // [t1] עם שירות: הנעיצה נשמרת בשירות (בסיסמה), לא ב-config. כישלון = לא מפעילים נעילה שאוצריא בה לא תעלה
        if (HardLock.Available && !AppTrust.IsTrustedLocation(otz))
        {
            string osha = Sha256Of(otz) ?? throw new BridgeException("internal", "לא ניתן לחשב SHA-256 של אוצריא", 500);
            HardLock.SetOtzariaPath(b.Str("password"), otz, osha.ToLowerInvariant());
        }
        Config.Save();
        SealConfig(); // strict/blockTaskManager נחתמים (הסיסמה אומתה למעלה)
        try { Apply(); }
        catch
        {
            // הפעלה שנכשלה לא משאירה את המחשב במצב נעילה חלקי
            Release();
            throw;
        }
        Config.Set("kiosk.confirmed", true);
        Config.Save();
        if (HardLock.Available)
        {
            try { HardLock.Enable(b.Str("password"), otz); }
            catch (Exception ex) { Config.Log("hardlock enable: " + ex.Message); }
        }
        return new { active = true };
    }

    private static object Disable(JsonElement b)
    {
        string pw = b.Str("password");
        bool viaRecovery = IsRecoveryCode(pw);
        Verify(pw, allowRecovery: true);
        // קוד שחזור פטור מ-2FA (ביטול חירום); סיסמה רגילה דורשת גם Windows Hello אם מופעל
        if (!viaRecovery) TwoFactor.Require("ביטול נעילת המחשב");
        if (HardLock.Available) HardLock.Disable(pw);
        EndTempUnlockLocal("הנעילה בוטלה ידנית", relock: false); // ביטול מלא: הנעילה לא תחזור בתום הפתיחה הזמנית
        Release();
        return new { active = false };
    }

    private static object Settings(JsonElement b)
    {
        // לפני שהוגדרה סיסמת נעילה בכלל (לא כאן ולא ברכיב ההקשחה) אין עדיין מה להגן עליו - Enable()
        // ממילא דורש HasPassword בנפרד, אז דחיית שמירת העדפות (שומר מסך וכו') עד שתוגדר סיסמה הייתה
        // חוסמת סתם הגדרה ראשונית, בלי לתרום לאבטחה בפועל
        if (PasswordConfigured)
        {
            Verify(b.Str("password"), allowRecovery: false);
            // שינוי הגדרות דורש Windows Hello כשהאימות הדו-שלבי כבר פעיל (הפעלה ראשונה מטופלת למטה)
            TwoFactor.Require("שינוי הגדרות הנעילה");
        }
        if (b.Has("twoFactor"))
        {
            bool want = b.Bool("twoFactor");
            if (want && !TwoFactor.Enabled)
            {
                // הפעלה ראשונה: לוודא ש-Hello זמין ושהמנהל מצליח לעבור אותו עכשיו — אחרת נעילה-עצמית
                if (!TwoFactor.Available())
                    throw BridgeException.BadRequest("Windows Hello אינו מוגדר במחשב הזה. הגדירו זיהוי פנים/טביעת אצבע/PIN ב-Windows ונסו שוב.");
                if (!TwoFactor.Verify("אישור הפעלת אימות דו-שלבי"))
                    throw new BridgeException("two_factor", "האימות נכשל — האימות הדו-שלבי לא הופעל", 401);
            }
            // כשרכיב ההקשחה מותקן, חובה לסנכרן אליו גם: אחרת Hello נאכף רק ב-HTTP של הגשר, ולא מול
            // הצינור של השירות עצמו (ביקורת #30 ממצא 1) — עדכון שלא עובר לשירות משאיר את הפרצה פתוחה.
            if (HardLock.Available) HardLock.SetTwoFactor(b.Str("password"), want);
            Config.Set("kiosk.twoFactor", want);
        }
        // אחרי האימות הדו-שלבי: הפתיחה הזמנית תלויה במצבו החדש
        ApplyTempUnlockSettings(b);
        if (b.Has("readOnly")) Config.Set("kiosk.readOnly", b.Bool("readOnly"));
        foreach (string k in new[] { "clock", "restScreen", "fullscreenOtzaria", "sideBar", "sideBarIconsOnly", "resetOnShutdown", "allowExcelDocs", "showParsha", "showDafYomi" })
            if (b.Has(k)) Config.Set("kiosk." + k, b.Bool(k));
        if (b.Has("restIdleMinutes")) Config.Set("kiosk.restIdleMinutes", (int)Math.Clamp(b.Long("restIdleMinutes", 0), 0, 240));
        if (b.Has("zmanimLocation"))
        {
            string loc = (b.Str("zmanimLocation") ?? "").Trim();
            if (loc.Length > 0 && Zmanim.ParseLocation(loc) == null) throw BridgeException.BadRequest("מיקום לא תקין: יש להקליד קו רוחב, קו אורך (למשל 31.78, 35.22)");
            Config.Set("kiosk.zmanimLocation", loc);
        }
        // עיר מרשימה (אם נבחרה) מועברת כפי שהיא ל-calendar.getDailyTimes באוצריא; הקואורדינטות
        // למעלה נשארות כאפשרות מתקדמת, וגם משמשות גיבוי כש-kiosk.zmanimCity ריק
        if (b.Has("zmanimCity")) Config.Set("kiosk.zmanimCity", (b.Str("zmanimCity") ?? "").Trim());
        // תיקון גובה לגיבוי (issue #33): מטרים מעל פני הים, רק למנוע kosherjava (הספרייה דורשת ≥ 0)
        if (b.Has("zmanimElevation")) Config.Set("kiosk.zmanimElevation", Math.Clamp(b.Num("zmanimElevation") ?? 0, 0, 5000));
        // מנוע חישוב הגיבוי: "noaa" (ברירת מחדל, ללא שינוי למתקינים קיימים) או "kosherjava" (חדש)
        if (b.Has("zmanimFallbackEngine"))
        {
            string eng = (b.Str("zmanimFallbackEngine") ?? "noaa").Trim().ToLowerInvariant();
            if (eng != "kosherjava") eng = "noaa";
            Config.Set("kiosk.zmanimFallbackEngine", eng);
        }
        if (b.Has("zmanimDiaspora")) Config.Set("kiosk.zmanimDiaspora", b.Bool("zmanimDiaspora"));
        if (b.Has("zmanimShow"))
        {
            // לא עושים ToLowerInvariant: המזהים (למשל "shemaGra", "minchaGedola") הם camelCase מצד
            // הלקוח, וקריאתם חזרה שם משווה באותיות-רישיות מדויקות (indexOf). הקטנת האותיות כאן שינתה
            // את הערך השמור כך שלא תאם יותר לשדות שהמשתמש סימן, והם נראו "חוזרים לברירת המחדל" אחרי
            // כל שמירה. הסינון בפועל (ZmanimLine) כבר משווה case-insensitive, אז אין צורך בנרמול כאן
            var ids = b.StrArray("zmanimShow").Select(x => x.Trim()).Where(x => x.Length > 0).Distinct().Take(20).ToArray();
            Config.Set("kiosk.zmanimShow", new JsonArray(ids.Select(x => (JsonNode)x).ToArray()));
        }
        if (b.Has("candleLightingMinutes")) Config.Set("kiosk.candleLightingMinutes", (int)Math.Clamp(b.Long("candleLightingMinutes", 18), 0, 90));
        if (b.Has("yahrzeitMonth"))
        {
            string mon = (b.Str("yahrzeitMonth") ?? "").Trim();
            if (mon.Length > 0 && !Zmanim.YahrzeitMonths.Contains(mon)) throw BridgeException.BadRequest("חודש עברי לא תקין ליום השנה");
            Config.Set("kiosk.yahrzeitMonth", mon);
        }
        if (b.Has("yahrzeitDay")) Config.Set("kiosk.yahrzeitDay", (int)Math.Clamp(b.Long("yahrzeitDay", 0), 0, 30));
        if (b.Has("yahrzeitLabel"))
        {
            string lbl = (b.Str("yahrzeitLabel") ?? "").Trim();
            if (lbl.Length > 200) lbl = lbl.Substring(0, 200);
            Config.Set("kiosk.yahrzeitLabel", lbl);
        }
        if (b.Has("venueName"))
        {
            string vn = (b.Str("venueName") ?? "").Trim();
            if (vn.Length > 60) vn = vn.Substring(0, 60);
            Config.Set("kiosk.venueName", vn);
        }
        if (b.Has("dedicationText"))
        {
            string ded = (b.Str("dedicationText") ?? "").Trim();
            if (ded.Length > 120) ded = ded.Substring(0, 120);
            Config.Set("kiosk.dedicationText", ded);
        }
        string cp = b.Str("clockPosition");
        if (cp is "bottom-left" or "bottom-right" or "top-left" or "top-right") Config.Set("kiosk.clockPosition", cp);
        string rzs = b.Str("restZmanimSide");
        if (rzs is "left" or "right") Config.Set("kiosk.restZmanimSide", rzs);
        string sbp = b.Str("sideBarPosition");
        if (sbp is "left" or "right" or "top" or "bottom") Config.Set("kiosk.sideBarPosition", sbp);
        if (b.Has("openSections"))
            Config.Set("kiosk.openSections", new JsonArray(b.StrArray("openSections")
                .Where(x => AllSections.Contains(x, StringComparer.OrdinalIgnoreCase))
                .Select(x => (JsonNode)x.ToLowerInvariant()).ToArray()));
        if (b.Has("allowOpenExtensions"))
            Config.Set("kiosk.allowOpenExtensions",
                new JsonArray(b.StrArray("allowOpenExtensions").Select(x => (JsonNode)x.TrimStart('.').ToLowerInvariant()).ToArray()));
        if (b.Has("otzariaPath"))
        {
            string p = b.Str("otzariaPath", "");
            if (p.Length > 0 && !File.Exists(p)) throw BridgeException.NotFound("הקובץ לא נמצא: " + p);
            // כשרכיב ההקשחה מותקן הנתיב נשמר קודם אצלו (מקור האמת); כישלון שם עוצר גם את השמירה המקומית
            if (HardLock.Available)
            {
                // [t1] נעיצת הקובץ הנוכחי בשירות (בסיסמה); מיקום מהימן לא צריך נעיצה
                string psha = p.Length > 0 && !AppTrust.IsTrustedLocation(p) ? Sha256Of(p)?.ToLowerInvariant() : "";
                HardLock.SetOtzariaPath(b.Str("password"), p, psha ?? "");
            }
            Config.Set("kiosk.otzariaPath", p);
        }
        bool schedChanged = false;
        if (b.TryGetProperty("schedule", out var sch) && sch.ValueKind == JsonValueKind.Object)
        {
            if (sch.Has("enabled")) Config.Set("kiosk.schedule.enabled", sch.Bool("enabled"));
            if (sch.Has("warn")) Config.Set("kiosk.schedule.warn", sch.Bool("warn"));
            string ss = sch.Str("start");
            if (ss != null)
            {
                if (!Sched.IsValid(ss)) throw BridgeException.BadRequest("שעת התחלה אינה תקינה (HH:MM)");
                Config.Set("kiosk.schedule.start", ss);
            }
            string se = sch.Str("end");
            if (se != null)
            {
                if (!Sched.IsValid(se)) throw BridgeException.BadRequest("שעת סיום אינה תקינה (HH:MM)");
                Config.Set("kiosk.schedule.end", se);
            }
            schedChanged = true;
        }
        Config.Save();
        SealConfig(); // WHY (T1): שמירת הגדרות לגיטימית (אחרי אימות סיסמה) => חתימה מחדש של מפתחות האבטחה
        if (IsActive) { Apply(); Overlay.ShowSideBar(); }
        if (schedChanged)
        {
            // כשרכיב ההקשחה מותקן הוא המתזמן — מעבירים אליו את החלון; אחרת הגשר מתזמן מיד
            if (HardLock.Available)
                try { HardLock.SetSchedule(b.Str("password"), ScheduleEnabled, ScheduleStart, ScheduleEnd); }
                catch (Exception ex) { Config.Log("hardlock schedule: " + ex.Message); }
            else
                try { ScheduleTick(); } catch (Exception ex) { Config.Log("schedule: " + ex.Message); }
        }
        // פתיחה זמנית שרצה והפכה לרדומה (האימות הדו-שלבי או הפתיחה כובו) מסתיימת מיד
        try { TempTick(); } catch (Exception ex) { Config.Log("temp unlock: " + ex.Message); }
        return Status();
    }

    // ---------- איתור אוצריא ----------

    public static string OtzariaPath()
    {
        // כשרכיב ההקשחה מותקן ויש לו נתיב — הוא מקור האמת, ו-config.json (כתיב למשתמש המחובר) לא נקרא.
        // נתיב ריק אצלו = זיהוי אוטומטי; נתיב שלא קיים בחשבון הזה = זיהוי אוטומטי (ולא ערך מ-config.json)
        string servicePath = HardLock.Policy?.OtzariaPath;
        string configured = servicePath;
        if (configured == null)
        {
            // [s2] WHY: כשהשירות קיים, נתיב מ-config.json (כתיב למשתמש) לא נקרא - אחרת משתמש מצביע את "אוצריא" על exe משלו.
            // חריג להגירה: נתיב מה-config שנמצא במיקום מנהל-בלבד (Program Files וכד') לא ניתן לזיוף ולכן מתקבל
            string cfg = Config.Get<string>("kiosk.otzariaPath", null);
            if (!HardLock.Available || (!string.IsNullOrEmpty(cfg) && AppTrust.IsTrustedLocation(cfg))) configured = cfg;
        }
        if (!string.IsNullOrEmpty(configured) && File.Exists(configured)) return configured;

        // WHY: אין נפילה לנתיב של תהליך "otzaria" שרץ - כל תהליך בשם הזה (גם עותק זדוני בתיקיה כתיבה) היה הופך ל"אוצריא" המהימנה
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string[] candidates =
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "otzaria", "otzaria.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "otzaria", "otzaria.exe"),
            Path.Combine(local, "Programs", "otzaria", "otzaria.exe"), // כתיב למשתמש: אחרון, ומאומת מול נעיצה (ר' CheckOtzariaForLaunch)
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    // ---------- זהות אוצריא: נתיב + נעיצת SHA256, לא שם תהליך ----------

    internal static bool SamePath(string a, string b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
        try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    /// <summary>
    /// החלטה טהורה (לבדיקות): האם מותר להפעיל את קובץ אוצריא.
    /// מיקום מהימן (לא כתיב למשתמש) - תמיד. מיקום כתיב: רק אם יש נעיצה והיא תואמת; בלי נעיצה (נעילה ישנה) - מותר פעם אחת
    /// ונעיצה נרשמת עכשיו (TOFU), כדי לא לנעול החוצה התקנה קיימת.
    /// </summary>
    internal enum LaunchVerdict { Allow, AllowAndPin, Refuse }
    internal static LaunchVerdict DecideLaunch(bool trustedLocation, string pinned, string actual, bool serviceControlled = false)
    {
        if (trustedLocation) return LaunchVerdict.Allow;
        if (string.IsNullOrEmpty(actual)) return LaunchVerdict.Refuse;
        // [s2] WHY: עם שירות - בלי TOFU (המשתמש יכול לכתוב את הנעיצה ל-config ולהחליף את הקובץ) ובלי נעיצה מ-config. רק נעיצה שהשירות מחזיק
        if (string.IsNullOrEmpty(pinned)) return serviceControlled ? LaunchVerdict.Refuse : LaunchVerdict.AllowAndPin;
        return string.Equals(pinned, actual, StringComparison.OrdinalIgnoreCase) ? LaunchVerdict.Allow : LaunchVerdict.Refuse;
    }

    private static string Sha256Of(string path)
    {
        try { using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); return Convert.ToHexString(SHA256.HashData(fs)); }
        catch { return null; }
    }

    /// <summary>נעיצת ה-SHA256 של אוצריא (בהפעלת הנעילה). נשמרת ב-config.json רק כשכבה נוספת: שמירה מוגנת אמיתית דורשת תמיכה בשירות (הצעה בדוח)</summary>
    /// <summary>
    /// [s2] נקודת חיבור לשירות: SHA-256 של אוצריא שהשירות מחזיק (מקור אמת, לא כתיב למשתמש). כרגע אין API בשירות שמחזיר/שומר אותו,
    /// ולכן null = עם שירות, אוצריא במיקום כתיב לא תופעל. חיבור (הצעה): HardLock.Policy.OtzariaSha256 + שמירה ב-SetOtzariaPath בסיסמה.
    /// </summary>
    internal static Func<string, string> ServiceOtzariaSha = path => ServicePinned(HardLock.Policy, path);

    /// <summary>
    /// הנעיצה שהשירות מחזיק, רק אם היא שייכת לאותו נתיב (נתיב אחר = נעיצה של קובץ אחר = אין נעיצה). ריק/חסר = null.
    /// WHY (T1): נעיצה נשמרת בשירות בסיסמה (SetOtzariaPath); בלי TOFU בצד הגשר
    /// </summary>
    internal static string ServicePinned(HardLock.PolicySnapshot pol, string path)
    {
        if (pol == null || string.IsNullOrEmpty(pol.OtzariaSha256) || !SamePath(pol.OtzariaPath, path)) return null;
        return pol.OtzariaSha256;
    }

    private static void PinOtzaria(string path)
    {
        if (HardLock.Available) return;   // [s2] עם שירות לא כותבים נעיצה ל-config כתיב (לא מאובטחת)
        string sha = Sha256Of(path);
        if (sha == null) return;
        Config.Set("kiosk.otzariaSha", sha);
        Config.Set("kiosk.otzariaShaPath", Path.GetFullPath(path));
    }

    /// <summary>
    /// לפני כל Process.Start של אוצריא: אין הפעלה של קובץ שלא אומת. [u2] WHY (TOCTOU): בעבר SHA חושב ואז Process.Start בלי נעילה - אפשר היה להחליף את הקובץ בין השניים.
    /// עכשיו הקובץ נפתח ב-FileShare.Read (AppTrust.LaunchLock), ה-SHA מה-handle, וה-handle מוחזק עד אחרי Process.Start.
    /// start=null: בדיקה בלבד (בלי הפעלה). מחזיר false אם לא אומת / לא הופעל
    /// </summary>
    private static bool CheckOtzariaForLaunch(string path, Action start = null)
    {
        AppTrust.LaunchLock fl = null;
        try
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;
            bool trusted = AppTrust.IsTrustedLocation(path);
            bool svc = HardLock.Available;
            string pinned = PinnedOtzariaSha(path, svc);
            string actual = null;
            if (!trusted)
            {
                fl = AppTrust.LaunchLock.Open(path);
                if (fl == null) return false;   // לא ניתן לנעול = fail-closed
                actual = fl.Sha256;
            }
            var v = DecideLaunch(trusted, pinned, actual, svc);
            if (v == LaunchVerdict.AllowAndPin) { PinOtzaria(path); try { Config.Save(); } catch { } }
            if (v == LaunchVerdict.Refuse)
                LogThrottled("otzUntrusted", "watchdog: קובץ אוצריא במיקום כתיב למשתמש ואינו תואם את הנעיצה - לא מופעל (הפעלת הנעילה מחדש בסיסמה תנעץ את הגרסה הנוכחית)", 120);
            if (v == LaunchVerdict.Refuse) return false;
            start?.Invoke();   // תחת הנעילה: הקובץ לא יוחלף בין האימות להפעלה
            return true;
        }
        catch { return false; }
        finally { fl?.Dispose(); }
    }

    private static string PinnedOtzariaSha(string path, bool svc) =>
        svc ? ServiceOtzariaSha(path)
            : SamePath(Config.Get<string>("kiosk.otzariaShaPath", null), path) ? Config.Get<string>("kiosk.otzariaSha", null) : null;

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (long mtime, long len, string sha)> _otzShaCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// [u2] WHY: זהות אוצריא של תהליך *רץ* לפי נתיב בלבד פטרה כל exe שהוצב בנתיב המוגדר במיקום כתיב (הוחלף אחרי ההפעלה/ננעץ אחר).
    /// הנתיב חייב להיות גם מאומת: מיקום מהימן, או SHA תואם לנעיצה (תמונה רצה לא ניתנת לכתיבה ולכן SHA של הקובץ הוא של התהליך).
    /// SHA מוטמן לפי (mtime,גודל) כדי לא לחשב כל סריקה. נעיצה חסרה בלי שירות = כמו בהפעלה (TOFU), לא נחסם
    /// </summary>
    internal static bool OtzariaImageVerified(string path)
    {
        try
        {
            if (string.IsNullOrEmpty(path)) return false;
            bool trusted = AppTrust.IsTrustedLocation(path);
            if (trusted) return true;
            var fi = new FileInfo(path);
            if (!fi.Exists) return false;
            long mt = fi.LastWriteTimeUtc.Ticks, len = fi.Length;
            string sha;
            if (_otzShaCache.TryGetValue(path, out var c) && c.mtime == mt && c.len == len) sha = c.sha;
            else { sha = Sha256Of(path); _otzShaCache[path] = (mt, len, sha); }
            bool svc = HardLock.Available;
            return DecideLaunch(false, PinnedOtzariaSha(path, svc), sha, svc) != LaunchVerdict.Refuse;
        }
        catch { return false; }
    }

    /// <summary>תהליך אוצריא בסשן הזה לפי נתיב התמונה (QueryFullProcessImageName) ולא לפי שם בלבד</summary>
    internal static Process FindOtzariaProcess(int session)
    {
        string path = OtzariaPath();
        if (string.IsNullOrEmpty(path)) return null;
        foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(path)))
        {
            try { if (p.SessionId == session && SamePath(ImagePath((uint)p.Id), path)) return p; } catch { }
        }
        return null;
    }

    // ---------- החלה ושחרור ----------

    /// <summary>
    /// הגנת עלייה (בעיה #8, "מסך שחור אחרי הפעלה מחדש"): רצה בתחילת כל הפעלה של הגשר, לפני כל דבר אחר.
    /// אם אין נעילה פעילה ומאושרת, או שהמחשב עלה במצב בטוח — מוחקים כל שארית של הנעילה ברישום
    /// (מעטפת Shell שמפנה לגשר, חסימת מנהל המשימות) ומוודאים ש-explorer רץ, כדי ששולחן העבודה תמיד יעלה.
    /// </summary>
    public static void BootGuard()
    {
        try
        {
            bool safeMode = SystemInformation.BootMode != BootMode.Normal;
            bool locked = ((IsActive && Config.Get("kiosk.confirmed", false)) || HardLock.ServiceActive) && !safeMode;
            if (safeMode && IsActive)
            {
                Config.Log("kiosk: מצב בטוח — הנעילה מבוטלת לעלייה הזו");
                Config.Set("kiosk.active", false);
                Config.Set("kiosk.confirmed", false);
                Config.Save();
            }
            if (locked) return;
            SetTaskbars(true); // אם התהליך הקודם קרס כשהסתיר את שורת המשימות - מחזירים אותה
            bool hadShell = ShellPointsToBridge();
            ClearRegistry();
            if (hadShell) Config.Log("kiosk: נמצאה מעטפת Shell שמפנה לגשר בלי נעילה פעילה — נמחקה, ו-explorer מופעל");
            // כשהגשר עצמו הופעל כמעטפת אין explorer כלל; בעלייה רגילה (Run) explorer כבר רץ ולא נוגעים בו
            if (hadShell || Process.GetProcessesByName("explorer").Length == 0) StartExplorer();
        }
        catch (Exception ex) { Config.Log("boot guard: " + ex.Message); StartExplorer(); }
    }

    private static bool ShellPointsToBridge()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(WinlogonKey);
            string v = k?.GetValue("Shell") as string;
            return !string.IsNullOrEmpty(v) && v.IndexOf("OtzariaBridge", StringComparison.OrdinalIgnoreCase) >= 0;
        }
        catch { return false; }
    }

    public static void RestoreOnStartup()
    {
        if (!IsActive && HardLock.ServiceActive) { ForceActivate(null); return; }
        if (!IsActive) return;
        // נעילה חוזרת בעלייה רק אם הופעלה בהצלחה. מצב שנשמר מהפעלה שנכשלה (גרסאות 0.0.x שמרו
        // "פעיל" לפני ההחלה) מנוקה, כדי שהפעלת הגשר לא תנעל את המחשב בהפתעה.
        if (!Config.Get("kiosk.confirmed", false))
        {
            Config.Log("kiosk: מצב נעילה לא מאושר מהפעלה קודמת — מנוקה בעלייה");
            Release();
            return;
        }
        try { Apply(); }
        catch (Exception ex) { Config.Log("kiosk restore: " + ex.Message); Release(); }
    }

    private static bool _taskMgrByPolicy;
    private static bool _sessionHooked;

    /// <summary>
    /// אפשרות "ביטול נעילה בכיבוי": כשהמחשב מכבה או המשתמש מתנתק, הנעילה מתבטלת, ובהפעלה הבאה המחשב רגיל.
    /// לא פועל עם נעילה מוקשחת (שם השירות הוא מקור האמת), ולא בסגירה של הגשר עצמו או בעדכון.
    /// </summary>
    private static void HookSessionEnding()
    {
        if (_sessionHooked) return;
        _sessionHooked = true;
        Microsoft.Win32.SystemEvents.SessionEnding += (_, _) =>
        {
            try
            {
                // פתיחה זמנית מקומית שממתינה נחשבת כנעילה: עם "ביטול נעילה בכיבוי" היא לא תחזור אחרי ההפעלה מחדש
                bool tempPending = TempUnlockPendingLocal;
                if ((!IsActive && !tempPending) || !Config.Get("kiosk.resetOnShutdown", false) || HardLock.ServiceActive) return;
                if (tempPending) { lock (TuGate) _tuUntil = null; Config.Set(KTuUntil, ""); }
                Config.Set("kiosk.active", false);
                Config.Set("kiosk.confirmed", false);
                Config.Set("kiosk.strict", false);
                Config.Save();
                ClearRegistry();
                Config.Log("kiosk: הנעילה בוטלה בכיבוי (הוגדר: ביטול נעילה בכיבוי)");
            }
            catch { }
        };
    }

    private static void Apply()
    {
        lock (Gate)
        {
            // חסימת מנהל המשימות דרך מדיניות. במחשבים רבים המפתח Policies\System נעול למשתמש רגיל
            // (נוצר על ידי מנהל/מדיניות קבוצתית), ולכן זו חסימה "במאמץ הטוב": אם אין הרשאה,
            // הנעילה ממשיכה, ומנהל המשימות נסגר על ידי שומר הנעילה ונחסם במקלדת (Ctrl+Shift+Esc).
            // הנעילה כאן כבר הוחלטה (Enable סירב מראש כשנדרש; ForceActivate/שחזור בעלייה חייבים לנעול - כשל סגור),
            // לכן כאן רק חסימה ברורה למשתמש ולא ביטול
            if (UserIsAdministrator())
            {
                Config.Log("kiosk: אזהרה — הנעילה הופעלה בחשבון עם הרשאות מנהל, שניתן לעקוף אותה ממנו. מומלץ להפעיל בעמדת לימוד בחשבון משתמש רגיל.");
                Tray.Notify(Program.AppName, Lang.T("אזהרה: חשבון זה הוא חשבון מנהל - ניתן לעקוף את הנעילה. מומלץ חשבון משתמש רגיל.", "Warning: this is an administrator account - the lock can be bypassed. A standard account is recommended."));
            }

            _taskMgrByPolicy = false;
            try
            {
                using var k = Registry.CurrentUser.CreateSubKey(PoliciesKey);
                if (SecBool("kiosk.blockTaskManager", true, true)) { k.SetValue("DisableTaskMgr", 1, RegistryValueKind.DWord); _taskMgrByPolicy = true; }
                else k.DeleteValue("DisableTaskMgr", false);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
            {
                Config.Log("kiosk: Policies\\System לא נגיש, חסימת מנהל המשימות בשומר בלבד: " + ex.Message);
            }

            try
            {
                using var k = Registry.CurrentUser.CreateSubKey(WinlogonKey);
                // נעילה קפדנית: המעטפת היא הגשר, שמפעיל את אוצריא. ללא strict המעטפת לא משתנה
                if (SecBool("kiosk.strict", false, true))
                    k.SetValue("Shell", "\"" + Installer.InstalledExe + "\" --background", RegistryValueKind.String);
                else k.DeleteValue("Shell", false);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
            {
                Config.Log("kiosk: Winlogon לא נגיש: " + ex.Message);
                if (SecBool("kiosk.strict", false, true))
                    throw BridgeException.Forbidden("נעילה קפדנית דורשת הרשאה לשנות את מעטפת Windows, ואין הרשאה כזו במחשב הזה. אפשר להפעיל נעילה רגילה.");
            }

            if (!InstallHook()) Config.Log("kiosk: התקנת hook המקלדת נכשלה, ה-watchdog ינסה שוב כל ~3 שניות");
            StartShellGuard();
            SetToastPolicy(true);
            HookSessionEnding();
            Overlay.ShowClock(); // שורת המשימות מוסתרת — שעה ותאריך בשעון צף
            Overlay.ShowSideBar(); // סרגל תוכנות בצד המסך: מעבר מהיר בין אוצריא לתוכנות התורניות
            _watchdog ??= new System.Threading.Timer(_ => Watch(), null, 0, 2000);
            StartFastGuard();
            Tray.SetKioskState(true);
        }
    }

    /// <summary>שחרור הנעילה. keepStrict: בפתיחה זמנית "נעילה קפדנית" נשמרת, כדי שתחזור כמו שהייתה בתום הזמן</summary>
    private static void Release(bool keepStrict = false)
    {
        lock (Gate)
        {
            Config.Set("kiosk.active", false);
            Config.Set("kiosk.confirmed", false);
            if (!keepStrict) Config.Set("kiosk.strict", false);
            // כשל בשמירה (נעילת קובץ של אנטי-וירוס, דיסק מלא) לא יעצור את שאר השחרור - אחרת נשאר מצב חצי-משוחרר
            try { Config.Save(); } catch (Exception ex) { Config.Log("release save: " + ex.Message); }
            // WHY (T1): kiosk.strict שונה, והוא חלק מקבוצת החתימה; בלי חתימה מחדש הקבוצה נחשבת פגומה (ברירות מחדל מחמירות).
            // ברקע: קריאת pipe לא תעכב/תתקע שחרור (מסלול חירום)
            if (!keepStrict) Task.Run(SealConfig);
            _watchdog?.Dispose();
            _watchdog = null;
            StopFastGuard();
            // ממתינים שסבב שומר שכבר רץ יסתיים, כדי שלא יסגור את explorer מיד אחרי שהופעל מחדש
            for (int i = 0; i < 30 && Volatile.Read(ref _watchBusy) == 1; i++) Thread.Sleep(100);
            RemoveHook();
            StopShellGuard(keepBaseline: keepStrict);   // [u2] פתיחה זמנית: baseline נשמר
            SetToastPolicy(false);
            Overlay.HideAll();
            RestoreWindowFrame(); UnblockClose();
            ClearRegistry();
            StartExplorer();
            Tray.SetKioskState(false);
            // בדיקה שנייה אחרי כמה שניות: אם explorer עדיין לא עלה, מפעילים שוב
            System.Threading.Tasks.Task.Delay(4000).ContinueWith(_ => { if (!IsActive) StartExplorer(); });
        }
    }

    private static void ClearRegistry()
    {
        // כל מפתח בנפרד: כישלון במדיניות (מפתח נעול) לא ימנע את מחיקת המעטפת, שהיא הקריטית
        try { using var k = Registry.CurrentUser.OpenSubKey(WinlogonKey, true); k?.DeleteValue("Shell", false); }
        catch (Exception ex) { Config.Log("kiosk clear shell: " + ex.Message); }
        try { using var k = Registry.CurrentUser.OpenSubKey(PoliciesKey, true); k?.DeleteValue("DisableTaskMgr", false); }
        catch (Exception ex) { Config.Log("kiosk clear policy: " + ex.Message); }
        SetToastPolicy(false);
        if (ShellPointsToBridge()) Config.Log("kiosk clear: אזהרה — המעטפת עדיין מפנה לגשר");
    }

    private static void StartExplorer()
    {
        try
        {
            if (Process.GetProcessesByName("explorer").Length == 0)
                Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"))
                    { UseShellExecute = true });
        }
        catch { }
    }

    // ---------- חירום ----------

    private static string EmergencyMarkerPath => Path.Combine(Config.Dir, "emergency-release.req");
    private static DateTime _markerBlockedUntil = DateTime.MinValue;
    private static int _markerFails;

    /// <summary>השירות מותקן והוא באמת מופסק לפי ה-SCM (לא סתם pipe שותק). בלי שירות מותקן: לא רלוונטי (false)</summary>
    internal static bool ServiceStopped()
    {
        try
        {
            if (!HardLock.ServiceInstalled()) return false;
            using var sc = new System.ServiceProcess.ServiceController(HardLock.ServiceName);
            return sc.Status == System.ServiceProcess.ServiceControllerStatus.Stopped;
        }
        catch { return false; } // fail-closed
    }

    /// <summary>
    /// אימות קוד שחזור לחירום. השירות הוא מקור האמת כשהוא מותקן; ה-hash המקומי (config.json, כתיב למשתמש) נחשב
    /// רק כשאין שירות מותקן, או כשהשירות מופסק באמת ב-SCM. pipe שותק בשירות שרץ: ניסיונות חוזרים ואז סירוב
    /// (מי שנתקע: עצירת השירות כמנהל, sc stop, ואז --unlock מקומי).
    /// </summary>
    internal static bool VerifyEmergencyCode(string code)
    {
        string norm = Secret.NormalizeRecovery(code);
        bool LocalOk() => LocalRecoveryMatches(norm);
        if (!HardLock.ServiceInstalled()) return LocalOk();
        for (int i = 0; ; i++)
        {
            bool ok = false;
            try { ok = HardLock.EmergencyDisable(code); } catch (Exception ex) { Config.Log("emergency: " + ex.Message); }
            if (ok) return true;
            if (ServiceStopped()) return LocalOk();
            bool responding = false;
            try { dynamic st = HardLock.Status(true); responding = st.running == true; } catch { }
            if (responding || i >= 2) return false; // השירות ענה והקוד שגוי / לא עונה גם אחרי ניסיונות: נשארים נעולים
            Thread.Sleep(1000);
        }
    }

    /// <summary>
    /// ה-hash/salt של קוד השחזור לשימוש מקומי (מצב רך, או שירות מופסק): config.json, ובהיעדרו (נמחק כשהשירות החזיק אותו) הקובץ של השירות ב-ProgramData,
    /// שנקרא רק כשהתהליך מורם (משתמש רגיל לא יכול לקרוא אותו; ומי שעצר את השירות הוא מנהל). לא קורא רשת/pipe.
    /// </summary>
    private static (string hash, string salt) LocalRecoveryStored()
    {
        string h = Config.Get<string>("kiosk.recoveryHash", null), s = Config.Get<string>("kiosk.recoverySalt", null);
        if (!string.IsNullOrEmpty(h) && !string.IsNullOrEmpty(s)) return (h, s);
        try
        {
            var st = StateStore.Read(Path.Combine(HardLock.DataDir, "hardlock.json"), out _, out _);
            return (st?["recoveryHash"]?.ToString(), st?["recoverySalt"]?.ToString());
        }
        catch { return (null, null); }
    }

    private static bool LocalRecoveryMatches(string norm)
    {
        var (h, s) = LocalRecoveryStored();
        return Secret.Matches(norm, h, s);
    }

    /// <summary>
    /// קוד שחזור משורת הפקודה: OtzariaBridge.exe --unlock CODE.
    /// WHY: גשר מוקשח (DACL) לא ניתן להריגה ע"י תהליך המשתמש, וגשר שנשאר רץ מחזיק kiosk.active בזיכרון ונשאר נעול.
    /// לכן מאותתים לו בקובץ סימון (עם הקוד, שהגשר מאמת בעצמו מול השירות/המקומי) והוא משחרר ויוצא; Kill רק כגיבוי לגשרים ישנים.
    /// </summary>
    public static bool EmergencyUnlock(string code)
    {
        if (!VerifyEmergencyCode(code)) return false;
        int self = Environment.ProcessId;
        var others = Process.GetProcessesByName("OtzariaBridge").Where(p => p.Id != self).ToList();
        if (others.Count > 0)
        {
            string marker = EmergencyMarkerPath;
            // WHY (U1): הקובץ בתיקיית המשתמש, ולכן אסור שיכיל משהו שאפשר לזייף מה-hash השמור (הוכחה = hash: pass-the-hash). כשהשירות מותקן ועונה:
            // טוקן חד-פעמי שהשירות הנפיק אחרי אימות הקוד בפייפ (challenge-response). הוכחה מבוססת hash נכתבת רק במצב רך (בלי שירות) או כשהשירות מופסק בפועל,
            // ושם ממילא אין סוד מול מי שיכול לערוך config.json. בלי שניהם: בלי סימון, נשאר Kill כגיבוי
            string content = null;
            if (HardLock.ServiceInstalled())
            {
                string tok = null;
                try { tok = HardLock.RecoveryTokenMint(code); } catch (Exception ex) { Config.Log("emergency token: " + ex.Message); }
                if (tok != null) content = TokenMarkerPrefix + tok;
            }
            if (content == null && (!HardLock.ServiceInstalled() || ServiceStopped()))
            {
                string[] proofs = Secret.RecoveryProofs(Secret.NormalizeRecovery(code), LocalRecoveryStored().salt);
                if (proofs != null) content = MarkerPrefix + string.Join("\n", proofs);
            }
            if (content != null)
            {
                try { Directory.CreateDirectory(Config.Dir); File.WriteAllText(marker, content); } catch (Exception ex) { Config.Log("emergency marker: " + ex.Message); }
                var end = DateTime.UtcNow.AddSeconds(8);
                while (DateTime.UtcNow < end && File.Exists(marker)) Thread.Sleep(200);
                try { File.Delete(marker); } catch { }
            }
            foreach (var p in others)
            {
                try { if (!p.WaitForExit(3000)) { p.Kill(); p.WaitForExit(3000); } } catch { }
            }
        }
        Config.Reload();
        Config.Set("kiosk.active", false);
        Config.Set("kiosk.strict", false);
        Config.Set(KTuUntil, ""); // ביטול חירום: גם פתיחה זמנית ממתינה לא תחזיר את הנעילה
        Config.Save();
        ClearRegistry();
        StartExplorer();
        return true;
    }

    /// <summary>נקרא מהשומר: קובץ סימון חירום (מ---unlock) עם קוד שהגשר מאמת בעצמו; קוד שגוי לא עושה כלום (5 כשלונות = דקת השהיה)</summary>
    private static void PollEmergencyMarker()
    {
        try
        {
            string f = EmergencyMarkerPath;
            if (!File.Exists(f)) return;
            string text;
            try { text = File.ReadAllText(f).Trim(); File.Delete(f); } catch { return; } // לא נמחק = לא מעבדים (בלי לולאה)
            if (DateTime.UtcNow < _markerBlockedUntil) return;
            string token = ParseTokenMarker(text);
            string[] proofs = token == null ? ParseMarker(text) : null;
            if (token == null && proofs == null) return; // קוד גלוי בקובץ כבר לא מתקבל (הכותב החדש כותב טוקן/הוכחה בלבד)
            if (token != null ? EmergencyReleaseToken(token) : EmergencyReleaseProof(proofs))
            {
                Config.Log("kiosk: שחרור חירום מ---unlock (קובץ סימון מאומת) - הגשר יוצא");
                Tray.ExitNow();
            }
            else if (++_markerFails >= 5) { _markerFails = 0; _markerBlockedUntil = DateTime.UtcNow.AddSeconds(60); }
        }
        catch (Exception ex) { Config.Log("emergency marker poll: " + ex.Message); }
    }

    internal const string MarkerPrefix = "proof-v1\n";
    internal const string TokenMarkerPrefix = "token-v1\n";

    /// <summary>טוקן חד-פעמי מקובץ סימון (token-v1); null אם הפורמט אחר/לא תקין</summary>
    internal static string ParseTokenMarker(string text)
    {
        const string head = "token-v1";
        if (text == null || !text.StartsWith(head, StringComparison.Ordinal)) return null;
        var lines = text.Substring(head.Length).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length == 1 && lines[0].Length is > 0 and <= 100 ? lines[0] : null;
    }

    /// <summary>מפרק את תוכן קובץ הסימון להוכחות; null אם הפורמט לא תקין (כולל קוד גלוי ישן)</summary>
    internal static string[] ParseMarker(string text)
    {
        const string head = "proof-v1";
        if (text == null || !text.StartsWith(head, StringComparison.Ordinal)) return null;
        var lines = text.Substring(head.Length).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length is 0 or > 4 || lines.Any(l => l.Length > 100)) return null;
        return lines;
    }

    /// <summary>כמו VerifyEmergencyCode אבל לפי הוכחה: מול השירות כשהוא מותקן ועונה; מקומית רק בלי שירות או כשהוא מופסק בפועל</summary>
    internal static bool VerifyEmergencyProof(string[] proofs)
    {
        bool LocalOk() => Secret.ProofMatches(proofs, LocalRecoveryStored().hash);
        // WHY (U1): הוכחה = hash מתקבלת רק כשאין שירות או שהוא מופסק בפועל. שירות שרץ: רק טוקן (VerifyEmergencyToken); אחרת מי שקרא את ה-hash היה משחרר
        bool installed = HardLock.ServiceInstalled();
        return ProofAcceptable(installed, installed && ServiceStopped(), LocalOk);
    }

    /// <summary>הכרעה טהורה: הוכחת hash מתקבלת רק בלי שירות מותקן, או כשהוא מופסק בפועל (localOk נבדק עצלנית)</summary>
    internal static bool ProofAcceptable(bool serviceInstalled, bool serviceStopped, Func<bool> localOk) =>
        !serviceInstalled ? localOk() : serviceStopped && localOk();

    /// <summary>צריכת טוקן חירום מול השירות (חד-פעמי). שירות לא מותקן/מופסק: טוקן לא תקף (נשאר נעול). לא עונה: מנסים שוב ואז נשארים נעולים</summary>
    internal static bool VerifyEmergencyToken(string token)
    {
        if (!HardLock.ServiceInstalled()) return false;
        for (int i = 0; ; i++)
        {
            bool? r = null;
            try { r = HardLock.RecoveryTokenConsume(token); } catch (Exception ex) { Config.Log("emergency token: " + ex.Message); }
            if (r == true) return true;
            if (r == false || ServiceStopped() || i >= 2) return false;
            Thread.Sleep(1000);
        }
    }

    private static bool EmergencyReleaseToken(string token)
    {
        if (!VerifyEmergencyToken(token)) return false;
        EndTempUnlockLocal("ביטול חירום בקוד שחזור", relock: false);
        Release();
        Config.Log("kiosk: הנעילה בוטלה בקובץ סימון חירום (טוקן חד-פעמי מהשירות)");
        return true;
    }

    private static bool EmergencyReleaseProof(string[] proofs)
    {
        if (!VerifyEmergencyProof(proofs)) return false;
        EndTempUnlockLocal("ביטול חירום בקוד שחזור", relock: false);
        Release();
        Config.Log("kiosk: הנעילה בוטלה בקובץ סימון חירום (הוכחת קוד שחזור)");
        return true;
    }

    /// <summary>
    /// קיצור חירום מהגשר הרץ (Ctrl+Alt+Shift+F12): מבטל את הנעילה כולה (גם את השירות) - אבל רק עם קוד השחזור.
    /// קיצור בלי שום אימות היה מאפשר לכל תלמיד לבטל את הנעילה, ולכן הקוד נדרש.
    /// </summary>
    public static bool EmergencyRelease(string code)
    {
        // WHY: ה-hash המקומי נחשב רק כשהשירות לא מותקן או מופסק באמת (VerifyEmergencyCode); עריכת config.json לא משחררת
        if (!VerifyEmergencyCode(code)) return false;
        EndTempUnlockLocal("ביטול חירום בקוד שחזור", relock: false);
        Release();
        Config.Log("kiosk: הנעילה בוטלה בקיצור חירום עם קוד שחזור");
        return true;
    }

    /// <summary>ביטול נעילה בזמן הסרה</summary>
    public static string ReleaseForUninstall(string password)
    {
        if (HardLock.Available)
        {
            try { HardLock.Verify(password, true); }
            catch (BridgeException) { return "נדרשת סיסמת הנעילה: --password <סיסמה>"; }
        }
        else if (ServiceDown()) return "רכיב ההקשחה לא מגיב; אי אפשר לאמת סיסמה (קוד שחזור: --unlock)";
        else if (HasPassword && !(Matches(password, "kiosk.passwordHash", "kiosk.salt") ||
                             Matches(Secret.NormalizeRecovery(password), "kiosk.recoveryHash", "kiosk.recoverySalt")))
            return "נדרשת סיסמת הנעילה: --password <סיסמה>";
        Config.Set("kiosk.active", false);
        Config.Set("kiosk.strict", false);
        ClearRegistry();
        StartExplorer();
        return null;
    }

    // ---------- מעקב ----------

    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] private static extern void SwitchToThisWindow(IntPtr hWnd, bool altTab);

    [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect rect);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    private static DateTime _lastFit = DateTime.MinValue;
    private static int _fitFails;

    /// <summary>רושם ליומן הודעה אבחונית לכל היותר פעם בכל כמה שניות לכל מפתח, כדי לא להציף את היומן</summary>
    private static readonly System.Collections.Generic.Dictionary<string, DateTime> _logTimes = new();
    private static void LogThrottled(string key, string msg, int seconds)
    {
        var now = DateTime.UtcNow;
        lock (_logTimes) // נקרא מכמה תהליכונים (Watch, FastGuard, שומר המעטפת, hook)
        {
            if (_logTimes.TryGetValue(key, out var t) && (now - t).TotalSeconds < seconds) return;
            _logTimes[key] = now;
        }
        Config.Log(msg);
    }

    /// <summary>
    /// מוודא שחלון אוצריא ממלא את כל המסך. אם הוא לא ממוקסם — ממקסם. אם הוא ממוקסם אבל עדיין משאיר
    /// פס ריק (אזור העבודה לא התעדכן אחרי סגירת שורת המשימות) — מותח את החלון ישירות לגבולות המסך (ואם זה לא עזר,
    /// משחזר וממקסם), לכל היותר פעם ב-8 שניות, ואחרי 3 ניסיונות כושלים רצופים משהה את הנסיון ל-5 דקות (כדי שלא ירצד). סבילות של 16 פיקסלים:
    /// חלון ממוקסם עשוי להיות קטן או גדול במעט מגבולות המסך (מסגרת, DPI) וזה אינו "פס ריק".
    /// חלון במסך מלא של אוצריא עצמה (מכסה את כל המסך) לא נוגעים בו.
    /// </summary>
    private static void FitToScreen(IntPtr hwnd)
    {
        const int SwRestore = 9, SwMaximize = 3, Tol = 16;
        if (!GetWindowRect(hwnd, out var r)) return;
        var b = System.Windows.Forms.Screen.FromHandle(hwnd).Bounds;
        bool covers = r.Left <= b.Left + Tol && r.Top <= b.Top + Tol && r.Right >= b.Right - Tol && r.Bottom >= b.Bottom - Tol;
        if (covers) { _fitFails = 0; return; }
        if (!IsZoomed(hwnd)) { ShowWindow(hwnd, SwMaximize); return; }
        // WHY: השהיה של 5 דקות אחרי כשלים השאירה חלון קטן/פס ריק חשוף (ושולחן עבודה גלוי) - עכשיו ממשיכים לנסות
        // כל 8 שניות לסירוגין (מתיחה ישירה / שחזור+מיקסום), בלי להשהות
        if ((DateTime.UtcNow - _lastFit).TotalSeconds < 8) return;
        _lastFit = DateTime.UtcNow;
        if (++_fitFails > 1000) _fitFails = 2;
        LogThrottled("fit", $"watchdog: ממלא את המסך (חלון {r.Left},{r.Top},{r.Right},{r.Bottom} מול מסך {b.Left},{b.Top},{b.Right},{b.Bottom})", 60);
        // קודם מותחים ישירות לגבולות המסך (בלי שחזור-ומקסום שמרצד); רק אם זה לא עזר בניסיון הקודם — שחזור ומקסום
        if (_fitFails % 2 == 1)
            SetWindowPos(hwnd, IntPtr.Zero, b.Left, b.Top, b.Width, b.Height, SwpNoZOrder | SwpNoActivate);
        else
        {
            ShowWindow(hwnd, SwRestore);
            ShowWindow(hwnd, SwMaximize);
        }
    }

    private const uint SwpNoZOrder = 0x0004, SwpNoActivate = 0x0010, SwpNoMove = 0x0002, SwpNoSize = 0x0001, SwpFrameChanged = 0x0020;
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hWnd);

    private static IntPtr _fsHwnd = IntPtr.Zero;
    private static long _fsOrigStyle;

    /// <summary>
    /// מסך מלא אמיתי של אוצריא בנעילה: מסיר כותרת ומסגרת וממלא את כל המסך. אם אוצריא כבר ללא מסגרת
    /// (מסך מלא שלה עצמה) לא נוגעים בה. המסגרת המקורית מוחזרת ביציאה מהנעילה. כיבוי: kiosk.fullscreenOtzaria=false.
    /// </summary>
    private static void MakeFullscreen(IntPtr hwnd)
    {
        const int GwlStyle = -16;
        const long WsCaption = 0xC00000, WsThickFrame = 0x40000;
        long style = GetWindowLongPtr(hwnd, GwlStyle).ToInt64();
        if ((style & (WsCaption | WsThickFrame)) == 0) return;
        _fsHwnd = hwnd;
        _fsOrigStyle = style;
        SetWindowLongPtr(hwnd, GwlStyle, new IntPtr(style & ~(WsCaption | WsThickFrame)));
        var b = System.Windows.Forms.Screen.FromHandle(hwnd).Bounds;
        SetWindowPos(hwnd, IntPtr.Zero, b.Left, b.Top, b.Width, b.Height, SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
        LogThrottled("fullscreen", "watchdog: אוצריא עברה למסך מלא (הוסרו כותרת ומסגרת)", 300);
    }

    /// <summary>מחזיר לחלון אוצריא את הכותרת והמסגרת שהוסרו במסך המלא</summary>
    private static void RestoreWindowFrame()
    {
        try
        {
            if (_fsHwnd == IntPtr.Zero || !IsWindow(_fsHwnd)) return;
            SetWindowLongPtr(_fsHwnd, -16, new IntPtr(_fsOrigStyle));
            SetWindowPos(_fsHwnd, IntPtr.Zero, 0, 0, 0, 0, SwpNoZOrder | SwpNoActivate | SwpNoMove | SwpNoSize | SwpFrameChanged);
            ShowWindow(_fsHwnd, 9 /* SW_RESTORE */);
            ShowWindow(_fsHwnd, 3 /* SW_MAXIMIZE */);
        }
        catch (Exception ex) { Config.Log("kiosk restore frame: " + ex.Message); }
        finally { _fsHwnd = IntPtr.Zero; }
    }

    private static int _watchBusy;

    /// <summary>
    /// מעטפת פקודה/מסוף/מארח סקריפטים בחזית בזמן נעילה נסגרת (כמו מנהל המשימות כשהמדיניות לא נכתבה).
    /// מדיניות DisableTaskMgr/DisableRegistryTools לא חוסמת אותם, והחזרת אוצריא לחזית לבדה לא עוצרת תהליך שכבר רץ.
    /// </summary>
    private static void CloseShellWindow(uint fgPid, int session)
    {
        try
        {
            using var p = Process.GetProcessById((int)fgPid);
            if (p.SessionId != session || !Programs.IsShellHost(p)) return;
            string n = p.ProcessName;
            if (!AppTrust.KillLogged(p, "shell")) return;
            LogThrottled("shell:" + n, "watchdog: חלון " + n + " נסגר (מעטפות ומארחי סקריפטים חסומים בנעילה)", 60);
        }
        catch { /* נסגר כבר או ללא הרשאה */ }
    }

    private static int _otzLaunches;
    private static DateTime _otzNextLaunch = DateTime.MinValue, _otzRunningSince = DateTime.MinValue;

    private static DateTime _snipSeen = DateTime.MinValue;

    /// <summary>כלי החיתוך (שברשימת התוכנות המותרות) רץ בחשבון הזה - עד 120 שניות רצופות, ואז חוזרים לאכוף</summary>
    private static bool SnipCaptureInProgress(int session)
    {
        bool found = false;
        foreach (string n in new[] { "SnippingTool", "ScreenClippingHost" })
            foreach (var p in Process.GetProcessesByName(n))
                using (p)
                {
                    try { if (p.SessionId == session && Programs.IsAllowedForeground((uint)p.Id)) found = true; }
                    catch { }
                }
        if (!found) { _snipSeen = DateTime.MinValue; return false; }
        if (_snipSeen == DateTime.MinValue) { _snipSeen = DateTime.UtcNow; LogThrottled("snip", "watchdog: כלי החיתוך פתוח - explorer לא נהרג עד 120 שניות", 30); }
        return (DateTime.UtcNow - _snipSeen).TotalSeconds < 120;
    }

    private static void Watch()
    {
        PollEmergencyMarker(); // לפני ה-gate: Release ממתין לסבב שומר רץ, ואסור שנחסום את עצמנו
        if (!IsActive || Interlocked.Exchange(ref _watchBusy, 1) == 1) return;
        try
        {
            int session = Process.GetCurrentProcess().SessionId;
            // ממצא ביקורת אבטחה: מתעדכן כאן (חוט הטיימר, כל 2 שניות) ולא בתוך ה-hook עצמו — ר' הערה ב-ForegroundIsAllowedProgram
            UpdateForegroundAllowedCache();
            // בזמן שכלי החיתוך המאושר פתוח לא הורגים את explorer (עד 2 דקות): שכבת הצילום של "חדש" בווינדוס 11
            // מתארחת בתוך explorer, ו-Kill כל 2 שניות הרג אותה מיד - נראה כרענון של כלי החיתוך בלי צילום (נצפה
            // ברישום: explorer/userinit/SearchHost נולדים מחדש בכל מחזור). המחיר: שורת המשימות עשויה להופיע לשניות
            if (!SnipCaptureInProgress(session))
            foreach (var p in Process.GetProcessesByName("explorer"))
            {
                try
                {
                    if (p.SessionId != session) continue;
                    p.Kill();
                    // אם Windows מרימה את explorer מחדש, שורת המשימות מהבהבת בכל מחזור; היומן יראה את זה
                    LogThrottled("explorer", "watchdog: explorer נסגר (אם ההודעה חוזרת כל דקה — Windows מרימה אותו מחדש)", 60);
                }
                catch { }
            }

            // אם המדיניות לא נכתבה (אין הרשאה) — סוגרים את מנהל המשימות כשהוא נפתח (מה שאפשר בלי הרשאות מנהל)
            if (!_taskMgrByPolicy && SecBool("kiosk.blockTaskManager", true, true))
                foreach (var p in Process.GetProcessesByName("Taskmgr"))
                {
                    try { if (p.SessionId == session) p.Kill(); } catch { }
                }

            string path = OtzariaPath();
            var otz = FindOtzariaProcess(session);
            if (otz == null)
            {
                _otzRunningSince = DateTime.MinValue;
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    // backoff: אם אוצריא קורסת בעלייה, הפעלה מחדש בכל 2 שניות יוצרת תהליך חדש בלי הפסקה (ומציפה את
                    // המחשב). ההמתנה בין ניסיונות גדלה 2,4,8,16,32,60 שניות, ומתאפסת אחרי ש-אוצריא רצה 20 שניות ברצף
                    if (DateTime.UtcNow >= _otzNextLaunch && !CheckOtzariaForLaunch(path, () => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(path) })?.Dispose()))
                    {
                        // קובץ לא מאומת: לא מפעילים, ושולחן העבודה לא נחשף (יציאה בסיסמה/קוד שחזור)
                        if (!Overlay.RestVisible) Overlay.ShowRest(IntPtr.Zero, idle: true);
                    }
                    else if (DateTime.UtcNow >= _otzNextLaunch)
                    {
                        // [u2] ההפעלה כבר בוצעה בתוך CheckOtzariaForLaunch (תחת LaunchLock)
                        _otzLaunches++;
                        _otzNextLaunch = DateTime.UtcNow.AddSeconds(Math.Min(60, 1 << Math.Min(_otzLaunches, 6)));
                        if (_otzLaunches >= 3) LogThrottled("otzCrash", "watchdog: אוצריא לא נשארת פתוחה (ניסיון הפעלה " + _otzLaunches + ") - ממתינים לפני הניסיון הבא", 60);
                    }
                    else if (_otzLaunches >= 3 && !Overlay.RestVisible) Overlay.ShowRest(IntPtr.Zero, idle: true); // בזמן ההמתנה לא מציגים שולחן עבודה
                }
                else if (!Overlay.RestVisible) Overlay.ShowRest(IntPtr.Zero, idle: true); // אין אוצריא: מסך סגור עם הודעה
                // WHY: גם בלי אוצריא מסכים נוספים חייבים כיסוי; SyncCovers מוותר לבד כשמסך המנוחה מוצג
                Overlay.SyncCovers(IntPtr.Zero);
                return;
            }
            if (_otzRunningSince == DateTime.MinValue) _otzRunningSince = DateTime.UtcNow;
            else if ((DateTime.UtcNow - _otzRunningSince).TotalSeconds >= 20) { _otzLaunches = 0; _otzNextLaunch = DateTime.MinValue; }

            IntPtr hwnd = otz.MainWindowHandle;
            if (hwnd == IntPtr.Zero) { Overlay.SyncCovers(IntPtr.Zero); return; } // חלון עוד לא נוצר: המסך הראשי נשאר, השאר מכוסים

            // אוצריא תמיד ממוקסמת: בלי מזעור, בלי חלון קטן, ובלי חור במקום שורת המשימות
            // אוצריא ממוזערת: מסך מנוחה עם סמל אוצריא, שעה ותאריך, במקום החזרה מיידית
            // שומר מסך אחרי זמן ללא פעילות (0 = כבוי), גם כשאוצריא פתוחה
            int idleMin = Config.Get("kiosk.restIdleMinutes", 0);
            if (idleMin > 0 && !Overlay.RestVisible && IdleMs() >= idleMin * 60000L) { Overlay.ShowRest(hwnd, idle: true); return; }
            if (Overlay.RestIdle) return;
            if (IsIconic(hwnd))
            {
                if (Config.Get("kiosk.restScreen", true)) { Overlay.ShowRest(hwnd); return; }
                ShowWindow(hwnd, 9 /* SW_RESTORE */);
            }
            else if (Overlay.RestVisible) Overlay.HideRest();
            if (Overlay.RestVisible) return;
            // מסכים נוספים: כיסוי שחור (אוצריא ממלאת רק את המסך שלה); תוכנה מותרת בחזית (אולי במסך השני) - בלי כיסוי
            if (ForegroundIsAllowedProgram()) Overlay.HideCovers(); else Overlay.SyncCovers(hwnd);
            if (Config.Get("kiosk.maximizeOtzaria", true)) FitToScreen(hwnd);
            if (Config.Get("kiosk.fullscreenOtzaria", true)) MakeFullscreen(hwnd);
            BlockClose(hwnd);

            GetWindowThreadProcessId(GetForegroundWindow(), out uint fgPid);
            if (fgPid == otz.Id || fgPid == Environment.ProcessId) return;
            // pid=0 ("Idle" בלוג): אין חלון בחזית ברגע הדגימה - מעבר פוקוס בין חלונות. החזרת אוצריא ברגע כזה גוזלת
            // את הפוקוס מהחלון שעומד לקבל אותו (למשל שכבת הצילום של כלי החיתוך אחרי "חדש") ונראית כרענון של התוכנה.
            // הדגימה הבאה (עוד 2 שניות) תבדוק שוב; חלון אסור אמיתי יזוהה אז לפי התהליך שלו
            if (fgPid == 0) return;
            CloseShellWindow(fgPid, session);
            if (Programs.IsAllowedForeground(fgPid)) return; // תוכנה מותרת (רשימה מוגדרת בסיסמה) בחזית
            try
            {
                using var fgp = Process.GetProcessById((int)fgPid);
                LogThrottled("fg:" + fgp.ProcessName, "watchdog: מחזיר את אוצריא לחזית מהתהליך " + fgp.ProcessName, 60);
            }
            catch { }
            if (!SetForegroundWindow(hwnd)) SwitchToThisWindow(hwnd, true);
        }
        catch (Exception ex) { Config.Log("watchdog: " + ex.Message); }
        finally { Interlocked.Exchange(ref _watchBusy, 0); }
    }

    // ---------- חסימת מקשים ----------

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr extra; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc fn, IntPtr hMod, uint threadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string name);

    private static bool _hookLogged;

    /// <summary>
    /// תמונת מצב של "האם התהליך הזה בחזית מותר", מחושבת מראש בחוט השומר (Watch, כל 2 שניות) ולא
    /// בתוך ה-hook. אובייקט record יחיד: הקצאה/קריאה של הפניה הן אטומיות ב-.NET, כך שה-hook תמיד
    /// רואה צמד (Pid, Allowed) עקבי מאותו עדכון, בלי צורך בנעילה.
    /// </summary>
    private sealed record FgAllowedCache(uint Pid, bool Allowed, bool Otzaria = false, bool Browser = false);
    private static FgAllowedCache _fgAllowedCache = new(0, false);

    /// <summary>
    /// מחשב ושומר את המטמון של "התהליך בחזית מותר". נקרא רק מ-Watch() (חוט טיימר נפרד מה-hook),
    /// ולכן מותר לו לקרוא ל-Process.GetProcessById/MainModule/WMI (Programs.ShouldYieldTo) שעלולות
    /// להיות איטיות — זה בדיוק מה שאסור לעשות בתוך ה-hook הסינכרוני (ר' ForegroundIsAllowedProgram).
    /// </summary>
    private static void UpdateForegroundAllowedCache()
    {
        try
        {
            GetWindowThreadProcessId(GetForegroundWindow(), out uint pid);
            bool otz = pid != 0 && IsOtzariaPid(pid);
            bool allowed = pid != 0 && pid != (uint)Environment.ProcessId && !otz && Programs.IsAllowedForeground(pid);
            bool browser = false;
            if (pid != 0)
                try { using var bp = Process.GetProcessById((int)pid); browser = BrowserNames.Contains(bp.ProcessName); } catch { }
            _fgAllowedCache = new FgAllowedCache(pid, allowed, otz, browser);
        }
        catch { _fgAllowedCache = new FgAllowedCache(0, false); }
    }

    /// <summary>
    /// ביצועים (ממצא ביקורת אבטחה): נקרא מתוך HookCallback, שרץ כ-callback סינכרוני של WH_KEYBOARD_LL.
    /// Windows "מנתק" hook מקלדת גלובלי שלא חוזר תוך כ-300ms (LowLevelHooksTimeout), ולכן אסור לקרוא כאן
    /// ל-Process.GetProcessById/MainModule/WMI (כמו שהיה ב-Programs.IsAllowedForeground/ShouldYieldTo) —
    /// אלה עלולות לקחת מילישניות עד שניות (עומס, AV), מה שהיה מאט הקלדה בזמן נעילה או מנתק את החסימה.
    /// במקום זה: רק שתי קריאות user32 זולות (GetForegroundWindow/GetWindowThreadProcessId) כדי לדעת
    /// איזה pid נמצא כרגע בחזית, והתשובה "מותר?" נלקחת מהמטמון שמתעדכן כל 2 שניות ב-Watch() (חוט אחר).
    /// אם ה-pid בחזית השתנה מאז העדכון האחרון (עד 2 שניות פער) — ברירת המחדל היא "לא מותר" (הכיוון הבטוח).
    /// </summary>
    private static bool ForegroundIsAllowedProgram()
    {
        GetWindowThreadProcessId(GetForegroundWindow(), out uint pid);
        if (pid == 0) return false;
        var c = _fgAllowedCache;
        return pid == c.Pid && c.Allowed;
    }

    private static readonly HashSet<string> BrowserNames = new(StringComparer.OrdinalIgnoreCase)
        { "msedge", "chrome", "firefox", "iexplore", "brave", "opera", "vivaldi" };

    private static bool IsOtzariaPid(uint pid)
    {
        try
        {
            // WHY: שם תהליך בלבד ניתן לזיוף (otzaria.exe בכל תיקיה); משווים את נתיב התמונה לנתיב המוגדר. בלי נתיב - לא אוצריא
            string path = OtzariaPath();
            return !string.IsNullOrEmpty(path) && SamePath(ImagePath(pid), path) && OtzariaImageVerified(path);   // [u2] נתיב + אימות SHA/מיקום
        }
        catch { return false; }
    }

    /// <summary>חזרה לאוצריא מתוכנה מותרת (Ctrl+Alt+O)</summary>
    private static void ReturnToOtzaria()
    {
        try
        {
            int session = Process.GetCurrentProcess().SessionId;
            var otz = FindOtzariaProcess(session);
            if (otz != null && otz.MainWindowHandle != IntPtr.Zero) BringToFront(otz.MainWindowHandle);
        }
        catch (Exception ex) { Config.Log("return to otzaria: " + ex.Message); }
    }

    private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var k = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            uint vk = k.vkCode;
            bool down = wParam == (IntPtr)0x100 || wParam == (IntPtr)0x104;
            bool ctrl = (GetAsyncKeyState(0x11) & 0x8000) != 0;
            bool shift = (GetAsyncKeyState(0x10) & 0x8000) != 0;
            // דגל ה-Alt של ה-hook (LLKHF_ALTDOWN) לא מסומן בכל מקלדת/פריסה (נצפה בלוג: F12 הגיע עם alt=False
            // למרות ש-Alt היה לחוץ) - לכן בכל הבדיקות מקבלים אם אחד מהם אומר "לחוץ": הדגל, MENU, LMENU או RMENU
            bool altHeld = (k.flags & 0x20) != 0 || (GetAsyncKeyState(0x12) & 0x8000) != 0 ||
                           (GetAsyncKeyState(0xA4) & 0x8000) != 0 || (GetAsyncKeyState(0xA5) & 0x8000) != 0;
            bool win = (GetAsyncKeyState(0x5B) & 0x8000) != 0 || (GetAsyncKeyState(0x5C) & 0x8000) != 0;
            // אבחון: האם F12 בכלל מגיע ל-hook (הלוג נכתב מחוץ ל-hook)
            if (vk == 0x7B && down) Task.Run(() => Config.Log($"hook: F12 ctrl={ctrl} alt={altHeld} shift={shift}"));

            // Ctrl+Alt+O: חזרה לאוצריא מתוכנה מותרת (רק כשתוכנה מותרת בחזית; באוצריא עצמה המקשים עוברים הלאה)
            // [r1] שינוי מינימלי בנקודת חיבור (קובץ בבעלות אחר): AltGr (Ctrl+Alt ימני) לא נחשב Ctrl+Alt+O — ר' KioskKeys.IsReturnHotkey
            if (KioskKeys.IsReturnHotkey(vk, down, ctrl, altHeld, (GetAsyncKeyState(0xA4) & 0x8000) != 0, (GetAsyncKeyState(0xA5) & 0x8000) != 0) && ForegroundIsAllowedProgram())
            {
                Task.Run(ReturnToOtzaria);
                return (IntPtr)1;
            }
            // Ctrl+Alt+Shift+F12: חלון קוד שחזור לביטול חירום של הנעילה. ה-hook כבר לא בחוט ה-UI, אבל עדיין לא
            // מריצים כאן: פתיחת טופס + pipe לשירות בתוך callback חורגים מ-300ms ו-Windows מסירה את ה-hook
            if (ctrl && altHeld && vk == 0x7B && shift && down)
            {
                Task.Run(() => { try { Config.Log("emergency: זוהה Ctrl+Alt+Shift+F12 ב-hook"); Tray.Invoke(EmergencyForm.Open); } catch (Exception ex) { Config.Log("emergency open: " + ex.Message); } });
                return (IntPtr)1;
            }
            GetWindowThreadProcessId(GetForegroundWindow(), out uint fgPid);
            var c = _fgAllowedCache;
            bool known = fgPid != 0 && fgPid == c.Pid; // pid שהשתנה מאז העדכון האחרון = "לא מותר" (הכיוון הבטוח)
            var ctx = new KioskKeys.Ctx(altHeld, ctrl, shift, win, known && c.Otzaria, known && c.Allowed, known && c.Browser,
                DialogGuard.IsFileDialogForeground(), DialogGuard.IsUnsafeEntryForeground()); // [r1] הארגומנט האחרון חדש (Enter על ערך אסור)
            if (KioskKeys.ShouldBlock(vk, ctx)) return (IntPtr)1;
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }
}
