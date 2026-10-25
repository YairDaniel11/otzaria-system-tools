using OtzariaBridge;
using Xunit;

namespace OtzariaBridge.Tests;

/// <summary>[x1] התאוששות שירות ו-backoff מעריכי מתמשך בגשר</summary>
public class X1Tests
{
    [Fact]
    public void Failure_recovery_never_ends_with_no_action()
    {
        var a = ServiceRecovery.FailureArgs("Svc");
        Assert.Contains("reset= 900", a[0]);                       // לא 86400: חלון איפוס קצר
        Assert.StartsWith("failureflag Svc 1", a[1]);
        string acts = ServiceRecovery.Actions;
        Assert.DoesNotContain("/none", acts);                       // אין פעולת "none" שמשאירה את השירות למטה
        Assert.EndsWith("restart/60000", acts);                     // האחרונה restart (SCM חוזר עליה)
        Assert.True(acts.Split('/').Count(x => x == "restart") >= 4);
    }

    [Theory]
    [InlineData(0, 0)] [InlineData(4, 0)] [InlineData(5, 60)] [InlineData(6, 120)]
    [InlineData(7, 240)] [InlineData(10, 1920)] [InlineData(11, 3600)] [InlineData(500, 3600)]
    public void Lock_seconds_exponential_capped(int fails, int secs) => Assert.Equal(secs, BridgeBackoff.LockSeconds(fails));

    [Fact]
    public void Clamp_until_caps_clock_tampering_and_expired()
    {
        long now = DateTime.UtcNow.Ticks;
        Assert.Equal(0, BridgeBackoff.ClampUntil(now - 1, now));
        Assert.Equal(now + TimeSpan.TicksPerSecond * 3600, BridgeBackoff.ClampUntil(now + TimeSpan.TicksPerDay * 30, now));
        Assert.Equal(now + 5000, BridgeBackoff.ClampUntil(now + 5000, now));
    }

    [Fact]
    public void Old_failures_forgotten_recent_kept()
    {
        long now = DateTime.UtcNow.Ticks;
        Assert.Equal(0, BridgeBackoff.EffectiveFails(9, now - TimeSpan.TicksPerHour * 25, now));
        Assert.Equal(9, BridgeBackoff.EffectiveFails(9, now - TimeSpan.TicksPerHour, now));
        Assert.Equal(0, BridgeBackoff.EffectiveFails(-3, 0, now));
    }
}
