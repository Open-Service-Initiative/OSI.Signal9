using System;
using System.ComponentModel.DataAnnotations;

namespace Signal9.Shared.DTOs.Base;

/// <summary>
/// Abstract base record for all DTOs in the Signal9 RMM system.
/// Provides common functionality and properties for all data transfer objects.
/// </summary>
public abstract record BaseDto : IBaseDto
{
    /// <summary>
    /// Gets the unique identifier for this DTO.
    /// Can be overridden by derived classes if needed.
    /// </summary>
    [StringLength(100, ErrorMessage = "Id cannot exceed 100 characters")]
    public virtual string Id { get; init; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Gets the timestamp when the DTO was created.
    /// Defaults to the current UTC time.
    /// </summary>
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}