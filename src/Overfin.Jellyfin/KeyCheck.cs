using System;
using System.Security.Cryptography;
using System.Text;

namespace Overfin.Jellyfin;

/// <summary>Tells Overfin's key apart from any other credential.</summary>
internal static class KeyCheck
{
    /// <summary>
    /// Whether a request signed in with an API key whose token's SHA-256 is
    /// keyHash: Overfin's. A user's token, an empty or malformed hash, or any
    /// other key is refused. The hashes are compared in constant time.
    /// </summary>
    public static bool IsOverfins(string? keyHash, bool isApiKey, string? token)
    {
        if (!isApiKey || string.IsNullOrEmpty(token) || keyHash is null || keyHash.Length != 64)
        {
            return false;
        }

        var sum = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(sum), Encoding.ASCII.GetBytes(keyHash.ToLowerInvariant()));
    }
}
