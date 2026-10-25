using System.ComponentModel.DataAnnotations;

namespace OSI.Signal9.Contracts.Tenants;

public enum TenantStatus
{
    Active = 0,
    Suspended = 1,
    Cancelled = 2
}

public enum SubscriptionPlan
{
    Basic = 0,
    Professional = 1,
    Enterprise = 2
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
    SubscriptionPlan Plan,
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

    public SubscriptionPlan Plan { get; init; } = SubscriptionPlan.Basic;

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

    public SubscriptionPlan Plan { get; init; }

    [Range(1, 100_000)]
    public int MaxDevices { get; init; }

    /// <summary>
    /// Whether agents newly enrolling here (and in descendants) wait for approval. Turning it off where an ancestor holds
    /// is refused (409); agents already enrolled are unaffected either way.
    /// </summary>
    public bool HoldsNewAgentsForApproval { get; init; }
}
