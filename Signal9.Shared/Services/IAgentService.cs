using Signal9.Shared.DTOs;
using Signal9.Shared.DTOs.Base;
using Signal9.Shared.Models;

namespace Signal9.Shared.Services;

/// <summary>
/// Service interface for agent management operations using Entity Framework
/// </summary>
public interface IAgentService
{
    /// <summary>
    /// Get all agents with paging
    /// </summary>
    Task<PagedResponse<object>> GetAgentsAsync(int page = 1, int pageSize = 50, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get agent by ID with optional includes
    /// </summary>
    Task<object?> GetAgentByIdAsync(Guid agentId, bool includeMetrics = false, bool includeTelemetry = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// Register a new agent
    /// </summary>
    Task<object> RegisterAgentAsync(object agentDto, CancellationToken cancellationToken = default);

    /// <summary>
    /// Update agent information
    /// </summary>
    Task<object?> UpdateAgentAsync(Guid agentId, object updateRequest, CancellationToken cancellationToken = default);

    /// <summary>
    /// Delete agent with optional data preservation
    /// </summary>
    Task<bool> DeleteAgentAsync(Guid agentId, bool preserveData = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get agent telemetry data with filtering
    /// </summary>
    Task<PagedResponse<object>> GetAgentTelemetryAsync(Guid agentId, DateTime? from = null, DateTime? to = null, int page = 1, int pageSize = 50, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get agent command history
    /// </summary>
    Task<PagedResponse<object>> GetAgentCommandsAsync(Guid agentId, int page = 1, int pageSize = 50, CancellationToken cancellationToken = default);

    /// <summary>
    /// Bulk operations for efficiency
    /// </summary>
    Task<BulkOperationResponse<object>> BulkUpdateAgentsAsync(IEnumerable<object> agents, CancellationToken cancellationToken = default);

    /// <summary>
    /// Universal mapping interface for flexibility
    /// </summary>
    TEntity MapToEntity<TDto, TEntity>(TDto dto) where TEntity : class;
}

/// <summary>
/// Bulk operation response for efficient batch processing
/// </summary>
public record BulkOperationResponse<T> : BaseDto<Guid>
{
    /// <summary>
    /// Successfully processed items
    /// </summary>
    public List<T> SuccessfulItems { get; init; } = new();
    
    /// <summary>
    /// Failed items with error details
    /// </summary>
    public List<BulkOperationError<T>> FailedItems { get; init; } = new();
    
    /// <summary>
    /// Overall operation status
    /// </summary>
    public bool IsSuccess => FailedItems.Count == 0;
    
    /// <summary>
    /// Performance metrics
    /// </summary>
    public TimeSpan ProcessingTime { get; init; }
    public int TotalProcessed => SuccessfulItems.Count + FailedItems.Count;
}

/// <summary>
/// Error details for bulk operations
/// </summary>
public record BulkOperationError<T>
{
    public required T Item { get; init; }
    public required string ErrorMessage { get; init; }
    public string? ErrorCode { get; init; }
    public Exception? Exception { get; init; }
}

/// <summary>
/// Paged response wrapper for collections
/// </summary>
public record PagedResponse<T> : BaseDto<Guid>
{
    public required IEnumerable<T> Items { get; init; }
    public required int TotalCount { get; init; }
    public required int PageNumber { get; init; }
    public required int PageSize { get; init; }
    public int TotalPages => (int)Math.Ceiling((double)TotalCount / PageSize);
    public bool HasNextPage => PageNumber < TotalPages;
    public bool HasPreviousPage => PageNumber > 1;
}
