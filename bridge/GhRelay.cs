using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OtzariaBridge;

/// <summary>
/// #65: שליחת דיווחי תקלות גם ל-GitHub, דרך מתווך (Cloudflare Worker, tools/relay) שמחזיק את הטוקן.
/// בגשר אין טוקן. כתובת המתווך: config "diag.relayUrl" (ריק = כבוי). בנוסף ל-feedback.report, לא במקומו.
/// עותק של כל דיווח נשמר ב-outbox/gh ונשלח כל 10 דקות; נמחק אחרי 2xx/409 (כפילות).
/// </summary>
internal static class GhRelay
{
    private static string Dir => Path.Combine(Config.Dir, "outbox", "gh");
    private static System.Threading.Timer _t;
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
            var payload = JsonSerializer.Serialize(new
            {
                title = (manual ? "[משוב] " : "[auto] ") + (first.Length > 90 ? first[..90] : first),
                fingerprint = Fingerprint(manual ? text + f : first),
                version = Program.Version,
                kind, repo, plugin, pluginVersion = pv,
                body = Cut(manual ? text : text + (tail.Length > 0 ? "\n\n----- יומן מקוצר -----\n" + tail : ""))
            });
            try
            {
                var r = await http.PostAsync(url, new StringContent(payload, Encoding.UTF8, "application/json"));
                int c = (int)r.StatusCode;
                if (c is >= 200 and < 300 or 409) File.Delete(f);
                else if (c == 429) return;
            }
            catch { return; } // אין רשת — ננסה בסבב הבא
        }
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
