using System.Security.Cryptography;
using System.Text;
using OtzariaBridge;
using Xunit;

namespace OtzariaBridge.Tests;

/// <summary>עדכון מקומי (בלי גיטהאב): סיווג הקבצים והאימות. המסלול זהה ל-VerifyPackage עם התגית החתומה מהקובץ עצמו.</summary>
public class LocalUpdateTests
{
    private static readonly ECDsa Key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private static readonly string Pub = Convert.ToBase64String(Key.ExportSubjectPublicKeyInfo());
    private static string Hex(byte[] d) => Convert.ToHexString(SHA256.HashData(d)).ToLowerInvariant();
    private static byte[] Pe() { var b = new byte[512]; b[0] = (byte)'M'; b[1] = (byte)'Z'; b[0x3C] = 0x80; b[0x80] = (byte)'P'; b[0x81] = (byte)'E'; return b; }

    private static (byte[] Sums, string Sig, byte[] Bv, byte[] Exe) Pkg(string ver = "4.0.0", string head = "# tag=v4.0.0\r\n# keyver=1\r\n", ECDsa signer = null, byte[] exe = null)
    {
        exe ??= Pe();
        var bv = Encoding.ASCII.GetBytes(ver);
        var sums = Encoding.ASCII.GetBytes(head + Hex(exe) + "  OtzariaBridge.exe\r\n" + Hex(bv) + "  BRIDGE_VERSION.txt\r\n");
        return (sums, Convert.ToBase64String((signer ?? Key).SignData(sums, HashAlgorithmName.SHA256)), bv, exe);
    }

    // כמו InstallLocal: התגית נלקחת מהקובץ החתום
    private static (Version, string) Local((byte[] Sums, string Sig, byte[] Bv, byte[] Exe) p, string cur = "3.6.1", string pub = null, byte[] exe = null)
    {
        string tag = UpdateApi.SignedTag(Encoding.UTF8.GetString(p.Sums));
        return UpdateApi.VerifyPackage(p.Sums, p.Sig, tag, p.Bv, exe ?? p.Exe, cur, pub ?? Pub);
    }

    [Fact]
    public void Classify_finds_all_four_even_with_browser_renames()
    {
        var f = UpdateApi.ClassifyLocalFiles(new[] { @"C:\d\OtzariaBridge (1).exe", @"C:\d\SHA256SUMS (2).txt", @"C:\d\SHA256SUMS.txt.sig", @"C:\d\BRIDGE_VERSION.txt" });
        Assert.NotNull(f.Exe); Assert.NotNull(f.Sums); Assert.NotNull(f.Sig); Assert.NotNull(f.BridgeVer);
        Assert.EndsWith(".sig", f.Sig);
    }

    [Fact]
    public void Classify_ignores_setup_and_unknown()
    {
        var f = UpdateApi.ClassifyLocalFiles(new[] { @"C:\d\OtzariaBridge-Setup-1.0.0.exe", @"C:\d\evil.exe", @"C:\d\notes.txt" });
        Assert.Null(f.Exe); Assert.Null(f.Sums); Assert.Null(f.Sig); Assert.Null(f.BridgeVer);
        Assert.Null(UpdateApi.ClassifyLocalFiles(null).Exe);
    }

    [Fact] public void Valid_local_package_passes() { var p = Pkg(); var (v, h) = Local(p); Assert.Equal(new Version(4, 0, 0), v); Assert.Equal(Hex(p.Exe), h); }

    [Fact]
    public void Wrong_signer_rejected() => Assert.Throws<BridgeException>(() => Local(Pkg(signer: ECDsa.Create(ECCurve.NamedCurves.nistP256))));

    [Fact]
    public void Missing_or_garbage_signature_rejected()
    {
        var p = Pkg();
        Assert.Throws<BridgeException>(() => Local((p.Sums, "", p.Bv, p.Exe)));
        Assert.Throws<BridgeException>(() => Local((p.Sums, "not-base64!!", p.Bv, p.Exe)));
    }

    [Fact] public void No_key_configured_rejected() => Assert.Throws<BridgeException>(() => Local(Pkg(), pub: ""));
    [Fact] public void Missing_keyver_rejected() => Assert.Throws<BridgeException>(() => Local(Pkg(head: "# tag=v4.0.0\r\n")));
    [Fact] public void Downgrade_and_same_version_rejected()
    {
        Assert.Throws<BridgeException>(() => Local(Pkg(ver: "3.0.0"), cur: "3.6.1"));
        Assert.Throws<BridgeException>(() => Local(Pkg(ver: "3.6.1"), cur: "3.6.1"));
    }
    [Fact] public void Tampered_exe_rejected() { var e = Pe(); e[300] = 7; Assert.Throws<BridgeException>(() => Local(Pkg(), exe: e)); }
    [Fact] public void Non_pe_exe_rejected() { var e = new byte[512]; Assert.Throws<BridgeException>(() => Local(Pkg(exe: e))); }
    [Fact] public void Signed_sums_without_tag_has_null_tag() => Assert.Null(UpdateApi.SignedTag(Encoding.UTF8.GetString(Pkg(head: "# keyver=1\r\n").Sums)));
}
