using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OtzariaBridge;

/// <summary>
/// חבילה [ב] (מימוש מינימלי): גודל תיקייה, דחיסה/חילוץ ZIP, גיבוי/שחזור הגדרות הגשר.
/// נרשם אוטומטית (ModuleInitializer) כדי לא לגעת בקבצים קיימים.
/// </summary>
public static class PackB
{
    [ModuleInitializer]
    internal static void Init()
    {
        Server.Route("/fs/dirsize", DirSize);
        Server.Route("/fs/zip", Zip);
        Server.Route("/fs/unzip", Unzip);
        Server.Route("/sys/settings-backup/export", Export);
        Server.Route("/sys/settings-backup/import", Import);
    }

    private static string Full(string p)
    {
        if (string.IsNullOrWhiteSpace(p)) throw BridgeException.BadRequest("נתיב ריק");
        p = Environment.ExpandEnvironmentVariables(p.Trim());
        if (!Path.IsPathFullyQualified(p)) throw BridgeException.BadRequest("נדרש נתיב מלא: " + p);
        string full = PathPolicy.LongName(Path.GetFullPath(p));
        FsApi.EnsureNotReadable(full);   // W2: סודות הגשר חסומים גם לדחיסה/חילוץ/גודל תיקייה
        FxPolicy.EnsurePathAllowed(full);
        return full;
    }

