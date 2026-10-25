using System.ComponentModel.DataAnnotations;

namespace OSI.Signal9.Contracts.ApiApplications;

/// <summary>
/// How an API application authenticates at the token endpoint, by its RFC 7591 <c>token_endpoint_auth_method</c>
/// name. Chosen at registration and fixed from then on.
/// </summary>
public static class TokenEndpointAuthMethods
{
    /// <summary>mTLS with a certificate from a trusted authority, matched on one registered subject (RFC 8705 §2.1).</summary>
    public const string TlsClientAuth = "tls_client_auth";

    /// <summary>mTLS with a self-signed certificate registered as one of the application's credentials (RFC 8705 §2.2).</summary>
    public const string SelfSignedTlsClientAuth = "self_signed_tls_client_auth";

    /// <summary>A JWT client assertion signed with a key registered as a credential (RFC 7523, OpenID Connect Core §9).</summary>
    public const string PrivateKeyJwt = "private_key_jwt";

    /// <summary>
    /// A hashed, expiring client secret, the last resort (RFC 6749 §2.3.1). Sent with HTTP Basic or in the form body.
    /// Never allowed to hold a scope that needs a recent MFA.
    /// </summary>
    public const string ClientSecret = "client_secret_basic";

    public static readonly IReadOnlyList<string> All = [TlsClientAuth, SelfSignedTlsClientAuth, PrivateKeyJwt, ClientSecret];

    /// <summary>Whether the method is mTLS, so tokens issued with it are bound to the certificate.</summary>
    public static bool IsMutualTls(string method) => method is TlsClientAuth or SelfSignedTlsClientAuth;
}

/// <summary>
/// The scopes whose endpoints need a recent MFA: running code (#82) and deleting tenants. An API application acting as
/// itself may hold one only if a person granted it with an MFA in the last 15 minutes, and only if it authenticates with
/// a certificate or <c>private_key_jwt</c>, never a client secret.
/// </summary>
public static class RecentMfaScopes
{
    public static readonly IReadOnlyList<string> All = ["scripts.execute", "terminal.execute", "tenants.delete"];
}

/// <summary>
/// The certificate subject a <c>tls_client_auth</c> application registers (RFC 8705 §2.1.2): one of
/// <c>tls_client_auth_subject_dn</c>, <c>tls_client_auth_san_dns</c>, <c>tls_client_auth_san_uri</c>,
/// <c>tls_client_auth_san_ip</c> or <c>tls_client_auth_san_email</c>, and its value.
/// </summary>
public sealed record TlsClientAuthSubject(
    [Required, AllowedValues("tls_client_auth_subject_dn", "tls_client_auth_san_dns", "tls_client_auth_san_uri",
        "tls_client_auth_san_ip", "tls_client_auth_san_email")]
    string Parameter,
    [Required, StringLength(500, MinimumLength = 1)] string Value);

/// <summary>
/// An external system registered at a tenant to use the API as itself (story 78), confined to that tenant and its
/// descendants (story 79). It acts with its own role, no higher than its creator's (story 80), narrowed to the scopes
/// it was granted. Its <see cref="ClientId"/> is its OAuth client ID.
/// </summary>
public sealed record ApiApplicationDto(
    Guid Id,
    Guid TenantId,
    string ClientId,
    string DisplayName,
    Guid RoleId,
    IReadOnlyList<string> Scopes,
    string TokenEndpointAuthMethod,
    TlsClientAuthSubject? TlsClientAuthSubject,
    DateTimeOffset CreatedAt);

public sealed record CreateApiApplicationRequest
{
    [Required, StringLength(200, MinimumLength = 1)]
    public string DisplayName { get; init; } = "";

    /// <summary>The application's own role at the tenant: no higher than the caller's access there.</summary>
    [Required]
    public Guid? RoleId { get; init; }

    /// <summary>The scopes it may request: within its role's. Scopes that need a recent MFA need the caller's too.</summary>
    [Required]
    public IReadOnlyList<string> Scopes { get; init; } = [];

    /// <summary>One of <see cref="TokenEndpointAuthMethods"/>.</summary>
    [Required, AllowedValues("tls_client_auth", "self_signed_tls_client_auth", "private_key_jwt", "client_secret_basic")]
    public string TokenEndpointAuthMethod { get; init; } = "";

    /// <summary>Required for, and only for, <c>tls_client_auth</c>.</summary>
    public TlsClientAuthSubject? TlsClientAuthSubject { get; init; }
}

/// <summary>An API application's editable fields, as a JSON merge patch (RFC 7396) sees them.</summary>
public sealed record UpdateApiApplicationRequest
{
    [Required, StringLength(200, MinimumLength = 1)]
    public string DisplayName { get; init; } = "";

    [Required]
    public Guid? RoleId { get; init; }

    [Required]
    public IReadOnlyList<string> Scopes { get; init; } = [];

    /// <summary>For a <c>tls_client_auth</c> application only: its credential.</summary>
    public TlsClientAuthSubject? TlsClientAuthSubject { get; init; }
}

/// <summary>What a credential is.</summary>
public static class ApiApplicationCredentialTypes
{
    /// <summary>A self-signed certificate, for <c>self_signed_tls_client_auth</c>.</summary>
    public const string Certificate = "certificate";

    /// <summary>A public key that verifies client assertions, for <c>private_key_jwt</c>.</summary>
    public const string PublicKey = "public-key";

    /// <summary>A client secret, for <c>client_secret_basic</c>. Its value is shown only once, when it's created.</summary>
    public const string Secret = "secret";
}

/// <summary>
/// One of an API application's credentials. Several can be live at once, so they can be rotated without downtime.
/// <see cref="Thumbprint"/> is the base64url SHA-256 of the certificate or public key (RFC 8705 §3.1, RFC 7638 style),
/// and <see cref="Secret"/> is returned only by the request that created a secret: it's stored as a hash.
/// </summary>
public sealed record ApiApplicationCredentialDto(
    Guid Id,
    string Type,
    string? Thumbprint,
    string? Subject,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt,
    string? Secret = null);

/// <summary>
/// Adds a credential of the type the application's method takes: <see cref="CertificatePem"/> for
/// <c>self_signed_tls_client_auth</c>, <see cref="PublicKeyPem"/> (or a certificate) for <c>private_key_jwt</c>, and
/// nothing but an optional <see cref="ExpiresAt"/> for a secret, which the server generates.
/// </summary>
public sealed record CreateApiApplicationCredentialRequest
{
    [StringLength(20_000)]
    public string? CertificatePem { get; init; }

    [StringLength(10_000)]
    public string? PublicKeyPem { get; init; }

    /// <summary>When a secret stops working: within a year. Defaults to 180 days.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }
}
