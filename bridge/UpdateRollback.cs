using System.Security.Cryptography;
using System.Text.Json;

namespace OtzariaBridge;

/// <summary>
/// שחזור אוטומטי לעדכון כושל (X3). לפני החלפת ה-exe המותקן נשמר גיבוי של הגרסה הקודמת + מצב "ממתין" (hash של הגיבוי והחדש).
/// הגשר החדש כותב health.ok (ה-hash של עצמו) רק אחרי ש-/health שלו ענה. המתקין (התהליך שהחליף את הקובץ) ממתין לסימן;
/// אם לא הגיע תוך N שניות, מחזיר את הגיבוי.
/// WHY: עדכון חתום שנשבר בפועל (קריסה בעלייה/תלות חסרה) השאיר קיוסק לא עובד בלי דרך חזרה. נלמד מ-KioskLock Updater.cs.
/// אי-downgrade זדוני: משחזרים רק לקובץ שה-SHA-256 שלו שווה ל-PrevSha שנרשם על ידינו בזמן הגיבוי (לא "כל קובץ ישן").
/// מגבלה ידועה: התיקייה ב-LOCALAPPDATA ושייכת למשתמש, ולכן תוקף שרץ כמשתמש יכול להחליף גם את המצב וגם את הגיבוי יחד; המטרה כאן
/// היא עמידות לתקלות, לא הגנה מפני משתמש מנהל-עצמי (במצב שירות העדכון העצמי נחסם ממילא, ר' UpdateApi.Install).
/// </summary>
internal static class UpdateRollback
{
    internal const string BackupName = "OtzariaBridge.prev.exe";
    internal const string StateName = "pending.json";
    internal const string HealthName = "health.ok";
    internal const int DefaultTimeoutSec = 90;

    internal sealed record State(string PrevSha, string PrevVersion, string NewSha, string NewVersion, DateTime StartedUtc);
    internal enum Verdict { None, Healthy, Waiting, Rollback }

    internal static string DefaultRoot => Path.Combine(Config.Dir, "rollback");

    private static string P(string root, string name) => Path.Combine(root, name);

