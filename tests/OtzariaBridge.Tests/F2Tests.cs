using System.Text.Json.Nodes;
using OtzariaBridge;
using Xunit;

namespace OtzariaBridge.Tests;

/// <summary>F2: הקשחת קודי גיבוי, ייבוא הגדרות חתומות, ומקלדת וירטואלית. WHY בכל בדיקה: הפרצה שנסגרה.</summary>
public class F2Tests
{
    // WHY: ייבוא קובץ זדוני לא מרחיב פתיחה ל-exe/bat/lnk גם עם סיסמה נכונה
    [Fact]
    public void Import_extensions_keep_only_safe_whitelist()
    {
        var v = SettingsIO.SanitizeSecurityValue("kiosk.allowOpenExtensions", new JsonArray("pdf", ".EXE", "bat", "lnk", "docx", "ps1", "html", "pdf"));
        var l = ((JsonArray)v).Select(x => x.GetValue<string>()).ToArray();
        Assert.Equal(new[] { "pdf", "docx" }, l);
    }

    // WHY: סקציות פתוחות רק מרשימת AllSections (לא ערכים שרירותיים)
    [Fact]
    public void Import_sections_filtered_to_known()
    {
        var v = (JsonArray)SettingsIO.SanitizeSecurityValue("kiosk.openSections", new JsonArray("audio", "evil", "WIFI"));
        Assert.Equal(new[] { "audio", "wifi" }, v.Select(x => x.GetValue<string>()).ToArray());
    }

    // WHY: סוג לא תקין/שעה לא תקינה => מדלגים (null), לא נכתב ערך שישבור את התזמון
    [Fact]
    public void Import_invalid_types_are_dropped()
    {
        Assert.Null(SettingsIO.SanitizeSecurityValue("kiosk.readOnly", JsonValue.Create("yes")));
        Assert.Null(SettingsIO.SanitizeSecurityValue("kiosk.schedule.start", JsonValue.Create("99:99")));
        Assert.Null(SettingsIO.SanitizeSecurityValue("kiosk.openSections", JsonValue.Create("audio")));
        Assert.NotNull(SettingsIO.SanitizeSecurityValue("kiosk.schedule.start", JsonValue.Create("08:30")));
        Assert.Equal(true, SettingsIO.SanitizeSecurityValue("kiosk.readOnly", JsonValue.Create(true)).GetValue<bool>());
    }

    // WHY: osk נשאר Hostile; חריג רק כשכל התנאים (פיצ'ר, נתיב System32, חתימה, הורה=הגשר) מתקיימים
    [Theory]
    [InlineData(true, true, true, true, true)]
    [InlineData(false, true, true, true, false)]
    [InlineData(true, false, true, true, false)]
    [InlineData(true, true, false, true, false)]
    [InlineData(true, true, true, false, false)]
    public void Osk_allowed_only_when_all_conditions(bool feat, bool path, bool sig, bool parent, bool expected)
    {
        Assert.True(ShellPolicy.IsHostileName("osk.exe"));
        Assert.Equal(expected, ShellPolicy.OskAllowed("osk.exe", feat, path, sig, parent));
        Assert.False(ShellPolicy.OskAllowed("notepad.exe", true, true, true, true));
    }
}
