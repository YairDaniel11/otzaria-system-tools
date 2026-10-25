using System.Text.Json;
using System.Text.Json.Nodes;

namespace OtzariaBridge;

/// <summary>
/// מדיניות סייר הקבצים: כבוי (ברירת מחדל) / מוגבל לתיקיות שהמנהל בחר / נעול עד הזנת סיסמת מנהל.
/// נאכפת בגשר עצמו (FsApi, RecycleApi) ולא רק בממשק, כדי שתוסף או סקריפט לא יעקפו אותה דרך ה-HTTP.
/// אחסון: Config, מפתחות fx.policy.*. מצב ה"פתוח זמנית" נשמר בזיכרון בלבד (הפעלה מחדש של הגשר נועלת שוב).
/// </summary>
public static class FxPolicy
{
    private const string KMode = "fx.policy.mode", KRoots = "fx.policy.roots", KMinutes = "fx.policy.unlockMinutes";
    private const int DefaultMinutes = 15, MaxMinutes = 1440, MaxRoots = 32;

    private static readonly object Gate = new();
    private static long _unlockedUntil;   // Environment.TickCount64, 0 = נעול

    public static void Register()
    {
        Server.Route("/fs/policy/get", _ => Get());
        Server.Route("/fs/policy/set", Set);
        Server.Route("/fs/policy/unlock", Unlock);
        Server.Route("/fs/policy/lock", _ => { Relock(); return Get(); });
    }

    // ---------- מצב ----------

    private sealed class State
    {
        public string Mode;          // off | restricted | locked (כפי שהוגדר; ערך לא מוכר = locked)
        public string Effective;     // open | restricted | locked
        public string[] Roots = Array.Empty<string>();   // קנוניים ותקפים
        public string[] Invalid = Array.Empty<string>();
        public bool Fallback;        // מוגבל בלי תיקיות תקפות -> נעול
        public int Minutes;
        public int UnlockedSeconds;
        public int TempUnlockSeconds;  // פתיחה זמנית של כל המחשב (Kiosk) — גם הסייר פתוח לגמרי עד סופה
    }

    private static int UnlockSeconds()
    {
        lock (Gate)
        {
            long left = _unlockedUntil - Environment.TickCount64;
            return left > 0 ? (int)(left / 1000) + 1 : 0;
        }
    }

    private static void Relock() { lock (Gate) _unlockedUntil = 0; }

    // כשרכיב ההקשחה מותקן ויש לו מדיניות — היא מקור האמת (config.json כתיב למשתמש המחובר); אחרת config.json
    private static HardLock.PolicySnapshot ServicePolicy()
    {
        var pol = HardLock.Policy;
        return pol?.FxMode != null ? pol : null;
    }

    /// <summary>
    /// האם המנהל לא קבע מצב כלל (לא בשירות ולא ב-config.json). WHY: בנעילה פעילה ברירת המחדל "off" פתחה את כל הכוננים
    /// בדיאלוגי Open/Save ובסייר; עכשיו ברירת המחדל בנעילה היא restricted. "off" מפורש של המנהל נשמר כפי שהוא.
    /// </summary>
    private static bool ModeDefaulted(HardLock.PolicySnapshot sp) =>
        sp?.FxMode == null && string.IsNullOrEmpty(Config.Get<string>(KMode, null));

    internal static string DefaultMode(bool kioskActive) => kioskActive ? "restricted" : "off";

    /// <summary>תיקיות ברירת מחדל כשהמצב לא נקבע: תיקיות המשתמש המוכרות (בלי תיקיית הבית כולה, שמכילה AppData)</summary>
    private static string[] DefaultRoots() =>
        FsApi.KnownFolders().Where(k => k.id != "home").Select(k => k.path).ToArray();

    private static string RawMode(HardLock.PolicySnapshot sp) =>
        sp?.FxMode ?? Config.Get<string>(KMode, null) ?? DefaultMode(Kiosk.IsActive);
    private static string[] RawRoots(HardLock.PolicySnapshot sp) =>
        sp?.FxRoots ?? (ModeDefaulted(sp) && Kiosk.IsActive ? DefaultRoots() : Config.GetList(KRoots));

    /// <summary>האם path בתוך תיקייה שהמנהל אישר במפורש (לא ברירות המחדל), במצב מוגבל. לבדיקת סיומות מריצות</summary>
    internal static bool IsInApprovedRoots(string path)
    {
        try
        {
            var sp = ServicePolicy();
            if (ModeDefaulted(sp)) return false;
            var st = Current();
            if (st.Effective != "restricted") return false;
            string canon = PathPolicy.Resolve(path);
            return st.Roots.Any(r => PathPolicy.IsUnder(canon, r));
        }
        catch { return false; }
    }
    private static int RawMinutes(HardLock.PolicySnapshot sp) => sp?.FxUnlockMinutes ?? Config.Get(KMinutes, DefaultMinutes);

