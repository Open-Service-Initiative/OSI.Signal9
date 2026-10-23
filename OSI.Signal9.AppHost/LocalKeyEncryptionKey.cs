using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

/// <summary>
/// The key-encryption key for local development: a certificate in place of Key Vault (story 125). It's created once
/// per developer and kept outside the repository, because the keys in the local database are wrapped by it.
/// </summary>
internal static class LocalKeyEncryptionKey
{
    /// <summary>Returns the path of the local key-encryption certificate (PKCS#12), creating it on first use.</summary>
    public static string EnsureCertificate()
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Signal9");
        var path = Path.Combine(directory, "local-kek.pfx");
        if (File.Exists(path))
            return path;

        Directory.CreateDirectory(directory);
        using var rsa = RSA.Create(3072);
        var request = new CertificateRequest("CN=Signal9 local key-encryption key", rsa, HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyEncipherment, critical: true));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));

        // Readable by this user only, like an SSH key.
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var file = new FileStream(path, options))
            file.Write(certificate.Export(X509ContentType.Pfx));
        return path;
    }
}
