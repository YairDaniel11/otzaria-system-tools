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

    /// <summary>{ code, mode: "file" | "print", dir } — "file" כותב רק לכונן נשלף (dir = שורש/תיקיה בו); "print" דרך קובץ זמני שנמחק. אין עותק קבוע במחשב</summary>
    private static object Keep(JsonElement b)
    {
        string code = (b.Str("code") ?? "").Trim();
        if (!Kiosk.IsRecoveryCode(code)) throw BridgeException.BadRequest(Lang.T("קוד השחזור אינו תואם", "The recovery code doesn't match"));
        string mode = (b.Str("mode") ?? "file").Trim().ToLowerInvariant();
        if (mode != "file" && mode != "print") throw BridgeException.BadRequest(Lang.T("אפשרות שמירה לא מוכרת", "Unknown keep option"));
        string text = BuildText(code);
        string path = null;
        if (mode == "file")
        {
            string dir = BackupCodes.RemovableTarget(b.Str("dir"), d => { try { return new DriveInfo(d).DriveType; } catch { return DriveType.Unknown; } });
            if (dir == null || !Directory.Exists(dir))
                throw BridgeException.BadRequest(Lang.T("שמירה לקובץ אפשרית רק לכונן חיצוני (נשלף). בחרו כונן מהרשימה, או הדפיסו.", "Saving to a file is possible only to an external (removable) drive. Pick a drive from the list, or print."));
            path = WriteTo(dir, text);
            try { Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), "/select,\"" + path + "\"") { UseShellExecute = false }); }
            catch (Exception ex) { Config.Log("recovery keep: " + ex.Message); }
        }
        else
        {
            string tmpDir = Path.Combine(Path.GetTempPath(), "otzaria-print-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmpDir);
            string tmp = WriteTo(tmpDir, text);
            try { Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "notepad.exe"), "/p \"" + tmp + "\"") { UseShellExecute = false }); }
            catch (Exception ex) { Config.Log("recovery print: " + ex.Message); }
            System.Threading.Tasks.Task.Run(async () =>
            {
                await System.Threading.Tasks.Task.Delay(30_000);
                try { File.WriteAllText(tmp, new string('*', 512)); } catch { }
                try { Directory.Delete(tmpDir, true); } catch { }
            }).Observe("recovery temp cleanup", quiet: true);
        }
        Config.Set(SavedKey, true);
        Config.Save();
        AdminLog.Add(mode == "print" ? "הדפסת קוד שחזור" : "שמירת קוד שחזור לכונן חיצוני");
        return new { ok = true, path };
    }

    private static string WriteTo(string dir, string text)
    {
        string path = Path.Combine(dir, Program.AppName + " - " + Lang.T("קוד שחזור", "recovery code") + ".txt");
        File.WriteAllText(path, text, System.Text.Encoding.UTF8);
        return path;
    }

    internal static string BuildText(string code)
    {
        string raw = code.Replace("-", "");
        string pretty = string.Join("-", Enumerable.Range(0, (raw.Length + 3) / 4).Select(i => raw.Substring(i * 4, Math.Min(4, raw.Length - i * 4))));
        return Program.AppName + " — " + Lang.T("קוד שחזור", "recovery code") + "\r\n\r\n" + pretty + "\r\n\r\n" +
            Lang.T("שמרו את הדף במקום בטוח, לא ליד המחשב.", "Keep this page in a safe place, not next to the computer.") + "\r\n" +
            Lang.T("ביטול נעילה: בחלון \"ביטול נעילה\", או מחלון פקודה:", "To turn off the lock: in the \"Turn off lock\" window, or from a command prompt:") +
            "\r\nOtzariaBridge.exe --unlock " + pretty + "\r\n" +
            "\r\n" + Lang.T("נוצר: ", "Created: ") + DateTime.Now.ToString("yyyy-MM-dd HH:mm") + "\r\n";
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