    /// <summary>הגירה לרכיב ההקשחה (בהתקנה/עדכון שלו): אם עוד אין לו מדיניות, מעבירים את הנוכחית מ-config.json</summary>
    internal static void SyncToService(string password)
    {
        var pol = HardLock.Policy;
        if (pol == null || pol.FxMode != null) return;
        if (ModeDefaulted(null)) return;   // ברירת מחדל לא נשמרת כהגדרה מפורשת של המנהל
        HardLock.SetFxPolicy(password, RawMode(null) ?? "off", RawRoots(null), Math.Clamp(RawMinutes(null), 1, MaxMinutes));
    }

    /// <summary>תיקיות מותרות לשמירה כשהסייר מוגבל; null = אין הגבלה (#60 צילום מסך)</summary>
    public static string[] SaveRootsOrNull()
    {
        var st = Current();
        return st.Effective == "open" ? null : st.Roots;
    }

    private static State Current()
    {
        var sp = ServicePolicy();
        string mode = (RawMode(sp) ?? "off").ToLowerInvariant();
        if (mode is not ("off" or "restricted" or "locked")) mode = "locked";   // ערך פגום: כשל סגור
        int minutes = Math.Clamp(RawMinutes(sp), 1, MaxMinutes);
        var st = new State { Mode = mode, Minutes = minutes, UnlockedSeconds = UnlockSeconds(), TempUnlockSeconds = Kiosk.TempUnlockSecondsLeft() };
        if (mode == "off") { st.Effective = "open"; return st; }
        // "הכול נפתח יחד": בזמן פתיחה זמנית של המחשב (בסיסמת הפתיחה הזמנית) גם הסייר פתוח לגמרי — בלי נעילה
        // ובלי הגבלת תיקיות — עד סוף אותו זמן. מקור הזמן הוא רכיב ההקשחה כשהוא מותקן (ראו Kiosk.TempUnlockSecondsLeft)
        if (st.TempUnlockSeconds > 0) { st.Effective = "open"; return st; }
        if (mode == "locked") { st.Effective = st.UnlockedSeconds > 0 ? "open" : "locked"; return st; }

        var ok = new List<string>();
        var bad = new List<string>();
        foreach (string r in RawRoots(sp))
        {
            try
            {
                string canon = PathPolicy.Resolve(r);
                if (Directory.Exists(canon)) ok.Add(canon); else bad.Add(r);
            }
            catch { bad.Add(r); }
        }
        foreach (string b in bad) Config.Log("fx policy: תיקייה מותרת שאינה קיימת/תקפה הוסרה מהאכיפה: " + b);
        st.Roots = ok.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        st.Invalid = bad.ToArray();
        if (st.Roots.Length == 0)
        {
            st.Fallback = true;   // לא נופלים ל"פתוח"
            st.Effective = st.UnlockedSeconds > 0 ? "open" : "locked";
        }
        else st.Effective = "restricted";
        return st;
    }

    // ---------- אכיפה (נקרא מ-FsApi/RecycleApi) ----------

    internal static BridgeException Locked() =>
        new("fx_locked", "סייר הקבצים נעול. יש להזין את סיסמת המנהל כדי לפתוח אותו", 423);

    internal static BridgeException Outside(string why = null) =>
        new("fx_outside_policy", why ?? "הנתיב נמצא מחוץ לתיקיות שהמנהל התיר בסייר הקבצים", 403);

    /// <summary>זורק fx_locked אם הסייר נעול כרגע (לנקודות קצה שאינן מקבלות נתיב)</summary>
    internal static void EnsureAvailable()
    {
        if (Current().Effective == "locked") throw Locked();
    }

    /// <summary>התיקיות הקנוניות והתקפות שהמדיניות מתירה כרגע</summary>
    internal static string[] ActiveRoots() => Current().Roots;

    /// <summary>האם המדיניות מגבילה כרגע לתיקיות מסוימות</summary>
    internal static bool IsRestricted => Current().Effective == "restricted";

    /// <summary>
    /// המצב האפקטיבי הנוכחי: open (אין הגבלה) / restricted (מוגבל לתיקיות) / locked.
    /// נקרא בלייב בכל קריאה (אין קאשינג מעבר ל-Current עצמו) כדי ש-DialogGuard (אכיפה על דיאלוגי
    /// פתיחה/שמירה מקוריים של Windows בכל תהליך) יגיב מיידית לשינוי מדיניות, בלי להפעיל מחדש את הגשר.
    /// </summary>
    internal static string EffectiveMode() => Current().Effective;

