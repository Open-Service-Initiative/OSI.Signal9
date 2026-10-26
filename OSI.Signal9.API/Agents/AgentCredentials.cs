using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using OSI.Signal9.Server.Identity;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace OSI.Signal9.API.Agents;

/// <summary>
/// An agent's credential (ADR-0005, stories 98-100): the self-signed certificate of its non-exportable key, registered
/// as its OAuth client for <c>self_signed_tls_client_auth</c> (RFC 8705 §2.2). Its client ID is the agent's ID, and it
/// may only obtain <c>agent</c> tokens with client credentials, which the mTLS token endpoint binds to the certificate.
/// </summary>
internal static class AgentCredentials
{
    /// <summary>
    /// The agent's OAuth client: confidential, authenticating only with <paramref name="certificate"/>, which is its key
    /// set's one key, and allowed only client credentials for the <c>agent</c> scope. No refresh tokens.
    /// </summary>
    public static OpenIddictApplicationDescriptor Client(Guid agentId, X509Certificate2 certificate) => new()
    {
        ClientId = agentId.ToString(),
        ClientType = ClientTypes.Confidential,
        DisplayName = $"Agent {agentId}",
        JsonWebKeySet = new JsonWebKeySet { Keys = { ClientCertificateKey.From(certificate) } },
        Permissions =
        {
            Permissions.Endpoints.Token,
            Permissions.GrantTypes.ClientCredentials,
            Permissions.Prefixes.Scope + Server.Identity.Scopes.Agent,
        },
    };

    /// <summary>
    /// The enrolled agent an access token belongs to: one with the <c>agent</c> scope, bound to a certificate
    /// (<c>cnf</c>), that the agent's own client obtained acting as itself. Null for any other token.
    /// </summary>
    public static Guid? AgentId(this ClaimsPrincipal user) =>
        user.HasScope(Server.Identity.Scopes.Agent)
        && user.HasClaim(c => c.Type == Claims.Confirmation)
        && user.GetClaim(Claims.Subject) is { } subject
        && subject == user.GetClaim(Claims.ClientId)
        && Guid.TryParse(subject, out var agentId)
            ? agentId
            : null;
}
