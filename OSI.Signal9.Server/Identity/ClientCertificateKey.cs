using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.IdentityModel.Tokens;

namespace OSI.Signal9.Server.Identity;

/// <summary>
/// A client certificate as a JWK in a client's key set, the way <c>self_signed_tls_client_auth</c> registers it
/// (RFC 8705 §2.2): the key's own parameters with the certificate in <c>x5c</c> (RFC 7517 §4.7). Agents and API
/// applications both register their certificates this way.
/// </summary>
public static class ClientCertificateKey
{
    /// <summary>
    /// The certificate's JWK: <c>kty</c> RSA with <c>n</c> and <c>e</c>, or EC with <c>crv</c>, <c>x</c> and <c>y</c>
    /// (RFC 7518 §6), plus <c>x5c</c>, and its SHA-256 thumbprint as <c>kid</c>.
    /// </summary>
    /// <exception cref="CryptographicException">The key is neither RSA (2048 bits or more) nor a NIST P-curve EC key.</exception>
    public static JsonWebKey From(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        var key = ApiApplicationClient.PublicKeyJwk(Guid.Empty, certificate.PublicKey.ExportSubjectPublicKeyInfo());
        key.Kid = ApiApplicationClient.Thumbprint(certificate.RawData);
        key.X5c.Add(Convert.ToBase64String(certificate.RawData));
        return key;
    }

    /// <summary>
    /// The certificate a signing JWK registers: the first in its <c>x5c</c>, when that certificate's key is the JWK's
    /// own (RFC 7517 §4.7), so every key parameter the JWK states must be the certificate's. Null for any other key.
    /// </summary>
    public static X509Certificate2? Certificate(JsonWebKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Use is not (JsonWebKeyUseNames.Sig or null or { Length: 0 }) || key.X5c is not [var encoded, ..])
            return null;

        X509Certificate2 certificate;
        try
        {
            certificate = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(encoded));
        }
        catch (Exception exception) when (exception is FormatException or CryptographicException)
        {
            return null;
        }

        try
        {
            var own = ApiApplicationClient.PublicKeyJwk(Guid.Empty, certificate.PublicKey.ExportSubjectPublicKeyInfo());
            if (key.Kty == own.Kty && Stated(key.N, own.N) && Stated(key.E, own.E) && Stated(key.Crv, own.Crv)
                && Stated(key.X, own.X) && Stated(key.Y, own.Y))
                return certificate;
        }
        catch (CryptographicException)
        {
        }
        certificate.Dispose();
        return null;
    }

    private static bool Stated(string? stated, string? actual) => stated is null || stated == actual;
}
