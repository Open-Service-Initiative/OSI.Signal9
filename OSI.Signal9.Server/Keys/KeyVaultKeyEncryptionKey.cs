using System.Security.Cryptography;
using Azure.Core;
using Azure.Security.KeyVault.Keys.Cryptography;

namespace OSI.Signal9.Server.Keys;

/// <summary>
/// A key-encryption key held in Azure Key Vault, reached by managed identity (stories 124, 127). Its private key
/// never leaves the vault: data-encryption keys are wrapped and unwrapped there with RSA-OAEP-256. Each sealed
/// secret records the key version that wrapped it, so a new version in the vault doesn't strand older secrets.
/// </summary>
public sealed class KeyVaultKeyEncryptionKey : KeyEncryptionKey
{
    private readonly Uri _keyId;
    private readonly CryptographyClient _client;

    /// <param name="keyId">The key's identifier without a version: <c>https://{vault}/keys/{name}</c>.</param>
    public KeyVaultKeyEncryptionKey(Uri keyId, TokenCredential credential)
    {
        if (keyId.Scheme != Uri.UriSchemeHttps || keyId.Segments is not ["/", "keys/", var name] || name.EndsWith('/'))
            throw new InvalidOperationException(
                $"KeyEncryption:KeyVault:KeyId must be an https key identifier without a version, such as https://{{vault}}.vault.azure.net/keys/{{name}}; it's {keyId}.");

        _keyId = keyId;
        Credential = credential;
        _client = new CryptographyClient(keyId, credential);
    }

    /// <summary>How this host signs in to Key Vault.</summary>
    public TokenCredential Credential { get; }

    protected override (string KekId, byte[] WrappedKey) WrapKey(byte[] dataKey)
    {
        var result = _client.WrapKey(KeyWrapAlgorithm.RsaOaep256, dataKey);
        return (result.KeyId, result.EncryptedKey);
    }

    protected override byte[] UnwrapKey(string kekId, byte[] wrappedKey)
    {
        // The identifier comes from the database. Only ever send it, with this host's token, to the configured key.
        if (!Uri.TryCreate(kekId, UriKind.Absolute, out var version) || !IsVersionOfConfiguredKey(version))
            throw new CryptographicException(
                $"The secret was sealed by key-encryption key {kekId}, which isn't a version of {_keyId}.");

        return new CryptographyClient(version, Credential).UnwrapKey(KeyWrapAlgorithm.RsaOaep256, wrappedKey).Key;
    }

    private bool IsVersionOfConfiguredKey(Uri version) =>
        version.Scheme == Uri.UriSchemeHttps
        && version.IsDefaultPort == _keyId.IsDefaultPort && version.Port == _keyId.Port
        && string.Equals(version.Host, _keyId.Host, StringComparison.OrdinalIgnoreCase)
        && version.Segments.Length == 4 && !version.Segments[3].EndsWith('/')
        && string.Equals(string.Concat(version.Segments[..3]), _keyId.AbsolutePath + "/", StringComparison.OrdinalIgnoreCase)
        && string.IsNullOrEmpty(version.Query) && string.IsNullOrEmpty(version.Fragment);
}
