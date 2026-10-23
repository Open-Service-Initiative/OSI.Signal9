using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace OSI.Signal9.Server.Keys;

/// <summary>
/// A key-encryption key held as a certificate's RSA key, for on-prem and local development (story 125). Data
/// encryption keys are wrapped with RSA-OAEP and SHA-256.
/// </summary>
public sealed class CertificateKeyEncryptionKey : KeyEncryptionKey, IDisposable
{
    /// <summary>
    /// RSA-3072 gives 128-bit security, which NIST SP 800-57 Part 1 requires for keys protecting data beyond 2030.
    /// </summary>
    public const int MinimumKeySize = 3072;

    private readonly X509Certificate2 _certificate;
    private readonly RSA _rsa;

    public CertificateKeyEncryptionKey(X509Certificate2 certificate)
    {
        _rsa = certificate.GetRSAPrivateKey()
            ?? throw new InvalidOperationException(
                $"The key-encryption certificate {certificate.Subject} has no RSA private key.");
        if (_rsa.KeySize is var keySize && keySize < MinimumKeySize)
        {
            _rsa.Dispose();
            throw new InvalidOperationException(
                $"The key-encryption certificate's RSA key is {keySize} bits; at least {MinimumKeySize} are required.");
        }

        _certificate = certificate;
        Id = "x509:" + certificate.GetCertHashString(HashAlgorithmName.SHA256);
    }

    /// <summary>Loads the certificate named by <paramref name="options"/>, as a PKCS#12 file or PEM files.</summary>
    public static CertificateKeyEncryptionKey Load(CertificateKeyOptions options)
    {
        if (string.IsNullOrEmpty(options.Path))
            throw new InvalidOperationException("KeyEncryption:Certificate:Path is required for the Certificate provider.");

        var certificate = options.KeyPath is { Length: > 0 } keyPath
            ? options.Password is { Length: > 0 } password
                ? X509Certificate2.CreateFromEncryptedPemFile(options.Path, password, keyPath)
                : X509Certificate2.CreateFromPemFile(options.Path, keyPath)
            : X509CertificateLoader.LoadPkcs12FromFile(options.Path, options.Password);
        try
        {
            return new CertificateKeyEncryptionKey(certificate);
        }
        catch
        {
            certificate.Dispose();
            throw;
        }
    }

    /// <summary>Names this key-encryption key in each sealed secret: the certificate's SHA-256 thumbprint.</summary>
    public string Id { get; }

    protected override (string KekId, byte[] WrappedKey) WrapKey(byte[] dataKey) =>
        (Id, _rsa.Encrypt(dataKey, RSAEncryptionPadding.OaepSHA256));

    protected override byte[] UnwrapKey(string kekId, byte[] wrappedKey)
    {
        if (kekId != Id)
            throw new CryptographicException(
                $"The secret was sealed by key-encryption key {kekId}, but this host's is {Id}.");
        return _rsa.Decrypt(wrappedKey, RSAEncryptionPadding.OaepSHA256);
    }

    public void Dispose()
    {
        _rsa.Dispose();
        _certificate.Dispose();
    }
}
