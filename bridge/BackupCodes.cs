using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OtzariaBridge;

/// <summary>
/// #69: קודי גיבוי חד-פעמיים לשחזור (כמו בגוגל) — בנוסף לקוד השחזור הראשי של המחשב, לא במקומו.
/// 10 קודים, כל אחד נמחק אחרי שימוש. מתאימים רק ל"שכחתי": ביטול הנעילה (גם Ctrl+Alt+Shift+F12 ו---unlock),
/// ואיפוס הסיסמה. לפעולות ניהול אחרות (מדיניות, ייבוא, BIOS) נדרשים הסיסמה או הקוד הראשי — כך קוד גיבוי
/// שדלף מאפשר לכל היותר ביטול נעילה אחד, ונשרף.
/// אין כאן "קוד-אב": כל קוד ייחודי למחשב, נוצר אקראית ונשמר רק כגיבוב (PBKDF2, Secret.cs).
/// כשרכיב ההקשחה מותקן — הגיבובים נשמרים גם אצלו (מקור האמת, op=set-backup), והוא שורף קוד שנוצל.
/// ר' docs/recovery-design.md.
/// </summary>
internal static class BackupCodes
{
    public const string Key = "kiosk.recoveryBackupHashes";   // "recovery" בשם — חסום בייצוא הגדרות ובדוחות
    public const int Count = 10;

    public static void Register()
    {
        Server.Route("/kiosk/recovery/backup", Generate);          // { password, mode?: "none"(ברירת מחדל)|"print"|"file"+dir על כונן נשלף }
        Server.Route("/kiosk/recovery/backup/status", _ => new { left = Left });
    }

    /// <summary>כמה קודי גיבוי שלא נוצלו נשארו</summary>
    public static int Left => ServiceHolds ? (HardLock.ServiceBackupLeft() ?? 0) : Config.GetList(Key).Length;

    /// <summary>
    /// F2 WHY: כשהשירות (SYSTEM) מותקן ועונה, הוא מקור האמת לקודי הגיבוי. config.json כתיב למשתמש: תוקף שכותב לשם גיבוב
    /// של קוד ידוע היה עובר את הבדיקה המקומית (דילוג על Verify/2FA). שירות ישן שלא מדווח backupLeft: נשארים עם העותק המקומי.
    /// </summary>
    internal static bool ServiceHolds => HardLock.Available && HardLock.ServiceBackupLeft() != null;

    /// <summary>מחיקת העותק המקומי כשהשירות מחזיק רשימה (נקרא מ-Kiosk.PurgeLocalPassword). true = נמחק</summary>
    internal static bool PurgeLocal()
    {
        if (Config.GetList(Key).Length == 0 || !ServiceHolds || (HardLock.ServiceBackupLeft() ?? 0) <= 0) return false;
        Config.Remove(Key);
        return true;
    }

