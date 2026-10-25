// Theme.cs - #56: ערכת עיצוב משותפת. "הגדרות מערכת" דוחפת את צבע הבסיס ומצב כהה/בהיר של אוצריא
// (theme.changed) ל-POST /theme, והחלונות של הגשר (שורת השעון בנעילה, מסך המנוחה) משתמשים בהם.
using System.Drawing;
using System.Text.Json;

namespace OtzariaBridge;

public static class Theme
{
    public static void Register() => Server.Route("/theme", Set);

    public static bool Dark => Config.Get("theme.mode", "dark") != "light";

    /// צבע ההדגשה מאוצריא; בהיעדרו — הזהב המקורי.
    public static Color Accent(Color fallback)
    {
        var s = Config.Get("theme.primary", "");
        try { if (s.Length == 7 && s[0] == '#') return ColorTranslator.FromHtml(s); } catch { }
        return fallback;
    }

    public static Color Bar => Dark ? Color.FromArgb(28, 32, 38) : Color.FromArgb(236, 238, 242);
    public static Color BarText => Dark ? Color.White : Color.FromArgb(24, 28, 34);

    private static object Set(JsonElement b)
    {
        string mode = b.TryGetProperty("mode", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString()! : "dark";
        string prim = b.TryGetProperty("primary", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString()! : "";
        if (prim.Length == 9 && prim.StartsWith("#")) prim = "#" + prim[3..]; // #AARRGGBB → #RRGGBB
        if (!(prim.Length == 7 && prim[0] == '#')) prim = "";
        mode = mode == "light" ? "light" : "dark";
        if (Config.Get("theme.mode", "") != mode) Config.Set("theme.mode", mode);
        if (Config.Get("theme.primary", "") != prim) Config.Set("theme.primary", prim);
        Config.Save();
        return new { ok = true };
    }
}
