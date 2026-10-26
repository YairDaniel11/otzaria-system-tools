using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OtzariaBridge;

/// <summary>
/// #65: שליחת דיווחי תקלות גם ל-GitHub, דרך מתווך (Cloudflare Worker, tools/relay) שמחזיק את הטוקן.
/// בגשר אין טוקן. כתובת המתווך: config "diag.relayUrl" (ריק = כבוי). בנוסף ל-feedback.report, לא במקומו.
/// עותק של כל דיווח נשמר ב-outbox/gh ונשלח כל 10 דקות; נמחק אחרי תשובה סופית (2xx/409/4xx קבוע). fingerprint ששודר נזכר 24 שעות ב-outbox/gh/sent.
/// </summary>
internal static class GhRelay
{
    private static string Dir => Path.Combine(Config.Dir, "outbox", "gh");
    private static string SentDir => Path.Combine(Dir, "sent");
    private static System.Threading.Timer _t;
    // כמה זמן קובץ ישן/טביעת אצבע ששודרה נחשבים "טריים"
    internal static readonly TimeSpan Ttl = TimeSpan.FromHours(24);
    private const string DefaultUrl = "https://achat-relay.e0548433917.workers.dev"; // #65; "" ב-config מכבה

    public static void Init() => _t = new System.Threading.Timer(_ => { try { Flush().Wait(); } catch { } }, null, TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(10));

    public static void Queue(string id, string body)
    {
        if (string.IsNullOrEmpty(Config.Get("diag.relayUrl", DefaultUrl))) return;
        try
        {
            Directory.CreateDirectory(Dir);
            if (Directory.GetFiles(Dir, "*.txt").Length >= 20) return;
            File.WriteAllText(Path.Combine(Dir, id + ".txt"), body, new UTF8Encoding(false));
        }
        catch { }
    }

    public static void Kick() => Task.Run(async () => { try { await Task.Delay(3000); await Flush(); } catch { } });

    private static async Task Flush()
    {
        string url = Config.Get("diag.relayUrl", DefaultUrl);
        if (string.IsNullOrEmpty(url) || !url.StartsWith("https://") || !Directory.Exists(Dir)) return;
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        string tail = "";
        try { tail = Scrub(Reports.LogTail(40)); } catch { }
        foreach (var f in Directory.GetFiles(Dir, "*.txt").OrderBy(x => x).Take(5))
        {
            // WHY: בגשרים ישנים קבצי selftest-crash ישנים נשארו בתור ונשלחו שוב ושוב (הסינון היה רק ב-Queue)
            if (IsStaleSelfTest(f, DateTime.UtcNow)) { TryDelete(f, "selftest ישן"); continue; }
            string text = Scrub(File.ReadAllText(f));
            string kind = "auto", repo = "", plugin = "", pv = "";
            if (text.StartsWith("#meta "))
            {
                var parts = text.Split("\n\n", 2);
                foreach (var kv in parts[0][6..].Split(';'))
                {
                    var x = kv.Split('=', 2); if (x.Length < 2) continue;
                    if (x[0] == "kind") kind = x[1]; else if (x[0] == "repo") repo = x[1]; else if (x[0] == "plugin") plugin = x[1]; else if (x[0] == "pv") pv = x[1];
                }
                text = parts.Length > 1 ? parts[1] : "";
            }
            bool manual = kind == "manual";
            string first = text.Split("\n\n", 2).Last().Split('\n')[0].Trim();
            string fp = Fingerprint(manual ? text + f : first);
            // WHY: אותו fingerprint כבר נשלח מהמחשב הזה ב-24 השעות האחרונות — לא שולחים שוב, רק מנקים את התור
            if (WasSent(SentDir, fp, DateTime.UtcNow)) { TryDelete(f, "fingerprint כבר נשלח"); continue; }
            var payload = JsonSerializer.Serialize(new
            {
                title = (manual ? "[משוב] " : "[auto] ") + (first.Length > 90 ? first[..90] : first),
                fingerprint = fp,
                version = Program.Version,
                kind, repo, plugin, pluginVersion = pv,
                body = Cut(manual ? text : text + (tail.Length > 0 ? "\n\n----- יומן מקוצר -----\n" + tail : ""))
            });
            bool done = false;
            try
            {
                var r = await http.PostAsync(url, new StringContent(payload, Encoding.UTF8, "application/json"));
                int c = (int)r.StatusCode;
                if (c is >= 200 and < 300 or 409) MarkSent(SentDir, fp);
                if (c == 429) return;
                done = IsFinal(c);
            }
            catch { return; } // אין רשת — ננסה בסבב הבא
            finally
            {
                // WHY: המחיקה ב-finally ורק בתשובה סופית; כישלון מחיקה נרשם ביומן (קודם נבלע, והקובץ נשלח בכל סבב)
                if (done) TryDelete(f, "אחרי שליחה");
            }
        }
    }

