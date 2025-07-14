using System.Linq.Expressions;
using Signal9.Shared.Models;
using Signal9.Shared.Models.Base;

namespace Signal9.Shared.Interfaces;

/// <summary>
/// Generic repository interface for common CRUD operations
/// </summary>
/// <typeparam name="TEntity">Entity type</typeparam>
public interface IRepository<TEntity> where TEntity : class
{
    Task<TEntity?> GetByIdAsync(string id);
    Task<IEnumerable<TEntity>> GetAllAsync();
    Task<IEnumerable<TEntity>> FindAsync(Expression<Func<TEntity, bool>> predicate);
    Task<TEntity> AddAsync(TEntity entity);
    Task<TEntity> UpdateAsync(TEntity entity);
    Task DeleteAsync(string id);
    Task<bool> ExistsAsync(string id);
    Task<int> CountAsync();
}

/// <summary>
/// Repository interface for Azure Tables entities
/// </summary>
/// <typeparam name="TTableEntity">Table entity type</typeparam>
public interface ITableRepository<TTableEntity> where TTableEntity : BaseTableEntity
{
    Task<TTableEntity?> GetAsync(string partitionKey, string rowKey);
    Task<IEnumerable<TTableEntity>> GetByPartitionAsync(string partitionKey);
    Task<IEnumerable<TTableEntity>> QueryAsync(string filter);
    Task<TTableEntity> UpsertAsync(TTableEntity entity);
    Task DeleteAsync(string partitionKey, string rowKey);
    Task<IEnumerable<TTableEntity>> BatchUpsertAsync(IEnumerable<TTableEntity> entities);
    Task BatchDeleteAsync(IEnumerable<(string partitionKey, string rowKey)> keys);
}

/// <summary>
/// Tenant-scoped repository interface
/// </summary>
/// <typeparam name="TEntity">Entity type</typeparam>
public interface ITenantScopedRepository<TEntity> : IRepository<TEntity> 
    where TEntity : class, ITenantScopedEntity
{
    Task<IEnumerable<TEntity>> GetByTenantAsync(string tenantId);
    Task<TEntity?> GetByTenantAndIdAsync(string tenantId, string id);
    Task<int> CountByTenantAsync(string tenantId);
}

/// <summary>
/// Agent-specific repository interface
/// </summary>
public interface IAgentRepository : ITenantScopedRepository<Agent>
{
    Task<Agent?> GetByMachineNameAsync(string tenantId, string machineName);
    Task<IEnumerable<Agent>> GetByStatusAsync(string tenantId, AgentStatus status);
    Task<IEnumerable<Agent>> GetRecentlySeenAsync(string tenantId, TimeSpan timeSpan);
    Task UpdateLastSeenAsync(string agentId, DateTime lastSeen);
}

/// <summary>
/// Agent command repository interface
/// </summary>
public interface IAgentCommandRepository : ITenantScopedRepository<AgentCommand>
{
    Task<IEnumerable<AgentCommand>> GetByAgentIdAsync(string tenantId, string agentId);
    Task<IEnumerable<AgentCommand>> GetByStatusAsync(string tenantId, CommandStatus status);
    Task<IEnumerable<AgentCommand>> GetPendingCommandsAsync(string tenantId);
    Task UpdateCommandStatusAsync(string commandId, CommandStatus status, string? result = null, string? errorMessage = null);
}

/// <summary>
/// Telemetry data repository interface for Azure Tables
/// </summary>
public interface ITelemetryRepository : ITableRepository<TelemetryData>
{
    Task<IEnumerable<TelemetryData>> GetByAgentAsync(string tenantId, string agentId, DateTime? fromDate = null, DateTime? toDate = null);
    Task<IEnumerable<TelemetryData>> GetByTypeAsync(string tenantId, TelemetryType telemetryType, DateTime? fromDate = null, DateTime? toDate = null);
    Task<IEnumerable<TelemetryData>> GetRecentAsync(string tenantId, TimeSpan timeSpan);
    Task CleanupOldDataAsync(string tenantId, DateTime cutoffDate);
}

/// <summary>
/// Tenant repository interface
/// </summary>
public interface ITenantRepository : IRepository<Tenant>
{
    Task<Tenant?> GetByTenantCodeAsync(string tenantCode);
    Task<Tenant?> GetByApiKeyAsync(string apiKey);
    Task<IEnumerable<Tenant>> GetByStatusAsync(TenantStatus status);
    Task<bool> ValidateTenantCodeAsync(string tenantCode);
}
