using Signal9.Shared.DTOs.Base;
using Signal9.Shared.DTOs.Core;
using Signal9.Shared.Models;

namespace Signal9.Shared.Services;

/// <summary>
/// Service interface for agent management operations using Entity Framework with unified hierarchy support
/// </summary>
public interface IAgentService
{
    /// <summary>
    /// Get all agents with advanced filtering and paging
    /// </summary>
    Task<PagedResponse<AgentDto>> GetAgentsAsync(AgentQueryRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get all agents with simple paging (legacy support)
    /// </summary>
    Task<PagedResponse<object>> GetAgentsAsync(int page = 1, int pageSize = 50, CancellationToken cancellationToken = default);

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
    Task<bool> DeleteAgentAsync(Guid agentId, bool preserveData = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// Generate agent configuration for registration
    /// </summary>
    Task<AgentConfigurationResponse> GenerateAgentConfigurationAsync(Guid agentId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get agent telemetry data with filtering
    /// </summary>
    Task<PagedResponse<TelemetryDataDto>> GetAgentTelemetryAsync(Guid agentId, DateTime? from = null, DateTime? to = null, int page = 1, int pageSize = 50, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get agent command history
    /// </summary>
    Task<PagedResponse<AgentCommandDto>> GetAgentCommandsAsync(Guid agentId, int page = 1, int pageSize = 50, CancellationToken cancellationToken = default);
}
