using System;
using System.Security.Cryptography;
using System.Text;
using Overfin.Jellyfin;
using Xunit;

public class KeyCheckTests
{
    private static string Hash(string s) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(s)));

    // Only an API key whose token hashes to the one Overfin set gets in.
    [Fact]
    public void OnlyOverfinsKey()
    {
        var hash = Hash("overfins-key");
        Assert.True(KeyCheck.IsOverfins(hash, true, "overfins-key"));
        Assert.True(KeyCheck.IsOverfins(hash.ToUpperInvariant(), true, "overfins-key"));
        Assert.False(KeyCheck.IsOverfins(hash, true, "another-key"));
        Assert.False(KeyCheck.IsOverfins(hash, false, "overfins-key")); // a user's token, though the same
        Assert.False(KeyCheck.IsOverfins(hash, true, ""));
        Assert.False(KeyCheck.IsOverfins(hash, true, null));
    }

    // Until Overfin set its key's hash, or with a malformed one, no one.
    [Fact]
    public void NoHashNoOne()
    {
        Assert.False(KeyCheck.IsOverfins("", true, "overfins-key"));
        Assert.False(KeyCheck.IsOverfins(null, true, "overfins-key"));
        Assert.False(KeyCheck.IsOverfins("abc", true, "overfins-key"));
    }
}
