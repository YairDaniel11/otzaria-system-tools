using System.Collections.Specialized;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;

namespace OtzariaBridge;

/// <summary>נקודות הקצה /fs/* לפי docs/BRIDGE_API.md</summary>
public static class FsApi
{
    private static readonly HashSet<string> TextExt = new(StringComparer.OrdinalIgnoreCase)
        { ".txt", ".md", ".json", ".csv", ".xml", ".html", ".htm", ".js", ".css", ".log", ".ini", ".tsv", ".yml", ".yaml", ".cfg", ".conf",
          ".bat", ".cmd", ".ps1", ".py", ".java", ".cs", ".ts", ".sql", ".srt", ".vtt", ".toml", ".tex", ".properties", ".sh", ".c", ".cpp", ".h",
          ".hpp", ".php", ".rb", ".go", ".rs", ".kt", ".swift", ".lua", ".jsx", ".tsx", ".scss", ".lrc", ".rst", ".bib" };

    /// <summary>סוגי מסמכים (zip עם XML) שחיפוש התוכן קורא מהם טקסט</summary>
    private static readonly HashSet<string> OfficeExt = new(StringComparer.OrdinalIgnoreCase)
        { ".docx", ".xlsx", ".pptx", ".odt", ".ods", ".odp" };

    private static readonly Dictionary<string, string> Mime = new(StringComparer.OrdinalIgnoreCase)
    {
        [".txt"] = "text/plain", [".md"] = "text/markdown", [".json"] = "application/json",
        [".csv"] = "text/csv", [".xml"] = "application/xml", [".html"] = "text/html", [".htm"] = "text/html",
        [".js"] = "text/javascript", [".css"] = "text/css", [".log"] = "text/plain", [".ini"] = "text/plain",
        [".pdf"] = "application/pdf", [".png"] = "image/png", [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif", [".bmp"] = "image/bmp", [".webp"] = "image/webp", [".svg"] = "image/svg+xml",
        [".tif"] = "image/tiff", [".tiff"] = "image/tiff", [".ico"] = "image/x-icon",
        [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".zip"] = "application/zip",
    };

    public static void Register()
    {
        FxPolicy.Register();
        Server.Route("/fs/roots", _ => Roots());
        Server.Route("/fs/list", List);
        Server.Route("/fs/stat", b => Stat(FullPath(b.Req("path"))));
        Server.Route("/fs/mkdir", Mkdir);
        Server.Route("/fs/rename", Rename);
        Server.Route("/fs/attributes", SetAttributes);
        Server.Route("/fs/copy", b => Transfer(b, move: false));
        Server.Route("/fs/move", b => Transfer(b, move: true));
        Server.Route("/fs/delete", Delete);
        Server.Route("/fs/read", Read);
        Server.Route("/fs/write-text", WriteText);
        Server.Route("/fs/open", Open);
        Server.Route("/fs/reveal", Reveal);
        Server.Route("/fs/clipboard-get", _ => ClipboardGet());
        Server.Route("/fs/clipboard-set", ClipboardSet);
    }

    // ---------- עזרים ----------

    private static string FullPath(string p)
    {
        if (string.IsNullOrWhiteSpace(p)) throw BridgeException.BadRequest("נתיב ריק");
        p = Environment.ExpandEnvironmentVariables(p.Trim());
        if (!Path.IsPathFullyQualified(p)) throw BridgeException.BadRequest("נדרש נתיב מלא: " + p);
        string full = PathPolicy.LongName(Path.GetFullPath(p));   // 8.3 -> שם ארוך לפני כל בדיקה
        EnsureNotReadable(full);            // סודות הגשר: חסום לכל פעולה (קריאה, רשימה, חיפוש, העתקה מקור/יעד)
        FxPolicy.EnsurePathAllowed(full);   // מדיניות סייר הקבצים: נעילה או הגבלה לתיקיות
        return full;
    }

    private static string Iso(DateTime t) => t.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

    private static bool IsHidden(FileSystemInfo i) =>
        (i.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0;

    private static bool Exists(string p) => File.Exists(p) || Directory.Exists(p);

    /// <summary>אזורים מוגנים: מחיקה, העברה ושינוי שם אסורים</summary>
    internal static void EnsureNotProtected(string p, bool tree = false)
    {
        // tree: שינוי שם/העברה/מחיקה. WHY (סבב 4): פעולה על אב של אזור מוגן (%LOCALAPPDATA%, Roaming\Microsoft\Windows)
        // מזיזה את המוגן עצמו, לכן חסום גם כשהנתיב הוא אב. ליצירה/כתיבה בתוך אב (קובץ בתיקיית הבית) נשאר מותר.
        // ביקורת #30 ממצא 3: השוואת מחרוזות לבדה עקיפה דרך \\?\, שמות 8.3 וצמתים.
        // בודקים גם את הנתיב כפי שנכתב וגם את הנתיב הסופי שמערכת הקבצים מחזירה.
        if (p.StartsWith(@"\\?\") || p.StartsWith(@"\\.\") || p.StartsWith(@"\??\"))
            throw BridgeException.Forbidden("נתיב מסוג התקן אינו נתמך");
        if (FsGuard.IsBlockedUnc(Path.GetFullPath(p), true, Environment.MachineName))
            throw BridgeException.Forbidden("נתיב רשת (UNC) אינו נתמך בפעולה זו");
        EnsureNotProtectedCore(p, tree);
        string canon = CanonicalPath(p);
        if (!string.Equals(canon, Path.GetFullPath(p), StringComparison.OrdinalIgnoreCase)) EnsureNotProtectedCore(canon, tree);
    }

    /// <summary>הנתיב הסופי (שמות ארוכים, צמתים וקישורים מפוענחים) של הנתיב או של ההורה הקיים הקרוב. הלוגיקה ב-PathPolicy</summary>
    internal static string CanonicalPath(string p) => PathPolicy.Canonical(p, strict: false);

    private static void EnsureNotProtectedCore(string p, bool tree = false)
    {
        string full = Path.GetFullPath(p).TrimEnd('\\');
        string root = Path.GetPathRoot(full)?.TrimEnd('\\') ?? "";
        if (full.Length == 0 || full.Equals(root, StringComparison.OrdinalIgnoreCase))
            throw BridgeException.Forbidden("לא ניתן לשנות שורש כונן");

        var guarded = new List<string>
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Config.Dir,
        };
        // הרצה אוטומטית והתקנה כמשתמש רגיל: Startup (משתמש+מערכת), תפריט התחלה, Tasks, %LOCALAPPDATA%\Programs.
        // WHY: כתיבה לשם שווה הרצת קוד בכניסה הבאה, מחוץ לכל מדיניות הקבצים והתוכנות.
        guarded.AddRange(FsGuard.AutoRunRoots());
        if (tree)
        {
            var anc = guarded.Concat(SecretRoots()).ToList();
            if (FsGuard.IsAncestorOfAny(full, anc)) throw BridgeException.Forbidden("אזור מוגן: הנתיב מכיל אזור מוגן");
        }
        foreach (string g in guarded.Where(x => !string.IsNullOrEmpty(x)))
        {
            string gg = g.TrimEnd('\\');
            if (full.Equals(gg, StringComparison.OrdinalIgnoreCase) ||
                full.StartsWith(gg + "\\", StringComparison.OrdinalIgnoreCase))
                throw BridgeException.Forbidden("אזור מוגן: " + gg);
        }
        // Program Files* בכל כונן
        string rest = full.Substring(root.Length).TrimStart('\\');
        if (rest.StartsWith("Program Files", StringComparison.OrdinalIgnoreCase) &&
            rest.Split('\\').Length >= 1 && rest.Split('\\')[0].StartsWith("Program Files", StringComparison.OrdinalIgnoreCase))
            throw BridgeException.Forbidden("אזור מוגן: Program Files");
    }

    /// <summary>
    /// סודות הגשר (Config.Dir: config.json עם hash/salt של הסיסמה והצימוד; ProgramData\OtzariaBridge) אסורים לכל גישה,
    /// כולל קריאה. בנעילה פעילה נוסף ProgramData וקוצי הרישום. נבדק על הנתיב כפי שנכתב, על השם הארוך (8.3)
    /// ועל הנתיב הסופי (צמתים/קישורים סימבוליים). קישור קשיח (hardlink) אינו מפוענח בנתיב; ר' EnsureNoHardlink.
    /// </summary>
    internal static void EnsureNotReadable(string p)
    {
        if (p.StartsWith(@"\\?\") || p.StartsWith(@"\\.\") || p.StartsWith(@"\??\"))
            throw BridgeException.Forbidden("נתיב מסוג התקן אינו נתמך");
        // UNC עצמי (\\localhost\c$) עוקף את כל בדיקות הנתיב: נדחה תמיד; כל UNC אחר בנעילה/הגבלה
        if (FsGuard.IsBlockedUnc(Path.GetFullPath(p), Kiosk.IsActive || FxPolicy.IsRestricted, Environment.MachineName))
            throw BridgeException.Forbidden("נתיב רשת (UNC) חסום");
        var roots = SecretRoots();
        string full = PathPolicy.LongName(Path.GetFullPath(p));
        if (FsGuard.IsUnderAny(full, roots)) throw BridgeException.Forbidden("אזור מוגן: נתוני הגשר");
        string canon = CanonicalPath(full);
        if (!string.Equals(canon, full, StringComparison.OrdinalIgnoreCase) && FsGuard.IsUnderAny(canon, roots))
            throw BridgeException.Forbidden("אזור מוגן: נתוני הגשר");
    }

    /// <summary>שורשי הסודות, גם בצורתם הקנונית (אם Config.Dir עצמו מאחורי צומת)</summary>
    internal static List<string> SecretRoots()
    {
        var roots = FsGuard.SecretRoots(Kiosk.IsActive, Config.Dir, HardLock.DataDir);
        foreach (string r in roots.ToList())
        {
            string c = CanonicalPath(r);
            if (!string.Equals(c, r, StringComparison.OrdinalIgnoreCase)) roots.Add(c);
        }
        return roots;
    }

    /// <summary>
    /// בנעילה: חסום יצירה/כתיבה של סיומות מריצות (exe, bat, ps1, lnk...) אלא אם היעד בתיקייה שהמנהל אישר במפורש
    /// (לא ברירות המחדל). הסיומת נקבעת לפי השם התקני (בלי נקודות/רווחים בסוף).
    /// </summary>
    internal static void EnsureWriteTargetAllowed(string dest)
    {
        if (!Kiosk.IsActive) return;
        // רשימה לבנה (סבב 4): כל סיומת שאינה מסמך/תמונה/טקסט/מדיה/ארכיב חסומה, לא רק רשימה שחורה
        if (FsGuard.IsWriteBlockedExt(dest) && !FxPolicy.IsInApprovedRoots(dest))
            throw BridgeException.Forbidden("בזמן נעילה אסור ליצור או לשנות קובץ מסוג " + FsGuard.StdExt(dest) + " מחוץ לתיקיות שאושרו");
    }

    /// <summary>
    /// כתיבה לקובץ קיים שיש לו עוד שם (hardlink) עלולה לשנות קובץ מוגן דרך שם בתיקייה מותרת. חסום בנעילה/הגבלה.
    /// </summary>
    internal static void EnsureNoHardlink(string path)
    {
        if (!(Kiosk.IsActive || FxPolicy.IsRestricted)) return;
        if (File.Exists(path) && PathPolicy.HardLinkCount(path) > 1)
            throw BridgeException.Forbidden("הקובץ מקושר בקישור קשיח (hardlink) לשם נוסף: הכתיבה אליו חסומה");
    }

    internal static string UniqueName(string dir, string name)
    {
        string baseName = Path.GetFileNameWithoutExtension(name);
        string ext = Path.GetExtension(name);
        if (Directory.Exists(Path.Combine(dir, name)) && ext.Length > 0 && !File.Exists(Path.Combine(dir, name)))
        {
            baseName = name;
            ext = "";
        }
        for (int i = 2; i < 10000; i++)
        {
            string candidate = Path.Combine(dir, $"{baseName} ({i}){ext}");
            if (!Exists(candidate)) return candidate;
        }
        throw new BridgeException("internal", "לא נמצא שם פנוי", 500);
    }

    // ---------- נקודות קצה ----------

    private static object Roots()
    {
        FxPolicy.EnsureAvailable();
        var drives = new List<object>();
        foreach (var d in DriveInfo.GetDrives())
        {
            string label = "", type = d.DriveType.ToString().ToLowerInvariant();
            long total = 0, free = 0;
            try
            {
                if (d.IsReady)
                {
                    label = d.VolumeLabel;
                    total = d.TotalSize;
                    free = d.AvailableFreeSpace;
                }
            }
            catch { }
            drives.Add(new { path = d.RootDirectory.FullName, label, type, totalBytes = total, freeBytes = free });
        }

        var known = new List<object>();
        foreach (var k in KnownFolders()) known.Add(new { k.id, k.name, k.path });
        if (FxPolicy.IsRestricted)
        {
            // מצב מוגבל: רק התיקיות שהמנהל התיר. כוננים ותיקיות אחרות לא מוצגים
            drives.Clear();
            known.Clear();
            foreach (string r in FxPolicy.ActiveRoots())
                known.Add(new { id = "root", name = Path.GetFileName(r.TrimEnd('\\')) is { Length: > 0 } n ? n : r, path = r });
        }
        return new { drives, known };
    }

    /// <summary>תיקיות המערכת המוכרות שקיימות במחשב (שולחן עבודה, מסמכים...)</summary>
    internal static List<(string id, string name, string path)> KnownFolders()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var known = new List<(string, string, string)>();
        void Add(string id, string name, string path)
        {
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path)) known.Add((id, name, path));
        }
        Add("desktop", "שולחן העבודה", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
        Add("documents", "מסמכים", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
        Add("downloads", "הורדות", KnownDownloads() ?? Path.Combine(home, "Downloads"));
        Add("pictures", "תמונות", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures));
        Add("music", "מוזיקה", Environment.GetFolderPath(Environment.SpecialFolder.MyMusic));
        Add("videos", "סרטונים", Environment.GetFolderPath(Environment.SpecialFolder.MyVideos));
        Add("home", "תיקיית הבית", home);
        return known;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint flags, IntPtr token, out IntPtr path);

    private static string KnownDownloads()
    {
        try
        {
            if (SHGetKnownFolderPath(new Guid("374DE290-123F-4565-9164-39C4925E467B"), 0, IntPtr.Zero, out IntPtr p) == 0)
            {
                string s = Marshal.PtrToStringUni(p);
                Marshal.FreeCoTaskMem(p);
                return s;
            }
        }
        catch { }
        return null;
    }

    private static object List(JsonElement b)
    {
        string path = FullPath(b.Req("path"));
        bool showHidden = b.Bool("showHidden");
        var dir = new DirectoryInfo(path);
        if (!dir.Exists) throw BridgeException.NotFound("התיקייה לא נמצאה: " + path);

        var dirs = new List<object>();
        var files = new List<object>();
        var secret = SecretRoots();
        foreach (var i in dir.EnumerateFileSystemInfos("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 })
                     .OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            if (FsGuard.IsUnderAny(i.FullName, secret)) continue;   // לא חושפים את תיקיית נתוני הגשר ברשימת ההורה
            bool hidden = IsHidden(i);
            if (hidden && !showHidden) continue;
            if (i is DirectoryInfo)
                dirs.Add(new { name = i.Name, path = i.FullName, type = "dir", size = 0L, modified = Iso(i.LastWriteTime), ext = "", hidden });
            else if (i is FileInfo f)
                files.Add(new { name = f.Name, path = f.FullName, type = "file", size = f.Length, modified = Iso(f.LastWriteTime), ext = f.Extension.TrimStart('.').ToLowerInvariant(), hidden });
        }
        string parent = dir.Parent?.FullName;
        if (parent != null && !FxPolicy.IsPathAllowed(parent)) parent = null;   // לא חושפים נתיב מחוץ לתיקיות המותרות
        return new { path = dir.FullName, parent, entries = dirs.Concat(files) };
    }

    private static object Stat(string path)
    {
        if (File.Exists(path))
        {
            var f = new FileInfo(path);
            return new { name = f.Name, path = f.FullName, type = "file", size = f.Length, created = Iso(f.CreationTime), modified = Iso(f.LastWriteTime), readOnly = f.IsReadOnly, hidden = IsHidden(f), itemCount = (int?)null };
        }
        if (Directory.Exists(path))
        {
            var d = new DirectoryInfo(path);
            int? count = null;
            try { count = d.EnumerateFileSystemInfos().Count(); } catch { }
            return new { name = d.Name, path = d.FullName, type = "dir", size = 0L, created = Iso(d.CreationTime), modified = Iso(d.LastWriteTime), readOnly = (d.Attributes & FileAttributes.ReadOnly) != 0, hidden = IsHidden(d), itemCount = count };
        }
        throw BridgeException.NotFound("לא נמצא: " + path);
    }

    private static object Mkdir(JsonElement b)
    {
        string path = FullPath(b.Req("path"));
        Kiosk.EnsureFileWriteAllowed();
        EnsureNotProtected(path);
        if (Exists(path)) throw BridgeException.Exists("כבר קיים: " + path);
        Directory.CreateDirectory(path);
        return new { path };
    }

    /// <summary>שינוי התכונות "מוסתר" ו"קריאה בלבד" של קובץ או תיקייה; מחזיר את המאפיינים החדשים</summary>
    private static object SetAttributes(JsonElement b)
    {
        Kiosk.EnsureFileWriteAllowed();
        string path = FullPath(b.Req("path"));
        if (!Exists(path)) throw BridgeException.NotFound("לא נמצא: " + path);
        EnsureNotProtected(path);
        var attrs = File.GetAttributes(path);
        if (b.TryGetProperty("hidden", out var h) && (h.ValueKind == JsonValueKind.True || h.ValueKind == JsonValueKind.False))
            attrs = h.GetBoolean() ? attrs | FileAttributes.Hidden : attrs & ~FileAttributes.Hidden;
        if (b.TryGetProperty("readOnly", out var r) && (r.ValueKind == JsonValueKind.True || r.ValueKind == JsonValueKind.False))
            attrs = r.GetBoolean() ? attrs | FileAttributes.ReadOnly : attrs & ~FileAttributes.ReadOnly;
        File.SetAttributes(path, attrs);
        return Stat(path);
    }

    private static object Rename(JsonElement b)
    {
        Kiosk.EnsureFileWriteAllowed();
        string path = FullPath(b.Req("path"));
        string newName = b.Req("newName").Trim();
        if (newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || newName is "." or "..")
            throw BridgeException.BadRequest("שם לא חוקי: " + newName);
        if (!Exists(path)) throw BridgeException.NotFound("לא נמצא: " + path);
        EnsureNotProtected(path, tree: true);

        string dest = Path.Combine(Path.GetDirectoryName(path)!, newName);
        FxPolicy.EnsurePathAllowed(dest);
        EnsureNotProtected(dest);
        EnsureWriteTargetAllowed(dest);   // שינוי שם ל-.bat/.exe וכו' = יצירת קובץ מריץ
        bool caseOnly = string.Equals(dest, path, StringComparison.OrdinalIgnoreCase);
        if (!caseOnly && Exists(dest)) throw BridgeException.Exists("כבר קיים: " + dest);

        if (Directory.Exists(path))
        {
            if (caseOnly)
            {
                string tmp = path + ".~rn" + Guid.NewGuid().ToString("N")[..6];
                Directory.Move(path, tmp);
                Directory.Move(tmp, dest);
            }
            else Directory.Move(path, dest);
        }
        else File.Move(path, dest);
        return new { path = dest };
    }

    private static object Transfer(JsonElement b, bool move)
    {
        Kiosk.EnsureFileWriteAllowed();
        string destDir = FullPath(b.Req("destDir"));
        if (!Directory.Exists(destDir)) throw BridgeException.NotFound("תיקיית היעד לא נמצאה: " + destDir);
        EnsureNotProtected(destDir);   // יעד ההעתקה/העברה הוא כתיבה: אסור ל-Startup, Program Files וכו'
        string conflict = b.Str("conflict", "rename");
        if (conflict is not ("skip" or "overwrite" or "rename"))
            throw BridgeException.BadRequest("conflict חייב להיות skip, overwrite או rename");

        int done = 0, skipped = 0;
        var failed = new List<object>();
        foreach (string raw in b.StrArray("sources"))
        {
            string src = raw;
            try
            {
                src = FullPath(raw);
                if (!Exists(src)) throw BridgeException.NotFound("לא נמצא");
                if (move) EnsureNotProtected(src, tree: true);
                if (File.Exists(src)) EnsureNoHardlink(src);   // קריאת המקור דרך שם נוסף לקובץ מוגן

                string dest = Path.Combine(destDir, Path.GetFileName(src.TrimEnd('\\')));
                EnsureNotProtected(dest);
                if (File.Exists(src)) EnsureWriteTargetAllowed(dest);
                FxPolicy.EnsurePathAllowed(dest);   // היעד עצמו (למשל קישור/צומת בתוך התיקייה) חייב להישאר בתוך המדיניות
                if (Directory.Exists(src) &&
                    (destDir + "\\").StartsWith(src.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                    throw BridgeException.BadRequest("לא ניתן להעתיק תיקייה לתוך עצמה");

                if (string.Equals(dest, src, StringComparison.OrdinalIgnoreCase))
                {
                    if (move) { skipped++; continue; }
                    dest = UniqueName(destDir, Path.GetFileName(src));
                }
                else if (Exists(dest))
                {
                    if (conflict == "skip") { skipped++; continue; }
                    if (conflict == "rename") dest = UniqueName(destDir, Path.GetFileName(src));
                    else
                    {
                        EnsureNoHardlink(dest);
                        if (Directory.Exists(dest) && File.Exists(src) || File.Exists(dest) && Directory.Exists(src))
                            throw BridgeException.Exists("קיים פריט מסוג אחר באותו שם");
                        if (File.Exists(dest) && !Directory.Exists(src)) File.Delete(dest);
                    }
                }

                FxPolicy.EnsurePathAllowed(dest);
                if (Directory.Exists(src))
                {
                    bool sameVolume = string.Equals(Path.GetPathRoot(src), Path.GetPathRoot(dest), StringComparison.OrdinalIgnoreCase);
                    if (move && sameVolume && !Directory.Exists(dest)) Directory.Move(src, dest);
                    else
                    {
                        CopyDir(src, dest, overwrite: conflict == "overwrite");
                        if (move) Directory.Delete(src, true);
                    }
                }
                else
                {
                    if (move) File.Move(src, dest, overwrite: true);
                    else File.Copy(src, dest, overwrite: true);
                }
                done++;
            }
            catch (BridgeException bx) when (bx.Code.StartsWith("fx_")) { throw; }   // הפרת מדיניות מבטלת את כל הבקשה
            catch (Exception ex)
            {
                failed.Add(new { path = src, message = ex.Message });
            }
        }
        return move ? new { moved = done, skipped, failed } : new { copied = done, skipped, failed };
    }

    /// <summary>פריט (קובץ/תיקייה) תחת שורש סודי, כפי שנכתב או בנתיב הסופי (קישור סימבולי)</summary>
    internal static bool IsSecretItem(string path, List<string> secret)
    {
        if (FsGuard.IsUnderAny(path, secret)) return true;
        string c = CanonicalPath(path);
        return !string.Equals(c, path, StringComparison.OrdinalIgnoreCase) && FsGuard.IsUnderAny(c, secret);
    }

    private static void CopyDir(string src, string dest, bool overwrite, int depth = 0)
    {
        // תקרת עומק כהגנה אחרונה מפני רקורסיה פתולוגית
        if (depth > 64) throw new IOException("עומק התיקיות עמוק מדי להעתקה: " + src);
        Directory.CreateDirectory(dest);
        var secret = SecretRoots();   // WHY: העתקת אב של Config.Dir לא תעתיק את config.json
        bool strict = Kiosk.IsActive || FxPolicy.IsRestricted;
        foreach (string f in Directory.GetFiles(src))
        {
            if (IsSecretItem(f, secret)) continue;
            if (strict && PathPolicy.HardLinkCount(f) > 1) continue;   // שם נוסף לקובץ מוגן
            string df = Path.Combine(dest, Path.GetFileName(f));
            EnsureWriteTargetAllowed(df);   // גם קבצים בתוך תיקייה: לא מעבירים .exe/.bat לתיקייה אחרת בנעילה
            File.Copy(f, df, overwrite);
        }
        foreach (string d in Directory.GetDirectories(src))
        {
            // דילוג על נקודות reparse (junction/symlink). junction שמצביע על תיקיית־אב יוצר לולאה
            // אינסופית → StackOverflowException שאי אפשר ללכוד והורג את כל התהליך (הגשר והמגש).
            if ((File.GetAttributes(d) & FileAttributes.ReparsePoint) != 0) continue;
            if (IsSecretItem(d, secret)) continue;
            CopyDir(d, Path.Combine(dest, Path.GetFileName(d)), overwrite, depth + 1);
        }
    }

    // מחיקה לסל המיחזור דרך IFileOperation (ולא SHFileOperation הישן: הוא לא תומך בנתיבים
    // ארוכים מ-MAX_PATH ונכשל עם קוד 124/DE_INVALIDFILES על תיקיות עמוקות בשמות עבריים/ארוכים)
    [ComImport, Guid("3AD05575-8857-4850-9277-11B85BDB8E09")] private class FileOperationCoClass { }

    [ComImport, Guid("947aab5f-0a5c-4c13-b4d6-4bf7836fc9f8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperation
    {
        void Advise(IntPtr pfops, out uint pdwCookie);
        void Unadvise(uint dwCookie);
        void SetOperationFlags(uint dwOperationFlags);
        void SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string pszMessage);
        void SetProgressDialog(IntPtr popd);
        void SetProperties(IntPtr pproparray);
        void SetOwnerWindow(IntPtr hwndOwner);
        void ApplyPropertiesToItem(IShellItem psiItem);
        void ApplyPropertiesToItems(IntPtr punkItems);
        void RenameItem(IShellItem psiItem, [MarshalAs(UnmanagedType.LPWStr)] string pszNewName, IntPtr pfopsItem);
        void RenameItems(IntPtr pUnkItems, [MarshalAs(UnmanagedType.LPWStr)] string pszNewName);
        void MoveItem(IShellItem psiItem, IShellItem psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string pszNewName, IntPtr pfopsItem);
        void MoveItems(IntPtr punkItems, IShellItem psiDestinationFolder);
        void CopyItem(IShellItem psiItem, IShellItem psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string pszCopyName, IntPtr pfopsItem);
        void CopyItems(IntPtr punkItems, IShellItem psiDestinationFolder);
        void DeleteItem(IShellItem psiItem, IntPtr pfopsItem);
        void DeleteItems(IntPtr punkItems);
        void NewItem(IShellItem psiDestinationFolder, uint dwFileAttributes, [MarshalAs(UnmanagedType.LPWStr)] string pszName, [MarshalAs(UnmanagedType.LPWStr)] string pszTemplateName, IntPtr pfopsItem);
        void PerformOperations();
        [return: MarshalAs(UnmanagedType.Bool)] bool GetAnyOperationsAborted();
    }

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        void GetParent(out IShellItem ppsi);
        void GetDisplayName(uint sigdnName, out IntPtr ppszName);
        void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        void Compare(IShellItem psi, uint hint, out int piOrder);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName([MarshalAs(UnmanagedType.LPWStr)] string path, IntPtr pbc, ref Guid riid, out IShellItem item);

    private static void Recycle(string path)
    {
        const uint FOF_SILENT = 0x4, FOF_NOCONFIRMATION = 0x10, FOF_ALLOWUNDO = 0x40, FOF_NOERRORUI = 0x400;
        var fo = (IFileOperation)new FileOperationCoClass();
        IShellItem item = null;
        try
        {
            fo.SetOperationFlags(FOF_SILENT | FOF_NOCONFIRMATION | FOF_ALLOWUNDO | FOF_NOERRORUI);
            Guid iid = typeof(IShellItem).GUID;
            SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out item);
            fo.DeleteItem(item, IntPtr.Zero);
            fo.PerformOperations();
            if (fo.GetAnyOperationsAborted())
                throw new IOException("המחיקה לסל המיחזור בוטלה");
        }
        catch (COMException ex)
        {
            throw new IOException("המחיקה לסל המיחזור נכשלה (קוד 0x" + ex.HResult.ToString("X8") + ")", ex);
        }
        finally
        {
            if (item != null) Marshal.ReleaseComObject(item);
            Marshal.ReleaseComObject(fo);
        }
    }

    private static object Delete(JsonElement b)
    {
        Kiosk.EnsureFileWriteAllowed();
        bool permanent = b.Bool("permanent");
        int deleted = 0;
        var failed = new List<object>();
        foreach (string raw in b.StrArray("paths"))
        {
            string p = raw;
            try
            {
                p = FullPath(raw);
                if (!Exists(p)) throw BridgeException.NotFound("לא נמצא");
                EnsureNotProtected(p, tree: true);
                if (!permanent) Recycle(p);
                else if (Directory.Exists(p)) Directory.Delete(p, true);
                else
                {
                    File.SetAttributes(p, FileAttributes.Normal);
                    File.Delete(p);
                }
                deleted++;
            }
            catch (BridgeException bx) when (bx.Code.StartsWith("fx_")) { throw; }
            catch (Exception ex)
            {
                failed.Add(new { path = p, message = ex.Message });
            }
        }
        return new { deleted, failed };
    }

    private static object Read(JsonElement b)
    {
        string path = FullPath(b.Req("path"));
        if (!File.Exists(path)) throw BridgeException.NotFound("הקובץ לא נמצא: " + path);
        long max = Math.Clamp(b.Long("maxBytes", 5L * 1024 * 1024), 1, 50L * 1024 * 1024);
        var fi = new FileInfo(path);
        string ext = fi.Extension;
        long flen;
        byte[] buf;
        // TOCTOU: הבדיקה (סודות + מדיניות) על הנתיב הסופי של ה-handle שנפתח בפועל, והקריאה ממנו. לא נפתח שוב בשם
        using (var fs = PathPolicy.OpenVerifiedRead(path, fin => { EnsureNotReadable(fin); FxPolicy.EnsurePathAllowed(fin); }))
        {
            // hardlink: שם שני לקובץ מוגן (config.json) בתיקייה מותרת; הנתיב הסופי לא מגלה זאת. חסום בנעילה
            if (Kiosk.IsActive && PathPolicy.HardLinkCountOfHandle(fs.SafeFileHandle) > 1)
                throw BridgeException.Forbidden("הקובץ מקושר בקישור קשיח (hardlink) לשם נוסף: הקריאה חסומה");
            flen = fs.Length;
            int len = (int)Math.Min(flen, max);
            buf = new byte[len];
            int read = 0;
            while (read < len)
            {
                int n = fs.Read(buf, read, len - read);
                if (n == 0) break;
                read += n;
            }
            if (read < len) Array.Resize(ref buf, read);
        }

        bool truncated = flen > max;
        string mime = Mime.TryGetValue(ext, out var m) ? m : "application/octet-stream";
        if (TextExt.Contains(ext))
        {
            string content = DecodeText(buf);
            return new { name = fi.Name, size = flen, mime, encoding = "utf8", content, truncated };
        }
        return new { name = fi.Name, size = flen, mime, encoding = "base64", content = Convert.ToBase64String(buf), truncated };
    }

    private static string DecodeText(byte[] buf)
    {
        if (buf.Length >= 3 && buf[0] == 0xEF && buf[1] == 0xBB && buf[2] == 0xBF) return Encoding.UTF8.GetString(buf, 3, buf.Length - 3);
        if (buf.Length >= 2 && buf[0] == 0xFF && buf[1] == 0xFE) return Encoding.Unicode.GetString(buf, 2, buf.Length - 2);
        if (buf.Length >= 2 && buf[0] == 0xFE && buf[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(buf, 2, buf.Length - 2);
        try
        {
            return new UTF8Encoding(false, true).GetString(buf);
        }
        catch (DecoderFallbackException)
        {
            // קבצי טקסט ישנים בעברית (Windows-1255)
            try
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                return Encoding.GetEncoding(1255).GetString(buf);
            }
            catch { return Encoding.UTF8.GetString(buf); }
        }
    }

    private static object WriteText(JsonElement b)
    {
        string path = FullPath(b.Req("path"));
        Kiosk.EnsureFileWriteAllowed();
        EnsureNotProtected(path);
        string content = b.Str("content", "");
        bool overwrite = b.Bool("overwrite");
        if (Directory.Exists(path)) throw BridgeException.Exists("קיימת תיקייה בשם זה");
        if (File.Exists(path) && !overwrite) throw BridgeException.Exists("הקובץ כבר קיים: " + path);
        string dir = Path.GetDirectoryName(path);
        if (!Directory.Exists(dir)) throw BridgeException.NotFound("התיקייה לא נמצאה: " + dir);
        EnsureWriteTargetAllowed(path);   // .bat/.exe/.lnk וכו' בנעילה מחוץ לתיקיות מאושרות
        EnsureNoHardlink(path);
        // TOCTOU: פותחים handle בלי לקטום, בודקים את הנתיב הסופי שלו (הגנה + מדיניות + סיומת), ורק אז קוטמים וכותבים
        using (var fs = new FileStream(path, overwrite ? FileMode.OpenOrCreate : FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        {
            string fin = PathPolicy.FinalPathOfHandle(fs.SafeFileHandle) ?? throw new IOException("פענוח הנתיב הסופי נכשל");
            EnsureNotProtected(fin);
            FxPolicy.EnsurePathAllowed(fin);
            EnsureWriteTargetAllowed(fin);
            if ((Kiosk.IsActive || FxPolicy.IsRestricted) && PathPolicy.HardLinkCountOfHandle(fs.SafeFileHandle) > 1)
                throw BridgeException.Forbidden("הקובץ מקושר בקישור קשיח (hardlink) לשם נוסף: הכתיבה אליו חסומה");
            fs.SetLength(0);
            var bytes = new UTF8Encoding(false).GetBytes(content);
            fs.Write(bytes, 0, bytes.Length);
        }
        return new { path, size = new FileInfo(path).Length };
    }

    private static object Open(JsonElement b)
    {
        string path = FullPath(b.Req("path"));
        if (!Exists(path)) throw BridgeException.NotFound("לא נמצא: " + path);
        EnsureNoHardlink(path);   // שם נוסף לקובץ מוגן נפתח באפליקציה חיצונית = קריאה
        if (FxPolicy.IsRestricted)
        {
            // פתיחת תיקייה או קיצור דרך מפעילה את סייר Windows עצמו, שאינו כפוף למדיניות
            string e = Path.GetExtension(path).ToLowerInvariant();
            if (Directory.Exists(path) || e is ".lnk" or ".url" or ".scf" or ".appref-ms")
                throw FxPolicy.Outside("במצב מוגבל אי אפשר לפתוח תיקיות וקיצורי דרך בסייר Windows");
        }
        if (Kiosk.IsActive || FxPolicy.IsRestricted)
        {
            // WHY: סיומת לפי שם תקני (בלי נקודות/רווחים בסוף) ורשימה שחורה קשיחה שגם הוספה ידנית לרשימת
            // הסיומות המותרות לא מבטלת: קוד, קיצורים ומאקרו. מסמכי Office נבדקים (מאקרו/DDE/תבנית מרוחקת)
            if (FsGuard.IsDangerousExt(path) || FsGuard.IsMacroExt(path))
                throw BridgeException.Forbidden("סוג הקובץ חסום לפתיחה: " + FsGuard.StdExt(path));
        }
        if (Kiosk.IsActive)
        {
            string ext = FsGuard.StdExt(path).TrimStart('.');
            bool allowed = File.Exists(path) && ext.Length > 0 && Kiosk.AllowedExtensions.Any(x =>
                x.TrimStart('.').Equals(ext, StringComparison.OrdinalIgnoreCase));
            if (!allowed) throw BridgeException.Locked();
        }
        if ((Kiosk.IsActive || FxPolicy.IsRestricted) && File.Exists(path))
        {
            string why = FsGuard.InspectDocument(path);
            if (why != null) throw BridgeException.Forbidden("הקובץ נחסם: " + why);
        }
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        return new { opened = true };
    }

    private static object Reveal(JsonElement b)
    {
        if (Kiosk.IsActive) throw BridgeException.Locked();
        string path = FullPath(b.Req("path"));
        if (FxPolicy.IsRestricted) throw FxPolicy.Outside("במצב מוגבל אי אפשר להציג בסייר Windows");
        if (!Exists(path)) throw BridgeException.NotFound("לא נמצא: " + path);
        string args = File.Exists(path) ? "/select,\"" + path + "\"" : "\"" + path + "\"";
        // בלי זה: השומר (Kiosk.Watch, רץ כל 2 שניות) לא מכיר explorer.exe שנפתח כך כ"מותר", ומחזיר
        // את אוצריא לחזית כמעט מיד - נראה כאילו הסייר "נפתח ברקע" בעוד שבפועל הוא נפתח וממש נסגר-לחזית
        Programs.GrantForegroundGrace();
        Process.Start(new ProcessStartInfo("explorer.exe", args) { UseShellExecute = true });
        return new { ok = true };
    }

    // ---------- לוח ----------

    private static object ClipboardGet() => Sta.Run(() =>
    {
        FxPolicy.EnsureAvailable();
        var list = Clipboard.ContainsFileDropList() ? Clipboard.GetFileDropList().Cast<string>().Where(FxPolicy.IsPathAllowed).ToArray() : Array.Empty<string>();
        bool cut = false;
        if (Clipboard.GetData("Preferred DropEffect") is MemoryStream ms && ms.Length >= 4)
        {
            var bytes = ms.ToArray();
            cut = (bytes[0] & 2) != 0;
        }
        return (object)new { paths = list, cut };
    });

    private static object ClipboardSet(JsonElement b)
    {
        string[] paths = b.StrArray("paths").Select(FullPath).ToArray();
        bool cut = b.Bool("cut");
        Sta.Run(() =>
        {
            if (paths.Length == 0)
            {
                Clipboard.Clear();
                return 0;
            }
            var col = new StringCollection();
            col.AddRange(paths);
            var data = new DataObject();
            data.SetFileDropList(col);
            data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes(cut ? 2 : 5)));
            Clipboard.SetDataObject(data, true);
            return 0;
        });
        return new { ok = true };
    }

    // ---------- חיפוש זורם (NDJSON) ----------

    private static readonly string[] SkipDirNames =
        { "$Recycle.Bin", "System Volume Information", "Windows", "WinSxS", "$WinREAgent", "Config.Msi", "node_modules", ".git" };

    public static async Task Search(HttpContext ctx, JsonElement b)
    {
        string root = FullPath(b.Req("root"));
        if (!Directory.Exists(root)) throw BridgeException.NotFound("תיקיית החיפוש לא נמצאה: " + root);
        string query = b.Str("query", "") ?? "";
        string contentQuery = b.Str("contentQuery");
        if (query.Length == 0 && string.IsNullOrEmpty(contentQuery))
            throw BridgeException.BadRequest("חסר query");
        var exts = new HashSet<string>(b.StrArray("extensions").Select(e => "." + e.TrimStart('.')), StringComparer.OrdinalIgnoreCase);
        int maxResults = (int)Math.Clamp(b.Long("maxResults", 500), 1, 10000);
        bool includeDirs = b.Bool("includeDirs", true);

        Func<string, bool> match;
        if (query.Length == 0) match = _ => true;
        else if (query.IndexOfAny(new[] { '*', '?' }) >= 0)
        {
            var rx = new Regex("^" + Regex.Escape(query).Replace("\\*", ".*").Replace("\\?", ".") + "$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            match = n => rx.IsMatch(n);
        }
        else match = n => n.Contains(query, StringComparison.CurrentCultureIgnoreCase);

        var res = ctx.Response;
        res.StatusCode = 200;
        res.ContentType = "application/x-ndjson; charset=utf-8";
        res.Headers.CacheControl = "no-cache";
        var ct = ctx.RequestAborted;

        async Task Emit(object o)
        {
            await res.WriteAsync(JsonSerializer.Serialize(o, Server.Json) + "\n", ct);
            await res.Body.FlushAsync(ct);
        }

        long scanned = 0;
        int hits = 0;
        bool truncated = false, cancelled = false;
        var sw = Stopwatch.StartNew();
        long lastProgress = 0;
        var stack = new Stack<string>();
        stack.Push(root);
        var opts = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        var secretRoots = SecretRoots();   // נתוני הגשר לעולם לא נסרקים ולא מופיעים בתוצאות

        try
        {
            while (stack.Count > 0 && !truncated)
            {
                if (ct.IsCancellationRequested) { cancelled = true; break; }
                string dir = stack.Pop();
                IEnumerable<FileSystemInfo> items;
                try { items = new DirectoryInfo(dir).EnumerateFileSystemInfos("*", opts).ToList(); }
                catch { continue; }

                foreach (var i in items)
                {
                    scanned++;
                    if (FsGuard.IsUnderAny(i.FullName, secretRoots)) continue;
                    bool isDir = i is DirectoryInfo;
                    if (isDir)
                    {
                        if (!SkipDirNames.Contains(i.Name, StringComparer.OrdinalIgnoreCase) &&
                            (i.Attributes & FileAttributes.System) == 0)
                            stack.Push(i.FullName);
                    }

                    bool ok;
                    string snippet = null;
                    if (isDir) ok = includeDirs && string.IsNullOrEmpty(contentQuery) && exts.Count == 0 && match(i.Name);
                    else
                    {
                        ok = (exts.Count == 0 || exts.Contains(i.Extension)) && match(i.Name);
                        if (ok && !string.IsNullOrEmpty(contentQuery))
                        {
                            snippet = ContentSnippet((FileInfo)i, contentQuery);
                            ok = snippet != null;
                        }
                    }

                    if (ok)
                    {
                        hits++;
                        await Emit(new
                        {
                            type = "hit",
                            name = i.Name,
                            path = i.FullName,
                            entryType = isDir ? "dir" : "file",
                            size = isDir ? 0L : ((FileInfo)i).Length,
                            modified = Iso(i.LastWriteTime),
                            snippet,
                        });
                        if (hits >= maxResults) { truncated = true; break; }
                    }

                    if (sw.ElapsedMilliseconds - lastProgress >= 300)
                    {
                        lastProgress = sw.ElapsedMilliseconds;
                        await Emit(new { type = "progress", scanned, current = dir });
                    }
                }
            }
            await Emit(new { type = "done", scanned, hits, truncated, cancelled });
        }
        catch (OperationCanceledException) { }
        catch (IOException) when (ct.IsCancellationRequested) { }
    }

    /// <summary>
    /// מחפש את המילה בתוכן הקובץ (טקסט, וגם Word/Excel/PowerPoint/OpenDocument) ומחזיר קטע קצר סביב ההתאמה הראשונה,
    /// או null אם אין התאמה. הקטע מוצג בשורת התוצאה, והצופה פותח את הקובץ במקום ההתאמה.
    /// </summary>
    private static string ContentSnippet(FileInfo f, string q)
    {
        try
        {
            // hardlink לקובץ מוגן (config.json) בתוך עץ החיפוש: לא קוראים תוכן מקבצים עם יותר משם אחד
            if ((Kiosk.IsActive || FxPolicy.IsRestricted) && PathPolicy.HardLinkCount(f.FullName) > 1) return null;
            string text;
            if (OfficeExt.Contains(f.Extension))
            {
                if (f.Length > 40L * 1024 * 1024) return null;
                text = OfficeText(f.FullName, f.Extension.ToLowerInvariant());
            }
            else
            {
                if (!TextExt.Contains(f.Extension) || f.Length > 4L * 1024 * 1024) return null;
                text = DecodeText(File.ReadAllBytes(f.FullName));
            }
            int at = text.IndexOf(q, StringComparison.CurrentCultureIgnoreCase);
            if (at < 0) return null;
            int from = Math.Max(0, at - 45), to = Math.Min(text.Length, at + q.Length + 70);
            return Regex.Replace(text.Substring(from, to - from), @"\s+", " ").Trim();
        }
        catch { return null; }
    }

    /// <summary>טקסט גולמי ממסמך zip של Office/OpenDocument (בלי תגיות XML)</summary>
    private static string OfficeText(string path, string ext)
    {
        using var z = System.IO.Compression.ZipFile.OpenRead(path);
        var parts = new List<string>();
        switch (ext)
        {
            case ".docx": parts.Add("word/document.xml"); break;
            case ".xlsx": parts.Add("xl/sharedStrings.xml"); break;
            case ".pptx": parts.AddRange(z.Entries.Where(e => Regex.IsMatch(e.FullName, @"^ppt/slides/slide\d+\.xml$")).Select(e => e.FullName)); break;
            default: parts.Add("content.xml"); break;
        }
        var sb = new StringBuilder();
        foreach (string name in parts)
        {
            var en = z.GetEntry(name);
            if (en == null) continue;
            using var rd = new StreamReader(en.Open(), Encoding.UTF8);
            var buf = new char[8_000_000];
            int got = 0, n;
            while (got < buf.Length && (n = rd.Read(buf, got, buf.Length - got)) > 0) got += n;   // תקרה נגד קובץ שמתפוצץ בפריסה
            string xml = new string(buf, 0, got);
            xml = Regex.Replace(xml, @"</(w:p|a:p|text:p|text:h|si)>", "\n");
            sb.Append(System.Net.WebUtility.HtmlDecode(Regex.Replace(xml, "<[^>]+>", ""))).Append('\n');
            if (sb.Length > 8_000_000) break;
        }
        return sb.ToString();
    }
}

/// <summary>הרצת פעולה על חוט STA (נדרש ללוח של Windows)</summary>
public static class Sta
{
    public static T Run<T>(Func<T> fn)
    {
        T result = default;
        Exception error = null;
        var t = new Thread(() =>
        {
            try { result = fn(); }
            catch (Exception ex) { error = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.IsBackground = true;
        t.Start();
        if (!t.Join(10000)) throw new TimeoutException("הלוח אינו מגיב");
        if (error != null) throw error;
        return result;
    }
}
