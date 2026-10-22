using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

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
        // סבב 4: סיומות שמריצות/טוענות קוד ושהרשימה הראשונה פספסה
        ".com", ".pif", ".vbe", ".wsh", ".ws", ".msc", ".jar", ".msp", ".application", ".chm", ".inf", ".sct", ".scf", ".xll", ".vsto",
        ".appref-ms", ".diagcab", ".py", ".ps1xml", ".psm1", ".wsc", ".gadget", ".psd1", ".pyw", ".mht", ".mhtml", ".settingcontent-ms", ".library-ms",
        // S3: חבילות התקנה/ערכות נושא/חיפוש/משימות/חיבורי נתונים/דיסקים שהרשימה הקודמת פספסה (פתיחתם מתקינה, מריצה או מושכת משאב מרוחק)
        ".appinstaller", ".appx", ".appxbundle", ".msix", ".msixbundle", ".theme", ".themepack", ".deskthemepack", ".search-ms", ".searchconnector-ms",
        ".job", ".iqy", ".rdp", ".iso", ".vhd", ".vhdx", ".img", ".slk", ".odc", ".xbap", ".website", ".jnlp", ".vb", ".mda", ".mde", ".prf", ".ps2", ".psc1", ".psc2",
        ".cer", ".crt", ".sys", ".ocx", ".drv", ".gpd", ".mof", ".rgs", ".xsl", ".xslt", ".hlp", ".msh", ".msh1", ".msh2", ".mshxml",
    };

    /// <summary>
    /// רשימה לבנה של סיומות שמותר ליצור/לכתוב בנעילה מחוץ לתיקיות מאושרות: מסמכים, תמונות, טקסט, שמע/וידאו, ארכיבים.
    /// WHY: רשימה שחורה תמיד מפספסת סיומת חדשה (.appinstaller וכו'); כאן כל מה שלא מוכר נחסם. בלי סיומת: מותר (אין הרצה לפי שם).
    /// </summary>
    public static readonly HashSet<string> WritableExt = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".csv", ".tsv", ".json", ".xml", ".rtf", ".log", ".srt", ".vtt", ".lrc", ".tex", ".bib", ".rst",
        // WHY (U3): .dot/.xlt/.xls (בינאריים ישנים) יכולים לשאת מאקרו VBA/XLM ובתיקיית Startup/XLSTART/Templates של Office מורצים אוטומטית; לא נכתבים בנעילה מחוץ לתיקיות מאושרות
        ".doc", ".docx", ".dotx", ".xlsx", ".xltx", ".ppt", ".pptx", ".pps", ".ppsx", ".odt", ".ods", ".odp",
        ".pdf", ".epub", ".djvu", ".mobi", ".html", ".htm",
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".svg", ".tif", ".tiff", ".ico", ".heic",
        ".mp3", ".wav", ".ogg", ".flac", ".m4a", ".aac", ".wma", ".mp4", ".mkv", ".avi", ".mov", ".webm", ".wmv",
        ".zip", ".7z", ".rar", ".tmp", ".bak",
        ".db", ".sqlite", ".sqlite3", ".hive", // נתוני אוצריא (seforim.db, cache.db, personal_notes.db ועוד): לא ניתנים להרצה
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
        string name = Path.GetFileName((path ?? "").TrimEnd('\\', '/'));
        // WHY (S3): "a.exe:payload" ו-"a.exe::$DATA" הם זרם/סוג של a.exe; בלי הסרת החלק אחרי ':' הסיומת נראית ".exe:payload" ועוקפת את הרשימות
        int colon = name.IndexOf(':');
        if (colon >= 0) name = name.Substring(0, colon);
        name = name.TrimEnd(' ', '.');
        int dot = name.LastIndexOf('.');
        return dot < 0 ? "" : name.Substring(dot).ToLowerInvariant();
    }

    /// <summary>האם שם הקובץ האחרון בנתיב כולל זרם נתונים חלופי (ADS): "a:payload", "a.txt:evil", "dir:stream"</summary>
    public static bool HasAds(string path)
    {
        string name = Path.GetFileName((path ?? "").TrimEnd('\\', '/'));
        return name.IndexOf(':') >= 0;
    }

    /// <summary>
    /// הנתיב בלי חלק ה-ADS: "C:\Windows:x" -> "C:\Windows". WHY (S3): בדיקות "תחת אזור מוגן" משוות מחרוזות, ו-"<מוגן>:stream"
    /// (ADS על התיקייה עצמה) לא שווה ולא מתחיל ב-"<מוגן>\", ולכן עקף את ההגנה.
    /// </summary>
    public static string StripAds(string fullPath)
    {
        if (string.IsNullOrEmpty(fullPath)) return fullPath;
        int i = fullPath.IndexOf(':', Math.Min(2, fullPath.Length));
        return i < 0 ? fullPath : fullPath.Substring(0, i);
    }

    public static bool IsDangerousExt(string path) => DangerousExt.Contains(StdExt(path));

    /// <summary>
    /// רשימה לבנה לפתיחה (/fs/open) בנעילה/הגבלה: מסמכים, תמונות, מדיה וטקסט. בלי html/svg/xml (דפדפן = משאבים מרוחקים וסקריפט),
    /// ארכיבים ונתונים. WHY: רשימה שחורה פספסה .appinstaller/.msix/.theme/.search-ms/.job/.iqy/.rdp/.iso/.vhd.
    /// </summary>
    public static readonly HashSet<string> OpenableExt = new(StringComparer.OrdinalIgnoreCase)
    {
        // WHY (U3): .csv/.tsv הוצאו: handler ברירת המחדל הוא Excel, שמריץ =cmd|... ופותח לפי תוכן (XLSM/ZIP בשם csv) בלי בדיקה
        ".txt", ".md", ".json", ".rtf", ".log", ".srt", ".vtt", ".lrc", ".tex", ".bib", ".rst",
        ".doc", ".docx", ".dot", ".dotx", ".xls", ".xlsx", ".xlt", ".xltx", ".ppt", ".pptx", ".pps", ".ppsx", ".odt", ".ods", ".odp",
        ".pdf", ".epub", ".djvu", ".mobi",
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".tif", ".tiff", ".ico", ".heic",
        ".mp3", ".wav", ".ogg", ".flac", ".m4a", ".aac", ".wma", ".mp4", ".mkv", ".avi", ".mov", ".webm", ".wmv",
    };

    /// <summary>האם מותר לפתוח את הקובץ לפי הסיומת (רשימה לבנה; אחרי הרשימות החסומות). ADS וסיומת ריקה = לא</summary>
    public static bool IsOpenAllowedExt(string path)
    {
        if (HasAds(path)) return false;
        string e = StdExt(path);
        return e.Length > 0 && !DangerousExt.Contains(e) && !MacroExt.Contains(e) && OpenableExt.Contains(e);
    }

    /// <summary>
    /// האם כתיבה/יצירה בנעילה חסומה לפי הסיומת: מסוכנת, או כל סיומת שאינה ברשימה הלבנה (סיומת ריקה מותרת).
    /// סיכון שבירה: קובץ עבודה בסיומת יוצאת דופן (.db, .dat, .otzar) לא יישמר בנעילה מחוץ לתיקיות מאושרות.
    /// </summary>
    public static bool IsWriteBlockedExt(string path)
    {
        if (HasAds(path)) return true;   // S3: "a:payload" (בלי נקודה) ו-"a.txt:evil" הם זרם נסתר: אף פעם לא ליצור
        string e = StdExt(path);
        if (DangerousExt.Contains(e) || MacroExt.Contains(e)) return true;
        return e.Length > 0 && !WritableExt.Contains(e);
    }

    /// <summary>
    /// נתיב UNC שנחסם: כל כתובת עצמית (localhost, 127.x, ::1, שם המחשב, ipv6-literal) תמיד, כי localhost\c$ עוקף בדיקות נתיב;
    /// וכל UNC אחר כשמצב קשיח (נעילה/הגבלה).
    /// </summary>
    public static bool IsBlockedUnc(string path, bool strict, string machineName)
    {
        if (path == null || !path.StartsWith("\\\\") || path.StartsWith("\\\\?\\") || path.StartsWith("\\\\.\\")) return false;
        string rest = path.Substring(2);
        int i = rest.IndexOf('\\');
        string host = (i < 0 ? rest : rest.Substring(0, i)).Trim('[', ']').ToLowerInvariant();
        string m = (machineName ?? "").ToLowerInvariant();
        bool local = host is "localhost" or "::1" or "." or "" || host.StartsWith("127.") || host.EndsWith(".ipv6-literal.net") ||
                     (m.Length > 0 && (host == m || host.StartsWith(m + ".")));
        return local || strict;
    }

    /// <summary>האם path הוא אב ממש של אחד השורשים (שינוי/מחיקה/העברה שלו מזיז את המוגן)</summary>
    public static bool IsAncestorOfAny(string path, IEnumerable<string> roots)
    {
        string a = path.TrimEnd('\\') + "\\";
        foreach (string r in roots)
            if (!string.IsNullOrEmpty(r) && r.TrimEnd('\\').StartsWith(a, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

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
        l.AddRange(OfficeAutoLoadRoots());
        return l.Where(x => !string.IsNullOrEmpty(x)).ToList();
    }

    /// <summary>
    /// תיקיות ש-Office טוען מהן אוטומטית בהפעלה (תבניות/תוספים/מאקרו): Word\STARTUP, Excel\XLSTART, Templates (Normal.dotm), AddIns,
    /// ו-%LOCALAPPDATA%\Microsoft\Office. WHY (U3): כתיבה אליהן = הרצת קוד בפתיחת Word/Excel הבאה, מחוץ לכל מדיניות הקבצים.
    /// סיכון שבירה: תבניות אישיות (.dotx) ותוספים שהמשתמש שומר בעצמו לשם נחסמים בכתיבה דרך הגשר.
    /// </summary>
    public static List<string> OfficeAutoLoadRoots()
    {
        string ad = F(Environment.SpecialFolder.ApplicationData), la = F(Environment.SpecialFolder.LocalApplicationData);
        var l = new List<string>
        {
            Path.Combine(ad, "Microsoft", "Word", "STARTUP"),
            Path.Combine(ad, "Microsoft", "Excel", "XLSTART"),
            Path.Combine(ad, "Microsoft", "Templates"),
            Path.Combine(ad, "Microsoft", "AddIns"),
            Path.Combine(la, "Microsoft", "Office"),
        };
        return l.Where(x => !string.IsNullOrEmpty(x) && !x.StartsWith("Microsoft")).ToList();
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

    /// <summary>
    /// סיומות שאפליקציית Office/ODF פותחת לפיהן. מסמך בסיומת כזו חייב להתאים לאחד הפורמטים המוכרים (לפי תוכן, לא לפי סיומת)
    /// אחרת הוא נחסם. WHY: Word/Excel/PowerPoint מזהים פורמט לפי תוכן ולא לפי סיומת (RTF בשם .doc, OLE בשם .rtf, HTML בשם .doc
    /// שנפתח ב-Word), ולכן זיהוי לפי סיומת עקיף בקלות.
    /// </summary>
    public static readonly HashSet<string> OfficeExt = new(StringComparer.OrdinalIgnoreCase)
    {
        ".doc", ".docx", ".dot", ".dotx", ".rtf", ".wri", ".xls", ".xlsx", ".xlt", ".xltx", ".xlsb", ".xlw",
        ".ppt", ".pptx", ".pps", ".ppsx", ".pot", ".potx", ".odt", ".ods", ".odp", ".odg", ".odf", ".ott", ".ots", ".otp", ".xps", ".oxps",
    };

    private enum DocKind { Unknown, Zip, Ole, Rtf }

    private static readonly byte[] OleMagic = { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 };

    private static DocKind Sniff(byte[] h)
    {
        if (h.Length >= 4 && h[0] == 'P' && h[1] == 'K' && ((h[2] == 3 && h[3] == 4) || (h[2] == 5 && h[3] == 6))) return DocKind.Zip;
        if (h.Length >= 8 && h.AsSpan(0, 8).SequenceEqual(OleMagic)) return DocKind.Ole;
        // Word מזהה RTF לפי "{\rtf" בתחילת הקובץ (סובל רווחים/זבל קצר לפניו): סורקים את ה-KB הראשון
        string t = Encoding.Latin1.GetString(h, 0, Math.Min(h.Length, 1024));
        if (t.IndexOf("{\\rt", StringComparison.OrdinalIgnoreCase) >= 0) return DocKind.Rtf;
        return DocKind.Unknown;
    }

    /// <summary>
    /// בודק מסמך OOXML (docx/xlsx/pptx...), ODF, RTF או OLE ישן. מחזיר סיבת חסימה, או null אם נראה נקי.
    /// הפורמט נקבע לפי תוכן (magic bytes), לא לפי סיומת; סיומת Office עם תוכן לא מוכר = חסום.
    /// חוסם: פרויקט מאקרו (vbaProject.bin, macrosheets, ODF Basic/Scripts), ActiveX/OLE מוטבע, שדות DDE,
    /// נוסחאות DDE באקסל, חיבורי נתונים, ויחס חיצוני (TargetMode=External) שאינו קישור רגיל (תבנית מרוחקת attachedTemplate,
    /// תמונה/מסגרת/OLE חיצוניים שמביאים משאב מהרשת ומדליפים NTLM). ה-XML מנותח ב-XmlReader מוגבל (בלי DTD/resolver), לא ב-regex.
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
            ext = (ext ?? "").ToLowerInvariant();
            bool office = OfficeExt.Contains(ext) || MacroExt.Contains(ext);
            switch (Sniff(Head(s, 1024)))
            {
                case DocKind.Rtf: return InspectRtf(s);
                case DocKind.Ole: return InspectOle(s);
                // WHY (U3): לא נשענים על הסיומת. zip בשם .txt/.csv/.png עלול להיפתח כמסמך Office בתוכנה אחרת, ולכן נסרק; רק epub ו-zip (לא נפתחים ב-Office) פטורים
                case DocKind.Zip: return office || (ext != ".epub" && ext != ".zip") ? InspectZip(s) : null;
            }
            // סיומת Office בלי תוכן מוכר (HTML/XML/טקסט בשם .doc ועוד) נחסמת
            if (office) return "סוג מסמך Office לא מזוהה לפי תוכנו";
            return null;   // pdf, תמונות, טקסט: לא מסמך מרוכב
        }
        catch { return "לא ניתן לבדוק את המסמך"; }
    }

    private static XmlReader SafeXml(Stream st) => XmlReader.Create(st, new XmlReaderSettings
    {
        DtdProcessing = DtdProcessing.Prohibit,   // XXE/billion-laughs: זורק -> חסום (fail-closed)
        XmlResolver = null,
        MaxCharactersInDocument = 64L * 1024 * 1024,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
    });

    /// <summary>ערך תכונה לפי שם מקומי, בלי רגישות לרישיות ובלי תלות ב-prefix</summary>
    private static string Attr(XmlReader r, string local)
    {
        if (!r.HasAttributes) return null;
        string found = null;
        for (int i = 0; i < r.AttributeCount; i++)
        {
            r.MoveToAttribute(i);
            if (string.Equals(r.LocalName, local, StringComparison.OrdinalIgnoreCase)) found = r.Value;
        }
        r.MoveToElement();
        return found;
    }

    /// <summary>הוראת שדה שמתחילה ב-DDE/DDEAUTO (אחרי רווחים/מרכאות/תווי בקרה)</summary>
    private static bool IsDdeInstr(string instr)
    {
        string t = new string((instr ?? "").Where(c => !char.IsWhiteSpace(c) && !char.IsControl(c) && c != '"' && c != '\'' && c != '\u00A0').ToArray());
        return t.StartsWith("DDE", StringComparison.OrdinalIgnoreCase);   // DDE, DDEAUTO
    }

    private static readonly Regex DdeFormula = new(@"^\s*[=+\-@]?\s*[\w.\-]+\s*\|", RegexOptions.Compiled);

    /// <summary>סיומות מדיה/גופן שמותרות כחלק בינארי בתוך מסמך (לא נטענות כקוד). כל חלק אחר שאינו XML נחסם</summary>
    private static readonly HashSet<string> BinaryPartExt = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".jpe", ".gif", ".bmp", ".tif", ".tiff", ".emf", ".wmf", ".wdp", ".jxr", ".ico", ".webp", ".odttf", ".ttf", ".otf", ".fntdata",
        ".mp3", ".wav", ".mp4", ".m4a", ".wma", ".avi", ".mov", ".wmv", ".mpg", ".mpeg", ".ogg", ".webm",
    };

    /// <summary>ערך ContentType/Type שמעיד על מאקרו, ActiveX, OLE או גיליון מאקרו (Office פותח את החלק לפיהם, לא לפי שם)</summary>
    private static bool IsRiskyContentType(string v)
    {
        if (string.IsNullOrEmpty(v)) return false;
        foreach (string k in new[] { "macroEnabled", "vbaProject", "vbaData", "activeX", "macrosheet", "oleObject", "ms-office.vba", "ms-excel.sheet.binary.macro" })
            if (v.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        return false;
    }

    private static string InspectZip(Stream s)
    {
        if (s.Length > 200L * 1024 * 1024) return "המסמך גדול מדי לבדיקה";
        s.Seek(0, SeekOrigin.Begin);
        using var z = new ZipArchive(s, ZipArchiveMode.Read, leaveOpen: true);
        if (z.Entries.Count > 20000) return "מסמך חשוד (יותר מדי רכיבים)";
        long total = 0;
        foreach (var e in z.Entries)
        {
            total += e.Length;
            string n = e.FullName.Replace('\\', '/');
            string sn = "/" + n;   // כך "/xl/macrosheets/" תופס גם חלק בתיקייה מקוננת
            if (n.IndexOf("vbaProject", StringComparison.OrdinalIgnoreCase) >= 0 ||
                n.IndexOf("vbaData", StringComparison.OrdinalIgnoreCase) >= 0 ||
                sn.IndexOf("/macrosheets/", StringComparison.OrdinalIgnoreCase) >= 0 ||
                sn.IndexOf("/connections", StringComparison.OrdinalIgnoreCase) >= 0 ||     // חיבורי נתונים/שאילתות: הרצת פקודה/משאב חיצוני
                sn.IndexOf("/queryTables/", StringComparison.OrdinalIgnoreCase) >= 0 ||
                n.StartsWith("Basic/", StringComparison.OrdinalIgnoreCase) ||
                n.StartsWith("Scripts/", StringComparison.OrdinalIgnoreCase) ||
                sn.IndexOf("/activeX/", StringComparison.OrdinalIgnoreCase) >= 0 ||
                sn.IndexOf("/embeddings/", StringComparison.OrdinalIgnoreCase) >= 0)
                return "המסמך מכיל מאקרו או רכיב מוטבע (" + n + ")";
        }
        if (total > 2L * 1024 * 1024 * 1024) return "מסמך חשוד (גודל לא דחוס חריג)";
        // WHY (U3): Office קובע את החלק הראשי לפי _rels/.rels ואת סוג החלק לפי [Content_Types].xml, לא לפי שם/תחילית. לכן כל חלק נבדק לפי תוכנו
        // (מתחיל ב-'<' = XML) עם כל הבדיקות בלי תנאי תחילית; חלק שאינו XML ואינו מדיה/גופן מוכרים נחסם (fail-closed).
        // סיכון false-positive: הרחבות יצרן נדירות (חלק .bin/.dat בתוך docx) נחסמות. printerSettings*.bin מותר במפורש.
        foreach (var e in z.Entries)
        {
            string n = e.FullName.Replace('\\', '/');
            if (n.EndsWith("/")) continue;   // רשומת תיקייה
            var head = new byte[16];
            int got = 0;
            using (var hs = e.Open())
            {
                int r0;
                while (got < head.Length && (r0 = hs.Read(head, got, head.Length - got)) > 0) got += r0;
            }
            if (!LooksLikeXml(head, got))
            {
                string ext = StdExt(n);
                bool printer = n.IndexOf("/printerSettings/", StringComparison.OrdinalIgnoreCase) >= 0 || n.StartsWith("printerSettings/", StringComparison.OrdinalIgnoreCase);
                bool okBin = BinaryPartExt.Contains(ext) || (printer && ext == ".bin") ||
                             string.Equals(n, "mimetype", StringComparison.OrdinalIgnoreCase) || got == 0;
                if (!okBin) return "המסמך מכיל רכיב לא מוכר (" + n + ")";
                continue;
            }
            if (e.Length > 64L * 1024 * 1024) return "רכיב XML גדול מדי לבדיקה (" + n + ")";
            using var st = e.Open();
            using var r = SafeXml(st);
            string why = n.EndsWith(".rels", StringComparison.OrdinalIgnoreCase) ? InspectRels(r, n) : InspectPart(r, n);
            if (why != null) return why;
        }
        return null;
    }

    /// <summary>XML לפי תוכן: '&lt;' אחרי BOM/רווחים, או BOM של UTF-16/UTF-8 (XmlReader מזהה קידוד לפי BOM)</summary>
    private static bool LooksLikeXml(byte[] h, int len)
    {
        int i = 0;
        if (len >= 2 && ((h[0] == 0xFF && h[1] == 0xFE) || (h[0] == 0xFE && h[1] == 0xFF))) return true;
        if (len >= 3 && h[0] == 0xEF && h[1] == 0xBB && h[2] == 0xBF) i = 3;
        while (i < len && (h[i] == ' ' || h[i] == '\t' || h[i] == '\r' || h[i] == '\n')) i++;
        return i < len && h[i] == '<';
    }

    private static string InspectRels(XmlReader r, string n)
    {
        while (r.Read())
        {
            if (r.NodeType != XmlNodeType.Element || !r.LocalName.Equals("Relationship", StringComparison.OrdinalIgnoreCase)) continue;
            if (IsRiskyContentType(Attr(r, "Type"))) return "המסמך מפנה לרכיב מאקרו/מוטבע (" + n + ")";
            if (!(Attr(r, "TargetMode") ?? "").Trim().Equals("External", StringComparison.OrdinalIgnoreCase)) continue;
            // קישור רגיל (hyperlink) מותר: נפתח רק בלחיצה. כל משאב חיצוני אחר נטען אוטומטית (תבנית, תמונה, OLE)
            if ((Attr(r, "Type") ?? "").EndsWith("/hyperlink", StringComparison.OrdinalIgnoreCase)) continue;
            return "המסמך מפנה למשאב חיצוני/תבנית מרוחקת (" + n + ")";
        }
        return null;
    }

    private static string InspectPart(XmlReader r, string n)
    {
        // WHY (U3): בלי תנאי תחילית בלבד. החלק הראשי יכול להיות בכל נתיב (לפי _rels/.rels); ה-namespace של האלמנט קובע את הסוג,
        // והתחילית (word/ xl/) נשארת כרמז נוסף. בדיקת ppaction חלה על כל חלק (התכונה לא לגיטימית בחלק אחר)
        bool word = n.StartsWith("word/", StringComparison.OrdinalIgnoreCase);
        bool xl = n.StartsWith("xl/", StringComparison.OrdinalIgnoreCase);
        // שדות Word: ההוראה יכולה להתפצל בין כמה w:instrText (DD + E), לכן מצברים לפי fldChar begin..separate
        var fields = new Stack<StringBuilder>();
        bool again = false;   // ReadElementContentAsString כבר התקדם לצומת הבא
        bool sawXl = false;
        while (again || r.Read())
        {
            again = false;
            if (r.NodeType != XmlNodeType.Element) continue;
            string ln = r.LocalName;
            string ns = r.NamespaceURI ?? "";
            bool nsWord = ns.IndexOf("wordprocessingml", StringComparison.OrdinalIgnoreCase) >= 0;
            bool nsXl = ns.IndexOf("spreadsheetml", StringComparison.OrdinalIgnoreCase) >= 0;
            if (nsXl) sawXl = true;
            if (IsRiskyContentType(Attr(r, "ContentType"))) return "המסמך מכיל סוג רכיב מאקרו/מוטבע (" + n + ")";
            if (ln.Equals("attachedTemplate", StringComparison.OrdinalIgnoreCase) || ln.Equals("externalData", StringComparison.OrdinalIgnoreCase) ||
                ln.Equals("event-listener", StringComparison.OrdinalIgnoreCase) || ln.Equals("section-source", StringComparison.OrdinalIgnoreCase) ||
                ln.Equals("table-source", StringComparison.OrdinalIgnoreCase) || ln.Equals("cell-range-source", StringComparison.OrdinalIgnoreCase))
                return "המסמך מכיל תבנית מצורפת, מקור חיצוני או אירוע-סקריפט (" + n + ")";
            string instrAttr = Attr(r, "instr");
            if (instrAttr != null && IsDdeInstr(instrAttr)) return "המסמך מכיל שדה DDE (" + n + ")";
            string act = Attr(r, "action") ?? "";
            if (act.StartsWith("ppaction://program", StringComparison.OrdinalIgnoreCase) || act.StartsWith("ppaction://macro", StringComparison.OrdinalIgnoreCase) ||
                act.StartsWith("ppaction://ole", StringComparison.OrdinalIgnoreCase))
                return "המצגת מכילה פעולה שמריצה תוכנה/מאקרו (" + n + ")";
            if (word || nsWord)
            {
                if (ln.Equals("fldChar", StringComparison.OrdinalIgnoreCase))
                {
                    string t = (Attr(r, "fldCharType") ?? "").ToLowerInvariant();
                    if (t == "begin") fields.Push(new StringBuilder());
                    else if ((t == "separate" || t == "end") && fields.Count > 0)
                    {
                        var sb = fields.Peek();
                        if (IsDdeInstr(sb.ToString())) return "המסמך מכיל שדה DDE (" + n + ")";
                        sb.Clear();   // אחרי separate מגיע טקסט התוצאה, לא הוראה
                        if (t == "end") fields.Pop();
                    }
                }
                else if (ln.Equals("instrText", StringComparison.OrdinalIgnoreCase) || ln.Equals("delInstrText", StringComparison.OrdinalIgnoreCase))
                {
                    bool empty = r.IsEmptyElement;
                    string txt = empty ? "" : r.ReadElementContentAsString();
                    again = !empty;
                    if (fields.Count > 0) fields.Peek().Append(txt);
                    else if (IsDdeInstr(txt)) return "המסמך מכיל שדה DDE (" + n + ")";
                }
            }
            else if ((xl || nsXl) && (ln == "f" || ln == "v" || ln == "t") && !r.IsEmptyElement)
            {
                string txt = r.ReadElementContentAsString();
                again = true;
                string tt = txt.TrimStart();
                bool formulaLike = ln == "f" || (tt.Length > 0 && "=+-@".IndexOf(tt[0]) >= 0);
                if (formulaLike && DdeFormula.IsMatch(txt)) return "הגיליון מכיל נוסחת DDE (" + n + ")";
            }
        }
        foreach (var sb in fields) if (IsDdeInstr(sb.ToString())) return "המסמך מכיל שדה DDE (" + n + ")";
        if ((xl || sawXl) && n.IndexOf("/externalLinks/", StringComparison.OrdinalIgnoreCase) >= 0) return "הגיליון מכיל קישור חיצוני (" + n + ")";
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

    /// <summary>תקרת גודל לסריקת RTF/OLE (כמו Zip 200MB): מעליה חסום. WHY (U3): קריאה חלקית (16/32MB) אפשרה להסתיר אובייקט/DDE אחרי הרף</summary>
    private const long MaxLegacyScan = 256L * 1024 * 1024;
    private const int ScanChunk = 8 * 1024 * 1024;
    private const int ScanOverlap = 8 * 1024;

    /// <summary>קורא את כל הזרם בנתחים חופפים (חפיפה מכסה תבנית שנחתכה בגבול), ומריץ scan על כל נתח; scan מחזיר סיבה או null</summary>
    private static string ScanChunks(Stream s, Func<byte[], int, string> scan)
    {
        if (s.Length > MaxLegacyScan) return "המסמך גדול מדי לבדיקה";
        s.Seek(0, SeekOrigin.Begin);
        var buf = new byte[ScanChunk + ScanOverlap];
        int carry = 0;
        while (true)
        {
            int got = carry, n;
            while (got < buf.Length && (n = s.Read(buf, got, buf.Length - got)) > 0) got += n;
            if (got == carry) return null;   // לא נקרא דבר חדש
            string why = scan(buf, got);
            if (why != null) return why;
            if (got < buf.Length) return null;
            Buffer.BlockCopy(buf, got - ScanOverlap, buf, 0, ScanOverlap);
            carry = ScanOverlap;
        }
    }

    private static readonly Regex RtfObj = new(@"\\(objdata|objautlink|objupdate|objemb|objlink|objclass|objsetsize|template|attachedtemplate)\b|\\\*\\(template|objclass|datafield|mailmerge)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex RtfCtl = new(@"\\\*|\\[a-zA-Z]+-?\d* ?|[{}\s""]", RegexOptions.Compiled);

    private static string InspectRtf(Stream s) => ScanChunks(s, (b, len) =>
    {
        string t = Encoding.Latin1.GetString(b, 0, len);
        if (RtfObj.IsMatch(t)) return "קובץ RTF עם אובייקט מוטבע או תבנית";
        // שדות: DDE/INCLUDEPICTURE/INCLUDETEXT/LINK אחרי הסרת סוגריים ומילות בקרה (הפיצול {DD}{E} לא עוקף)
        int i = 0;
        while ((i = t.IndexOf("\\fldinst", i, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            i += 8;
            string seg = RtfCtl.Replace(t.Substring(i, Math.Min(400, t.Length - i)), "");
            if (seg.StartsWith("DDE", StringComparison.OrdinalIgnoreCase) || seg.StartsWith("INCLUDEPICTURE", StringComparison.OrdinalIgnoreCase) ||
                seg.StartsWith("INCLUDETEXT", StringComparison.OrdinalIgnoreCase) || seg.StartsWith("LINK", StringComparison.OrdinalIgnoreCase) ||
                seg.StartsWith("IMPORT", StringComparison.OrdinalIgnoreCase))
                return "קובץ RTF עם שדה DDE/קישור חיצוני";
        }
        return null;
    });

    private static string InspectOle(Stream s) => ScanChunks(s, (b, len) =>
    {
        // שמות הזרמים ב-OLE שמורים כ-UTF-16, וטקסט שדות Word כ-ASCII או UTF-16: מסירים אפסים ומחפשים על הטקסט הדחוס
        var packed = new byte[len];
        int k = 0;
        for (int q = 0; q < len; q++) if (b[q] != 0) packed[k++] = b[q];
        string t = Encoding.Latin1.GetString(packed, 0, k);
        foreach (string needle in new[] { "_VBA_PROJECT", "Macros", "ObjectPool", "Ole10Native", "DDEAUTO" })
            if (t.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0) return "מסמך ישן עם מאקרו, DDE או אובייקט מוטבע";
        if (Regex.IsMatch(t, @"\bDDE\s")) return "מסמך ישן עם שדה DDE";
        // גיליון מאקרו Excel 4.0 (XLM): רשומת BOUNDSHEET (0x0085) עם סוג גיליון 1 (macro) או 6 (VBA module)
        for (int i = 0; i + 10 < len; i++)
            if (b[i] == 0x85 && b[i + 1] == 0x00 && b[i + 3] == 0x00 && b[i + 2] >= 8 && b[i + 2] <= 80 && b[i + 8] <= 2 && (b[i + 9] == 1 || b[i + 9] == 6))
                return "חוברת עבודה ישנה עם גיליון מאקרו";
        return null;
    });

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
