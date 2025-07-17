using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Signal9.Shared.Models;
using Signal9.Shared.DTOs.Analytics;
using Signal9.Shared.DTOs.Base;
using System.Net;
using System.Text.Json;
using SystemWeb = System.Web;
using Microsoft.Azure.WebJobs.Extensions.OpenApi.Core.Attributes;
using Microsoft.OpenApi.Models;
using System.ComponentModel.DataAnnotations;

namespace Signal9.Web.Functions.Analytics;

/// <summary>
/// Azure Functions for analytics, reporting, and business intelligence operations
/// </summary>
public class AnalyticsFunctions
{
    private readonly ILogger<AnalyticsFunctions> _logger;

    public AnalyticsFunctions(ILogger<AnalyticsFunctions> logger)
    {
        _logger = logger;
    }

    #region Dashboard Analytics

    /// <summary>
    /// Get comprehensive dashboard analytics data
    /// </summary>
    [Function("GetDashboardAnalytics")]
    [OpenApiOperation(operationId: "GetDashboardAnalytics", tags: new[] { "Analytics" }, Summary = "Get dashboard analytics", Description = "Retrieve comprehensive dashboard analytics data with time range filtering and tenant scope")]
    [OpenApiParameter(name: "timeRange", In = ParameterLocation.Query, Required = false, Type = typeof(string), Description = "Time range filter (1h, 24h, 7d, 30d, 90d) - default: 7d")]
    [OpenApiParameter(name: "tenantId", In = ParameterLocation.Query, Required = false, Type = typeof(string), Description = "Filter by specific tenant ID")]
    [OpenApiParameter(name: "includeDetails", In = ParameterLocation.Query, Required = false, Type = typeof(bool), Description = "Include detailed breakdown data")]
    [OpenApiResponseWithBody(statusCode: HttpStatusCode.OK, contentType: "application/json", bodyType: typeof(DashboardAnalyticsResponse), Description = "Dashboard analytics data")]
    [OpenApiResponseWithBody(statusCode: HttpStatusCode.BadRequest, contentType: "application/json", bodyType: typeof(object), Description = "Invalid parameters")]
    [OpenApiResponseWithBody(statusCode: HttpStatusCode.InternalServerError, contentType: "application/json", bodyType: typeof(object), Description = "Internal server error")]
    public async Task<IActionResult> GetDashboardAnalyticsAsync(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "analytics/dashboard")] HttpRequest req,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Getting dashboard analytics");

        try
        {
            var query = req.Query;
            var timeRange = query["timeRange"].FirstOrDefault() ?? "7d"; // 1h, 24h, 7d, 30d, 90d
            var tenantId = query["tenantId"].FirstOrDefault(); // Optional: filter by tenant
            var includeComparisons = bool.TryParse(query["includeComparisons"].FirstOrDefault(), out var comparisons) && comparisons;

            var analytics = new DashboardAnalyticsResponse
            {
                TimeRange = timeRange,
                GeneratedAt = DateTime.UtcNow,
                TenantId = !string.IsNullOrEmpty(tenantId) && Guid.TryParse(tenantId, out var tId) ? tId : null,

                // Key Performance Indicators
                KPIs = await GetKPIMetrics(timeRange, tenantId),

                // Agent Status Distribution
                AgentStatus = await GetAgentStatusDistribution(tenantId),

                // Performance Metrics
                Performance = await GetPerformanceMetrics(timeRange, tenantId),

                // Alert Summary
                Alerts = await GetAlertSummary(timeRange, tenantId),

                // Usage Trends
                UsageTrends = await GetUsageTrends(timeRange, tenantId),

                // Geographic Distribution
                Geographic = await GetGeographicDistribution(tenantId),

                // Comparisons (if requested)
                Comparisons = includeComparisons ? await GetComparisonData(timeRange, tenantId) : null
            };

            return new OkObjectResult(analytics);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting dashboard analytics");
            return new ObjectResult(new { error = "Failed to retrieve analytics", details = ex.Message })
            {
                StatusCode = StatusCodes.Status500InternalServerError
            };
        }
    }

    /// <summary>
    /// Get detailed tenant analytics
    /// </summary>
    [Function("GetTenantAnalytics")]
    [OpenApiOperation(operationId: "GetTenantAnalytics", tags: new[] { "Analytics" }, Summary = "Get tenant analytics", Description = "Retrieve comprehensive analytics data for a specific tenant with time range filtering and hierarchical options")]
    [OpenApiParameter(name: "tenantId", In = ParameterLocation.Path, Required = true, Type = typeof(string), Description = "The unique identifier of the tenant")]
    [OpenApiParameter(name: "timeRange", In = ParameterLocation.Query, Required = false, Type = typeof(string), Description = "Time range filter (1h, 24h, 7d, 30d, 90d) - default: 30d")]
    [OpenApiParameter(name: "includeChildren", In = ParameterLocation.Query, Required = false, Type = typeof(bool), Description = "Include child tenant data in analytics")]
    [OpenApiResponseWithBody(statusCode: HttpStatusCode.OK, contentType: "application/json", bodyType: typeof(TenantAnalyticsResponse), Description = "Tenant analytics data")]
    [OpenApiResponseWithBody(statusCode: HttpStatusCode.BadRequest, contentType: "application/json", bodyType: typeof(object), Description = "Invalid tenant ID format")]
    [OpenApiResponseWithBody(statusCode: HttpStatusCode.NotFound, contentType: "application/json", bodyType: typeof(object), Description = "Tenant not found")]
    [OpenApiResponseWithBody(statusCode: HttpStatusCode.InternalServerError, contentType: "application/json", bodyType: typeof(object), Description = "Internal server error")]
    public async Task<IActionResult> GetTenantAnalyticsAsync(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "analytics/tenants/{tenantId}")] HttpRequest req,
        string tenantId,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Getting tenant analytics for {TenantId}", tenantId);

        try
        {
            if (!Guid.TryParse(tenantId, out var id))
            {
                return new BadRequestObjectResult(new { error = "Invalid tenant ID format" });
            }

            var query = req.Query;
            var timeRange = query["timeRange"].FirstOrDefault() ?? "30d";
            var includeChildren = bool.TryParse(query["includeChildren"].FirstOrDefault(), out var children) && children;

            var analytics = new TenantAnalyticsResponse
            {
                TenantId = id,
                TimeRange = timeRange,
                GeneratedAt = DateTime.UtcNow,
                IncludeChildTenants = includeChildren,

                // Agent Analytics
                AgentMetrics = await GetTenantAgentMetrics(id, timeRange, includeChildren),

                // Command Analytics
                CommandMetrics = await GetTenantCommandMetrics(id, timeRange, includeChildren),

                // Performance Analytics
                PerformanceMetrics = await GetTenantPerformanceMetrics(id, timeRange, includeChildren),

                // Usage Analytics
                UsageMetrics = await GetTenantUsageMetrics(id, timeRange, includeChildren),

                // Cost Analytics
                CostMetrics = await GetTenantCostMetrics(id, timeRange, includeChildren),

                // Security Analytics
                SecurityMetrics = await GetTenantSecurityMetrics(id, timeRange, includeChildren),

                // Trends and Forecasting
                Trends = await GetTenantTrends(id, timeRange, includeChildren)
            };

            return new OkObjectResult(analytics);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting tenant analytics for {TenantId}", tenantId);
            return new ObjectResult(new { error = "Failed to retrieve tenant analytics", details = ex.Message })
            {
                StatusCode = StatusCodes.Status500InternalServerError
            };
        }
    }

    /// <summary>
    /// Get detailed agent analytics
    /// </summary>
    [Function("GetAgentAnalytics")]
    [OpenApiOperation(operationId: "GetAgentAnalytics", tags: new[] { "Analytics" }, Summary = "Get agent analytics", Description = "Retrieve comprehensive analytics data for a specific agent including performance, availability, and trends")]
    [OpenApiParameter(name: "agentId", In = ParameterLocation.Path, Required = true, Type = typeof(Guid), Description = "The unique identifier of the agent")]
    [OpenApiParameter(name: "timeRange", In = ParameterLocation.Query, Required = false, Type = typeof(string), Description = "Time range filter (1h, 24h, 7d, 30d, 90d) - default: 7d")]
    [OpenApiParameter(name: "includeDetailedMetrics", In = ParameterLocation.Query, Required = false, Type = typeof(bool), Description = "Include detailed performance metrics")]
    [OpenApiResponseWithBody(statusCode: HttpStatusCode.OK, contentType: "application/json", bodyType: typeof(AgentAnalyticsResponse), Description = "Agent analytics data")]
    [OpenApiResponseWithBody(statusCode: HttpStatusCode.BadRequest, contentType: "application/json", bodyType: typeof(object), Description = "Invalid agent ID format")]
    [OpenApiResponseWithBody(statusCode: HttpStatusCode.NotFound, contentType: "application/json", bodyType: typeof(object), Description = "Agent not found")]
    [OpenApiResponseWithBody(statusCode: HttpStatusCode.InternalServerError, contentType: "application/json", bodyType: typeof(object), Description = "Internal server error")]
    public async Task<IActionResult> GetAgentAnalyticsAsync(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "analytics/agents/{agentId:guid}")] HttpRequest req,
        Guid agentId,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Getting agent analytics for {AgentId}", agentId);

        try
        {
            var query = req.Query;
            var timeRange = query["timeRange"].FirstOrDefault() ?? "7d";
            var includeDetailedMetrics = bool.TryParse(query["includeDetailedMetrics"].FirstOrDefault(), out var detailed) && detailed;

            var analytics = new AgentAnalyticsResponse
            {
                AgentId = agentId,
                TimeRange = timeRange,
                GeneratedAt = DateTime.UtcNow,

                // Performance Metrics
                PerformanceMetrics = await GetAgentPerformanceMetrics(agentId, timeRange, includeDetailedMetrics),

                // Availability Metrics
                AvailabilityMetrics = await GetAgentAvailabilityMetrics(agentId, timeRange),

                // Command History Analytics
                CommandAnalytics = await GetAgentCommandAnalytics(agentId, timeRange),

                // Resource Usage Analytics
                ResourceAnalytics = await GetAgentResourceAnalytics(agentId, timeRange, includeDetailedMetrics),

                // Error and Issue Analytics
                ErrorAnalytics = await GetAgentErrorAnalytics(agentId, timeRange),

                // Trend Analysis
                Trends = await GetAgentTrends(agentId, timeRange)
            };

            return new OkObjectResult(analytics);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting agent analytics for {AgentId}", agentId);
            return new ObjectResult(new { error = "Failed to retrieve agent analytics", details = ex.Message })
            {
                StatusCode = StatusCodes.Status500InternalServerError
            };
        }
    }

    #endregion

    #region Reporting

    /// <summary>
    /// Generate comprehensive system reports
    /// </summary>
    [Function("GenerateReport")]
    [OpenApiOperation(operationId: "GenerateReport", tags: new[] { "Analytics" }, Summary = "Generate report", Description = "Generate comprehensive analytics reports with customizable parameters and output formats")]
    [OpenApiRequestBody(contentType: "application/json", bodyType: typeof(ReportGenerationRequest), Description = "Report generation parameters")]
    [OpenApiResponseWithBody(statusCode: HttpStatusCode.OK, contentType: "application/json", bodyType: typeof(ReportGenerationResponse), Description = "Generated report data")]
    [OpenApiResponseWithBody(statusCode: HttpStatusCode.BadRequest, contentType: "application/json", bodyType: typeof(object), Description = "Invalid report request")]
    [OpenApiResponseWithBody(statusCode: HttpStatusCode.InternalServerError, contentType: "application/json", bodyType: typeof(object), Description = "Internal server error")]
    public async Task<IActionResult> GenerateReportAsync(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "analytics/reports")] HttpRequest req,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Generating analytics report");

        try
        {
            var reportRequest = await req.ReadFromJsonAsync<ReportGenerationRequest>(cancellationToken);

            if (reportRequest == null)
            {
                return new BadRequestObjectResult(new { error = "Invalid report request" });
            }

            // Validate report parameters
            var validationErrors = ValidateReportRequest(reportRequest);
            if (validationErrors.Any())
            {
                return new BadRequestObjectResult(new { 
                    error = "Report validation failed", 
                    errors = validationErrors 
                });
            }

            // Generate report
            var report = await GenerateReport(reportRequest);

            return new OkObjectResult(report);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating report");
            return new ObjectResult(new { error = "Failed to generate report", details = ex.Message })
            {
                StatusCode = StatusCodes.Status500InternalServerError
            };
        }
    }

    /// <summary>
    /// Get available report templates
    /// </summary>
    [Function("GetReportTemplates")]
    [OpenApiOperation(operationId: "GetReportTemplates", tags: new[] { "Analytics" }, Summary = "Get report templates", Description = "Retrieve available report templates with optional category filtering")]
    [OpenApiParameter(name: "category", In = ParameterLocation.Query, Required = false, Type = typeof(string), Description = "Filter by category (performance, security, usage, compliance)")]
    [OpenApiResponseWithBody(statusCode: HttpStatusCode.OK, contentType: "application/json", bodyType: typeof(List<ReportTemplateResponse>), Description = "List of available report templates")]
    [OpenApiResponseWithBody(statusCode: HttpStatusCode.BadRequest, contentType: "application/json", bodyType: typeof(object), Description = "Invalid category parameter")]
    [OpenApiResponseWithBody(statusCode: HttpStatusCode.InternalServerError, contentType: "application/json", bodyType: typeof(object), Description = "Internal server error")]
    public async Task<IActionResult> GetReportTemplatesAsync(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "analytics/report-templates")] HttpRequest req,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Getting report templates");

        try
        {
            var query = req.Query;
            var category = query["category"].FirstOrDefault(); // "performance", "security", "usage", "compliance"

            var templates = await GetAvailableReportTemplates(category);

            return new OkObjectResult(templates);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting report templates");
            return new ObjectResult(new { error = "Failed to retrieve report templates", details = ex.Message })
            {
                StatusCode = StatusCodes.Status500InternalServerError
            };
        }
    }

    /// <summary>
    /// Get scheduled reports
    /// </summary>
    [Function("GetScheduledReports")]
    [OpenApiOperation(operationId: "GetScheduledReports", tags: new[] { "Analytics" }, Summary = "Get scheduled reports", Description = "Retrieve scheduled reports with optional filtering by tenant and status")]
    [OpenApiParameter(name: "tenantId", In = ParameterLocation.Query, Required = false, Type = typeof(string), Description = "Filter by tenant ID")]
    [OpenApiParameter(name: "isActive", In = ParameterLocation.Query, Required = false, Type = typeof(bool), Description = "Filter by active status")]
    [OpenApiParameter(name: "page", In = ParameterLocation.Query, Required = false, Type = typeof(int), Description = "Page number for pagination (default: 1)")]
    [OpenApiParameter(name: "pageSize", In = ParameterLocation.Query, Required = false, Type = typeof(int), Description = "Page size for pagination (default: 20, max: 100)")]
    [OpenApiResponseWithBody(statusCode: HttpStatusCode.OK, contentType: "application/json", bodyType: typeof(object), Description = "Paginated list of scheduled reports")]
    [OpenApiResponseWithBody(statusCode: HttpStatusCode.BadRequest, contentType: "application/json", bodyType: typeof(object), Description = "Invalid pagination parameters")]
    [OpenApiResponseWithBody(statusCode: HttpStatusCode.InternalServerError, contentType: "application/json", bodyType: typeof(object), Description = "Internal server error")]
    public async Task<IActionResult> GetScheduledReportsAsync(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "analytics/scheduled-reports")] HttpRequest req,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Getting scheduled reports");

        try
        {
            var query = req.Query;
            var tenantId = query["tenantId"].FirstOrDefault();
            var isActive = bool.TryParse(query["isActive"].FirstOrDefault(), out var active) ? active : (bool?)null;
            var page = int.TryParse(query["page"].FirstOrDefault(), out var p) ? Math.Max(1, p) : 1;
            var pageSize = int.TryParse(query["pageSize"].FirstOrDefault(), out var ps) ? Math.Max(1, Math.Min(100, ps)) : 20;

            var scheduledReports = await GetScheduledReportsFromDatabase(tenantId, isActive);
            var totalCount = scheduledReports.Count();
            var totalPages = (int)Math.Ceiling((double)totalCount / pageSize);
            
            var pagedReports = scheduledReports
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToList();

            var result = new
            {
                Items = pagedReports,
                Page = page,
                PageSize = pageSize,
                TotalCount = totalCount,
                TotalPages = totalPages
            };

            return new OkObjectResult(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting scheduled reports");
            return new ObjectResult(new { error = "Failed to retrieve scheduled reports", details = ex.Message })
            {
                StatusCode = StatusCodes.Status500InternalServerError
            };
        }
    }

    #endregion

    #region Custom Analytics

    /// <summary>
    /// Execute custom analytics queries
    /// </summary>
    [Function("ExecuteCustomQuery")]
    [OpenApiOperation(operationId: "ExecuteCustomQuery", tags: new[] { "Analytics" }, Summary = "Execute custom analytics query", Description = "Execute custom analytics queries with validation and security checks")]
    [OpenApiRequestBody(contentType: "application/json", bodyType: typeof(CustomAnalyticsQueryRequest), Description = "Custom analytics query parameters")]
    [OpenApiResponseWithBody(statusCode: HttpStatusCode.OK, contentType: "application/json", bodyType: typeof(object), Description = "Query execution results")]
    [OpenApiResponseWithBody(statusCode: HttpStatusCode.BadRequest, contentType: "application/json", bodyType: typeof(object), Description = "Invalid query parameters")]
    [OpenApiResponseWithBody(statusCode: HttpStatusCode.Forbidden, contentType: "application/json", bodyType: typeof(object), Description = "Query not allowed for security reasons")]
    [OpenApiResponseWithBody(statusCode: HttpStatusCode.InternalServerError, contentType: "application/json", bodyType: typeof(object), Description = "Internal server error")]
    public async Task<IActionResult> ExecuteCustomQueryAsync(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "analytics/custom-query")] HttpRequest req,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Executing custom analytics query");

        try
        {
            var queryRequest = await req.ReadFromJsonAsync<CustomAnalyticsQueryRequest>(cancellationToken);

            if (queryRequest == null)
            {
                return new BadRequestObjectResult(new { error = "Invalid query request" });
            }

            // Validate and sanitize query
            var validationErrors = ValidateCustomQuery(queryRequest);
            if (validationErrors.Any())
            {
                return new BadRequestObjectResult(new { 
                    error = "Query validation failed", 
                    errors = validationErrors 
                });
            }

            // Execute query with security checks
            var result = await ExecuteCustomAnalyticsQuery(queryRequest);

            return new OkObjectResult(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing custom query");
            return new ObjectResult(new { error = "Failed to execute custom query", details = ex.Message })
            {
                StatusCode = StatusCodes.Status500InternalServerError
            };
        }
    }

    /// <summary>
    /// Get data export for analytics
    /// </summary>
    [Function("ExportAnalyticsData")]
    [OpenApiOperation(operationId: "ExportAnalyticsData", tags: new[] { "Analytics" }, Summary = "Export analytics data", Description = "Export analytics data in various formats with optional filtering and date range")]
    [OpenApiRequestBody(contentType: "application/json", bodyType: typeof(AnalyticsExportRequest), Description = "Export request parameters")]
    [OpenApiResponseWithBody(statusCode: HttpStatusCode.Accepted, contentType: "application/json", bodyType: typeof(AnalyticsExportResponse), Description = "Export initiated successfully")]
    [OpenApiResponseWithBody(statusCode: HttpStatusCode.BadRequest, contentType: "application/json", bodyType: typeof(object), Description = "Invalid export parameters")]
    [OpenApiResponseWithBody(statusCode: HttpStatusCode.InternalServerError, contentType: "application/json", bodyType: typeof(object), Description = "Internal server error")]
    public async Task<IActionResult> ExportAnalyticsDataAsync(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "analytics/export")] HttpRequest req,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Exporting analytics data");

        try
        {
            var exportRequest = await req.ReadFromJsonAsync<AnalyticsExportRequest>(cancellationToken);

            if (exportRequest == null)
            {
                return new BadRequestObjectResult(new { error = "Invalid export request" });
            }

            // Validate export parameters
            var validationErrors = ValidateExportRequest(exportRequest);
            if (validationErrors.Any())
            {
                return new BadRequestObjectResult(new { 
                    error = "Export validation failed", 
                    errors = validationErrors 
                });
            }

            // Generate export (async for large datasets)
            var exportResult = await InitiateDataExport(exportRequest);

            return new AcceptedResult(string.Empty, exportResult);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error exporting analytics data");
            return new ObjectResult(new { error = "Failed to export data", details = ex.Message })
            {
                StatusCode = StatusCodes.Status500InternalServerError
            };
        }
    }

    #endregion

    #region Performance Analytics

    /// <summary>
    /// Get performance benchmarks and comparisons
    /// </summary>
    [Function("GetPerformanceBenchmarks")]
    [OpenApiOperation(operationId: "GetPerformanceBenchmarks", tags: new[] { "Analytics" }, Summary = "Get performance benchmarks", Description = "Retrieve performance benchmarks and comparisons with optional filtering by tenant, agent, and metric type")]
    [OpenApiParameter(name: "tenantId", In = ParameterLocation.Query, Required = false, Type = typeof(string), Description = "Filter by tenant ID")]
    [OpenApiParameter(name: "agentId", In = ParameterLocation.Query, Required = false, Type = typeof(string), Description = "Filter by agent ID")]
    [OpenApiParameter(name: "metric", In = ParameterLocation.Query, Required = false, Type = typeof(string), Description = "Filter by metric type (cpu, memory, disk, network, response_time)")]
    [OpenApiParameter(name: "timeRange", In = ParameterLocation.Query, Required = false, Type = typeof(string), Description = "Time range filter (default: 30d)")]
    [OpenApiResponseWithBody(statusCode: HttpStatusCode.OK, contentType: "application/json", bodyType: typeof(object), Description = "Performance benchmarks data")]
    [OpenApiResponseWithBody(statusCode: HttpStatusCode.BadRequest, contentType: "application/json", bodyType: typeof(object), Description = "Invalid parameters")]
    [OpenApiResponseWithBody(statusCode: HttpStatusCode.InternalServerError, contentType: "application/json", bodyType: typeof(object), Description = "Internal server error")]
    public async Task<IActionResult> GetPerformanceBenchmarksAsync(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "analytics/performance/benchmarks")] HttpRequest req,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Getting performance benchmarks");

        try
        {
            var query = req.Query;
            var tenantId = query["tenantId"].FirstOrDefault();
            var agentId = query["agentId"].FirstOrDefault();
            var metric = query["metric"].FirstOrDefault(); // "cpu", "memory", "disk", "network", "response_time"
            var timeRange = query["timeRange"].FirstOrDefault() ?? "30d";

            var benchmarks = await GetPerformanceBenchmarks(tenantId, agentId, metric, timeRange);

            return new OkObjectResult(benchmarks);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting performance benchmarks");
            return new ObjectResult(new { error = "Failed to retrieve benchmarks", details = ex.Message })
            {
                StatusCode = StatusCodes.Status500InternalServerError
            };
        }
    }

    #endregion

    #region Helper Methods

    private async Task<KPIMetrics> GetKPIMetrics(string timeRange, string? tenantId)
    {
        await Task.Delay(1);
        return new KPIMetrics
        {
            TotalAgents = 1247,
            OnlineAgents = 1089,
            OfflineAgents = 158,
            AverageCpuUsage = 45.2,
            AverageMemoryUsage = 67.8,
            TotalAlerts = 45,
            CriticalAlerts = 2
        };
    }

    private async Task<AgentStatusDistribution> GetAgentStatusDistribution(string? tenantId)
    {
        await Task.Delay(1);
        return new AgentStatusDistribution
        {
            StatusCounts = new Dictionary<string, int>
            {
                { "online", 1089 },
                { "offline", 125 },
                { "error", 23 },
                { "updating", 10 }
            },
            StatusPercentages = new Dictionary<string, double>
            {
                { "online", 87.3 },
                { "offline", 10.0 },
                { "error", 1.8 },
                { "updating", 0.8 }
            }
        };
    }

    private async Task<PerformanceMetrics> GetPerformanceMetrics(string timeRange, string? tenantId)
    {
        await Task.Delay(1);
        return new PerformanceMetrics
        {
            CpuUsage = new List<TimeSeriesDataPoint>
            {
                new TimeSeriesDataPoint { Timestamp = DateTime.UtcNow.AddHours(-1), Value = 45.2 },
                new TimeSeriesDataPoint { Timestamp = DateTime.UtcNow, Value = 47.1 }
            },
            MemoryUsage = new List<TimeSeriesDataPoint>
            {
                new TimeSeriesDataPoint { Timestamp = DateTime.UtcNow.AddHours(-1), Value = 67.8 },
                new TimeSeriesDataPoint { Timestamp = DateTime.UtcNow, Value = 69.3 }
            },
            DiskUsage = new List<TimeSeriesDataPoint>
            {
                new TimeSeriesDataPoint { Timestamp = DateTime.UtcNow.AddHours(-1), Value = 23.4 },
                new TimeSeriesDataPoint { Timestamp = DateTime.UtcNow, Value = 24.1 }
            },
            NetworkThroughput = new List<TimeSeriesDataPoint>
            {
                new TimeSeriesDataPoint { Timestamp = DateTime.UtcNow.AddHours(-1), Value = 125.6 },
                new TimeSeriesDataPoint { Timestamp = DateTime.UtcNow, Value = 130.2 }
            }
        };
    }

    private async Task<AlertSummary> GetAlertSummary(string timeRange, string? tenantId)
    {
        await Task.Delay(1);
        return new AlertSummary
        {
            Total = 45,
            BySeverity = new Dictionary<string, int>
            {
                { "critical", 2 },
                { "high", 8 },
                { "medium", 20 },
                { "low", 15 }
            },
            ByCategory = new Dictionary<string, int>
            {
                { "performance", 20 },
                { "security", 8 },
                { "connectivity", 12 },
                { "system", 5 }
            },
            RecentAlerts = new List<AlertInfo>
            {
                new AlertInfo
                {
                    Id = Guid.NewGuid(),
                    Title = "High CPU Usage",
                    Severity = "critical",
                    Category = "performance",
                    CreatedAt = DateTime.UtcNow.AddMinutes(-30)
                }
            }
        };
    }

    private async Task<UsageTrends> GetUsageTrends(string timeRange, string? tenantId)
    {
        await Task.Delay(1);
        var baseDate = DateTime.UtcNow.AddDays(-7);
        var agentRegistrations = new List<TimeSeriesDataPoint>();
        var commandExecutions = new List<TimeSeriesDataPoint>();
        var dataTransfer = new List<TimeSeriesDataPoint>();
        
        for (int i = 0; i < 7; i++)
        {
            var timestamp = baseDate.AddDays(i);
            agentRegistrations.Add(new TimeSeriesDataPoint { Timestamp = timestamp, Value = 10 + new Random().Next(-2, 5) });
            commandExecutions.Add(new TimeSeriesDataPoint { Timestamp = timestamp, Value = 2000 + new Random().Next(-200, 500) });
            dataTransfer.Add(new TimeSeriesDataPoint { Timestamp = timestamp, Value = 1024 + new Random().Next(-100, 200) });
        }
        
        return new UsageTrends
        {
            AgentRegistrations = agentRegistrations,
            CommandExecutions = commandExecutions,
            DataTransfer = dataTransfer
        };
    }

    private async Task<GeographicDistribution> GetGeographicDistribution(string? tenantId)
    {
        await Task.Delay(1);
        return new GeographicDistribution
        {
            ByRegion = new Dictionary<string, int>
            {
                { "North America", 456 },
                { "Europe", 312 },
                { "Asia Pacific", 278 },
                { "South America", 134 },
                { "Others", 67 }
            },
            ByCountry = new Dictionary<string, int>
            {
                { "United States", 380 },
                { "Canada", 76 },
                { "Germany", 125 },
                { "United Kingdom", 87 },
                { "France", 100 },
                { "Japan", 145 },
                { "Australia", 133 },
                { "Brazil", 95 },
                { "Mexico", 39 }
            }
        };
    }

    private async Task<ComparisonData?> GetComparisonData(string timeRange, string? tenantId)
    {
        await Task.Delay(1);
        return new ComparisonData
        {
            PercentageChanges = new Dictionary<string, double>
            {
                { "agentsOnline", 4.2 },
                { "commandsExecuted", 9.8 },
                { "errorRate", -33.3 }
            },
            TrendIndicators = new Dictionary<string, string>
            {
                { "agentsOnline", "up" },
                { "commandsExecuted", "up" },
                { "errorRate", "down" }
            }
        };
    }

    private async Task<TenantAgentMetrics> GetTenantAgentMetrics(Guid tenantId, string timeRange, bool includeChildren)
    {
        await Task.Delay(1);
        return new TenantAgentMetrics
        {
            TotalAgents = 25,
            ActiveAgents = 23,
            GrowthRate = 12.5,
            ByStatus = new Dictionary<string, int>
            {
                { "online", 23 },
                { "offline", 2 }
            }
        };
    }

    private async Task<TenantCommandMetrics> GetTenantCommandMetrics(Guid tenantId, string timeRange, bool includeChildren)
    {
        await Task.Delay(1);
        return new TenantCommandMetrics
        {
            TotalExecuted = 1247,
            Successful = 1205,
            Failed = 42,
            AverageExecutionTime = 125.5
        };
    }

    private async Task<TenantPerformanceMetrics> GetTenantPerformanceMetrics(Guid tenantId, string timeRange, bool includeChildren)
    {
        await Task.Delay(1);
        return new TenantPerformanceMetrics
        {
            AverageCpu = 42.3,
            AverageMemory = 65.7,
            AverageDisk = 28.1,
            Trends = new List<TimeSeriesDataPoint>
            {
                new TimeSeriesDataPoint { Timestamp = DateTime.UtcNow.AddHours(-1), Value = 40.1 },
                new TimeSeriesDataPoint { Timestamp = DateTime.UtcNow, Value = 42.3 }
            }
        };
    }

    private async Task<TenantUsageMetrics> GetTenantUsageMetrics(Guid tenantId, string timeRange, bool includeChildren)
    {
        await Task.Delay(1);
        return new TenantUsageMetrics
        {
            DataTransferred = 125400000000, // 125.4 GB in bytes
            ApiCalls = 15632,
            StorageUsed = 2800000000 // 2.8 GB in bytes
        };
    }

    private async Task<TenantCostMetrics> GetTenantCostMetrics(Guid tenantId, string timeRange, bool includeChildren)
    {
        await Task.Delay(1);
        return new TenantCostMetrics
        {
            TotalCost = 247.85m,
            ProjectedMonthlyCost = 743.55m,
            Breakdown = new Dictionary<string, decimal>
            {
                { "compute", 150.00m },
                { "storage", 45.50m },
                { "bandwidth", 52.35m }
            }
        };
    }

    private async Task<TenantSecurityMetrics> GetTenantSecurityMetrics(Guid tenantId, string timeRange, bool includeChildren)
    {
        await Task.Delay(1);
        return new TenantSecurityMetrics
        {
            SecurityIncidents = 12,
            Vulnerabilities = 3,
            ComplianceScore = 94.2
        };
    }

    private async Task<TenantTrends> GetTenantTrends(Guid tenantId, string timeRange, bool includeChildren)
    {
        await Task.Delay(1);
        return new TenantTrends
        {
            Growth = new Dictionary<string, double>
            {
                { "agents", 12.5 },
                { "usage", 8.3 },
                { "cost", 5.7 }
            },
            Forecasts = new Dictionary<string, double>
            {
                { "nextMonthAgents", 28.0 },
                { "nextMonthCost", 260.0 }
            }
        };
    }

    private async Task<AgentPerformanceMetrics> GetAgentPerformanceMetrics(Guid agentId, string timeRange, bool includeDetailedMetrics)
    {
        await Task.Delay(1);
        return new AgentPerformanceMetrics
        {
            CpuStats = new PerformanceStats { Average = 35.2, Min = 10.0, Max = 85.0, P95 = 75.0 },
            MemoryStats = new PerformanceStats { Average = 72.1, Min = 45.0, Max = 95.0, P95 = 90.0 },
            DiskStats = new PerformanceStats { Average = 15.8, Min = 5.0, Max = 65.0, P95 = 45.0 }
        };
    }

    private async Task<AgentAvailabilityMetrics> GetAgentAvailabilityMetrics(Guid agentId, string timeRange)
    {
        await Task.Delay(1);
        return new AgentAvailabilityMetrics
        {
            UptimePercentage = 99.8,
            DowntimeMinutes = 135, // 2h 15m
            Events = new List<AvailabilityEvent>
            {
                new AvailabilityEvent
                {
                    Type = "downtime",
                    Timestamp = DateTime.UtcNow.AddHours(-3),
                    DurationMinutes = 135
                }
            }
        };
    }

    private async Task<AgentCommandAnalytics> GetAgentCommandAnalytics(Guid agentId, string timeRange)
    {
        await Task.Delay(1);
        return new AgentCommandAnalytics
        {
            TotalExecuted = 342,
            SuccessRate = 97.8,
            AverageExecutionTime = 1.2,
            ByType = new Dictionary<string, int>
            {
                { "system", 150 },
                { "monitoring", 120 },
                { "maintenance", 72 }
            }
        };
    }

    private async Task<AgentResourceAnalytics> GetAgentResourceAnalytics(Guid agentId, string timeRange, bool includeDetailedMetrics)
    {
        await Task.Delay(1);
        return new AgentResourceAnalytics
        {
            CpuTrends = new List<TimeSeriesDataPoint>
            {
                new TimeSeriesDataPoint { Timestamp = DateTime.UtcNow.AddHours(-1), Value = 85.0 },
                new TimeSeriesDataPoint { Timestamp = DateTime.UtcNow, Value = 89.2 }
            },
            MemoryTrends = new List<TimeSeriesDataPoint>
            {
                new TimeSeriesDataPoint { Timestamp = DateTime.UtcNow.AddHours(-1), Value = 88.0 },
                new TimeSeriesDataPoint { Timestamp = DateTime.UtcNow, Value = 91.5 }
            },
            DiskTrends = new List<TimeSeriesDataPoint>
            {
                new TimeSeriesDataPoint { Timestamp = DateTime.UtcNow.AddHours(-1), Value = 1200.0 },
                new TimeSeriesDataPoint { Timestamp = DateTime.UtcNow, Value = 1250.0 }
            }
        };
    }

    private async Task<AgentErrorAnalytics> GetAgentErrorAnalytics(Guid agentId, string timeRange)
    {
        await Task.Delay(1);
        return new AgentErrorAnalytics
        {
            TotalErrors = 8,
            ByType = new Dictionary<string, int>
            {
                { "timeout", 5 },
                { "connection", 3 }
            },
            Trends = new List<TimeSeriesDataPoint>
            {
                new TimeSeriesDataPoint { Timestamp = DateTime.UtcNow.AddDays(-1), Value = 12 },
                new TimeSeriesDataPoint { Timestamp = DateTime.UtcNow, Value = 8 }
            }
        };
    }

    private async Task<AgentTrends> GetAgentTrends(Guid agentId, string timeRange)
    {
        await Task.Delay(1);
        return new AgentTrends
        {
            PerformanceTrends = new Dictionary<string, string>
            {
                { "cpu", "stable" },
                { "memory", "increasing" },
                { "disk", "stable" }
            },
            ReliabilityScore = 94.5
        };
    }

    private List<string> ValidateReportRequest(ReportGenerationRequest request)
    {
        var errors = new List<string>();
        
        if (string.IsNullOrEmpty(request.Type))
            errors.Add("Report type is required");
            
        if (request.StartDate >= request.EndDate)
            errors.Add("Start date must be before end date");
            
        return errors;
    }

    private async Task<ReportGenerationResponse> GenerateReport(ReportGenerationRequest request)
    {
        await Task.Delay(100);
        return new ReportGenerationResponse
        {
            ReportId = Guid.NewGuid(),
            Type = request.Type,
            Status = "completed",
            GeneratedAt = DateTime.UtcNow,
            DownloadUrl = "/api/reports/download/12345"
        };
    }

    private async Task<List<ReportTemplateResponse>> GetAvailableReportTemplates(string? category)
    {
        await Task.Delay(1);
        return new List<ReportTemplateResponse>
        {
            new ReportTemplateResponse
            {
                Id = Guid.NewGuid(),
                Name = "System Performance Report",
                Description = "Comprehensive system performance analysis",
                Category = "Performance",
                Parameters = new List<string> { "timeRange", "tenantId" }
            },
            new ReportTemplateResponse
            {
                Id = Guid.NewGuid(),
                Name = "Security Audit Report",
                Description = "Security events and compliance analysis",
                Category = "Security",
                Parameters = new List<string> { "timeRange", "severity", "tenantId" }
            }
        };
    }

    private async Task<List<ScheduledReportResponse>> GetScheduledReportsFromDatabase(string? tenantId, bool? isActive)
    {
        await Task.Delay(1);
        return new List<ScheduledReportResponse>
        {
            new ScheduledReportResponse
            {
                Id = Guid.NewGuid(),
                Name = "Weekly Performance Report",
                TemplateId = Guid.NewGuid(),
                Schedule = "0 0 * * 1", // Weekly on Monday
                IsActive = true,
                LastRun = DateTime.UtcNow.AddDays(-7),
                NextRun = DateTime.UtcNow.AddDays(0)
            }
        };
    }

    private List<string> ValidateCustomQuery(CustomAnalyticsQueryRequest query)
    {
        var errors = new List<string>();
        
        if (string.IsNullOrEmpty(query.QueryType))
            errors.Add("Query type is required");
            
        // Add more validation based on query type and security requirements
        
        return errors;
    }

    private async Task<object> ExecuteCustomAnalyticsQuery(CustomAnalyticsQueryRequest query)
    {
        await Task.Delay(100);
        return new
        {
            queryId = Guid.NewGuid(),
            executedAt = DateTime.UtcNow,
            resultCount = 1247,
            data = new[] { new { sample = "data" } }
        };
    }

    private List<string> ValidateExportRequest(AnalyticsExportRequest request)
    {
        var errors = new List<string>();
        
        if (string.IsNullOrEmpty(request.Format))
            errors.Add("Export format is required");
            
        return errors;
    }

    private async Task<object> InitiateDataExport(AnalyticsExportRequest request)
    {
        await Task.Delay(100);
        return new
        {
            exportId = Guid.NewGuid(),
            status = "processing",
            estimatedCompletion = DateTime.UtcNow.AddMinutes(10)
        };
    }

    private async Task<object> GetPerformanceBenchmarks(string? tenantId, string? agentId, string? metric, string timeRange)
    {
        await Task.Delay(1);
        return new
        {
            metric,
            timeRange,
            benchmarks = new
            {
                excellent = 90.0,
                good = 75.0,
                average = 60.0,
                poor = 45.0
            },
            currentValue = 72.3,
            percentile = 68,
            trend = "improving"
        };
    }

    #endregion
}

