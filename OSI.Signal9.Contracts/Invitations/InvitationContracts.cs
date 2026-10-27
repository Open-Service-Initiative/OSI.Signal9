using System.ComponentModel.DataAnnotations;

namespace OSI.Signal9.Contracts.Invitations;

/// <summary>Where an invitation is in its life. Read-only: it changes when it's redeemed, through <c>:revoke</c>, or with time.</summary>
public enum InvitationStatus
{
    /// <summary>It can still be redeemed.</summary>
    Pending,

    /// <summary>Someone redeemed it; it can't be used again.</summary>
    Redeemed,

    /// <summary>72 hours passed before anyone redeemed it.</summary>
    Expired,

    /// <summary>Withdrawn before anyone redeemed it.</summary>
    Revoked,
}

/// <summary>
/// A single-use, expiring offer of one role assignment: <see cref="RoleId"/> at <see cref="TenantId"/> (stories 28-37).
/// It's redeemed at <see cref="Link"/>, on auth, after the person signs in with Entra. The link carries only an opaque
/// reference, of which Signal9 keeps just a hash, so <see cref="Link"/> is present only in the response that creates the
/// invitation. It's never matched by email: only the optional <see cref="UserId"/> and <see cref="EntraTenantId"/> pin
/// who can redeem it.
/// </summary>
/// <param name="UserId">The existing user it's for, if any: only that user's Entra identity can redeem it.</param>
/// <param name="EntraTenantId">The Entra directory it's pinned to, if any: only people from it can redeem it.</param>
/// <param name="RedeemedByUserId">The user who redeemed it, created by the redemption if they didn't exist yet.</param>
public sealed record InvitationDto(
    Guid Id,
    Guid TenantId,
    string Url,
    Guid RoleId,
    Guid? UserId,
    Guid? EntraTenantId,
    InvitationStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? RedeemedAt,
    Guid? RedeemedByUserId,
    DateTimeOffset? RevokedAt,
    string? Link = null);

public sealed record CreateInvitationRequest
{
    /// <summary>The role the invitation offers at the tenant.</summary>
    [Required]
    public Guid? RoleId { get; init; }

    /// <summary>An existing user to offer the role to, rather than whoever redeems it.</summary>
    public Guid? UserId { get; init; }

    /// <summary>The Entra directory (<c>tid</c>) the person redeeming it must sign in from.</summary>
    public Guid? EntraTenantId { get; init; }
}
