using System.Diagnostics;
using System.Text.Json;
using Microsoft.Win32;

namespace OtzariaBridge;

/// <summary>שילוב תוכנות PDF — שלבים א ו-ב של docs/PDF-INTEGRATION-SPEC.md.
/// POST /pdf/handlers { force? } → { defaultId, handlers: [{ id, name, kind, exists, isDefault, capabilities }] }
/// POST /pdf/open { path, handlerId?, page? } → { opened, handlerId, pageHonored }
/// אבטחה: פתיחה לפי id מרשימה סגורה בלבד; הנתיב עובר FxPolicy; ארגומנטים כרשימה (ArgumentList);
/// בנעילה /pdf/open חסום אלא אם pdf ב-kiosk.allowOpenExtensions, ואז רק לתוכנת ברירת המחדל.</summary>
internal static class PdfApi
{
    private sealed record Handler(string Id, string Name, string Kind, string Exe, string[] Caps);

    private static (DateTime at, List<Handler> list)? _cache;

    public static void Register()
    {
        Server.Route("/pdf/handlers", b => Handlers(b.Bool("force")));
        Server.Route("/pdf/open", Open);
    }

    private static object Handlers(bool force)
    {
        var list = Detect(force);
        string def = DefaultId(list);
        return new
        {
            defaultId = def,
            handlers = list.Select(h => new { id = h.Id, name = h.Name, kind = h.Kind, exists = true, isDefault = h.Id == def, capabilities = h.Caps }),
        };
    }

