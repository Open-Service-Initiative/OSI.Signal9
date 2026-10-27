using System.Security.Cryptography;
using System.Text;

namespace OSI.Signal9.Server.Keys;

/// <summary>
/// A secret sealed under the key-encryption key: <see cref="Ciphertext"/> is the secret encrypted with AES-256-GCM
/// under a fresh data-encryption key, and <see cref="WrappedKey"/> is that data-encryption key wrapped by the
/// key-encryption key identified by <see cref="KekId"/>.
/// </summary>
/// <param name="Ciphertext">The 12-byte nonce, the ciphertext and the 16-byte tag.</param>
public sealed record SealedSecret(string KekId, byte[] WrappedKey, byte[] Ciphertext);

/// <summary>
/// The single root of trust for every key Signal9 stores (ADR-0008, story 127). Each secret gets its own
/// data-encryption key, so the key-encryption key only ever wraps 32 random bytes, which also fits RSA-OAEP's size
/// limit. The context is authenticated, so a sealed secret can't be moved to another record.
/// </summary>
public abstract class KeyEncryptionKey
{
    private const int DataKeySize = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;

    /// <summary>Wraps a data-encryption key, returning the identifier of the key-encryption key that wrapped it.</summary>
    protected abstract (string KekId, byte[] WrappedKey) WrapKey(byte[] dataKey);

    /// <summary>Unwraps a data-encryption key with the key-encryption key that <paramref name="kekId"/> names.</summary>
    /// <exception cref="CryptographicException">That key isn't this key-encryption key, or unwrapping fails.</exception>
    protected abstract byte[] UnwrapKey(string kekId, byte[] wrappedKey);

    public SealedSecret Seal(ReadOnlySpan<byte> plaintext, string context)
    {
        var dataKey = RandomNumberGenerator.GetBytes(DataKeySize);
        try
        {
            var ciphertext = new byte[NonceSize + plaintext.Length + TagSize];
            var nonce = ciphertext.AsSpan(0, NonceSize);
            RandomNumberGenerator.Fill(nonce);
            using (var aes = new AesGcm(dataKey, TagSize))
            {
                aes.Encrypt(nonce, plaintext, ciphertext.AsSpan(NonceSize, plaintext.Length),
                    ciphertext.AsSpan(NonceSize + plaintext.Length), Encoding.UTF8.GetBytes(context));
            }

            var (kekId, wrappedKey) = WrapKey(dataKey);
            return new SealedSecret(kekId, wrappedKey, ciphertext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKey);
        }
    }

    /// <exception cref="CryptographicException">
    /// The secret was sealed by another key-encryption key, for another context, or has been tampered with.
    /// </exception>
    public byte[] Open(SealedSecret secret, string context)
    {
        if (secret.Ciphertext.Length < NonceSize + TagSize)
            throw new CryptographicException("The sealed secret is too short.");

        var dataKey = UnwrapKey(secret.KekId, secret.WrappedKey);
        try
        {
            var ciphertext = secret.Ciphertext.AsSpan();
            var plaintextLength = ciphertext.Length - NonceSize - TagSize;
            var plaintext = new byte[plaintextLength];
            using var aes = new AesGcm(dataKey, TagSize);
            aes.Decrypt(ciphertext[..NonceSize], ciphertext.Slice(NonceSize, plaintextLength),
                ciphertext[(NonceSize + plaintextLength)..], plaintext, Encoding.UTF8.GetBytes(context));
            return plaintext;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKey);
        }
    }
}
