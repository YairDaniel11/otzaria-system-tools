// E2 / #30 ממצא 8: עדכון השירות בלי להריץ מורם את העותק שב-%LOCALAPPDATA% (תיקייה שהמשתמש יכול לכתוב אליה).
// השירות (SYSTEM, רץ מ-Program Files) מוריד בעצמו את OtzariaBridge.exe מההוצאה הרשמית ב-GitHub, מאמת את אותה חבילה חתומה
// כמו הגשר (ECDSA על SHA256SUMS, תגית חתומה, keyver, אי-downgrade, hash, PE, FileVersion), מחליף את עצמו ומתאתחל.
// WHY (מיזוג upstream): הגרסה של המפתח אימתה רק SHA256 מול קובץ מאותו Release, כלומר מי ששולט ב-Release מריץ קוד כ-SYSTEM.
// כאן חובה חתימה עם UpdateApi.PublicKeySpkiBase64 (המפתח הפרטי לא ב-GitHub Release אלא בסוד CI), ובלי מפתח = לא מעדכנים.
// רק שדרוג, רק כשהנעילה לא נאכפת. אם אין הוצאה מאומתת - הלקוח חוזר לדרך הישנה (UAC).
// qa-final-a #1/#2: הפקודה לא חוסמת את ה-pipe ולא את _gate (ההורדה ברקע, התשובה "התחיל"), דורשת מנהל/גשר שהשירות הפעיל/סיסמה,
// מוגבלת בקצב; ההחלפה אטומית עם rollback, והעלייה הבאה מחזירה את ה-exe הקודם אם הגרסה החדשה לא התייצבה (SvcUpdateGuard).
using System.Diagnostics;
using System.Net.Http;
using System.Text.Json.Nodes;

namespace OtzariaBridge;

public static class SvcUpdate
{
    static JsonObject Err(string m) => new() { ["ok"] = false, ["code"] = "failed", ["message"] = m };

    /// <summary>מגבלת קצב: עדכון אחד בו-זמנית, ולא יותר מאחד לכל MinGapMs (מונע הצפת הורדות מלקוח כלשהו)</summary>
    internal sealed class RateGate
    {
        public const long MinGapMs = 10 * 60 * 1000;
        private readonly object _l = new();
        private bool _busy; private long _last = long.MinValue;
        public bool TryEnter(long nowMs)
        {
            lock (_l)
            {
                if (_busy || (_last != long.MinValue && nowMs - _last < MinGapMs)) return false;
                _busy = true; _last = nowMs; return true;
            }
        }
        public void Exit() { lock (_l) _busy = false; }
    }

    internal static readonly RateGate Gate = new();

    /// <summary>
    /// מי רשאי להפעיל עדכון עצמי: מנהל מורם, הגשר שהשירות עצמו הפעיל, או סיסמת נעילה תקינה.
    /// WHY: בעבר כל משתמש מאומת יכול היה להפעיל הורדות כבדות כשהנעילה לא נאכפת
    /// </summary>
    internal static bool Authorized(bool admin, bool ownBridge, bool passwordOk) => admin || ownBridge || passwordOk;

    /// <summary>רץ בתוך השירות, מחזיק את _gate רק לרגע: מאמת בקשה ומתחיל עדכון ברקע. stillAllowed נבדק שוב רגע לפני ההחלפה</summary>
    public static JsonObject Start(string version, bool enforced, Func<bool> stillAllowed)
    {
        try
        {
            if (enforced) return Err("נעילה פעילה — עדכון עם סיסמה בלבד");
            if (string.IsNullOrWhiteSpace(UpdateApi.PublicKeySpkiBase64)) return Err("לא הוגדר מפתח אימות עדכונים");
            var want = UpdateApi.Parse(version ?? ""); var cur = UpdateApi.Parse(Program.Version);
            if (want == null || cur == null || want <= cur) return Err("אין גרסה חדשה יותר");
            if (!Gate.TryEnter(Environment.TickCount64))
                return new JsonObject { ["ok"] = false, ["code"] = "too_many", ["message"] = "עדכון כבר רץ או רץ לאחרונה — נסו שוב בעוד כמה דקות" };
            SafeTask.Run(() =>
            {
                try
                {
                    var r = Task.Run(() => RunAsync(want, cur, stillAllowed)).GetAwaiter().GetResult();
                    if (r["ok"]?.GetValue<bool>() != true) HardLock.SvcLog("self-update נכשל: " + r["message"]);
                }
                catch (Exception ex) { HardLock.SvcLog("self-update: " + ex.Message); }
                finally { Gate.Exit(); }
            }, "self-update");
            return new JsonObject { ["ok"] = true, ["started"] = true };
        }
        catch (Exception ex) { HardLock.SvcLog("self-update: " + ex.Message); return Err(ex.Message); }
    }

