using System.ComponentModel.DataAnnotations;

namespace OSI.Signal9.Contracts.Tenants;

public enum TenantStatus
{
    Active = 0,
    Suspended = 1,
    Cancelled = 2
}

/// <summary>
/// A node in the tenant tree, such as a consultancy, an MSP, a client or a site. Tenants nest to a configurable maximum depth.
/// </summary>
/// <param name="HoldsNewAgentsForApproval">
/// Whether agents newly enrolling here wait for a person's approval before they receive work (story 96): set here or
/// inherited from an ancestor. Off by default.
/// </param>
/// <param name="ApprovalHoldInherited">
/// Whether an ancestor holds new agents for approval, so the hold can't be turned off here.
/// </param>
public sealed record TenantDto(
    Guid Id,
    Guid? ParentTenantId,
    string Name,
    string? Description,
    string ContactEmail,
    string? ContactPhone,
    int MaxDevices,
    TenantStatus Status,
    int DeviceCount,
    bool HoldsNewAgentsForApproval,
    bool ApprovalHoldInherited,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record CreateTenantRequest
{
    [Required, StringLength(200, MinimumLength = 1)]
    public string Name { get; init; } = string.Empty;

    [StringLength(1000)]
    public string? Description { get; init; }

    [Required, EmailAddress, StringLength(255)]
    public string ContactEmail { get; init; } = string.Empty;

    [Phone, StringLength(32)]
    public string? ContactPhone { get; init; }

    public Guid? ParentTenantId { get; init; }

    [Range(1, 100_000)]
    public int MaxDevices { get; init; } = 25;
}

/// <summary>
/// A tenant's editable fields: the document a JSON merge patch (RFC 7396) applies to. Status is read-only and changes
/// through the <c>:suspend</c>, <c>:activate</c> and <c>:cancel</c> actions; the parent changes with a tenant transfer.
/// </summary>
public sealed record UpdateTenantRequest
{
    [Required, StringLength(200, MinimumLength = 1)]
    public string Name { get; init; } = string.Empty;

    [StringLength(1000)]
    public string? Description { get; init; }

    [Required, EmailAddress, StringLength(255)]
    public string ContactEmail { get; init; } = string.Empty;

    [Phone, StringLength(32)]
    public string? ContactPhone { get; init; }

    [Range(1, 100_000)]
    public int MaxDevices { get; init; }

    /// <summary>
    /// Whether agents newly enrolling here (and in descendants) wait for approval. Turning it off where an ancestor holds
    /// is refused (409); agents already enrolled are unaffected either way.
    /// </summary>
    public bool HoldsNewAgentsForApproval { get; init; }
}

/// <summary>Where a tenant transfer stands. Accepting it completes it: the tenant moves in the same step.</summary>
public enum TenantTransferStatus
{
    /// <summary>Proposed, waiting for the receiving side to accept or decline it.</summary>
    Pending = 0,

    /// <summary>Accepted by the receiving side, and so completed: the tenant now sits under its new parent.</summary>
    Accepted = 1,

    /// <summary>Declined by the receiving side. Nothing moved.</summary>
    Declined = 2,

    /// <summary>Withdrawn by the proposing side before it was decided. Nothing moved.</summary>
    Cancelled = 3,
}

/// <summary>
/// A proposal to move a tenant, with all its descendants, to a new parent tenant (a tenant transfer). One side, an
/// Administrator above the tenant, proposes it; the other, at the new parent, accepts or declines it.
/// </summary>
/// <param name="TenantId">The tenant being transferred.</param>
/// <param name="Url">The transfer's canonical URL, under the tenant being transferred, where both sides act on it.</param>
/// <param name="TenantName">Its name, which both sides see so the receiving side knows what it's accepting.</param>
/// <param name="FromParentTenantId">Its parent when the transfer was proposed.</param>
/// <param name="ToParentTenantId">The parent it moves to when the transfer is accepted.</param>
/// <param name="ToParentTenantName">The new parent's name, which both sides see.</param>
/// <param name="DecidedAt">When it was accepted, declined or cancelled.</param>
public sealed record TenantTransferDto(
    Guid Id,
    Guid TenantId,
    string Url,
    string? TenantName,
    Guid? FromParentTenantId,
    Guid ToParentTenantId,
    string? ToParentTenantName,
    TenantTransferStatus Status,
    DateTimeOffset ProposedAt,
    DateTimeOffset? DecidedAt);

/// <summary>Proposes transferring the tenant to a new parent tenant, which may be in another tenant tree.</summary>
public sealed record ProposeTenantTransferRequest
{
    [Required]
    public Guid? ToParentTenantId { get; init; }
}

/// <summary>Which side of a tenant transfer a tenant is on, for listing its transfers.</summary>
public enum TenantTransferDirection
{
    /// <summary>Transfers of the tenant, or of tenants that were beneath it when proposed.</summary>
    Outgoing = 0,

    /// <summary>Transfers to the tenant, or to tenants beneath it, as their new parent.</summary>
    Incoming = 1,
}

/// <summary>
/// A device a tenant transfer would bring into a root tenant tree that already has a device for the same hardware
/// (SMBIOS UUID, or manufacturer and serial): one device per machine per root tree. Listed, by ID only, in the 409
/// refusing the transfer.
/// </summary>
/// <param name="DeviceId">The device in the transferred subtree.</param>
/// <param name="DuplicateOfDeviceId">The device already in the receiving tree with the same hardware.</param>
public sealed record DuplicateDeviceDto(Guid DeviceId, Guid DuplicateOfDeviceId);

/// <summary>
/// What deleting a tenant would do, as things stand (#134, story 11): what still blocks it, and what the cascade would
/// remove beyond the tenant's own subtree. Deleting refuses (409) while <see cref="ChildTenantIds"/> or
/// <see cref="DeviceIds"/> isn't empty, and, except for a Superadministrator, while
/// <see cref="TenantsLeftWithoutAdministrator"/> isn't.
/// </summary>
/// <param name="ChildTenantIds">Child tenants, which must be deleted or transferred first.</param>
/// <param name="DeviceIds">Live devices, which must be deleted or transferred first.</param>
/// <param name="OutsideRoleAssignments">
/// Role assignments at other tenants, held by users the tenant owns: they're deleted with their users, and each removal is
/// audited at the tenant where it was.
/// </param>
/// <param name="TenantsLeftWithoutAdministrator">
/// Tenants where removing those assignments would leave no Administrator at the tenant or above it.
/// </param>
public sealed record TenantDeletionPreviewDto(
    Guid TenantId,
    IReadOnlyList<Guid> ChildTenantIds,
    IReadOnlyList<Guid> DeviceIds,
    IReadOnlyList<Roles.RoleAssignmentDto> OutsideRoleAssignments,
    IReadOnlyList<Guid> TenantsLeftWithoutAdministrator);
