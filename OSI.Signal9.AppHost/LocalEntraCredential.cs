using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

/// <summary>
/// The Entra app registration's credential for local development: a certificate in place of the managed identity's
/// federated credential (story 125). It's created once per developer, outside the repository; upload the public
/// certificate (<c>entra-credential.cer</c>, next to it) to the app registration's certificates.
/// </summary>
internal static class LocalEntraCredential
{
    /// <summary>Returns the path of the local credential certificate (PKCS#12), creating it on first use.</summary>
    public static string EnsureCertificate()
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Signal9");
        var path = Path.Combine(directory, "entra-credential.pfx");
        if (File.Exists(path))
            return path;

        Directory.CreateDirectory(directory);
        using var rsa = RSA.Create(3072);
        var request = new CertificateRequest("CN=Signal9 local Entra credential", rsa, HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        // Entra accepts certificate credentials for up to two years.
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(2));

        // Readable by this user only, like an SSH key.
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var file = new FileStream(path, options))
            file.Write(certificate.Export(X509ContentType.Pfx));
        File.WriteAllText(Path.ChangeExtension(path, ".cer"), certificate.ExportCertificatePem());
        return path;
    }
}
