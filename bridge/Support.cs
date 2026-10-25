using System.Text;
using System.Text.Json.Nodes;

namespace OtzariaBridge;

/// <summary>
/// תיבת דואר יוצא לתקלות: קריסות ושגיאות פנימיות נשמרות כאן אוטומטית (בלי סיסמאות),
/// והתוסף "הגדרות מערכת" שולח אותן דרך ממשק הדיווח של אוצריא (feedback.report), שמחכה לחיבור אם אין.
/// </summary>
internal static class Outbox
{
    private static string Dir => Path.Combine(Config.Dir, "outbox");
    private static readonly Dictionary<string, DateTime> Recent = new();
    private const int MaxFiles = 20;

    public static void Init()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Add("crash", e.ExceptionObject?.ToString());
        Application.ThreadException += (_, e) => Add("ui-error", e.Exception?.ToString());
        TaskScheduler.UnobservedTaskException += (_, e) => { Add("task-error", e.Exception?.ToString()); e.SetObserved(); };
        Server.Route("/diag/pending", _ => Pending());
        Server.Route("/diag/ack", b => Ack(b));
    }

    private static bool IsNetworkNoise(string t) =>
        System.Text.RegularExpressions.Regex.IsMatch(t, @"HttpRequestException|SocketException|WebException|TaskCanceledException|No such host|UpdateApi",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    public static void Add(string kind, string text)
    {
        if (!Config.Get("diag.autoReport", true) || string.IsNullOrWhiteSpace(text)) return;
        if (IsNetworkNoise(text)) return; // A7: אין רשת / בדיקת עדכונים — לא תקלה
        try
        {
            string firstLine = text.Split('\n')[0].Trim();
            lock (Recent)
            {
                // אותה תקלה לא נרשמת שוב בתוך שעה
                if (Recent.TryGetValue(firstLine, out var at) && DateTime.UtcNow - at < TimeSpan.FromHours(1)) return;
                Recent[firstLine] = DateTime.UtcNow;
            }
            Directory.CreateDirectory(Dir);
            var files = Directory.GetFiles(Dir, "*.txt");
            if (files.Length >= MaxFiles) return;
            string body = "סוג: " + kind + "\nגרסת גשר: " + Program.Version + "\nמערכת: " + Environment.OSVersion +
                          "\nנעילה פעילה: " + Kiosk.IsActive + "\nזמן: " + DateTime.Now.ToString("s") + "\n\n" + Clean(text);
            string id = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + "-" + kind;
            if (body.Length > 4000) body = body[..4000];
            File.WriteAllText(Path.Combine(Dir, id + ".txt"), body, new UTF8Encoding(false));
            // הקריסה המכוונת של הבדיקה העצמית נשמרת ב-outbox (הבדיקה מאמתת זאת ומוחקת), אבל לא נשלחת ל-GitHub כ-issue
            if (!text.Contains("selftest-crash", StringComparison.Ordinal)) GhRelay.Queue(id, body); // #65
        }
        catch { }
    }

    /// <summary>מסיר נתיבי פרופיל אישיים (שם המשתמש ב-Windows)</summary>
    private static string Clean(string s)
    {
        string user = Environment.UserName;
        return string.IsNullOrEmpty(user) ? s : s.Replace("\\" + user + "\\", "\\<user>\\", StringComparison.OrdinalIgnoreCase);
    }

    private static object Pending()
    {
        var list = new JsonArray();
        if (Directory.Exists(Dir))
            foreach (var f in Directory.GetFiles(Dir, "*.txt").OrderBy(x => x).Take(10))
            {
                string t = File.ReadAllText(f);
                // #81-#90: קריסת הבדיקה העצמית דלפה ל-GitHub דרך התוסף (/diag/pending) — לא נמסרת לשליחה
                if (t.Contains("selftest-crash", StringComparison.Ordinal)) continue;
                list.Add(new JsonObject { ["id"] = Path.GetFileNameWithoutExtension(f), ["text"] = t });
            }
        return new JsonObject { ["enabled"] = Config.Get("diag.autoReport", true), ["items"] = list };
    }

    private static object Ack(System.Text.Json.JsonElement b)
    {
        int n = 0;
        if (b.TryGetProperty("ids", out var ids) && ids.ValueKind == System.Text.Json.JsonValueKind.Array)
            foreach (var x in ids.EnumerateArray())
            {
                string id = Path.GetFileName(x.GetString() ?? "");
                string f = Path.Combine(Dir, id + ".txt");
                if (id.Length > 0 && File.Exists(f)) { File.Delete(f); n++; }
            }
        if (b.TryGetProperty("enabled", out var en) && (en.ValueKind == System.Text.Json.JsonValueKind.True || en.ValueKind == System.Text.Json.JsonValueKind.False))
            Config.Set("diag.autoReport", en.GetBoolean());
        return new { removed = n, enabled = Config.Get("diag.autoReport", true) };
    }
}
