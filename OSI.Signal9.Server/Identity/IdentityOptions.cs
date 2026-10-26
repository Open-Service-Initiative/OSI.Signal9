using System.ComponentModel.DataAnnotations;

namespace OSI.Signal9.Server.Identity;

/// <summary>Where the authorization server lives. Both <c>auth</c> and <c>api</c> read the same values.</summary>
public sealed class IdentityOptions
{
    public const string SectionName = "Identity";

    /// <summary>The static issuer, on the <c>auth</c> host (ADR-0003). It's the same on every host.</summary>
    [Required]
    public Uri Issuer { get; set; } = null!;

    /// <summary>The base address of the <c>api</c> host, where the RFC 8705 mTLS endpoint aliases live.</summary>
    [Required]
    public Uri ApiUri { get; set; } = null!;

    /// <summary>
    /// The base address of the <c>web</c> host, the dashboard's backend-for-frontend. web reads it to register its
    /// redirect URI, and auth to link to the dashboard once an invitation is accepted.
    /// </summary>
    public Uri? WebUri { get; set; }

    /// <summary>
    /// PEM-encoded root (and optionally intermediate) certificate authorities trusted to issue client certificates for
    /// <c>tls_client_auth</c>. With none, only <c>self_signed_tls_client_auth</c> is offered.
    /// </summary>
    public string[] TlsClientCertificateAuthorities { get; set; } = [];
}
