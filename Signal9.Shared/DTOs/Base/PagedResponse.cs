using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace Signal9.Shared.DTOs.Base;

/// <summary>
/// Generic paginated response for list-based queries in the Signal9 RMM system.
/// Provides a standardized way to return paginated data with metadata.
/// </summary>
/// <typeparam name="T">The type of items in the paginated collection.</typeparam>
public record PagedResponse<T> : BaseDto<Guid>
{
    /// <summary>
    /// Gets the collection of items for the current page.
    /// Cannot be null, but can be empty for no results.
    /// </summary>
    [Required(ErrorMessage = "Items collection is required")]
    public required IReadOnlyList<T> Items { get; init; }

    /// <summary>
    /// Gets the current page number (1-based).
    /// Must be greater than 0.
    /// </summary>
    [Range(1, int.MaxValue, ErrorMessage = "Page must be greater than 0")]
    public required int Page { get; init; }

    /// <summary>
    /// Gets the number of items per page.
    /// Must be between 1 and 1000 to prevent performance issues.
    /// </summary>
    [Range(1, 1000, ErrorMessage = "PageSize must be between 1 and 1000")]
    public required int PageSize { get; init; }

    /// <summary>
    /// Gets the total number of items across all pages.
    /// Cannot be negative.
    /// </summary>
    [Range(0, int.MaxValue, ErrorMessage = "TotalCount cannot be negative")]
    public required int TotalCount { get; init; }

    /// <summary>
    /// Gets the total number of pages based on the total count and page size.
    /// Calculated automatically to ensure consistency.
    /// </summary>
    public int TotalPages => PageSize > 0 ? (int)Math.Ceiling(TotalCount / (double)PageSize) : 0;

    /// <summary>
    /// Gets a value indicating whether there is a next page.
    /// Calculated automatically based on current page and total pages.
    /// </summary>
    public bool HasNextPage => Page < TotalPages;

    /// <summary>
    /// Gets a value indicating whether there is a previous page.
    /// Calculated automatically based on current page.
    /// </summary>
    public bool HasPreviousPage => Page > 1;
}


