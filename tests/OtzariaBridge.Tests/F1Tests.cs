using System.Text;
using OtzariaBridge;
using Xunit;

namespace OtzariaBridge.Tests;

/// <summary>F1: הקשחת SnipApi / PdfApi / PdfAssoc / PdfOps / OneOcrApi. WHY בכל בדיקה: העקיפה שנמצאה בסקירה.</summary>
public class F1Tests
{
    // ---- SnipApi: סקריפט מתיקיית משתמש לא רץ בנעילה, ובלי Bypass/-File ----
    [Fact]
    public void Snip_override_is_ignored_when_locked_down() =>
        Assert.Equal("EMBED", SnipApi.PickScript(true, "evil", "EMBED"));

    [Fact]
    public void Snip_override_used_when_not_locked_and_blank_falls_back()
    {
        Assert.Equal("custom", SnipApi.PickScript(false, "custom", "EMBED"));
        Assert.Equal("EMBED", SnipApi.PickScript(false, "  ", "EMBED"));
        Assert.Equal("EMBED", SnipApi.PickScript(false, null, "EMBED"));
    }

    [Fact]
    public void Snip_command_is_encoded_with_quoted_params()
    {
        string enc = SnipApi.BuildEncodedCommand("param($Out)", @"C:\a'b\o.json", 25, true);
        string cmd = Encoding.Unicode.GetString(Convert.FromBase64String(enc));
        Assert.Contains(@"-Out 'C:\a''b\o.json'", cmd);
        Assert.Contains("-TimeoutSec 25 -Dump", cmd);
        Assert.DoesNotContain("-File", cmd);
    }

    // ---- PdfApi: סיומת עם נקודה, ו-Find לא סומך על מיקום כתיב ----
    [Theory]
    [InlineData(".pdf", true)]
    [InlineData("PDF", true)]
    [InlineData("docx", false)]
    public void Pdf_ext_list_accepts_leading_dot(string ext, bool expected) =>
        Assert.Equal(expected, PdfApi.PdfAllowedInList(new[] { ext }));

    // ---- PdfAssoc: --open-pdf מקבל רק pdf קיים ----
    [Fact]
    public void Inbox_rejects_non_pdf_missing_ads_and_relative()
    {
        string dir = Path.Combine(Path.GetTempPath(), "f1t-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string txt = Path.Combine(dir, "a.txt"); File.WriteAllText(txt, "x");
            Assert.False(PdfAssoc.InboxPathOk(txt));
            Assert.False(PdfAssoc.InboxPathOk(Path.Combine(dir, "missing.pdf")));
            Assert.False(PdfAssoc.InboxPathOk(txt + ":s.pdf"));
            Assert.False(PdfAssoc.InboxPathOk(@"a.pdf"));
            Assert.False(PdfAssoc.InboxPathOk(null));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // ---- PdfOps: מנוע במיקום כתיב (temp של המשתמש) לא מהימן ----
    [Fact]
    public void Pdf_engine_in_user_writable_dir_is_untrusted()
    {
        string f = Path.Combine(Path.GetTempPath(), "gswin64c-" + Guid.NewGuid().ToString("N") + ".exe");
        File.WriteAllText(f, "x");
        try
        {
            Assert.False(PdfOps.TrustedEngine(f));
            Assert.False(PdfOps.TrustedEngine(null));
        }
        finally { File.Delete(f); }
    }

    // ---- OneOcr: טעינה רק ממיקום מנהל, או חתימת Microsoft לכל ה-DLL ----
    [Fact]
    public void OneOcr_admin_location_requires_all_files()
    {
        var files = new[] { "a.dll", "b.dll" };
        Assert.True(OneOcrApi.AllAdminOnly("D", files, _ => true));
        Assert.False(OneOcrApi.AllAdminOnly("D", files, p => p.EndsWith("a.dll")));
        Assert.False(OneOcrApi.AllAdminOnly(null, files, _ => true));
    }

    [Fact]
    public void OneOcr_requires_microsoft_signature_on_every_dll()
    {
        var ms = new AppTrust.SigInfo(true, "CN=Microsoft Corporation, O=Microsoft Corporation, C=US");
        var other = new AppTrust.SigInfo(true, "CN=Evil, O=Evil Inc");
        Assert.True(OneOcrApi.DllsMicrosoftSigned(new[] { "a", "b" }, _ => ms));
        Assert.False(OneOcrApi.DllsMicrosoftSigned(new[] { "a", "b" }, p => p == "a" ? ms : other));
        Assert.False(OneOcrApi.DllsMicrosoftSigned(new[] { "a" }, _ => new AppTrust.SigInfo(false, ms.Subject)));
        Assert.False(OneOcrApi.DllsMicrosoftSigned(new[] { "a" }, _ => AppTrust.SigInfo.None));
    }
}
