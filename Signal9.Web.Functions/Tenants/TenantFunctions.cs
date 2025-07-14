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

namespace Signal9.Web.Functions.Tenants;

/// <summary>
/// Azure Functions for comprehensive tenant management operations
/// </summary>
public class TenantFunctions
{
    private readonly ILogger<TenantFunctions> _logger;
    
    // Temporary in-memory storage until database is implemented
    private static readonly List<TenantResponse> Tenants = new();
    private static readonly object LockObject = new();
    private static bool _initialized;

    public TenantFunctions(ILogger<TenantFunctions> logger)
    {
        _logger = logger;
        
        // Initialize with mock data only once
        if (!_initialized)
        {
            lock (LockObject)
            {
                if (!_initialized)
                {
                    Tenants.AddRange(GenerateMockTenants());
                    _initialized = true;
                }
            }
        }
    }

    #region Basic CRUD Operations

    /// <summary>
    /// Get all tenants with advanced filtering, sorting, and pagination
    /// </summary>
    [Function("GetTenants")]
    public async Task<HttpResponseData> GetTenantsAsync(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "tenants")] HttpRequestData req)
    {
        _logger.LogInformation("Getting tenants with filters");

        try
        {
            var query = SystemWeb.HttpUtility.ParseQueryString(req.Url.Query);
            
            // Filtering parameters
            var parentTenantId = query["parentTenantId"];
            var tenantType = query["tenantType"];
            var isActive = bool.TryParse(query["isActive"], out var active) ? active : (bool?)null;
            var plan = query["plan"];
            var search = query["search"]; // Search across name, code, description
            
            // Sorting parameters
            var sortBy = query["sortBy"] ?? "name";
            var sortOrder = query["sortOrder"] ?? "asc";
            
            // Pagination parameters
            var page = int.TryParse(query["page"], out var p) ? Math.Max(1, p) : 1;
            var pageSize = int.TryParse(query["pageSize"], out var ps) ? Math.Max(1, Math.Min(100, ps)) : 20;
            
            // Include parameters
            var includeAgentCount = bool.TryParse(query["includeAgentCount"], out var includeAc) && includeAc;
            var includeInactive = bool.TryParse(query["includeInactive"], out var includeI) && includeI;

            // TODO: Replace with actual database query using Entity Framework
            List<TenantResponse> allTenants;
            lock (LockObject)
            {
                allTenants = Tenants.ToList(); // Create a copy to avoid modifications during enumeration
            }
            var filteredTenants = ApplyTenantFilters(allTenants, parentTenantId, tenantType, isActive, plan, search, includeInactive);
            var sortedTenantsEnum = ApplyTenantSorting(filteredTenants, sortBy, sortOrder);
            var sortedTenants = sortedTenantsEnum.ToList(); // Materialize to avoid multiple enumeration
            
            var totalCount = sortedTenants.Count;
            
            var pagedTenants = sortedTenants
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToList();

            // Enrich with additional data if requested
            if (includeAgentCount)
            {
                // TODO: Add agent counts from database
                foreach (var tenant in pagedTenants)
                {
                    tenant.AgentCount = new Random().Next(0, 50);
                }
            }

            var result = new PagedResponse<TenantResponse>
            {
                Items = pagedTenants,
                Page = page,
                PageSize = pageSize,
                TotalCount = totalCount
            };

            var response = req.CreateResponse(HttpStatusCode.OK);
            response.Headers.Add("Content-Type", "application/json");
            await response.WriteStringAsync(JsonSerializer.Serialize(result, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            }));

            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting tenants");
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteStringAsync(JsonSerializer.Serialize(new { error = "Failed to retrieve tenants", details = ex.Message }));
            return errorResponse;
        }
    }

    /// <summary>
    /// Get a specific tenant by ID with optional related data
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
                await badRequestResponse.WriteStringAsync(JsonSerializer.Serialize(new { error = "Invalid tenant ID format" }));
                return badRequestResponse;
            }

            // TODO: Replace with actual database query
            TenantResponse? tenant;
            lock (LockObject)
            {
                tenant = Tenants.FirstOrDefault(t => t.Id == id.ToString());
            }

            if (tenant == null)
            {
                var notFoundResponse = req.CreateResponse(HttpStatusCode.NotFound);
                await notFoundResponse.WriteStringAsync(JsonSerializer.Serialize(new { error = "Tenant not found" }));
                return notFoundResponse;
            }

            // TODO: Include related data based on query parameters
            // if (includeChildren)
            // {
            //     // tenant.ChildTenants = await GetChildTenants(id);
            // }

            var response = req.CreateResponse(HttpStatusCode.OK);
            response.Headers.Add("Content-Type", "application/json");
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
            await errorResponse.WriteStringAsync(JsonSerializer.Serialize(new { error = "Failed to retrieve tenant", details = ex.Message }));
            return errorResponse;
        }
    }

    /// <summary>
    /// Create a new tenant with comprehensive validation
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

            // Comprehensive validation
            var validationErrors = ValidateCreateTenantRequest(createRequest);
            if (validationErrors.Any())
            {
                var badRequestResponse = req.CreateResponse(HttpStatusCode.BadRequest);
                await badRequestResponse.WriteStringAsync(JsonSerializer.Serialize(new { 
                    error = "Validation failed", 
                    errors = validationErrors 
                }));
                return badRequestResponse;
            }

            // TODO: Additional business logic validation
            // - Check if parent tenant exists and is active
            // - Validate tenant hierarchy depth
            // - Check for duplicate codes within the same parent
            // - Validate plan limits

            var tenantId = Guid.NewGuid();
            var tenant = new TenantResponse
            {
                Id = tenantId.ToString(),
                TenantId = tenantId.ToString(), // Required for TenantScopedDto
                Name = createRequest!.Name,
                Code = createRequest.Code,
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

            var response = req.CreateResponse(HttpStatusCode.Created);
            response.Headers.Add("Content-Type", "application/json");
            response.Headers.Add("Location", $"/api/tenants/{tenant.Id}");
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
            await errorResponse.WriteStringAsync(JsonSerializer.Serialize(new { error = "Failed to create tenant", details = ex.Message }));
            return errorResponse;
        }
    }

    /// <summary>
    /// Update an existing tenant with partial updates support
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
                await badRequestResponse.WriteStringAsync(JsonSerializer.Serialize(new { error = "Invalid tenant ID format" }));
                return badRequestResponse;
            }

            var requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            var updateRequest = JsonSerializer.Deserialize<UpdateTenantRequest>(requestBody, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });

            _logger.LogInformation($"Update request - TenantType: {updateRequest?.TenantType}");

            if (updateRequest == null)
            {
                var badRequestResponse = req.CreateResponse(HttpStatusCode.BadRequest);
                await badRequestResponse.WriteStringAsync(JsonSerializer.Serialize(new { error = "Invalid update data" }));
                return badRequestResponse;
            }

            // TODO: Get tenant from database
            // var tenant = await dbContext.Tenants.FindAsync(id);
            TenantResponse? tenant;
            lock (LockObject)
            {
                tenant = Tenants.FirstOrDefault(t => t.Id == id.ToString());
            }

            if (tenant == null)
            {
                var notFoundResponse = req.CreateResponse(HttpStatusCode.NotFound);
                await notFoundResponse.WriteStringAsync(JsonSerializer.Serialize(new { error = "Tenant not found" }));
                return notFoundResponse;
            }

            // Validate updates
            var validationErrors = ValidateUpdateTenantRequest(updateRequest, tenant);
            if (validationErrors.Any())
            {
                var badRequestResponse = req.CreateResponse(HttpStatusCode.BadRequest);
                await badRequestResponse.WriteStringAsync(JsonSerializer.Serialize(new { 
                    error = "Validation failed", 
                    errors = validationErrors 
                }));
                return badRequestResponse;
            }

            // Apply updates (only non-null values)
            _logger.LogInformation($"Before update - TenantType: {tenant.TenantType}");
            ApplyTenantUpdates(tenant, updateRequest);
            tenant.UpdatedAt = DateTime.UtcNow;
            _logger.LogInformation($"After update - TenantType: {tenant.TenantType}");

            // Update the tenant in the in-memory list to ensure persistence
            lock (LockObject)
            {
                var index = Tenants.FindIndex(t => t.Id == tenant.Id);
                if (index >= 0)
                {
                    Tenants[index] = tenant;
                    _logger.LogInformation($"Updated tenant at index {index} - TenantType: {Tenants[index].TenantType}");
                }
            }

            // TODO: Save changes to database
            // await dbContext.SaveChangesAsync();
            
            // Temporary: Changes are already applied to the in-memory object

            var response = req.CreateResponse(HttpStatusCode.OK);
            response.Headers.Add("Content-Type", "application/json");
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
            await errorResponse.WriteStringAsync(JsonSerializer.Serialize(new { error = "Failed to update tenant", details = ex.Message }));
            return errorResponse;
        }
    }

    /// <summary>
    /// Delete a tenant with dependency checking
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
                await badRequestResponse.WriteStringAsync(JsonSerializer.Serialize(new { error = "Invalid tenant ID format" }));
                return badRequestResponse;
            }

            var query = SystemWeb.HttpUtility.ParseQueryString(req.Url.Query);
            var force = bool.TryParse(query["force"], out var f) && f;

            // TODO: Get tenant and check dependencies
            // var tenant = await dbContext.Tenants.FindAsync(id);
            // var hasAgents = await dbContext.Agents.AnyAsync(a => a.TenantId == id);
            // var hasChildTenants = await dbContext.Tenants.AnyAsync(t => t.ParentTenantId == id);

            TenantResponse? tenant;
            lock (LockObject)
            {
                tenant = Tenants.FirstOrDefault(t => t.Id == id.ToString());
            }
            var hasAgents = false; // TODO: Check when agents are implemented
            var hasChildTenants = false; // TODO: Check when hierarchy is implemented

            if (tenant == null)
            {
                var notFoundResponse = req.CreateResponse(HttpStatusCode.NotFound);
                await notFoundResponse.WriteStringAsync(JsonSerializer.Serialize(new { error = "Tenant not found" }));
                return notFoundResponse;
            }

            if ((hasAgents || hasChildTenants) && !force)
            {
                var conflictResponse = req.CreateResponse(HttpStatusCode.Conflict);
                await conflictResponse.WriteStringAsync(JsonSerializer.Serialize(new { 
                    error = "Cannot delete tenant with dependencies",
                    dependencies = new {
                        hasAgents,
                        hasChildTenants
                    },
                    message = "Use ?force=true to override (this will delete all dependencies)"
                }));
                return conflictResponse;
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

            var response = req.CreateResponse(HttpStatusCode.NoContent);
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting tenant {TenantId}", tenantId);
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteStringAsync(JsonSerializer.Serialize(new { error = "Failed to delete tenant", details = ex.Message }));
            return errorResponse;
        }
    }

    #endregion

    #region Advanced Operations

    /// <summary>
    /// Get tenant hierarchy (tree structure)
    /// </summary>
    [Function("GetTenantHierarchy")]
    public async Task<HttpResponseData> GetTenantHierarchyAsync(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "tenants/hierarchy")] HttpRequestData req)
    {
        _logger.LogInformation("Getting tenant hierarchy");

        try
        {
            var query = SystemWeb.HttpUtility.ParseQueryString(req.Url.Query);
            var rootTenantId = query["rootTenantId"];
            var maxDepth = int.TryParse(query["maxDepth"], out var depth) ? depth : 10;
            var includeInactive = bool.TryParse(query["includeInactive"], out var inactive) && inactive;

            // TODO: Build hierarchical tree from database
            var hierarchy = BuildTenantHierarchy(rootTenantId, maxDepth, includeInactive);

            var response = req.CreateResponse(HttpStatusCode.OK);
            response.Headers.Add("Content-Type", "application/json");
            await response.WriteStringAsync(JsonSerializer.Serialize(hierarchy, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            }));

            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting tenant hierarchy");
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteStringAsync(JsonSerializer.Serialize(new { error = "Failed to retrieve hierarchy", details = ex.Message }));
            return errorResponse;
        }
    }

    /// <summary>
    /// Bulk operations on multiple tenants
    /// </summary>
    [Function("BulkTenantOperations")]
    public async Task<HttpResponseData> BulkTenantOperationsAsync(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "tenants/bulk")] HttpRequestData req)
    {
        _logger.LogInformation("Performing bulk tenant operations");

        try
        {
            var requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            var bulkRequest = JsonSerializer.Deserialize<BulkTenantOperationRequest>(requestBody, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });

            if (bulkRequest == null || !bulkRequest.TenantIds.Any())
            {
                var badRequestResponse = req.CreateResponse(HttpStatusCode.BadRequest);
                await badRequestResponse.WriteStringAsync(JsonSerializer.Serialize(new { error = "Invalid bulk operation request" }));
                return badRequestResponse;
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

            var response = req.CreateResponse(HttpStatusCode.OK);
            response.Headers.Add("Content-Type", "application/json");
            await response.WriteStringAsync(JsonSerializer.Serialize(new { results }, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            }));

            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error performing bulk operations");
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteStringAsync(JsonSerializer.Serialize(new { error = "Failed to perform bulk operations", details = ex.Message }));
            return errorResponse;
        }
    }

    #endregion

    #region Helper Methods

    private List<TenantResponse> GenerateMockTenants()
    {
        // TODO: Replace with actual database query
        return new List<TenantResponse>
        {
            new TenantResponse
            {
                Id = Guid.NewGuid().ToString(),
                TenantId = Guid.NewGuid().ToString(), // Required for TenantScopedDto
                Name = "Acme Corporation",
                Code = "ACME",
                Description = "Large enterprise corporation",
                TenantType = "Organization",
                ContactEmail = "admin@acme.com",
                ContactPhone = "+1-555-0100",
                Plan = SubscriptionTier.Enterprise,
                MaxAgents = 500,
                IsActive = true,
                CreatedAt = DateTime.UtcNow.AddDays(-90),
                UpdatedAt = DateTime.UtcNow.AddDays(-10),
                AgentCount = 245
            },
            new TenantResponse
            {
                Id = Guid.NewGuid().ToString(),
                TenantId = Guid.NewGuid().ToString(), // Required for TenantScopedDto
                Name = "Demo Organization",
                Code = "DEMO",
                Description = "Demo organization for testing",
                TenantType = "Organization",
                ContactEmail = "demo@signal9.com",
                ContactPhone = "+1-555-0123",
                Plan = SubscriptionTier.Professional,
                MaxAgents = 100,
                IsActive = true,
                CreatedAt = DateTime.UtcNow.AddDays(-30),
                UpdatedAt = DateTime.UtcNow.AddDays(-5),
                AgentCount = 15
            },
            new TenantResponse
            {
                Id = Guid.NewGuid().ToString(),
                TenantId = Guid.NewGuid().ToString(), // Required for TenantScopedDto
                Name = "Small Business Ltd",
                Code = "SMALL",
                Description = "Small business testing basic features",
                TenantType = "Organization",
                ContactEmail = "contact@smallbiz.com",
                Plan = SubscriptionTier.Basic,
                MaxAgents = 25,
                IsActive = true,
                CreatedAt = DateTime.UtcNow.AddDays(-15),
                AgentCount = 8
            }
        };
    }

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
                (t.Code?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (t.Description?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        return filtered;
    }

    private IEnumerable<TenantResponse> ApplyTenantSorting(IEnumerable<TenantResponse> tenants, string sortBy, string sortOrder)
    {
        var ordered = sortBy.ToLower() switch
        {
            "name" => sortOrder.ToLower() == "desc" ? tenants.OrderByDescending(t => t.Name) : tenants.OrderBy(t => t.Name),
            "code" => sortOrder.ToLower() == "desc" ? tenants.OrderByDescending(t => t.Code) : tenants.OrderBy(t => t.Code),
            "created" => sortOrder.ToLower() == "desc" ? tenants.OrderByDescending(t => t.CreatedAt) : tenants.OrderBy(t => t.CreatedAt),
            "updated" => sortOrder.ToLower() == "desc" ? tenants.OrderByDescending(t => t.UpdatedAt) : tenants.OrderBy(t => t.UpdatedAt),
            "agents" => sortOrder.ToLower() == "desc" ? tenants.OrderByDescending(t => t.AgentCount) : tenants.OrderBy(t => t.AgentCount),
            _ => tenants.OrderBy(t => t.Name)
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

        if (request.Name.Length > 100)
            errors.Add("Name cannot exceed 100 characters");

        if (!string.IsNullOrEmpty(request.Code) && request.Code.Length > 20)
            errors.Add("Code cannot exceed 20 characters");

        if (!string.IsNullOrEmpty(request.ContactEmail) && !IsValidEmail(request.ContactEmail))
            errors.Add("Invalid email format");

        if (request.MaxAgents < 1)
            errors.Add("MaxAgents must be greater than 0");

        return errors;
    }

    private List<string> ValidateUpdateTenantRequest(UpdateTenantRequest request, TenantResponse _)
    {
        var errors = new List<string>();

        if (!string.IsNullOrEmpty(request.Name) && request.Name.Length > 100)
            errors.Add("Name cannot exceed 100 characters");

        if (!string.IsNullOrEmpty(request.Code) && request.Code.Length > 20)
            errors.Add("Code cannot exceed 20 characters");

        if (!string.IsNullOrEmpty(request.ContactEmail) && !IsValidEmail(request.ContactEmail))
            errors.Add("Invalid email format");

        if (request.MaxAgents.HasValue && request.MaxAgents < 1)
            errors.Add("MaxAgents must be greater than 0");

        return errors;
    }

    private void ApplyTenantUpdates(TenantResponse tenant, UpdateTenantRequest update)
    {
        if (!string.IsNullOrWhiteSpace(update.Name))
            tenant.Name = update.Name;
        if (update.Code != null)
            tenant.Code = update.Code;
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

    #endregion
}

#region Supporting DTOs

public class BulkTenantOperationRequest
{
    public List<Guid> TenantIds { get; set; } = new();
    public string Operation { get; set; } = string.Empty; // "activate", "deactivate", "delete", "update_plan"
    public object? Data { get; set; }
}

#endregion
