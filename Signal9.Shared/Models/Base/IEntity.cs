using System.ComponentModel.DataAnnotations;

namespace Signal9.Shared.Models.Base;

/// <summary>
/// Base interface for all entities in the system
/// </summary>
public interface IEntity
{
    /// <summary>
    /// Unique identifier for the entity
    /// </summary>
    Guid Id { get; set; }
    
    /// <summary>
    /// Timestamp when the entity was created
    /// </summary>
    DateTime CreatedAt { get; set; }
    
    /// <summary>
    /// Timestamp when the entity was last updated
    /// </summary>
    DateTime UpdatedAt { get; set; }
}

/// <summary>
/// Base interface for tenant-scoped entities
/// </summary>
public interface IParentScopedEntity : IEntity
{
    /// <summary>
    /// Tenant identifier for multi-tenant isolation
    /// </summary>
    Guid ParentId { get; set; }
}

/// <summary>
/// Base interface for entities that can be soft deleted
/// </summary>
public interface ISoftDeletable
{
    /// <summary>
    /// Indicates if the entity has been soft deleted
    /// </summary>
    bool IsDeleted { get; set; }
    
    /// <summary>
    /// Timestamp when the entity was deleted
    /// </summary>
    DateTime? DeletedAt { get; set; }
}
