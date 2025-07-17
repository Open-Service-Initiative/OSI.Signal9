using System.ComponentModel.DataAnnotations;
using Signal9.Shared.Models;

namespace Signal9.Shared.DTOs;

/// <summary>
/// Request DTO for querying agents with filtering and pagination
/// </summary>
public record AgentQueryRequest
{
    /// <summary>
    /// Unique identifier for this request
    /// </summary>
    public Guid Id { get; init; } = Guid.NewGuid();
    
    /// <summary>
    /// The tenant ID to query agents for (unified hierarchy)
    /// </summary>
    public Guid? ParentId { get; init; }
    /// <summary>
    /// Filter by agent status
    /// </summary>
    public AgentStatus? Status { get; init; }
    
    /// <summary>
    /// Filter by platform/OS
    /// </summary>
    public string? Platform { get; init; }
    
    /// <summary>
    /// Search term for machine names, etc.
    /// </summary>
    public string? Search { get; init; }
    
    /// <summary>
    /// Filter by online status
    /// </summary>
    public bool? IsOnline { get; init; }
    
    /// <summary>
    /// Page number for pagination (1-based)
    /// </summary>
    [Range(1, int.MaxValue, ErrorMessage = "Page must be positive")]
    public int Page { get; init; } = 1;
    
    /// <summary>
    /// Page size for pagination
    /// </summary>
    [Range(1, 100, ErrorMessage = "PageSize must be between 1 and 100")]
    public int PageSize { get; init; } = 20;
    
    /// <summary>
    /// Field to sort by
    /// </summary>
    public string SortBy { get; init; } = "MachineName";
    
    /// <summary>
    /// Sort order (asc/desc)
    /// </summary>
    public string SortOrder { get; init; } = "asc";
    
    // ParentId inherited from BaseDto<Guid> represents the tenant to query agents for
}