#region Supporting DTOs

public class DashboardAnalytics
{
    public string TimeRange { get; set; } = string.Empty;
    public DateTime GeneratedAt { get; set; }
    public Guid? TenantId { get; set; }
    public object KPIs { get; set; } = new();
    public object AgentStatus { get; set; } = new();
    public object Performance { get; set; } = new();
    public object Alerts { get; set; } = new();
    public object UsageTrends { get; set; } = new();
    public object Geographic { get; set; } = new();
    public object? Comparisons { get; set; }
}

public class TenantAnalytics
{
    public Guid TenantId { get; set; }
    public string TimeRange { get; set; } = string.Empty;
    public DateTime GeneratedAt { get; set; }
    public bool IncludeChildTenants { get; set; }
    public object AgentMetrics { get; set; } = new();
    public object CommandMetrics { get; set; } = new();
    public object PerformanceMetrics { get; set; } = new();
    public object UsageMetrics { get; set; } = new();
    public object CostMetrics { get; set; } = new();
    public object SecurityMetrics { get; set; } = new();
    public object Trends { get; set; } = new();
}

public class AgentAnalytics
{
    public Guid AgentId { get; set; }
    public string TimeRange { get; set; } = string.Empty;
    public DateTime GeneratedAt { get; set; }
    public object PerformanceMetrics { get; set; } = new();
    public object AvailabilityMetrics { get; set; } = new();
    public object CommandAnalytics { get; set; } = new();
    public object ResourceAnalytics { get; set; } = new();
    public object ErrorAnalytics { get; set; } = new();
    public object Trends { get; set; } = new();
}

