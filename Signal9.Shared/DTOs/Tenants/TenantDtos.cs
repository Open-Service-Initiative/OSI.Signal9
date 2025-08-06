using System.ComponentModel.DataAnnotations;
using Signal9.Shared.DTOs.Base;
using Signal9.Shared.DTOs;
using Signal9.Shared.Models;
using Signal9.Shared.Contracts;

namespace Signal9.Shared.DTOs.Tenants;

/// <summary>
/// Request model for creating a new tenant in the Signal9 RMM system.
/// Now uses enhanced TenantDto with shared contracts.
/// </summary>
public record CreateTenantRequest : BaseDto<Guid>
{
    /// <summary>
    /// Gets the unique tenant slug for registration.
    /// </summary>
    [Required(ErrorMessage = "TenantSlug is required")]
    [StringLength(20, MinimumLength = 1, ErrorMessage = "TenantSlug must be between 1 and 20 characters")]
    [RegularExpression(@"^[a-zA-Z0-9\-_]+$", ErrorMessage = "TenantSlug can only contain alphanumeric characters, hyphens, and underscores")]
    public required string TenantSlug { get; init; }

    /// <summary>
    /// Gets the name of the tenant.
    /// </summary>
    [Required(ErrorMessage = "Name is required")]
    [StringLength(200, MinimumLength = 1, ErrorMessage = "Name must be between 1 and 200 characters")]
    public required string Name { get; init; }
    
    /// <summary>
    /// Gets the description of the tenant.
    /// </summary>
    [StringLength(500, ErrorMessage = "Description cannot exceed 500 characters")]
    public string? Description { get; init; }
    
    /// <summary>
    /// Gets the contact email for the tenant.
    /// </summary>
    [Required(ErrorMessage = "ContactEmail is required")]
    [StringLength(255, ErrorMessage = "ContactEmail cannot exceed 255 characters")]
    [EmailAddress(ErrorMessage = "ContactEmail must be a valid email address")]
    public required string ContactEmail { get; init; }
    
    /// <summary>
    /// Gets the subscription tier for the tenant.
    /// </summary>
    public SubscriptionTier SubscriptionTier { get; init; } = SubscriptionTier.Basic;
    
    /// <summary>
    /// Gets the maximum number of agents allowed.
    /// </summary>
    [Range(1, int.MaxValue, ErrorMessage = "MaxAgents must be at least 1")]
    public int MaxAgents { get; init; } = 10;
    
    /// <summary>
    /// Gets the data retention period in days.
    /// </summary>
    [Range(1, int.MaxValue, ErrorMessage = "DataRetentionDays must be at least 1")]
    public int DataRetentionDays { get; init; } = 30;
    
    /// <summary>
    /// Gets the tenant configuration settings (JSON).
    /// </summary>
    public string? Settings { get; init; }

    // Additional backward compatibility properties
    public string? Slug { get; init; }
    public string? Plan { get; init; }
    public string? TenantType { get; init; } = "Standard";
    public string? ParentTenantId { get; init; }
    public string? ContactPhone { get; init; }
    
    /// <summary>
    /// Legacy TenantId property for backward compatibility
    /// </summary>
    public Guid TenantId { get; init; } = Guid.Empty;

    /// <summary>
    /// Converts this request to an enhanced TenantDto - SIMPLIFIED for Entity Framework approach
    /// </summary>
    public object ToTenantDto()
    {
        return new
        {
            TenantCode = TenantSlug,
            Name,
            Description,
            ContactEmail,
            SubscriptionTier,
            MaxAgents,
            DataRetentionDays,
            Status = TenantStatus.Active
        };
    }
}

/// <summary>
/// Request model for updating an existing tenant.
/// </summary>
public record UpdateTenantRequest : BaseDto<Guid>
{
    /// <summary>
    /// Gets the tenant ID to update.
    /// </summary>
    [Required(ErrorMessage = "TenantId is required")]
    public required string TenantId { get; init; }

    /// <summary>
    /// Gets the updated name of the tenant.
    /// </summary>
    [StringLength(200, MinimumLength = 1, ErrorMessage = "Name must be between 1 and 200 characters")]
    public string? Name { get; init; }
    
    /// <summary>
    /// Gets the updated description of the tenant.
    /// </summary>
    [StringLength(500, ErrorMessage = "Description cannot exceed 500 characters")]
    public string? Description { get; init; }
    
