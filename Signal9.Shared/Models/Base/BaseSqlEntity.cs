using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Signal9.Shared.Models.Base;

/// <summary>
/// Base class for entities stored in Azure SQL Database
/// Optimized for Entity Framework Core with multi-tenant patterns
/// </summary>
public abstract class BaseSqlEntity : IParentScopedEntity, ISoftDeletable
{
    /// <summary>
    /// Primary key for SQL entities
    /// </summary>
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public Guid Id { get; set; }

    /// <summary>
    /// Tenant identifier for multi-tenant isolation
    /// Used in global query filters
    /// </summary>
    [Required]
    [MaxLength(50)]
    [Column("tenant_id")]
    public Guid ParentId { get; set; }

    /// <summary>
    /// Timestamp when the entity was created
    /// </summary>
    [Required]
    [Column("created_at")]
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// Timestamp when the entity was last updated
    /// </summary>
    [Required]
    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; }

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
        Id = Guid.NewGuid();
        CreatedAt = DateTime.UtcNow;
        UpdatedAt = DateTime.UtcNow;
    }

    protected BaseSqlEntity(Guid parentId)
    {
        ParentId = parentId;
        Id = Guid.NewGuid();
        CreatedAt = DateTime.UtcNow;
        UpdatedAt = DateTime.UtcNow;
    }
}