    /// <summary>
    /// החלפת exe אטומית עם rollback. מאמת ש-neu תואם expectedSha, מזיז exe→old, neu→exe; כשל בשלב השני מחזיר old→exe.
    /// אחרי ההחלפה מאמת שוב את ה-hash של exe. null = הצלחה; אחרת הסיבה (והמצב הקודם שוחזר)
    /// </summary>
    internal static string SwapExe(string exe, string neu, string old, string expectedSha)
    {
        try
        {
            if (!File.Exists(neu)) return "הקובץ החדש חסר";
            if (!string.IsNullOrEmpty(expectedSha) && !string.Equals(InstallTrust.Sha256File(neu), expectedSha, StringComparison.OrdinalIgnoreCase))
                return "ה-hash של הקובץ החדש אינו תואם";
        }
        catch (Exception ex) { return "אימות הקובץ החדש נכשל: " + ex.Message; }
        bool movedOld = false;
        try
        {
            try { File.Delete(old); } catch { }
            if (File.Exists(exe)) { File.Move(exe, old, true); movedOld = true; } // מותר להזיז exe רץ
            File.Move(neu, exe, true);
            if (!string.IsNullOrEmpty(expectedSha) && !string.Equals(InstallTrust.Sha256File(exe), expectedSha, StringComparison.OrdinalIgnoreCase))
                throw new IOException("ה-hash אחרי ההחלפה אינו תואם");
            return null;
        }
        catch (Exception ex)
        {
            string err = "ההחלפה נכשלה: " + ex.Message;
            try
            {
                if (movedOld)
                {
                    if (File.Exists(exe)) File.Delete(exe);
                    File.Move(old, exe, true);
                }
            }
            catch (Exception rb) { err += " (גם השחזור נכשל: " + rb.Message + ")"; }
            try { if (File.Exists(neu)) File.Delete(neu); } catch { }
            return err;
        }
    }

    static async Task<JsonObject> RunAsync(Version want, Version cur, Func<bool> stillAllowed)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("OtzariaBridge-Service");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        string json = await http.GetStringAsync("https://api.github.com/repos/" + UpdateApi.Repo + "/releases?per_page=20");
        // אותה בחירה מאומתת כמו בגשר (חתימה/תגית/keyver); הלקוח לא יכול לכוון להוצאה אחרת
        var rel = await UpdateApi.SelectVerifiedRelease(json, UpdateApi.DownloadCapped, UpdateApi.PublicKeySpkiBase64);
        if (rel == null) return Err("אין הוצאה מאומתת (חתומה)");
        byte[] sums = await UpdateApi.DownloadCapped(rel.SumsUrl, UpdateApi.MaxSmallDownload);
        string sig = System.Text.Encoding.ASCII.GetString(await UpdateApi.DownloadCapped(rel.SigUrl, UpdateApi.MaxSmallDownload));
        byte[] bv = await UpdateApi.DownloadCapped(rel.BridgeVerUrl, UpdateApi.MaxSmallDownload);
        byte[] data = await UpdateApi.DownloadCapped(rel.ExeUrl, UpdateApi.MaxExeDownload);
        var (ver, hash) = UpdateApi.VerifyPackage(sums, sig, rel.Tag, bv, data, Program.Version, UpdateApi.PublicKeySpkiBase64);
        if (ver < want) return Err("הגרסה החתומה נמוכה מהמבוקשת");

