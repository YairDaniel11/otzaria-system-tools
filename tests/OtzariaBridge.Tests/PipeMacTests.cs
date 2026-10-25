using System.Security.Cryptography;
using System.Text.Json.Nodes;
using OtzariaBridge;
using Xunit;

namespace OtzariaBridge.Tests;

// #32 ממצא 1: חתימת פקודות רגישות בצינור השירות
public class PipeMacTests
{
    private static readonly byte[] Key = RandomNumberGenerator.GetBytes(32);

    private static JsonObject Signed(long ts)
    {
        var q = new JsonObject { ["op"] = "disable", ["password"] = "secret1", ["hello"] = "ABC", ["ts"] = ts };
        q["mac"] = PipeMac.Compute(Key, q);
        return q;
    }

    [Fact] public void ValidFresh() => Assert.True(PipeMac.Verify(Key, Signed(1000), 1010));
    [Fact] public void Stale() => Assert.False(PipeMac.Verify(Key, Signed(1000), 1000 + PipeMac.WindowSeconds + 1));
    [Fact] public void WrongKey() => Assert.False(PipeMac.Verify(RandomNumberGenerator.GetBytes(32), Signed(1000), 1000));
    [Fact] public void NoMac() { var q = Signed(1000); q.Remove("mac"); Assert.False(PipeMac.Verify(Key, q, 1000)); }
    [Fact] public void NullKey() => Assert.False(PipeMac.Verify(null, Signed(1000), 1000));

    [Fact]
    public void TamperedOpOrNonce()
    {
        var q = Signed(1000); q["op"] = "uninstall"; Assert.False(PipeMac.Verify(Key, q, 1000));
        q = Signed(1000); q["hello"] = "XYZ"; Assert.False(PipeMac.Verify(Key, q, 1000));
        q = Signed(1000); q["extra"] = true; Assert.False(PipeMac.Verify(Key, q, 1000));
    }

    [Fact]
    public void OrderIndependent()
    {
        var a = Signed(1000);
        var b = new JsonObject { ["ts"] = 1000L, ["hello"] = "ABC", ["password"] = "secret1", ["op"] = "disable", ["mac"] = a["mac"]!.ToString() };
        Assert.True(PipeMac.Verify(Key, b, 1000));
    }
}