    /// <summary>
    /// Gets the updated contact email for the tenant.
    /// </summary>
    [StringLength(255, ErrorMessage = "ContactEmail cannot exceed 255 characters")]
    [EmailAddress(ErrorMessage = "ContactEmail must be a valid email address")]
    public string? ContactEmail { get; init; }
    
    /// <summary>
    /// Gets the updated subscription tier for the tenant.
    /// </summary>
    public SubscriptionTier? SubscriptionTier { get; init; }
    
    /// <summary>
    /// Gets the updated maximum number of agents allowed.
    /// </summary>
    [Range(1, int.MaxValue, ErrorMessage = "MaxAgents must be at least 1")]
    public int? MaxAgents { get; init; }
    
    /// <summary>
    /// Gets the updated data retention period in days.
    /// </summary>
    [Range(1, int.MaxValue, ErrorMessage = "DataRetentionDays must be at least 1")]
    public int? DataRetentionDays { get; init; }
    
    /// <summary>
    /// Gets the updated tenant configuration settings (JSON).
    /// </summary>
    public string? Settings { get; init; }

    /// <summary>
    /// Gets the updated tenant status.
    /// </summary>
    public TenantStatus? Status { get; init; }

    // Additional backward compatibility properties
    public string? Slug { get; init; }
    public string? Plan { get; init; }
    public string? TenantType { get; init; }
    public string? ParentTenantId { get; init; }
    public string? ContactPhone { get; init; }
    public bool? IsActive { get; init; }
}

/// <summary>
/// Response model for tenant operations.
/// </summary>
public record TenantResponse : BaseDto<Guid>
{
    /// <summary>
    /// Gets the tenant information using enhanced TenantDto.
    /// </summary>
    public object? Tenant { get; init; }

    /// <summary>
    /// Gets a value indicating whether the operation was successful.
    /// </summary>
    public bool IsSuccess { get; init; } = true;

    /// <summary>
    /// Gets the operation message or error details.
    /// </summary>
    public string? Message { get; init; }

    /// <summary>
    /// Gets additional metadata about the tenant.
    /// </summary>
    public Dictionary<string, object> Metadata { get; init; } = [];

    /// <summary>
    /// Convenience properties that delegate to the inner Tenant for backward compatibility
    /// </summary>
    public string? Name { get; set; }
    public string? Slug { get; set; }
    public string? Description { get; set; }
    public string? ContactEmail { get; set; }
    public string? ContactPhone { get; set; }
    public SubscriptionTier? Plan { get; set; }
    public int? MaxAgents { get; set; }
    public bool? IsActive { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public int AgentCount { get; set; }
    public string? TenantId { get; set; } // String for backward compatibility 
    public string? TenantType { get; set; }
    public string? ParentTenantId { get; set; }
}

/// <summary>
/// Request model for tenant authentication/validation.
/// </summary>
public record TenantAuthenticationRequest : BaseDto<Guid>
{
    /// <summary>
    /// Gets the tenant slug for authentication.
    /// </summary>
    [Required(ErrorMessage = "TenantSlug is required")]
    [StringLength(20, MinimumLength = 1, ErrorMessage = "TenantSlug must be between 1 and 20 characters")]
    public required string TenantSlug { get; init; }

    /// <summary>
    /// Gets the API key for authentication (optional).
    /// </summary>
    [StringLength(100, ErrorMessage = "ApiKey cannot exceed 100 characters")]
    public string? ApiKey { get; init; }
}

/// <summary>
/// Response model for tenant authentication.
/// </summary>
public record TenantAuthenticationResponse : BaseDto<Guid>
{
    /// <summary>
    /// Gets a value indicating whether authentication was successful.
    /// </summary>
    public bool IsAuthenticated { get; init; }

    /// <summary>
    /// Gets the tenant information if authentication was successful.
    /// </summary>
    public object? Tenant { get; init; }

    /// <summary>
    /// Gets the authentication message or error details.
    /// </summary>
    public string? Message { get; init; }

    /// <summary>
    /// Gets the session token or authentication context.
    /// </summary>
    public string? Token { get; init; }
}

/// <summary>
/// Alias for SubscriptionTier to maintain UI compatibility
/// </summary>
public enum TenantPlan
{
    Free = 0,
    Standard = 1,
    Professional = 2,
    Enterprise = 3
}