    private static List<Handler> Detect(bool force)
    {
        if (!force && _cache is { } c && DateTime.UtcNow - c.at < TimeSpan.FromMinutes(5)) return c.list;
        var list = new List<Handler>
        {
            new("windows-default", "ברירת המחדל של Windows", "viewer", null, new[] { "open" }),
            new("windows-chooser", "בחירת תוכנה (פתח באמצעות)", "viewer", null, new[] { "open" }),
        };
        void Add(string id, string name, string kind, string[] caps, params string[] exes)
        {
            string exe = exes.Select(Find).FirstOrDefault(p => p != null);
            if (exe != null) list.Add(new(id, name, kind, exe, caps));
        }
        string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
               la = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        Add("sumatra", "SumatraPDF", "viewer", new[] { "open", "openAtPage", "print" },
            "SumatraPDF.exe", Path.Combine(la, "SumatraPDF", "SumatraPDF.exe"), Path.Combine(pf, "SumatraPDF", "SumatraPDF.exe"));
        Add("acrobat", "Adobe Acrobat", "editor", new[] { "open", "openAtPage", "print" },
            "Acrobat.exe", Path.Combine(pf, "Adobe", "Acrobat DC", "Acrobat", "Acrobat.exe"));
        if (!list.Any(h => h.Id == "acrobat"))
            Add("acrobat-reader", "Adobe Acrobat Reader", "viewer", new[] { "open", "openAtPage", "print" },
                "AcroRd32.exe", Path.Combine(pf86, "Adobe", "Acrobat Reader DC", "Reader", "AcroRd32.exe"), Path.Combine(pf, "Adobe", "Acrobat Reader DC", "Reader", "AcroRd32.exe"));
        Add("foxit", "Foxit PDF Reader", "viewer", new[] { "open" },
            "FoxitPDFReader.exe", Path.Combine(pf86, "Foxit Software", "Foxit PDF Reader", "FoxitPDFReader.exe"), Path.Combine(pf, "Foxit Software", "Foxit PDF Reader", "FoxitPDFReader.exe"));
        // 1.0.2: עוד תוכנות PDF נפוצות — כל מה שמותקן מופיע ב"פתיחה בתוכנה אחרת"
        Add("pdfxchange", "PDF-XChange Editor", "editor", new[] { "open" },
            "PDFXEdit.exe", Path.Combine(pf, "Tracker Software", "PDF Editor", "PDFXEdit.exe"));
        Add("nitro", "Nitro PDF", "editor", new[] { "open" }, "NitroPDF.exe", Path.Combine(pf, "Nitro", "PDF Pro", "14", "NitroPDF.exe"));
        Add("pdf24", "PDF24", "tool", new[] { "open" }, Path.Combine(pf, "PDF24", "pdf24-Toolbox.exe"), Path.Combine(pf, "PDF24", "pdf24.exe"));
        Add("pdfsam", "PDFsam", "tool", new[] { "open" }, Path.Combine(pf, "PDFsam Basic", "pdfsam.exe"));
        Add("edge", "Microsoft Edge", "viewer", new[] { "open" }, "msedge.exe", Path.Combine(pf86, "Microsoft", "Edge", "Application", "msedge.exe"));
        Add("wps", "WPS PDF", "editor", new[] { "open" }, Path.Combine(la, "Kingsoft", "WPS Office", "ksolaunch.exe"));
        // כל התוכנות ש-Windows מציע ב"פתח באמצעות" (ABBYY, PDF-XChange, LibreOffice, אפליקציות Store ועוד)
        try
        {
            var known = new HashSet<string>(list.Where(h => h.Exe != null).Select(h => Path.GetFileName(h.Exe).ToLowerInvariant()));
            foreach (var (id, name) in PdfOpenWith.Sta(() => PdfOpenWith.Enumerate(".pdf").Select(i => (i.Id, i.Name)).ToList()))
            {
                string file = Path.GetFileName(id.Substring(6));
                if (known.Contains(file) || list.Any(h => h.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) continue;
                list.Add(new(id, name, "viewer", null, new[] { "open" }));
            }
        }
        catch (Exception ex) { Config.Log("pdf assoc: " + ex.Message); }
        _cache = (DateTime.UtcNow, list);
        return list;
    }

    /// <summary>
    /// נתיב מלא קיים, או שם exe שנמצא ב-App Paths של HKLM בלבד, ובתנאי שהקובץ במיקום שרק מנהל כותב אליו.
    /// WHY: HKCU ניתן לכתיבה למשתמש (App Paths\SumatraPDF.exe -> exe שרירותי שהגשר המהימן היה מריץ), וגם נתיב מלא ב-LocalAppData
    /// ניתן להחלפה. סיכון false-positive: SumatraPDF/WPS שהותקנו למשתמש בלבד (LocalAppData) לא יופיעו; נשארת "ברירת המחדל של Windows".
    /// </summary>
    private static string Find(string p)
    {
        try
        {
            string found = null;
            if (Path.IsPathFullyQualified(p)) found = File.Exists(p) ? p : null;
            else
            {
                using var k = Registry.LocalMachine.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths\" + p);
                if (k?.GetValue(null) is string v) { v = Environment.ExpandEnvironmentVariables(v.Trim('"')); if (File.Exists(v)) found = v; }
            }
            return found != null && AppTrust.IsAdminOnlyLocation(found) ? found : null;
        }
        catch { }
        return null;
    }

    /// <summary>התוכנה שמשויכת ל-.pdf אצל המשתמש (UserChoice), אם היא אחת מהרשימה; אחרת windows-default</summary>
    private static string DefaultId(List<Handler> list)
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.pdf\UserChoice");
            string prog = (k?.GetValue("ProgId") as string ?? "").ToLowerInvariant();
            if (prog.Contains("sumatra") && list.Any(h => h.Id == "sumatra")) return "sumatra";
            if (prog.Contains("acro") && list.Any(h => h.Id.StartsWith("acrobat"))) return list.First(h => h.Id.StartsWith("acrobat")).Id;
            if (prog.Contains("foxit") && list.Any(h => h.Id == "foxit")) return "foxit";
        }
        catch { }
        return "windows-default";
    }

    /// <summary>סיומת pdf ברשימת המנהל (עם/בלי נקודה מובילה, כמו FsApi.EnsureOpenExtAllowed)</summary>
    internal static bool PdfAllowedInList(IEnumerable<string> allowed) =>
        allowed.Any(x => x.TrimStart('.').Equals("pdf", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// פתיחה בנעילה/מצב מוגבל: אותו רצף כמו FsApi.Open — handle נעול (FileShare.Read), נתיב סופי, EnsureNotReadable, FxPolicy, סיומת, hardlink,
    /// InspectDocument והעתק פרטי נעול. WHY: בדיקת נתיב טקסטואלי ואז Process.Start על המקור השאירה TOCTOU וחסרו בדיקות תוכן/hardlink.
    /// </summary>
    private static void OpenVerified(string path)
    {
        FileStream hold;
        try { hold = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); }
        catch (IOException) { throw BridgeException.Forbidden("הקובץ פתוח לכתיבה בתוכנה אחרת: סגרו אותו ונסו שוב"); }
        using (hold)
        {
            string fin = PathPolicy.FinalPathOfHandle(hold.SafeFileHandle) ?? throw new IOException("פענוח הנתיב הסופי נכשל");
            FsApi.EnsureNotReadable(fin);
            FxPolicy.EnsurePathAllowed(fin);
            FsApi.EnsureOpenExtAllowed(fin);
            if (!fin.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) throw BridgeException.BadRequest("רק קובצי PDF");
            if (PathPolicy.HardLinkCountOfHandle(hold.SafeFileHandle) > 1)
                throw BridgeException.Forbidden("הקובץ מקושר בקישור קשיח (hardlink) לשם נוסף: הפתיחה חסומה");
            string why = FsGuard.InspectDocument(hold, FsGuard.StdExt(fin));
            if (why != null) throw BridgeException.Forbidden("הקובץ נחסם: " + why);
            if (hold.Length <= 256L * 1024 * 1024)
            {
                string baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OtzariaBridge", "open");
                var keep = FsApi.CopyForOpen(hold, fin, baseDir, out string copy);
                try { Process.Start(new ProcessStartInfo(copy) { UseShellExecute = true }); }
                catch { keep.Dispose(); throw; }
                _ = Task.Run(async () => { try { await Task.Delay(TimeSpan.FromMinutes(2)); } finally { keep.Dispose(); } });
            }
            else Process.Start(new ProcessStartInfo(fin) { UseShellExecute = true });
        }
    }

    private static object Open(JsonElement b)
    {
        string raw = b.Req("path");
        raw = Environment.ExpandEnvironmentVariables(raw.Trim());
        if (!Path.IsPathFullyQualified(raw)) throw BridgeException.BadRequest("נדרש נתיב מלא");
        string path = Path.GetFullPath(raw);
        FxPolicy.EnsurePathAllowed(path);
        if (!File.Exists(path)) throw new BridgeException("not_found", "הקובץ לא נמצא", 404);
        if (!path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) throw BridgeException.BadRequest("רק קובצי PDF");

        string id = b.Str("handlerId", "windows-default");
        // WHY: בנעילה או במצב מוגבל רק ברירת המחדל של Windows ורק דרך הרצף המאומת; תוכנה חיצונית (exe) לא מופעלת בלי AppTrust
        bool locked = Kiosk.IsActive || FxPolicy.IsRestricted;
        if (Kiosk.IsActive && !PdfAllowedInList(Kiosk.AllowedExtensions)) throw BridgeException.Locked();
        if (locked)
        {
            OpenVerified(path);
            return new { opened = true, handlerId = "windows-default", pageHonored = false };
        }
        var h = Detect(false).FirstOrDefault(x => x.Id == id) ?? throw new BridgeException("no_handler", "התוכנה אינה ברשימה: " + id, 400);
        long page = b.Long("page", 0);
        bool atPage = page > 0 && h.Caps.Contains("openAtPage");

        if (h.Id == "windows-default") { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        else if (h.Id == "windows-chooser")
        {
            PdfAssoc.ShowOpenWith(path); // חלון שנשאר בחזית (במקום rundll32 שנעלם)
        }
        else if (h.Id.StartsWith("assoc:"))
        {
            if (!PdfOpenWith.Sta(() => PdfOpenWith.Open(".pdf", h.Id, path))) throw new BridgeException("open_failed", "הפתיחה בתוכנה שנבחרה נכשלה", 500);
        }
        else
        {
            // WHY: הגשר מהימן ל-AppTrust; exe שהוחלף אחרי הזיהוי (מטמון 5 דקות) לא יופעל — בדיקת מיקום מנהל-בלבד שוב לפני ההפעלה
            if (h.Exe == null || !AppTrust.IsAdminOnlyLocation(h.Exe)) throw BridgeException.Forbidden("התוכנה אינה במיקום מהימן");
            var psi = new ProcessStartInfo(h.Exe) { UseShellExecute = false };
            if (atPage && h.Id == "sumatra") { psi.ArgumentList.Add("-page"); psi.ArgumentList.Add(page.ToString()); }
            if (atPage && h.Id.StartsWith("acrobat")) { psi.ArgumentList.Add("/A"); psi.ArgumentList.Add("page=" + page); }
            psi.ArgumentList.Add(path);
            Process.Start(psi);
        }
        return new { opened = true, handlerId = h.Id, pageHonored = atPage };
    }
}
