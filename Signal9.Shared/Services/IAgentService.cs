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
    /// Get paginated agents with filtering
    /// </summary>
    Task<PagedResponse<AgentDto>> GetAgentsAsync(AgentQueryRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get agent by ID with optional includes
    /// </summary>
    Task<AgentDto?> GetAgentByIdAsync(Guid agentId, bool includeMetrics = false, bool includeTelemetry = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// Register a new agent
    /// </summary>
    Task<AgentDto> RegisterAgentAsync(AgentDto agentDto, CancellationToken cancellationToken = default);

    /// <summary>
    /// Update agent information
    /// </summary>
    Task<AgentDto?> UpdateAgentAsync(Guid agentId, AgentUpdateRequest updateRequest, CancellationToken cancellationToken = default);

    /// <summary>
    /// Delete agent with optional data preservation
    /// </summary>
    Task DeleteAgentAsync(Guid agentId, bool preserveData = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// Generate agent configuration for registration
    /// </summary>
    Task<Dictionary<string, object>> GenerateAgentConfigurationAsync(Guid agentId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Service interface for modern data mapping operations
/// </summary>
public interface IDataMappingService
{
    /// <summary>
    /// Process bulk operations with error handling
    /// </summary>
    Task<BulkOperationResponse<TResult>> ProcessBulkOperation<TSource, TResult>(
        IEnumerable<TSource> items, 
        Func<TSource, TResult> processor, 
        string operationName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Convert entity to DTO with proper mapping
    /// </summary>
    TDto MapToDto<TEntity, TDto>(TEntity entity) where TDto : class;

    /// <summary>
    /// Convert DTO to entity with proper mapping
    /// </summary>
    TEntity MapToEntity<TDto, TEntity>(TDto dto) where TEntity : class;
}

/// <summary>
/// Bulk operation response for efficient batch processing
/// </summary>
public record BulkOperationResponse<T> : TenantScopedDto
{
    /// <summary>
    /// Successfully processed items
    /// </summary>
    public required IEnumerable<T> SuccessfulItems { get; init; }

    /// <summary>
    /// Failed items with error details
    /// </summary>
    public required IEnumerable<FailedItem<T>> FailedItems { get; init; }

    /// <summary>
    /// Total count of processed items
    /// </summary>
    public int TotalCount => SuccessfulItems.Count() + FailedItems.Count();

    /// <summary>
    /// Count of successful items
    /// </summary>
    public int SuccessCount => SuccessfulItems.Count();

    /// <summary>
    /// Count of failed items
    /// </summary>
    public int FailureCount => FailedItems.Count();

    /// <summary>
    /// Overall success indicator
    /// </summary>
    public bool IsSuccess => FailureCount == 0;

    /// <summary>
    /// Success percentage
    /// </summary>
    public double SuccessPercentage => TotalCount > 0 ? (double)SuccessCount / TotalCount * 100 : 0;
}

/// <summary>
/// Failed item container for bulk operations
/// </summary>
public record FailedItem<T>
{
    /// <summary>
    /// The item that failed processing
    /// </summary>
    public required T Item { get; init; }

    /// <summary>
    /// Error message describing the failure
    /// </summary>
    public required string ErrorMessage { get; init; }

    /// <summary>
    /// Exception details if available
    /// </summary>
    public string? ExceptionDetails { get; init; }

    /// <summary>
    /// Timestamp when the failure occurred
    /// </summary>
    public DateTime FailedAt { get; init; } = DateTime.UtcNow;
}