    // ---------- גודל כולל לתיקייה ----------
    private static object DirSize(JsonElement b)
    {
        string path = Full(b.Req("path"));
        if (!Directory.Exists(path)) throw BridgeException.NotFound("לא נמצאה תיקייה: " + path);
        long size = 0; int files = 0, dirs = 0; bool partial = false;
        var deadline = DateTime.UtcNow.AddSeconds(25);
        var opt = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var i in new DirectoryInfo(path).EnumerateFileSystemInfos("*", opt))
        {
            if (DateTime.UtcNow > deadline) { partial = true; break; }
            if (i is FileInfo f) { files++; try { size += f.Length; } catch { } }
            else dirs++;
        }
        return new { path, size, files, dirs, partial };
    }

    // ---------- ZIP ----------
    private static object Zip(JsonElement b)
    {
        Kiosk.EnsureFileWriteAllowed();
        var sources = b.StrArray("paths").Select(Full).ToArray();
        if (sources.Length == 0) throw BridgeException.BadRequest("לא נבחרו פריטים");
        string dest = Full(b.Req("dest"));
        if (!dest.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) dest += ".zip";
        FsApi.EnsureNotProtected(dest);
        dest = Unique(dest);
        using (var zip = ZipFile.Open(dest, ZipArchiveMode.Create))
        {
            foreach (string s in sources)
            {
                if (File.Exists(s)) zip.CreateEntryFromFile(s, Path.GetFileName(s), CompressionLevel.Optimal);
                else if (Directory.Exists(s))
                {
                    string root = Path.GetFileName(s.TrimEnd('\\', '/'));
                    var opt = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
                    bool any = false;
                    foreach (var f in Directory.EnumerateFiles(s, "*", opt))
                    {
                        if (string.Equals(f, dest, StringComparison.OrdinalIgnoreCase)) continue;
                        string rel = Path.Combine(root, Path.GetRelativePath(s, f)).Replace('\\', '/');
                        try { zip.CreateEntryFromFile(f, rel, CompressionLevel.Optimal); any = true; } catch { }
                    }
                    if (!any) zip.CreateEntry(root + "/");
                }
                else throw BridgeException.NotFound("לא נמצא: " + s);
            }
        }
        return new { path = dest };
    }

    private static object Unzip(JsonElement b)
    {
        Kiosk.EnsureFileWriteAllowed();
        string src = Full(b.Req("path"));
        if (!File.Exists(src)) throw BridgeException.NotFound("לא נמצא: " + src);
        string dest = b.Has("dest") ? Full(b.Req("dest"))
            : Unique(Path.Combine(Path.GetDirectoryName(src)!, Path.GetFileNameWithoutExtension(src)));
        FxPolicy.EnsurePathAllowed(dest);
        FsApi.EnsureNotProtected(dest);
        string root = Path.GetFullPath(dest).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        int count = 0;
        using (var zip = ZipFile.OpenRead(src))
        {
            foreach (var e in zip.Entries)
            {
                string target = Path.GetFullPath(Path.Combine(dest, e.FullName));
                // הגנה מ-Zip Slip: אין יציאה מתיקיית היעד
                if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
                if (e.FullName.EndsWith('/') || e.FullName.EndsWith('\\')) { Directory.CreateDirectory(target); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                e.ExtractToFile(target, overwrite: false);
                count++;
            }
        }
        return new { path = dest, files = count };
    }

    private static string Unique(string p)
    {
        if (!File.Exists(p) && !Directory.Exists(p)) return p;
        string dir = Path.GetDirectoryName(p)!, ext = Path.GetExtension(p), name = Path.GetFileNameWithoutExtension(p);
        if (Directory.Exists(p)) { ext = ""; name = Path.GetFileName(p); }
        for (int i = 2; ; i++)
        {
            string c = Path.Combine(dir, name + " (" + i + ")" + ext);
            if (!File.Exists(c) && !Directory.Exists(c)) return c;
        }
    }

    // ---------- גיבוי / שחזור הגדרות ----------
    // סודות ומצב רגעי לא נכנסים לגיבוי ולא נדרסים בשחזור
    private static bool Skip(string k) =>
        k.Contains("hash", StringComparison.OrdinalIgnoreCase) || k.Contains("salt", StringComparison.OrdinalIgnoreCase) ||
        k.Contains("secret", StringComparison.OrdinalIgnoreCase) || k.Contains("token", StringComparison.OrdinalIgnoreCase) ||
        k.StartsWith("auth.") || k.StartsWith("kiosk.twoFactor") || k.StartsWith("airplane.") ||
        k is "kiosk.active" or "kiosk.activatedBy" or "kiosk.confirmed" or "kiosk.schedule.skipUntil";

    private static object Export(JsonElement b)
    {
        JsonObject cfg;
        try { cfg = JsonNode.Parse(File.ReadAllText(Config.FilePath)) as JsonObject ?? new JsonObject(); }
        catch { cfg = new JsonObject(); }
        var bridge = new JsonObject();
        foreach (var kv in cfg) if (!Skip(kv.Key)) bridge[kv.Key] = kv.Value?.DeepClone();
        var root = new JsonObject
        {
            ["format"] = "otzaria-settings-backup",
            ["version"] = 1,
            ["created"] = DateTime.UtcNow.ToString("o"),
            ["bridge"] = bridge,
        };
        // הגדרות התוספים (נשלחות מהתוסף עצמו, אם יש)
        if (b.ValueKind == JsonValueKind.Object && b.TryGetProperty("plugins", out var pl))
            root["plugins"] = JsonNode.Parse(pl.GetRawText());
        string desk = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        string path = Unique(Path.Combine(desk, "גיבוי הגדרות אוצריא " + DateTime.Now.ToString("yyyy-MM-dd") + ".json"));
        // TypeInfoResolver מפורש הכרחי - ר' הערה ב-HardLock.cs.Save
        File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true, Encoder = Server.Json.Encoder,
            TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
        }));
        return new { path, keys = bridge.Count };
    }

    private static object Import(JsonElement b)
    {
        if (Kiosk.IsActive) throw BridgeException.Locked("אי אפשר לשחזר הגדרות במצב נעילה");
        // שחזור משנה גם את הגדרות הנעילה — דורש את סיסמת הנעילה כשהוגדרה
        // [w4] לא נשען על hash מקומי: כשהשירות מחזיק את הסיסמה העותק המקומי נמחק (Kiosk.PurgeLocalPassword)
        if (Kiosk.PasswordConfigured || Kiosk.ServiceDown())
        {
            Kiosk.RequirePassword(b.Str("password"));
            TwoFactor.Require("שחזור הגדרות");
        }
        string path = Full(b.Req("path"));
        JsonObject root;
        try { root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject; }
        catch { throw BridgeException.BadRequest("הקובץ אינו גיבוי הגדרות תקין"); }
        if (root == null || (string)root["format"] != "otzaria-settings-backup" || root["bridge"] is not JsonObject bridge)
            throw BridgeException.BadRequest("הקובץ אינו גיבוי הגדרות תקין");
        int n = 0;
        foreach (var kv in bridge)
        {
            if (Skip(kv.Key) || kv.Key.StartsWith("fx.policy")) continue;   // מדיניות הסייר לא משוחזרת מקובץ
            Config.Set(kv.Key, kv.Value?.DeepClone());
            n++;
        }
        Config.Save();
        return new { restored = n, plugins = root["plugins"] };
    }
}
