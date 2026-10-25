using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace OtzariaBridge;

/// <summary>שלב ג של docs/PDF-INTEGRATION-SPEC.md — פעולות PDF כבדות במנוע מותקן (כמו ב-PDF24), עבור "צופה PDF+OCR".
/// מנוע: Ghostscript — זה שמגיע עם PDF24 (Program Files\PDF24\gs\bin) או התקנה רגילה של Ghostscript; qpdf אם מותקן.
/// הקובץ עובר בחתיכות (אין צורך בנתיב בדיסק ובמדיניות תיקיות): begin → chunk… → run → read… → end.
///   POST /pdf/engines → { engines: [{ id, name, path }], ops: [...] }
///   POST /pdf/op/begin {} → { id }
///   POST /pdf/op/chunk { id, data (base64) } → { size }
///   POST /pdf/op/run { id, op, params } → { size, engine }
///   POST /pdf/op/read { id, offset } → { data (base64, עד 1MB), offset, size, done }
///   POST /pdf/op/end { id } → { removed }
/// כל מנוע מותקן מזוהה עם הפעולות שהוא יודע (Detect): Ghostscript (גם של PDF24), qpdf, MuPDF, PDFtk, Poppler, LibreOffice, Word.
/// פעולות: compress, grayscale, pdfa, repair, protect, unlock, linearize, pdf2png (ZIP), totext, office2pdf {ext}. run מקבל engine לבחירת מנוע. קבצים זמניים ב-%TEMP%\OtzariaBridge-pdfops, נמחקים ב-end או אחרי שעה.</summary>
internal static class PdfOps
{
    private static string Root => Path.Combine(Path.GetTempPath(), "OtzariaBridge-pdfops");
    private static readonly ConcurrentDictionary<string, DateTime> Jobs = new();
    private const long MaxBytes = 1024L * 1024 * 1024; // 1GB

    public static void Register()
    {
        Server.Route("/pdf/engines", _ => Engines());
        Server.Route("/pdf/op/begin", _ => Begin());
        Server.Route("/pdf/op/chunk", Chunk);
        Server.Route("/pdf/op/run", Run);
        Server.Route("/pdf/op/read", Read);
        Server.Route("/pdf/op/end", b => { Cleanup(Id(b)); return new { removed = true }; });
        try { if (Directory.Exists(Root)) foreach (var d in Directory.GetDirectories(Root)) if (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(d) > TimeSpan.FromHours(1)) Directory.Delete(d, true); } catch { }
    }

    // ── זיהוי מנועים ─────────────────────────────────────────────────────
    private static string Gs()
    {
        string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
               la = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var cands = new List<string>
        {
            Path.Combine(pf, "PDF24", "gs", "bin", "gswin64c.exe"), Path.Combine(pf, "PDF24", "gs", "bin", "gswin32c.exe"),
            Path.Combine(pf86, "PDF24", "gs", "bin", "gswin32c.exe"),
        };
        foreach (var root in new[] { Path.Combine(pf, "gs"), Path.Combine(pf86, "gs") })
            try { if (Directory.Exists(root)) foreach (var d in Directory.GetDirectories(root).OrderByDescending(x => x)) { cands.Add(Path.Combine(d, "bin", "gswin64c.exe")); cands.Add(Path.Combine(d, "bin", "gswin32c.exe")); } } catch { }
        return cands.FirstOrDefault(File.Exists) ?? OnPath("gswin64c.exe") ?? OnPath("gswin32c.exe");
    }

    private static string Qpdf()
    {
        string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        try { foreach (var d in Directory.GetDirectories(pf, "qpdf*")) { var p = Path.Combine(d, "bin", "qpdf.exe"); if (File.Exists(p)) return p; } } catch { }
        return OnPath("qpdf.exe");
    }

    internal static bool TrustedEngine(string exe) => exe != null && AppTrust.IsAdminOnlyLocation(exe);

