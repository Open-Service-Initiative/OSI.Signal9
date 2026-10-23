using System.ComponentModel.DataAnnotations;

namespace OSI.Signal9.Server.Keys;

/// <summary>Where the key-encryption key lives (ADR-0008, stories 124-127).</summary>
public enum KeyEncryptionProvider
{
    /// <summary>A certificate with an RSA private key, for on-prem and local development.</summary>
    Certificate,

    /// <summary>An Azure Key Vault RSA key, reached by managed identity.</summary>
    KeyVault,
}

/// <summary>The <c>KeyEncryption</c> section: which key-encryption key wraps every key Signal9 stores.</summary>
public sealed class KeyEncryptionOptions
{
    public const string SectionName = "KeyEncryption";

    [Required]
    public KeyEncryptionProvider? Provider { get; set; }

    public CertificateKeyOptions Certificate { get; set; } = new();

    public KeyVaultKeyOptions KeyVault { get; set; } = new();
}

/// <summary>
/// A certificate file, as Kestrel reads one: a PKCS#12 file (with an optional password), or a PEM certificate with
/// its PEM private key in <see cref="KeyPath"/>.
/// </summary>
public sealed class CertificateKeyOptions
{
    public string? Path { get; set; }

    public string? KeyPath { get; set; }

    public string? Password { get; set; }
}

public sealed class KeyVaultKeyOptions
{
    /// <summary>The key's identifier without a version, such as <c>https://{vault}.vault.azure.net/keys/{name}</c>.</summary>
    public Uri? KeyId { get; set; }

    /// <summary>The client ID of a user-assigned managed identity, or none for the system-assigned identity.</summary>
    public string? ManagedIdentityClientId { get; set; }
}
