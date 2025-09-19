using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Signal9.Shared.Models;
using Signal9.Shared.DTOs.Tenants;
using Signal9.Shared.DTOs.Base;
using System.Net;
using System.Text.Json;
using SystemWeb = System.Web;
using SystemNet = System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.OpenApi.Models;
using System.ComponentModel.DataAnnotations;
// Correct OpenAPI namespaces for isolated worker model version 1.5.1
using Microsoft.Azure.WebJobs.Extensions.OpenApi.Core.Attributes;
using Microsoft.Azure.WebJobs.Extensions.OpenApi.Core.Enums;

namespace Signal9.Web.Functions.Tenants;

/// <summary>
/// Azure Functions for comprehensive tenant management operations
/// </summary>
public class TenantFunctions(ILogger<TenantFunctions> logger)
{
    // Temporary in-memory storage until database is implemented
    private static readonly List<TenantResponse> Tenants = [];
    private static readonly Lock LockObject = new();

    // No mock data initialization - start with empty collection

    #region Basic CRUD Operations

    /// <summary>
    /// Get all tenants with comprehensive filtering, searching, sorting, and pagination
    /// </summary>
    /// <param name="req">HTTP request containing query parameters for filtering, sorting, and pagination</param>
    /// <returns>Paginated list of tenants matching the specified criteria</returns>
    /// <response code="200">Returns paginated list of tenants</response>
    /// <response code="400">Bad request - invalid parameters</response>
    /// <response code="500">Internal server error</response>
    [Function("GetTenants")]
    [OpenApiOperation(operationId: "GetTenants", tags: ["Tenants"], Summary = "Get all tenants", Description = "Retrieve a paginated list of all tenants with comprehensive filtering, searching, and sorting capabilities")]
    [OpenApiParameter(name: "page", In = ParameterLocation.Query, Required = false, Type = typeof(int), Description = "Page number (default: 1)")]
    [OpenApiParameter(name: "pageSize", In = ParameterLocation.Query, Required = false, Type = typeof(int), Description = "Page size (default: 20, max: 100)")]
    [OpenApiParameter(name: "includeInactive", In = ParameterLocation.Query, Required = false, Type = typeof(bool), Description = "Include inactive tenants")]
    [OpenApiParameter(name: "sortBy", In = ParameterLocation.Query, Required = false, Type = typeof(string), Description = "Sort field (name, slug, created, updated, agents)")]
    [OpenApiParameter(name: "sortOrder", In = ParameterLocation.Query, Required = false, Type = typeof(string), Description = "Sort order: asc or desc (default: asc)")]
    [OpenApiParameter(name: "slug", In = ParameterLocation.Query, Required = false, Type = typeof(string), Description = "Filter by exact tenant slug")]
    [OpenApiParameter(name: "name", In = ParameterLocation.Query, Required = false, Type = typeof(string), Description = "Filter by tenant name (partial match)")]
    [OpenApiParameter(name: "email", In = ParameterLocation.Query, Required = false, Type = typeof(string), Description = "Filter by contact email (exact match)")]
    [OpenApiParameter(name: "tenantType", In = ParameterLocation.Query, Required = false, Type = typeof(string), Description = "Filter by tenant type")]
    [OpenApiParameter(name: "plan", In = ParameterLocation.Query, Required = false, Type = typeof(string), Description = "Filter by subscription plan")]
    [OpenApiParameter(name: "isActive", In = ParameterLocation.Query, Required = false, Type = typeof(bool), Description = "Filter by active status")]
    [OpenApiParameter(name: "parentTenantId", In = ParameterLocation.Query, Required = false, Type = typeof(string), Description = "Filter by parent tenant ID")]
    [OpenApiParameter(name: "search", In = ParameterLocation.Query, Required = false, Type = typeof(string), Description = "General search query across name, slug, and description")]
    [OpenApiResponseWithBody(statusCode: HttpStatusCode.OK, contentType: "application/json", bodyType: typeof(PagedResponse<TenantResponse>), Description = "Paginated list of tenants")]
    [OpenApiResponseWithBody(statusCode: HttpStatusCode.BadRequest, contentType: "application/json", bodyType: typeof(object), Description = "Bad request")]
    [OpenApiResponseWithBody(statusCode: HttpStatusCode.InternalServerError, contentType: "application/json", bodyType: typeof(object), Description = "Internal server error")]
    public Task<IActionResult> GetTenantsAsync(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "tenants")] HttpRequest req)
    {
        logger.LogInformation("Getting tenants with filters");

        try
        {
            if (req == null)
            {
                logger.LogError("HttpRequest is null");
                return Task.FromResult<IActionResult>(new BadRequestObjectResult(new { error = "Request is null" }));
            }

            var query = req.Query;
            
            // Basic pagination parameters with safe null handling
            var pageStr = query.TryGetValue("page", out var pageValues) ? pageValues.FirstOrDefault() : null;
            var page = int.TryParse(pageStr, out var p) ? Math.Max(1, p) : 1;
            
            var pageSizeStr = query.TryGetValue("pageSize", out var pageSizeValues) ? pageSizeValues.FirstOrDefault() : null;
            var pageSize = int.TryParse(pageSizeStr, out var ps) ? Math.Max(1, Math.Min(100, ps)) : 20;
            
            var includeInactiveStr = query.TryGetValue("includeInactive", out var includeInactiveValues) ? includeInactiveValues.FirstOrDefault() : null;
            var includeInactive = bool.TryParse(includeInactiveStr, out var includeI) && includeI;

            // Sorting parameters
            var sortBy = query.TryGetValue("sortBy", out var sortByValues) ? sortByValues.FirstOrDefault() : null;
            sortBy = string.IsNullOrEmpty(sortBy) ? "name" : sortBy;
            
            var sortOrder = query.TryGetValue("sortOrder", out var sortOrderValues) ? sortOrderValues.FirstOrDefault() : null;
            sortOrder = string.IsNullOrEmpty(sortOrder) ? "asc" : sortOrder;

            // Filtering parameters
            var slug = query.TryGetValue("slug", out var slugValues) ? slugValues.FirstOrDefault() : null;
            var name = query.TryGetValue("name", out var nameValues) ? nameValues.FirstOrDefault() : null;
            var email = query.TryGetValue("email", out var emailValues) ? emailValues.FirstOrDefault() : null;
            var tenantType = query.TryGetValue("tenantType", out var tenantTypeValues) ? tenantTypeValues.FirstOrDefault() : null;
            var plan = query.TryGetValue("plan", out var planValues) ? planValues.FirstOrDefault() : null;
            var isActiveStr = query.TryGetValue("isActive", out var isActiveValues) ? isActiveValues.FirstOrDefault() : null;
            var isActive = bool.TryParse(isActiveStr, out var active) ? active : (bool?)null;
            var parentTenantId = query.TryGetValue("parentTenantId", out var parentTenantIdValues) ? parentTenantIdValues.FirstOrDefault() : null;
            
            // General search parameter
            var search = query.TryGetValue("search", out var searchValues) ? searchValues.FirstOrDefault() : null;

            logger.LogInformation("Query parameters parsed successfully");

            // Get all tenants
            List<TenantResponse> allTenants;
            lock (LockObject)
            {
                allTenants = [.. Tenants];
            }

            logger.LogInformation("Retrieved {Count} tenants from storage", allTenants.Count);

            // Apply comprehensive filtering
            var filteredTenants = ApplyTenantSearchFilters(allTenants, slug, name, email, tenantType, plan, isActive, parentTenantId, search, includeInactive);
            logger.LogInformation("Calling ApplyTenantSorting with sortBy={SortBy}, sortOrder={SortOrder}", sortBy, sortOrder);
            var sortedTenants = ApplyTenantSorting(filteredTenants, sortBy, sortOrder).ToList();
            logger.LogInformation("Sorting completed, {Count} tenants after sorting", sortedTenants.Count);
            
            var totalCount = sortedTenants.Count;
            
            var pagedTenants = sortedTenants
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToList();

            logger.LogInformation("Creating PagedResponse with {Count} items", pagedTenants.Count);

            var result = new PagedResponse<TenantResponse>
            {
                Items = pagedTenants,
                Page = page,
                PageSize = pageSize,
                TotalCount = totalCount
            };

            logger.LogInformation("PagedResponse created successfully");
            return Task.FromResult<IActionResult>(new OkObjectResult(result));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error getting tenants");
            return Task.FromResult<IActionResult>(new ObjectResult(new { error = "Failed to retrieve tenants", details = ex.Message })
            {
                StatusCode = StatusCodes.Status500InternalServerError
            });
        }
    }

    /// <summary>
    /// Get a specific tenant by ID with optional related data
    /// </summary>
    /// <param name="req">HTTP request</param>
    /// <param name="tenantId">The unique identifier of the tenant</param>
    /// <returns>Tenant details if found</returns>
    /// <response code="200">Returns tenant details</response>
    /// <response code="400">Invalid tenant ID format</response>
    /// <response code="404">Tenant not found</response>
    /// <response code="500">Internal server error</response>
    [Function("GetTenant")]
    [OpenApiOperation(operationId: "GetTenant", tags: ["Tenants"], Summary = "Get tenant by ID", Description = "Retrieve a specific tenant by its unique identifier")]
    [OpenApiParameter(name: "tenantId", In = ParameterLocation.Path, Required = true, Type = typeof(string), Description = "The unique identifier of the tenant")]
    [OpenApiResponseWithBody(statusCode: HttpStatusCode.OK, contentType: "application/json", bodyType: typeof(TenantResponse), Description = "Tenant details")]
    [OpenApiResponseWithBody(statusCode: HttpStatusCode.BadRequest, contentType: "application/json", bodyType: typeof(object), Description = "Invalid tenant ID format")]
    [OpenApiResponseWithBody(statusCode: HttpStatusCode.NotFound, contentType: "application/json", bodyType: typeof(object), Description = "Tenant not found")]
    [OpenApiResponseWithBody(statusCode: HttpStatusCode.InternalServerError, contentType: "application/json", bodyType: typeof(object), Description = "Internal server error")]
    public Task<IActionResult> GetTenantAsync(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "tenants/{tenantId}")] HttpRequest req,
        string tenantId)
    {
        logger.LogInformation("Getting tenant {TenantId}", tenantId);

        try
        {
            if (!Guid.TryParse(tenantId, out var id))
            {
                return Task.FromResult<IActionResult>(new BadRequestObjectResult(new { error = "Invalid tenant ID format" }));
            }

            // TODO: Replace with actual database query
            TenantResponse? tenant;
            lock (LockObject)
            {
                tenant = Tenants.FirstOrDefault(t => t.Id == id);
            }

            if (tenant == null)
            {
                return Task.FromResult<IActionResult>(new NotFoundObjectResult(new { error = "Tenant not found" }));
            }

            // TODO: Include related data based on query parameters
            // if (includeChildren)
            // {
            //     // tenant.ChildTenants = await GetChildTenants(id);
            // }

            return Task.FromResult<IActionResult>(new OkObjectResult(tenant));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error getting tenant {TenantId}", tenantId);
            return Task.FromResult<IActionResult>(new ObjectResult(new { error = "Failed to retrieve tenant", details = ex.Message })
            {
                StatusCode = StatusCodes.Status500InternalServerError
            });
        }
    }

    /// <summary>
    /// Create a new tenant with comprehensive validation
    /// </summary>
    /// <param name="req">HTTP request containing tenant creation data</param>
    /// <returns>Created tenant details</returns>
    /// <response code="201">Tenant created successfully</response>
    /// <response code="400">Validation failed</response>
    /// <response code="500">Internal server error</response>
    [Function("CreateTenant")]
    [OpenApiOperation(operationId: "CreateTenant", tags: ["Tenants"], Summary = "Create new tenant", Description = "Create a new tenant with validation and automatic ID generation")]
    [OpenApiRequestBody(contentType: "application/json", bodyType: typeof(CreateTenantRequest), Description = "Tenant creation request")]
    [OpenApiResponseWithBody(statusCode: HttpStatusCode.Created, contentType: "application/json", bodyType: typeof(TenantResponse), Description = "Tenant created successfully")]
    [OpenApiResponseWithBody(statusCode: HttpStatusCode.BadRequest, contentType: "application/json", bodyType: typeof(object), Description = "Validation failed")]
    [OpenApiResponseWithBody(statusCode: HttpStatusCode.InternalServerError, contentType: "application/json", bodyType: typeof(object), Description = "Internal server error")]
    public async Task<IActionResult> CreateTenantAsync(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "tenants")] HttpRequest req)
    {
        logger.LogInformation("Creating new tenant");

        try
        {
            var requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            var createRequest = JsonSerializer.Deserialize<CreateTenantRequest>(requestBody);

            // Comprehensive validation
            var validationErrors = ValidateCreateTenantRequest(createRequest);
            if (validationErrors.Count != 0)
            {
                return new BadRequestObjectResult(new { 
                    error = "Validation failed", 
                    errors = validationErrors 
                });
            }

            // TODO: Additional business logic validation
            // - Check if parent tenant exists and is active
            // - Validate tenant hierarchy depth
            // - Check for duplicate codes within the same parent
            // - Validate plan limits

            var tenantId = Guid.NewGuid();
            var tenant = new TenantResponse
            {
                Id = tenantId,
                TenantId = tenantId.ToString(), // Required for TenantScopedDto
                Name = createRequest!.Name,
                Slug = createRequest.TenantSlug,
                Description = createRequest.Description,
                ParentTenantId = createRequest.ParentTenantId,
                TenantType = createRequest.TenantType ?? "Organization",
                ContactEmail = createRequest.ContactEmail,
                ContactPhone = createRequest.ContactPhone,
                Plan = createRequest.SubscriptionTier, // Use SubscriptionTier from request
                MaxAgents = createRequest.MaxAgents, // MaxAgents has default value
                CreatedAt = DateTime.UtcNow,
                IsActive = true,
                AgentCount = 0
            };

            // TODO: Save to database with transaction
            // using var transaction = await dbContext.Database.BeginTransactionAsync();
            // dbContext.Tenants.Add(tenant);
            // await dbContext.SaveChangesAsync();
            // await transaction.CommitAsync();
            
            // Temporary: Save to in-memory collection
            lock (LockObject)
            {
                Tenants.Add(tenant);
            }

            var createdResult = new CreatedResult($"/api/tenants/{tenant.Id}", tenant);
            return createdResult;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error creating tenant");
            return new ObjectResult(new { error = "Failed to create tenant", details = ex.Message })
            {
                StatusCode = StatusCodes.Status500InternalServerError
            };
        }
    }

    /// <summary>
    /// Update an existing tenant with partial updates support
    /// </summary>
    /// <param name="req">HTTP request containing tenant update data</param>
    /// <param name="tenantId">The unique identifier of the tenant to update</param>
    /// <returns>Updated tenant details</returns>
    /// <response code="200">Tenant updated successfully</response>
    /// <response code="400">Invalid request data</response>
    /// <response code="404">Tenant not found</response>
    /// <response code="500">Internal server error</response>
    [Function("UpdateTenant")]
    [OpenApiOperation(operationId: "UpdateTenant", tags: ["Tenants"], Summary = "Update tenant", Description = "Update an existing tenant with partial updates support")]
    [OpenApiParameter(name: "tenantId", In = ParameterLocation.Path, Required = true, Type = typeof(string), Description = "The unique identifier of the tenant to update")]
    [OpenApiRequestBody(contentType: "application/json", bodyType: typeof(UpdateTenantRequest), Description = "Tenant update request")]
    [OpenApiResponseWithBody(statusCode: HttpStatusCode.OK, contentType: "application/json", bodyType: typeof(TenantResponse), Description = "Tenant updated successfully")]
    [OpenApiResponseWithBody(statusCode: HttpStatusCode.BadRequest, contentType: "application/json", bodyType: typeof(object), Description = "Invalid request data")]
    [OpenApiResponseWithBody(statusCode: HttpStatusCode.NotFound, contentType: "application/json", bodyType: typeof(object), Description = "Tenant not found")]
    [OpenApiResponseWithBody(statusCode: HttpStatusCode.InternalServerError, contentType: "application/json", bodyType: typeof(object), Description = "Internal server error")]
    public async Task<IActionResult> UpdateTenantAsync(
        [HttpTrigger(AuthorizationLevel.Function, "put", Route = "tenants/{tenantId}")] HttpRequest req,
        string tenantId)
    {
        logger.LogInformation("Updating tenant {TenantId}", tenantId);

        try
        {
            if (!Guid.TryParse(tenantId, out var id))
            {
                return new BadRequestObjectResult(new { error = "Invalid tenant ID format" });
            }

            var requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            var updateRequest = JsonSerializer.Deserialize<UpdateTenantRequest>(requestBody);

            logger.LogInformation($"Update request - TenantType: {updateRequest?.TenantType}");

            if (updateRequest == null)
            {
                return new BadRequestObjectResult(new { error = "Invalid update data" });
            }

            // TODO: Get tenant from database
            // var tenant = await dbContext.Tenants.FindAsync(id);
            TenantResponse? tenant;
            lock (LockObject)
            {
                tenant = Tenants.FirstOrDefault(t => t.Id == id);
            }

            if (tenant == null)
            {
                return new NotFoundObjectResult(new { error = "Tenant not found" });
            }

            // Validate updates
            var validationErrors = ValidateUpdateTenantRequest(updateRequest, tenant);
            if (validationErrors.Count != 0)
            {
                return new BadRequestObjectResult(new { 
                    error = "Validation failed", 
                    errors = validationErrors 
                });
            }

            // Apply updates (only non-null values)
            logger.LogInformation($"Before update - TenantType: {tenant.TenantType}");
            ApplyTenantUpdates(tenant, updateRequest);
            tenant.UpdatedAt = DateTime.UtcNow;
            logger.LogInformation($"After update - TenantType: {tenant.TenantType}");

            // Update the tenant in the in-memory list to ensure persistence
            lock (LockObject)
            {
                var index = Tenants.FindIndex(t => t.Id == tenant.Id);
                if (index >= 0)
                {
                    Tenants[index] = tenant;
                    logger.LogInformation($"Updated tenant at index {index} - TenantType: {Tenants[index].TenantType}");
                }
            }

            // TODO: Save changes to database
            // await dbContext.SaveChangesAsync();
            
            // Temporary: Changes are already applied to the in-memory object

            return new OkObjectResult(tenant);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error updating tenant {TenantId}", tenantId);
            return new ObjectResult(new { error = "Failed to update tenant", details = ex.Message })
            {
                StatusCode = StatusCodes.Status500InternalServerError
            };
        }
    }

    /// <summary>
    /// Delete a tenant with dependency checking
    /// </summary>
    /// <param name="req">HTTP request</param>
    /// <param name="tenantId">The unique identifier of the tenant to delete</param>
    /// <returns>No content if successful</returns>
    /// <response code="204">Tenant deleted successfully</response>
    /// <response code="400">Invalid tenant ID format</response>
    /// <response code="404">Tenant not found</response>
    /// <response code="409">Tenant has dependencies</response>
    /// <response code="500">Internal server error</response>
    [Function("DeleteTenant")]
    [OpenApiOperation(operationId: "DeleteTenant", tags: ["Tenants"], Summary = "Delete tenant", Description = "Delete a tenant with dependency checking and optional cascade deletion")]
    [OpenApiParameter(name: "tenantId", In = ParameterLocation.Path, Required = true, Type = typeof(string), Description = "The unique identifier of the tenant to delete")]
    [OpenApiParameter(name: "force", In = ParameterLocation.Query, Required = false, Type = typeof(bool), Description = "Force deletion even if dependencies exist")]
    [OpenApiResponseWithoutBody(statusCode: HttpStatusCode.NoContent, Description = "Tenant deleted successfully")]
    [OpenApiResponseWithBody(statusCode: HttpStatusCode.BadRequest, contentType: "application/json", bodyType: typeof(object), Description = "Invalid tenant ID format")]
    [OpenApiResponseWithBody(statusCode: HttpStatusCode.NotFound, contentType: "application/json", bodyType: typeof(object), Description = "Tenant not found")]
    [OpenApiResponseWithBody(statusCode: HttpStatusCode.Conflict, contentType: "application/json", bodyType: typeof(object), Description = "Tenant has dependencies")]
    [OpenApiResponseWithBody(statusCode: HttpStatusCode.InternalServerError, contentType: "application/json", bodyType: typeof(object), Description = "Internal server error")]
    public Task<IActionResult> DeleteTenantAsync(
        [HttpTrigger(AuthorizationLevel.Function, "delete", Route = "tenants/{tenantId}")] HttpRequest req,
        string tenantId)
    {
        logger.LogInformation("Deleting tenant {TenantId}", tenantId);

        try
        {
            if (!Guid.TryParse(tenantId, out var id))
            {
                return Task.FromResult<IActionResult>(new BadRequestObjectResult(new { error = "Invalid tenant ID format" }));
            }

            var forceStr = req.Query.TryGetValue("force", out var forceValues) ? forceValues.FirstOrDefault() : null;
            var force = bool.TryParse(forceStr, out var f) && f;

            // TODO: Get tenant and check dependencies
            // var tenant = await dbContext.Tenants.FindAsync(id);
            // var hasAgents = await dbContext.Agents.AnyAsync(a => a.TenantId == id);
            // var hasChildTenants = await dbContext.Tenants.AnyAsync(t => t.ParentTenantId == id);

            TenantResponse? tenant;
            lock (LockObject)
            {
                tenant = Tenants.FirstOrDefault(t => t.Id == id);
            }
            var hasAgents = false; // TODO: Check when agents are implemented
            var hasChildTenants = false; // TODO: Check when hierarchy is implemented

            if (tenant == null)
            {
                return Task.FromResult<IActionResult>(new NotFoundObjectResult(new { error = "Tenant not found" }));
            }

            if ((hasAgents || hasChildTenants) && !force)
            {
                return Task.FromResult<IActionResult>(new ConflictObjectResult(new { 
                    error = "Cannot delete tenant with dependencies",
                    dependencies = new {
                        hasAgents,
                        hasChildTenants
                    },
                    message = "Use ?force=true to override (this will delete all dependencies)"
                }));
            }

            // TODO: Implement deletion logic
            if (force && (hasAgents || hasChildTenants))
            {
                // Cascade delete or reassign dependencies
                // This should be done in a transaction
            }

            // Remove from in-memory collection (or soft delete by deactivating)
            lock (LockObject)
            {
                Tenants.Remove(tenant);
            }

            // TODO: Save changes to database
            // await dbContext.SaveChangesAsync();

            return Task.FromResult<IActionResult>(new NoContentResult());
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error deleting tenant {TenantId}", tenantId);
            return Task.FromResult<IActionResult>(new ObjectResult(new { error = "Failed to delete tenant", details = ex.Message })
            {
                StatusCode = StatusCodes.Status500InternalServerError
            });
        }
    }

    #endregion

    #region Advanced Operations

    /// <summary>
    /// Get tenant hierarchy (tree structure)
    /// </summary>
    /// <param name="req">HTTP request with hierarchy parameters</param>
    /// <returns>Hierarchical tenant tree structure</returns>
    /// <response code="200">Returns tenant hierarchy tree</response>
    /// <response code="500">Internal server error</response>
    [Function("GetTenantHierarchy")]
    [OpenApiOperation(operationId: "GetTenantHierarchy", tags: ["Tenants"], Summary = "Get tenant hierarchy", Description = "Retrieve the tenant hierarchy as a tree structure")]
    [OpenApiParameter(name: "rootTenantId", In = ParameterLocation.Query, Required = false, Type = typeof(string), Description = "Root tenant ID to start hierarchy from")]
    [OpenApiParameter(name: "maxDepth", In = ParameterLocation.Query, Required = false, Type = typeof(int), Description = "Maximum depth to traverse (default: 10)")]
    [OpenApiParameter(name: "includeInactive", In = ParameterLocation.Query, Required = false, Type = typeof(bool), Description = "Include inactive tenants in hierarchy")]
    [OpenApiResponseWithBody(statusCode: HttpStatusCode.OK, contentType: "application/json", bodyType: typeof(object), Description = "Tenant hierarchy tree")]
    [OpenApiResponseWithBody(statusCode: HttpStatusCode.InternalServerError, contentType: "application/json", bodyType: typeof(object), Description = "Internal server error")]
    public Task<IActionResult> GetTenantHierarchyAsync(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "tenants/hierarchy")] HttpRequest req)
    {
        logger.LogInformation("Getting tenant hierarchy");

        try
        {
            var rootTenantId = req.Query.TryGetValue("rootTenantId", out var rootTenantIdValues) ? rootTenantIdValues.FirstOrDefault() : null;
            var maxDepthStr = req.Query.TryGetValue("maxDepth", out var maxDepthValues) ? maxDepthValues.FirstOrDefault() : null;
            var maxDepth = int.TryParse(maxDepthStr, out var depth) ? depth : 10;
            var includeInactiveStr = req.Query.TryGetValue("includeInactive", out var includeInactiveValues) ? includeInactiveValues.FirstOrDefault() : null;
            var includeInactive = bool.TryParse(includeInactiveStr, out var inactive) && inactive;

            // TODO: Build hierarchical tree from database
            var hierarchy = BuildTenantHierarchy(rootTenantId, maxDepth, includeInactive);

            return Task.FromResult<IActionResult>(new OkObjectResult(hierarchy));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error getting tenant hierarchy");
            return Task.FromResult<IActionResult>(new ObjectResult(new { error = "Failed to retrieve hierarchy", details = ex.Message })
            {
                StatusCode = StatusCodes.Status500InternalServerError
            });
        }
    }

    /// <summary>
    /// Bulk operations on multiple tenants
    /// </summary>
    /// <param name="req">HTTP request containing bulk operation data</param>
    /// <returns>Results of bulk operations on tenants</returns>
    /// <response code="200">Bulk operations completed</response>
    /// <response code="400">Invalid bulk operation request</response>
    /// <response code="500">Internal server error</response>
    [Function("BulkTenantOperations")]
    [OpenApiOperation(operationId: "BulkTenantOperations", tags: ["Tenants"], Summary = "Bulk tenant operations", Description = "Perform bulk operations on multiple tenants")]
    [OpenApiRequestBody(contentType: "application/json", bodyType: typeof(BulkTenantOperationRequest), Description = "Bulk operation request")]
    [OpenApiResponseWithBody(statusCode: HttpStatusCode.OK, contentType: "application/json", bodyType: typeof(object), Description = "Bulk operations completed")]
    [OpenApiResponseWithBody(statusCode: HttpStatusCode.BadRequest, contentType: "application/json", bodyType: typeof(object), Description = "Invalid bulk operation request")]
    [OpenApiResponseWithBody(statusCode: HttpStatusCode.InternalServerError, contentType: "application/json", bodyType: typeof(object), Description = "Internal server error")]
    public async Task<IActionResult> BulkTenantOperationsAsync(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "tenants/bulk")] HttpRequest req)
    {
        logger.LogInformation("Performing bulk tenant operations");

        try
        {
            var requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            var bulkRequest = JsonSerializer.Deserialize<BulkTenantOperationRequest>(requestBody);

            if (bulkRequest == null || bulkRequest.TenantIds.Count == 0)
            {
                return new BadRequestObjectResult(new { error = "Invalid bulk operation request" });
            }

            var results = new List<BulkOperationResult<object>>();

            // TODO: Implement bulk operations with proper transaction handling
            foreach (var tenantId in bulkRequest.TenantIds)
            {
                try
                {
                    var result = await ProcessBulkTenantOperation(tenantId, bulkRequest.Operation, bulkRequest.Data);
                    results.Add(result);
                }
                catch (Exception ex)
                {
                    results.Add(new BulkOperationResult<object>
                    {
                        Id = tenantId,
                        Operation = "Delete",
                        TenantId = tenantId,
                        Success = false,
                        Error = ex.Message
                    });
                }
            }

            return new OkObjectResult(new { results });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error performing bulk operations");
            return new ObjectResult(new { error = "Failed to perform bulk operations", details = ex.Message })
            {
                StatusCode = StatusCodes.Status500InternalServerError
            };
        }
    }

    /// <summary>
    /// Get tenant statistics and summary information
    /// </summary>
    /// <param name="req">HTTP request with statistics parameters</param>
    /// <returns>Comprehensive tenant statistics and metrics</returns>
    /// <response code="200">Returns tenant statistics</response>
    /// <response code="500">Internal server error</response>
    [Function("GetTenantStatistics")]
    [OpenApiOperation(operationId: "GetTenantStatistics", tags: ["Tenants"], Summary = "Get tenant statistics", Description = "Retrieve comprehensive tenant statistics and summary information")]
    [OpenApiParameter(name: "includeInactive", In = ParameterLocation.Query, Required = false, Type = typeof(bool), Description = "Include inactive tenants in statistics")]
    [OpenApiResponseWithBody(statusCode: HttpStatusCode.OK, contentType: "application/json", bodyType: typeof(object), Description = "Tenant statistics")]
    [OpenApiResponseWithBody(statusCode: HttpStatusCode.InternalServerError, contentType: "application/json", bodyType: typeof(object), Description = "Internal server error")]
    public Task<IActionResult> GetTenantStatisticsAsync(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "tenants/_stats")] HttpRequest req)
    {
        logger.LogInformation("Getting tenant statistics");

        try
        {
            var includeInactiveStr = req.Query.TryGetValue("includeInactive", out var includeInactiveValues) ? includeInactiveValues.FirstOrDefault() : null;
            var includeInactive = bool.TryParse(includeInactiveStr, out var inactive) && inactive;

            List<TenantResponse> allTenants;
            lock (LockObject)
            {
                allTenants = [.. Tenants];
            }

            var activeTenants = allTenants.Where(t => t.IsActive == true).ToList();
            var tenantsToAnalyze = includeInactive ? allTenants : activeTenants;

            var statistics = new
            {
                TotalTenants = allTenants.Count,
                ActiveTenants = activeTenants.Count,
                InactiveTenants = allTenants.Count - activeTenants.Count,
                TotalAgents = tenantsToAnalyze.Sum(t => t.AgentCount),
                AverageAgentsPerTenant = tenantsToAnalyze.Count > 0 ? (double)tenantsToAnalyze.Sum(t => t.AgentCount) / tenantsToAnalyze.Count : 0,
                ByPlan = tenantsToAnalyze.GroupBy(t => t.Plan).ToDictionary(g => g.Key?.ToString() ?? "Unknown", g => g.Count()),
                ByType = tenantsToAnalyze.GroupBy(t => t.TenantType).ToDictionary(g => g.Key ?? "Unknown", g => g.Count()),
                RecentlyCreated = tenantsToAnalyze.Where(t => t.CreatedAt > DateTime.UtcNow.AddDays(-30)).Count(),
                RecentlyUpdated = tenantsToAnalyze.Where(t => t.UpdatedAt > DateTime.UtcNow.AddDays(-7)).Count()
            };

            return Task.FromResult<IActionResult>(new OkObjectResult(statistics));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error getting tenant statistics");
            return Task.FromResult<IActionResult>(new ObjectResult(new { error = "Failed to get statistics", details = ex.Message })
            {
                StatusCode = StatusCodes.Status500InternalServerError
            });
        }
    }

    #endregion

    #region Helper Methods

    private IEnumerable<TenantResponse> ApplyTenantFilters(List<TenantResponse> tenants, string? parentTenantId,
        string? tenantType, bool? isActive, string? plan, string? search, bool includeInactive)
    {
        var filtered = tenants.AsEnumerable();

        if (!includeInactive)
        {
            filtered = filtered.Where(t => t.IsActive == true);
        }

        if (!string.IsNullOrEmpty(parentTenantId) && Guid.TryParse(parentTenantId, out var parentId))
        {
            filtered = filtered.Where(t => t.ParentTenantId == parentId.ToString());
        }

        if (!string.IsNullOrEmpty(tenantType))
        {
            filtered = filtered.Where(t => string.Equals(t.TenantType, tenantType, StringComparison.OrdinalIgnoreCase));
        }

        if (isActive.HasValue)
        {
            filtered = filtered.Where(t => t.IsActive == isActive.Value);
        }

        if (!string.IsNullOrEmpty(plan) && Enum.TryParse<SubscriptionTier>(plan, true, out var planEnum))
        {
            filtered = filtered.Where(t => t.Plan == planEnum);
        }

        if (!string.IsNullOrEmpty(search))
        {
            filtered = filtered.Where(t => 
                (!string.IsNullOrEmpty(t.Name) && t.Name.Contains(search, StringComparison.OrdinalIgnoreCase)) ||
                (t.Slug?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (t.Description?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        return filtered;
    }

    private IEnumerable<TenantResponse> ApplyTenantSorting(IEnumerable<TenantResponse> tenants, string sortBy, string sortOrder)
    {
        // Handle empty collections
        if (!tenants.Any())
        {
            return tenants;
        }

        var ordered = sortBy.ToLower() switch
        {
            "name" => sortOrder.Equals("desc", StringComparison.CurrentCultureIgnoreCase) ? tenants.OrderByDescending(t => t.Name ?? string.Empty) : tenants.OrderBy(t => t.Name ?? string.Empty),
            "slug" => sortOrder.Equals("desc", StringComparison.CurrentCultureIgnoreCase) ? tenants.OrderByDescending(t => t.Slug ?? string.Empty) : tenants.OrderBy(t => t.Slug ?? string.Empty),
            "created" => sortOrder.Equals("desc", StringComparison.CurrentCultureIgnoreCase) ? tenants.OrderByDescending(t => t.CreatedAt) : tenants.OrderBy(t => t.CreatedAt),
            "updated" => sortOrder.Equals("desc", StringComparison.CurrentCultureIgnoreCase) ? tenants.OrderByDescending(t => t.UpdatedAt ?? DateTime.MinValue) : tenants.OrderBy(t => t.UpdatedAt ?? DateTime.MinValue),
            "agents" => sortOrder.Equals("desc", StringComparison.CurrentCultureIgnoreCase) ? tenants.OrderByDescending(t => t.AgentCount) : tenants.OrderBy(t => t.AgentCount),
            _ => tenants.OrderBy(t => t.Name ?? string.Empty)
        };

        return ordered;
    }

    private List<string> ValidateCreateTenantRequest(CreateTenantRequest? request)
    {
        var errors = new List<string>();

        if (request == null)
        {
            errors.Add("Request body is required");
            return errors;
        }

        if (string.IsNullOrWhiteSpace(request.Name))
            errors.Add("Name is required");

        if (request.Name.Length > 200)
            errors.Add("Name cannot exceed 200 characters");

        if (!string.IsNullOrEmpty(request.TenantSlug) && request.TenantSlug.Length > 20)
            errors.Add("TenantSlug cannot exceed 20 characters");

        if (!string.IsNullOrEmpty(request.ContactEmail) && !IsValidEmail(request.ContactEmail))
            errors.Add("Invalid email format");

        if (request.MaxAgents < 1)
            errors.Add("MaxAgents must be greater than 0");

        return errors;
    }

    private List<string> ValidateUpdateTenantRequest(UpdateTenantRequest request, TenantResponse _)
    {
        var errors = new List<string>();

        if (!string.IsNullOrEmpty(request.Name) && request.Name.Length > 200)
            errors.Add("Name cannot exceed 200 characters");

        if (!string.IsNullOrEmpty(request.Slug) && request.Slug.Length > 20)
            errors.Add("Slug cannot exceed 20 characters");

        if (!string.IsNullOrEmpty(request.ContactEmail) && !IsValidEmail(request.ContactEmail))
            errors.Add("Invalid email format");

        if (request.MaxAgents is < 1)
            errors.Add("MaxAgents must be greater than 0");

        return errors;
    }

    private void ApplyTenantUpdates(TenantResponse tenant, UpdateTenantRequest update)
    {
        if (!string.IsNullOrWhiteSpace(update.Name))
            tenant.Name = update.Name;
        if (update.Slug != null)
            tenant.Slug = update.Slug;
        if (update.Description != null)
            tenant.Description = update.Description;
        if (update.TenantType != null)
            tenant.TenantType = update.TenantType;
        if (update.ContactEmail != null)
            tenant.ContactEmail = update.ContactEmail;
        if (update.ContactPhone != null)
            tenant.ContactPhone = update.ContactPhone;
        if (update.Plan != null && Enum.TryParse<SubscriptionTier>(update.Plan, out var planValue))
            tenant.Plan = planValue;
        if (update.MaxAgents.HasValue)
            tenant.MaxAgents = update.MaxAgents.Value;
        if (update.IsActive.HasValue)
            tenant.IsActive = update.IsActive.Value;
    }

    // TODO: Implement when needed
    // private int GetDefaultMaxAgents(SubscriptionTier plan)
    // {
    //     return plan switch
    //     {
    //         SubscriptionTier.Basic => 10,
    //         SubscriptionTier.Professional => 50,
    //         SubscriptionTier.Enterprise => 200,
    //         _ => 10
    //     };
    // }

    private bool IsValidEmail(string email)
    {
        try
        {
            var addr = new SystemNet.Mail.MailAddress(email);
            return addr.Address == email;
        }
        catch
        {
            return false;
        }
    }

    private object BuildTenantHierarchy(string? _, int __, bool ___)
    {
        // TODO: Implement hierarchical tree building
        return new { message = "Hierarchy building not yet implemented" };
    }

    private Task<BulkOperationResult<object>> ProcessBulkTenantOperation(Guid tenantId, string operation, object? _)
    {
        // TODO: Implement bulk operations
        return Task.FromResult(new BulkOperationResult<object>
        {
            Id = tenantId,
            Operation = operation,
            TenantId = tenantId,
            Success = true,
            Message = $"Operation {operation} completed"
        });
    }

    private IEnumerable<TenantResponse> ApplyTenantSearchFilters(List<TenantResponse> tenants, string? slug, string? name, string? email, 
        string? tenantType, string? plan, bool? isActive, string? parentTenantId, string? generalQuery, bool includeInactive)
    {
        var filtered = tenants.AsEnumerable();

        // Active/inactive filter
        if (!includeInactive)
        {
            filtered = filtered.Where(t => t.IsActive == true);
        }

        // Exact slug match
        if (!string.IsNullOrEmpty(slug))
        {
            filtered = filtered.Where(t => string.Equals(t.Slug, slug, StringComparison.OrdinalIgnoreCase));
        }

        // Partial name match
        if (!string.IsNullOrEmpty(name))
        {
            filtered = filtered.Where(t => !string.IsNullOrEmpty(t.Name) && t.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
        }

        // Exact email match
        if (!string.IsNullOrEmpty(email))
        {
            filtered = filtered.Where(t => string.Equals(t.ContactEmail, email, StringComparison.OrdinalIgnoreCase));
        }

        // Tenant type filter
        if (!string.IsNullOrEmpty(tenantType))
        {
            filtered = filtered.Where(t => string.Equals(t.TenantType, tenantType, StringComparison.OrdinalIgnoreCase));
        }

        // Plan filter
        if (!string.IsNullOrEmpty(plan) && Enum.TryParse<SubscriptionTier>(plan, true, out var planEnum))
        {
            filtered = filtered.Where(t => t.Plan == planEnum);
        }

        // Active status filter
        if (isActive.HasValue)
        {
            filtered = filtered.Where(t => t.IsActive == isActive.Value);
        }

        // Parent tenant filter
        if (!string.IsNullOrEmpty(parentTenantId) && Guid.TryParse(parentTenantId, out var parentId))
        {
            filtered = filtered.Where(t => t.ParentTenantId == parentId.ToString());
        }

        // General search query (searches across multiple fields)
        if (!string.IsNullOrEmpty(generalQuery))
        {
            filtered = filtered.Where(t => 
                (!string.IsNullOrEmpty(t.Name) && t.Name.Contains(generalQuery, StringComparison.OrdinalIgnoreCase)) ||
                (t.Slug?.Contains(generalQuery, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (t.Description?.Contains(generalQuery, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (t.ContactEmail?.Contains(generalQuery, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        return filtered;
    }

    #endregion
}

#region Supporting DTOs

/// <summary>
/// Request model for bulk tenant operations
/// </summary>
public class BulkTenantOperationRequest
{
    /// <summary>
    /// List of tenant IDs to perform operations on
    /// </summary>
    public List<Guid> TenantIds { get; set; } = [];
    
    /// <summary>
    /// Operation to perform on the tenants
    /// </summary>
    public string Operation { get; set; } = string.Empty;
    
    /// <summary>
    /// Additional data for the operation (optional)
    /// </summary>
    public object? Data { get; set; }
}

/// <summary>
/// Result of a bulk operation on a single tenant
/// </summary>
public class BulkOperationResult<T>
{
    /// <summary>
    /// The tenant ID that was processed
    /// </summary>
    public Guid Id { get; set; }
    
    /// <summary>
    /// The tenant ID (for backward compatibility)
    /// </summary>
    public Guid TenantId { get; set; }
    
    /// <summary>
    /// The operation that was performed
    /// </summary>
    public string Operation { get; set; } = string.Empty;
    
    /// <summary>
    /// Whether the operation was successful
    /// </summary>
    public bool Success { get; set; }
    
    /// <summary>
    /// Result data from the operation
    /// </summary>
    public T? Data { get; set; }
    
    /// <summary>
    /// Success message or error details
    /// </summary>
    public string? Message { get; set; }
    
    /// <summary>
    /// Error message if operation failed
    /// </summary>
    public string? Error { get; set; }
}

#endregion
