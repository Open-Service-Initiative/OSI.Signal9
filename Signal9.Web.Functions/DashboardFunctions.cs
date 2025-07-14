using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Text.Json;

namespace Signal9.Web.Functions;

/// <summary>
/// Legacy dashboard functions - most functionality has been moved to specialized function classes
/// This class now serves as a compatibility layer and houses general dashboard endpoints
/// </summary>
public class DashboardFunctions
{
    private readonly ILogger<DashboardFunctions> _logger;

    public DashboardFunctions(ILogger<DashboardFunctions> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Get dashboard overview data
    /// </summary>
    [Function("GetDashboardOverview")]
    public async Task<HttpResponseData> GetDashboardOverviewAsync(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "dashboard/overview")] HttpRequestData req)
    {
        _logger.LogInformation("Getting dashboard overview");

        try
        {
            var overview = new
            {
                TotalTenants = 15,
                ActiveTenants = 13,
                TotalAgents = 247,
                OnlineAgents = 201,
                OfflineAgents = 46,
                AlertsCount = 8,
                RecentActivity = new[]
                {
                    new { Time = DateTime.UtcNow.AddMinutes(-5), Event = "Agent registered", Description = "New agent DEV-WS-03 registered" },
                    new { Time = DateTime.UtcNow.AddMinutes(-12), Event = "Tenant created", Description = "New tenant 'Marketing Dept' created" },
                    new { Time = DateTime.UtcNow.AddMinutes(-18), Event = "Alert resolved", Description = "High CPU usage alert resolved for SRV-01" }
                }
            };

            var response = req.CreateResponse(HttpStatusCode.OK);
            response.Headers.Add("Content-Type", "application/json");
            await response.WriteStringAsync(JsonSerializer.Serialize(overview, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            }));

            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting dashboard overview");
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteStringAsync("Failed to retrieve dashboard overview");
            return errorResponse;
        }
    }

    /// <summary>
    /// Get dashboard statistics
    /// </summary>
    [Function("GetDashboardStats")]
    public async Task<HttpResponseData> GetDashboardStatsAsync(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "dashboard/stats")] HttpRequestData req)
    {
        _logger.LogInformation("Getting dashboard statistics");

        try
        {
            var stats = new
            {
                AgentsByStatus = new
                {
                    Online = 201,
                    Offline = 46,
                    Error = 3
                },
                TenantsByPlan = new
                {
                    Basic = 8,
                    Professional = 5,
                    Enterprise = 2
                },
                SystemPerformance = new
                {
                    AverageResponseTime = 145,
                    UptimePercentage = 99.8,
                    ThroughputPerSecond = 1250
                }
            };

            var response = req.CreateResponse(HttpStatusCode.OK);
            response.Headers.Add("Content-Type", "application/json");
            await response.WriteStringAsync(JsonSerializer.Serialize(stats, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            }));

            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting dashboard statistics");
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteStringAsync("Failed to retrieve dashboard statistics");
            return errorResponse;
        }
    }

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
