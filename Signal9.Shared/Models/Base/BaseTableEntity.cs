using System.ComponentModel.DataAnnotations;
using Azure;
using Azure.Data.Tables;

namespace Signal9.Shared.Models.Base;

/// <summary>
/// Base class for entities stored in Azure Tables
/// Implements ITableEntity for Azure Tables SDK compatibility
/// </summary>
public abstract class BaseTableEntity : ITableEntity, ITenantScopedEntity, ISoftDeletable
{
    /// <summary>
    /// Partition key for Azure Tables - uses TenantId for multi-tenant isolation
    /// </summary>
    [Required]
    public string PartitionKey { get; set; } = string.Empty;
    
    /// <summary>
    /// Row key for Azure Tables - uses entity ID
    /// </summary>
    [Required]
    public string RowKey { get; set; } = string.Empty;
    
    /// <summary>
    /// ETag for optimistic concurrency control
    /// </summary>
    public ETag ETag { get; set; } = default;
    
    /// <summary>
    /// Timestamp managed by Azure Tables
    /// </summary>
    public DateTimeOffset? Timestamp { get; set; }

    // IEntity implementation
    public string Id 
    { 
        get => RowKey; 
        set => RowKey = value; 
    }
    
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // ITenantScopedEntity implementation
    public string TenantId 
    { 
        get => PartitionKey; 
        set => PartitionKey = value; 
    }

    // ISoftDeletable implementation
    public bool IsDeleted { get; set; } = false;
    public DateTime? DeletedAt { get; set; }

    protected BaseTableEntity()
    {
        Id = Guid.NewGuid().ToString();
        CreatedAt = DateTime.UtcNow;
        UpdatedAt = DateTime.UtcNow;
    }

    protected BaseTableEntity(string tenantId, string? id = null)
    {
        TenantId = tenantId;
        Id = id ?? Guid.NewGuid().ToString();
        CreatedAt = DateTime.UtcNow;
        UpdatedAt = DateTime.UtcNow;
    }
}
