// AdminLog.cs - #57: יומן פעולות מנהל (הפעלה/ביטול נעילה, שינוי סיסמה, שחזור, ייבוא הגדרות...).
// קובץ טקסט ליד config.json, עד 500 שורות. קריאה דרך POST /admin-log עם סיסמת מנהל.
using System.Text.Json;

namespace OtzariaBridge;

public static class AdminLog
{
    private static readonly object Gate = new();
    private const int Max = 500;
    private static string FilePath => Path.Combine(Config.Dir, "admin-log.txt");

    public static void Register() => Server.Route("/admin-log", Read);

    public static void Add(string action)
    {
        try
        {
            lock (Gate)
            {
                var lines = File.Exists(FilePath) ? File.ReadAllLines(FilePath).ToList() : new List<string>();
                lines.Add(DateTime.Now.ToString("yyyy-MM-dd HH:mm") + "\t" + action.Replace('\n', ' '));
                if (lines.Count > Max) lines.RemoveRange(0, lines.Count - Max);
                File.WriteAllLines(FilePath, lines);
            }
        }
        catch { /* יומן הוא תוספת — לא מפיל פעולה */ }
    }

    private static object Read(JsonElement b)
    {
        Kiosk.RequirePasswordOrRecovery(b.Str("password"));
        string[] lines;
        lock (Gate) lines = File.Exists(FilePath) ? File.ReadAllLines(FilePath) : Array.Empty<string>();
        return new
        {
            entries = lines.Reverse().Take(200).Select(l =>
            {
                int i = l.IndexOf('\t');
                return new { time = i > 0 ? l[..i] : "", action = i > 0 ? l[(i + 1)..] : l };
            }).ToArray()
        };
    }
}
