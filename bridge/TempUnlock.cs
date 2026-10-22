using System.Globalization;

namespace OtzariaBridge;

/// <summary>
/// פתיחה זמנית של כל הנעילה, בסיסמה נפרדת מסיסמת הנעילה הראשית — היגיון טהור וקל לבדיקה,
/// משותף לגשר (Kiosk, בלי רכיב הקשחה) ולרכיב ההקשחה (HardLockService, מקור האמת כשהוא מותקן).
/// משך הפתיחה קבוע (מוגדר מראש בהגדרות, לא נבחר בכל פעם) ומוגבל ל-60 דקות.
/// זמן הסיום נשמר כרגע מוחלט (DateTimeOffset בפורמט "o"), כך שמעבר שעון קיץ/חורף לא מקצר ולא מאריך אותו.
/// </summary>
public static class TempUnlock
{
    public const int MinMinutes = 1, MaxMinutes = 60, DefaultMinutes = 30;

    /// <summary>אורך מינימלי לסיסמת הפתיחה הזמנית (כמו סיסמת הנעילה)</summary>
    public const int MinSecretLength = 6;

    /// <summary>משך הפתיחה מוגבל ל-1–60 דקות</summary>
    public static int ClampMinutes(long minutes) => (int)Math.Clamp(minutes, MinMinutes, MaxMinutes);

    /// <summary>זמן הסיום של פתיחה שמתחילה עכשיו (המשך מוגבל ל-1–60 דקות)</summary>
    public static DateTimeOffset Until(DateTimeOffset now, long minutes) => now.AddMinutes(ClampMinutes(minutes));

    /// <summary>מפענח זמן סיום שנשמר; null אם ריק או לא תקין</summary>
    public static DateTimeOffset? ParseUntil(string iso)
    {
        if (string.IsNullOrWhiteSpace(iso)) return null;
        return DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var d) ? d : null;
    }

    /// <summary>
    /// כמה שניות נשארו לפתיחה (0 = לא פעילה). זמן סיום שרחוק יותר מהמשך המרבי (60 דקות ועוד דקת חסד)
    /// אינו יכול להיווצר בדרך הרגילה — למשל ערך שנערך ידנית או שעון שהוזז אחורה — ולכן נחשב פג: כשל סגור, הנעילה חוזרת.
    /// </summary>
    public static int SecondsLeft(DateTimeOffset? until, DateTimeOffset now)
    {
        if (until == null) return 0;
        var left = until.Value - now;
        if (left <= TimeSpan.Zero || left > TimeSpan.FromMinutes(MaxMinutes + 1)) return 0;
        return (int)Math.Ceiling(left.TotalSeconds);
    }

    public static int SecondsLeft(string iso, DateTimeOffset now) => SecondsLeft(ParseUntil(iso), now);

    /// <summary>תצוגת זמן שנותר: "m:ss" (למשל 29:05)</summary>
    public static string Format(int seconds)
    {
        if (seconds < 0) seconds = 0;
        return (seconds / 60) + ":" + (seconds % 60).ToString("00", CultureInfo.InvariantCulture);
    }

    // ---------- זמן מונוטוני (השירות) ----------
    // WHY: זמן הסיום נשמר כרגע מוחלט (שעון קיר). הזזת שעון אחורה (או אזור זמן/NTP זדוני) האריכה פתיחה זמנית.
    // השירות מחזיק בנוסף נקודת סיום מונוטונית (TickCount64) ובודק קפיצות שעון; הפתיחה לא נשמרת אחרי אתחול/הפעלה מחדש.

    /// <summary>סטייה בין שעון הקיר לשעון המונוטוני שמעבר לה הפתיחה מבוטלת (2 דקות)</summary>
    public const long ClockJumpToleranceMs = 120_000;

    /// <summary>נקודת סיום מונוטונית (TickCount64) לפתיחה שמתחילה עכשיו</summary>
    public static long EndTick(long nowTickMs, long minutes) => nowTickMs + ClampMinutes(minutes) * 60_000L;

    /// <summary>שניות שנותרו לפי השעון המונוטוני. endTick==0 (לא הוגדר, למשל אחרי הפעלת שירות) → 0, כלומר נעול. חסום ל-61 דקות</summary>
    public static int MonoSecondsLeft(long endTick, long nowTickMs)
    {
        if (endTick == 0) return 0;
        long leftMs = endTick - nowTickMs;
        if (leftMs <= 0 || leftMs > (MaxMinutes + 1) * 60_000L) return 0;
        return (int)Math.Ceiling(leftMs / 1000.0);
    }
}

/// <summary>מזהה קפיצת שעון קיר ביחס לשעון מונוטוני בין שתי דגימות רצופות (לא תקין לשימוש משני חוטים בלי נעילה)</summary>
public sealed class ClockGuard
{
    private bool _has;
    private long _wall, _mono;

    /// <summary>true אם בין הדגימה הקודמת לזו שעון הקיר התקדם שונה מהמונוטוני ביותר מ-tolerance. הדגימה הראשונה — false</summary>
    public bool Sample(long wallMs, long monoMs, long toleranceMs = TempUnlock.ClockJumpToleranceMs)
    {
        bool jumped = _has && Math.Abs((wallMs - _wall) - (monoMs - _mono)) > toleranceMs;
        _has = true; _wall = wallMs; _mono = monoMs;
        return jumped;
    }
}
