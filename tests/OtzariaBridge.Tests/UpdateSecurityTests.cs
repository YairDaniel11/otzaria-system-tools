using System.Security.Cryptography;
using System.Text;
using OtzariaBridge;
using Xunit;

namespace OtzariaBridge.Tests;

/// <summary>
/// שרשרת האמון של העדכון (חתימה, תגית חתומה, downgrade, PE) ובחירת ההוצאה. הכול טהור: בלי רשת ובלי דיסק.
/// המפתחות נוצרים בזמן הבדיקה (זמניים), לא נשמר שום מפתח בריפו.
/// </summary>
public class UpdateSecurityTests
{
    private static readonly ECDsa Key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private static readonly string Pub = Convert.ToBase64String(Key.ExportSubjectPublicKeyInfo());
    private static string Sign(byte[] d, ECDsa k = null) => Convert.ToBase64String((k ?? Key).SignData(d, HashAlgorithmName.SHA256));
    private static string Hex(byte[] d) => Convert.ToHexString(SHA256.HashData(d)).ToLowerInvariant();

    private static byte[] FakePe(int fill = 0)
    {
        var b = new byte[512];
        b[0] = (byte)'M'; b[1] = (byte)'Z'; b[0x3C] = 0x80;
        b[0x80] = (byte)'P'; b[0x81] = (byte)'E';
        b[300] = (byte)fill;
        return b;
    }

    private record Pkg(byte[] Sums, string Sig, byte[] Bv, byte[] Exe);

    private static Pkg Make(string tag = "v0.1.5", string ver = "4.0.0", byte[] exe = null, ECDsa signer = null, string sumsOverride = null)
    {
        exe ??= FakePe();
        var bv = Encoding.ASCII.GetBytes(ver);
        string sums = sumsOverride ?? $"# tag={tag}\r\n{Hex(exe)}  OtzariaBridge.exe\r\n{Hex(bv)}  BRIDGE_VERSION.txt\r\n";
        var sb = Encoding.ASCII.GetBytes(sums);
        return new Pkg(sb, Sign(sb, signer), bv, exe);
    }

    private static (Version Version, string Sha256) Verify(Pkg p, string tag = "v0.1.5", string cur = "3.6.1", string pub = null, string sig = null, byte[] sums = null, byte[] exe = null)
        => UpdateApi.VerifyPackage(sums ?? p.Sums, sig ?? p.Sig, tag, p.Bv, exe ?? p.Exe, cur, pub ?? Pub);

    [Fact]
    public void Valid_package_passes()
    {
        var p = Make();
        var (v, h) = Verify(p);
        Assert.Equal(new Version(4, 0, 0), v);
        Assert.Equal(Hex(p.Exe), h);
    }

