using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace OtzariaBridge;

/// <summary>
/// צד השירות של ConfigSeal (פקודת pipe "config-sign"): מפתח HMAC אקראי שנשמר ב-ProgramData (SecureDir: SYSTEM/מנהלים בלבד)
/// והחישוב עצמו. WHY (T1): config.json כתיב למשתמש, ולכן החתימה על מפתחות האבטחה חייבת להיעשות במפתח שהמשתמש לא יכול לקרוא.
/// הפורמט תואם ל-ConfigSeal: HMAC-SHA256(key, "otzaria-config-seal-v1|" + base64(data)).
/// </summary>
internal static class ConfigSealKey
{
    public const string Prefix = "otzaria-config-seal-v1|";
    public const int MaxData = 16 * 1024;
    public const int KeyLength = 32;

    public static byte[] Mac(byte[] key, byte[] data) =>
        HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(Prefix + Convert.ToBase64String(data)));

    /// <summary>
    /// MAC עם מונה: HMAC(key, Prefix + base64(data) + "|ctr=" + n). WHY (U1): בלי מונה, חתימה ישנה תקפה לנצח ואפשר להחזיר config.json
    /// ישן (rollback) שהיה חתום כדין; עם מונה שהשירות מחזיק, רק החתימה האחרונה תקפה.
    /// </summary>
    public static byte[] MacWithCounter(byte[] key, byte[] data, long counter) =>
        HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(Prefix + Convert.ToBase64String(data) + "|ctr=" + counter));

    /// <summary>
    /// האם מונה שהוצג ב-config מתקבל מול המונה הנוכחי בשירות. presented=null (config בלי מונה = חתום לפני העדכון): רק כשהשירות עוד לא הנפיק מונה (0).
    /// מונה מוצג חייב להיות שווה בדיוק לנוכחי (ישן = rollback = נדחה; עתידי = זיוף)
    /// </summary>
    public static bool CounterAccepts(long current, long? presented) =>
        presented == null ? current == 0 : (presented.Value > 0 && presented.Value == current);

    /// <summary>
    /// qa-final-a #8: מונה לכל זהות (SID של הלקוח מהצינור): config.json הוא לכל פרופיל, ומונה גלובלי יחיד גרם לחתימה של חשבון אחד לפסול את זו של האחר.
    /// ללא מונה פר-SID קיים: נופלים למונה הגלובלי הישן (חתימות שהונפקו לפני העדכון נשארות תקפות)
    /// </summary>
    public static long CounterFor(JsonObject state, string sid)
    {
        if (!string.IsNullOrEmpty(sid) && state?["sealCounters"] is JsonObject per && per[sid] is JsonValue pv && pv.TryGetValue<long>(out long pl)) return pl;
        return state?["sealCounter"] is JsonValue gv && gv.TryGetValue<long>(out long gl) ? gl : 0;
    }

    /// <summary>מקדם את המונה של הזהות ורושם אותו במצב (השמירה לדיסק באחריות הקורא). sid ריק: המונה הגלובלי</summary>
    public static long Advance(JsonObject state, string sid)
    {
        long next = CounterFor(state, sid) + 1;
        if (string.IsNullOrEmpty(sid)) state["sealCounter"] = next;
        else { var per = state["sealCounters"] as JsonObject ?? new JsonObject(); per[sid] = next; state["sealCounters"] = per; }
        return next;
    }

    /// <summary>
    /// טוען את המפתח. חסר/באורך שגוי: יוצר חדש (כתיבה אטומית: זמני + החלפה); מפתח חדש מבטל חתימות קיימות (fail-closed; חותמים מחדש בשמירת הגדרות).
    /// WHY (U1): שגיאת IO זמנית (נעילת קובץ ע"י AV/גיבוי) לא אמורה לדרוס מפתח תקין ולהפיל את כל החתימות: במקרה כזה מנסים שוב וזורקים, בלי ליצור חדש.
    /// </summary>
    public static byte[] LoadOrCreate(string path, Action<string> log = null)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                if (!File.Exists(path)) break;
                var k = File.ReadAllBytes(path);
                if (k.Length == KeyLength) return k;
                log?.Invoke("config-seal: קובץ המפתח באורך שגוי; נוצר מפתח חדש");
                break;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log?.Invoke("config-seal: קריאת מפתח נכשלה (ניסיון " + (attempt + 1) + "): " + ex.Message);
                if (attempt >= 3) throw; // לא דורסים מפתח שאולי תקין
                Thread.Sleep(150);
            }
        }
        var key = RandomNumberGenerator.GetBytes(KeyLength);
        string tmp = path + ".tmp";
        File.WriteAllBytes(tmp, key);
        File.Move(tmp, path, true);
        return key;
    }

    /// <summary>
    /// מי רשאי לבקש חתימה. הצינור פתוח לכל משתמש מאומת, ולכן חתימה על נתונים שרירותיים = זיוף. מורשים: מנהל מורם; גשר שהשירות עצמו הפעיל (מוגן DACL);
    /// או תהליך מנתיב ה-exe של השירות שהציג סיסמת נעילה נכונה בבקשה. WHY (U1): "נתיב = ServiceExe" לבדו אינו הוכחה (DOTNET_STARTUP_HOOKS ב-HKCU
    /// מריץ קוד זר בתוך exe תקין), וחתימה שנעשתה כשהנעילה לא נאכפת נשארת תקפה אחר כך.
    /// </summary>
    public static bool ClientMaySign(bool admin, bool pathIsServiceExe, bool ownBridge, bool passwordOk) =>
        admin || ownBridge || (pathIsServiceExe && passwordOk);
}
