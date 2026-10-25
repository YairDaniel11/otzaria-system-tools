// E2 / #30 ממצא 8: עדכון השירות בלי להריץ מורם את העותק שב-%LOCALAPPDATA% (תיקייה שהמשתמש יכול לכתוב אליה).
// השירות (SYSTEM, רץ מ-Program Files) מוריד בעצמו את OtzariaBridge.exe מההוצאה הרשמית ב-GitHub, מאמת את אותה חבילה חתומה
// כמו הגשר (ECDSA על SHA256SUMS, תגית חתומה, keyver, אי-downgrade, hash, PE, FileVersion), מחליף את עצמו ומתאתחל.
// WHY (מיזוג upstream): הגרסה של המפתח אימתה רק SHA256 מול קובץ מאותו Release, כלומר מי ששולט ב-Release מריץ קוד כ-SYSTEM.
// כאן חובה חתימה עם UpdateApi.PublicKeySpkiBase64 (המפתח הפרטי לא ב-GitHub Release אלא בסוד CI), ובלי מפתח = לא מעדכנים.
// רק שדרוג, רק כשהנעילה לא נאכפת. אם אין הוצאה מאומתת - הלקוח חוזר לדרך הישנה (UAC).
using System.Diagnostics;
using System.Net.Http;
using System.Text.Json.Nodes;

namespace OtzariaBridge;

public static class SvcUpdate
{
    static JsonObject Err(string m) => new() { ["ok"] = false, ["code"] = "failed", ["message"] = m };

    /// <summary>רץ בתוך השירות. enforced = האם הנעילה נאכפת כרגע</summary>
    public static JsonObject Run(string version, bool enforced)
    {
        try
        {
            if (enforced) return Err("נעילה פעילה — עדכון עם סיסמה בלבד");
            if (string.IsNullOrWhiteSpace(UpdateApi.PublicKeySpkiBase64)) return Err("לא הוגדר מפתח אימות עדכונים");
            var want = UpdateApi.Parse(version ?? ""); var cur = UpdateApi.Parse(Program.Version);
            if (want == null || cur == null || want <= cur) return Err("אין גרסה חדשה יותר");
            return Task.Run(() => RunAsync(want, cur)).GetAwaiter().GetResult();
        }
        catch (Exception ex) { HardLock.SvcLog("self-update: " + ex.Message); return Err(ex.Message); }
    }

    static async Task<JsonObject> RunAsync(Version want, Version cur)
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
        try { File.Delete(old); } catch { }
        File.Move(exe, old);             // מותר להזיז exe רץ
        File.Move(neu, exe);
        HardLock.SvcLog("self-update: " + Program.Version + " -> " + ver.ToString(3) + " tag=" + rel.Tag + " sha256=" + hash + " (חתימה מאומתת)");
        Process.Start(new ProcessStartInfo("cmd.exe",
            $"/c timeout /t 2 >nul & sc stop {HardLock.ServiceName} & timeout /t 3 >nul & sc start {HardLock.ServiceName}")
        { UseShellExecute = false, CreateNoWindow = true });
        return new JsonObject { ["ok"] = true, ["version"] = ver.ToString(3) };
    }
}
