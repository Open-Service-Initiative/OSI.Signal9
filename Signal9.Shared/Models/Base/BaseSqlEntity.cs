using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Signal9.Shared.Models.Base;

/// <summary>
/// Base class for entities stored in Azure SQL Database
/// Optimized for Entity Framework Core with multi-tenant patterns
/// </summary>
public abstract class BaseSqlEntity : ITenantScopedEntity, ISoftDeletable
{
    /// <summary>
    /// Primary key for SQL entities
    /// </summary>
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Tenant identifier for multi-tenant isolation
    /// Used in global query filters
    /// </summary>
    [Required]
    [MaxLength(50)]
    [Column("tenant_id")]
    public string TenantId { get; set; } = string.Empty;

    /// <summary>
    /// Timestamp when the entity was created
    /// </summary>
    [Required]
    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Timestamp when the entity was last updated
    /// </summary>
    [Required]
    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Indicates if the entity has been soft deleted
    /// </summary>
    [Column("is_deleted")]
    public bool IsDeleted { get; set; } = false;

    /// <summary>
    /// Timestamp when the entity was deleted
    /// </summary>
    [Column("deleted_at")]
    public DateTime? DeletedAt { get; set; }

    /// <summary>
    /// Optimistic concurrency control
    /// </summary>
    [Timestamp]
    [Column("row_version")]
    public byte[]? RowVersion { get; set; }

    protected BaseSqlEntity()
    {
        Id = Guid.NewGuid().ToString();
        CreatedAt = DateTime.UtcNow;
        UpdatedAt = DateTime.UtcNow;
    }

    protected BaseSqlEntity(string tenantId)
    {
        TenantId = tenantId;
        Id = Guid.NewGuid().ToString();
        CreatedAt = DateTime.UtcNow;
        UpdatedAt = DateTime.UtcNow;
    }
}
