using Azure.Data.Tables;
using Microsoft.Extensions.Logging;
using Signal9.Shared.Interfaces;
using Signal9.Shared.Models;
using Signal9.Shared.Models.Base;

namespace Signal9.Shared.Services;

/// <summary>
/// Enhanced table storage service optimized for Azure Tables
/// Implements Microsoft's recommended patterns for time-series data and multi-tenant isolation
/// </summary>
public class TableStorageService(TableServiceClient tableServiceClient, ILogger<TableStorageService> logger) : ITableStorageService
{
    private readonly string _telemetryTableName = "telemetrydata";

    #region Telemetry Data Repository Implementation

    public async Task<TelemetryData?> GetTelemetryAsync(string tenantId, string rowKey)
    {
        try
        {
            var tableClient = await GetTableClientAsync(_telemetryTableName);
            var response = await tableClient.GetEntityIfExistsAsync<TelemetryData>(tenantId, rowKey);
            return response.HasValue ? response.Value : null;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error retrieving telemetry data {RowKey} for tenant {TenantId}", rowKey, tenantId);
            throw;
        }
    }

    public async Task<IEnumerable<TelemetryData>> GetTelemetryByAgentAsync(Guid tenantId, Guid agentId,
        DateTime? fromDate = null, DateTime? toDate = null)
    {
        try
        {
            var tableClient = await GetTableClientAsync(_telemetryTableName);
            var results = new List<TelemetryData>();

            // Build OData filter for agent-specific queries
            var filter = $"PartitionKey eq '{tenantId}' and AgentId eq '{agentId}'";
            
            if (fromDate.HasValue || toDate.HasValue)
            {
                // Convert dates to reverse ticks for time-based filtering
                if (fromDate.HasValue)
                {
                    var fromTicks = DateTime.MaxValue.Ticks - fromDate.Value.Ticks;
                    filter += $" and RowKey le '{agentId}_{fromTicks:D19}'";
                }
                
                if (toDate.HasValue)
                {
                    var toTicks = DateTime.MaxValue.Ticks - toDate.Value.Ticks;
                    filter += $" and RowKey ge '{agentId}_{toTicks:D19}'";
                }
            }

            await foreach (var entity in tableClient.QueryAsync<TelemetryData>(filter))
            {
                results.Add(entity);
            }

            return results.OrderByDescending(t => t.CreatedAt);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error retrieving telemetry data for agent {AgentId} in tenant {TenantId}", agentId, tenantId);
            throw;
        }
    }

    public async Task<IEnumerable<TelemetryData>> GetTelemetryByTypeAsync(string tenantId, TelemetryType telemetryType, DateTime? fromDate = null, DateTime? toDate = null)
    {
        try
        {
            var tableClient = await GetTableClientAsync(_telemetryTableName);
            var results = new List<TelemetryData>();

            // Build OData filter for telemetry type queries
            var filter = $"PartitionKey eq '{tenantId}' and TelemetryType eq {(int)telemetryType}";

            await foreach (var entity in tableClient.QueryAsync<TelemetryData>(filter))
            {
                if (fromDate.HasValue && entity.CreatedAt < fromDate.Value) continue;
                if (toDate.HasValue && entity.CreatedAt > toDate.Value) continue;
                
                results.Add(entity);
            }

            return results.OrderByDescending(t => t.CreatedAt);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error retrieving telemetry data by type {TelemetryType} for tenant {TenantId}", telemetryType, tenantId);
            throw;
        }
    }

    public async Task<IEnumerable<TelemetryData>> GetRecentTelemetryAsync(string tenantId, TimeSpan timeSpan)
    {
        try
        {
            var tableClient = await GetTableClientAsync(_telemetryTableName);
            var results = new List<TelemetryData>();
            var cutoffTime = DateTime.UtcNow - timeSpan;

            var filter = $"PartitionKey eq '{tenantId}' and CreatedAt ge datetime'{cutoffTime:yyyy-MM-ddTHH:mm:ss.fffZ}'";

            await foreach (var entity in tableClient.QueryAsync<TelemetryData>(filter))
            {
                results.Add(entity);
            }

            return results.OrderByDescending(t => t.CreatedAt);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error retrieving recent telemetry data for tenant {TenantId}", tenantId);
            throw;
        }
    }

