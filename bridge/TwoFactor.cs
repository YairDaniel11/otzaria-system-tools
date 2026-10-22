using Windows.Security.Credentials.UI;

namespace OtzariaBridge;

/// <summary>
/// אימות דו-שלבי (גרסה 3.x) דרך Windows Hello — זיהוי פנים, טביעת אצבע או PIN.
/// כשמופעל, פעולות רגישות (ביטול נעילה, שינוי הגדרות הנעילה) דורשות גם את הסיסמה וגם את Hello.
/// קוד השחזור משורת הפקודה (--unlock) פטור תמיד, כדי שתקלת חומרה לא תנעל לצמיתות.
/// </summary>
public static class TwoFactor
{
    // S3: config.json כתיב למשתמש, והעברת kiosk.twoFactor ל-false בקובץ כיבתה את Hello. כשהשירות מחזיק את ההגדרה (HardLock.ServiceTwoFactor,
    // הוא מקור האמת, מסונכרן ב-SetTwoFactor) היא מחייבת גם אם הקובץ אומר false. OR ולא AND: עריכת הקובץ יכולה רק להחמיר.
    public static bool Enabled => Config.Get("kiosk.twoFactor", false) || ServiceSays();

    private static bool ServiceSays()
    {
        try { return HardLock.ServiceTwoFactor; } catch { return false; }
    }

    private static bool _avail;
    private static DateTime _availAt = DateTime.MinValue;

    /// <summary>האם Windows Hello מוגדר וזמין במחשב הזה (במטמון ל-60 שניות)</summary>
    public static bool Available()
    {
        if (DateTime.UtcNow - _availAt < TimeSpan.FromSeconds(60)) return _avail;
        try { _avail = UserConsentVerifier.CheckAvailabilityAsync().GetAwaiter().GetResult() == UserConsentVerifierAvailability.Available; }
        catch { _avail = false; }
        _availAt = DateTime.UtcNow;
        return _avail;
    }

    /// <summary>מציג את חלון Windows Hello ומחזיר אם המשתמש אומת. נכשל בשקט (false) אם אינו זמין/בוטל</summary>
    public static bool Verify(string message)
    {
        try
        {
            if (UserConsentVerifier.CheckAvailabilityAsync().GetAwaiter().GetResult() != UserConsentVerifierAvailability.Available)
                return false;
            Programs.GrantForegroundGrace(); // חלון Hello אינו "תוכנה מותרת" - ר' הערה ב-HardLock.RunHello
            return UserConsentVerifier.RequestVerificationAsync(message).GetAwaiter().GetResult() == UserConsentVerificationResult.Verified;
        }
        catch { return false; }
    }

    /// <summary>דורש Windows Hello כשהאימות הדו-שלבי פעיל; זורק אם נכשל או בוטל</summary>
    public static void Require(string message)
    {
        if (!Enabled) return;
        if (!Verify(message))
            throw new BridgeException("two_factor", "נדרש אימות Windows Hello (זיהוי פנים/טביעת אצבע/PIN) והוא נכשל או בוטל", 401);
    }
}