    /// <summary>"salt|hash" לכל קוד</summary>
    private static string Pack(string code)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        return Convert.ToBase64String(salt) + "|" + Secret.Hash(Secret.NormalizeRecovery(code), salt);
    }

    private static int IndexOf(string code)
    {
        string n = Secret.NormalizeRecovery(code);
        if (n.Length != 12) return -1;           // לא נראה כמו קוד — לא מבזבזים 10 חישובי PBKDF2
        var list = Config.GetList(Key);
        for (int i = 0; i < list.Length; i++)
        {
            var parts = list[i].Split('|');
            if (parts.Length == 2 && Secret.Matches(n, parts[1], parts[0])) return i;
        }
        return -1;
    }

    /// <summary>האם זה קוד גיבוי בתוקף (בלי לשרוף). שימו לב — לא נבדק מול רכיב ההקשחה; הוא בודק בעצמו</summary>
    public static bool Matches(string code)
    {
        if (ServiceHolds) return Secret.NormalizeRecovery(code).Length == 12 && HardLock.IsBackupCode(code);
        // שירות מותקן ורץ אבל לא עונה/מופסק-לא-באמת: fail-closed, בלי עותק מקומי. רק בלי שירות או כשהוא מופסק ב-SCM (מסלול חירום)
        if (HardLock.ServiceInstalled() && !Kiosk.ServiceStopped()) return false;
        return IndexOf(code) >= 0;
    }

    /// <summary>שורף את הקוד (אחרי שימוש מוצלח). מחזיר false אם לא היה קוד כזה</summary>
    public static bool Consume(string code)
    {
        if (ServiceHolds) return false; // השירות שורף בעצמו בזמן disable/set-password; העותק המקומי לא רלוונטי
        int i = IndexOf(code);
        if (i < 0) return false;
        var list = Config.GetList(Key).ToList();
        list.RemoveAt(i);
        Config.Set(Key, new JsonArray(list.Select(s => (JsonNode)JsonValue.Create(s)).ToArray()));
        Config.Save();
        AdminLog.Add("נוצל קוד גיבוי לשחזור (נשארו " + list.Count + ")");
        Config.Log("recovery: נוצל קוד גיבוי חד-פעמי, נשארו " + list.Count);
        return true;
    }

    /// <summary>יוצר 10 קודים חדשים (מחליף את הקודמים), שומר לקובץ/מדפיס, ומחזיר אותם פעם אחת בלבד</summary>
    private static object Generate(JsonElement b)
    {
        if (!Kiosk.PasswordConfigured) throw BridgeException.BadRequest(Lang.T("יש להגדיר סיסמת נעילה לפני יצירת קודי גיבוי", "Set a lock password before creating backup codes"));
        string pw = b.Str("password");
        Kiosk.RequirePasswordOrRecovery(pw);
        if (!Kiosk.IsRecoveryCode(pw ?? "")) TwoFactor.Require(Lang.T("יצירת קודי גיבוי לשחזור", "Creating backup recovery codes"));

        var codes = Enumerable.Range(0, Count).Select(_ => Secret.NewRecoveryCode()).ToArray();
        var packed = codes.Select(Pack).ToArray();
        // כשרכיב ההקשחה מותקן הוא מקור האמת — אם השמירה שם נכשלת, לא משנים כלום כאן
        // F2: שירות מותקן שלא עונה => נכשלים (אחרת הקודים שהוצגו לא היו עובדים בשירות); ובשירות לא שומרים עותק מקומי
        if (!HardLock.Available && HardLock.ServiceInstalled())
            throw new BridgeException("unsupported", Lang.T("רכיב ההקשחה לא מגיב; אי אפשר ליצור קודי גיבוי כרגע", "The hardening service is not responding; cannot create backup codes now"), 503);
        if (HardLock.Available)
        {
            HardLock.SetBackupCodes(pw, packed);
            Config.Remove(Key);
        }
        else Config.Set(Key, new JsonArray(packed.Select(s => (JsonNode)JsonValue.Create(s)).ToArray()));
        Config.Save();
        AdminLog.Add("נוצרו " + Count + " קודי גיבוי לשחזור (הקודמים בוטלו)");

        // WHY (qa-final-a #5): ברירת המחדל כבר לא קובץ ב"מסמכים" (קריא לאותו חשבון Windows שהנעילה נועדה נגדו, ונשאר בענן/גיבוי).
        // הקודים מוצגים פעם אחת בחלון; הדפסה עוברת דרך קובץ זמני שנמחק; קובץ רק לכונן נשלף
        string path = null;
        string mode = ResolveMode(b.Str("mode"));
        string removableDir = mode == "file" ? RemovableTarget(b.Str("dir"), d => { try { return new DriveInfo(d).DriveType; } catch { return DriveType.Unknown; } }) : null;
        if (mode == "file" && removableDir == null)
        {
            Config.Log("backup codes: שמירה לקובץ נדרשת כונן נשלף (dir) — הקודים הוצגו בלבד");
            mode = "none";
        }
        if (mode == "print")
        {
            string tmpDir = Path.Combine(Path.GetTempPath(), "otzaria-print-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmpDir);
            string tmp = WriteFile(codes, tmpDir);
            try { Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "notepad.exe"), "/p \"" + tmp + "\"") { UseShellExecute = false }); }
            catch (Exception ex) { Config.Log("backup codes print: " + ex.Message); }
            // הדפסה אסינכרונית: מחכים ואז מוחקים (דריסה לפני מחיקה, כדי שלא יישאר בשבבי דיסק קריאים בקלות)
            System.Threading.Tasks.Task.Run(async () =>
            {
                await System.Threading.Tasks.Task.Delay(30_000);
                try { File.WriteAllText(tmp, new string('*', 512)); } catch { }
                try { Directory.Delete(tmpDir, true); } catch { }
            }).Observe("backup codes temp cleanup", quiet: true);
        }
        else if (mode == "file")
        {
            path = WriteFile(codes, removableDir);
            try { Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), "/select,\"" + path + "\"") { UseShellExecute = false }); }
            catch (Exception ex) { Config.Log("backup codes open: " + ex.Message); }
        }
        return new { ok = true, codes = codes.Select(Pretty).ToArray(), path, left = codes.Length };
    }

    public static string Pretty(string code)
    {
        string c = Secret.NormalizeRecovery(code);
        return string.Join("-", Enumerable.Range(0, (c.Length + 3) / 4).Select(i => c.Substring(i * 4, Math.Min(4, c.Length - i * 4))));
    }

    /// <summary>none (ברירת מחדל, גם לערך לא מוכר), print, file. "file" חוקי רק עם dir על כונן נשלף (RemovableTarget)</summary>
    internal static string ResolveMode(string mode) => (mode ?? "").Trim().ToLowerInvariant() switch { "print" => "print", "file" => "file", _ => "none" };

    /// <summary>תיקיית יעד מאושרת לקובץ קודים: רק שורש/תיקיה על כונן נשלף קיים. null = לא מאושר. typeOf מקבל שורש כונן ("E:\")</summary>
    internal static string RemovableTarget(string dir, Func<string, DriveType> typeOf)
    {
        if (string.IsNullOrWhiteSpace(dir)) return null;
        try
        {
            string full = Path.GetFullPath(dir);
            string root = Path.GetPathRoot(full);
            if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal)) return null;
            return typeOf(root) == DriveType.Removable ? full : null;
        }
        catch { return null; }
    }

    private static string WriteFile(string[] codes, string dir)
    {
        string path = Path.Combine(dir, Program.AppName + " - " + Lang.T("קודי גיבוי לשחזור", "backup recovery codes") + ".txt");
        var sb = new StringBuilder();
        sb.Append(Program.AppName).Append(" — ").Append(Lang.T("קודי גיבוי חד-פעמיים לשחזור", "One-time backup recovery codes")).Append("\r\n\r\n");
        for (int i = 0; i < codes.Length; i++) sb.Append($"{i + 1,2}.  ").Append(Pretty(codes[i])).Append("\r\n");
        sb.Append("\r\n").Append(Lang.T(
            "כל קוד עובד פעם אחת בלבד, ונמחק אחרי השימוש. מתאים לביטול הנעילה (גם Ctrl+Alt+Shift+F12) ולאיפוס סיסמה שנשכחה.\r\n" +
            "יצירת קודים חדשים מבטלת את כל הקודים שבדף הזה. קוד השחזור הראשי ממשיך לעבוד כרגיל.\r\n" +
            "שמרו את הדף במקום בטוח, לא ליד המחשב.\r\n",
            "Each code works once only and is deleted after use. Use it to turn off the lock (also Ctrl+Alt+Shift+F12) or to reset a forgotten password.\r\n" +
            "Creating new codes cancels all the codes on this page. The main recovery code keeps working as usual.\r\n" +
            "Keep this page in a safe place, not next to the computer.\r\n"));
        sb.Append("\r\n").Append(Lang.T("נוצר: ", "Created: ")).Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm")).Append("\r\n");
        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        return path;
    }
}
