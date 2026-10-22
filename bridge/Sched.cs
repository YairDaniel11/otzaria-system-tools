using System.Globalization;

namespace OtzariaBridge;

/// <summary>
/// חלון זמן יומי לנעילה מתוזמנת (גרסה 3.x). תומך בחלון שחוצה חצות, למשל 20:00–02:00:
/// הנעילה פעילה מ-20:00 בערב עד 02:00 בלילה, ובשאר השעות המחשב רגיל.
/// פונקציה טהורה וקלה לבדיקה — אותו היגיון משמש את הגשר (נעילה רכה) ואת השירות (נעילה מוקשחת).
/// </summary>
public static class Sched
{
    /// <summary>מנתח "HH:mm" ל-TimeSpan; null אם אינו תקין</summary>
    public static TimeSpan? ParseTime(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        if (TimeSpan.TryParseExact(s.Trim(), new[] { @"hh\:mm", @"h\:mm" }, CultureInfo.InvariantCulture, out var t)
            && t >= TimeSpan.Zero && t < TimeSpan.FromDays(1))
            return t;
        return null;
    }

    public static bool IsValid(string s) => ParseTime(s) != null;

    /// <summary>
    /// האם 'now' (לפי שעון מקומי) נמצא בחלון [start, end).
    /// start==end או זמן לא תקין → תמיד false (כדי לא לנעול בטעות לכל היום).
    /// start&lt;end → חלון באותו יום; start&gt;end → חלון שחוצה חצות.
    /// </summary>
    public static bool InWindow(string start, string end, DateTime now)
    {
        var s = ParseTime(start);
        var e = ParseTime(end);
        if (s == null || e == null || s == e) return false;
        var t = now.TimeOfDay;
        return s < e ? (t >= s && t < e) : (t >= s || t < e);
    }

    /// <summary>הרגע שבו נגמר החלון הנוכחי (אם 'now' בתוכו), אחרת null</summary>
    public static DateTime? CurrentWindowEnd(string start, string end, DateTime now)
    {
        if (!InWindow(start, end, now)) return null;
        var e = ParseTime(end)!.Value;
        var endToday = now.Date + e;
        return endToday > now ? endToday : endToday.AddDays(1);
    }

    // ---------- אזור זמן (השירות) ----------
    // WHY: התזמון נשמר כשעות-שעון ("20:00"), ולכן שינוי אזור הזמן של Windows (מותר למשתמש רגיל) הזיז את חלון הנעילה
    // ושחרר אותו. השירות שומר את מזהה אזור הזמן ברגע הגדרת התזמון ומחשב לפיו, לא לפי אזור הזמן הנוכחי של המחשב.

    /// <summary>מזהה אזור הזמן הנוכחי של המחשב (עוקף מטמון .NET, שלא מתעדכן בשינוי אזור זמן תוך כדי ריצה של השירות)</summary>
    public static string LocalTzId()
    {
        try { TimeZoneInfo.ClearCachedData(); return TimeZoneInfo.Local.Id; }
        catch { return ""; }
    }

    /// <summary>מפענח מזהה אזור זמן שמור. ריק/לא מוכר → האזור הנוכחי של המחשב (שירות ישן שלא שמר מזהה)</summary>
    public static TimeZoneInfo ResolveTz(string id)
    {
        if (!string.IsNullOrWhiteSpace(id))
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id.Trim()); } catch { }
        }
        try { TimeZoneInfo.ClearCachedData(); } catch { }
        return TimeZoneInfo.Local;
    }

    /// <summary>הזמן המקומי באזור tz ברגע מוחלט נתון</summary>
    public static DateTime LocalIn(TimeZoneInfo tz, DateTimeOffset now) => TimeZoneInfo.ConvertTime(now, tz).DateTime;

    public static bool InWindow(string start, string end, DateTimeOffset now, TimeZoneInfo tz) =>
        InWindow(start, end, LocalIn(tz, now));

    /// <summary>סוף החלון הנוכחי כרגע מוחלט (לפי אזור הזמן של התזמון), או null מחוץ לחלון</summary>
    public static DateTimeOffset? CurrentWindowEnd(string start, string end, DateTimeOffset now, TimeZoneInfo tz)
    {
        var local = LocalIn(tz, now);
        if (CurrentWindowEnd(start, end, local) is not DateTime endLocal) return null;
        endLocal = DateTime.SpecifyKind(endLocal, DateTimeKind.Unspecified);
        if (tz.IsInvalidTime(endLocal)) endLocal = endLocal.AddHours(1); // "חור" של מעבר לשעון קיץ
        return new DateTimeOffset(endLocal, tz.GetUtcOffset(endLocal));
    }
}