    internal static string Sha(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(fs));
    }

    internal static State Load(string root)
    {
        try { return JsonSerializer.Deserialize<State>(File.ReadAllText(P(root, StateName))); }
        catch { return null; }
    }

    /// <summary>גיבוי ה-exe המותקן ורישום מצב ממתין. false = אין מה לגבות (התקנה ראשונה) או שהגיבוי נכשל: אז אין מצב ואין שחזור</summary>
    internal static bool Prepare(string root, string installedExe, string newSha, string newVersion, string prevVersion, DateTime nowUtc)
    {
        try
        {
            if (!File.Exists(installedExe)) return false;
            Directory.CreateDirectory(root);
            Clear(root);
            string bak = P(root, BackupName);
            File.Copy(installedExe, bak, true);
            string prev = Sha(bak);
            // הגיבוי חייב להיות זהה למקור (אחרת המקור הוחלף תוך כדי העתקה)
            if (!string.Equals(prev, Sha(installedExe), StringComparison.OrdinalIgnoreCase)) return false;
            File.WriteAllText(P(root, StateName), JsonSerializer.Serialize(new State(prev, prevVersion, newSha.ToUpperInvariant(), newVersion, nowUtc)));
            return true;
        }
        catch (Exception ex) { Config.Log("rollback: הכנת גיבוי נכשלה: " + ex.Message); return false; }
    }

    internal static Verdict Evaluate(State s, string healthText, DateTime nowUtc, int timeoutSec)
    {
        if (s == null) return Verdict.None;
        if (!string.IsNullOrWhiteSpace(healthText) && string.Equals(healthText.Trim(), s.NewSha, StringComparison.OrdinalIgnoreCase)) return Verdict.Healthy;
        return nowUtc - s.StartedUtc >= TimeSpan.FromSeconds(timeoutSec) ? Verdict.Rollback : Verdict.Waiting;
    }

    /// <summary>הגשר הרץ קורא אחרי ש-/health ענה: כותב סימן רק אם הוא עצמו הגרסה שממתינים לה (לפי hash)</summary>
    internal static bool ReportHealthy(string root, string selfSha)
    {
        try
        {
            var s = Load(root);
            if (s == null || string.IsNullOrEmpty(selfSha) || !string.Equals(s.NewSha, selfSha, StringComparison.OrdinalIgnoreCase)) return false;
            File.WriteAllText(P(root, HealthName), selfSha);
            return true;
        }
        catch { return false; }
    }

    internal static void Clear(string root)
    {
        foreach (var n in new[] { StateName, HealthName }) { try { File.Delete(P(root, n)); } catch { } }
    }

    /// <summary>משחזר את הגיבוי על ה-exe המותקן, רק אם ה-hash שלו שווה ל-PrevSha שנרשם. מחזיר שגיאה או null</summary>
    internal static string Restore(string root, string installedExe, Action<string, string> copy = null)
    {
        var s = Load(root);
        if (s == null || string.IsNullOrEmpty(s.PrevSha)) return "אין מצב שחזור";
        string bak = P(root, BackupName);
        try
        {
            if (!File.Exists(bak) || !string.Equals(Sha(bak), s.PrevSha, StringComparison.OrdinalIgnoreCase)) return "הגיבוי חסר או שונה מהנרשם (לא משחזרים)";
            copy ??= (a, b) => { for (int i = 0; ; i++) { try { File.Copy(a, b, true); return; } catch (IOException) when (i < 9) { Thread.Sleep(500); } } };
            copy(bak, installedExe);
            if (!string.Equals(Sha(installedExe), s.PrevSha, StringComparison.OrdinalIgnoreCase)) return "אימות אחרי שחזור נכשל";
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }

    /// <summary>
    /// ממתין לסימן תקינות עד timeoutSec. תקין = מנקה מצב. נכשל = מבצע שחזור (stopNew מופעל קודם כדי לשחרר את הקובץ) ואז startOld.
    /// מחזיר Healthy / Rollback (גם כשהשחזור עצמו נכשל; הלוג מפרט) / None.
    /// </summary>
    internal static Verdict Watch(string root, string installedExe, int timeoutSec, Func<DateTime> now, Action<int> sleepMs,
        Action stopNew, Action startOld)
    {
        var s = Load(root);
        if (s == null) return Verdict.None;
        while (true)
        {
            string h = null; try { h = File.ReadAllText(P(root, HealthName)); } catch { }
            var v = Evaluate(s, h, now(), timeoutSec);
            if (v == Verdict.Healthy) { Config.Log("update: הגרסה החדשה תקינה " + s.NewVersion); Clear(root); return v; }
            if (v == Verdict.Rollback) break;
            sleepMs(1000);
        }
        Config.Log("update: הגרסה החדשה לא דיווחה תקינות תוך " + timeoutSec + " שניות; משחזר " + s.PrevVersion);
        try { stopNew(); } catch { }
        string err = Restore(root, installedExe);
        Clear(root);
        if (err != null) Config.Log("update: שחזור נכשל: " + err);
        else { try { startOld(); } catch (Exception ex) { Config.Log("update: הפעלת הגרסה המשוחזרת נכשלה: " + ex.Message); } }
        return Verdict.Rollback;
    }

    /// <summary>רץ בגשר החדש אחרי עלייה: ממתין שה-/health המקומי יענה 200 ואז כותב את הסימן</summary>
    internal static void StartHealthReporter()
    {
        var s = Load(DefaultRoot);
        if (s == null) return;
        _ = Task.Run(async () =>
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            for (int i = 0; i < 60; i++)
            {
                try
                {
                    using var r = await http.GetAsync("http://127.0.0.1:" + Program.Port + "/health");
                    if (r.IsSuccessStatusCode) { ReportHealthy(DefaultRoot, InstallTrust.SelfSha256()); return; }
                }
                catch { }
                await Task.Delay(1000);
            }
        });
    }
}
