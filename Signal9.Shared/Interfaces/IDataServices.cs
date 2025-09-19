using Azure.Data.Tables;
using Signal9.Shared.DTOs;
using Signal9.Shared.Models;

namespace Signal9.Shared.Interfaces;

/// <summary>
/// Interface for relational data operations using Entity Framework and SQL Server.
/// Works directly with Entity Framework models.
/// </summary>
public interface IRelationalDataService
{
    // Agent operations using entities
    Task<Agent?> GetAgentByIdAsync(string tenantId, string agentId);
    Task<IEnumerable<Agent>> GetAgentsByStatusAsync(string tenantId, AgentStatus status);
    Task<Agent> CreateAgentAsync(Agent agent);
    Task<Agent> UpdateAgentAsync(Agent agent);
    
    // Bridge method for SignalR hub compatibility
    Task<Agent?> GetAgentAsync(Guid tenantId, string agentId);
}

/// <summary>
/// Interface for non-relational data operations using Azure Table Storage.
/// Handles high-volume telemetry data using DTO-based table entities.
/// </summary>
public interface ITableStorageService
{
    // Telemetry operations using DTOs
    Task<TelemetryData> CreateTelemetryAsync(TelemetryData telemetry);
    Task<IEnumerable<TelemetryData>> BatchCreateTelemetryAsync(IEnumerable<TelemetryData> telemetryData);
    Task<IEnumerable<TelemetryData>> GetTelemetryByAgentAsync(Guid tenantId, Guid agentId, DateTime? fromDate = null,
        DateTime? toDate = null);
    Task<TelemetryData?> GetTelemetryAsync(string tenantId, string rowKey);
    Task CleanupOldTelemetryAsync(string tenantId, DateTime cutoffDate);
    Task<T> UpsertEntityAsync<T>(string tableName, T entity) where T : class, ITableEntity;
}

/// <summary>
/// Interface for SignalR hub operations for real-time agent communication.
/// </summary>
public interface IAgentHubService
{
    Task SendCommandToAgentAsync(string agentId, object command);
    Task NotifyAgentStatusChangeAsync(Guid tenantId, string agentId, string status);
    Task NotifyTelemetryUpdateAsync(Guid tenantId, string agentId, TelemetryData telemetryData);
    Task JoinTenantGroupAsync(string connectionId, Guid tenantId);
    Task LeaveTenantGroupAsync(string connectionId, Guid tenantId);
}
