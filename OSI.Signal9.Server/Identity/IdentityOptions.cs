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
}
