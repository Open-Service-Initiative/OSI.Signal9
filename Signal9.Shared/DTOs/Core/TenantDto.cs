using System.ComponentModel.DataAnnotations;
using Signal9.Shared.DTOs.Base;
using Signal9.Shared.Contracts;
using Signal9.Shared.Models;

namespace Signal9.Shared.DTOs;

/// <summary>
/// Tenant DTO - represents an organizational unit in the Signal9 RMM system.
/// Uses unified hierarchy where ParentId represents parent tenant for MSP structures.
/// </summary>
public record TenantDto : BaseDto<Guid>, ITenantData
{
    /// <summary>
    /// Unique tenant code for registration
    /// </summary>
    [Required(ErrorMessage = "TenantCode is required")]
    [StringLength(20, MinimumLength = 1, ErrorMessage = "TenantCode must be between 1 and 20 characters")]
    [RegularExpression(@"^[a-zA-Z0-9\-_]+$", ErrorMessage = "TenantCode can only contain alphanumeric characters, hyphens, and underscores")]
    public required string TenantCode { get; init; }
    
    /// <summary>
    /// Tenant display name
    /// </summary>
    [Required(ErrorMessage = "Name is required")]
    [StringLength(200, MinimumLength = 1, ErrorMessage = "Name must be between 1 and 200 characters")]
    public required string Name { get; init; }
    
    /// <summary>
    /// Tenant description
    /// </summary>
    [StringLength(500, ErrorMessage = "Description cannot exceed 500 characters")]
    public string? Description { get; init; }
    
    /// <summary>
    /// Primary contact email
    /// </summary>
    [Required(ErrorMessage = "ContactEmail is required")]
    [StringLength(255, ErrorMessage = "ContactEmail cannot exceed 255 characters")]
    [EmailAddress(ErrorMessage = "ContactEmail must be a valid email address")]
    public required string ContactEmail { get; init; }
    
    /// <summary>
    /// Subscription tier for this tenant
    /// </summary>
    public SubscriptionTier SubscriptionTier { get; init; } = SubscriptionTier.Basic;
    
    /// <summary>
    /// Data retention period in days
    /// </summary>
    [Range(1, 365, ErrorMessage = "DataRetentionDays must be between 1 and 365")]
    public int DataRetentionDays { get; init; } = 90;
    
    /// <summary>
    /// Current tenant status
    /// </summary>
    public TenantStatus Status { get; init; } = TenantStatus.Active;
    
    /// <summary>
    /// When the subscription expires
    /// </summary>
    public DateTime? SubscriptionExpiresAt { get; init; }
    
    /// <summary>
    /// API key for this tenant
    /// </summary>
    [StringLength(128, ErrorMessage = "ApiKey cannot exceed 128 characters")]
    public string? ApiKey { get; init; }
    
    /// <summary>
    /// Primary contact phone
    /// </summary>
    [StringLength(50, ErrorMessage = "ContactPhone cannot exceed 50 characters")]
    [Phone(ErrorMessage = "ContactPhone must be a valid phone number")]
    public string? ContactPhone { get; init; }
    
    /// <summary>
    /// Whether the tenant is active
    /// </summary>
    public bool IsActive { get; init; } = true;
    
    /// <summary>
    /// Maximum number of agents allowed for this tenant
    /// </summary>
    [Range(1, int.MaxValue, ErrorMessage = "MaxAgents must be at least 1")]
    public int MaxAgents { get; init; } = 100;
    
    /// <summary>
    /// Tenant configuration settings (JSON serialized)
    /// </summary>
    public string? Settings { get; init; }
    
    // ParentId inherited from BaseDto<Guid> represents parent tenant for MSP hierarchies
}


