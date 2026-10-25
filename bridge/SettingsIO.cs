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

    /// <summary>
    /// F2 WHY: מפתחות ש-ConfigSeal חותם (SecurityKeys) מתקבלים מקובץ חיצוני (גם אם הסיסמה נכונה, הקובץ יכול להיות זדוני).
    /// מאמתים סוג וערך: סיומות רק מהרשימה הלבנה של FsGuard (בלי exe/bat/lnk...), סקציות רק מ-AllSections, שעות תקינות.
    /// מחזיר null = דלג על המפתח (לא מייבאים ערך לא תקין).
    /// </summary>
    internal static JsonNode SanitizeSecurityValue(string key, JsonNode v)
    {
        try
        {
            switch (key)
            {
                case "kiosk.readOnly": case "kiosk.allowExcelDocs": case "kiosk.blockTaskManager":
                case "kiosk.schedule.enabled": case "kiosk.schedule.warn": case "kiosk.barPrograms":
                    return v is JsonValue bv && bv.TryGetValue<bool>(out bool bb) ? JsonValue.Create(bb) : null;
                case "kiosk.schedule.start": case "kiosk.schedule.end":
                    return v is JsonValue sv && sv.TryGetValue<string>(out var t) && Sched.IsValid(t) ? JsonValue.Create(t) : null;
                case "kiosk.allowOpenExtensions":
                {
                    if (v is not JsonArray a) return null;
                    var res = new JsonArray();
                    foreach (var x in a)
                    {
                        string e = (x?.GetValue<string>() ?? "").TrimStart('.').ToLowerInvariant();
                        if (e.Length == 0 || e.Length > 8 || !FsGuard.IsOpenAllowedExt("x." + e)) continue; // בחוץ: exe/bat/ps1/lnk/msi/html וכו'
                        if (!res.Any(r => r.GetValue<string>() == e)) res.Add(e);
                    }
                    return res;
                }
                case "kiosk.openSections":
                {
                    if (v is not JsonArray a) return null;
                    var res = new JsonArray();
                    foreach (var x in a)
                    {
                        string sec = x?.GetValue<string>() ?? "";
                        if (Kiosk.AllSections.Contains(sec, StringComparer.OrdinalIgnoreCase) && !res.Any(r => r.GetValue<string>() == sec)) res.Add(sec.ToLowerInvariant());
                    }
                    return res;
                }
            }
        }
        catch { return null; }
        return v;
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
            var val = Clean(kv.Value?.DeepClone());
            if (ConfigSeal.SecurityKeys.Contains(kv.Key) || kv.Key == "kiosk.barPrograms")
            {
                val = SanitizeSecurityValue(kv.Key, val);
                if (val == null) { skipped++; continue; }
            }
            Config.Set(kv.Key, val);
            n++;
        }
        Config.Save();
        Kiosk.SealConfig(); // F2 WHY: בלי חתימה מחדש sec.seal לא תואם => SecurityIntact=false => פתיחות/סיומות נעלמות והגשר "נסגר"
        AdminLog.Add("יבוא הגדרות (" + n + " הגדרות)");
        return new { ok = true, applied = n, skipped };
    }
}
