using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace OSI.Signal9.API.Tests;

/// <summary>Client certificates for mTLS tests: self-signed ones and ones issued by a test certificate authority.</summary>
internal static class TestCertificates
{
    private static readonly Oid ClientAuthentication = new("1.3.6.1.5.5.7.3.2");

    // X.509 chain building checks validity against the real clock, not the test's FakeTimeProvider.
    private static DateTimeOffset Now => DateTimeOffset.UtcNow;

    /// <summary>The test certificate authority, trusted by the fixture for <c>tls_client_auth</c>.</summary>
    public static X509Certificate2 Authority { get; } = CreateAuthority();

    /// <summary>The authority's certificate without its private key, as the hosts are configured with it.</summary>
    public static string AuthorityPem { get; } = Authority.ExportCertificatePem();

    /// <summary>A self-signed client certificate, as an agent or an API application generates for itself.</summary>
    public static X509Certificate2 SelfSigned(string commonName = "self-signed-client")
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={commonName}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        AddClientExtensions(request);
        return request.CreateSelfSigned(Now.AddMinutes(-5), Now.AddDays(30));
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
