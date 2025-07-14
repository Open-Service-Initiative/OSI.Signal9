using System;

namespace Signal9.Shared.DTOs.Base;

/// <summary>
/// Base interface for all DTOs in the Signal9 RMM system.
/// </summary>
public interface IBaseDto
{
    /// <summary>
    /// Gets the unique identifier for this DTO.
    /// </summary>
    string Id { get; }

    /// <summary>
    /// Gets the timestamp when the DTO was created.
    /// </summary>
    DateTime Timestamp { get; }
}