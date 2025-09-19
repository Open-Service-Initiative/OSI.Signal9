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
public class RelationalDataService(Signal9DbContext context, ILogger<RelationalDataService> logger, Guid? currentParentId = null) : IRelationalDataService
{
    public async Task<Agent?> GetAgentAsync(Guid parentId, Guid agentId)
    {
        return await GetAgentByIdAsync(parentId, agentId);
    }

    #region Agent Repository Implementation

    public async Task<Agent?> GetAgentByIdAsync(Guid tenantId, Guid agentId)
    {
        try
        {
            return await context.Agents
                .FirstOrDefaultAsync(a => a.ParentId == tenantId && a.Id == agentId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error retrieving agent {AgentId} for tenant {TenantId}", agentId, tenantId);
            throw;
        }
    }

    public async Task<Agent?> GetAgentByMachineNameAsync(Guid parentId, string machineName)
    {
        try
        {
            return await context.Agents
                .FirstOrDefaultAsync(a => a.ParentId == parentId && a.MachineName == machineName);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error retrieving agent by machine name {MachineName} for tenant {TenantId}", machineName, parentId);
            throw;
        }
    }

    public async Task<IEnumerable<Agent>> GetAgentsByStatusAsync(Guid parentId, AgentStatus status)
    {
        try
        {
            return await context.Agents
                .Where(a => a.ParentId == parentId && a.Status == status)
                .OrderBy(a => a.MachineName)
                .ToListAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error retrieving agents by status {Status} for tenant {TenantId}", status, parentId);
            throw;
        }
    }

    public async Task<IEnumerable<Agent>> GetRecentlySeenAgentsAsync(Guid parentId, TimeSpan timeSpan)
    {
        try
        {
            var cutoffTime = DateTime.UtcNow - timeSpan;
            return await context.Agents
                .Where(a => a.ParentId == parentId && a.LastSeen >= cutoffTime)
                .OrderByDescending(a => a.LastSeen)
                .ToListAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error retrieving recently seen agents for tenant {TenantId}", parentId);
            throw;
        }
    }

    public async Task<Agent> CreateAgentAsync(Agent agent)
    {
        try
        {
            // Validate tenant isolation
            if (currentParentId != null && agent.ParentId != currentParentId)
            {
                throw new UnauthorizedAccessException("Cannot create agent for different tenant");
            }

            context.Agents.Add(agent);
            await context.SaveChangesAsync();

            logger.LogInformation("Agent {AgentId} created successfully for tenant {TenantId}", agent.Id, agent.ParentId);
            return agent;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error creating agent for tenant {TenantId}", agent.ParentId);
            throw;
        }
    }

    public async Task<Agent> UpdateAgentAsync(Agent agent)
    {
        try
        {
            // Validate tenant isolation
            if (currentParentId != null && agent.ParentId != currentParentId)
            {
                throw new UnauthorizedAccessException("Cannot update agent for different tenant");
            }

            context.Agents.Update(agent);
            await context.SaveChangesAsync();

            logger.LogInformation("Agent {AgentId} updated successfully for tenant {TenantId}", agent.Id, agent.ParentId);
            return agent;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error updating agent {AgentId} for tenant {TenantId}", agent.Id, agent.ParentId);
            throw;
        }
    }

    public async Task UpdateAgentLastSeenAsync(Guid agentId, DateTime lastSeen)
    {
        try
        {
            var agent = await context.Agents.FirstOrDefaultAsync(a => a.Id == agentId);
            if (agent != null)
            {
                agent.LastSeen = lastSeen;
                agent.Status = AgentStatus.Online;
                await context.SaveChangesAsync();
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error updating last seen for agent {AgentId}", agentId);
            throw;
        }
    }

    #endregion

    #region Agent Command Repository Implementation

    public async Task<AgentCommand?> GetCommandByIdAsync(Guid parentId, Guid commandId)
    {
        try
        {
            return await context.AgentCommands
                .FirstOrDefaultAsync(c => c.ParentId == parentId && c.Id == commandId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error retrieving command {CommandId} for tenant {TenantId}", commandId, parentId);
            throw;
        }
    }

    public async Task<IEnumerable<AgentCommand>> GetCommandsByAgentIdAsync(Guid parentId, Guid agentId)
    {
        try
        {
            return await context.AgentCommands
                .Where(c => c.ParentId == parentId && c.AgentId == agentId)
                .OrderByDescending(c => c.ScheduledAt)
                .ToListAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error retrieving commands for agent {AgentId} in tenant {TenantId}", agentId, parentId);
            throw;
        }
    }

    public async Task<IEnumerable<AgentCommand>> GetPendingCommandsAsync(Guid parentId)
    {
        try
        {
            return await context.AgentCommands
                .Where(c => c.ParentId == parentId && c.Status == CommandStatus.Pending)
                .OrderBy(c => c.Priority)
                .ThenBy(c => c.ScheduledAt)
                .ToListAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error retrieving pending commands for tenant {TenantId}", parentId);
            throw;
        }
    }

    public async Task<AgentCommand> CreateCommandAsync(AgentCommand command)
    {
        try
        {
            // Validate tenant isolation
            if (currentParentId != null && command.ParentId != currentParentId)
            {
                throw new UnauthorizedAccessException("Cannot create command for different tenant");
            }

            context.AgentCommands.Add(command);
            await context.SaveChangesAsync();

            logger.LogInformation("Command {CommandId} created successfully for agent {AgentId} in tenant {TenantId}", 
                command.Id, command.AgentId, command.ParentId);
            return command;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error creating command for agent {AgentId} in tenant {TenantId}", 
                command.AgentId, command.ParentId);
            throw;
        }
    }

    public async Task UpdateCommandStatusAsync(Guid commandId, CommandStatus status, string? result = null, string? errorMessage = null)
    {
        try
        {
            var command = await context.AgentCommands.FirstOrDefaultAsync(c => c.Id == commandId);
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

                await context.SaveChangesAsync();
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error updating command status for command {CommandId}", commandId);
            throw;
        }
    }

    #endregion

    #region Tenant Repository Implementation

    public async Task<Tenant?> GetTenantByCodeAsync(string tenantCode)
    {
        try
        {
            return await context.Tenants
                .FirstOrDefaultAsync(t => t.TenantCode == tenantCode);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error retrieving tenant by code {TenantCode}", tenantCode);
            throw;
        }
    }

    public async Task<Tenant?> GetTenantByApiKeyAsync(string apiKey)
    {
        try
        {
            return await context.Tenants
                .FirstOrDefaultAsync(t => t.ApiKey == apiKey);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error retrieving tenant by API key");
            throw;
        }
    }

    public async Task<bool> ValidateTenantCodeAsync(string tenantCode)
    {
        try
        {
            return await context.Tenants
                .AnyAsync(t => t.TenantCode == tenantCode && t.Status == TenantStatus.Active);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error validating tenant code {TenantCode}", tenantCode);
            throw;
        }
    }

    public async Task<Tenant> CreateTenantAsync(Tenant tenant)
    {
        try
        {
            context.Tenants.Add(tenant);
            await context.SaveChangesAsync();

            logger.LogInformation("Tenant {TenantId} created successfully with code {TenantCode}", 
                tenant.Id, tenant.TenantCode);
            return tenant;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error creating tenant with code {TenantCode}", tenant.TenantCode);
            throw;
        }
    }

    #endregion

    #region Generic Repository Operations

    public async Task<T?> GetByIdAsync<T>(string id) where T : class, IEntity
    {
        try
        {
            return await context.Set<T>().FindAsync(id);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error retrieving entity {EntityType} with id {Id}", typeof(T).Name, id);
            throw;
        }
    }

    public async Task<IEnumerable<T>> GetAllAsync<T>() where T : class, IEntity
    {
        try
        {
            return await context.Set<T>().ToListAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error retrieving all entities of type {EntityType}", typeof(T).Name);
            throw;
        }
    }

    public async Task<int> GetCountAsync<T>() where T : class, IEntity
    {
        try
        {
            return await context.Set<T>().CountAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error counting entities of type {EntityType}", typeof(T).Name);
            throw;
        }
    }

    #endregion

    public void Dispose()
    {
        context.Dispose();
    }
}
