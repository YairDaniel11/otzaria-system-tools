using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace OtzariaBridge;

/// <summary>
/// חתימת HMAC על נתונים בעלי משמעות אבטחתית שנשמרים ב-config.json (כתיב למשתמש המחובר), בעזרת מפתח שרק השירות (SYSTEM) מחזיק.
/// WHY (S3): config.json הוא קובץ שהמשתמש עורך; בלי חתימה אפשר להוסיף שם טוקן צימוד, לפתוח הגדרות (kiosk.openSections),
/// לכבות readOnly/twoFactor וכו' ישירות בקובץ. הגשר עצמו אינו יכול להחזיק סוד שהמשתמש לא יכול לקרוא, ולכן המפתח נשאר בשירות:
/// הגשר שולח לשירות את הנתונים בפקודה "config-sign" ומקבל MAC. הגשר לא מכיר את המפתח.
///
/// מצבים (SealState): NoService = אין שירות מותקן/מוגדר, Unsupported = שירות ישן שלא מכיר את הפקודה (שניהם: התנהגות קודמת, תאימות לאחור);
/// Down = השירות מותקן ולא עונה / ענה בשגיאה (fail-closed: לא מקבלים נתון לא מאומת); Ok = חתימה זמינה.
/// U1: חתימה (config-sign) מוגבלת לגשר שהשירות הפעיל / מנהל מורם / סיסמה בבקשה; אימות (config-verify) פתוח כי אינו אורקל חתימה;
/// וחתימת מפתחות האבטחה נושאת מונה מונוטוני מהשירות (הגנת rollback).
/// </summary>
public static class ConfigSeal
{
    public enum SealState { NoService, Unsupported, Down, Ok }

    private static readonly Func<byte[], (SealState state, byte[] mac)> DefaultProvider = ServiceProvider;

    /// <summary>ספק ה-MAC. ברירת מחדל: השירות דרך ה-pipe. בבדיקות מוחלף (ואז גם האימות עובר דרכו, בלי מונה).</summary>
    public static Func<byte[], (SealState state, byte[] mac)> Provider = DefaultProvider;

    private static bool UseService => Provider == DefaultProvider;

    private static readonly ConcurrentDictionary<string, bool> Good = new();
    private static SealState _probe = SealState.NoService;
    private static DateTime _probeAt = DateTime.MinValue;

    public const string CounterKey = "sec.ctr";

    // סיסמה שאומתה זה עתה בגשר (Kiosk.Verify): נשלחת לשירות בבקשת חתימה מגשר שהשירות לא הפעיל. WHY (U1): ר' ConfigSealKey.ClientMaySign
    private static string _verifiedPw;
    private static DateTime _verifiedAt = DateTime.MinValue;
    private static readonly TimeSpan PwTtl = TimeSpan.FromMinutes(2);

    /// <summary>מתעד סיסמה שאומתה לאחרונה מול השירות (תקפה 2 דקות) לצורך בקשת חתימה</summary>
    public static void NoteVerified(string password)
    {
        _verifiedPw = string.IsNullOrEmpty(password) ? null : password;
        _verifiedAt = DateTime.UtcNow;
    }

    private static string RecentPassword() =>
        _verifiedPw != null && DateTime.UtcNow - _verifiedAt < PwTtl ? _verifiedPw : null;

    private static bool IsUnknownOp(JsonObject res) =>
        res?["code"]?.ToString() == "unknown_op" || res?["message"]?.ToString()?.StartsWith("פקודה לא מוכרת") == true;

    /// <summary>
    /// WHY (U1): בעבר כל ok:false (forbidden/internal/too_many) נחשב "שירות ישן" = Unsupported = אין אכיפה (fail-open, ובמטמון 30 שניות).
    /// עכשיו רק "פקודה לא מוכרת" היא Unsupported; כל שגיאה אחרת = Down (נסגר).
    /// </summary>
    internal static SealState ClassifyError(JsonObject res) => IsUnknownOp(res) ? SealState.Unsupported : SealState.Down;

    private static (SealState, byte[]) ServiceProvider(byte[] data)
    {
        try
        {
            if (!HardLock.Available) return (SealState.NoService, null);
            var req = new JsonObject { ["op"] = "config-sign", ["data"] = Convert.ToBase64String(data) };
            if (RecentPassword() is string pw) req["password"] = pw;
            var res = HardLock.Call(req, 1500);
            if (res == null) return (SealState.Down, null);
            if (res["ok"]?.GetValue<bool>() == true && res["mac"] is JsonNode m)
            {
                try { return (SealState.Ok, Convert.FromBase64String(m.ToString())); } catch { return (SealState.Down, null); }
            }
            return (ClassifyError(res), null);   // רק "פקודה לא מוכרת" = שירות ישן; forbidden/too_many/internal = סגור
        }
        catch { return (SealState.Down, null); }
    }

