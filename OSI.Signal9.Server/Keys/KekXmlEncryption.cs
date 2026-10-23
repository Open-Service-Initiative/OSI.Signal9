using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;
using Microsoft.Extensions.DependencyInjection;

namespace OSI.Signal9.Server.Keys;

/// <summary>Wraps each Data Protection key with the key-encryption key before it's stored (ADR-0008).</summary>
internal sealed class KekXmlEncryptor(KeyEncryptionKey kek) : IXmlEncryptor
{
    internal const string Context = "signal9/data-protection";

    public EncryptedXmlInfo Encrypt(XElement plaintextElement)
    {
        var plaintext = Encoding.UTF8.GetBytes(plaintextElement.ToString(SaveOptions.DisableFormatting));
        try
        {
            var secret = kek.Seal(plaintext, Context);
            var element = new XElement("sealedSecret",
                new XAttribute("kekId", secret.KekId),
                new XElement("wrappedKey", Convert.ToBase64String(secret.WrappedKey)),
                new XElement("ciphertext", Convert.ToBase64String(secret.Ciphertext)));
            return new EncryptedXmlInfo(element, typeof(KekXmlDecryptor));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }
}

/// <summary>Unwraps Data Protection keys sealed by <see cref="KekXmlEncryptor"/>. Data Protection creates it by type.</summary>
internal sealed class KekXmlDecryptor(IServiceProvider services) : IXmlDecryptor
{
    public XElement Decrypt(XElement encryptedElement)
    {
        var secret = new SealedSecret(
            (string?)encryptedElement.Attribute("kekId") ?? throw new CryptographicException("The sealed key has no kekId."),
            Convert.FromBase64String((string?)encryptedElement.Element("wrappedKey") ?? ""),
            Convert.FromBase64String((string?)encryptedElement.Element("ciphertext") ?? ""));
        var plaintext = services.GetRequiredService<KeyEncryptionKey>().Open(secret, KekXmlEncryptor.Context);
        try
        {
            return XElement.Parse(Encoding.UTF8.GetString(plaintext));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }
}
