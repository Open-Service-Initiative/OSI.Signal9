using System;
using System.ComponentModel.DataAnnotations;

namespace Signal9.Shared.DTOs.Base;

/// <summary>
/// Base record for paginated requests in the Signal9 RMM system.
/// Provides common pagination parameters for list-based queries.
/// </summary>
public record PagedRequest : BaseDto<Guid>
{
    /// <summary>
    /// Gets the page number to retrieve (1-based).
    /// Must be greater than 0.
    /// </summary>
    [Range(1, int.MaxValue, ErrorMessage = "Page must be greater than 0")]
    public int Page { get; init; } = 1;

    /// <summary>
    /// Gets the number of items per page.
    /// Must be between 1 and 1000 to prevent performance issues.
    /// </summary>
    [Range(1, 1000, ErrorMessage = "PageSize must be between 1 and 1000")]
    public int PageSize { get; init; } = 50;

    /// <summary>
    /// Gets the property name to sort by.
    /// Should be a valid property name for the entity being queried.
    /// </summary>
    [MaxLength(100, ErrorMessage = "SortBy cannot exceed 100 characters")]
    [RegularExpression(@"^[a-zA-Z][a-zA-Z0-9_\.]*$", ErrorMessage = "SortBy must be a valid property name")]
    public string? SortBy { get; init; }

    /// <summary>
    /// Gets a value indicating whether to sort in descending order.
    /// Default is false (ascending order).
    /// </summary>
    public bool SortDescending { get; init; }
}


