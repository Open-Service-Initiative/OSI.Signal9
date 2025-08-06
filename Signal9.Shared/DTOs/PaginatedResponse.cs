using System.ComponentModel.DataAnnotations;
using Signal9.Shared.DTOs.Base;

namespace Signal9.Shared.DTOs;

/// <summary>
/// Paginated response wrapper for MSP platform APIs
/// Provides pagination metadata along with the requested data
/// </summary>
public record PaginatedResponse<T> : BaseDto<Guid>
{
    /// <summary>
    /// The data items for the current page
    /// </summary>
    public IEnumerable<T> Data { get; set; } = [];

    /// <summary>
    /// Legacy property for backward compatibility
    /// </summary>
    public IEnumerable<T> Items
    {
        get => Data;
        set => Data = value;
    }

    /// <summary>
    /// Current page number (1-based)
    /// </summary>
    [Range(1, int.MaxValue, ErrorMessage = "Page must be greater than 0")]
    public int Page { get; set; } = 1;

    /// <summary>
    /// Number of items per page
    /// </summary>
    [Range(1, 1000, ErrorMessage = "PageSize must be between 1 and 1000")]
    public int PageSize { get; set; } = 50;

    /// <summary>
    /// Total number of items across all pages
    /// </summary>
    [Range(0, long.MaxValue, ErrorMessage = "TotalCount must be non-negative")]
    public long TotalCount { get; set; }

    /// <summary>
    /// Total number of pages - can be set directly or computed
    /// </summary>
    public int TotalPages { get; set; }

    /// <summary>
    /// Whether there are more pages after the current one
    /// </summary>
    public bool HasNextPage => Page < TotalPages;

    /// <summary>
    /// Whether there are pages before the current one
    /// </summary>
    public bool HasPreviousPage => Page > 1;

    /// <summary>
    /// Index of the first item on this page (0-based)
    /// </summary>
    public long StartIndex => PageSize > 0 ? (Page - 1) * PageSize : 0;

    /// <summary>
    /// Index of the last item on this page (0-based)
    /// </summary>
    public long EndIndex => Math.Min(StartIndex + PageSize - 1, TotalCount - 1);
}



