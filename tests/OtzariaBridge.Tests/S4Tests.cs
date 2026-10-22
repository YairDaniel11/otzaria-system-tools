using System.Text;
using OtzariaBridge;
using Xunit;

namespace OtzariaBridge.Tests;

/// <summary>[s4] שרשרת אספקה: allowlist הורדה, keyver מפורש, מונוטוניות גרסה בהעתקה מוגברת</summary>
public class S4Tests
{
    private const string Dl = "https://github.com/" + UpdateApi.Repo + "/releases/download/v1.0.0/";

    [Theory]
    [InlineData(Dl + "OtzariaBridge.exe", true)]
    [InlineData("https://objects.githubusercontent.com/x/y", true)]
    [InlineData("https://release-assets.githubusercontent.com/x", true)]
    [InlineData("http://github.com/" + UpdateApi.Repo + "/releases/download/v1/a", false)]      // לא https
    [InlineData("https://github.com/evil/repo/releases/download/v1/a", false)]                  // ריפו אחר
    [InlineData("https://evil.example.com/OtzariaBridge.exe", false)]
    [InlineData("https://github.com.evil.com/" + UpdateApi.Repo + "/releases/download/v1/a", false)]
    [InlineData("https://user@objects.githubusercontent.com/x", false)]
    [InlineData("https://objects.githubusercontent.com:8443/x", false)]
    [InlineData("file:///c:/x.exe", false)]
    [InlineData("", false)]
    public void Download_allowlist(string url, bool ok) => Assert.Equal(ok, UpdateApi.IsAllowedDownloadUrl(url));

    [Fact]
    public void SelectRelease_drops_assets_from_foreign_hosts()
    {
        string json = "[{\"tag_name\":\"v1.0.0\",\"assets\":[" +
            "{\"name\":\"OtzariaBridge.exe\",\"browser_download_url\":\"https://evil.example.com/a.exe\"}," +
            "{\"name\":\"SHA256SUMS.txt\",\"browser_download_url\":\"" + Dl + "SHA256SUMS.txt\"}]}]";
        var r = UpdateApi.SelectRelease(json);
        Assert.Null(r.ExeUrl);
        Assert.NotNull(r.SumsUrl);
    }

    [Fact]
    public void Missing_keyver_is_zero_and_below_minimum()
    {
        Assert.Equal(0, UpdateApi.SignedKeyVersion("# tag=v1.0.0\n"));
        Assert.True(UpdateApi.SignedKeyVersion("# tag=v1.0.0\n") < UpdateApi.MinKeyVersion);
        Assert.Equal(1, UpdateApi.SignedKeyVersion("# tag=v1.0.0\n# keyver=1\n"));
    }

    [Fact]
    public void Download_caps_are_sane()
    {
        Assert.True(UpdateApi.MaxSmallDownload <= 1 << 20);
        Assert.True(UpdateApi.MaxExeDownload >= 100L << 20 && UpdateApi.MaxExeDownload <= 1L << 30);
    }

    [Fact]
    public void Downgrade_detection_uses_file_version()
    {
        string dir = Path.Combine(Path.GetTempPath(), "s4-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string dst = Path.Combine(dir, "dst.exe"), missing = Path.Combine(dir, "none.exe");
            // יעד שאינו קיים: לא נחשב הורדת גרסה
            Assert.False(InstallTrust.IsDowngrade(Environment.ProcessPath!, missing));
            // מקור בלי גרסה קריאה מול יעד עם גרסה: fail-closed
            string noVer = Path.Combine(dir, "src.txt"); File.WriteAllText(noVer, "x");
            File.Copy(Environment.ProcessPath!, dst);
            var dv = InstallTrust.FileVer(dst);
            if (dv != null) Assert.True(InstallTrust.IsDowngrade(noVer, dst));
            // אותו קובץ/גרסה שווה: מותר
            Assert.False(InstallTrust.IsDowngrade(Environment.ProcessPath!, dst));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void CopyVerified_refuses_downgrade_unless_explicit()
    {
        string dir = Path.Combine(Path.GetTempPath(), "s4-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string dst = Path.Combine(dir, "dst.exe"), src = Path.Combine(dir, "src.exe");
            File.Copy(Environment.ProcessPath!, dst);
            File.WriteAllBytes(src, Encoding.ASCII.GetBytes("not an exe"));
            string hash = InstallTrust.Sha256File(src);
            if (InstallTrust.FileVer(dst) == null) return; // אין גרסה לבדיקה בסביבה הזו
            Assert.NotNull(InstallTrust.CopyVerified(src, dst, hash));
            Assert.Null(InstallTrust.CopyVerified(src, dst, hash, allowDowngrade: true));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
