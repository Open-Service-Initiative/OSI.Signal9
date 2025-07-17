using System;

namespace Signal9.Shared.DTOs.Base;

/// <summary>
/// Base interface for all DTOs in the Signal9 RMM system.
/// </summary>
public interface IBaseDto<TId> where TId : notnull
{
    /// <summary>
    /// Gets the unique identifier for this DTO.
    /// </summary>
    TId Id { get; }

    /// <summary>
    /// Gets the parent DTO's ID for hierarchical relationships.
    /// </summary>
    TId? ParentId { get; }

    /// <summary>
    /// Gets the timestamp when the DTO was created.
    /// </summary>
    DateTime CreatedAt { get; }
}

