using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;
using Signal9.Shared.Models.Base;
using Signal9.Shared.Contracts;

namespace Signal9.Shared.Models;

/// <summary>
/// Tenant entity for multi-tenant SaaS architecture
/// Stores tenant configuration and settings
/// </summary>
[Table("tenants")]
[Index(nameof(TenantCode), IsUnique = true)]
public class Tenant : BaseSqlEntity, ITenantData
{
    /// <summary>
    /// Unique tenant code for registration
    /// </summary>
    [Required]
    [MaxLength(20)]
    [Column("tenant_code")]
    public string TenantCode { get; set; } = string.Empty;
    
    /// <summary>
    /// Tenant display name
    /// </summary>
    [Required]
    [MaxLength(200)]
    [Column("name")]
    public string Name { get; set; } = string.Empty;
    
    /// <summary>
    /// Tenant description
    /// </summary>
    [MaxLength(500)]
    [Column("description")]
    public string? Description { get; set; }
    
    /// <summary>
    /// Primary contact email
    /// </summary>
    [Required]
    [MaxLength(255)]
    [Column("contact_email")]
    public string ContactEmail { get; set; } = string.Empty;
    
    /// <summary>
    /// Tenant subscription tier
    /// </summary>
    [Column("subscription_tier")]
    public SubscriptionTier SubscriptionTier { get; set; } = SubscriptionTier.Basic;
    
    /// <summary>
    /// Maximum number of agents allowed
    /// </summary>
    [Column("max_agents")]
    public int MaxAgents { get; set; } = 10;
    
    /// <summary>
    /// Data retention period in days
    /// </summary>
    [Column("data_retention_days")]
    public int DataRetentionDays { get; set; } = 30;
    
    /// <summary>
    /// Tenant configuration settings (JSON)
    /// </summary>
    [Column("settings")]
    public string? Settings { get; set; }
    
    /// <summary>
    /// Tenant status
    /// </summary>
    [Column("status")]
    public TenantStatus Status { get; set; } = TenantStatus.Active;
    
    /// <summary>
    /// Subscription expiration date
    /// </summary>
    [Column("subscription_expires_at")]
    public DateTime? SubscriptionExpiresAt { get; set; }
    
    /// <summary>
    /// API key for tenant authentication
    /// </summary>
    [MaxLength(100)]
    [Column("api_key")]
    public string? ApiKey { get; set; }

    public Tenant() : base() 
    {
        // For tenants, TenantId is the same as Id
        ParentId = Id;
    }
    
    public Tenant(string tenantCode, string name, string contactEmail) : base()
    {
        TenantCode = tenantCode;
        Name = name;
        ContactEmail = contactEmail;
        ParentId = Id; // Self-reference for tenants
        ApiKey = GenerateApiKey();
    }
    
    private static string GenerateApiKey()
    {
        return Convert.ToBase64String(Guid.NewGuid().ToByteArray()).Replace("=", "").Replace("+", "").Replace("/", "");
    }
}

/// <summary>
/// Tenant subscription tiers
/// </summary>
public enum SubscriptionTier
{
    Basic = 0,
    Professional = 1,
    Enterprise = 2
}

/// <summary>
/// Tenant status
/// </summary>
public enum TenantStatus
{
    Active = 0,
    Suspended = 1,
    Expired = 2,
    Cancelled = 3
}
