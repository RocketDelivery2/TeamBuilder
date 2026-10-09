using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using TeamBuilder.Infrastructure.WebPush;

namespace TeamBuilder.Tests.Support;

/// <summary>
/// A simulated browser for Web Push tests: its own P-256 key pair and auth secret (what
/// <c>PushSubscription.toJSON().keys</c> carries), and the receiving side of RFC 8291 so a test
/// can read what the server encrypted for it. Also a fixed VAPID key pair for test hosts.
/// </summary>
internal sealed class SimulatedBrowser : IDisposable
{
    private readonly ECDiffieHellman _key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

    public SimulatedBrowser(string endpoint)
    {
        Endpoint = endpoint;
        AuthSecret = RandomNumberGenerator.GetBytes(16);
        PublicKey = WebPushEncryption.ExportUncompressed(_key);
    }

    public string Endpoint { get; }
    public byte[] PublicKey { get; }
    public byte[] AuthSecret { get; }
    public string P256dh => Base64Url.Encode(PublicKey);
    public string Auth => Base64Url.Encode(AuthSecret);

    public object RegistrationBody(string? previousEndpoint = null) => new
    {
        endpoint = Endpoint,
        expirationTime = (long?)null,
        keys = new { p256dh = P256dh, auth = Auth },
        previousEndpoint
    };

    /// <summary>Decrypts an aes128gcm body the way a browser does.</summary>
    public byte[] Decrypt(byte[] body) => WebPushDecryption.Decrypt(body, _key, AuthSecret);

    public void Dispose() => _key.Dispose();
}

/// <summary>The receiver side of RFC 8291 / RFC 8188 (single record), for tests only.</summary>
internal static class WebPushDecryption
{
    public static byte[] Decrypt(byte[] body, ECDiffieHellman userAgentKey, byte[] authSecret)
    {
        var salt = body[..16];
        var recordSize = BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(16, 4));
        var idLength = body[20];
        var asPublic = body.AsSpan(21, idLength).ToArray();
        var ciphertext = body.AsSpan(21 + idLength).ToArray();
        if (ciphertext.Length > recordSize)
            throw new CryptographicException("More than one record.");

        using var asKey = WebPushEncryption.ImportPublicKey(asPublic);
        var uaPublic = WebPushEncryption.ExportUncompressed(userAgentKey);
        var ecdh = userAgentKey.DeriveRawSecretAgreement(asKey.PublicKey);
        var prkKey = HKDF.Extract(HashAlgorithmName.SHA256, ecdh, authSecret);
        var keyInfo = Encoding.ASCII.GetBytes("WebPush: info\0").Concat(uaPublic).Concat(asPublic).ToArray();
        var ikm = HKDF.Expand(HashAlgorithmName.SHA256, prkKey, 32, keyInfo);
        var prk = HKDF.Extract(HashAlgorithmName.SHA256, ikm, salt);
        var cek = HKDF.Expand(HashAlgorithmName.SHA256, prk, 16, Encoding.ASCII.GetBytes("Content-Encoding: aes128gcm\0"));
        var nonce = HKDF.Expand(HashAlgorithmName.SHA256, prk, 12, Encoding.ASCII.GetBytes("Content-Encoding: nonce\0"));

        var plaintext = new byte[ciphertext.Length - 16];
        using var aes = new AesGcm(cek, 16);
        aes.Decrypt(nonce, ciphertext.AsSpan(0, ciphertext.Length - 16), ciphertext.AsSpan(ciphertext.Length - 16), plaintext);

        var end = Array.LastIndexOf(plaintext, (byte)0x02);
        if (end < 0 || plaintext.AsSpan(end + 1).IndexOfAnyExcept((byte)0) >= 0)
            throw new CryptographicException("Missing last-record delimiter.");
        return plaintext[..end];
    }
}

internal static class WebPushTestKeys
{
    /// <summary>A throwaway VAPID key pair generated for tests (never used anywhere real).</summary>
    public static readonly (string PublicKey, string PrivateKey) Vapid = VapidKeys.Generate();

    /// <summary>Configuration that enables Web Push in a test host.</summary>
    public static Dictionary<string, string?> EnabledConfiguration(int maxDevices = 10) => new()
    {
        ["WebPush:Enabled"] = "true",
        ["WebPush:Subject"] = "mailto:qa@teambuilder.test",
        ["WebPush:VapidPublicKey"] = Vapid.PublicKey,
        ["WebPush:VapidPrivateKey"] = Vapid.PrivateKey,
        ["WebPush:MaxDevicesPerPlayer"] = maxDevices.ToString(System.Globalization.CultureInfo.InvariantCulture)
    };
}