        string exe = HardLock.ServiceExe, neu = exe + ".new", old = exe + ".old";
        File.WriteAllBytes(neu, data); // Program Files — רק SYSTEM/מנהלים כותבים
        if (!UpdateApi.FileVersionMatches(neu, ver)) { try { File.Delete(neu); } catch { } return Err("גרסת הקובץ אינה הגרסה החתומה"); }
        if (stillAllowed != null && !stillAllowed()) { try { File.Delete(neu); } catch { } return Err("הנעילה נאכפת מחדש — העדכון בוטל"); }
        string oldSha = null; try { oldSha = InstallTrust.Sha256File(exe); } catch { }
        string newSha = InstallTrust.Sha256(data);
        // סמן "ממתין לאישור" לפני ההחלפה: אם הגרסה החדשה קורסת בעלייה, העלייה הבאה משחזרת את הקודמת (SvcUpdateGuard)
        SvcUpdateGuard.Begin(HardLock.DataDir, newSha, oldSha);
        string swapErr = SwapExe(exe, neu, old, newSha);
        if (swapErr != null) { SvcUpdateGuard.Clear(HardLock.DataDir); HardLock.SvcLog("self-update: " + swapErr); return Err(swapErr); }
        HardLock.SvcLog("self-update: " + Program.Version + " -> " + ver.ToString(3) + " tag=" + rel.Tag + " sha256=" + hash + " (חתימה מאומתת)");
        // אתחול: stop + start; אם start נכשל — משחזרים את .old ומנסים שוב
        string svc = HardLock.ServiceName;
        Process.Start(new ProcessStartInfo("cmd.exe",
            $"/c timeout /t 2 >nul & sc stop {svc} >nul & timeout /t 3 >nul & sc start {svc} >nul || (move /y \"{old}\" \"{exe}\" >nul & sc start {svc} >nul)")
        { UseShellExecute = false, CreateNoWindow = true });
        return new JsonObject { ["ok"] = true, ["version"] = ver.ToString(3) };
    }
}

/// <summary>
/// qa-final-a #2: סמן "עדכון ממתין" בתיקיית המצב של השירות. אם אחרי עדכון השירות עולה שוב ושוב בלי להתייצב
/// (יותר מ-MaxStarts עליות בלי Confirm), העלייה הבאה מחזירה את ה-exe הקודם (.old), רק אם ה-hash שלו זהה לרשום.
/// מגבלה: קריסה שחוזרת רק אחרי חלון האישור (ConfirmAfterSeconds) לא תוחזר אוטומטית.
/// </summary>
internal static class SvcUpdateGuard
{
    internal const int MaxStarts = 3;
    internal const int ConfirmAfterSeconds = 90;
    internal enum Outcome { None, Counted, RolledBack, RollbackFailed }

    static string MarkerPath(string dir) => Path.Combine(dir, "svc-update-pending.json");

    internal static void Begin(string dir, string newSha, string oldSha)
    {
        var o = new JsonObject { ["newSha"] = newSha, ["oldSha"] = oldSha ?? "", ["starts"] = 0 };
        StateStore.WriteAtomic(MarkerPath(dir), o.ToJsonString());
    }

    internal static void Clear(string dir)
    {
        try { File.Delete(MarkerPath(dir)); } catch { }
        try { File.Delete(MarkerPath(dir) + ".bak"); } catch { }
    }

    /// <summary>נקרא בתחילת OnStart. exe = ServiceExe, old = exe + ".old"</summary>
    internal static Outcome OnStart(string dir, string exe, string old)
    {
        var m = StateStore.Read(MarkerPath(dir), out var st, out _);
        if (st == StateLoad.Missing) return Outcome.None;
        if (m == null) { Clear(dir); return Outcome.None; }
        string cur = null; try { cur = InstallTrust.Sha256File(exe); } catch { }
        string newSha = m["newSha"]?.ToString(), oldSha = m["oldSha"]?.ToString();
        if (!string.Equals(cur, newSha, StringComparison.OrdinalIgnoreCase)) { Clear(dir); return Outcome.None; } // ה-exe כבר לא העדכון הממתין
        int starts = (m["starts"] is JsonValue v && v.TryGetValue<int>(out int n) ? n : 0) + 1;
        if (starts <= MaxStarts)
        {
            m["starts"] = starts;
            try { StateStore.WriteAtomic(MarkerPath(dir), m.ToJsonString()); } catch { }
            return Outcome.Counted;
        }
        try
        {
            if (string.IsNullOrEmpty(oldSha) || !File.Exists(old) ||
                !string.Equals(InstallTrust.Sha256File(old), oldSha, StringComparison.OrdinalIgnoreCase)) { Clear(dir); return Outcome.RollbackFailed; }
            string bad = exe + ".bad";
            try { File.Delete(bad); } catch { }
            File.Move(exe, bad, true);
            File.Move(old, exe, true);
            Clear(dir);
            return Outcome.RolledBack;
        }
        catch { return Outcome.RollbackFailed; }
    }

    /// <summary>השירות רץ יציב: האישור מוחק את הסמן (ה-.old נשמר עד העדכון הבא)</summary>
    internal static void Confirm(string dir) => Clear(dir);
}
