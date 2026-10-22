using OtzariaBridge;
using Xunit;

namespace OtzariaBridge.Tests;

/// <summary>סבב 4: אב של סודות/מוגן, Unzip, UNC, סיומות (רשימה לבנה)</summary>
public class FsRound4Tests
{
    private static string Tmp()
    {
        string d = Path.Combine(Path.GetTempPath(), "r4-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        return d;
    }

    // ---- 3: אב של מוגן ----
    [Fact]
    public void Tree_ops_block_ancestors_of_protected()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        Assert.Throws<BridgeException>(() => FsApi.EnsureNotProtected(local, tree: true));
        Assert.Throws<BridgeException>(() => FsApi.EnsureNotProtected(Path.Combine(roaming, "Microsoft", "Windows"), tree: true));
        Assert.Throws<BridgeException>(() => FsApi.EnsureNotProtected(Path.Combine(roaming, "Microsoft"), tree: true));
    }

    [Fact]
    public void Creating_inside_an_ancestor_stays_allowed()
    {
        // יצירה/כתיבה בתיקיית אב (ללא tree) לא נחסמת: אחרת אי אפשר לשמור קובץ בתיקיית הבית
        string d = Tmp();
        try { FsApi.EnsureNotProtected(Path.Combine(d, "a.txt")); }
        finally { Directory.Delete(d, true); }
    }

    // ---- 1: סודות דרך אב ----
    [Fact]
    public void CopyDir_style_check_flags_secret_items_under_ancestor()
    {
        var secret = FsApi.SecretRoots();
        Assert.True(FsApi.IsSecretItem(Config.FilePath, secret));
        Assert.True(FsApi.IsSecretItem(Config.Dir, secret));
        Assert.False(FsApi.IsSecretItem(Path.Combine(Path.GetTempPath(), "nothing.txt"), secret));
    }

    [Fact]
    public void Ancestor_helper_detects_parent_of_root()
    {
        Assert.True(FsGuard.IsAncestorOfAny(Path.GetDirectoryName(Config.Dir)!, new[] { Config.Dir }));
        Assert.False(FsGuard.IsAncestorOfAny(Config.Dir, new[] { Config.Dir }));
    }

    [Theory]
    [InlineData("\\\\localhost\\c$\\x", false, true)]
    [InlineData("\\\\127.0.0.1\\c$\\x", false, true)]
    [InlineData("\\\\[::1]\\c$\\x", false, true)]
    [InlineData("\\\\0--1.ipv6-literal.net\\c$", false, true)]
    [InlineData("\\\\server\\share\\x", false, false)]
    [InlineData("\\\\server\\share\\x", true, true)]
    [InlineData("C:\\x\\y", true, false)]
    public void Unc_blocking(string p, bool strict, bool blocked) => Assert.Equal(blocked, FsGuard.IsBlockedUnc(p, strict, "MYPC"));

    [Fact]
    public void Unc_machine_name_is_blocked() => Assert.True(FsGuard.IsBlockedUnc("\\\\mypc\\c$\\x", false, "MYPC"));

    [Fact]
    public void EnsureNotReadable_rejects_loopback_unc() =>
        Assert.Throws<BridgeException>(() => FsApi.EnsureNotReadable("\\\\localhost\\c$\\Windows"));

    // ---- 2: Unzip ----
    [Theory]
    [InlineData("a/b.txt", true)] [InlineData("a\\b.txt", true)] [InlineData("../x.txt", false)] [InlineData("a/../../x", false)]
    [InlineData("/abs.txt", false)] [InlineData("C:\\x.txt", false)] [InlineData("a.txt:evil.exe", false)] [InlineData("\\\\srv\\s\\x", false)]
    public void Zip_entry_names(string n, bool safe) => Assert.Equal(safe, PackB.IsSafeEntryName(n));

    [Fact]
    public void Unzip_rejects_whole_archive_on_unsafe_entry()
    {
        string d = Tmp();
        try { Assert.Throws<BridgeException>(() => PackB.CheckZipEntries(new[] { "ok.txt", "../evil.txt" }, d)); }
        finally { Directory.Delete(d, true); }
    }

    [Fact]
    public void Unzip_to_ancestor_of_startup_is_rejected_per_entry()
    {
        // פריסה ל-Roaming: הרשומה "Microsoft\Windows\Start Menu\Programs\Startup\x.txt" מגיעה ל-Startup
        string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        Assert.Throws<BridgeException>(() => PackB.CheckZipEntries(new[] { "Microsoft/Windows/Start Menu/Programs/Startup/x.txt" }, roaming));
    }

    [Fact]
    public void Unzip_to_config_dir_ancestor_hits_secret()
    {
        string parent = Path.GetDirectoryName(Config.Dir)!;
        string rel = Path.GetRelativePath(parent, Config.FilePath).Replace('\\', '/');
        Assert.Throws<BridgeException>(() => PackB.CheckZipEntries(new[] { rel }, parent));
    }

    [Fact]
    public void Unzip_normal_entries_pass()
    {
        string d = Tmp();
        try { PackB.CheckZipEntries(new[] { "a/", "a/b.txt", "c.pdf" }, d); }
        finally { Directory.Delete(d, true); }
    }

    // ---- 4: סיומות ----
    [Theory]
    [InlineData("a.com")] [InlineData("a.pif")] [InlineData("a.vbe")] [InlineData("a.wsh")] [InlineData("a.ws")] [InlineData("a.msc")]
    [InlineData("a.jar")] [InlineData("a.msp")] [InlineData("a.application")] [InlineData("a.chm")] [InlineData("a.inf")] [InlineData("a.sct")]
    [InlineData("a.scf")] [InlineData("a.xll")] [InlineData("a.vsto")] [InlineData("a.appref-ms")] [InlineData("a.diagcab")] [InlineData("a.py")]
    [InlineData("a.ps1xml")] [InlineData("a.psm1")] [InlineData("a.wsc")] [InlineData("a.gadget")]
    public void New_dangerous_extensions(string n) => Assert.True(FsGuard.IsDangerousExt("C:\\x\\" + n));

    [Theory]
    [InlineData("a.txt")] [InlineData("a.docx")] [InlineData("a.PDF")] [InlineData("a.png")] [InlineData("a.zip")] [InlineData("README")] [InlineData("a.json")]
    public void Legit_files_are_writable(string n) => Assert.False(FsGuard.IsWriteBlockedExt("C:\\x\\" + n));

    [Theory]
    [InlineData("a.appinstaller")] [InlineData("a.docm")] [InlineData("a.exe.")] [InlineData("a.unknownext")] [InlineData("a.xyz")]
    public void Unknown_or_dangerous_are_blocked_for_write(string n) => Assert.True(FsGuard.IsWriteBlockedExt("C:\\x\\" + n));
}
