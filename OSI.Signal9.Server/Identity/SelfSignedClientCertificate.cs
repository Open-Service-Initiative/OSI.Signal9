using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace OSI.Signal9.Server.Identity;

/// <summary>
/// The one policy for every self-signed certificate a client registers for <c>self_signed_tls_client_auth</c> (RFC 8705
/// §2.2): an agent's at enrollment and rotation (ADR-0005, stories 98-100) and an API application's credential (story
/// 84). At authentication, <see cref="Signal9ApplicationManager{TApplication}"/> then accepts only a registered one.
/// </summary>
public static class SelfSignedClientCertificate
{
    /// <summary>The longest validity a client certificate may have: the CA/Browser Forum's cap for TLS certificates.</summary>
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
    /// Reads a certificate a client registers for itself, or says why it isn't acceptable: it must be self-signed with a
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
        if (!SignatureVerifies(certificate))
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
    /// Verifies the certificate's signature over its TBSCertificate with its own public key, which proves the client
    /// holds the private key, as a CSR would. A chain built with the certificate as its own trust anchor doesn't: trust
    /// anchors' signatures aren't checked.
    /// </summary>
    private static bool SignatureVerifies(X509Certificate2 certificate)
    {
        try
        {
            var reader = new AsnReader(certificate.RawData, AsnEncodingRules.DER).ReadSequence();
            var tbs = reader.ReadEncodedValue().Span;
            var algorithm = reader.ReadSequence();
            var oid = algorithm.ReadObjectIdentifier();
            var parameters = algorithm.HasData ? algorithm.ReadEncodedValue() : default;
            var signature = reader.ReadBitString(out _);
            if (Hash(oid, parameters.Span) is not { } hash)
                return false;

            using var rsa = certificate.GetRSAPublicKey();
            using var ecdsa = certificate.GetECDsaPublicKey();
            return oid switch
            {
                RsaPss => rsa?.VerifyData(tbs, signature, hash, RSASignaturePadding.Pss) == true,
                _ when oid.StartsWith(RsaPrefix, StringComparison.Ordinal) =>
                    rsa?.VerifyData(tbs, signature, hash, RSASignaturePadding.Pkcs1) == true,
                _ => ecdsa?.VerifyData(tbs, signature, hash, DSASignatureFormat.Rfc3279DerSequence) == true,
            };
        }
        catch (Exception exception) when (exception is AsnContentException or CryptographicException)
        {
            return false;
        }
    }

    private const string RsaPrefix = "1.2.840.113549.1.1.";
    private const string RsaPss = "1.2.840.113549.1.1.10";

    /// <summary>
    /// The hash a signature algorithm uses. RSA-PSS names it in its parameters, and must use MGF1 with the same hash and a
    /// salt as long as it (RFC 4055 §3.1), the profile .NET verifies.
    /// </summary>
    private static HashAlgorithmName? Hash(string oid, ReadOnlySpan<byte> parameters)
    {
        switch (oid)
        {
            case "1.2.840.113549.1.1.11" or "1.2.840.10045.4.3.2":
                return HashAlgorithmName.SHA256;
            case "1.2.840.113549.1.1.12" or "1.2.840.10045.4.3.3":
                return HashAlgorithmName.SHA384;
            case "1.2.840.113549.1.1.13" or "1.2.840.10045.4.3.4":
                return HashAlgorithmName.SHA512;
            case RsaPss:
                var pss = new AsnReader(parameters.ToArray(), AsnEncodingRules.DER).ReadSequence();
                var hash = Digest(pss.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0)).ReadSequence().ReadObjectIdentifier());
                var maskGeneration = pss.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 1)).ReadSequence();
                var mgf1 = maskGeneration.ReadObjectIdentifier() == "1.2.840.113549.1.1.8"
                    && Digest(maskGeneration.ReadSequence().ReadObjectIdentifier()) == hash;
                var saltLength = pss.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 2)).ReadInteger();
                return hash is { } name && mgf1 && saltLength == HashLength(name) ? name : null;
            default:
                return null;
        }
    }

    private static HashAlgorithmName? Digest(string oid) => oid switch
    {
        "2.16.840.1.101.3.4.2.1" => HashAlgorithmName.SHA256,
        "2.16.840.1.101.3.4.2.2" => HashAlgorithmName.SHA384,
        "2.16.840.1.101.3.4.2.3" => HashAlgorithmName.SHA512,
        _ => null,
    };

    private static int HashLength(HashAlgorithmName hash) =>
        hash == HashAlgorithmName.SHA256 ? 32 : hash == HashAlgorithmName.SHA384 ? 48 : 64;
}
