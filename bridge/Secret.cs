using System.Security.Cryptography;

namespace OtzariaBridge;

/// <summary>
/// קריפטוגרפיית הסיסמה המשותפת לנעילה הרכה (Kiosk, רצה בהרשאות המשתמש) ולרכיב ההקשחה
/// (HardLockService, רץ כ-SYSTEM). עד כה כל אחד הגדיר עותק נפרד וזהה (Hash/Matches/
/// NormalizeRecovery/NewRecoveryCode) — כפילות שיוצרת סיכון שהם יתפצלו בלי ששמים לב
/// (למשל במספר האיטרציות). שני המקומות שומרים את הסיסמה/קוד השחזור בפורמט ובמיקום משלהם,
/// ולכן המחלקה הזו עוסקת רק בחישוב הקריפטוגרפי הטהור ולא בשמירה/קריאה של ההגדרות.
/// </summary>
public static class Secret
{
    /// <summary>איטרציות לגיבובים חדשים (OWASP 2023 ל-PBKDF2-HMAC-SHA256: 600k+)</summary>
    public const int Iterations = 600_000;
    /// <summary>הערך ההיסטורי: גיבובים ישנים (Base64 חשוף, בלי תחילית גרסה) נבדקים איתו ומשודרגים בכניסה מוצלחת</summary>
    public const int LegacyIterations = 200_000;
    private const string V2 = "v2$";   // תחילית גרסה: "v2$<base64>" = Iterations. בלי תחילית = LegacyIterations

    /// <summary>
    /// גיבוב בפורמט הנוכחי (עם תחילית גרסה). WHY (S3): 200k איטרציות חלשות מול GPU כשהגיבוב נחשף; הגרסה בתוך המחרוזת השמורה
    /// מאפשרת תאימות לאחור בלי שינוי בפורמט האחסון של Kiosk/HardLock (שניהם שומרים את המחרוזת כמות שהיא).
    /// </summary>
    public static string Hash(string secret, byte[] salt) =>
        V2 + Convert.ToBase64String(Rfc2898DeriveBytes.Pbkdf2(secret, salt, Iterations, HashAlgorithmName.SHA256, 32));

    /// <summary>גיבוב בפורמט הישן (לבדיקות ולכלי הגירה בלבד)</summary>
    public static string HashLegacy(string secret, byte[] salt) =>
        Convert.ToBase64String(Rfc2898DeriveBytes.Pbkdf2(secret, salt, LegacyIterations, HashAlgorithmName.SHA256, 32));

    /// <summary>האם הגיבוב השמור בפורמט ישן וצריך להחליף אותו (Hash מחדש) אחרי אימות מוצלח</summary>
    public static bool NeedsUpgrade(string stored) => !string.IsNullOrEmpty(stored) && !stored.StartsWith(V2, StringComparison.Ordinal);

    /// <summary>בודק אם secret תואם לגיבוב/salt שמורים (Base64). false גם כשחסר/לא תקין — לעולם לא זורק</summary>
    public static bool Matches(string secret, string hashB64, string saltB64)
    {
        if (string.IsNullOrEmpty(hashB64) || string.IsNullOrEmpty(saltB64) || string.IsNullOrEmpty(secret)) return false;
        try
        {
            bool v2 = hashB64.StartsWith(V2, StringComparison.Ordinal);
            byte[] expected = Convert.FromBase64String(v2 ? hashB64.Substring(V2.Length) : hashB64);
            byte[] actual = Rfc2898DeriveBytes.Pbkdf2(secret, Convert.FromBase64String(saltB64), v2 ? Iterations : LegacyIterations, HashAlgorithmName.SHA256, 32);
            return CryptographicOperations.FixedTimeEquals(expected, actual);
        }
        catch { return false; } // גיבוב/salt שמורים בפורמט פגום — נחשב "לא תואם", לא קריסה
    }

    /// <summary>מנרמל קוד שחזור שהוקלד: בלי מקפים/רווחים, אותיות גדולות (תואם לאלפבית ב-NewRecoveryCode)</summary>
    public static string NormalizeRecovery(string s) => (s ?? "").Replace("-", "").Replace(" ", "").ToUpperInvariant();

    public static string NewRecoveryCode()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // בלי I/O/0/1 (דמיון חזותי)
        return new string(Enumerable.Range(0, 12).Select(_ => alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)]).ToArray());
    }
}
