using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Signal9.Shared.DTOs;
using Signal9.Shared.DTOs.Common;
using Signal9.Shared.Models;
using Signal9.Shared.Services;
using System.ComponentModel.DataAnnotations;
using System.Net;

namespace Signal9.Web.Functions.Agents;

/// <summary>
/// Modern Azure Functions for comprehensive agent management operations.
/// Uses ASP.NET Core integration with production-ready patterns.
/// </summary>
public class AgentFunctions
{
    private readonly ILogger<AgentFunctions> _logger;
    private readonly IAgentService _agentService;

    public AgentFunctions(
        ILogger<AgentFunctions> logger,
        IAgentService agentService)
    {
        _logger = logger;
        _agentService = agentService;
    }

    #region Query Operations

    /// <summary>
    /// Get agents with advanced filtering, sorting, and pagination
    /// </summary>
    [Function("GetAgents")]
    public async Task<IActionResult> GetAgentsAsync(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "agents")] HttpRequest req,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Getting agents with filters");

        try
        {
            // Extract and validate query parameters
            var request = ExtractAgentQueryRequest(req);

            // Validate parent access
            if (!ValidateParentAccess(request.ParentId ?? Guid.Empty))
            {
                return new UnauthorizedObjectResult(new { error = "Invalid parent access" });
            }

            // Execute query with filtering and pagination
            var agents = await _agentService.GetAgentsAsync(request, cancellationToken);

            // Transform to response DTOs
            var agentResponses = agents.Items.Select(agent =>
                AgentResponse.FromAgentDto(agent)).ToList();

            var pagedResponse = new Shared.DTOs.Base.PagedResponse<AgentResponse>
            {
                Items = agentResponses,
                Page = agents.Page,
                PageSize = agents.PageSize,
                TotalCount = agents.TotalCount
            };

            return new OkObjectResult(pagedResponse);
        }
        catch (ValidationException ex)
        {
            _logger.LogWarning("Validation failed for GetAgents: {Error}", ex.Message);
            return new BadRequestObjectResult(new { error = "Validation failed", details = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting agents");
            return new ObjectResult(new { error = "Failed to retrieve agents" })
            {
                StatusCode = StatusCodes.Status500InternalServerError
            };
        }
    }

    /// <summary>
    /// Get a specific agent by ID with detailed information
    /// </summary>
    [Function("GetAgent")]
    public async Task<IActionResult> GetAgentAsync(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "agents/{agentId:guid}")] HttpRequest req,
        Guid agentId,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Getting agent {AgentId}", agentId);

        try
        {
            // Extract include parameters
            var includeMetrics = req.Query.ContainsKey("includeMetrics") &&
                               bool.Parse(req.Query["includeMetrics"].FirstOrDefault() ?? "false");
            var includeTelemetry = req.Query.ContainsKey("includeTelemetry") &&
                                 bool.Parse(req.Query["includeTelemetry"].FirstOrDefault() ?? "false");

            // Get agent with optional includes
            var agent = await _agentService.GetAgentByIdAsync(agentId, includeMetrics, includeTelemetry, cancellationToken);

            if (agent == null)
            {
                return new NotFoundObjectResult(new { error = "Agent not found" });
            }

            // Transform to response DTO
            var response = agent.CreateAgentResponse();

            return new OkObjectResult(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting agent {AgentId}", agentId);
            return new ObjectResult(new { error = "Failed to retrieve agent" })
            {
                StatusCode = StatusCodes.Status500InternalServerError
            };
        }
    }

    #endregion

    #region Registration Operations

    /// <summary>
    /// Register a new agent in the Signal9 RMM system
    /// </summary>
    [Function("RegisterAgent")]
    public async Task<IActionResult> RegisterAgentAsync(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "agents/register")] HttpRequest req,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Processing agent registration");

        try
        {
            // Deserialize and validate registration request
            var registrationRequest = await req.ReadFromJsonAsync<AgentRegistrationRequest>(cancellationToken);

            if (registrationRequest == null)
            {
                return new BadRequestObjectResult(new { error = "Invalid registration data" });
            }

            // Validate request using built-in validation
            if (!TryValidateModel(registrationRequest, out var validationResults))
            {
                return new BadRequestObjectResult(new
                {
                    error = "Validation failed",
                    errors = validationResults.Select(r => r.ErrorMessage)
                });
            }

            // Convert to agent DTO using modern mapping
            var agentDto = registrationRequest.ToAgentDto();

            // Register agent through service layer
            var registeredAgent = await _agentService.RegisterAgentAsync(agentDto, cancellationToken);

            // Generate agent configuration
            var configuration = await _agentService.GenerateAgentConfigurationAsync(registeredAgent.Id, cancellationToken);

            // Create comprehensive response
            var response = new AgentRegistrationResponse
            {
                ParentId = registeredAgent.ParentId,  // Fixed: was TenantId
                Agent = registeredAgent.CreateAgentResponse(),
                Configuration = configuration,
                RegistrationStatus = "Success",
                Message = "Agent registered successfully"
            };

            return new CreatedResult($"/api/agents/{registeredAgent.Id}", response);
        }
        catch (ValidationException ex)
        {
            _logger.LogWarning("Agent registration validation failed: {Error}", ex.Message);
            return new BadRequestObjectResult(new { error = "Registration validation failed", details = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning("Agent registration conflict: {Error}", ex.Message);
            return new ConflictObjectResult(new { error = "Registration conflict", details = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during agent registration");
            return new ObjectResult(new { error = "Registration failed" })
            {
                StatusCode = StatusCodes.Status500InternalServerError
            };
        }
    }

    #endregion

    #region Management Operations

    /// <summary>
    /// Update agent information
    /// </summary>
    [Function("UpdateAgent")]
    public async Task<IActionResult> UpdateAgentAsync(
        [HttpTrigger(AuthorizationLevel.Function, "put", Route = "agents/{agentId:guid}")] HttpRequest req,
        Guid agentId,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Updating agent {AgentId}", agentId);

        try
        {
            // Deserialize update request
            var updateRequest = await req.ReadFromJsonAsync<AgentUpdateRequest>(cancellationToken);

            if (updateRequest == null)
            {
                return new BadRequestObjectResult(new { error = "Invalid update data" });
            }

            // Validate request
            if (!TryValidateModel(updateRequest, out var validationResults))
            {
                return new BadRequestObjectResult(new
                {
                    error = "Validation failed",
                    errors = validationResults.Select(r => r.ErrorMessage)
                });
            }

            // Update through service layer
            var updatedAgent = await _agentService.UpdateAgentAsync(agentId, updateRequest, cancellationToken);

            if (updatedAgent == null)
            {
                return new NotFoundObjectResult(new { error = "Agent not found" });
            }

            // Transform to response
            var response = updatedAgent.CreateAgentResponse();

            return new OkObjectResult(response);
        }
        catch (ValidationException ex)
        {
            _logger.LogWarning("Agent update validation failed: {Error}", ex.Message);
            return new BadRequestObjectResult(new { error = "Update validation failed", details = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating agent {AgentId}", agentId);
            return new ObjectResult(new { error = "Update failed" })
            {
                StatusCode = StatusCodes.Status500InternalServerError
            };
        }
    }

    /// <summary>
    /// Delete/unregister an agent
    /// </summary>
    [Function("DeleteAgent")]
    public async Task<IActionResult> DeleteAgentAsync(
        [HttpTrigger(AuthorizationLevel.Function, "delete", Route = "agents/{agentId:guid}")] HttpRequest req,
        Guid agentId,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Deleting agent {AgentId}", agentId);

        try
        {
            // Check if agent exists
            var agent = await _agentService.GetAgentByIdAsync(agentId, false, false, cancellationToken);

            if (agent == null)
            {
                return new NotFoundObjectResult(new { error = "Agent not found" });
            }

            // Extract preservation flag
            var preserveData = req.Query.ContainsKey("preserveData") &&
                             bool.Parse(req.Query["preserveData"].FirstOrDefault() ?? "false");

            // Delete through service layer
            await _agentService.DeleteAgentAsync(agentId, preserveData, cancellationToken);

            return new NoContentResult();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting agent {AgentId}", agentId);
            return new ObjectResult(new { error = "Deletion failed" })
            {
                StatusCode = StatusCodes.Status500InternalServerError
            };
        }
    }

    #endregion

    #region Helper Methods

    private AgentQueryRequest ExtractAgentQueryRequest(HttpRequest req)
    {
        var query = req.Query;

        return new AgentQueryRequest
        {
            ParentId = Guid.TryParse(query["parentId"], out var parentId) ? parentId : Guid.Empty,  // Fixed: was tenantId
            Status = Enum.TryParse<AgentStatus>(query["status"], out var status) ? status : null,
            Platform = query["platform"].FirstOrDefault(),
            Search = query["search"].FirstOrDefault(),
            IsOnline = bool.TryParse(query["isOnline"], out var online) ? online : null,
            Page = int.TryParse(query["page"], out var page) ? Math.Max(1, page) : 1,
            PageSize = int.TryParse(query["pageSize"], out var pageSize) ? Math.Max(1, Math.Min(100, pageSize)) : 20,
            SortBy = query["sortBy"].FirstOrDefault() ?? "MachineName",
            SortOrder = query["sortOrder"].FirstOrDefault() ?? "asc"
        };
    }

    private bool ValidateParentAccess(Guid parentId)  // Renamed from ValidateTenantAccess for unified hierarchy
    {
        // TODO: Implement actual parent validation
        return parentId != Guid.Empty;
    }

    private static bool TryValidateModel<T>(T model, out List<ValidationResult> validationResults)
    {
        validationResults = new List<ValidationResult>();
        var context = new ValidationContext(model!);
        return Validator.TryValidateObject(model!, context, validationResults, true);
    }

    #endregion
}
