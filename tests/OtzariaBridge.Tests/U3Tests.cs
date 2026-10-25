using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using OtzariaBridge;
using Xunit;

namespace OtzariaBridge.Tests;

/// <summary>U3: הקשחת FsGuard/FsApi/UpdateApi/PackB מול ממצאי QA עוין (סבב 2). WHY בכל בדיקה: העקיפה שנמצאה.</summary>
public class U3Tests
{
    private static byte[] Zip(params (string name, byte[] data)[] entries)
    {
        var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, true))
            foreach (var (n, d) in entries) { using var w = z.CreateEntry(n).Open(); w.Write(d); }
        return ms.ToArray();
    }
    private static (string, byte[]) E(string n, string t) => (n, Encoding.UTF8.GetBytes(t));
    private static string Inspect(string ext, byte[] data) => FsGuard.InspectDocument(new MemoryStream(data), ext);
    private const string W = "xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"";
    private const string X = "xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"";

    // ---- 1: חלק ראשי בנתיב שרירותי / בלי סיומת xml ----
    [Fact]
    public void Dde_in_main_part_with_custom_path_is_blocked() =>
        Assert.NotNull(Inspect(".docx", Zip(E("[Content_Types].xml", "<Types/>"), E("zzz/main.xml",
            $"<w:document {W}><w:body><w:p><w:r><w:instrText>DDEAUTO c:\\\\windows\\\\system32\\\\cmd.exe</w:instrText></w:r></w:p></w:body></w:document>"))));

    [Fact]
    public void Dde_formula_in_sheet_outside_xl_prefix_is_blocked() =>
        Assert.NotNull(Inspect(".xlsx", Zip(E("data/sheet.part", $"<worksheet {X}><sheetData><row><c><f>cmd|'/c calc'!A1</f></c></row></sheetData></worksheet>"))));

    [Fact]
    public void Xml_part_without_xml_extension_is_scanned_by_content() =>
        Assert.NotNull(Inspect(".docx", Zip(E("word/document.dat",
            $"<w:document {W}><w:p><w:fldSimple w:instr=\"DDE cmd calc\"/></w:p></w:document>"))));

    [Theory]
    [InlineData("a/b/xl/macrosheets/sheet1.xml")]
    [InlineData("foo/embeddings/oleObject1.bin")]
    [InlineData("x/activeX/activeX1.xml")]
    public void Nested_macro_parts_blocked(string name) =>
        Assert.NotNull(Inspect(".xlsx", Zip(E(name, "<x/>"))));

    [Fact]
    public void Unknown_binary_non_media_part_is_blocked() =>
        Assert.NotNull(Inspect(".docx", Zip(E("word/document.xml", "<w:document " + W + "/>"), ("word/evil.dll", new byte[] { 0x4D, 0x5A, 0x90, 0 }))));

    [Fact]
    public void Media_and_printer_settings_parts_are_allowed()
    {
        var d = Zip(E("[Content_Types].xml", "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"/>"),
            E("word/document.xml", $"<w:document {W}><w:body><w:p><w:r><w:t>=a|b</w:t></w:r></w:p></w:body></w:document>"),
            ("word/media/image1.png", new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3 }),
            ("xl/printerSettings/printerSettings1.bin", new byte[] { 1, 2, 3, 4 }),
            ("word/fonts/font1.odttf", new byte[] { 9, 9, 9 }));
        Assert.Null(Inspect(".docx", d));   // גם w:t עם '|' אינו נוסחת DDE
    }

    [Fact]
    public void Macro_content_type_in_content_types_is_blocked() =>
        Assert.NotNull(Inspect(".docx", Zip(E("[Content_Types].xml",
            "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.ms-word.document.macroEnabled.main+xml\"/></Types>"))));

    [Fact]
    public void Zip_with_office_content_named_txt_is_inspected() =>
        Assert.NotNull(Inspect(".txt", Zip(("xl/vbaProject.bin", new byte[] { 1 }))));

    [Fact]
    public void Plain_zip_and_epub_not_inspected() =>
        Assert.Null(Inspect(".epub", Zip(E("Scripts/a.js", "x"))));

    // ---- 2: RTF/OLE מעבר לרף הישן ----
    [Fact]
    public void Rtf_object_after_old_16mb_cap_is_blocked()
    {
        using var ms = new MemoryStream();
        ms.Write(Encoding.ASCII.GetBytes(@"{\rtf1 "));
        ms.Write(Enumerable.Repeat((byte)'a', 17 * 1024 * 1024).ToArray());
        ms.Write(Encoding.ASCII.GetBytes(@"{\object\objemb{\*\objdata 0105}}}"));
        Assert.NotNull(Inspect(".rtf", ms.ToArray()));
    }

    [Fact]
    public void Rtf_pattern_straddling_chunk_boundary_is_blocked()
    {
        // 8MB = גבול נתח; \objdata חוצה אותו
        var pad = Enumerable.Repeat((byte)'a', 8 * 1024 * 1024 - 6).ToArray();
        var data = Encoding.ASCII.GetBytes(@"{\rtf1 ").Concat(pad).Concat(Encoding.ASCII.GetBytes(@"\objdata 01}")).ToArray();
        Assert.NotNull(Inspect(".rtf", data));
    }

    [Fact]
    public void Ole_macro_after_old_32mb_cap_is_blocked()
    {
        byte[] ole = { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 };
        var data = ole.Concat(new byte[33 * 1024 * 1024]).Concat(Encoding.Unicode.GetBytes("_VBA_PROJECT")).ToArray();
        Assert.NotNull(Inspect(".doc", data));
    }

    [Fact]
    public void Clean_rtf_passes() => Assert.Null(Inspect(".rtf", Encoding.ASCII.GetBytes(@"{\rtf1\ansi hello}")));

    // ---- 3: csv/tsv ----
    [Theory]
    [InlineData("a.csv")] [InlineData("a.tsv")]
    public void Csv_tsv_not_openable(string n) => Assert.False(FsGuard.IsOpenAllowedExt(n));

    [Fact]
    public void Xlsm_zip_named_csv_blocked_by_content() =>
        Assert.NotNull(Inspect(".csv", Zip(E("xl/macrosheets/sheet1.xml", "<x/>"))));

    // ---- 4: העתקה מוגנת ל-Open ----
    [Fact]
    public void CopyForOpen_copies_locks_and_keeps_name()
    {
        string root = Path.Combine(Path.GetTempPath(), "u3open" + Guid.NewGuid().ToString("N"));
        string srcPath = Path.Combine(Path.GetTempPath(), "u3src" + Guid.NewGuid().ToString("N") + ".docx");
        try
        {
            File.WriteAllText(srcPath, "hello");
            using var src = new FileStream(srcPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var keep = FsApi.CopyForOpen(src, srcPath, root, out string copy);
            Assert.Equal(Path.GetFileName(srcPath), Path.GetFileName(copy));
            // המחזיק חוסם מחיקה וכתיבה מצד אחרים (read-only + FileShare.Read)
            Assert.ThrowsAny<Exception>(() => File.Delete(copy));
            Assert.ThrowsAny<Exception>(() => new FileStream(copy, FileMode.Open, FileAccess.Write, FileShare.ReadWrite));
            using (var rd = new FileStream(copy, FileMode.Open, FileAccess.Read, FileShare.Read)) Assert.Equal(5, rd.Length);
            Assert.Equal("hello", File.ReadAllText(srcPath));   // המקור לא נפגע
        }
        finally { try { Directory.Delete(root, true); } catch { } try { File.Delete(srcPath); } catch { } }
    }

    // ---- 5: Office autoload + סיומות בינאריות ישנות ----
    [Fact]
    public void Office_autoload_dirs_are_autorun_roots()
    {
        var roots = FsGuard.AutoRunRoots();
        string ad = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        Assert.Contains(Path.Combine(ad, "Microsoft", "Word", "STARTUP"), roots);
        Assert.Contains(Path.Combine(ad, "Microsoft", "Excel", "XLSTART"), roots);
        Assert.Contains(Path.Combine(ad, "Microsoft", "Templates"), roots);
        Assert.Contains(Path.Combine(ad, "Microsoft", "AddIns"), roots);
        Assert.Contains(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Office"), roots);
    }

    [Theory]
    [InlineData("a.dot")] [InlineData("a.xlt")] [InlineData("a.xls")]
    public void Legacy_binary_templates_write_blocked(string n) => Assert.True(FsGuard.IsWriteBlockedExt(n));

    [Fact]
    public void Docx_write_still_allowed() => Assert.False(FsGuard.IsWriteBlockedExt("a.docx"));

    // ---- 6: SelectRelease עם חתימה ----
    private static string Rel(string tag) =>
        "{\"tag_name\":\"" + tag + "\",\"html_url\":\"https://github.com/" + UpdateApi.Repo + "/releases/tag/" + tag + "\",\"assets\":[" +
        "{\"name\":\"OtzariaBridge.exe\",\"browser_download_url\":\"https://objects.githubusercontent.com/x/" + tag + "/exe\"}," +
        "{\"name\":\"SHA256SUMS.txt\",\"browser_download_url\":\"https://objects.githubusercontent.com/x/" + tag + "/sums\"}," +
        "{\"name\":\"SHA256SUMS.txt.sig\",\"browser_download_url\":\"https://objects.githubusercontent.com/x/" + tag + "/sig\"}," +
        "{\"name\":\"BRIDGE_VERSION.txt\",\"browser_download_url\":\"https://objects.githubusercontent.com/x/" + tag + "/bv\"}]}";

    [Fact]
    public async Task Unsigned_highest_release_does_not_block_older_signed()
    {
        using var ec = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        string pub = Convert.ToBase64String(ec.ExportSubjectPublicKeyInfo());
        byte[] sumsOk = Encoding.UTF8.GetBytes("# tag=v1.0.5\n# keyver=1\n");
        string sigOk = Convert.ToBase64String(ec.SignData(sumsOk, System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        Task<byte[]> Dl(string url, long max)
        {
            if (url.Contains("/v9.9.9/sums")) return Task.FromResult(Encoding.UTF8.GetBytes("# tag=v9.9.9\n# keyver=1\n"));
            if (url.Contains("/v9.9.9/sig")) return Task.FromResult(Encoding.ASCII.GetBytes(Convert.ToBase64String(new byte[64])));   // חתימה שגויה
            if (url.Contains("/v1.0.5/sums")) return Task.FromResult(sumsOk);
            if (url.Contains("/v1.0.5/sig")) return Task.FromResult(Encoding.ASCII.GetBytes(sigOk));
            throw new InvalidOperationException();
        }
        var json = "[" + Rel("v9.9.9") + "," + Rel("v1.0.5") + "]";
        Assert.Equal("v9.9.9", UpdateApi.SelectRelease(json).Tag);   // הפונקציה הפשוטה (בלי אימות) נשארת
        var r = await UpdateApi.SelectVerifiedRelease(json, Dl, pub);
        Assert.Equal("v1.0.5", r.Tag);
        Assert.Null(await UpdateApi.SelectVerifiedRelease(json, Dl, ""));   // בלי מפתח: אף אחת
    }

    [Fact]
    public async Task Signed_tag_mismatch_is_skipped()
    {
        using var ec = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        string pub = Convert.ToBase64String(ec.ExportSubjectPublicKeyInfo());
        byte[] sums = Encoding.UTF8.GetBytes("# tag=v0.0.1\n# keyver=1\n");   // חתום אך לתגית אחרת (ערבוב נכסים)
        string sig = Convert.ToBase64String(ec.SignData(sums, System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        Task<byte[]> Dl(string url, long max) => Task.FromResult(url.EndsWith("/sig") ? Encoding.ASCII.GetBytes(sig) : sums);
        Assert.Null(await UpdateApi.SelectVerifiedRelease("[" + Rel("v2.0.0") + "]", Dl, pub));
    }

    // ---- 7: html_url ----
    [Theory]
    [InlineData("https://github.com/Hadabadra/achat-shaalti/releases/tag/v1", true)]
    [InlineData("https://evil.example/Hadabadra/achat-shaalti/releases", false)]
    [InlineData("http://github.com/Hadabadra/achat-shaalti/releases", false)]
    [InlineData("https://github.com/other/repo/releases/tag/v1", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("https://github.com.evil.com/Hadabadra/achat-shaalti/releases", false)]
    public void Release_page_url_allowlist(string u, bool ok) => Assert.Equal(ok, UpdateApi.IsAllowedReleasePage(u));

    [Fact]
    public void Foreign_html_url_dropped_from_candidate() =>
        Assert.Null(UpdateApi.SelectRelease("[" + Rel("v1.0.0").Replace("https://github.com/" + UpdateApi.Repo, "https://evil.example/x") + "]").Url);

    // ---- 7: PackB סכמה ----
    [Theory]
    [InlineData("lang", "\"he\"", true)]
    [InlineData("diag.autoReport", "true", true)]
    [InlineData("kiosk.restIdleMinutes", "5", true)]
    [InlineData("kiosk.restIdleMinutes", "1e300", false)]
    [InlineData("lang", "{\"a\":1}", false)]
    [InlineData("lang", "null", false)]
    [InlineData("kiosk.dedicationText", "[1,2]", false)]
    [InlineData("kiosk.zmanimShow", "[\"a\",\"b\"]", true)]
    [InlineData("kiosk.zmanimShow", "[{\"a\":1}]", false)]
    public void Import_value_schema(string key, string json, bool ok) => Assert.Equal(ok, PackB.ImportValueOk(key, JsonNode.Parse(json)));

    [Fact]
    public void Import_rejects_oversized_string() =>
        Assert.False(PackB.ImportValueOk("kiosk.venueName", JsonValue.Create(new string('x', 5000))));

    // ---- 7: DACL ----
    [Fact]
    public void Acl_write_check_fails_closed_on_missing_path() =>
        Assert.True(InstallTrust.AclAllowsUserWrite(@"C:\definitely\not\here\a.exe"));

    [Fact]
    public void Acl_write_check_detects_user_writable_temp_file()
    {
        string f = Path.Combine(Path.GetTempPath(), "u3acl" + Guid.NewGuid().ToString("N") + ".exe");
        try { File.WriteAllText(f, "x"); Assert.True(InstallTrust.AclAllowsUserWrite(f)); }
        finally { File.Delete(f); }
    }
}
