using System.Text.Json;

namespace OtzariaBridge;

/// <summary>
/// מצב תחזוקה: חלון זמן קצר (30 דקות) שבו שומרי הנעילה של הגשר מושהים כדי שאפשר יהיה לסגור ולעדכן את אוצריא
/// (בלי להרוג תהליכים). נפתח בסיסמת הנעילה (אותה השהיית ניסיונות ואותו Windows Hello כמו ביטול נעילה), נשמר בזיכרון
/// בלבד על שעון מונוטוני — הפעלה מחדש של הגשר, שינוי שעון או תום הזמן מחזירים את הנעילה אוטומטית (כשל סגור).
/// ההיגיון הטהור כאן; החיבור לשומרים ב-KioskMaintenance.cs.
/// </summary>
public static class Maintenance
{
    public const int Minutes = 30;

    public static long EndTick(long nowTickMs) => nowTickMs + Minutes * 60_000L;

    /// <summary>שניות שנותרו (0 = לא פעיל / הסתיים / ערך לא סביר). endTick==0 = לא הופעל</summary>
    public static int SecondsLeft(long endTick, long nowTickMs)
    {
        if (endTick == 0) return 0;
        long left = endTick - nowTickMs;
        if (left <= 0 || left > (Minutes + 1) * 60_000L) return 0;
        return (int)Math.Ceiling(left / 1000.0);
    }
}

public static partial class Kiosk
{
    private static long _maintEnd;

    /// <summary>שניות שנותרו למצב תחזוקה (0 = לא פעיל)</summary>
    public static int MaintenanceSecondsLeft() => Maintenance.SecondsLeft(Interlocked.Read(ref _maintEnd), Environment.TickCount64);

    public static bool MaintenanceActive => MaintenanceSecondsLeft() > 0;

    /// <summary>
    /// פותח מצב תחזוקה בסיסמת הנעילה הרגילה (לא קוד שחזור). דורש נעילה פעילה; אימות עם הגבלת ניסיונות
    /// ו-Windows Hello כשהאימות הדו-שלבי פעיל — בדיוק כמו ביטול נעילה. זורק BridgeException בכשל.
    /// </summary>
    public static void StartMaintenance(string password)
    {
        if (!IsActive && !HardLock.ServiceActive) throw BridgeException.BadRequest(Lang.T("המחשב אינו נעול", "The computer is not locked"));
        Verify(password, allowRecovery: false);
        TwoFactor.Require("מצב תחזוקה");
        AdminLog.Add("פתיחת מצב תחזוקה (" + Maintenance.Minutes + " דקות)");
        Interlocked.Exchange(ref _maintEnd, Maintenance.EndTick(Environment.TickCount64));
        try { UnblockClose(); } catch { }
        Overlay.HideRest();
        Overlay.HideCovers();
        Config.Log("maintenance: מצב תחזוקה נפתח ל-" + Maintenance.Minutes + " דקות");
        Tray.Notify(Program.AppName, Lang.T("מצב תחזוקה: " + Maintenance.Minutes + " דקות לסגירה/עדכון של אוצריא, ואז הנעילה חוזרת.",
            "Maintenance mode: " + Maintenance.Minutes + " minutes to close/update Otzaria, then the lock returns."), ToolTipIcon.Info);
        Tray.RefreshStatus();
    }

    /// <summary>סיום מוקדם (הכיוון הבטוח — בלי סיסמה)</summary>
    public static void EndMaintenance()
    {
        if (Interlocked.Exchange(ref _maintEnd, 0) != 0) Config.Log("maintenance: הסתיים ידנית — הנעילה חוזרת");
        Tray.RefreshStatus();
    }

    /// <summary>נקרא מהשומרים: true = מושהים. בתום הזמן מנקה את המצב ומודיע פעם אחת</summary>
    private static bool MaintenanceHolds()
    {
        long end = Interlocked.Read(ref _maintEnd);
        if (end == 0) return false;
        if (Maintenance.SecondsLeft(end, Environment.TickCount64) > 0) return true;
        if (Interlocked.CompareExchange(ref _maintEnd, 0, end) == end)
        {
            Config.Log("maintenance: הזמן הסתיים — הנעילה חוזרת");
            Tray.Notify(Program.AppName, Lang.T("מצב התחזוקה הסתיים. הנעילה חוזרת.", "Maintenance mode ended. The lock is back."), ToolTipIcon.Info);
            Tray.RefreshStatus();
        }
        return false;
    }

    /// <summary>ביטול נעילה בסיסמה (אותו מסלול כמו /kiosk/disable: אימות, הגבלת ניסיונות, Hello, קוד שחזור/גיבוי) — בלי תלות בתוספים</summary>
    public static void UnlockWithPassword(string password)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new { password = password ?? "" }));
        Disable(doc.RootElement.Clone());
    }
}
