using System.Security.Cryptography;
using System.Text;

namespace OSI.Signal9.Server.Identity;

/// <summary>
/// Enrollment token secrets (#127): 256 bits from the system CSPRNG, base64url-encoded behind a fixed prefix so secret
/// scanners can recognise a leaked one. Only <see cref="Hash"/> is stored.
/// </summary>
public static class EnrollmentSecret
{
    /// <summary>Marks a string as a Signal9 enrollment token secret.</summary>
    public const string Prefix = "s9e_";

    public static string New() => Prefix + Base64Url(RandomNumberGenerator.GetBytes(32));

    /// <summary>
    /// SHA-256 of the secret's UTF-8 bytes. A fast hash is enough for a 256-bit random secret: there's nothing to guess
    /// (OWASP Password Storage: slow hashes are for low-entropy passwords).
    /// </summary>
    public static byte[] Hash(string secret) => SHA256.HashData(Encoding.UTF8.GetBytes(secret));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
