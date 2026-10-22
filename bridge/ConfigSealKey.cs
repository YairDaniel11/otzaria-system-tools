using System.Security.Cryptography;
using System.Text;

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

    /// <summary>טוען את המפתח; חסר/באורך שגוי: יוצר חדש (כתיבה אטומית: זמני + החלפה). מפתח חדש מבטל חתימות קיימות (fail-closed; חותמים מחדש בשמירת הגדרות)</summary>
    public static byte[] LoadOrCreate(string path, Action<string> log = null)
    {
        try
        {
            if (File.Exists(path))
            {
                var k = File.ReadAllBytes(path);
                if (k.Length == KeyLength) return k;
                log?.Invoke("config-seal: קובץ המפתח באורך שגוי; נוצר מפתח חדש");
            }
        }
        catch (Exception ex) { log?.Invoke("config-seal: קריאת מפתח נכשלה: " + ex.Message); }
        var key = RandomNumberGenerator.GetBytes(KeyLength);
        string tmp = path + ".tmp";
        File.WriteAllBytes(tmp, key);
        File.Move(tmp, path, true);
        return key;
    }

    /// <summary>
    /// מי רשאי לבקש חתימה. הצינור פתוח לכל משתמש מאומת, ולכן חתימה על נתונים שרירותיים = זיוף. מורשים: מנהל מורם, או תהליך שנתיב ה-exe שלו
    /// הוא ה-exe של השירות (Program Files, לא כתיב למשתמש); כשהנעילה נאכפת — רק גשר שהשירות עצמו הפעיל (מוגן DACL).
    /// </summary>
    public static bool ClientMaySign(bool admin, bool pathIsServiceExe, bool ownBridge, bool enforced) =>
        admin || (pathIsServiceExe && (!enforced || ownBridge));
}