    private static string OnPath(string exe)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            try { var p = Path.Combine(dir.Trim().Trim('"'), exe); if (dir.Length > 0 && File.Exists(p)) return p; } catch { }
        return null;
    }

    // ── כל מנוע מותקן והפעולות שלו. ההעדפה לפי הסדר ברשימה; התוסף יכול לבחור מנוע מסוים (engine) ─────────
    private sealed record Engine(string Id, string Name, string Exe, string[] Ops);

    private static string FindIn(params string[] cands) => cands.FirstOrDefault(c => c != null && File.Exists(c));
    private static IEnumerable<string> Sub(string root, string pattern, params string[] rel)
    {
        var res = new List<string>();
        try { if (Directory.Exists(root)) foreach (var d in Directory.GetDirectories(root, pattern).OrderByDescending(x => x)) res.Add(Path.Combine(new[] { d }.Concat(rel).ToArray())); } catch { }
        return res;
    }

    private static List<Engine> Detect()
    {
        string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
               la = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var list = new List<Engine>();
        // WHY: הגשר מהימן ל-AppTrust ומריץ את המנוע בהרשאותיו; exe מ-LocalAppData/PATH כתיב (או מתחזה בשם gswin64c.exe) היה רץ דרכו.
        // רק מיקום שרק מנהל כותב אליו. סיכון false-positive: PDF24/mutool/qpdf שהותקנו למשתמש בלבד לא יזוהו.
        void Add(string id, string name, string exe, params string[] ops) { if (TrustedEngine(exe)) list.Add(new(id, name, exe, ops)); }

        string gs = Gs();
        Add("ghostscript", gs != null && gs.Contains("PDF24", StringComparison.OrdinalIgnoreCase) ? "Ghostscript (PDF24)" : "Ghostscript", gs,
            "compress", "grayscale", "pdfa", "repair", "protect", "unlock", "pdf2png");
        Add("qpdf", "qpdf", Qpdf(), "linearize", "protect", "unlock", "repair");
        Add("mutool", "MuPDF (mutool)", FindIn(Sub(pf, "MuPDF*", "mutool.exe").Concat(new[] { OnPath("mutool.exe") }).ToArray()), "repair", "unlock", "compress", "pdf2png");
        Add("pdftk", "PDFtk", FindIn(Path.Combine(pf86, "PDFtk", "bin", "pdftk.exe"), Path.Combine(pf, "PDFtk", "bin", "pdftk.exe"), Path.Combine(pf86, "PDFtk Server", "bin", "pdftk.exe"), OnPath("pdftk.exe")),
            "protect", "unlock", "repair");
        Add("poppler", "Poppler", FindIn(Sub(pf, "poppler*", "Library", "bin", "pdftotext.exe").Concat(Sub(pf, "poppler*", "bin", "pdftotext.exe")).Concat(new[] { OnPath("pdftotext.exe") }).ToArray()), "totext");
        Add("libreoffice", "LibreOffice", FindIn(Path.Combine(pf, "LibreOffice", "program", "soffice.exe"), Path.Combine(pf86, "LibreOffice", "program", "soffice.exe"), OnPath("soffice.exe")), "office2pdf");
        Add("word", "Microsoft Word", FindIn(Path.Combine(pf, "Microsoft Office", "root", "Office16", "WINWORD.EXE"), Path.Combine(pf86, "Microsoft Office", "root", "Office16", "WINWORD.EXE")), "office2pdf");

        return list;
    }

    private static object Engines()
    {
        var list = Detect();
        // תוכנות PDF גרפיות שמותקנות (לפתיחה בלבד — אין להן ממשק פקודות) — מ-PdfApi
        return new
        {
            engines = list.Select(e => new { id = e.Id, name = e.Name, path = e.Exe, ops = e.Ops }),
            ops = list.SelectMany(e => e.Ops).Distinct(),
        };
    }

    // ── העברת קבצים ─────────────────────────────────────────────────────
    private static string Id(JsonElement b)
    {
        string id = b.Req("id");
        if (id.Length != 32 || !id.All(Uri.IsHexDigit)) throw BridgeException.BadRequest("מזהה לא תקין");
        if (!Jobs.ContainsKey(id)) throw new BridgeException("not_found", "הפעולה לא נמצאה (פג תוקף?)", 404);
        Jobs[id] = DateTime.UtcNow;
        return id;
    }
    private static string Dir(string id) => Path.Combine(Root, id);

    private static object Begin()
    {
        if (Kiosk.IsActive) throw BridgeException.Locked();
        string id = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(Dir(id));
        Jobs[id] = DateTime.UtcNow;
        return new { id };
    }

    private static object Chunk(JsonElement b)
    {
        string id = Id(b), f = Path.Combine(Dir(id), "in.pdf");
        byte[] data = Convert.FromBase64String(b.Req("data"));
        using (var s = new FileStream(f, FileMode.Append, FileAccess.Write)) { if (s.Length + data.Length > MaxBytes) throw BridgeException.BadRequest("הקובץ גדול מדי"); s.Write(data); }
        return new { size = new FileInfo(f).Length };
    }

    private static object Read(JsonElement b)
    {
        string id = Id(b), f = Path.Combine(Dir(id), "out.pdf");
        if (!File.Exists(f)) throw new BridgeException("not_found", "אין תוצאה", 404);
        long off = b.Long("offset", 0), size = new FileInfo(f).Length;
        using var s = File.OpenRead(f);
        s.Seek(off, SeekOrigin.Begin);
        var buf = new byte[(int)Math.Min(1024 * 1024, Math.Max(0, size - off))];
        int n = s.Read(buf, 0, buf.Length);
        return new { data = Convert.ToBase64String(buf, 0, n), offset = off + n, size, done = off + n >= size };
    }

    private static void Cleanup(string id) { try { Jobs.TryRemove(id, out _); Directory.Delete(Dir(id), true); } catch { } }

    // ── הרצה ────────────────────────────────────────────────────────────
    private static object Run(JsonElement b)
    {
        if (Kiosk.IsActive) throw BridgeException.Locked();
        string id = Id(b), dir = Dir(id), inp = Path.Combine(dir, "in.pdf"), outp = Path.Combine(dir, "out.pdf");
        if (!File.Exists(inp)) throw BridgeException.BadRequest("לא הועלה קובץ");
        try { File.Delete(outp); } catch { }
        string op = b.Req("op"), want = b.Str("engine", "");
        JsonElement p = b.TryGetProperty("params", out var pp) && pp.ValueKind == JsonValueKind.Object ? pp : default;
        string P(string k, string def = "") => p.ValueKind == JsonValueKind.Object ? (p.Str(k, def) ?? def) : def;
        var all = Detect().Where(e => e.Ops.Contains(op)).ToList();
        var eng = all.FirstOrDefault(e => e.Id == want) ?? all.FirstOrDefault()
                  ?? throw BridgeException.Unsupported("לא נמצאה במחשב תוכנה שיודעת לבצע את הפעולה (" + op + ")");
        string ext = P("ext", "pdf").ToLowerInvariant();
        if (op == "office2pdf")
        {
            if (!new[] { "doc", "docx", "odt", "rtf", "xls", "xlsx", "ods", "ppt", "pptx", "odp", "txt" }.Contains(ext)) throw BridgeException.BadRequest("סוג קובץ לא נתמך: " + ext);
            string src = Path.Combine(dir, "in." + ext); File.Move(inp, src, true); inp = src;
        }
        string up = P("userPassword"), own = P("ownerPassword");
        if (own.Length == 0) own = up.Length > 0 ? up : Guid.NewGuid().ToString("N");
        var a = new List<string>();
        string exe = eng.Exe, resultFile = outp;
        switch (eng.Id)
        {
            case "ghostscript":
                a.AddRange(new[] { "-dBATCH", "-dNOPAUSE", "-dQUIET", "-dSAFER" });
                if (op == "pdf2png")
                {
                    Directory.CreateDirectory(Path.Combine(dir, "png"));
                    a.AddRange(new[] { "-sDEVICE=png16m", "-r" + Math.Clamp((int)b.Long("dpi", 150), 50, 600), "-sOutputFile=" + Path.Combine(dir, "png", "p%04d.png"), inp });
                    break;
                }
                a.AddRange(new[] { "-sDEVICE=pdfwrite", "-dCompatibilityLevel=1.7" });
                switch (op)
                {
                    case "compress":
                        string lvl = P("level", "ebook"); if (!new[] { "screen", "ebook", "printer", "prepress" }.Contains(lvl)) lvl = "ebook";
                        a.Add("-dPDFSETTINGS=/" + lvl); a.Add("-dDetectDuplicateImages=true"); break;
                    case "grayscale": a.AddRange(new[] { "-sColorConversionStrategy=Gray", "-dProcessColorModel=/DeviceGray" }); break;
                    case "pdfa": a.AddRange(new[] { "-dPDFA=2", "-dPDFACompatibilityPolicy=1", "-sColorConversionStrategy=RGB" }); break;
                    case "unlock": a.Add("-sPDFPassword=" + P("password")); break;
                    case "protect": a.Add("-sOwnerPassword=" + own); if (up.Length > 0) a.Add("-sUserPassword=" + up); a.Add("-dEncryptionR=3"); a.Add("-dKeyLength=128"); break;
                }
                a.Add("-sOutputFile=" + outp); a.Add(inp); break;
            case "qpdf":
                if (op == "linearize") a.AddRange(new[] { "--linearize", inp, outp });
                else if (op == "repair") a.AddRange(new[] { inp, outp });
                else if (op == "unlock") { a.Add("--password=" + P("password")); a.AddRange(new[] { "--decrypt", inp, outp }); }
                else if (op == "protect") a.AddRange(new[] { "--encrypt", up, own, "256", "--", inp, outp });
                break;
            case "mutool":
                if (op == "pdf2png") { Directory.CreateDirectory(Path.Combine(dir, "png")); a.AddRange(new[] { "draw", "-r", Math.Clamp((int)b.Long("dpi", 150), 50, 600).ToString(), "-o", Path.Combine(dir, "png", "p%04d.png"), inp }); break; }
                a.Add("clean");
                if (op == "unlock") { a.Add("-p"); a.Add(P("password")); a.Add("-D"); }
                if (op == "compress") a.Add("-gggz");
                a.AddRange(new[] { inp, outp }); break;
            case "pdftk":
                if (op == "unlock") a.AddRange(new[] { inp, "input_pw", P("password"), "output", outp });
                else if (op == "protect") { a.AddRange(new[] { inp, "output", outp, "owner_pw", own }); if (up.Length > 0) { a.Add("user_pw"); a.Add(up); } a.Add("encrypt_128bit"); }
                else a.AddRange(new[] { inp, "output", outp });
                break;
            case "poppler":
                resultFile = Path.Combine(dir, "out.txt"); a.AddRange(new[] { "-enc", "UTF-8", "-layout", inp, resultFile }); break;
            case "libreoffice":
                a.AddRange(new[] { "--headless", "--norestore", "--convert-to", "pdf", "--outdir", dir, inp }); break;
            case "word":
                // וורד בלי ממשק פקודות להמרה — דרך PowerShell ו-COM
                exe = "powershell.exe";
                a.AddRange(new[] { "-NoProfile", "-NonInteractive", "-Command",
                    "$w=New-Object -ComObject Word.Application;$w.Visible=$false;try{$d=$w.Documents.Open($args[0],$false,$true);$d.ExportAsFixedFormat($args[1],17);$d.Close($false)}finally{$w.Quit()}", inp, outp }); break;
            case "tesseract":
                Directory.CreateDirectory(Path.Combine(dir, "png"));
                throw BridgeException.Unsupported("זיהוי ב-Tesseract המותקן יתווסף בהמשך — בינתיים ה-OCR המובנה בתוסף");
        }
        if (a.Count == 0) throw BridgeException.BadRequest("פעולה לא מוכרת: " + op);

        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var x in a) psi.ArgumentList.Add(x);
        using var pr = Process.Start(psi);
        var err = pr.StandardError.ReadToEndAsync().Observe("pdfops stderr", quiet: true); pr.StandardOutput.ReadToEndAsync().Observe("pdfops stdout", quiet: true);
        if (!pr.WaitForExit(10 * 60 * 1000)) { try { pr.Kill(true); } catch { } throw new BridgeException("timeout", "הפעולה ארכה יותר מדי", 504); }

        if (op == "office2pdf") { var made = Path.Combine(dir, "in.pdf"); if (eng.Id == "libreoffice" && File.Exists(made)) File.Move(made, outp, true); }
        if (op == "pdf2png")
        {
            // התמונות נארזות ל-ZIP אחד ונקראות כמו כל תוצאה
            var pngs = Directory.Exists(Path.Combine(dir, "png")) ? Directory.GetFiles(Path.Combine(dir, "png"), "*.png").OrderBy(x => x).ToArray() : Array.Empty<string>();
            if (pngs.Length > 0) { resultFile = outp; if (File.Exists(outp)) File.Delete(outp); System.IO.Compression.ZipFile.CreateFromDirectory(Path.Combine(dir, "png"), outp); }
        }
        if (resultFile != outp && File.Exists(resultFile)) File.Move(resultFile, outp, true);
        bool okExit = pr.ExitCode == 0 || (eng.Id == "qpdf" && pr.ExitCode == 3); // qpdf: 3 = אזהרות, התוצאה תקינה
        if (!File.Exists(outp) || new FileInfo(outp).Length == 0 || !okExit)
        {
            string msg = (err.Result ?? "").Trim();
            if (op == "unlock" && msg.Contains("password", StringComparison.OrdinalIgnoreCase)) throw new BridgeException("wrong_password", "סיסמה שגויה", 401);
            throw new BridgeException("op_failed", "הפעולה נכשלה (" + eng.Name + "): " + (msg.Length > 300 ? msg[..300] : msg), 500);
        }
        string kind = op == "pdf2png" ? "zip" : op == "totext" ? "txt" : "pdf";
        return new { size = new FileInfo(outp).Length, inSize = new FileInfo(inp).Length, engine = eng.Id, engineName = eng.Name, kind };
    }
}
