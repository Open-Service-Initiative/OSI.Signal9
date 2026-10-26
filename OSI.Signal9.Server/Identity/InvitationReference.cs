using System.Security.Cryptography;
using System.Text;

namespace OSI.Signal9.Server.Identity;

/// <summary>
/// Invitation references (#121, story 29): 256 bits from the system CSPRNG, base64url-encoded behind a fixed prefix so
/// secret scanners can recognise a leaked link. The link carries only this; only <see cref="Hash"/> is stored.
/// </summary>
public static class InvitationReference
{
    /// <summary>Marks a string as a Signal9 invitation reference.</summary>
    public const string Prefix = "s9i_";

    /// <summary>Where on auth an invitation is redeemed, followed by its reference.</summary>
    public const string Path = "invitations";

    public static string New() => Prefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>The link to redeem the invitation at auth, the issuer's host (ADR-0003).</summary>
    public static Uri Link(Uri issuer, string reference) => new(issuer, $"{Path}/{reference}");

    /// <summary>
    /// SHA-256 of the reference's UTF-8 bytes. A fast hash is enough for a 256-bit random value: there's nothing to guess
    /// (OWASP Password Storage: slow hashes are for low-entropy passwords).
    /// </summary>
    public static byte[] Hash(string reference) => SHA256.HashData(Encoding.UTF8.GetBytes(reference));
}
