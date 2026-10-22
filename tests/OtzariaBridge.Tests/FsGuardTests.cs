using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using OtzariaBridge;
using Xunit;

namespace OtzariaBridge.Tests;

/// <summary>הקשחת מערכת הקבצים (W2): סודות, הרצה אוטומטית, סיומות מסוכנות, מסמכי Office, 8.3, hardlink, TOCTOU, הפעלה</summary>
public class FsGuardTests
{
    private static string Tmp()
    {
        string d = Path.Combine(Path.GetTempPath(), "fsg-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        return d;
    }

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

    [Theory]
    [InlineData(@"C:\a\x.exe", ".exe")]
    [InlineData(@"C:\a\x.BAT.", ".bat")]       // Windows מתעלם מנקודה/רווח בסוף
    [InlineData(@"C:\a\x.ps1 ", ".ps1")]
    [InlineData(@"C:\a\x", "")]
    [InlineData(@"C:\a\x.docx", ".docx")]
    public void StdExt_trims_trailing_dots_and_spaces(string p, string e) => Assert.Equal(e, FsGuard.StdExt(p));

    [Theory]
    [InlineData("a.exe")] [InlineData("a.bat")] [InlineData("a.cmd")] [InlineData("a.ps1")] [InlineData("a.vbs")] [InlineData("a.js")]
    [InlineData("a.jse")] [InlineData("a.wsf")] [InlineData("a.lnk")] [InlineData("a.dll")] [InlineData("a.reg")] [InlineData("a.scr")]
    [InlineData("a.msi")] [InlineData("a.hta")] [InlineData("a.cpl")] [InlineData("a.url")] [InlineData("a.EXE.")]
    public void Dangerous_extensions_are_flagged(string n) => Assert.True(FsGuard.IsDangerousExt(@"C:\x\" + n));

    [Fact]
    public void Plain_documents_are_not_dangerous() { Assert.False(FsGuard.IsDangerousExt(@"C:\x\a.docx")); Assert.False(FsGuard.IsDangerousExt(@"C:\x\a.pdf")); }

    [Theory]
    [InlineData("a.docm")] [InlineData("a.xlsm")] [InlineData("a.pptm")] [InlineData("a.DOTM")]
    public void Macro_extensions_are_flagged(string n) => Assert.True(FsGuard.IsMacroExt(@"C:\x\" + n));

    [Fact]
    public void AutoRun_roots_cover_startup_start_menu_tasks_programs()
    {
        var roots = FsGuard.AutoRunRoots();
        Assert.Contains(roots, r => r.EndsWith("Startup", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(roots, r => r.EndsWith(@"Start Menu", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(roots, r => r.EndsWith("Tasks", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(roots, r => r.EndsWith("Programs", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void EnsureNotProtected_blocks_startup_and_programs()
    {
        string startup = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        Assert.Throws<BridgeException>(() => FsApi.EnsureNotProtected(Path.Combine(startup, "evil.bat")));
        string programs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "x", "y.exe");
        Assert.Throws<BridgeException>(() => FsApi.EnsureNotProtected(programs));
    }

    [Fact]
    public void EnsureNotReadable_blocks_config_dir_even_for_read()
    {
        Assert.Throws<BridgeException>(() => FsApi.EnsureNotReadable(Config.FilePath));
        Assert.Throws<BridgeException>(() => FsApi.EnsureNotReadable(Config.Dir));
        Assert.Throws<BridgeException>(() => FsApi.EnsureNotReadable(Path.Combine(Config.Dir, "sub", "..", "config.json")));
        Assert.Throws<BridgeException>(() => FsApi.EnsureNotReadable(Path.Combine(HardLock.DataDir, "hardlock.json")));
    }

    [Fact]
    public void EnsureNotReadable_allows_ordinary_files()
    {
        string d = Tmp();
        try { FsApi.EnsureNotReadable(Path.Combine(d, "a.txt")); }
        finally { Directory.Delete(d, true); }
    }

    [Fact]
    public void ShortName_8dot3_is_expanded_before_check()
    {
        // C:\PROGRA~1 -> C:\Program Files (אם 8.3 מופעל בכונן; אחרת הנתיב נשאר כפי שהוא ובכל מקרה לא נשבר)
        string r = PathPolicy.LongName(@"C:\PROGRA~1");
        Assert.True(r.Equals(@"C:\Program Files", StringComparison.OrdinalIgnoreCase) || r.Equals(@"C:\PROGRA~1", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(@"C:\Windows\x", PathPolicy.LongName(@"C:\Windows\x"));
    }

    [Fact]
    public void ShortName_of_protected_secret_dir_is_blocked()
    {
        string dir = Config.Dir;
        Directory.CreateDirectory(dir);
        string probe = Path.Combine(dir, "config.json");
        string sn = PathPolicy.LongName(probe);
        Assert.Throws<BridgeException>(() => FsApi.EnsureNotReadable(sn));
    }

    [Fact]
    public void HardLinkCount_detects_second_name()
    {
        string d = Tmp();
        try
        {
            string a = Path.Combine(d, "a.txt"), b = Path.Combine(d, "b.txt");
            File.WriteAllText(a, "x");
            Assert.Equal(1, PathPolicy.HardLinkCount(a));
            var p = Process.Start(new ProcessStartInfo("cmd.exe", "/c mklink /H \"" + b + "\" \"" + a + "\"") { CreateNoWindow = true, UseShellExecute = false });
            p.WaitForExit();
            if (!File.Exists(b)) return;   // סביבה ללא הרשאה/NTFS: אין מה לבדוק
            Assert.Equal(2, PathPolicy.HardLinkCount(a));
            Assert.Equal(2, PathPolicy.HardLinkCount(b));
        }
        finally { Directory.Delete(d, true); }
    }

    [Fact]
    public void OpenVerifiedRead_checks_final_path_of_the_opened_handle()
    {
        string d = Tmp();
        try
        {
            string a = Path.Combine(d, "a.txt");
            File.WriteAllText(a, "hello");
            string seen = null;
            using (var fs = PathPolicy.OpenVerifiedRead(a, f => seen = f)) Assert.Equal(5, fs.Length);
            Assert.Equal(PathPolicy.Resolve(a), seen, ignoreCase: true);
            Assert.Throws<BridgeException>(() => PathPolicy.OpenVerifiedRead(a, _ => throw BridgeException.Forbidden("x")));
            // ה-handle נסגר אחרי דחייה: אפשר למחוק את הקובץ
            File.Delete(a);
        }
        finally { Directory.Delete(d, true); }
    }

    [Fact]
    public void Recycle_fake_I_pointing_to_startup_is_rejected()
    {
        string startup = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        Assert.Throws<BridgeException>(() => RecycleApi.CheckRestoreTarget(Path.Combine(startup, "evil.txt")));
        Assert.Throws<BridgeException>(() => RecycleApi.CheckRestoreTarget(Config.FilePath));
    }

    // ---------- מסמכי Office ----------

    [Fact]
    public void Clean_docx_passes()
    {
        var z = Zip(("word/document.xml", "<w:document><w:p><w:r><w:t>שלום</w:t></w:r></w:p></w:document>"),
            ("word/_rels/document.xml.rels", "<Relationships><Relationship Id=\"1\" Type=\"http://x/hyperlink\" Target=\"http://a\" TargetMode=\"External\"/></Relationships>"));
        Assert.Null(Inspect(".docx", z));
    }

    [Fact]
    public void Docx_with_vba_project_is_blocked() =>
        Assert.NotNull(Inspect(".docx", Zip(("word/document.xml", "<a/>"), ("word/vbaProject.bin", "x"))));

    [Fact]
    public void Docx_with_remote_template_is_blocked() =>
        Assert.NotNull(Inspect(".docx", Zip(("word/_rels/settings.xml.rels",
            "<Relationships><Relationship Id=\"1\" Type=\"http://x/attachedTemplate\" Target=\"http://evil/t.dotm\" TargetMode=\"External\"/></Relationships>"))));

    [Fact]
    public void Docx_with_dde_field_is_blocked() =>
        Assert.NotNull(Inspect(".docx", Zip(("word/document.xml", "<w:p><w:r><w:instrText> DDEAUTO c:\\\\windows\\\\system32\\\\cmd.exe \"/k calc\" </w:instrText></w:r></w:p>"))));

    [Fact]
    public void Xlsx_with_dde_formula_is_blocked() =>
        Assert.NotNull(Inspect(".xlsx", Zip(("xl/worksheets/sheet1.xml", "<worksheet><sheetData><row><c><f>cmd|' /c calc'!A1</f></c></row></sheetData></worksheet>"))));

    [Fact]
    public void Corrupt_zip_fails_closed() => Assert.NotNull(Inspect(".docx", new byte[] { 1, 2, 3, 4, 5 }));

    [Fact]
    public void Rtf_with_embedded_object_is_blocked()
    {
        Assert.NotNull(Inspect(".rtf", Encoding.ASCII.GetBytes(@"{\rtf1{\object\objemb{\*\objdata 0105}}}")));
        Assert.Null(Inspect(".rtf", Encoding.ASCII.GetBytes(@"{\rtf1 hello}")));
    }

    [Fact]
    public void Legacy_doc_with_macros_is_blocked()
    {
        var data = new List<byte>(new byte[512]);
        data.AddRange(Encoding.Unicode.GetBytes("_VBA_PROJECT"));
        Assert.NotNull(Inspect(".doc", data.ToArray()));
        Assert.Null(Inspect(".doc", new byte[2048]));
    }

    // ---------- הפעלה ----------

    [Theory]
    [InlineData("restart", true, false, true)]
    [InlineData("shutdown", true, false, true)]
    [InlineData("restart", true, true, false)]    // power נפתח במפורש
    [InlineData("restart", false, false, false)]  // אין נעילה
    [InlineData("sleep", true, false, false)]
    [InlineData("lock", true, false, false)]
    public void PowerNeedsAuth_matrix(string action, bool kiosk, bool powerOpen, bool expected) =>
        Assert.Equal(expected, FsGuard.PowerNeedsAuth(action, kiosk, powerOpen ? new[] { "audio", "power" } : new[] { "audio", "energy" }));

    [Fact]
    public void Fx_default_mode_is_restricted_when_locked() { Assert.Equal("restricted", FxPolicy.DefaultMode(true)); Assert.Equal("off", FxPolicy.DefaultMode(false)); }
}
