using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using OSI.Signal9.Contracts.ApiApplications;
using OSI.Signal9.Server.Data;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace OSI.Signal9.Server.Identity;

/// <summary>
/// An API application's OAuth client, as the OpenIddict application's properties hold it (#124): its fixed RFC 7591
/// <c>token_endpoint_auth_method</c> and its credentials. Certificates and public keys become the client's key set;
/// secrets are kept only as hashes, each with its expiry.
/// </summary>
public static class ApiApplicationClient
{
    /// <summary>The RFC 7591 metadata name of the method, fixed at registration.</summary>
    public const string AuthMethodProperty = "token_endpoint_auth_method";

    public const string CredentialsProperty = "signal9_credentials";

    /// <summary>The API application's ID and tenant, so auth can name them in consent's audit entries.</summary>
    public const string ApplicationIdProperty = "signal9_api_application_id";

    public const string TenantIdProperty = "signal9_tenant_id";

    /// <summary>Whether its authorization requests must carry an OpenID Connect nonce (story 86).</summary>
    public const string RequireNonceProperty = "signal9_require_nonce";

    /// <summary>
    /// Describes <paramref name="application"/>'s OAuth client: always confidential (public clients are deferred), and
    /// for a delegated application, authorization code at its redirect URIs with optional PAR, refresh tokens, PKCE
    /// unless it was turned off for nonce, and explicit consent, which nothing can turn off (no admin consent). Its
    /// method, credentials and certificate subject are set separately.
    /// </summary>
    public static void Describe(OpenIddictApplicationDescriptor descriptor, ApiApplication application)
    {
        descriptor.ClientId = application.ClientId;
        descriptor.ClientType = ClientTypes.Confidential;
        descriptor.ApplicationType = ApplicationTypes.Web;
        descriptor.DisplayName = application.DisplayName;
        descriptor.ConsentType = ConsentTypes.Explicit;
        descriptor.Permissions.Clear();
        descriptor.Permissions.UnionWith([Permissions.Endpoints.Token, Permissions.Endpoints.Revocation]);
        descriptor.RedirectUris.Clear();
        descriptor.Requirements.Clear();
        if (application.Kind == ApiApplicationKinds.Delegated)
        {
            descriptor.Permissions.UnionWith([
                Permissions.Endpoints.Authorization, Permissions.Endpoints.PushedAuthorization,
                Permissions.GrantTypes.AuthorizationCode, Permissions.GrantTypes.RefreshToken, Permissions.ResponseTypes.Code,
            ]);
            foreach (var uri in application.RedirectUris)
                descriptor.RedirectUris.Add(new Uri(uri));
            if (application.RequirePkce)
                descriptor.Requirements.Add(Requirements.Features.ProofKeyForCodeExchange);
        }
        else
        {
            descriptor.Permissions.Add(Permissions.GrantTypes.ClientCredentials);
        }
        foreach (var scope in application.Scopes)
            descriptor.Permissions.Add(Permissions.Prefixes.Scope + scope);
        descriptor.Properties[ApplicationIdProperty] = JsonSerializer.SerializeToElement(application.Id);
        descriptor.Properties[TenantIdProperty] = JsonSerializer.SerializeToElement(application.TenantId);
        descriptor.Properties[RequireNonceProperty] = JsonSerializer.SerializeToElement(application.RequireNonce);
    }

    /// <summary>The API application's ID and tenant, or null for a client that isn't one.</summary>
    public static (Guid Id, Guid TenantId)? Owner(ImmutableDictionary<string, JsonElement> properties) =>
        properties.TryGetValue(ApplicationIdProperty, out var id) && properties.TryGetValue(TenantIdProperty, out var tenant)
            ? (id.GetGuid(), tenant.GetGuid())
            : null;

    /// <summary>Whether the client's authorization requests must carry a nonce.</summary>
    public static bool RequiresNonce(ImmutableDictionary<string, JsonElement> properties) =>
        properties.TryGetValue(RequireNonceProperty, out var value) && value.ValueKind == JsonValueKind.True;

