using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OtzariaBridge;

/// <summary>סבב G: חובת שמירת קוד השחזור (קובץ/הדפסה) לפני הפעלת נעילה, וימי שבוע לזמני הנעילה.</summary>
internal static class PackG
{
    public const string SavedKey = "kiosk.recoverySaved";
    public const string DaysKey = "kiosk.schedule.days";

    public static void Register()
    {
        Server.Route("/kiosk/recovery/keep", Keep);
    }

    /// <summary>מותקנות קיימות (בלי המפתח) נחשבות כאילו נשמר — הדרישה חלה על קוד חדש בלבד</summary>
    public static bool RecoverySaved => Config.Get(SavedKey, true);

    public static void MarkNewCode() { Config.Set(SavedKey, false); }

    /// <summary>{ code, mode: "file" | "print" } — שומר את הקוד לקובץ במסמכים (ו/או מדפיס) ומסמן שנשמר</summary>
    private static object Keep(JsonElement b)
    {
        string code = (b.Str("code") ?? "").Trim();
        if (!Kiosk.IsRecoveryCode(code)) throw BridgeException.BadRequest(Lang.T("קוד השחזור אינו תואם", "The recovery code doesn't match"));
        string pretty = string.Join("-", Enumerable.Range(0, (code.Replace("-", "").Length + 3) / 4)
            .Select(i => code.Replace("-", "").Substring(i * 4, Math.Min(4, code.Replace("-", "").Length - i * 4))));
        string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        string path = Path.Combine(docs, Program.AppName + " - " + Lang.T("קוד שחזור", "recovery code") + ".txt");
        File.WriteAllText(path,
            Program.AppName + " — " + Lang.T("קוד שחזור", "recovery code") + "\r\n\r\n" + pretty + "\r\n\r\n" +
            Lang.T("שמרו את הדף במקום בטוח, לא ליד המחשב.", "Keep this page in a safe place, not next to the computer.") + "\r\n" +
            Lang.T("ביטול נעילה: בחלון \"ביטול נעילה\", או מחלון פקודה:", "To turn off the lock: in the \"Turn off lock\" window, or from a command prompt:") +
            "\r\nOtzariaBridge.exe --unlock " + pretty + "\r\n" +
            "\r\n" + Lang.T("נוצר: ", "Created: ") + DateTime.Now.ToString("yyyy-MM-dd HH:mm") + "\r\n", System.Text.Encoding.UTF8);
        string mode = b.Str("mode") ?? "file";
        try
        {
            if (mode == "print") Process.Start(new ProcessStartInfo("notepad.exe", "/p \"" + path + "\"") { UseShellExecute = false });
            else Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"") { UseShellExecute = false });
        }
        catch (Exception ex) { Config.Log("recovery keep: " + ex.Message); }
        Config.Set(SavedKey, true);
        Config.Save();
        AdminLog.Add(mode == "print" ? "הדפסת קוד שחזור" : "שמירת קוד שחזור לקובץ");
        return new { ok = true, path };
    }

    // ---------- ימי שבוע לזמני נעילה (0 = ראשון … 6 = שבת). רשימה ריקה = כל הימים ----------

    public static int[] Days => Config.GetList(DaysKey).Select(s => int.TryParse(s, out var d) ? d : -1).Where(d => d is >= 0 and <= 6).Distinct().ToArray();

    public static bool DayAllowed(DateTime day) { var d = Days; return d.Length == 0 || d.Contains((int)day.DayOfWeek); }

    /// <summary>חלון שהתחיל אתמול ונמשך אחרי חצות שייך ליום שבו התחיל</summary>
    public static bool WindowDayAllowed(string start, DateTime now)
    {
        var t = Sched.ParseTime(start);
        if (t == null) return true;
        return DayAllowed(now.TimeOfDay >= t.Value ? now.Date : now.Date.AddDays(-1));
    }

    public static void ApplyDays(JsonElement sch)
    {
        if (!sch.TryGetProperty("days", out var ds) || ds.ValueKind != JsonValueKind.Array) return;
        var arr = new JsonArray();
        foreach (var d in ds.EnumerateArray())
            if (d.ValueKind == JsonValueKind.Number && d.TryGetInt32(out var n) && n is >= 0 and <= 6 && !arr.Any(x => x!.ToString() == n.ToString())) arr.Add(n.ToString());
        Config.Set(DaysKey, arr.Count == 7 ? new JsonArray() : arr);
    }
}
