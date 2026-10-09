namespace TeamBuilder.Infrastructure.WebPush;

/// <summary>Unpadded base64url (RFC 4648 §5), the encoding of every Web Push key.</summary>
public static class Base64Url
{
    public static string Encode(ReadOnlySpan<byte> data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Decodes padded or unpadded base64url (standard base64 is tolerated); false for anything else.</summary>
    public static bool TryDecode(string? value, out byte[] bytes)
    {
        bytes = [];
        if (string.IsNullOrEmpty(value) || value.Length > 4096)
            return false;

        var base64 = value.TrimEnd('=').Replace('-', '+').Replace('_', '/');
        base64 = base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '=');
        try
        {
            bytes = Convert.FromBase64String(base64);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public static byte[] Decode(string value) =>
        TryDecode(value, out var bytes) ? bytes : throw new FormatException("Not a base64url value.");
}