    /// <summary>A credential as stored: one of a certificate, a public key or a secret's hash.</summary>
    public sealed record Credential(
        Guid Id,
        string Type,
        DateTimeOffset CreatedAt,
        DateTimeOffset? ExpiresAt,
        string? Thumbprint,
        string? Subject,
        string? Certificate = null,
        string? PublicKey = null,
        string? SecretHash = null);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The registered method, or null for a client that isn't an API application (agents, web, tests).</summary>
    public static string? AuthMethod(ImmutableDictionary<string, JsonElement> properties) =>
        properties.TryGetValue(AuthMethodProperty, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public static IReadOnlyList<Credential> Credentials(ImmutableDictionary<string, JsonElement> properties) =>
        properties.TryGetValue(CredentialsProperty, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.Deserialize<List<Credential>>(Json) ?? []
            : [];

    public static JsonElement Serialize(IEnumerable<Credential> credentials) =>
        JsonSerializer.SerializeToElement(credentials.ToList(), Json);

    /// <summary>
    /// The key set OpenIddict verifies the client with: its certificates (with <c>x5c</c>, for
    /// <c>self_signed_tls_client_auth</c>) and public keys (for <c>private_key_jwt</c>), each identified by credential ID.
    /// Certificates become JWKs with <see cref="ClientCertificateKey.From"/>, which keeps an ECDSA certificate's
    /// <c>x5c</c> (#108).
    /// </summary>
    public static JsonWebKeySet KeySet(IEnumerable<Credential> credentials)
    {
        var set = new JsonWebKeySet();
        foreach (var credential in credentials)
        {
            if (credential.Certificate is { } der)
            {
                using var certificate = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(der));
                var key = ClientCertificateKey.From(certificate);
                key.Kid = credential.Id.ToString("D");
                set.Keys.Add(key);
            }
            else if (credential.PublicKey is { } spki)
            {
                set.Keys.Add(PublicKeyJwk(credential.Id, Convert.FromBase64String(spki)));
            }
        }
        return set;
    }

    /// <summary>The base64url SHA-256 of a certificate (RFC 8705 §3.1) or of a public key's SubjectPublicKeyInfo.</summary>
    public static string Thumbprint(byte[] der) => Base64UrlEncoder.Encode(SHA256.HashData(der));

    /// <summary>A JWK of an RSA or EC public key, given as SubjectPublicKeyInfo.</summary>
    /// <exception cref="CryptographicException">The key is neither RSA nor a NIST P-curve EC key.</exception>
    public static JsonWebKey PublicKeyJwk(Guid id, byte[] subjectPublicKeyInfo)
    {
        var key = new JsonWebKey { Kid = id.ToString("D"), Use = JsonWebKeyUseNames.Sig };
        try
        {
            using var rsa = RSA.Create();
            rsa.ImportSubjectPublicKeyInfo(subjectPublicKeyInfo, out _);
            if (rsa.KeySize < 2048)
                throw new CryptographicException("RSA keys must be at least 2048 bits.");
            var parameters = rsa.ExportParameters(false);
            key.Kty = JsonWebAlgorithmsKeyTypes.RSA;
            key.N = Base64UrlEncoder.Encode(parameters.Modulus);
            key.E = Base64UrlEncoder.Encode(parameters.Exponent);
            return key;
        }
        catch (CryptographicException) when (IsEc(subjectPublicKeyInfo, out var parameters))
        {
            key.Kty = JsonWebAlgorithmsKeyTypes.EllipticCurve;
            key.Crv = parameters.Curve.Oid.FriendlyName switch
            {
                "nistP256" or "ECDSA_P256" => JsonWebKeyECTypes.P256,
                "nistP384" or "ECDSA_P384" => JsonWebKeyECTypes.P384,
                "nistP521" or "ECDSA_P521" => JsonWebKeyECTypes.P521,
                _ => throw new CryptographicException("EC keys must be on P-256, P-384 or P-521."),
            };
            key.X = Base64UrlEncoder.Encode(parameters.Q.X);
            key.Y = Base64UrlEncoder.Encode(parameters.Q.Y);
            return key;
        }
    }

    private static bool IsEc(byte[] subjectPublicKeyInfo, out ECParameters parameters)
    {
        try
        {
            using var ec = ECDsa.Create();
            ec.ImportSubjectPublicKeyInfo(subjectPublicKeyInfo, out _);
            parameters = ec.ExportParameters(false);
            return true;
        }
        catch (CryptographicException)
        {
            parameters = default;
            return false;
        }
    }
}
