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
/// Down = השירות מותקן ולא עונה (fail-closed: לא מקבלים נתון לא מאומת); Ok = חתימה זמינה.
/// נקודת חיבור נדרשת בשירות (HardLock.cs, לא נערך כאן): ר' ההצעה בדוח, פקודת pipe "config-sign" שמחזירה {ok:true, mac:base64}
/// = HMAC-SHA256(מפתח שירות אקראי השמור ב-ProgramData, "otzaria-config-seal-v1|" + base64(data)).
/// </summary>
public static class ConfigSeal
{
    public enum SealState { NoService, Unsupported, Down, Ok }

    /// <summary>ספק ה-MAC. ברירת מחדל: השירות דרך ה-pipe. בבדיקות מוחלף.</summary>
    public static Func<byte[], (SealState state, byte[] mac)> Provider = ServiceProvider;

    private static readonly ConcurrentDictionary<string, bool> Good = new();
    private static SealState _probe = SealState.NoService;
    private static DateTime _probeAt = DateTime.MinValue;

    private static (SealState, byte[]) ServiceProvider(byte[] data)
    {
        try
        {
            if (!HardLock.Available) return (SealState.NoService, null);
            var res = HardLock.Call(new JsonObject { ["op"] = "config-sign", ["data"] = Convert.ToBase64String(data) }, 1500);
            if (res == null) return (SealState.Down, null);
            if (res["ok"]?.GetValue<bool>() == true && res["mac"] is JsonNode m)
            {
                try { return (SealState.Ok, Convert.FromBase64String(m.ToString())); } catch { return (SealState.Down, null); }
            }
            return (SealState.Unsupported, null);   // השירות ענה אבל לא מכיר את הפקודה
        }
        catch { return (SealState.Down, null); }
    }

    /// <summary>האם החתימה נאכפת: שירות מותקן שתומך בפקודה (או מותקן ולא עונה: סגור). במטמון 30 שניות</summary>
    public static bool Enforced
    {
        get
        {
            if (DateTime.UtcNow - _probeAt > TimeSpan.FromSeconds(30))
            {
                _probe = Provider(Encoding.UTF8.GetBytes("probe")).state;
                _probeAt = DateTime.UtcNow;
            }
            return _probe is SealState.Ok or SealState.Down;
        }
    }

    /// <summary>מאפס את מטמוני החתימה/הזמינות (בדיקות, או אחרי התקנה/הסרה של השירות)</summary>
    public static void Reset() { Good.Clear(); _probeAt = DateTime.MinValue; }

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
        var (st, mac) = Provider(Encoding.UTF8.GetBytes(canonical ?? ""));
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

    public const string SealKey = "sec.seal";

    /// <summary>ייצוג קנוני יציב של ערכי מפתחות האבטחה (ממוין, JSON קומפקטי; מפתח חסר = null)</summary>
    public static string Canonical(JsonObject cfg)
    {
        var sb = new StringBuilder("otzaria-config-seal-v1\n");
        foreach (string k in SecurityKeys.OrderBy(x => x, StringComparer.Ordinal))
            sb.Append(k).Append('=').Append(cfg?[k]?.ToJsonString() ?? "null").Append('\n');
        return sb.ToString();
    }

    /// <summary>חותם את מצב מפתחות האבטחה הנוכחי ושומר ב-cfg[SealKey]. נקרא מקוד הנעילה אחרי שמירה לגיטימית של הגדרות</summary>
    public static void Seal(JsonObject cfg)
    {
        string mac = Sign(Canonical(cfg));
        if (mac != null) cfg[SealKey] = mac;
    }

    /// <summary>
    /// האם מפתחות האבטחה ב-cfg לא שונו מאז החתימה. כשהחתימה לא נאכפת (אין שירות/שירות ישן): true.
    /// נאכפת וחסרה או לא תואמת: false, והקורא חייב לחזור לערכי ברירת המחדל המחמירים (fail-closed).
    /// </summary>
    public static bool SecurityIntact(JsonObject cfg)
    {
        if (!Enforced) return true;
        return Verify(Canonical(cfg), cfg?[SealKey]?.ToString());
    }
}