    /// <summary>
    /// אימות בשירות (config-verify): בלי הגבלת חותם, כי הוא מחזיר רק כן/לא על MAC מועמד (אין אורקל חתימה).
    /// seal=true: עם מונה (ר' ConfigSealKey.CounterAccepts)
    /// </summary>
    private static (SealState state, bool valid) ServiceVerify(byte[] data, string macB64, bool seal, long? ctr)
    {
        try
        {
            if (!HardLock.Available) return (SealState.NoService, false);
            var req = new JsonObject { ["op"] = "config-verify", ["data"] = Convert.ToBase64String(data), ["mac"] = macB64 ?? "" };
            if (seal) { req["kind"] = "seal"; if (ctr != null) req["counter"] = ctr.Value; }
            var res = HardLock.Call(req, 1500);
            if (res == null) return (SealState.Down, false);
            if (res["ok"]?.GetValue<bool>() == true) return (SealState.Ok, res["valid"]?.GetValue<bool>() == true);
            return (ClassifyError(res), false);
        }
        catch { return (SealState.Down, false); }
    }

    /// <summary>האם החתימה נאכפת: שירות מותקן שתומך בפקודה (או מותקן ולא עונה: סגור). במטמון 30 שניות</summary>
    public static bool Enforced
    {
        get
        {
            if (DateTime.UtcNow - _probeAt > TimeSpan.FromSeconds(30))
            {
                var probe = Encoding.UTF8.GetBytes("probe");
                var st = UseService ? ServiceVerify(probe, "", false, null).state : Provider(probe).state;
                // שירות ישן שמכיר רק config-sign: בודקים גם דרכו לפני שמכריזים "לא נאכף"
                if (UseService && st == SealState.Unsupported) st = Provider(probe).state;
                _probe = st;
                _probeAt = DateTime.UtcNow;
            }
            return _probe is SealState.Ok or SealState.Down;
        }
    }

    /// <summary>מאפס את מטמוני החתימה/הזמינות (בדיקות, או אחרי התקנה/הסרה של השירות)</summary>
    public static void Reset() { Good.Clear(); _probeAt = DateTime.MinValue; _lastSealGood = null; _verifiedPw = null; }

    /// <summary>MAC (Base64) לטקסט, או null כשאין חתימה זמינה</summary>
    public static string Sign(string canonical)
    {
        var (st, mac) = Provider(Encoding.UTF8.GetBytes(canonical ?? ""));
        return st == SealState.Ok && mac != null ? Convert.ToBase64String(mac) : null;
    }

    /// <summary>
    /// האם ה-MAC תואם. NoService/Unsupported: true (תאימות לאחור, אין מה לאכוף). Down: false (fail-closed) אלא אם כבר אומת בתהליך הזה.
    /// WHY המטמון: בקשת HTTP בודקת צימוד בכל קריאה; חיבור pipe לכל בקשה יאט, ותקלת pipe רגעית לא צריכה לנתק תוספים שכבר אומתו.
    /// </summary>
    public static bool Verify(string canonical, string macB64)
    {
        string ck = canonical + "\n" + macB64;
        if (macB64 != null && Good.ContainsKey(ck)) return true;
        var data = Encoding.UTF8.GetBytes(canonical ?? "");
        if (UseService)
        {
            var (vs, valid) = ServiceVerify(data, macB64, false, null);
            if (vs == SealState.NoService) return true;
            if (vs == SealState.Down) return false;
            if (vs == SealState.Ok)
            {
                if (!valid || string.IsNullOrEmpty(macB64)) return false;
                Good[ck] = true;
                return true;
            }
            // Unsupported: שירות ישן בלי config-verify, אימות בחישוב MAC דרך config-sign (למטה)
        }
        var (st, mac) = Provider(data);
        switch (st)
        {
            case SealState.NoService:
            case SealState.Unsupported:
                return true;
            case SealState.Down:
                return false;
        }
        if (string.IsNullOrEmpty(macB64) || mac == null) return false;
        try
        {
            if (!CryptographicOperations.FixedTimeEquals(mac, Convert.FromBase64String(macB64))) return false;
        }
        catch { return false; }
        Good[ck] = true;
        return true;
    }

    // ---------- חתימה על קבוצת מפתחות אבטחה ב-config.json ----------

    /// <summary>מפתחות config בעלי משמעות אבטחתית (לשימוש הקוראים ב-Kiosk דרך SecurityIntact; ר' הצעה בדוח)</summary>
    public static readonly string[] SecurityKeys =
    {
        "kiosk.allowOpenExtensions", "kiosk.openSections", "kiosk.readOnly", "kiosk.allowExcelDocs", "kiosk.strict", "kiosk.blockTaskManager",
        "kiosk.twoFactor", "kiosk.otzariaPath", "kiosk.schedule.enabled", "kiosk.schedule.start", "kiosk.schedule.end", "kiosk.schedule.warn",
    };