    /// <summary>תשובה סופית = אין טעם לנסות שוב: הצלחה, כפילות או דחייה קבועה (4xx). 5xx/408/429 זמניים.</summary>
    internal static bool IsFinal(int c) => c is >= 200 and < 300 or 409 || (c is >= 400 and < 500 && c != 408 && c != 429);

    /// <summary>קובץ selftest-crash שגילו מעל 24 שעות</summary>
    internal static bool IsStaleSelfTest(string file, DateTime nowUtc)
    {
        try
        {
            return nowUtc - File.GetLastWriteTimeUtc(file) > Ttl &&
                   File.ReadAllText(file).Contains("selftest-crash", StringComparison.Ordinal);
        }
        catch { return false; }
    }

    internal static bool WasSent(string sentDir, string fp, DateTime nowUtc)
    {
        try
        {
            string m = Path.Combine(sentDir, fp + ".txt");
            if (!File.Exists(m)) return false;
            if (nowUtc - File.GetLastWriteTimeUtc(m) <= Ttl) return true;
            File.Delete(m); // פג תוקף
        }
        catch { }
        return false;
    }

    internal static void MarkSent(string sentDir, string fp)
    {
        try
        {
            Directory.CreateDirectory(sentDir);
            File.WriteAllText(Path.Combine(sentDir, fp + ".txt"), DateTime.UtcNow.ToString("o"));
            foreach (var old in Directory.GetFiles(sentDir, "*.txt")) // ניקוי פגי תוקף, שהתיקייה לא תגדל
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(old) > Ttl) File.Delete(old);
        }
        catch { }
    }

    internal static bool TryDelete(string f, string why)
    {
        try { File.Delete(f); return true; }
        catch (Exception e) { Config.Log("GhRelay: מחיקת " + Path.GetFileName(f) + " נכשלה (" + why + "): " + e.Message); return false; }
    }

    private static string Cut(string b) => b.Length > 6000 ? b[..6000] : b;

    private static string Fingerprint(string s)
    {
        s = Regex.Replace(s, @"\d+", "#");
        var h = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(s));
        return Convert.ToHexString(h)[..16].ToLowerInvariant();
    }

    /// <summary>הסתרת פרטים אישיים: שם משתמש/מחשב, נתיבי פרופיל, שמות קבצים (חוץ מקוד: cs/dll/exe)</summary>
    public static string Scrub(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        foreach (var w in new[] { Environment.UserName, Environment.MachineName })
            if (!string.IsNullOrEmpty(w) && w.Length > 1) s = Regex.Replace(s, Regex.Escape(w), "<user>", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"(?i)([a-z]:\\users\\)[^\\\s""']+", "$1<user>");
        s = Regex.Replace(s, @"(?<=[\\/])[^\\/\r\n:*?""<>|]+\.(?!(?:cs|dll|exe)\b)[A-Za-z0-9]{1,6}\b", "<file>");
        return s;
    }
}