    public async Task<TelemetryData> CreateTelemetryAsync(TelemetryData telemetry)
    {
        try
        {
            var tableClient = await GetTableClientAsync(_telemetryTableName);
            await tableClient.UpsertEntityAsync(telemetry);

            logger.LogDebug("Telemetry data created successfully for agent {AgentId} in tenant {TenantId}", 
                telemetry.AgentId, telemetry.ParentId);
            return telemetry;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error creating telemetry data for agent {AgentId} in tenant {TenantId}", 
                telemetry.AgentId, telemetry.ParentId);
            throw;
        }
    }

    public async Task<IEnumerable<TelemetryData>> BatchCreateTelemetryAsync(IEnumerable<TelemetryData> telemetryData)
    {
        try
        {
            var tableClient = await GetTableClientAsync(_telemetryTableName);
            var batches = telemetryData.GroupBy(t => t.PartitionKey).ToList();

            foreach (var batch in batches)
            {
                var batchItems = batch.Take(100).ToList(); // Azure Tables batch limit
                var batchOperations = batchItems.Select(item => new TableTransactionAction(TableTransactionActionType.UpsertReplace, item));
                
                await tableClient.SubmitTransactionAsync(batchOperations);
            }

            logger.LogInformation("Batch created {Count} telemetry data entries", telemetryData.Count());
            return telemetryData;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error batch creating telemetry data");
            throw;
        }
    }

    public async Task CleanupOldTelemetryAsync(string tenantId, DateTime cutoffDate)
    {
        try
        {
            var tableClient = await GetTableClientAsync(_telemetryTableName);
            var filter = $"PartitionKey eq '{tenantId}' and CreatedAt lt datetime'{cutoffDate:yyyy-MM-ddTHH:mm:ss.fffZ}'";
            var entitiesToDelete = new List<(string partitionKey, string rowKey)>();

            await foreach (var entity in tableClient.QueryAsync<TelemetryData>(filter, select: ["PartitionKey", "RowKey"]))
            {
                entitiesToDelete.Add((entity.PartitionKey, entity.RowKey));
            }

            // Delete in batches
            var batches = entitiesToDelete.GroupBy(e => e.partitionKey).ToList();
            foreach (var batch in batches)
            {
                var batchItems = batch.Take(100).ToList(); // Azure Tables batch limit
                var batchOperations = batchItems.Select(item => new TableTransactionAction(TableTransactionActionType.Delete, new TableEntity(item.partitionKey, item.rowKey)));
                
                if (batchOperations.Any())
                {
                    await tableClient.SubmitTransactionAsync(batchOperations);
                }
            }

            logger.LogInformation("Cleaned up {Count} old telemetry entries for tenant {TenantId}", 
                entitiesToDelete.Count, tenantId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error cleaning up old telemetry data for tenant {TenantId}", tenantId);
            throw;
        }
    }

    #endregion

    #region Generic Table Operations

    public async Task<T?> GetEntityAsync<T>(string tableName, string partitionKey, string rowKey) where T : class, ITableEntity, new()
    {
        try
        {
            var tableClient = await GetTableClientAsync(tableName);
            var response = await tableClient.GetEntityIfExistsAsync<T>(partitionKey, rowKey);
            return response.HasValue ? response.Value : null;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error retrieving entity from table {TableName}", tableName);
            throw;
        }
    }

    public async Task<IEnumerable<T>> QueryEntitiesAsync<T>(string tableName, string filter) where T : class, ITableEntity, new()
    {
        try
        {
            var tableClient = await GetTableClientAsync(tableName);
            var results = new List<T>();

            await foreach (var entity in tableClient.QueryAsync<T>(filter))
            {
                results.Add(entity);
            }

            return results;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error querying entities from table {TableName}", tableName);
            throw;
        }
    }

    public async Task<T> UpsertEntityAsync<T>(string tableName, T entity) where T : class, ITableEntity
    {
        try
        {
            var tableClient = await GetTableClientAsync(tableName);
            await tableClient.UpsertEntityAsync(entity);
            return entity;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error upserting entity to table {TableName}", tableName);
            throw;
        }
    }

    public async Task DeleteEntityAsync(string tableName, string partitionKey, string rowKey)
    {
        try
        {
            var tableClient = await GetTableClientAsync(tableName);
            await tableClient.DeleteEntityAsync(partitionKey, rowKey);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error deleting entity from table {TableName}", tableName);
            throw;
        }
    }

    #endregion

    #region Helper Methods

    private async Task<TableClient> GetTableClientAsync(string tableName)
    {
        var tableClient = tableServiceClient.GetTableClient(tableName);
        await tableClient.CreateIfNotExistsAsync();
        return tableClient;
    }

    #endregion

    public void Dispose()
    {
        // TableServiceClient doesn't implement IDisposable
    }
}
