using System;
using System.ComponentModel.DataAnnotations;

namespace Signal9.Shared.DTOs.Base;

/// <summary>
/// Abstract base record for all DTOs in the Signal9 RMM system.
/// Provides unified hierarchical structure where every DTO has an ID and optional parent.
/// </summary>
public abstract record BaseDto<TId> : IBaseDto<TId> where TId : notnull
{
    /// <summary>
    /// Gets the unique identifier for this DTO.
    /// </summary>
    [Required(ErrorMessage = "Id is required")]
    public TId Id { get; init; } = typeof(TId) == typeof(Guid) ? (TId)(object)Guid.NewGuid() : default!;

    /// <summary>
    /// Gets the parent DTO's ID for hierarchical relationships.
    /// Null for root entities (top-level tenants).
    /// For agents: ParentId = TenantId they belong to
    /// For tenants: ParentId = Parent tenant ID (for MSP hierarchies)
    /// </summary>
    public TId? ParentId { get; init; }

    /// <summary>
    /// Gets the timestamp when the DTO was created.
    /// </summary>
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
}

