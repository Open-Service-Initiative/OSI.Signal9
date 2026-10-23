using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.DependencyInjection;

namespace OSI.Signal9.Server.Identity;

/// <summary>
/// The one seam for sender-constrained tokens (ADR-0004): which proofs of possession a client can present, and how
/// tokens are bound to them. Today that's mTLS (RFC 8705): the token endpoint authenticates clients by their
/// certificate and binds access tokens to it with <c>cnf.x5t#S256</c>, and <c>api</c>'s OpenIddict validation rejects a
/// bound token presented without that certificate (its built-in <c>ValidateProofOfPossession</c> handler). DPoP
/// (RFC 9449) would be added here, beside mTLS, once OpenIddict supports it.
/// </summary>
public static class ProofOfPossession
{
    public static OpenIddictServerBuilder UseSignal9ProofOfPossession(this OpenIddictServerBuilder server,
        IdentityOptions identity)
    {
        // self_signed_tls_client_auth: the certificate must be one registered in the client's key set.
        server.EnableSelfSignedTlsClientAuthentication();

        // tls_client_auth: only when an operator trusts a certificate authority for client certificates.
        if (identity.TlsClientCertificateAuthorities is [_, ..] authorities)
        {
            var certificates = new X509Certificate2Collection();
            foreach (var pem in authorities)
                certificates.ImportFromPem(pem);
            server.EnablePublicKeyInfrastructureTlsClientAuthentication(certificates);
        }

        // Access tokens issued over mTLS are always bound to the certificate. Refresh tokens are bound to it only for
        // public clients: a confidential client's refresh token is already bound by client authentication
        // (RFC 8705 §4, RFC 9700 §4.14.2), which OpenIddict enforces.
        return server.UseClientCertificateBoundAccessTokens().UseClientCertificateBoundRefreshTokens();
    }
}