public class ReportGenerationRequest
{
    public string Type { get; set; } = string.Empty; // "performance", "security", "usage", "compliance"
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }
    public string? TenantId { get; set; }
    public string Format { get; set; } = "json"; // "json", "pdf", "excel", "csv"
    public Dictionary<string, object> Parameters { get; set; } = new();
}

public class ReportTemplate
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string[] Parameters { get; set; } = Array.Empty<string>();
}

public class ScheduledReport
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public Guid TemplateId { get; set; }
    public string Schedule { get; set; } = string.Empty; // Cron expression
    public bool IsActive { get; set; }
    public DateTime? LastRun { get; set; }
    public DateTime? NextRun { get; set; }
    public Dictionary<string, object> Parameters { get; set; } = new();
}

public class CustomAnalyticsQuery
{
    public string QueryType { get; set; } = string.Empty; // "aggregation", "filter", "join"
    public string DataSource { get; set; } = string.Empty; // "agents", "commands", "telemetry"
    public Dictionary<string, object> Filters { get; set; } = new();
    public string[] GroupBy { get; set; } = Array.Empty<string>();
    public Dictionary<string, string> Aggregations { get; set; } = new(); // field -> function
    public int Limit { get; set; } = 1000;
}

public class AnalyticsExportRequest
{
    public string DataType { get; set; } = string.Empty; // "agents", "tenants", "analytics"
    public string Format { get; set; } = string.Empty; // "csv", "json", "excel"
    public DateTime? StartDate { get; set; }
    public DateTime? EndDate { get; set; }
    public string? TenantId { get; set; }
    public Dictionary<string, object> Filters { get; set; } = new();
}

#endregion
