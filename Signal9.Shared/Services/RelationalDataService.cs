using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Signal9.Shared.Data;
using Signal9.Shared.Interfaces;
using Signal9.Shared.Models;
using Signal9.Shared.Models.Base;

namespace Signal9.Shared.Services;

/// <summary>
/// Enhanced relational data service implementing Microsoft's multi-tenant patterns
/// Provides optimized data access for Azure SQL Database entities
/// </summary>
public class RelationalDataService : IRelationalDataService
{
    private readonly Signal9DbContext _context;
    private readonly ILogger<RelationalDataService> _logger;
    private readonly string? _currentTenantId;

    public RelationalDataService(Signal9DbContext context, ILogger<RelationalDataService> logger, string? currentTenantId = null)
    {
        _context = context;
        _logger = logger;
        _currentTenantId = currentTenantId;
    }

    public async Task<Agent?> GetAgentAsync(Guid tenantId, string agentId)
    {
        return await GetAgentByIdAsync(tenantId.ToString(), agentId);
    }

    #region Agent Repository Implementation

    public async Task<Agent?> GetAgentByIdAsync(string tenantId, string agentId)
    {
        try
        {
            return await _context.Agents
                .FirstOrDefaultAsync(a => a.TenantId == tenantId && a.Id == agentId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving agent {AgentId} for tenant {TenantId}", agentId, tenantId);
            throw;
        }
    }

    public async Task<Agent?> GetAgentByMachineNameAsync(string tenantId, string machineName)
    {
        try
        {
            return await _context.Agents
                .FirstOrDefaultAsync(a => a.TenantId == tenantId && a.MachineName == machineName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving agent by machine name {MachineName} for tenant {TenantId}", machineName, tenantId);
            throw;
        }
    }

    public async Task<IEnumerable<Agent>> GetAgentsByStatusAsync(string tenantId, AgentStatus status)
    {
        try
        {
            return await _context.Agents
                .Where(a => a.TenantId == tenantId && a.Status == status)
                .OrderBy(a => a.MachineName)
                .ToListAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving agents by status {Status} for tenant {TenantId}", status, tenantId);
            throw;
        }
    }

    public async Task<IEnumerable<Agent>> GetRecentlySeenAgentsAsync(string tenantId, TimeSpan timeSpan)
    {
        try
        {
            var cutoffTime = DateTime.UtcNow - timeSpan;
            return await _context.Agents
                .Where(a => a.TenantId == tenantId && a.LastSeen >= cutoffTime)
                .OrderByDescending(a => a.LastSeen)
                .ToListAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving recently seen agents for tenant {TenantId}", tenantId);
            throw;
        }
    }

    public async Task<Agent> CreateAgentAsync(Agent agent)
    {
        try
        {
            // Validate tenant isolation
            if (_currentTenantId != null && agent.TenantId != _currentTenantId)
            {
                throw new UnauthorizedAccessException("Cannot create agent for different tenant");
            }

            _context.Agents.Add(agent);
            await _context.SaveChangesAsync();

            _logger.LogInformation("Agent {AgentId} created successfully for tenant {TenantId}", agent.Id, agent.TenantId);
            return agent;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating agent for tenant {TenantId}", agent.TenantId);
            throw;
        }
    }

    public async Task<Agent> UpdateAgentAsync(Agent agent)
    {
        try
        {
            // Validate tenant isolation
            if (_currentTenantId != null && agent.TenantId != _currentTenantId)
            {
                throw new UnauthorizedAccessException("Cannot update agent for different tenant");
            }

            _context.Agents.Update(agent);
            await _context.SaveChangesAsync();

            _logger.LogInformation("Agent {AgentId} updated successfully for tenant {TenantId}", agent.Id, agent.TenantId);
            return agent;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating agent {AgentId} for tenant {TenantId}", agent.Id, agent.TenantId);
            throw;
        }
    }

    public async Task UpdateAgentLastSeenAsync(string agentId, DateTime lastSeen)
    {
        try
        {
            var agent = await _context.Agents.FirstOrDefaultAsync(a => a.Id == agentId);
            if (agent != null)
            {
                agent.LastSeen = lastSeen;
                agent.Status = AgentStatus.Online;
                await _context.SaveChangesAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating last seen for agent {AgentId}", agentId);
            throw;
        }
    }

    #endregion

    #region Agent Command Repository Implementation

    public async Task<AgentCommand?> GetCommandByIdAsync(string tenantId, string commandId)
    {
        try
        {
            return await _context.AgentCommands
                .FirstOrDefaultAsync(c => c.TenantId == tenantId && c.Id == commandId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving command {CommandId} for tenant {TenantId}", commandId, tenantId);
            throw;
        }
    }

    public async Task<IEnumerable<AgentCommand>> GetCommandsByAgentIdAsync(string tenantId, string agentId)
    {
        try
        {
            return await _context.AgentCommands
                .Where(c => c.TenantId == tenantId && c.AgentId == agentId)
                .OrderByDescending(c => c.ScheduledAt)
                .ToListAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving commands for agent {AgentId} in tenant {TenantId}", agentId, tenantId);
            throw;
        }
    }

    public async Task<IEnumerable<AgentCommand>> GetPendingCommandsAsync(string tenantId)
    {
        try
        {
            return await _context.AgentCommands
                .Where(c => c.TenantId == tenantId && c.Status == CommandStatus.Pending)
                .OrderBy(c => c.Priority)
                .ThenBy(c => c.ScheduledAt)
                .ToListAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving pending commands for tenant {TenantId}", tenantId);
            throw;
        }
    }

    public async Task<AgentCommand> CreateCommandAsync(AgentCommand command)
    {
        try
        {
            // Validate tenant isolation
            if (_currentTenantId != null && command.TenantId != _currentTenantId)
            {
                throw new UnauthorizedAccessException("Cannot create command for different tenant");
            }

            _context.AgentCommands.Add(command);
            await _context.SaveChangesAsync();

            _logger.LogInformation("Command {CommandId} created successfully for agent {AgentId} in tenant {TenantId}", 
                command.Id, command.AgentId, command.TenantId);
            return command;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating command for agent {AgentId} in tenant {TenantId}", 
                command.AgentId, command.TenantId);
            throw;
        }
    }

    public async Task UpdateCommandStatusAsync(string commandId, CommandStatus status, string? result = null, string? errorMessage = null)
    {
        try
        {
            var command = await _context.AgentCommands.FirstOrDefaultAsync(c => c.Id == commandId);
            if (command != null)
            {
                command.Status = status;
                command.Result = result;
                command.ErrorMessage = errorMessage;

                switch (status)
                {
                    case CommandStatus.Running:
                        command.StartedAt = DateTime.UtcNow;
                        break;
                    case CommandStatus.Completed:
                    case CommandStatus.Failed:
                    case CommandStatus.Cancelled:
                    case CommandStatus.Timeout:
                        command.CompletedAt = DateTime.UtcNow;
                        break;
                }

                await _context.SaveChangesAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating command status for command {CommandId}", commandId);
            throw;
        }
    }

    #endregion

    #region Tenant Repository Implementation

    public async Task<Tenant?> GetTenantByCodeAsync(string tenantCode)
    {
        try
        {
            return await _context.Tenants
                .FirstOrDefaultAsync(t => t.TenantCode == tenantCode);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving tenant by code {TenantCode}", tenantCode);
            throw;
        }
    }

    public async Task<Tenant?> GetTenantByApiKeyAsync(string apiKey)
    {
        try
        {
            return await _context.Tenants
                .FirstOrDefaultAsync(t => t.ApiKey == apiKey);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving tenant by API key");
            throw;
        }
    }

    public async Task<bool> ValidateTenantCodeAsync(string tenantCode)
    {
        try
        {
            return await _context.Tenants
                .AnyAsync(t => t.TenantCode == tenantCode && t.Status == TenantStatus.Active);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error validating tenant code {TenantCode}", tenantCode);
            throw;
        }
    }

    public async Task<Tenant> CreateTenantAsync(Tenant tenant)
    {
        try
        {
            _context.Tenants.Add(tenant);
            await _context.SaveChangesAsync();

            _logger.LogInformation("Tenant {TenantId} created successfully with code {TenantCode}", 
                tenant.Id, tenant.TenantCode);
            return tenant;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating tenant with code {TenantCode}", tenant.TenantCode);
            throw;
        }
    }

    #endregion

    #region Generic Repository Operations

    public async Task<T?> GetByIdAsync<T>(string id) where T : class, IEntity
    {
        try
        {
            return await _context.Set<T>().FindAsync(id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving entity {EntityType} with id {Id}", typeof(T).Name, id);
            throw;
        }
    }

    public async Task<IEnumerable<T>> GetAllAsync<T>() where T : class, IEntity
    {
        try
        {
            return await _context.Set<T>().ToListAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving all entities of type {EntityType}", typeof(T).Name);
            throw;
        }
    }

    public async Task<int> GetCountAsync<T>() where T : class, IEntity
    {
        try
        {
            return await _context.Set<T>().CountAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error counting entities of type {EntityType}", typeof(T).Name);
            throw;
        }
    }

    #endregion

    public void Dispose()
    {
        _context.Dispose();
    }
}
