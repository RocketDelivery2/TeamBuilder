using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace TeamBuilder.Infrastructure.WebPush;

/// <summary>
/// Message encryption for Web Push (RFC 8291) in the <c>aes128gcm</c> content coding
/// (RFC 8188), as one record: an ephemeral P-256 key agreement with the browser's public key,
/// HKDF-SHA-256 keyed by the browser's auth secret, then AES-128-GCM. Only the browser can read
/// the payload; the push service sees ciphertext.
/// </summary>
public static class WebPushEncryption
{
    public const int PublicKeyLength = 65;
    public const int AuthSecretLength = 16;
    public const int SaltLength = 16;

    /// <summary>The record size written in the header; payloads must fit one record.</summary>
    public const int RecordSize = 4096;

    /// <summary>Largest plaintext that fits one record (record size − delimiter − GCM tag).</summary>
    public const int MaxPlaintextLength = RecordSize - 1 - 16;

    private static readonly byte[] KeyInfoPrefix = Encoding.ASCII.GetBytes("WebPush: info\0");
    private static readonly byte[] CekInfo = Encoding.ASCII.GetBytes("Content-Encoding: aes128gcm\0");
    private static readonly byte[] NonceInfo = Encoding.ASCII.GetBytes("Content-Encoding: nonce\0");

    /// <summary>True when the bytes are an uncompressed point on P-256 (what browsers send as <c>p256dh</c>).</summary>
    public static bool IsValidUserAgentPublicKey(ReadOnlySpan<byte> publicKey)
    {
        if (publicKey.Length != PublicKeyLength || publicKey[0] != 0x04)
            return false;
        try
        {
            using var key = ImportPublicKey(publicKey);
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    /// <summary>Encrypts with a fresh ephemeral key and random salt.</summary>
    public static byte[] Encrypt(ReadOnlySpan<byte> userAgentPublicKey, ReadOnlySpan<byte> authSecret, ReadOnlySpan<byte> plaintext)
    {
        using var ephemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        Span<byte> salt = stackalloc byte[SaltLength];
        RandomNumberGenerator.Fill(salt);
        return Encrypt(userAgentPublicKey, authSecret, plaintext, ephemeral, salt);
    }

    /// <summary>Deterministic form (given key and salt), for the RFC 8291 test vector.</summary>
    /// <exception cref="ArgumentException">A key has the wrong size or the payload is too large.</exception>
    /// <exception cref="CryptographicException">The browser key is not a P-256 point.</exception>
    public static byte[] Encrypt(
        ReadOnlySpan<byte> userAgentPublicKey,
        ReadOnlySpan<byte> authSecret,
        ReadOnlySpan<byte> plaintext,
        ECDiffieHellman applicationServerKey,
        ReadOnlySpan<byte> salt)
    {
        if (userAgentPublicKey.Length != PublicKeyLength || userAgentPublicKey[0] != 0x04)
            throw new ArgumentException("The browser public key must be a 65-byte uncompressed P-256 point.", nameof(userAgentPublicKey));
        if (authSecret.Length != AuthSecretLength)
            throw new ArgumentException("The auth secret must be 16 bytes.", nameof(authSecret));
        if (salt.Length != SaltLength)
            throw new ArgumentException("The salt must be 16 bytes.", nameof(salt));
        if (plaintext.Length > MaxPlaintextLength)
            throw new ArgumentException("The push payload is too large for one record.", nameof(plaintext));

        using var userAgentKey = ImportPublicKey(userAgentPublicKey);
        var asPublic = ExportUncompressed(applicationServerKey);
        var ecdhSecret = applicationServerKey.DeriveRawSecretAgreement(userAgentKey.PublicKey);

        // RFC 8291 §3.3-3.4: IKM from the ECDH secret keyed by the auth secret, bound to both keys.
        var prkKey = HKDF.Extract(HashAlgorithmName.SHA256, ecdhSecret, authSecret.ToArray());
        var keyInfo = Concat(KeyInfoPrefix, userAgentPublicKey, asPublic);
        var ikm = HKDF.Expand(HashAlgorithmName.SHA256, prkKey, 32, keyInfo);

        // RFC 8188 §2.2-2.3: content encryption key and nonce from the salt.
        var prk = HKDF.Extract(HashAlgorithmName.SHA256, ikm, salt.ToArray());
        var cek = HKDF.Expand(HashAlgorithmName.SHA256, prk, 16, CekInfo);
        var nonce = HKDF.Expand(HashAlgorithmName.SHA256, prk, 12, NonceInfo);

        // One record: plaintext then the 0x02 "last record" delimiter, no padding.
        var record = new byte[plaintext.Length + 1];
        plaintext.CopyTo(record);
        record[^1] = 0x02;

        var header = SaltLength + 4 + 1 + asPublic.Length;
        var output = new byte[header + record.Length + 16];
        salt.CopyTo(output);
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(SaltLength, 4), RecordSize);
        output[SaltLength + 4] = (byte)asPublic.Length;
        asPublic.CopyTo(output.AsSpan(SaltLength + 5));

        using var aes = new AesGcm(cek, 16);
        aes.Encrypt(nonce, record, output.AsSpan(header, record.Length), output.AsSpan(header + record.Length, 16));

        CryptographicOperations.ZeroMemory(ecdhSecret);
        CryptographicOperations.ZeroMemory(cek);
        return output;
    }

    internal static ECDiffieHellman ImportPublicKey(ReadOnlySpan<byte> uncompressed)
    {
        var key = ECDiffieHellman.Create();
        try
        {
            key.ImportParameters(new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                Q = new ECPoint { X = uncompressed.Slice(1, 32).ToArray(), Y = uncompressed.Slice(33, 32).ToArray() }
            });
            return key;
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }

    internal static byte[] ExportUncompressed(ECAlgorithm key)
    {
        var q = key.ExportParameters(false).Q;
        return Concat([0x04], q.X!, q.Y!);
    }

    private static byte[] Concat(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, ReadOnlySpan<byte> c)
    {
        var result = new byte[a.Length + b.Length + c.Length];
        a.CopyTo(result);
        b.CopyTo(result.AsSpan(a.Length));
        c.CopyTo(result.AsSpan(a.Length + b.Length));
        return result;
    }
}
