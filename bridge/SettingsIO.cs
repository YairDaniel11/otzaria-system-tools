// SettingsIO.cs - #57: ייצוא ויבוא הגדרות בין מחשבים.
// מייצא רק הגדרות תוכן (נעילה, סייר, שומר מסך, ערכת עיצוב) — בלי סיסמאות, קודים, מפתחות, צימודים ונתיבים של המחשב.
// יבוא דורש סיסמת מנהל במחשב היעד, ומחיל רק את אותם מפתחות מותרים.
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OtzariaBridge;

public static class SettingsIO
{
    private static readonly string[] Prefixes = { "kiosk.", "fx.", "theme.", "zmanim." };
    private static readonly Regex Blocked = new("(hash|salt|secret|password|recovery|token|key|pair|device|path|twofactor|tfa|tempunlock|active|roots|location|confirmed|strict)", RegexOptions.IgnoreCase);

    public static void Register()
    {
        Server.Route("/settings/export", Export);
        Server.Route("/settings/import", Import);
    }

    private static bool Allowed(string k) => Prefixes.Any(p => k.StartsWith(p, StringComparison.Ordinal)) && !Blocked.IsMatch(k);

    // ניקוי רקורסיבי: אובייקט מקונן (למשל fx.policy) לא יעביר שורשי נתיבים/סודות בשמות שדות פנימיים.
    private static JsonNode Clean(JsonNode n)
    {
        if (n is JsonObject o) foreach (var kv in o.ToList()) { if (Blocked.IsMatch(kv.Key)) o.Remove(kv.Key); else Clean(kv.Value); }
        else if (n is JsonArray a) foreach (var x in a) Clean(x);
        return n;
    }

    private static object Export(JsonElement b)
    {
        Kiosk.RequirePasswordOrRecovery(b.Str("password"));
        var o = new JsonObject();
        foreach (var k in Config.Keys().Where(Allowed).OrderBy(k => k)) o[k] = Clean(Config.Raw(k)?.DeepClone());
        var doc = new JsonObject { ["format"] = "achat-shaalti-settings", ["version"] = 1, ["bridge"] = Program.Version, ["exported"] = DateTime.Now.ToString("s"), ["settings"] = o };
        string text = doc.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        string file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "achat-shaalti-settings-" + DateTime.Now.ToString("yyyyMMdd-HHmm") + ".json");
        try { File.WriteAllText(file, text); } catch { file = null; }
        AdminLog.Add("ייצוא הגדרות");
        return new { ok = true, count = o.Count, file, text };
    }

    private static object Import(JsonElement b)
    {
        Kiosk.RequirePassword(b.Str("password"));
        JsonObject s;
        try { s = (JsonNode.Parse(b.Str("text") ?? "") as JsonObject)?["settings"] as JsonObject; }
        catch { s = null; }
        if (s == null) throw BridgeException.BadRequest("הקובץ אינו קובץ הגדרות של אחת שאלתי");
        int n = 0, skipped = 0;
        foreach (var kv in s.ToList())
        {
            if (!Allowed(kv.Key)) { skipped++; continue; }
            Config.Set(kv.Key, Clean(kv.Value?.DeepClone()));
            n++;
        }
        Config.Save();
        AdminLog.Add("יבוא הגדרות (" + n + " הגדרות)");
        return new { ok = true, applied = n, skipped };
    }
}
