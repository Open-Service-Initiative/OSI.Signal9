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
/// A customer organization. Tenants can be nested one level: an MSP tenant owns client tenants.
/// </summary>
public sealed record TenantDto(
    Guid Id,
    Guid? ParentTenantId,
    string Code,
    string Name,
    string? Description,
    string ContactEmail,
    string? ContactPhone,
    SubscriptionPlan Plan,
    int MaxAgents,
    TenantStatus Status,
    int AgentCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record CreateTenantRequest
{
    /// <summary>Short, unique, URL-safe code agents use to enroll. Immutable after creation.</summary>
    [Required, StringLength(32, MinimumLength = 2), RegularExpression("^[a-z0-9][a-z0-9-]*$",
        ErrorMessage = "Code may contain lowercase letters, digits and hyphens.")]
    public string Code { get; init; } = string.Empty;

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
    public int MaxAgents { get; init; } = 25;
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
    public int MaxAgents { get; init; }

    public TenantStatus Status { get; init; }
}
