using System.Text.Json.Nodes;
using OtzariaBridge;
using Xunit;

/// <summary>באגי VM: הסרה (השירות הרג את עצמו, SCM הפעיל אותו מחדש) ו-enable עם נתיב שלא קיים</summary>
public class UninstallTests
{
    [Fact]
    public void Uninstall_never_kills_service_process_in_session0()
    {
        Assert.False(Installer.ShouldKill(100, 0, 5, keepService: true));  // השירות (session 0) נשמר בהסרה
        Assert.True(Installer.ShouldKill(100, 1, 5, keepService: true));   // גשר של משתמש נהרג
        Assert.False(Installer.ShouldKill(5, 1, 5, keepService: true));    // עצמי לעולם לא
        Assert.True(Installer.ShouldKill(100, 0, 5, keepService: false));  // התנהגות התקנה/עדכון נשמרת
    }

    [Fact]
    public void Recovery_is_disabled_before_uninstall_and_restorable()
    {
        var d = ServiceRecovery.DisableArgs("Svc");
        Assert.Contains(d, a => a == "failureflag Svc 0");
        Assert.Contains(d, a => a.StartsWith("failure Svc reset= 0") && a.EndsWith("actions= \"\""));
        Assert.DoesNotContain(d, a => a.Contains("restart"));
        Assert.Contains(ServiceRecovery.FailureArgs("Svc"), a => a.Contains("restart"));
    }

    [Fact]
    public void Enable_with_missing_file_is_rejected_and_state_untouched()
    {
        var st = new JsonObject { ["active"] = false, ["otzariaPath"] = "C:/Otz/otzaria.exe" };
        var e = HardLockService.ApplyEnableOtzariaPath(st, "C:/nope/missing.exe", _ => false);
        Assert.NotNull(e);
        Assert.Equal("bad_request", e["code"]!.ToString());
        Assert.False(st["active"]!.GetValue<bool>());
        Assert.Equal("C:/Otz/otzaria.exe", st["otzariaPath"]!.ToString());
        Assert.Null(HardLockService.ApplyEnableOtzariaPath(st, "C:/Otz/otzaria.exe", _ => true));
    }
}