    /// <summary>זורק אם הנתיב אסור: נעול -> fx_locked; מחוץ לתיקיות -> fx_outside_policy; כל כשל בפענוח נחשב אסור</summary>
    internal static void EnsurePathAllowed(string path)
    {
        var st = Current();
        if (st.Effective == "open") return;
        if (st.Effective == "locked") throw Locked();
        bool ok;
        try
        {
            string canon = PathPolicy.Resolve(path);
            ok = !PathPolicy.IsUnder(canon, PathPolicy.Resolve(Config.Dir)) && st.Roots.Any(r => PathPolicy.IsUnder(canon, r));
        }
        catch { ok = false; }
        if (!ok) throw Outside();
    }

    /// <summary>גרסה שלא זורקת, לסינון רשימות (למשל סל מיחזור, לוח)</summary>
    internal static bool IsPathAllowed(string path)
    {
        try { EnsurePathAllowed(path); return true; }
        catch (BridgeException) { return false; }
    }

    // ---------- נקודות קצה ----------

    private static object Get()
    {
        var st = Current();
        var sp = ServicePolicy();
        return new
        {
            mode = st.Mode,
            effective = st.Effective,
            locked = st.Effective == "locked",
            fallback = st.Fallback,
            roots = st.Mode == "restricted" ? RawRoots(sp) : Array.Empty<string>(),
            activeRoots = st.Roots,
            invalidRoots = st.Invalid,
            unlockMinutes = st.Minutes,
            unlockedSeconds = st.UnlockedSeconds,
            tempUnlockSeconds = st.TempUnlockSeconds,
            passwordConfigured = Kiosk.PasswordConfigured,
            defaults = FsApi.KnownFolders().Where(k => k.id != "home").Select(k => new { k.id, k.name, k.path }).ToArray(),
        };
    }

    /// <summary>
    /// שער ההרשאה לפעולות ניהול על המדיניות. דורש גם Windows Hello כשהאימות הדו-שלבי פעיל, חוץ מביטול
    /// חירום בקוד שחזור. שינוי מדיניות מקבל גם קוד שחזור, כדי שהמנהל לא יינעל מחוץ למדיניות.
    /// </summary>
    private static void AdminGate(JsonElement b, bool allowRecovery)
    {
        if (!Kiosk.PasswordConfigured)
            throw new BridgeException("no_password", "יש להגדיר סיסמת נעילה בהגדרות הנעילה לפני שינוי מדיניות סייר הקבצים", 400);
        string pw = b.Str("password");
        if (allowRecovery) Kiosk.RequirePasswordOrRecovery(pw); else Kiosk.RequirePassword(pw);
        if (!Kiosk.IsRecoveryCode(pw)) TwoFactor.Require("שינוי מדיניות סייר הקבצים");
        AdminLog.Add("שינוי מדיניות סייר הקבצים");
    }

    private static object Set(JsonElement b)
    {
        AdminGate(b, allowRecovery: true);
        string mode = (b.Str("mode", "off") ?? "off").ToLowerInvariant();
        if (mode is not ("off" or "restricted" or "locked")) throw BridgeException.BadRequest("mode חייב להיות off, restricted או locked");

        var roots = new List<string>();
        foreach (string raw in b.StrArray("roots"))
        {
            string full;
            try { full = PathPolicy.Normalize(Environment.ExpandEnvironmentVariables(raw)); }
            catch (ArgumentException ex) { throw BridgeException.BadRequest(ex.Message + ": " + raw); }
            if (!Directory.Exists(full)) throw BridgeException.BadRequest("התיקייה לא נמצאה: " + raw);
            if (!roots.Contains(full, StringComparer.OrdinalIgnoreCase)) roots.Add(full);
        }
        if (roots.Count > MaxRoots) throw BridgeException.BadRequest("יותר מדי תיקיות (מקסימום " + MaxRoots + ")");
        if (mode == "restricted" && roots.Count == 0) throw BridgeException.BadRequest("במצב מוגבל יש לבחור לפחות תיקייה אחת");
        int minutes = (int)Math.Clamp(b.Long("unlockMinutes", DefaultMinutes), 1, MaxMinutes);

        // כשרכיב ההקשחה מותקן המדיניות נשמרת קודם אצלו (מקור האמת); כישלון שם עוצר גם את השמירה המקומית
        if (HardLock.Available) HardLock.SetFxPolicy(b.Str("password"), mode, roots, minutes);
        var arr = new JsonArray();
        foreach (string r in roots) arr.Add(r);
        Config.Set(KMode, JsonValue.Create(mode));
        Config.Set(KRoots, arr);
        Config.Set(KMinutes, JsonValue.Create(minutes));
        Config.Save();
        Relock();
        Config.Log("fx policy: mode=" + mode + " roots=" + roots.Count);
        return Get();
    }

    private static object Unlock(JsonElement b)
    {
        AdminGate(b, allowRecovery: false);
        int minutes = Math.Clamp(RawMinutes(ServicePolicy()), 1, MaxMinutes);
        lock (Gate) _unlockedUntil = Environment.TickCount64 + minutes * 60_000L;
        return Get();
    }
}
