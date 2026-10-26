using System.Text.Json;
using OtzariaBridge;
using Xunit;

namespace OtzariaBridge.Tests;

/// <summary>סקירת איכות ב': WriteBinary באותה מדיניות כמו WriteText, ועוד.</summary>
public class QaBTests
{
    private static string Tmp()
    {
        string d = Path.Combine(Path.GetTempPath(), "qab-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        return d;
    }
    private static JsonElement Body(object o) => JsonSerializer.SerializeToElement(o);
    private static string B64(string s) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(s));

    [Fact]
    public void WriteBinary_writes_then_appends()
    {
        string d = Tmp();
        try
        {
            string p = Path.Combine(d, "a.txt");
            FsApi.WriteBinary(Body(new { path = p, content = B64("ab") }));
            FsApi.WriteBinary(Body(new { path = p, content = B64("cd"), append = true }));
            Assert.Equal("abcd", File.ReadAllText(p));
        }
        finally { Directory.Delete(d, true); }
    }

    [Fact]
    public void WriteBinary_refuses_existing_without_overwrite_and_overwrites_with_it()
    {
        string d = Tmp();
        try
        {
            string p = Path.Combine(d, "a.txt");
            File.WriteAllText(p, "old-long-content");
            Assert.Throws<BridgeException>(() => FsApi.WriteBinary(Body(new { path = p, content = B64("x") })));
            FsApi.WriteBinary(Body(new { path = p, content = B64("x"), overwrite = true }));
            Assert.Equal("x", File.ReadAllText(p));   // נקטם
        }
        finally { Directory.Delete(d, true); }
    }

    [Fact]
    public void WriteBinary_unique_picks_new_name()
    {
        string d = Tmp();
        try
        {
            string p = Path.Combine(d, "a.txt");
            File.WriteAllText(p, "old");
            FsApi.WriteBinary(Body(new { path = p, content = B64("n"), unique = true }));
            Assert.Equal("old", File.ReadAllText(p));
            Assert.Equal("n", File.ReadAllText(Path.Combine(d, "a (2).txt")));
        }
        finally { Directory.Delete(d, true); }
    }

    [Fact]
    public void WriteBinary_append_to_missing_file_fails()
    {
        string d = Tmp();
        try { Assert.Throws<BridgeException>(() => FsApi.WriteBinary(Body(new { path = Path.Combine(d, "no.txt"), content = B64("x"), append = true }))); }
        finally { Directory.Delete(d, true); }
    }

    [Fact]
    public void WriteBinary_blocks_protected_paths()
    {
        string p = Path.Combine(Config.Dir, "evil.bin");
        Assert.Throws<BridgeException>(() => FsApi.WriteBinary(Body(new { path = p, content = B64("x") })));
        Assert.False(File.Exists(p));
    }
}

public class QaBTrustTests
{
    [Fact]
    public void SelfSigned_cert_does_not_chain_to_machine_root()
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var req = new System.Security.Cryptography.X509Certificates.CertificateRequest("O=Microsoft Corporation, CN=fake", rsa,
            System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        Assert.False(AppTrust.ChainsToMachineRoot(cert));
    }

    [Theory]
    [InlineData(@"C:\Windows\System32\kernel32.dll")]   // קטלוג
    [InlineData(@"C:\Windows\explorer.exe")]
    public void Real_microsoft_files_still_verify(string path)
    {
        if (!File.Exists(path)) return;
        var s = AppTrust.GetSignature(path);
        Assert.True(s.Valid);
        Assert.True(AppTrust.IsMicrosoftSubject(s.Subject));
    }
}

public class QaBMiscTests
{
    [Theory]
    [InlineData("https://example.com/a", true)]
    [InlineData("mailto:a@b.c", true)]
    [InlineData("#bookmark", true)]
    [InlineData("other.docx", true)]
    [InlineData("ms-msdt:/id PCWDiagnostic", false)]
    [InlineData("search-ms:query=x", false)]
    [InlineData("ms-settings:network", false)]
    [InlineData("file:///C:/x.exe", false)]
    [InlineData("\\\\server\\share", false)]
    [InlineData("//server/share", false)]
    public void Hyperlink_target_scheme_is_checked(string target, bool safe) =>
        Assert.Equal(safe, FsGuard.IsSafeHyperlinkTarget(target));

    [Fact]
    public void Optional_sealed_keys_do_not_change_canonical_when_absent_but_do_when_present()
    {
        var cfg = new System.Text.Json.Nodes.JsonObject { ["kiosk.readOnly"] = true };
        string before = ConfigSeal.Canonical(cfg);
        Assert.DoesNotContain("fx.policy", before);
        cfg["fx.policy.mode"] = "off";
        Assert.NotEqual(before, ConfigSeal.Canonical(cfg));
    }
}

public class QaBZipTests
{
    [Fact]
    public void Zip_caps_reject_bomb_and_too_many_entries()
    {
        PackB.CheckZipSize(new[] { (1000L, 500L) });
        Assert.Throws<BridgeException>(() => PackB.CheckZipSize(new[] { (200L << 20, 10L) }));
        Assert.Throws<BridgeException>(() => PackB.CheckZipSize(new[] { (3L << 30, 3L << 29), (3L << 30, 3L << 29) }));
        Assert.Throws<BridgeException>(() => PackB.CheckZipSize(Enumerable.Repeat((1L, 1L), PackB.MaxUnzipEntries + 1)));
    }
}
