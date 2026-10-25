using System.ComponentModel.DataAnnotations;

namespace OSI.Signal9.Contracts.Enrollment;

/// <summary>Where an enrollment token is in its life. Read-only: it changes through the <c>:revoke</c> action, with use or with time.</summary>
public enum EnrollmentTokenStatus
{
    /// <summary>New agents can enroll with it.</summary>
    Active,

    /// <summary>Its expiry has passed.</summary>
    Expired,

    /// <summary>It has been used as many times as its use limit allows.</summary>
    UsedUp,

    /// <summary>Revoked for good. Agents already enrolled with it are unaffected.</summary>
    Revoked,
}

/// <summary>
/// A revocable secret, issued for one tenant, that lets new agents enroll into it (stories 92-94). Multi-use and
/// non-expiring unless it has a use limit (<see cref="MaxUses"/>; 1 is single use) or an expiry. Signal9 stores only a
/// hash of the secret, so <see cref="Secret"/> is present only in the response that issues it: creation and
/// <c>:rotate</c>. <see cref="UseCount"/> and <see cref="LastUsedAt"/> show how often and when agents enrolled with it,
/// so a leaked token is easy to spot (story 95).
/// </summary>
public sealed record EnrollmentTokenDto(
    Guid Id,
    Guid TenantId,
    string Url,
    string Name,
    int? MaxUses,
    DateTimeOffset? ExpiresAt,
    EnrollmentTokenStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? RotatedAt,
    DateTimeOffset? RevokedAt,
    int UseCount,
    DateTimeOffset? LastUsedAt,
    string? Secret = null);

public sealed record CreateEnrollmentTokenRequest
{
    /// <summary>What the token is for, such as the site or installer it's given to.</summary>
    [Required, StringLength(200, MinimumLength = 1)]
    public string Name { get; init; } = string.Empty;

    /// <summary>How many agents can enroll with it; 1 for single use. Unlimited when omitted.</summary>
    [Range(1, int.MaxValue)]
    public int? MaxUses { get; init; }

    /// <summary>When it stops working; must be in the future. Never when omitted.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }
}
