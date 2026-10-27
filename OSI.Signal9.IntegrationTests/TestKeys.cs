using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace OSI.Signal9.IntegrationTests;

/// <summary>
/// Key-encryption key certificates for the test run, written to PKCS#12 files the hosts load by path, as an on-prem
/// or local deployment would (ADR-0008).
/// </summary>
internal static class TestKeys
{
    /// <summary>The key-encryption key every host in the run shares unless a test asks for another.</summary>
    public static string KekPath { get; } = CreateKek();

    /// <summary>Writes a new key-encryption key certificate and returns its path.</summary>
    public static string CreateKek()
    {
        using var rsa = RSA.Create(3072);
        var request = new CertificateRequest("CN=Signal9 test key-encryption key", rsa, HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyEncipherment, critical: true));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));

        var path = Path.Combine(Path.GetTempPath(), $"signal9-test-kek-{Guid.NewGuid():N}.pfx");
        File.WriteAllBytes(path, certificate.Export(X509ContentType.Pfx));
        return path;
    }
}
