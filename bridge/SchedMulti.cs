using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OtzariaBridge;

/// <summary>C4: חלונות נעילה נוספים ("הוסף זמן") מעבר לחלון הראשי של kiosk.schedule.
/// נשמרים כרשימת "HH:MM-HH:MM" ב-kiosk.schedule.extra. נאכפים בתזמון של הגשר (נעילה רכה)
/// ובשירות (נעילה מוקשחת) - הרשימה מועברת אליו ב-HardLock.SetSchedule.</summary>
internal static class SchedMulti
{
    private const string Key = "kiosk.schedule.extra";

    public static object[] Extra => Config.GetList(Key)
        .Select(s => s.Split('-')).Where(p => p.Length == 2)
        .Select(p => (object)new { start = p[0], end = p[1] }).ToArray();

    /// <summary>"HH:mm-HH:mm,..." של החלונות הנוספים - כך הם מועברים לרכיב ההקשחה (HardLock.SetSchedule)</summary>
    public static string ExtraCsv => string.Join(",", Config.GetList(Key));

    /// <summary>החלון הראשי + הנוספים</summary>
    public static List<(string start, string end)> AllWindows()
    {
        var all = new List<(string, string)> { (Kiosk.ScheduleStart, Kiosk.ScheduleEnd) };
        all.AddRange(Sched.ParseWindows(ExtraCsv));
        return all;
    }

    public static bool InAnyWindow(DateTime now) =>
        AllWindows().Any(w => Sched.InWindow(w.start, w.end, now) && PackG.WindowDayAllowed(w.start, now));

    /// <summary>סוף החלון המתוזמן שבתוכו אנו נמצאים כעת — הרגע שבו המחשב יוצא מכל חלונות הנעילה.
    /// משמש כדי ש"ביטול נעילה" ידני בתוך חלון מתוזמן לא יידרס מיד על ידי התזמון שינעל מחדש.
    /// חלון החוצה חצות מטופל: אם אנו בזנב שאחרי חצות הסוף הוא היום, אחרת מחר.</summary>
    public static DateTime CurrentWindowEnd(DateTime now) =>
        Sched.CurrentWindowEnd(AllWindows().Where(w => PackG.WindowDayAllowed(w.start, now)), now) ?? now;

    public static void Apply(JsonElement sch)
    {
        if (!sch.TryGetProperty("extra", out var ex) || ex.ValueKind != JsonValueKind.Array) return;
        var arr = new JsonArray();
        foreach (var w in ex.EnumerateArray().Take(10))
        {
            string s = w.Str("start"), e = w.Str("end");
            if (!Sched.IsValid(s) || !Sched.IsValid(e)) throw BridgeException.BadRequest(Lang.T("זמן נוסף אינו תקין (HH:MM)", "An additional time is invalid (HH:MM)"));
            arr.Add(s + "-" + e);
        }
        Config.Set(Key, arr);
    }
}
