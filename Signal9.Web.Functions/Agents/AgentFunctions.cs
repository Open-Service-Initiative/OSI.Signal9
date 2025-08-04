using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Signal9.Shared.DTOs;
using Signal9.Shared.DTOs.Base;
using Signal9.Shared.DTOs.Common;
using Signal9.Shared.DTOs.Extensions;
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
    [OpenApiOperation(operationId: "GetAgents", tags: new[] { "Agents" }, Summary = "Get all agents", Description = "Retrieve a paginated list of agents with advanced filtering, sorting, and search capabilities")]
    [OpenApiParameter(name: "parentId", In = ParameterLocation.Query, Required = false, Type = typeof(Guid), Description = "Filter by parent tenant ID")]
    [OpenApiParameter(name: "status", In = ParameterLocation.Query, Required = false, Type = typeof(string), Description = "Filter by agent status (Online, Offline, Error)")]
    [OpenApiParameter(name: "platform", In = ParameterLocation.Query, Required = false, Type = typeof(string), Description = "Filter by platform (Windows, Linux, macOS)")]
    [OpenApiParameter(name: "search", In = ParameterLocation.Query, Required = false, Type = typeof(string), Description = "Search term for machine name, IP address, or description")]
    [OpenApiParameter(name: "isOnline", In = ParameterLocation.Query, Required = false, Type = typeof(bool), Description = "Filter by online status")]
    [OpenApiParameter(name: "page", In = ParameterLocation.Query, Required = false, Type = typeof(int), Description = "Page number (default: 1)")]
    [OpenApiParameter(name: "pageSize", In = ParameterLocation.Query, Required = false, Type = typeof(int), Description = "Page size (default: 20, max: 100)")]
    [OpenApiParameter(name: "sortBy", In = ParameterLocation.Query, Required = false, Type = typeof(string), Description = "Sort field (default: MachineName)")]
    [OpenApiParameter(name: "sortOrder", In = ParameterLocation.Query, Required = false, Type = typeof(string), Description = "Sort order: asc or desc (default: asc)")]
    [OpenApiResponseWithBody(statusCode: HttpStatusCode.OK, contentType: "application/json", bodyType: typeof(Signal9.Shared.DTOs.Base.PagedResponse<AgentResponse>), Description = "Paginated list of agents")]
    [OpenApiResponseWithBody(statusCode: HttpStatusCode.BadRequest, contentType: "application/json", bodyType: typeof(object), Description = "Validation failed")]
    [OpenApiResponseWithBody(statusCode: HttpStatusCode.Unauthorized, contentType: "application/json", bodyType: typeof(object), Description = "Invalid tenant access")]
    [OpenApiResponseWithBody(statusCode: HttpStatusCode.InternalServerError, contentType: "application/json", bodyType: typeof(object), Description = "Internal server error")]
    public async Task<IActionResult> GetAgentsAsync(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "agents")] HttpRequest req,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Getting agents with filters");

        try
        {
            // Extract and validate query parameters with proper typing
            var request = ExtractAgentQueryRequest(req);
            
            // Validate parent tenant access if specified
            if (!ValidateParentAccess(request.ParentId))
            {
                return new UnauthorizedObjectResult(new { error = "Invalid parent tenant access" });
            }

            // Execute query with filtering and pagination
            var agents = await _agentService.GetAgentsAsync(request, cancellationToken);
            
            // Transform to response DTOs
            var agentResponses = agents.Items.Select(agent => 
                AgentResponse.FromAgentDto(agent)).ToList();

            var pagedResponse = new Signal9.Shared.DTOs.Base.PagedResponse<AgentResponse>
            {
                Id = Guid.NewGuid(),
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
            
            // Create comprehensive response
            var response = registeredAgent.CreateAgentResponse();

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
            Id = Guid.NewGuid(),
            ParentId = Guid.TryParse(query["parentId"], out var parentId) ? parentId : Guid.Empty,
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

    private bool ValidateParentAccess(Guid parentId)
    {
        // TODO: Implement actual parent tenant validation
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