    [Fact]
    public void Tampered_sums_is_rejected()
    {
        var p = Make();
        var bad = (byte[])p.Sums.Clone();
        bad[bad.Length - 5] ^= 1; // שינוי בבית אחד: החתימה כבר לא תקפה
        Assert.Throws<BridgeException>(() => Verify(p, sums: bad));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-base64!!")]
    [InlineData("AAAA")]
    public void Missing_or_garbage_signature_is_rejected(string sig)
    {
        var p = Make();
        Assert.Throws<BridgeException>(() => UpdateApi.VerifyPackage(p.Sums, sig, "v0.1.5", p.Bv, p.Exe, "3.6.1", Pub));
        Assert.Throws<BridgeException>(() => UpdateApi.VerifyPackage(p.Sums, null, "v0.1.5", p.Bv, p.Exe, "3.6.1", Pub));
    }

    [Fact]
    public void Signature_from_other_key_is_rejected()
    {
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var p = Make(signer: other);
        Assert.Throws<BridgeException>(() => Verify(p));
    }

    [Fact]
    public void Empty_embedded_key_fails_closed()
    {
        // ברירת המחדל בקוד (מפתח לא הוגדר) חייבת לדחות הכול, גם חתימה "תקפה"
        var p = Make();
        Assert.True(UpdateApi.PublicKeySpkiBase64 == "" || UpdateApi.PublicKeySpkiBase64.Length > 50);
        Assert.Throws<BridgeException>(() => Verify(p, pub: ""));
    }

    [Fact]
    public void Wrong_curve_is_rejected()
    {
        using var p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        var p = Make(signer: p384);
        Assert.Throws<BridgeException>(() => Verify(p, pub: Convert.ToBase64String(p384.ExportSubjectPublicKeyInfo())));
    }

    [Fact]
    public void Signed_tag_must_match_release_tag()
    {
        var p = Make(tag: "v0.1.4"); // חתום כהוצאה אחרת (ערבוב נכסים)
        Assert.Throws<BridgeException>(() => Verify(p, tag: "v0.1.5"));
    }

    [Theory]
    [InlineData("3.6.1")] // זהה
    [InlineData("3.6.0")] // downgrade
    [InlineData("2.9.9")]
    public void Downgrade_or_same_version_is_rejected(string signedVer)
    {
        var p = Make(ver: signedVer);
        var ex = Assert.Throws<BridgeException>(() => Verify(p, cur: "3.6.1"));
        Assert.Contains("גבוהה", ex.Message);
    }

    [Fact]
    public void Newer_version_passes()
    {
        Assert.Equal(new Version(3, 6, 2), Verify(Make(ver: "3.6.2")).Version);
    }

    [Fact]
    public void Exe_that_differs_from_signed_hash_is_rejected()
    {
        var p = Make();
        Assert.Throws<BridgeException>(() => Verify(p, exe: FakePe(fill: 9)));
    }

    [Fact]
    public void Non_pe_exe_is_rejected_even_if_hash_is_signed()
    {
        var notPe = Encoding.ASCII.GetBytes("#!/bin/sh\necho pwned\n");
        var p = Make(exe: notPe);
        var ex = Assert.Throws<BridgeException>(() => Verify(p));
        Assert.Contains("הרצה", ex.Message);
    }

    [Fact]
    public void Missing_exe_line_in_signed_sums_is_rejected()
    {
        var bv = Encoding.ASCII.GetBytes("4.0.0");
        var p = Make(sumsOverride: $"# tag=v0.1.5\r\n{Hex(bv)}  BRIDGE_VERSION.txt\r\n");
        Assert.Throws<BridgeException>(() => Verify(p));
    }

    [Fact]
    public void Bridge_version_file_must_match_signed_hash()
    {
        var p = Make(ver: "4.0.0");
        var swapped = Encoding.ASCII.GetBytes("9.9.9");
        Assert.Throws<BridgeException>(() => UpdateApi.VerifyPackage(p.Sums, p.Sig, "v0.1.5", swapped, p.Exe, "3.6.1", Pub));
    }

    [Fact]
    public void IsPe_checks_mz_and_pe_signature()
    {
        Assert.True(UpdateApi.IsPe(FakePe()));
        Assert.False(UpdateApi.IsPe(null));
        Assert.False(UpdateApi.IsPe(new byte[10]));
        var noPe = FakePe(); noPe[0x80] = 0;
        Assert.False(UpdateApi.IsPe(noPe));
        var badOffset = FakePe(); badOffset[0x3C] = 0xFF; badOffset[0x3D] = 0xFF;
        Assert.False(UpdateApi.IsPe(badOffset));
    }

    [Fact]
    public void FileVersionMatches_compares_real_file_version()
    {
        // קובץ אמיתי עם FileVersion ידוע: האסמבלי של הגשר (הבדיקות מפנות אליו)
        string path = typeof(UpdateApi).Assembly.Location;
        var actual = UpdateApi.Parse(System.Diagnostics.FileVersionInfo.GetVersionInfo(path).FileVersion);
        Assert.NotNull(actual);
        Assert.True(UpdateApi.FileVersionMatches(path, actual));
        Assert.False(UpdateApi.FileVersionMatches(path, new Version(actual.Major + 1, 0, 0)));
        Assert.False(UpdateApi.FileVersionMatches(path + ".missing", actual));
    }

    // ---- בחירת ההוצאה (באג isLatest) ----

    private static string Rel(string tag, bool draft = false, bool pre = false, bool withAssets = true)
        => "{\"tag_name\":\"" + tag + "\",\"draft\":" + (draft ? "true" : "false") + ",\"prerelease\":" + (pre ? "true" : "false") +
           ",\"html_url\":\"https://x/" + tag + "\",\"body\":\"n\",\"assets\":" +
           (withAssets ? "[{\"name\":\"OtzariaBridge.exe\",\"browser_download_url\":\"https://x/" + tag + "/exe\"},{\"name\":\"SHA256SUMS.txt\",\"browser_download_url\":\"https://x/" + tag + "/sums\"},{\"name\":\"SHA256SUMS.txt.sig\",\"browser_download_url\":\"https://x/" + tag + "/sig\"},{\"name\":\"BRIDGE_VERSION.txt\",\"browser_download_url\":\"https://x/" + tag + "/bv\"}]" : "[]") + "}";

    [Fact]
    public void SelectRelease_picks_highest_tag_regardless_of_order_and_has_no_latest_field()
    {
        // אין שדה "latest" ב-API של GitHub; הסדר ברשימה לא מובטח
        var r = UpdateApi.SelectRelease("[" + Rel("v0.1.3") + "," + Rel("v0.1.10") + "," + Rel("v0.1.9") + "]");
        Assert.Equal("v0.1.10", r.Tag);
        Assert.Equal("https://x/v0.1.10/sig", r.SigUrl);
        Assert.Equal("https://x/v0.1.10/bv", r.BridgeVerUrl);
    }

    [Fact]
    public void SelectRelease_skips_drafts_and_prereleases()
    {
        var r = UpdateApi.SelectRelease("[" + Rel("v9.0.0", draft: true) + "," + Rel("v8.0.0", pre: true) + "," + Rel("v0.1.2") + "]");
        Assert.Equal("v0.1.2", r.Tag);
    }

    [Fact]
    public void SelectRelease_skips_non_version_tags_and_handles_empty()
    {
        Assert.Null(UpdateApi.SelectRelease("[]"));
        Assert.Null(UpdateApi.SelectRelease("{}"));
        Assert.Equal("v1.0.0", UpdateApi.SelectRelease("[" + Rel("nightly") + "," + Rel("v1.0.0") + "]").Tag);
    }

    [Fact]
    public void SelectRelease_release_without_assets_has_null_urls()
    {
        var r = UpdateApi.SelectRelease("[" + Rel("v1.0.0", withAssets: false) + "]");
        Assert.Null(r.SigUrl);
        Assert.Null(r.ExeUrl);
    }

    [Fact]
    public void SignedTag_and_FindHash_parse_manifest()
    {
        string h = new string('a', 64);
        string s = "# tag=v1.2.3\r\n" + h + "  OtzariaBridge.exe\r\n" + h.ToUpper() + " *Other.exe\r\n";
        Assert.Equal("v1.2.3", UpdateApi.SignedTag(s));
        Assert.Equal(h, UpdateApi.FindHash(s, "OtzariaBridge.exe"));
        Assert.Equal(h, UpdateApi.FindHash(s, "other.exe"));
        Assert.Null(UpdateApi.FindHash(s, "missing.exe"));
        Assert.Null(UpdateApi.SignedTag("no tag here"));
    }
}
