using System.ComponentModel.DataAnnotations;
using Signal9.Shared.Models;
using Signal9.Shared.DTOs.Base;

namespace Signal9.Shared.DTOs;

/// <summary>
/// Request DTO for querying agents with filtering and pagination
/// </summary>
public record AgentQueryRequest : BaseDto<Guid>
{
    /// <summary>
    /// Status filter
    /// </summary>
    public AgentStatus? Status { get; init; }
    
    /// <summary>
    /// Platform filter
    /// </summary>
    public string? Platform { get; init; }
    
    /// <summary>
    /// Search term
    /// </summary>
    public string? Search { get; init; }
    
    /// <summary>
    /// Online status filter
    /// </summary>
    public bool? IsOnline { get; init; }
    
    /// <summary>
    /// Page number
    /// </summary>
    [Range(1, int.MaxValue, ErrorMessage = "Page must be at least 1")]
    public int Page { get; init; } = 1;
    
    /// <summary>
    /// Page size
    /// </summary>
    [Range(1, 100, ErrorMessage = "PageSize must be between 1 and 100")]
    public int PageSize { get; init; } = 20;
    
    /// <summary>
    /// Sort field
    /// </summary>
    public string SortBy { get; init; } = "MachineName";
    
    /// <summary>
    /// Sort order
    /// </summary>
    public string SortOrder { get; init; } = "asc";
}