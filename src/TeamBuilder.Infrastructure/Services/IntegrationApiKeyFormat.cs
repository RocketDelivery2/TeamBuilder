using System.Security.Cryptography;
using System.Text;

namespace TeamBuilder.Infrastructure.Services;

/// <summary>
/// Integration API key format: <c>tbk_{prefix}_{secret}</c>, 60 ASCII characters. The prefix is
/// 12 lowercase hex characters (6 random bytes) and identifies the key; the secret is 32 random
/// bytes in base64url (43 characters, which may themselves contain '_' or '-'), so parsing is by
/// position, never by splitting. The stored hash is SHA-256 over the whole key: the secret
/// carries 256 bits of entropy, so a slow password hash would add nothing.
/// </summary>
public static class IntegrationApiKeyFormat
{
    public const string Marker = "tbk_";
    public const int PrefixLength = 12;
    public const int SecretLength = 43;
    public const int KeyLength = 4 + PrefixLength + 1 + SecretLength;

    public static (string Key, string Prefix) Generate()
    {
        var prefix = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(PrefixLength / 2));
        var secret = WebPush.Base64Url.Encode(RandomNumberGenerator.GetBytes(32));
        return ($"{Marker}{prefix}_{secret}", prefix);
    }

    /// <summary>The prefix of a well-formed key; false for anything else (no lookup is worth doing).</summary>
    public static bool TryParse(string? key, out string prefix)
    {
        prefix = string.Empty;
        if (key is null || key.Length != KeyLength || !key.StartsWith(Marker, StringComparison.Ordinal) || key[Marker.Length + PrefixLength] != '_')
            return false;

        var candidate = key.AsSpan(Marker.Length, PrefixLength);
        foreach (var c in candidate)
        {
            if (!char.IsAsciiHexDigitLower(c))
                return false;
        }
        foreach (var c in key.AsSpan(Marker.Length + PrefixLength + 1))
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_')
                return false;
        }

        prefix = candidate.ToString();
        return true;
    }

    public static byte[] Hash(string key) => SHA256.HashData(Encoding.ASCII.GetBytes(key));

    public static bool Matches(string key, byte[] storedHash) =>
        storedHash.Length == SHA256.HashSizeInBytes && CryptographicOperations.FixedTimeEquals(Hash(key), storedHash);

    /// <summary>The non-secret display form, e.g. <c>tbk_3f9a0c1d2e4b</c>.</summary>
    public static string Display(string prefix) => Marker + prefix;
}
