using System.ComponentModel.DataAnnotations;
using Azure;
using Azure.Data.Tables;

namespace Signal9.Shared.Models.Base;

/// <summary>
/// Base class for entities stored in Azure Tables
/// Implements ITableEntity for Azure Tables SDK compatibility
/// </summary>
public abstract class BaseTableEntity : ITableEntity, IParentScopedEntity, ISoftDeletable
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
    public required string RowKey { get; set; }
    
    /// <summary>
    /// ETag for optimistic concurrency control
    /// </summary>
    public ETag ETag { get; set; } = default;
    
    /// <summary>
    /// Timestamp managed by Azure Tables
    /// </summary>
    public DateTimeOffset? Timestamp { get; set; }

    // IEntity implementation
    public Guid Id
    {
        get => Guid.TryParse(RowKey, out var guid) ? guid : Guid.Empty;
        set => RowKey = value.ToString();
    }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // ITenantScopedEntity implementation
    public Guid ParentId
    {
        get => Guid.TryParse(PartitionKey, out var guid) ? guid : Guid.Empty;
        set => PartitionKey = value.ToString();
    }

    // ISoftDeletable implementation
    public bool IsDeleted { get; set; } = false;
    public DateTime? DeletedAt { get; set; }

    protected BaseTableEntity()
    {
        Id = Guid.NewGuid();
        CreatedAt = DateTime.UtcNow;
        UpdatedAt = DateTime.UtcNow;
    }

    protected BaseTableEntity(string tenantId, string? id = null)
    {
        ParentId = Guid.TryParse(tenantId, out var guid) ? guid : Guid.NewGuid();
        Id = id != null ? Guid.Parse(id) : Guid.NewGuid();
        CreatedAt = DateTime.UtcNow;
        UpdatedAt = DateTime.UtcNow;
    }
}
