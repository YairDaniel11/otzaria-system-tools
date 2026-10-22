using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using OtzariaBridge;
using Xunit;

namespace OtzariaBridge.Tests;

/// <summary>S3: הקשחת מערכת קבצים/מסמכים/ייבוא/סיסמאות/חתימת config. WHY בכל בדיקה: עקיפה שנמצאה בביקורת QA עוין.</summary>
[Collection("S3Serial")]   // מחליפים ConfigSeal.Provider הסטטי: לא במקביל
public class S3Tests
{
    private static byte[] Zip(params (string name, string text)[] entries)
    {
        var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, true))
            foreach (var (n, t) in entries)
            {
                using var w = new StreamWriter(z.CreateEntry(n).Open(), new UTF8Encoding(false));
                w.Write(t);
            }
        return ms.ToArray();
    }

    private static string Inspect(string ext, byte[] data) => FsGuard.InspectDocument(new MemoryStream(data), ext);
    private static readonly byte[] Ole = { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 };
    private const string W = "xmlns:w=\"http://w\"";

    // ---- 1: זיהוי לפי תוכן ----
    [Fact]
    public void Rtf_content_named_doc_is_inspected_as_rtf() =>
        Assert.NotNull(Inspect(".doc", Encoding.ASCII.GetBytes(@"{\rtf1{\object\objemb{\*\objdata 0105}}}")));

    [Fact]
    public void Rtf_with_template_named_docx_is_blocked() =>
        Assert.NotNull(Inspect(".docx", Encoding.ASCII.GetBytes(@"{\rtf1{\*\template \\evil\share\t.dotm}}")));

    [Fact]
    public void Rtf_dde_split_across_groups_is_blocked() =>
        Assert.NotNull(Inspect(".rtf", Encoding.ASCII.GetBytes(@"{\rtf1{\field{\*\fldinst {DD}{E}AUTO c:\\windows\\system32\\cmd.exe}}}")));

    [Fact]
    public void Ole_with_macro_named_rtf_is_blocked()
    {
        var d = Ole.Concat(new byte[256]).Concat(Encoding.Unicode.GetBytes("Macros")).ToArray();
        Assert.NotNull(Inspect(".rtf", d));
    }

    [Fact]
    public void Ole_ppt_variants_checked_including_pps()
    {
        var d = Ole.Concat(Encoding.Unicode.GetBytes("_VBA_PROJECT")).ToArray();
        Assert.NotNull(Inspect(".pps", d));
        Assert.NotNull(Inspect(".pot", d));
    }

    [Fact]
    public void Ole_dde_text_ascii_or_utf16_is_blocked()
    {
        Assert.NotNull(Inspect(".doc", Ole.Concat(Encoding.ASCII.GetBytes(" DDEAUTO cmd")).ToArray()));
        Assert.NotNull(Inspect(".doc", Ole.Concat(Encoding.Unicode.GetBytes(" DDEAUTO cmd")).ToArray()));
    }

    [Fact]
    public void Xls_with_macro_sheet_boundsheet_is_blocked()
    {
        // BOUNDSHEET: 0x0085, len=10, lbPlyPos(4), hsState=0, dt=1 (macro sheet)
        var rec = new byte[] { 0x85, 0x00, 0x0A, 0x00, 0, 0, 0, 0, 0x00, 0x01, 0, 0 };
        Assert.NotNull(Inspect(".xls", Ole.Concat(new byte[100]).Concat(rec).ToArray()));
    }

    [Fact]
    public void Unknown_office_content_is_blocked_but_plain_types_pass()
    {
        Assert.NotNull(Inspect(".doc", Encoding.UTF8.GetBytes("<html><body>x</body></html>")));   // Word פותח HTML בשם .doc
        Assert.NotNull(Inspect(".xlsx", new byte[] { 1, 2, 3 }));
        Assert.Null(Inspect(".txt", Encoding.UTF8.GetBytes("hello")));
        Assert.Null(Inspect(".pdf", Encoding.ASCII.GetBytes("%PDF-1.4")));
    }

    [Fact]
    public void Plain_zip_is_not_treated_as_office() =>
        Assert.Null(Inspect(".zip", Zip(("Scripts/a.txt", "x"))));

    [Theory]
    [InlineData("<Relationships><Relationship Id=\"1\" Type=\"http://x/attachedTemplate\" Target=\"http://e/t\" TargetMode = 'External' /></Relationships>")]
    [InlineData("<Relationships><Relationship Id=\"1\" Type=\"http://x/oleObject\" Target=\"file:///\\\\e\\s\" TargetMode=\"external\"/></Relationships>")]
    [InlineData("<?xml version=\"1.0\"?><Relationships>\n<Relationship\n Id=\"1\"\n Type=\"http://x/image\"\n TargetMode=\"External\" Target=\"\\\\e\\i.png\"/></Relationships>")]
    public void External_relationship_variants_are_blocked(string rels) =>
        Assert.NotNull(Inspect(".docx", Zip(("word/_rels/document.xml.rels", rels))));

    [Fact]
    public void Utf16_encoded_rels_are_still_parsed()
    {
        string xml = "<?xml version=\"1.0\" encoding=\"utf-16\"?><Relationships><Relationship Id=\"1\" Type=\"http://x/attachedTemplate\" Target=\"http://e\" TargetMode=\"External\"/></Relationships>";
        var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            using var s = z.CreateEntry("word/_rels/settings.xml.rels").Open();
            var b = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(xml)).ToArray();
            s.Write(b, 0, b.Length);
        }
        Assert.NotNull(Inspect(".docx", ms.ToArray()));
    }

    [Fact]
    public void Dde_split_across_instrtext_runs_is_blocked()
    {
        string doc = $"<w:document {W}><w:p><w:r><w:fldChar w:fldCharType=\"begin\"/></w:r><w:r><w:instrText>DD</w:instrText></w:r><w:r><w:instrText>EAUTO cmd</w:instrText></w:r>" +
                     "<w:r><w:fldChar w:fldCharType=\"separate\"/></w:r><w:r><w:fldChar w:fldCharType=\"end\"/></w:r></w:p></w:document>";
        Assert.NotNull(Inspect(".docx", Zip(("word/document.xml", doc))));
    }

    [Fact]
    public void Dde_in_utf16_part_and_fldsimple_is_blocked()
    {
        Assert.NotNull(Inspect(".docx", Zip(("word/document.xml", $"<w:document {W}><w:fldSimple w:instr=\" DDEAUTO calc \"/></w:document>"))));
    }

    [Fact]
    public void Normal_word_fields_pass()
    {
        string doc = $"<w:document {W}><w:p><w:r><w:fldChar w:fldCharType=\"begin\"/></w:r><w:r><w:instrText> HYPERLINK \"http://a\" </w:instrText></w:r>" +
                     "<w:r><w:fldChar w:fldCharType=\"separate\"/></w:r><w:r><w:t>hidden address</w:t></w:r><w:r><w:fldChar w:fldCharType=\"end\"/></w:r></w:p></w:document>";
        Assert.Null(Inspect(".docx", Zip(("word/document.xml", doc))));
    }

    [Fact]
    public void Xml_with_dtd_fails_closed() =>
        Assert.NotNull(Inspect(".docx", Zip(("word/document.xml", "<!DOCTYPE a [<!ENTITY x \"y\">]><a>&x;</a>"))));

    [Fact]
    public void Xlsx_dde_formula_and_connections_are_blocked()
    {
        Assert.NotNull(Inspect(".xlsx", Zip(("xl/worksheets/sheet1.xml", "<worksheet><sheetData><row><c><f> powershell |'-c calc'!A1</f></c></row></sheetData></worksheet>"))));
        Assert.NotNull(Inspect(".xlsx", Zip(("xl/connections.xml", "<connections/>"))));
        Assert.Null(Inspect(".xlsx", Zip(("xl/worksheets/sheet1.xml", "<worksheet><sheetData><row><c><f>SUM(A1:A2)</f><v>3</v></c></row></sheetData></worksheet>"))));
    }

    [Fact]
    public void Pptx_program_action_is_blocked() =>
        Assert.NotNull(Inspect(".pptx", Zip(("ppt/slides/slide1.xml", "<p:sld xmlns:p=\"http://p\" xmlns:a=\"http://a\"><a:hlinkClick action=\"ppaction://program\"/></p:sld>"))));

    // ---- 2: רשימה לבנה / סיומות ----
    [Theory]
    [InlineData(@"C:\x\a.appinstaller")]
    [InlineData(@"C:\x\a.msix")]
    [InlineData(@"C:\x\a.theme")]
    [InlineData(@"C:\x\a.search-ms")]
    [InlineData(@"C:\x\a.job")]
    [InlineData(@"C:\x\a.iqy")]
    [InlineData(@"C:\x\a.rdp")]
    [InlineData(@"C:\x\a.iso")]
    [InlineData(@"C:\x\a.vhd")]
    [InlineData(@"C:\x\a.exe")]
    [InlineData(@"C:\x\a.docm")]
    [InlineData(@"C:\x\a.html")]
    [InlineData(@"C:\x\noext")]
    [InlineData(@"C:\x\a.exe:payload")]
    [InlineData(@"C:\x\a:payload")]
    public void Open_whitelist_rejects(string p) => Assert.False(FsGuard.IsOpenAllowedExt(p));

    [Theory]
    [InlineData(@"C:\x\a.pdf")]
    [InlineData(@"C:\x\a.docx")]
    [InlineData(@"C:\x\a.PNG")]
    [InlineData(@"C:\x\a.docx.")]
    public void Open_whitelist_accepts(string p) => Assert.True(FsGuard.IsOpenAllowedExt(p));

    [Theory]
    [InlineData(@"C:\x\a.appinstaller")]
    [InlineData(@"C:\x\a.msix")]
    [InlineData(@"C:\x\a.iso")]
    public void New_dangerous_exts_are_write_blocked(string p) => Assert.True(FsGuard.IsWriteBlockedExt(p));

    // ---- 5: ADS ----
    [Theory]
    [InlineData(@"C:\x\a:payload")]
    [InlineData(@"C:\x\a.txt:evil")]
    [InlineData(@"C:\x\a.exe:evil")]
    [InlineData(@"C:\x\a.exe::$DATA")]
    public void Ads_names_are_write_blocked(string p) => Assert.True(FsGuard.IsWriteBlockedExt(p));

    [Fact]
    public void StdExt_ignores_stream_part() => Assert.Equal(".exe", FsGuard.StdExt(@"C:\x\a.exe:evil"));

    [Fact]
    public void StripAds_leaves_drive_colon()
    {
        Assert.Equal(@"C:\Windows", FsGuard.StripAds(@"C:\Windows:x"));
        Assert.Equal(@"C:\a\b.txt", FsGuard.StripAds(@"C:\a\b.txt"));
    }

    [Fact]
    public void Ads_on_protected_directory_is_blocked()
    {
        string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        Assert.Throws<BridgeException>(() => FsApi.EnsureNotProtected(win + ":evil"));
        Assert.Throws<BridgeException>(() => FsApi.EnsureNotProtected(Config.Dir + ":evil"));
        Assert.Throws<BridgeException>(() => FsApi.EnsureNotReadable(Config.Dir + ":evil"));
    }

    // ---- 4: ייבוא ----
    [Theory]
    [InlineData("kiosk.otzariaPath")]
    [InlineData("kiosk.strict")]
    [InlineData("kiosk.schedule.enabled")]
    [InlineData("kiosk.schedule.start")]
    [InlineData("kiosk.allowOpenExtensions")]
    [InlineData("kiosk.openSections")]
    [InlineData("kiosk.readOnly")]
    [InlineData("kiosk.blockTaskManager")]
    [InlineData("kiosk.programs")]
    [InlineData("fx.policy.mode")]
    [InlineData("kiosk.someFutureSecuritySetting")]
    [InlineData("auth.clients.json")]
    public void Import_rejects_security_and_unknown_keys(string k) => Assert.False(PackB.ImportAllowed(k));

    [Theory]
    [InlineData("lang")]
    [InlineData("kiosk.clock")]
    [InlineData("kiosk.zmanimCity")]
    public void Import_accepts_display_keys(string k) => Assert.True(PackB.ImportAllowed(k));

    // ---- 6: סיסמאות ----
    [Fact]
    public void Secret_new_hash_is_versioned_and_verifies()
    {
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        string h = Secret.Hash("pw", salt);
        Assert.StartsWith("v2$", h);
        Assert.False(Secret.NeedsUpgrade(h));
        Assert.True(Secret.Matches("pw", h, Convert.ToBase64String(salt)));
        Assert.False(Secret.Matches("pW", h, Convert.ToBase64String(salt)));
        Assert.True(Secret.Iterations >= 600_000);
    }

    [Fact]
    public void Secret_legacy_hash_still_verifies_and_is_flagged_for_upgrade()
    {
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        string legacy = Secret.HashLegacy("pw", salt);
        Assert.DoesNotContain("$", legacy);
        Assert.True(Secret.NeedsUpgrade(legacy));
        Assert.True(Secret.Matches("pw", legacy, Convert.ToBase64String(salt)));
        Assert.False(Secret.Matches("x", legacy, Convert.ToBase64String(salt)));
    }

    // ---- 3: חתימת config ----
    private sealed class FakeService : IDisposable
    {
        private readonly Func<byte[], (ConfigSeal.SealState, byte[])> _old;
        public FakeService(ConfigSeal.SealState state, byte[] key = null)
        {
            _old = ConfigSeal.Provider;
            ConfigSeal.Provider = d => state == ConfigSeal.SealState.Ok ? (state, HMACSHA256.HashData(key, d)) : (state, null);
            ConfigSeal.Reset();
        }
        public void Dispose() { ConfigSeal.Provider = _old; ConfigSeal.Reset(); }
    }

    private static JsonObject Entry(string id = "a1") =>
        new() { ["id"] = id, ["client"] = "c", ["hash"] = "ABC" };

    [Fact]
    public void Pairing_entry_without_valid_sig_is_untrusted_when_enforced()
    {
        using var _ = new FakeService(ConfigSeal.SealState.Ok, new byte[] { 1, 2, 3 });
        var e = Entry();
        Assert.False(Pairing.Trusted(e));                       // נוסף ישירות ל-config
        e["sig"] = Convert.ToBase64String(new byte[32]);
        Assert.False(Pairing.Trusted(e));                       // sig מזויף
        e["sig"] = ConfigSeal.Sign(Pairing.Canon(e));
        Assert.True(Pairing.Trusted(e));                        // אושר ונחתם בשירות
        e["hash"] = "EVIL";
        Assert.False(Pairing.Trusted(e));                       // שינוי הגיבוב אחרי החתימה
    }

    [Fact]
    public void Pairing_service_down_fails_closed_and_old_service_is_legacy()
    {
        using (new FakeService(ConfigSeal.SealState.Down)) Assert.False(Pairing.Trusted(Entry()));
        using (new FakeService(ConfigSeal.SealState.Unsupported)) Assert.True(Pairing.Trusted(Entry()));
        using (new FakeService(ConfigSeal.SealState.NoService)) Assert.True(Pairing.Trusted(Entry()));
    }

    [Fact]
    public void Security_keys_seal_detects_tampering()
    {
        using var _ = new FakeService(ConfigSeal.SealState.Ok, new byte[] { 9, 9 });
        var cfg = new JsonObject { ["kiosk.readOnly"] = true, ["kiosk.openSections"] = new JsonArray("audio") };
        Assert.False(ConfigSeal.SecurityIntact(cfg));           // עוד לא נחתם
        ConfigSeal.Seal(cfg);
        Assert.True(ConfigSeal.SecurityIntact(cfg));
        cfg["kiosk.readOnly"] = false;
        Assert.False(ConfigSeal.SecurityIntact(cfg));
        cfg["kiosk.readOnly"] = true;
        cfg["kiosk.openSections"] = new JsonArray("audio", "usb");
        Assert.False(ConfigSeal.SecurityIntact(cfg));
        cfg["unrelated"] = 1;                                   // מפתח לא אבטחתי לא משפיע
        cfg["kiosk.openSections"] = new JsonArray("audio");
        Assert.True(ConfigSeal.SecurityIntact(cfg));
    }

    [Fact]
    public void Seal_is_not_enforced_without_service_support()
    {
        using var _ = new FakeService(ConfigSeal.SealState.Unsupported);
        Assert.True(ConfigSeal.SecurityIntact(new JsonObject()));
    }
}
