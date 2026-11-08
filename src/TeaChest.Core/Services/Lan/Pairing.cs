using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace TeaChest.Services.Lan;

/// <summary>
/// Short authentication string (numeric comparison) for pairing two devices.
///
/// Both sides already hold each other's TLS certificate. The client commits to a random nonce
/// (sends its hash), the server replies with its own nonce, then the client reveals its nonce.
/// Each side derives a 6-digit code from both certificate fingerprints and both nonces. A
/// man-in-the-middle runs two TLS sessions with different certificates, so the codes only match
/// if it can predict a nonce that has not been revealed yet: about a one in a million chance
/// per attempt. A typed PIN is not used, because without a PAKE a MITM could brute-force it
/// offline from the handshake.
/// </summary>
public static class Pairing
{
    private const int NonceBytes = 32;
    private static readonly byte[] CommitLabel = Encoding.ASCII.GetBytes("AppMigrate pairing commit v1");
    private static readonly byte[] CodeLabel = Encoding.ASCII.GetBytes("AppMigrate pairing code v1");

    public static byte[] Fingerprint(X509Certificate certificate)
        => SHA256.HashData(certificate.GetRawCertData());

    public static byte[] NewNonce() => RandomNumberGenerator.GetBytes(NonceBytes);

    public static byte[] Commit(byte[] nonce) => SHA256.HashData(Concat(CommitLabel, nonce));

    public static bool VerifyCommit(byte[] commitment, byte[] nonce)
        => nonce.Length == NonceBytes && CryptographicOperations.FixedTimeEquals(commitment, Commit(nonce));

    /// <summary>The 6-digit code, formatted "123 456". Argument order is the same on both sides.</summary>
    public static string Code(byte[] serverFingerprint, byte[] clientFingerprint, byte[] clientNonce, byte[] serverNonce)
    {
        byte[] hash = SHA256.HashData(Concat(CodeLabel, serverFingerprint, clientFingerprint, clientNonce, serverNonce));
        uint value = BinaryPrimitives.ReadUInt32BigEndian(hash) % 1_000_000;
        string digits = value.ToString("D6");
        return digits[..3] + " " + digits[3..];
    }

    public static byte[] DecodeBase64(string? value, int expectedLength)
    {
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(value ?? "");
        }
        catch (FormatException)
        {
            throw new InvalidDataException("The other device sent malformed pairing data.");
        }
        if (bytes.Length != expectedLength) throw new InvalidDataException("The other device sent malformed pairing data.");
        return bytes;
    }

    private static byte[] Concat(params byte[][] parts)
    {
        byte[] result = new byte[parts.Sum(p => p.Length)];
        int offset = 0;
        foreach (byte[] part in parts)
        {
            Buffer.BlockCopy(part, 0, result, offset, part.Length);
            offset += part.Length;
        }
        return result;
    }

    internal const int HashBytes = 32;
    internal const int NonceLength = NonceBytes;
}
