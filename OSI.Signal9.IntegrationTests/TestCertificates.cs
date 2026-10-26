using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace OSI.Signal9.IntegrationTests;

/// <summary>The key kinds a test agent certificate can be made with.</summary>
public enum AgentKey { EcdsaP256, EcdsaP384, Rsa3072, Rsa2048, EcdsaBrainpoolP256 }

/// <summary>Client certificates for mTLS tests: self-signed ones and ones issued by a test certificate authority.</summary>
internal static class TestCertificates
{
    private static readonly Oid ClientAuthentication = new("1.3.6.1.5.5.7.3.2");

    // Chain building checks validity on the hosts' clock, which the fixture starts at the real time.
    private static DateTimeOffset Now => DateTimeOffset.UtcNow;

    /// <summary>The test certificate authority, trusted by the fixture for <c>tls_client_auth</c>.</summary>
    public static X509Certificate2 Authority { get; } = CreateAuthority();

    /// <summary>The authority's certificate without its private key, as the hosts are configured with it.</summary>
    public static string AuthorityPem { get; } = Authority.ExportCertificatePem();

    /// <summary>A self-signed client certificate, as an agent or an API application generates for itself.</summary>
    public static X509Certificate2 SelfSigned(string commonName = "self-signed-client")
    {
        using var key = RSA.Create(3072);
        var request = new CertificateRequest($"CN={commonName}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        AddClientExtensions(request);
        return request.CreateSelfSigned(Now.AddMinutes(-5), Now.AddDays(30));
    }

    /// <summary>
    /// A self-signed certificate as an agent makes for its new key at enrollment: ECDSA P-256 by default (a TPM's
    /// usual key), valid for a year, with the digitalSignature key usage and the clientAuth EKU. Each part can be
    /// varied to make one an enrollment must refuse.
    /// </summary>
    public static X509Certificate2 AgentCertificate(AgentKey key = AgentKey.EcdsaP256, bool clientAuth = true,
        bool digitalSignature = true, bool certificateAuthority = false, DateTimeOffset? notBefore = null,
        DateTimeOffset? notAfter = null, string commonName = "Signal9 agent")
    {
        using AsymmetricAlgorithm algorithm = key switch
        {
            AgentKey.EcdsaP256 => ECDsa.Create(ECCurve.NamedCurves.nistP256),
            AgentKey.EcdsaP384 => ECDsa.Create(ECCurve.NamedCurves.nistP384),
            AgentKey.EcdsaBrainpoolP256 => ECDsa.Create(ECCurve.NamedCurves.brainpoolP256r1),
            AgentKey.Rsa3072 => RSA.Create(3072),
            _ => RSA.Create(2048),
        };
        var request = algorithm is ECDsa ecdsa
            ? new CertificateRequest($"CN={commonName}", ecdsa, HashAlgorithmName.SHA256)
            : new CertificateRequest($"CN={commonName}", (RSA)algorithm, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            digitalSignature ? X509KeyUsageFlags.DigitalSignature : X509KeyUsageFlags.KeyEncipherment, critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [clientAuth ? ClientAuthentication : new Oid("1.3.6.1.5.5.7.3.1")], critical: false));
        if (certificateAuthority)
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, critical: true));
        return request.CreateSelfSigned(notBefore ?? Now.AddMinutes(-5), notAfter ?? Now.AddDays(365));
    }

    /// <summary>
    /// An otherwise acceptable agent certificate (RSA 3072) signed with SHA-1, which .NET won't make itself: the signature
    /// generator names sha1WithRSAEncryption and signs with SHA-1.
    /// </summary>
    public static X509Certificate2 Sha1SignedCertificate()
    {
        using var key = RSA.Create(3072);
        var request = new CertificateRequest("CN=Signal9 agent", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        AddClientExtensions(request);
        return request.Create(request.SubjectName, new Sha1RsaSignatureGenerator(key), Now.AddMinutes(-5), Now.AddDays(30),
            RandomNumberGenerator.GetBytes(16));
    }

    private sealed class Sha1RsaSignatureGenerator(RSA key) : X509SignatureGenerator
    {
        // AlgorithmIdentifier { sha1WithRSAEncryption, NULL }.
        public override byte[] GetSignatureAlgorithmIdentifier(HashAlgorithmName hashAlgorithm) =>
            [0x30, 0x0D, 0x06, 0x09, 0x2A, 0x86, 0x48, 0x86, 0xF7, 0x0D, 0x01, 0x01, 0x05, 0x05, 0x00];

        public override byte[] SignData(byte[] data, HashAlgorithmName hashAlgorithm) =>
            key.SignData(data, HashAlgorithmName.SHA1, RSASignaturePadding.Pkcs1);

        protected override PublicKey BuildPublicKey() => CreateForRSA(key, RSASignaturePadding.Pkcs1).PublicKey;
    }

    /// <summary>A copy of <paramref name="certificate"/> whose signature no longer verifies: its last byte is changed.</summary>
    public static X509Certificate2 WithBrokenSignature(X509Certificate2 certificate)
    {
        var der = certificate.RawData;
        der[^1] ^= 0x01;
        return X509CertificateLoader.LoadCertificate(der);
    }

    /// <summary>
    /// A client certificate issued by <see cref="Authority"/>, with one subject alternative name of each kind
    /// RFC 8705 §2.1.2 can register: DNS name, URI, IP address and email.
    /// </summary>
    public static X509Certificate2 IssuedByAuthority(string subject, string dnsName, string uri, string ipAddress,
        string email)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = ClientRequest(subject, key);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(dnsName);
        san.AddUri(new Uri(uri));
        san.AddIpAddress(IPAddress.Parse(ipAddress));
        san.AddEmailAddress(email);
        request.CertificateExtensions.Add(san.Build());

        var serial = RandomNumberGenerator.GetBytes(16);
        using var issued = request.Create(Authority, Now.AddMinutes(-5), Now.AddDays(30), serial);
        return issued.CopyWithPrivateKey(key);
    }

    /// <summary>A client certificate issued by a certificate authority the hosts don't trust.</summary>
    public static X509Certificate2 SelfSignedAuthorityIssued(string subject)
    {
        using var authority = CreateAuthority();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = ClientRequest(subject, key);
        using var issued = request.Create(authority, Now.AddMinutes(-5), Now.AddDays(30), RandomNumberGenerator.GetBytes(16));
        return issued.CopyWithPrivateKey(key);
    }

    /// <summary>A TLS server certificate for <c>localhost</c>, for tests that run Kestrel.</summary>
    public static X509Certificate2 Server()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        using var certificate = request.CreateSelfSigned(Now.AddMinutes(-5), Now.AddDays(1));
        return WithUsableKey(certificate);
    }

    /// <summary>
    /// The certificate with a private key SslStream can use: on Linux, an ephemeral key has to round-trip through
    /// PKCS#12 first.
    /// </summary>
    public static X509Certificate2 WithUsableKey(X509Certificate2 certificate) =>
        X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pfx), null);

    /// <summary>
    /// The <c>X-Forwarded-Client-Cert</c> header as Container Apps ingress (Envoy) sends it:
    /// <c>Hash=…;Cert="…";Chain="…";</c>. Envoy documents the PEM as URL-encoded; Container Apps' own sample shows it
    /// unencoded, so tests cover both.
    /// </summary>
    public static string ContainerAppsHeader(X509Certificate2 certificate, bool urlEncoded = true)
    {
        var pem = certificate.ExportCertificatePem();
        var field = urlEncoded ? Uri.EscapeDataString(pem) : pem.ReplaceLineEndings(string.Empty);
        var hash = Convert.ToHexStringLower(SHA256.HashData(certificate.RawData));
        return $"Hash={hash};Cert=\"{field}\";Chain=\"{field}\";";
    }

    private static CertificateRequest ClientRequest(string subject, ECDsa key)
    {
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256);
        AddClientExtensions(request);
        return request;
    }

    private static void AddClientExtensions(CertificateRequest request)
    {
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([ClientAuthentication], critical: false));
    }

    private static X509Certificate2 CreateAuthority()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=Signal9 Test CA", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, critical: true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, critical: true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, critical: false));
        return request.CreateSelfSigned(Now.AddDays(-1), Now.AddYears(1));
    }
}