    /// <summary>
    /// מפתחות שנכללים בחתימה רק כשיש להם ערך (QA-B 3). WHY: הוספה ל-SecurityKeys הייתה משנה את הייצוג הקנוני של כל התקנה קיימת
    /// ופוסלת את החתימה שלה בעדכון (נעילה תקועה); כך config קיים בלי המפתחות האלה נשאר תקף, והוספה ידנית שלהם = אי-התאמה = נסגר.
    /// </summary>
    public static readonly string[] OptionalSealedKeys = { "fx.policy.mode", "fx.policy.roots", "fx.policy.unlockMinutes", "kiosk.resetOnShutdown" };

    public const string SealKey = "sec.seal";

    /// <summary>ייצוג קנוני יציב של ערכי מפתחות האבטחה (ממוין, JSON קומפקטי; מפתח חסר = null)</summary>
    public static string Canonical(JsonObject cfg)
    {
        var sb = new StringBuilder("otzaria-config-seal-v1\n");
        foreach (string k in SecurityKeys.OrderBy(x => x, StringComparer.Ordinal))
            sb.Append(k).Append('=').Append(cfg?[k]?.ToJsonString() ?? "null").Append('\n');
        foreach (string k in OptionalSealedKeys.OrderBy(x => x, StringComparer.Ordinal))   // רק כשקיימים (QA-B 3)
            if (cfg?[k] is JsonNode v) sb.Append(k).Append('=').Append(v.ToJsonString()).Append('\n');
        return sb.ToString();
    }

    /// <summary>
    /// חותם את מצב מפתחות האבטחה הנוכחי ושומר ב-cfg[SealKey]. נקרא מקוד הנעילה אחרי שמירה לגיטימית של הגדרות.
    /// WHY (U1): מול שירות תומך המונה (cfg[CounterKey]) נקבע בשירות ונכלל ב-MAC: רק החתימה האחרונה תקפה (הגנת rollback).
    /// </summary>
    public static void Seal(JsonObject cfg)
    {
        string canonical = Canonical(cfg);
        if (UseService)
        {
            var (st, mac, ctr) = ServiceSealSign(Encoding.UTF8.GetBytes(canonical));
            if (st == SealState.Ok && mac != null)
            {
                cfg[SealKey] = Convert.ToBase64String(mac);
                if (ctr != null) cfg[CounterKey] = ctr.Value; else cfg.Remove(CounterKey);
                return;
            }
            if (st != SealState.Unsupported) return;
            // שירות ישן: חתימה רגילה בלי מונה (למטה)
        }
        string m = Sign(canonical);
        if (m != null) { cfg[SealKey] = m; cfg.Remove(CounterKey); }
    }

    private static (SealState, byte[], long?) ServiceSealSign(byte[] data)
    {
        try
        {
            if (!HardLock.Available) return (SealState.NoService, null, null);
            var req = new JsonObject { ["op"] = "config-sign", ["kind"] = "seal", ["data"] = Convert.ToBase64String(data) };
            if (RecentPassword() is string pw) req["password"] = pw;
            var res = HardLock.Call(req, 1500);
            if (res == null) return (SealState.Down, null, null);
            if (res["ok"]?.GetValue<bool>() == true && res["mac"] is JsonNode m)
            {
                long? ctr = res["counter"] is JsonValue cv && cv.TryGetValue<long>(out long c) ? c : null;
                try { return (SealState.Ok, Convert.FromBase64String(m.ToString()), ctr); } catch { return (SealState.Down, null, null); }
            }
            return (ClassifyError(res), null, null);
        }
        catch { return (SealState.Down, null, null); }
    }

    private static string _lastSealGood; // "canonical\nmac\nctr" האחרון שאומת: בנפילת pipe רגעית נשארים עליו (ולא על חתימה ישנה יותר)

    /// <summary>
    /// האם מפתחות האבטחה ב-cfg לא שונו מאז החתימה. כשהחתימה לא נאכפת (אין שירות/שירות ישן): true.
    /// נאכפת וחסרה, לא תואמת או עם מונה ישן (rollback): false, והקורא חייב לחזור לערכי ברירת המחדל המחמירים (fail-closed).
    /// </summary>
    public static bool SecurityIntact(JsonObject cfg)
    {
        if (!Enforced) return true;
        string canonical = Canonical(cfg), mac = cfg?[SealKey]?.ToString();
        if (!UseService) return Verify(canonical, mac);
        long? ctr = cfg?[CounterKey] is JsonValue cv && cv.TryGetValue<long>(out long c) ? c : null;
        string triple = canonical + "\n" + mac + "\n" + ctr;
        var (st, valid) = ServiceVerify(Encoding.UTF8.GetBytes(canonical), mac, true, ctr);
        switch (st)
        {
            case SealState.NoService: return true;
            case SealState.Unsupported: return Verify(canonical, mac);
            case SealState.Down: return mac != null && triple == _lastSealGood;
        }
        if (!valid || string.IsNullOrEmpty(mac)) return false;
        _lastSealGood = triple;
        return true;
    }
}
