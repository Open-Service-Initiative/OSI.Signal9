using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Azure.Functions.Worker.Extensions.SignalRService;
using Signal9.Shared.Models;
using Signal9.Shared.DTOs;
using System.Net;
using System.Text.Json;

namespace Signal9.Web.Functions;

/// <summary>
/// Functions for tenant and device management operations
/// </summary>
public class TenantManagementFunctions
{
    private readonly ILogger<TenantManagementFunctions> _logger;

    public TenantManagementFunctions(ILogger<TenantManagementFunctions> logger)
    {
        _logger = logger;
    }

    // ======================
    // TENANT CRUD OPERATIONS
    // ======================

    /// <summary>
    /// Get all tenants with optional filtering and pagination
    /// </summary>
    [Function("GetTenants")]
    public async Task<HttpResponseData> GetTenantsAsync(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "tenants")] HttpRequestData req)
    {
        _logger.LogInformation("Getting tenants");

        try
        {
            var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
            var parentTenantId = query["parentTenantId"];
            var tenantType = query["tenantType"];
            var isActive = bool.TryParse(query["isActive"], out var active) ? active : (bool?)null;
            var pageStr = query["page"];
            var pageSizeStr = query["pageSize"];
            
            var page = int.TryParse(pageStr, out var p) ? Math.Max(1, p) : 1;
            var pageSize = int.TryParse(pageSizeStr, out var ps) ? Math.Max(1, Math.Min(100, ps)) : 20;

            // TODO: Replace with actual database query
            // This would use Entity Framework to query the database
            var mockTenants = new List<Tenant>();

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteStringAsync(JsonSerializer.Serialize(new 
            {
                Tenants = mockTenants,
                Page = page,
                PageSize = pageSize,
                TotalCount = 0,
                TotalPages = 0
            }, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            }));

            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting tenants");
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteStringAsync("Failed to retrieve tenants");
            return errorResponse;
        }
    }

    /// <summary>
    /// Get a specific tenant by ID
    /// </summary>
    [Function("GetTenant")]
    public async Task<HttpResponseData> GetTenantAsync(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "tenants/{tenantId}")] HttpRequestData req,
        string tenantId)
    {
        _logger.LogInformation("Getting tenant {TenantId}", tenantId);

        try
        {
            if (!Guid.TryParse(tenantId, out var id))
            {
                var badRequestResponse = req.CreateResponse(HttpStatusCode.BadRequest);
                await badRequestResponse.WriteStringAsync("Invalid tenant ID format");
                return badRequestResponse;
            }

            // TODO: Replace with actual database query
            // var tenant = await dbContext.Tenants.FindAsync(id);
            Tenant? tenant = null;

            if (tenant == null)
            {
                var notFoundResponse = req.CreateResponse(HttpStatusCode.NotFound);
                await notFoundResponse.WriteStringAsync("Tenant not found");
                return notFoundResponse;
            }

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteStringAsync(JsonSerializer.Serialize(tenant, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            }));

            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting tenant {TenantId}", tenantId);
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteStringAsync("Failed to retrieve tenant");
            return errorResponse;
        }
    }

    /// <summary>
    /// Create a new tenant
    /// </summary>
    [Function("CreateTenant")]
    public async Task<HttpResponseData> CreateTenantAsync(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "tenants")] HttpRequestData req)
    {
        _logger.LogInformation("Creating new tenant");

        try
        {
            var requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            var createRequest = JsonSerializer.Deserialize<CreateTenantRequest>(requestBody, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });

            if (createRequest == null || string.IsNullOrWhiteSpace(createRequest.Name))
            {
                var badRequestResponse = req.CreateResponse(HttpStatusCode.BadRequest);
                await badRequestResponse.WriteStringAsync("Invalid tenant data. Name is required.");
                return badRequestResponse;
            }

            // Validate parent tenant exists if specified
            if (createRequest.ParentTenantId.HasValue)
            {
                // TODO: Check if parent tenant exists in database
                // if (!await dbContext.Tenants.AnyAsync(t => t.TenantId == createRequest.ParentTenantId))
                // {
                //     return BadRequest("Parent tenant not found");
                // }
            }

            var tenant = new Tenant
            {
                TenantId = Guid.NewGuid(),
                Name = createRequest.Name,
                Code = createRequest.Code,
                Description = createRequest.Description,
                ParentTenantId = createRequest.ParentTenantId,
                TenantType = createRequest.TenantType ?? "Organization",
                Level = createRequest.ParentTenantId.HasValue ? 1 : 0, // TODO: Calculate actual level
                ContactEmail = createRequest.ContactEmail,
                ContactPhone = createRequest.ContactPhone,
                Plan = createRequest.Plan ?? TenantPlan.Basic,
                MaxAgents = createRequest.MaxAgents ?? 10,
                CreatedAt = DateTime.UtcNow,
                IsActive = true
            };

            // TODO: Save to database
            // dbContext.Tenants.Add(tenant);
            // await dbContext.SaveChangesAsync();

            var response = req.CreateResponse(HttpStatusCode.Created);
            await response.WriteStringAsync(JsonSerializer.Serialize(tenant, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            }));

            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating tenant");
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteStringAsync("Failed to create tenant");
            return errorResponse;
        }
    }

    /// <summary>
    /// Update an existing tenant
    /// </summary>
    [Function("UpdateTenant")]
    public async Task<HttpResponseData> UpdateTenantAsync(
        [HttpTrigger(AuthorizationLevel.Function, "put", Route = "tenants/{tenantId}")] HttpRequestData req,
        string tenantId)
    {
        _logger.LogInformation("Updating tenant {TenantId}", tenantId);

        try
        {
            if (!Guid.TryParse(tenantId, out var id))
            {
                var badRequestResponse = req.CreateResponse(HttpStatusCode.BadRequest);
                await badRequestResponse.WriteStringAsync("Invalid tenant ID format");
                return badRequestResponse;
            }

            var requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            var updateRequest = JsonSerializer.Deserialize<UpdateTenantRequest>(requestBody, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });

            if (updateRequest == null)
            {
                var badRequestResponse = req.CreateResponse(HttpStatusCode.BadRequest);
                await badRequestResponse.WriteStringAsync("Invalid update data");
                return badRequestResponse;
            }

            // TODO: Get tenant from database
            // var tenant = await dbContext.Tenants.FindAsync(id);
            Tenant? tenant = null;

            if (tenant == null)
            {
                var notFoundResponse = req.CreateResponse(HttpStatusCode.NotFound);
                await notFoundResponse.WriteStringAsync("Tenant not found");
                return notFoundResponse;
            }

            // Update properties
            if (!string.IsNullOrWhiteSpace(updateRequest.Name))
                tenant.Name = updateRequest.Name;
            if (updateRequest.Code != null)
                tenant.Code = updateRequest.Code;
            if (updateRequest.Description != null)
                tenant.Description = updateRequest.Description;
            if (updateRequest.ContactEmail != null)
                tenant.ContactEmail = updateRequest.ContactEmail;
            if (updateRequest.ContactPhone != null)
                tenant.ContactPhone = updateRequest.ContactPhone;
            if (updateRequest.Plan.HasValue)
                tenant.Plan = updateRequest.Plan.Value;
            if (updateRequest.MaxAgents.HasValue)
                tenant.MaxAgents = updateRequest.MaxAgents.Value;
            if (updateRequest.IsActive.HasValue)
                tenant.IsActive = updateRequest.IsActive.Value;

            tenant.UpdatedAt = DateTime.UtcNow;

            // TODO: Save changes to database
            // await dbContext.SaveChangesAsync();

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteStringAsync(JsonSerializer.Serialize(tenant, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            }));

            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating tenant {TenantId}", tenantId);
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteStringAsync("Failed to update tenant");
            return errorResponse;
        }
    }

    /// <summary>
    /// Delete a tenant (soft delete by deactivating)
    /// </summary>
    [Function("DeleteTenant")]
    public async Task<HttpResponseData> DeleteTenantAsync(
        [HttpTrigger(AuthorizationLevel.Function, "delete", Route = "tenants/{tenantId}")] HttpRequestData req,
        string tenantId)
    {
        _logger.LogInformation("Deleting tenant {TenantId}", tenantId);

        try
        {
            if (!Guid.TryParse(tenantId, out var id))
            {
                var badRequestResponse = req.CreateResponse(HttpStatusCode.BadRequest);
                await badRequestResponse.WriteStringAsync("Invalid tenant ID format");
                return badRequestResponse;
            }

            // TODO: Get tenant from database and check for dependencies
            // var tenant = await dbContext.Tenants.FindAsync(id);
            // var hasAgents = await dbContext.Agents.AnyAsync(a => a.TenantId == id);
            // var hasChildTenants = await dbContext.Tenants.AnyAsync(t => t.ParentTenantId == id);

            var tenant = (Tenant?)null;
            var hasAgents = false;
            var hasChildTenants = false;

            if (tenant == null)
            {
                var notFoundResponse = req.CreateResponse(HttpStatusCode.NotFound);
                await notFoundResponse.WriteStringAsync("Tenant not found");
                return notFoundResponse;
            }

            if (hasAgents || hasChildTenants)
            {
                var conflictResponse = req.CreateResponse(HttpStatusCode.Conflict);
                await conflictResponse.WriteStringAsync("Cannot delete tenant with active agents or child tenants");
                return conflictResponse;
            }

            // Soft delete by deactivating
            tenant.IsActive = false;
            tenant.UpdatedAt = DateTime.UtcNow;

            // TODO: Save changes to database
            // await dbContext.SaveChangesAsync();

            var response = req.CreateResponse(HttpStatusCode.NoContent);
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting tenant {TenantId}", tenantId);
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteStringAsync("Failed to delete tenant");
            return errorResponse;
        }
    }

    // ======================
    // DEVICE/AGENT CRUD OPERATIONS
    // ======================

    /// <summary>
    /// Get all agents with optional filtering and pagination
    /// </summary>
    [Function("GetAgents")]
    public async Task<HttpResponseData> GetAgentsAsync(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "agents")] HttpRequestData req)
    {
        _logger.LogInformation("Getting agents");

        try
        {
            var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
            var tenantId = query["tenantId"];
            var status = query["status"];
            var groupName = query["groupName"];
            var pageStr = query["page"];
            var pageSizeStr = query["pageSize"];
            
            var page = int.TryParse(pageStr, out var p) ? Math.Max(1, p) : 1;
            var pageSize = int.TryParse(pageSizeStr, out var ps) ? Math.Max(1, Math.Min(100, ps)) : 20;

            // TODO: Replace with actual database query
            // This would use Entity Framework to query the database with filters
            var mockAgents = new List<Agent>();

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteStringAsync(JsonSerializer.Serialize(new 
            {
                Agents = mockAgents,
                Page = page,
                PageSize = pageSize,
                TotalCount = 0,
                TotalPages = 0
            }, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            }));

            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting agents");
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteStringAsync("Failed to retrieve agents");
            return errorResponse;
        }
    }

    /// <summary>
    /// Get agents for a specific tenant
    /// </summary>
    [Function("GetTenantAgents")]
    public async Task<HttpResponseData> GetTenantAgentsAsync(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "tenants/{tenantId}/agents")] HttpRequestData req,
        string tenantId)
    {
        _logger.LogInformation("Getting agents for tenant {TenantId}", tenantId);

        try
        {
            if (!Guid.TryParse(tenantId, out var id))
            {
                var badRequestResponse = req.CreateResponse(HttpStatusCode.BadRequest);
                await badRequestResponse.WriteStringAsync("Invalid tenant ID format");
                return badRequestResponse;
            }

            var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
            var status = query["status"];
            var pageStr = query["page"];
            var pageSizeStr = query["pageSize"];
            
            var page = int.TryParse(pageStr, out var p) ? Math.Max(1, p) : 1;
            var pageSize = int.TryParse(pageSizeStr, out var ps) ? Math.Max(1, Math.Min(100, ps)) : 20;

            // TODO: Replace with actual database query
            // var agents = await dbContext.Agents.Where(a => a.TenantId == id).ToListAsync();
            var mockAgents = new List<Agent>();

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteStringAsync(JsonSerializer.Serialize(new 
            {
                Agents = mockAgents,
                Page = page,
                PageSize = pageSize,
                TotalCount = 0,
                TotalPages = 0
            }, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            }));

            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting agents for tenant {TenantId}", tenantId);
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteStringAsync("Failed to retrieve tenant agents");
            return errorResponse;
        }
    }

    /// <summary>
    /// Get a specific agent by ID
    /// </summary>
    [Function("GetAgent")]
    public async Task<HttpResponseData> GetAgentAsync(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "agents/{agentId}")] HttpRequestData req,
        string agentId)
    {
        _logger.LogInformation("Getting agent {AgentId}", agentId);

        try
        {
            if (!Guid.TryParse(agentId, out var id))
            {
                var badRequestResponse = req.CreateResponse(HttpStatusCode.BadRequest);
                await badRequestResponse.WriteStringAsync("Invalid agent ID format");
                return badRequestResponse;
            }

            // TODO: Replace with actual database query
            // var agent = await dbContext.Agents.FindAsync(id);
            Agent? agent = null;

            if (agent == null)
            {
                var notFoundResponse = req.CreateResponse(HttpStatusCode.NotFound);
                await notFoundResponse.WriteStringAsync("Agent not found");
                return notFoundResponse;
            }

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteStringAsync(JsonSerializer.Serialize(agent, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            }));

            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting agent {AgentId}", agentId);
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteStringAsync("Failed to retrieve agent");
            return errorResponse;
        }
    }

    /// <summary>
    /// Create/Register a new agent
    /// </summary>
    [Function("CreateAgent")]
    public async Task<HttpResponseData> CreateAgentAsync(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "agents")] HttpRequestData req)
    {
        _logger.LogInformation("Creating new agent");

        try
        {
            var requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            var createRequest = JsonSerializer.Deserialize<CreateAgentRequest>(requestBody, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });

            if (createRequest == null || string.IsNullOrWhiteSpace(createRequest.MachineName))
            {
                var badRequestResponse = req.CreateResponse(HttpStatusCode.BadRequest);
                await badRequestResponse.WriteStringAsync("Invalid agent data. MachineName is required.");
                return badRequestResponse;
            }

            // Validate tenant exists if specified
            if (createRequest.TenantId.HasValue)
            {
                // TODO: Check if tenant exists in database
                // if (!await dbContext.Tenants.AnyAsync(t => t.TenantId == createRequest.TenantId && t.IsActive))
                // {
                //     return BadRequest("Tenant not found or inactive");
                // }
            }

            var agent = new Agent
            {
                AgentId = Guid.NewGuid(),
                MachineName = createRequest.MachineName,
                Domain = createRequest.Domain,
                OperatingSystem = createRequest.OperatingSystem ?? "Unknown",
                OSVersion = createRequest.OSVersion,
                Architecture = createRequest.Architecture,
                TotalMemoryMB = createRequest.TotalMemoryMB ?? 0,
                ProcessorCores = createRequest.ProcessorCores ?? 0,
                ProcessorName = createRequest.ProcessorName,
                IpAddress = createRequest.IpAddress ?? "0.0.0.0",
                MacAddress = createRequest.MacAddress,
                TenantId = createRequest.TenantId,
                GroupName = createRequest.GroupName,
                Version = createRequest.Version,
                FirstSeen = DateTime.UtcNow,
                LastSeen = DateTime.UtcNow,
                Status = AgentStatus.Online,
                Tags = createRequest.Tags ?? new Dictionary<string, object>(),
                CustomProperties = createRequest.CustomProperties ?? new Dictionary<string, object>()
            };

            // TODO: Save to database
            // dbContext.Agents.Add(agent);
            // await dbContext.SaveChangesAsync();

            var response = req.CreateResponse(HttpStatusCode.Created);
            await response.WriteStringAsync(JsonSerializer.Serialize(agent, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            }));

            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating agent");
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteStringAsync("Failed to create agent");
            return errorResponse;
        }
    }

    /// <summary>
    /// Update an existing agent
    /// </summary>
    [Function("UpdateAgent")]
    public async Task<HttpResponseData> UpdateAgentAsync(
        [HttpTrigger(AuthorizationLevel.Function, "put", Route = "agents/{agentId}")] HttpRequestData req,
        string agentId)
    {
        _logger.LogInformation("Updating agent {AgentId}", agentId);

        try
        {
            if (!Guid.TryParse(agentId, out var id))
            {
                var badRequestResponse = req.CreateResponse(HttpStatusCode.BadRequest);
                await badRequestResponse.WriteStringAsync("Invalid agent ID format");
                return badRequestResponse;
            }

            var requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            var updateRequest = JsonSerializer.Deserialize<UpdateAgentRequest>(requestBody, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });

            if (updateRequest == null)
            {
                var badRequestResponse = req.CreateResponse(HttpStatusCode.BadRequest);
                await badRequestResponse.WriteStringAsync("Invalid update data");
                return badRequestResponse;
            }

            // TODO: Get agent from database
            // var agent = await dbContext.Agents.FindAsync(id);
            Agent? agent = null;

            if (agent == null)
            {
                var notFoundResponse = req.CreateResponse(HttpStatusCode.NotFound);
                await notFoundResponse.WriteStringAsync("Agent not found");
                return notFoundResponse;
            }

            // Update properties
            if (!string.IsNullOrWhiteSpace(updateRequest.MachineName))
                agent.MachineName = updateRequest.MachineName;
            if (updateRequest.Domain != null)
                agent.Domain = updateRequest.Domain;
            if (!string.IsNullOrWhiteSpace(updateRequest.OperatingSystem))
                agent.OperatingSystem = updateRequest.OperatingSystem;
            if (updateRequest.OSVersion != null)
                agent.OSVersion = updateRequest.OSVersion;
            if (updateRequest.Architecture != null)
                agent.Architecture = updateRequest.Architecture;
            if (updateRequest.TotalMemoryMB.HasValue)
                agent.TotalMemoryMB = updateRequest.TotalMemoryMB.Value;
            if (updateRequest.ProcessorCores.HasValue)
                agent.ProcessorCores = updateRequest.ProcessorCores.Value;
            if (updateRequest.ProcessorName != null)
                agent.ProcessorName = updateRequest.ProcessorName;
            if (!string.IsNullOrWhiteSpace(updateRequest.IpAddress))
                agent.IpAddress = updateRequest.IpAddress;
            if (updateRequest.MacAddress != null)
                agent.MacAddress = updateRequest.MacAddress;
            if (updateRequest.TenantId.HasValue)
                agent.TenantId = updateRequest.TenantId.Value;
            if (updateRequest.GroupName != null)
                agent.GroupName = updateRequest.GroupName;
            if (updateRequest.Version != null)
                agent.Version = updateRequest.Version;
            if (updateRequest.Status.HasValue)
                agent.Status = updateRequest.Status.Value;
            if (updateRequest.Tags != null)
                agent.Tags = updateRequest.Tags;
            if (updateRequest.CustomProperties != null)
                agent.CustomProperties = updateRequest.CustomProperties;

            agent.LastSeen = DateTime.UtcNow;

            // TODO: Save changes to database
            // await dbContext.SaveChangesAsync();

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteStringAsync(JsonSerializer.Serialize(agent, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            }));

            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating agent {AgentId}", agentId);
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteStringAsync("Failed to update agent");
            return errorResponse;
        }
    }

    /// <summary>
    /// Delete an agent
    /// </summary>
    [Function("DeleteAgent")]
    public async Task<HttpResponseData> DeleteAgentAsync(
        [HttpTrigger(AuthorizationLevel.Function, "delete", Route = "agents/{agentId}")] HttpRequestData req,
        string agentId)
    {
        _logger.LogInformation("Deleting agent {AgentId}", agentId);

        try
        {
            if (!Guid.TryParse(agentId, out var id))
            {
                var badRequestResponse = req.CreateResponse(HttpStatusCode.BadRequest);
                await badRequestResponse.WriteStringAsync("Invalid agent ID format");
                return badRequestResponse;
            }

            // TODO: Get agent from database
            // var agent = await dbContext.Agents.FindAsync(id);
            Agent? agent = null;

            if (agent == null)
            {
                var notFoundResponse = req.CreateResponse(HttpStatusCode.NotFound);
                await notFoundResponse.WriteStringAsync("Agent not found");
                return notFoundResponse;
            }

            // TODO: Remove from database
            // dbContext.Agents.Remove(agent);
            // await dbContext.SaveChangesAsync();

            var response = req.CreateResponse(HttpStatusCode.NoContent);
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting agent {AgentId}", agentId);
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteStringAsync("Failed to delete agent");
            return errorResponse;
        }
    }

    /// <summary>
    /// Update agent status (heartbeat)
    /// </summary>
    [Function("UpdateAgentStatus")]
    public async Task<HttpResponseData> UpdateAgentStatusAsync(
        [HttpTrigger(AuthorizationLevel.Function, "patch", Route = "agents/{agentId}/status")] HttpRequestData req,
        string agentId)
    {
        _logger.LogInformation("Updating status for agent {AgentId}", agentId);

        try
        {
            if (!Guid.TryParse(agentId, out var id))
            {
                var badRequestResponse = req.CreateResponse(HttpStatusCode.BadRequest);
                await badRequestResponse.WriteStringAsync("Invalid agent ID format");
                return badRequestResponse;
            }

            var requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            var statusRequest = JsonSerializer.Deserialize<UpdateAgentStatusRequest>(requestBody, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });

            if (statusRequest == null)
            {
                var badRequestResponse = req.CreateResponse(HttpStatusCode.BadRequest);
                await badRequestResponse.WriteStringAsync("Invalid status data");
                return badRequestResponse;
            }

            // TODO: Get agent from database
            // var agent = await dbContext.Agents.FindAsync(id);
            Agent? agent = null;

            if (agent == null)
            {
                var notFoundResponse = req.CreateResponse(HttpStatusCode.NotFound);
                await notFoundResponse.WriteStringAsync("Agent not found");
                return notFoundResponse;
            }

            agent.Status = statusRequest.Status;
            agent.LastSeen = DateTime.UtcNow;

            // TODO: Save changes to database
            // await dbContext.SaveChangesAsync();

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteStringAsync(JsonSerializer.Serialize(new { Status = agent.Status, LastSeen = agent.LastSeen }, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            }));

            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating agent status {AgentId}", agentId);
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteStringAsync("Failed to update agent status");
            return errorResponse;
        }
    }

    // ======================
    // SIGNALR NEGOTIATE
    // ======================

    /// <summary>
    /// SignalR negotiate function for web dashboard connections
    /// </summary>
    [Function("negotiate")]
    public SignalRConnectionInfo Negotiate(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post")] HttpRequestData req,
        [SignalRConnectionInfoInput(HubName = "DashboardHub")] SignalRConnectionInfo connectionInfo)
    {
        _logger.LogInformation("Dashboard client requesting SignalR connection negotiation");
        return connectionInfo;
    }
}
