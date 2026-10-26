using OtzariaBridge;
using Xunit;

namespace OtzariaBridge.Tests;

// הצפת דיווחים כפולים: תור שלא מתרוקן ושליחה חוזרת של אותו fingerprint
public class GhRelayTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ghrelay-" + Guid.NewGuid().ToString("N"));
    public GhRelayTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string Make(string text, TimeSpan age)
    {
        string f = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(f, text);
        File.SetLastWriteTimeUtc(f, DateTime.UtcNow - age);
        return f;
    }

    [Theory]
    [InlineData(200, true)] [InlineData(201, true)] [InlineData(409, true)]
    [InlineData(400, true)] [InlineData(404, true)]
    [InlineData(408, false)] [InlineData(429, false)] [InlineData(500, false)] [InlineData(502, false)] [InlineData(503, false)]
    public void IsFinal_OnlyPermanentAnswers(int code, bool expected) => Assert.Equal(expected, GhRelay.IsFinal(code));

    [Fact] public void StaleSelfTest_OldAndMatching() => Assert.True(GhRelay.IsStaleSelfTest(Make("x selftest-crash y", TimeSpan.FromHours(25)), DateTime.UtcNow));
    [Fact] public void StaleSelfTest_Fresh() => Assert.False(GhRelay.IsStaleSelfTest(Make("selftest-crash", TimeSpan.FromHours(1)), DateTime.UtcNow));
    [Fact] public void StaleSelfTest_OldButOtherReport() => Assert.False(GhRelay.IsStaleSelfTest(Make("real crash", TimeSpan.FromHours(48)), DateTime.UtcNow));
    [Fact] public void StaleSelfTest_Missing() => Assert.False(GhRelay.IsStaleSelfTest(Path.Combine(_dir, "nope.txt"), DateTime.UtcNow));

    [Fact]
    public void SentFingerprint_RememberedThenExpires()
    {
        string sent = Path.Combine(_dir, "sent");
        Assert.False(GhRelay.WasSent(sent, "0123456789abcdef", DateTime.UtcNow));
        GhRelay.MarkSent(sent, "0123456789abcdef");
        Assert.True(GhRelay.WasSent(sent, "0123456789abcdef", DateTime.UtcNow));
        Assert.False(GhRelay.WasSent(sent, "fedcba9876543210", DateTime.UtcNow));
        Assert.False(GhRelay.WasSent(sent, "0123456789abcdef", DateTime.UtcNow.AddHours(25)));
        Assert.False(File.Exists(Path.Combine(sent, "0123456789abcdef.txt")));
    }

    [Fact]
    public void TryDelete_RemovesFile_AndReportsFailureWithoutThrowing()
    {
        string f = Make("a", TimeSpan.Zero);
        Assert.True(GhRelay.TryDelete(f, "test"));
        Assert.False(File.Exists(f));
        string locked = Make("b", TimeSpan.Zero);
        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.False(GhRelay.TryDelete(locked, "test"));
    }
}
