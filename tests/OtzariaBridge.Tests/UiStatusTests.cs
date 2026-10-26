using System.Text.Json;
using System.Text.Json.Nodes;
using OtzariaBridge;
using Xunit;

namespace OtzariaBridge.Tests;

/// <summary>שדות הבריאות החדשים ב-HardLock.Status() (hard.*): בוליאנים/קודים בלבד, בלי נתיבים או SID. WHY: לוח "בריאות ההקשחה" בממשק קורא אותם.</summary>
public class UiStatusTests : IDisposable
{
    private readonly string _t = Path.Combine(Path.GetTempPath(), "ui-" + Guid.NewGuid().ToString("N"));
    public UiStatusTests() { Directory.CreateDirectory(_t); }
    public void Dispose() { try { Directory.Delete(_t, true); } catch { } }
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void IssueCodes_MapToShortCodes_NoSidOrText()
    {
        var codes = HardLock.IssueCodes(new JsonArray("S-1-5-21-123: DisableTaskMgr לא אומת", "S-1-5-21-9: Access denied C:/secret"));
        Assert.Equal(new[] { "unverified", "error" }, codes);
        Assert.DoesNotContain(codes, c => c.Contains("S-1-5") || c.Contains("secret"));
    }

    [Fact]
    public void IssueCodes_Null_WhenServiceSilent_AndEmptyWhenClean()
    {
        Assert.Null(HardLock.IssueCodes(null));
        Assert.Empty(HardLock.IssueCodes(new JsonArray()));
    }

    [Fact]
    public void UpdateHealth_None_WhenNoPending()
    {
        var j = JsonSerializer.SerializeToNode(HardLock.UpdateHealthFor(_t, T0))!;
        Assert.Equal("none", j["state"]!.ToString());
        Assert.True(j["ok"]!.GetValue<bool>());
        Assert.False(j["rollback"]!.GetValue<bool>());
    }

    [Fact]
    public void UpdateHealth_WaitingHealthyRollback()
    {
        File.WriteAllText(Path.Combine(_t, UpdateRollback.StateName),
            JsonSerializer.Serialize(new UpdateRollback.State("AA", "1.0", "BB", "1.1", T0)));
        var w = JsonSerializer.SerializeToNode(HardLock.UpdateHealthFor(_t, T0.AddSeconds(5)))!;
        Assert.Equal("waiting", w["state"]!.ToString());
        Assert.True(w["pending"]!.GetValue<bool>());
        var r = JsonSerializer.SerializeToNode(HardLock.UpdateHealthFor(_t, T0.AddSeconds(UpdateRollback.DefaultTimeoutSec + 1)))!;
        Assert.Equal("rollback", r["state"]!.ToString());
        Assert.False(r["ok"]!.GetValue<bool>());
        File.WriteAllText(Path.Combine(_t, UpdateRollback.HealthName), "BB");
        var h = JsonSerializer.SerializeToNode(HardLock.UpdateHealthFor(_t, T0.AddSeconds(500)))!;
        Assert.Equal("healthy", h["state"]!.ToString());
        Assert.True(h["ok"]!.GetValue<bool>());
        Assert.DoesNotContain("BB", h.ToJsonString());
    }

    [Fact]
    public void Status_ExposesNewOptionalFields_WithoutService()
    {
        var j = JsonSerializer.SerializeToNode(HardLock.Status(), Server.Json)!.AsObject();
        foreach (var k in new[] { "enforcementIssues", "stateCorrupt", "hasRecovery", "configSealOk", "updateHealth" })
            Assert.True(j.ContainsKey(k), k);
        Assert.True(j.ContainsKey("installed"));
    }
}
