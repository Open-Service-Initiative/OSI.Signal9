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
/// Full replacement of a tenant's mutable fields (PUT semantics).
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

    public TenantStatus Status { get; init; }
}
