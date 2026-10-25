using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using OSI.Signal9.Server.Identity;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace OSI.Signal9.API.Agents;

/// <summary>
/// An agent's credential (ADR-0005, stories 98-100): the self-signed certificate of its non-exportable key, registered
/// as its OAuth client for <c>self_signed_tls_client_auth</c> (RFC 8705 §2.2). Its client ID is the agent's ID, and it
/// may only obtain <c>agent</c> tokens with client credentials, which the mTLS token endpoint binds to the certificate.
/// </summary>
internal static class AgentCredentials
{
    /// <summary>The longest validity an agent certificate may have: the CA/Browser Forum's cap for TLS certificates.</summary>
    public static readonly TimeSpan MaxCertificateLifetime = TimeSpan.FromDays(398);

    private const string ClientAuthentication = "1.3.6.1.5.5.7.3.2";

    // SHA-2 signatures only: RSA PKCS#1 v1.5, RSA-PSS and ECDSA.
    private static readonly HashSet<string> SignatureAlgorithms =
    [
        "1.2.840.113549.1.1.11", "1.2.840.113549.1.1.12", "1.2.840.113549.1.1.13", "1.2.840.113549.1.1.10",
        "1.2.840.10045.4.3.2", "1.2.840.10045.4.3.3", "1.2.840.10045.4.3.4",
    ];

    // NIST P-256, P-384 and P-521.
    private static readonly HashSet<string> Curves = ["1.2.840.10045.3.1.7", "1.3.132.0.34", "1.3.132.0.35"];

    /// <summary>
    /// Reads the certificate an enrolling agent presents, or says why it isn't acceptable: it must be self-signed with a
    /// valid signature, an ECDSA P-256 (or stronger) or RSA 3072+ key, the digitalSignature key usage and the clientAuth
    /// extended key usage, not a CA, and valid now for at most <see cref="MaxCertificateLifetime"/>.
    /// </summary>
    public static X509Certificate2? Read(string pem, DateTimeOffset now, out string? problem)
    {
        X509Certificate2 certificate;
        try
        {
            certificate = X509Certificate2.CreateFromPem(pem);
        }
        catch (CryptographicException)
        {
            problem = "The certificate must be one PEM-encoded X.509 certificate.";
            return null;
        }

        problem = Problem(certificate, now);
        if (problem is null)
            return certificate;
        certificate.Dispose();
        return null;
    }

    private static string? Problem(X509Certificate2 certificate, DateTimeOffset now)
    {
        if (!certificate.SubjectName.RawData.AsSpan().SequenceEqual(certificate.IssuerName.RawData))
            return "The certificate must be self-signed.";
        if (certificate.Extensions.OfType<X509BasicConstraintsExtension>().Any(b => b.CertificateAuthority))
            return "The certificate must not be a certificate authority.";
        if (certificate.Extensions.OfType<X509KeyUsageExtension>().SingleOrDefault() is not { } usage
            || !usage.KeyUsages.HasFlag(X509KeyUsageFlags.DigitalSignature))
            return "The certificate must have the digitalSignature key usage.";
        if (certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().SingleOrDefault() is not { } extended
            || !extended.EnhancedKeyUsages.Cast<Oid>().Any(o => o.Value == ClientAuthentication))
            return "The certificate must have the clientAuth extended key usage.";
        if (!StrongKey(certificate))
            return "The certificate's key must be ECDSA on P-256, P-384 or P-521, or RSA of at least 3072 bits.";
        if (certificate.SignatureAlgorithm.Value is not { } algorithm || !SignatureAlgorithms.Contains(algorithm))
            return "The certificate must be signed with SHA-256 or stronger.";
        var notBefore = new DateTimeOffset(certificate.NotBefore.ToUniversalTime());
        var notAfter = new DateTimeOffset(certificate.NotAfter.ToUniversalTime());
        if (now < notBefore || now > notAfter)
            return "The certificate must be valid now.";
        if (notAfter - notBefore > MaxCertificateLifetime)
            return $"The certificate must be valid for at most {MaxCertificateLifetime.TotalDays} days.";
        if (!SignatureVerifies(certificate, now))
            return "The certificate's self-signature doesn't verify.";
        return null;
    }

    private static bool StrongKey(X509Certificate2 certificate)
    {
        if (certificate.GetRSAPublicKey() is { } rsa)
        {
            using (rsa)
                return rsa.KeySize >= 3072;
        }
        if (certificate.GetECDsaPublicKey() is { } ecdsa)
        {
            using (ecdsa)
                return ecdsa.ExportParameters(false).Curve.Oid?.Value is { } curve && Curves.Contains(curve);
        }
        return false;
    }

    /// <summary>
    /// Builds the certificate's chain with only itself trusted, as OpenIddict does for <c>self_signed_tls_client_auth</c>:
    /// that verifies its signature with its own key, its validity and its clientAuth usage.
    /// </summary>
    private static bool SignatureVerifies(X509Certificate2 certificate, DateTimeOffset now)
    {
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(certificate);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.ApplicationPolicy.Add(new Oid(ClientAuthentication));
        chain.ChainPolicy.VerificationTime = now.LocalDateTime;
        chain.ChainPolicy.VerificationTimeIgnored = false;
        try
        {
            return chain.Build(certificate) && chain.ChainElements.Count == 1;
        }
        finally
        {
            foreach (var element in chain.ChainElements)
                element.Certificate.Dispose();
        }
    }

    /// <summary>
    /// The agent's OAuth client: confidential, authenticating only with <paramref name="certificate"/>, which is its key
    /// set's one key, and allowed only client credentials for the <c>agent</c> scope. No refresh tokens.
    /// </summary>
    public static OpenIddictApplicationDescriptor Client(Guid agentId, X509Certificate2 certificate) => new()
    {
        ClientId = agentId.ToString(),
        ClientType = ClientTypes.Confidential,
        DisplayName = $"Agent {agentId}",
        JsonWebKeySet = new JsonWebKeySet { Keys = { ClientCertificateKey.From(certificate) } },
        Permissions =
        {
            Permissions.Endpoints.Token,
            Permissions.GrantTypes.ClientCredentials,
            Permissions.Prefixes.Scope + Server.Identity.Scopes.Agent,
        },
    };

    /// <summary>
    /// The enrolled agent an access token belongs to: one with the <c>agent</c> scope, bound to a certificate
    /// (<c>cnf</c>), that the agent's own client obtained acting as itself. Null for any other token.
    /// </summary>
    public static Guid? AgentId(this ClaimsPrincipal user) =>
        user.HasScope(Server.Identity.Scopes.Agent)
        && user.HasClaim(c => c.Type == Claims.Confirmation)
        && user.GetClaim(Claims.Subject) is { } subject
        && subject == user.GetClaim(Claims.ClientId)
        && Guid.TryParse(subject, out var agentId)
            ? agentId
            : null;
}
