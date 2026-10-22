using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace OtzariaBridge;

/// <summary>
/// לוגיקה טהורה (ללא Config/Kiosk) להגנת מערכת הקבצים: אילו מיקומים סודיים או מריצים-אוטומטית, אילו סיומות מסוכנות,
/// ובדיקת מסמכי Office לפני פתיחה (מאקרו, DDE, תבנית/משאב מרוחק). FsApi/RecycleApi מחברים אותה למדיניות.
/// כל כשל בפענוח מסמך = חסום (fail-closed).
/// </summary>
public static class FsGuard
{
    /// <summary>סיומות שמריצות קוד או מוסיפות הרצה אוטומטית. אסור ליצור/לכתוב אותן בנעילה מחוץ לתיקיות מאושרות</summary>
    public static readonly HashSet<string> DangerousExt = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".bat", ".cmd", ".ps1", ".vbs", ".js", ".jse", ".wsf", ".lnk", ".dll", ".reg", ".scr", ".msi", ".hta", ".cpl", ".url",
    };

    /// <summary>פורמטי מסמך עם מאקרו: חסומים לפתיחה בנעילה, גם אם המנהל הוסיף אותם לרשימת הסיומות</summary>
    public static readonly HashSet<string> MacroExt = new(StringComparer.OrdinalIgnoreCase)
    {
        ".docm", ".dotm", ".xlsm", ".xltm", ".xlam", ".pptm", ".potm", ".ppam", ".ppsm", ".sldm",
    };

    /// <summary>
    /// הסיומת התקנית של שם קובץ: אחרי הסרת נקודות ורווחים בסוף (Windows מתעלם מהם: "a.bat." הוא "a.bat").
    /// ריק אם אין סיומת. מקבל גם נתיב מלא.
    /// </summary>
    public static string StdExt(string path)
    {
        string name = Path.GetFileName((path ?? "").TrimEnd('\\', '/')).TrimEnd(' ', '.');
        int dot = name.LastIndexOf('.');
        return dot < 0 ? "" : name.Substring(dot).ToLowerInvariant();
    }

    public static bool IsDangerousExt(string path) => DangerousExt.Contains(StdExt(path));
    public static bool IsMacroExt(string path) => MacroExt.Contains(StdExt(path));

    /// <summary>האם path (מנורמל וקנוני) הוא אחת השורשים או מתחתיו</summary>
    public static bool IsUnderAny(string path, IEnumerable<string> roots)
    {
        foreach (string r in roots)
            if (!string.IsNullOrEmpty(r) && PathPolicy.IsUnder(path, r)) return true;
        return false;
    }

    private static string F(Environment.SpecialFolder f) => Environment.GetFolderPath(f);

    /// <summary>
    /// מיקומים שכתיבה אליהם (יצירה/שינוי/מחיקה/שחזור) אסורה לגמרי: הרצה אוטומטית בכניסה או התקנת תוכנה
    /// כמשתמש רגיל. Startup (משתמש+מערכת), תפריט התחלה, Tasks, %LOCALAPPDATA%\Programs.
    /// </summary>
    public static List<string> AutoRunRoots()
    {
        var l = new List<string>
        {
            F(Environment.SpecialFolder.Startup),
            F(Environment.SpecialFolder.CommonStartup),
            Path.Combine(F(Environment.SpecialFolder.ApplicationData), "Microsoft", "Windows", "Start Menu"),
            Path.Combine(F(Environment.SpecialFolder.CommonApplicationData), "Microsoft", "Windows", "Start Menu"),
            Path.Combine(F(Environment.SpecialFolder.Windows), "System32", "Tasks"),
            Path.Combine(F(Environment.SpecialFolder.Windows), "Tasks"),
            Path.Combine(F(Environment.SpecialFolder.LocalApplicationData), "Programs"),
        };
        return l.Where(x => !string.IsNullOrEmpty(x)).ToList();
    }

    /// <summary>
    /// מיקומי סודות ונתוני הגשר: חסומים לקריאה/רשימה/חיפוש/העתקה תמיד (config.json: hash/salt של הסיסמה והצימוד).
    /// בנעילה פעילה נוסף ProgramData כולו ו-System32\config (קוצי הרישום).
    /// </summary>
    public static List<string> SecretRoots(bool kioskActive, string configDir, string hardLockDir)
    {
        var l = new List<string> { configDir, hardLockDir };
        if (kioskActive)
        {
            l.Add(F(Environment.SpecialFolder.CommonApplicationData));
            l.Add(Path.Combine(F(Environment.SpecialFolder.Windows), "System32", "config"));
            l.Add(Path.Combine(F(Environment.SpecialFolder.Windows), "Repair"));
        }
        return l.Where(x => !string.IsNullOrEmpty(x)).ToList();
    }

    // ---------- בדיקת מסמכי Office ----------

    private static readonly Regex DdeInstr = new(@"(DDEAUTO|DDE\s)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex DdeFormula = new(@"(cmd|powershell|mshta|msexcel|excel)\s*\|", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ExtRel = new(@"<Relationship\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// בודק מסמך OOXML (docx/xlsx/pptx...), ODF, RTF או OLE ישן. מחזיר סיבת חסימה, או null אם נראה נקי.
    /// חוסם: פרויקט מאקרו (vbaProject.bin, macrosheets, ODF Basic/Scripts), ActiveX/OLE מוטבע, שדות DDE,
    /// נוסחאות DDE באקסל, ויחס חיצוני (TargetMode=External) שאינו קישור רגיל (תבנית מרוחקת attachedTemplate,
    /// תמונה/מסגרת/OLE חיצוניים שמביאים משאב מהרשת ומדליפים NTLM).
    /// </summary>
    public static string InspectDocument(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return InspectDocument(fs, StdExt(path));
        }
        catch { return "לא ניתן לבדוק את המסמך"; }
    }

    public static string InspectDocument(Stream s, string ext)
    {
        try
        {
            if (ext is ".rtf") return InspectRtf(s);
            if (ext is ".doc" or ".xls" or ".ppt" or ".dot" or ".xlt" or ".pot") return InspectOle(s);
            if (ext is ".docx" or ".xlsx" or ".pptx" or ".dotx" or ".xltx" or ".potx" or ".ppsx" or ".odt" or ".ods" or ".odp" or ".xlsb")
                return InspectZip(s);
            return null;   // pdf, תמונות, טקסט: לא מסמך מרוכב
        }
        catch { return "לא ניתן לבדוק את המסמך"; }
    }

    private static string InspectZip(Stream s)
    {
        if (s.Length > 200L * 1024 * 1024) return "המסמך גדול מדי לבדיקה";
        using var z = new ZipArchive(s, ZipArchiveMode.Read, leaveOpen: true);
        if (z.Entries.Count > 20000) return "מסמך חשוד (יותר מדי רכיבים)";
        foreach (var e in z.Entries)
        {
            string n = e.FullName.Replace('\\', '/');
            if (n.IndexOf("vbaProject", StringComparison.OrdinalIgnoreCase) >= 0 ||
                n.IndexOf("vbaData", StringComparison.OrdinalIgnoreCase) >= 0 ||
                n.StartsWith("xl/macrosheets/", StringComparison.OrdinalIgnoreCase) ||
                n.StartsWith("Basic/", StringComparison.OrdinalIgnoreCase) ||
                n.StartsWith("Scripts/", StringComparison.OrdinalIgnoreCase) ||
                n.IndexOf("/activeX/", StringComparison.OrdinalIgnoreCase) >= 0 ||
                n.IndexOf("/embeddings/", StringComparison.OrdinalIgnoreCase) >= 0)
                return "המסמך מכיל מאקרו או רכיב מוטבע (" + n + ")";
        }
        foreach (var e in z.Entries)
        {
            string n = e.FullName.Replace('\\', '/');
            bool isRels = n.EndsWith(".rels", StringComparison.OrdinalIgnoreCase);
            bool isXml = n.EndsWith(".xml", StringComparison.OrdinalIgnoreCase);
            if (!isRels && !isXml) continue;
            if (e.Length > 16L * 1024 * 1024) return "רכיב XML גדול מדי לבדיקה (" + n + ")";
            string text;
            using (var rd = new StreamReader(e.Open(), Encoding.UTF8)) text = rd.ReadToEnd();
            if (isRels)
            {
                foreach (Match m in ExtRel.Matches(text))
                {
                    string r = m.Value;
                    if (r.IndexOf("TargetMode=\"External\"", StringComparison.OrdinalIgnoreCase) < 0 &&
                        r.IndexOf("TargetMode='External'", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (Regex.IsMatch(r, @"Type=[""'][^""']*/hyperlink[""']", RegexOptions.IgnoreCase)) continue;
                    return "המסמך מפנה למשאב חיצוני/תבנית מרוחקת (" + n + ")";
                }
            }
            else if (n.StartsWith("word/", StringComparison.OrdinalIgnoreCase) || n.StartsWith("ppt/", StringComparison.OrdinalIgnoreCase))
            {
                if (Regex.IsMatch(text, @"<w:instrText[^>]*>[^<]*(DDEAUTO|DDE\s)", RegexOptions.IgnoreCase) ||
                    Regex.IsMatch(text, @"w:instr=""\s*(DDEAUTO|DDE\s)", RegexOptions.IgnoreCase) ||
                    Regex.IsMatch(text, @"<w:(attachedTemplate|externalData)\b", RegexOptions.IgnoreCase))
                    return "המסמך מכיל שדה DDE או תבנית מצורפת (" + n + ")";
            }
            else if (n.StartsWith("xl/", StringComparison.OrdinalIgnoreCase))
            {
                if (n.StartsWith("xl/worksheets/", StringComparison.OrdinalIgnoreCase) || n.Equals("xl/sharedStrings.xml", StringComparison.OrdinalIgnoreCase))
                    foreach (Match m in Regex.Matches(text, @"<(f|v|t)[^>]*>([^<]*)</\1>", RegexOptions.IgnoreCase))
                        if (DdeFormula.IsMatch(m.Groups[2].Value))
                            return "הגיליון מכיל נוסחת DDE (" + n + ")";
                if (n.StartsWith("xl/externalLinks/", StringComparison.OrdinalIgnoreCase)) return "הגיליון מכיל קישור חיצוני (" + n + ")";
            }
        }
        return null;
    }

    private static byte[] Head(Stream s, int max)
    {
        s.Seek(0, SeekOrigin.Begin);
        var buf = new byte[(int)Math.Min(s.Length, max)];
        int got = 0, n;
        while (got < buf.Length && (n = s.Read(buf, got, buf.Length - got)) > 0) got += n;
        return buf;
    }

    private static string InspectRtf(Stream s)
    {
        string t = Encoding.Latin1.GetString(Head(s, 16 * 1024 * 1024));
        if (Regex.IsMatch(t, @"\\(objdata|objautlink|objupdate|objemb|objlink)\b|\\\*\\template|\\\*\\(objclass|datafield)", RegexOptions.IgnoreCase))
            return "קובץ RTF עם אובייקט מוטבע או תבנית";
        return null;
    }

    private static string InspectOle(Stream s)
    {
        byte[] b = Head(s, 32 * 1024 * 1024);
        if (b.Length >= 4 && b[0] == 'P' && b[1] == 'K') return InspectZip(s);   // סיומת ישנה על תוכן OOXML
        // שמות הזרמים ב-OLE שמורים כ-UTF-16: "_VBA_PROJECT", "Macros", "VBA", "_xlnm", ObjectPool
        foreach (string needle in new[] { "_VBA_PROJECT", "Macros", "ObjectPool", "\u0001Ole10Native" })
            if (IndexOf(b, Encoding.Unicode.GetBytes(needle)) >= 0) return "מסמך ישן עם מאקרו או אובייקט מוטבע";
        return null;
    }

    private static int IndexOf(byte[] hay, byte[] needle) => hay.AsSpan().IndexOf(needle);

    // ---------- הפעלה (/sys/power) ----------

    /// <summary>
    /// האם פעולת הפעלה בנעילה פעילה דורשת סיסמה: הפעלה מחדש וכיבוי תמיד (אלא אם המנהל פתח במפורש "power"
    /// ברשימת ההגדרות הפתוחות); התנתקות אסורה לגמרי בנעילה (נבדק בנפרד). שינה/נעילה אינן פוגעות.
    /// </summary>
    public static bool PowerNeedsAuth(string action, bool kioskActive, IEnumerable<string> openSections)
    {
        if (!kioskActive) return false;
        if (action is not ("restart" or "shutdown")) return false;
        return !openSections.Contains("power", StringComparer.OrdinalIgnoreCase);
    }
}
