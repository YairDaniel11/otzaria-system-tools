using OtzariaBridge;
using Xunit;

namespace OtzariaBridge.Tests;

public class RecoveryCodeTests
{
    [Theory]
    [InlineData("abcd-efgh", "ABCDEFGH")]
    [InlineData(" ab cd-EF gh ", "ABCDEFGH")]
    [InlineData("ABCDEFGH", "ABCDEFGH")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void NormalizeRecovery_strips_separators_and_uppercases(string input, string expected) =>
        Assert.Equal(expected, Secret.NormalizeRecovery(input));

    [Fact]
    public void NewRecoveryCode_roundtrips_through_normalization()
    {
        var code = Secret.NewRecoveryCode();
        var n = Secret.NormalizeRecovery(code);
        Assert.False(string.IsNullOrEmpty(n));
        Assert.Equal(n, Secret.NormalizeRecovery(n.ToLowerInvariant()));
        Assert.NotEqual(n, Secret.NormalizeRecovery(Secret.NewRecoveryCode()));
    }
}
