using System.ComponentModel.DataAnnotations;
using Signal9.Shared.DTOs.Base;

namespace Signal9.Shared.DTOs.Analytics;

/// <summary>
/// Dashboard analytics response containing comprehensive system metrics.
/// </summary>
public record DashboardAnalyticsResponse : BaseDto<Guid>
{
    /// <summary>
    /// Gets the time range for the analytics data (e.g., "7d", "30d").
    /// Must be a valid time range identifier.
    /// </summary>
    [Required(ErrorMessage = "TimeRange is required")]
    [StringLength(20, MinimumLength = 1, ErrorMessage = "TimeRange must be between 1 and 20 characters")]
    [RegularExpression(@"^(1h|6h|12h|24h|7d|30d|90d|1y)$", ErrorMessage = "TimeRange must be one of: 1h, 6h, 12h, 24h, 7d, 30d, 90d, 1y")]
    public required string TimeRange { get; init; }
    
    /// <summary>
    /// Gets when this analytics data was generated.
    /// </summary>
    public DateTime GeneratedAt { get; init; } = DateTime.UtcNow;
    
    /// <summary>
    /// Gets the tenant ID if filtered by tenant.
    /// Must be a valid GUID if specified.
    /// </summary>
    public Guid? TenantId { get; init; }
    
    /// <summary>
    /// Gets the key performance indicators.
    /// Must contain valid KPI metrics data.
    /// </summary>
    [Required(ErrorMessage = "KPIs are required")]
    public required KPIMetrics KPIs { get; init; }
    
    /// <summary>
    /// Gets the agent status distribution.
    /// Must contain valid agent status information.
    /// </summary>
    [Required(ErrorMessage = "AgentStatus is required")]
    public required AgentStatusDistribution AgentStatus { get; init; }
    
    /// <summary>
    /// Gets the performance metrics.
    /// Must contain valid performance data.
    /// </summary>
    [Required(ErrorMessage = "Performance metrics are required")]
    public required PerformanceMetrics Performance { get; init; }
    
    /// <summary>
    /// Gets the alert summary.
    /// Must contain valid alert summary data.
    /// </summary>
    [Required(ErrorMessage = "Alerts summary is required")]
    public required AlertSummary Alerts { get; init; }
    
    /// <summary>
    /// Gets the usage trends.
    /// Must contain valid usage trend data.
    /// </summary>
    [Required(ErrorMessage = "UsageTrends are required")]
    public required UsageTrends UsageTrends { get; init; }
    
    /// <summary>
    /// Gets the geographic distribution.
    /// Must contain valid geographic distribution data.
    /// </summary>
    [Required(ErrorMessage = "Geographic distribution is required")]
    public required GeographicDistribution Geographic { get; init; }
    
    /// <summary>
    /// Gets the comparison data if requested.
    /// Optional comparison metrics for trend analysis.
    /// </summary>
    public ComparisonData? Comparisons { get; init; }
}

/// <summary>
/// Key performance indicators for the dashboard.
/// </summary>
public record KPIMetrics
{
    /// <summary>
    /// Gets the total number of active agents.
    /// Cannot be negative.
    /// </summary>
    [Range(0, int.MaxValue, ErrorMessage = "TotalAgents cannot be negative")]
    public int TotalAgents { get; init; }
    
    /// <summary>
    /// Gets the number of online agents.
    /// Cannot be negative and should not exceed total agents.
    /// </summary>
    [Range(0, int.MaxValue, ErrorMessage = "OnlineAgents cannot be negative")]
    public int OnlineAgents { get; init; }
    
    /// <summary>
    /// Gets the number of offline agents.
    /// Cannot be negative and should not exceed total agents.
    /// </summary>
    [Range(0, int.MaxValue, ErrorMessage = "OfflineAgents cannot be negative")]
    public int OfflineAgents { get; init; }
    
    /// <summary>
    /// Gets the average CPU usage percentage.
    /// Must be between 0 and 100.
    /// </summary>
    [Range(0.0, 100.0, ErrorMessage = "AverageCpuUsage must be between 0 and 100")]
    public double AverageCpuUsage { get; init; }
    
    /// <summary>
    /// Gets the average memory usage percentage.
    /// Must be between 0 and 100.
    /// </summary>
    [Range(0.0, 100.0, ErrorMessage = "AverageMemoryUsage must be between 0 and 100")]
    public double AverageMemoryUsage { get; init; }
    
    /// <summary>
    /// Gets the total number of alerts.
    /// Cannot be negative.
    /// </summary>
    [Range(0, int.MaxValue, ErrorMessage = "TotalAlerts cannot be negative")]
    public int TotalAlerts { get; init; }
    
    /// <summary>
    /// Gets the number of critical alerts.
    /// Cannot be negative and should not exceed total alerts.
    /// </summary>
    [Range(0, int.MaxValue, ErrorMessage = "CriticalAlerts cannot be negative")]
    public int CriticalAlerts { get; init; }
}

/// <summary>
/// Agent status distribution data.
/// </summary>
public record AgentStatusDistribution
{
    /// <summary>
    /// Gets the count by status.
    /// </summary>
    public Dictionary<string, int> StatusCounts { get; init; } = new();
    
    /// <summary>
    /// Gets the percentage by status.
    /// </summary>
    public Dictionary<string, double> StatusPercentages { get; init; } = new();
}

/// <summary>
/// Performance metrics data.
/// </summary>
public record PerformanceMetrics
{
    /// <summary>
    /// Gets the CPU usage time series data.
    /// </summary>
    public List<TimeSeriesDataPoint> CpuUsage { get; init; } = new();
    
    /// <summary>
    /// Gets the memory usage time series data.
    /// </summary>
    public List<TimeSeriesDataPoint> MemoryUsage { get; init; } = new();
    
    /// <summary>
    /// Gets the disk usage time series data.
    /// </summary>
    public List<TimeSeriesDataPoint> DiskUsage { get; init; } = new();
    
    /// <summary>
    /// Gets the network throughput time series data.
    /// </summary>
    public List<TimeSeriesDataPoint> NetworkThroughput { get; init; } = new();
}

/// <summary>
/// Alert summary data.
/// </summary>
public record AlertSummary
{
    /// <summary>
    /// Gets the total number of alerts.
    /// </summary>
    public int Total { get; init; }
    
    /// <summary>
    /// Gets the alerts by severity.
    /// </summary>
    public Dictionary<string, int> BySeverity { get; init; } = new();
    
    /// <summary>
    /// Gets the alerts by category.
    /// </summary>
    public Dictionary<string, int> ByCategory { get; init; } = new();
    
    /// <summary>
    /// Gets the recent alerts.
    /// </summary>
    public List<AlertInfo> RecentAlerts { get; init; } = new();
}

/// <summary>
/// Usage trends data.
/// </summary>
public record UsageTrends
{
    /// <summary>
    /// Gets the agent registration trends.
    /// </summary>
    public List<TimeSeriesDataPoint> AgentRegistrations { get; init; } = new();
    
    /// <summary>
    /// Gets the command execution trends.
    /// </summary>
    public List<TimeSeriesDataPoint> CommandExecutions { get; init; } = new();
    
    /// <summary>
    /// Gets the data transfer trends.
    /// </summary>
    public List<TimeSeriesDataPoint> DataTransfer { get; init; } = new();
}

/// <summary>
/// Geographic distribution data.
/// </summary>
public record GeographicDistribution
{
    /// <summary>
    /// Gets the agents by region.
    /// </summary>
    public Dictionary<string, int> ByRegion { get; init; } = new();
    
    /// <summary>
    /// Gets the agents by country.
    /// </summary>
    public Dictionary<string, int> ByCountry { get; init; } = new();
}

/// <summary>
/// Comparison data for trend analysis.
/// </summary>
public record ComparisonData
{
    /// <summary>
    /// Gets the percentage change from previous period.
    /// </summary>
    public Dictionary<string, double> PercentageChanges { get; init; } = new();
    
    /// <summary>
    /// Gets the trend indicators.
    /// </summary>
    public Dictionary<string, string> TrendIndicators { get; init; } = new();
}

/// <summary>
/// Time series data point.
/// </summary>
public record TimeSeriesDataPoint
{
    /// <summary>
    /// Gets the timestamp.
    /// Must be a valid date and time.
    /// </summary>
    [Required(ErrorMessage = "Timestamp is required")]
    public required DateTime Timestamp { get; init; }
    
    /// <summary>
    /// Gets the value.
    /// Must be a finite number (not NaN or infinity).
    /// </summary>
    [Required(ErrorMessage = "Value is required")]
    public required double Value { get; init; }
    
    /// <summary>
    /// Gets additional metadata.
    /// Optional supplementary data for the time series point.
    /// </summary>
    public Dictionary<string, object>? Metadata { get; init; }
}

/// <summary>
/// Alert information.
/// </summary>
public record AlertInfo
{
    /// <summary>
    /// Gets the alert ID.
    /// </summary>
    
    
    /// <summary>
    /// Gets the alert title.
    /// </summary>
    public required string Title { get; init; }
    
    /// <summary>
    /// Gets the alert severity.
    /// </summary>
    public required string Severity { get; init; }
    
    /// <summary>
    /// Gets the alert category.
    /// </summary>
    public required string Category { get; init; }
    
    /// <summary>
    /// Gets when the alert was created.
    /// </summary>
    }

/// <summary>
/// Tenant analytics response containing detailed metrics for a specific tenant.
/// Uses unified hierarchy where ParentId represents the tenant this analytics data belongs to.
/// </summary>
public record TenantAnalyticsResponse : BaseDto<Guid>
{
    /// <summary>
    /// Gets the time range for the analytics data.
    /// </summary>
    public required string TimeRange { get; init; }
    
    /// <summary>
    /// Gets when this analytics data was generated.
    /// </summary>
    public DateTime GeneratedAt { get; init; } = DateTime.UtcNow;
    
    /// <summary>
    /// Gets whether child tenants are included.
    /// </summary>
    public bool IncludeChildTenants { get; init; }
    
    /// <summary>
    /// Gets the agent metrics.
    /// </summary>
    public required TenantAgentMetrics AgentMetrics { get; init; }
    
    /// <summary>
    /// Gets the command metrics.
    /// </summary>
    public required TenantCommandMetrics CommandMetrics { get; init; }
    
    /// <summary>
    /// Gets the performance metrics.
    /// </summary>
    public required TenantPerformanceMetrics PerformanceMetrics { get; init; }
    
    /// <summary>
    /// Gets the usage metrics.
    /// </summary>
    public required TenantUsageMetrics UsageMetrics { get; init; }
    
    /// <summary>
    /// Gets the cost metrics.
    /// </summary>
    public required TenantCostMetrics CostMetrics { get; init; }
    
    /// <summary>
    /// Gets the security metrics.
    /// </summary>
    public required TenantSecurityMetrics SecurityMetrics { get; init; }
    
    /// <summary>
    /// Gets the trend analysis.
    /// </summary>
    public required TenantTrends Trends { get; init; }
}

/// <summary>
/// Tenant agent metrics.
/// </summary>
public record TenantAgentMetrics
{
    /// <summary>
    /// Gets the total number of agents.
    /// </summary>
    public int TotalAgents { get; init; }
    
    /// <summary>
    /// Gets the active agents count.
    /// </summary>
    public int ActiveAgents { get; init; }
    
    /// <summary>
    /// Gets the agent growth rate.
    /// </summary>
    public double GrowthRate { get; init; }
    
    /// <summary>
    /// Gets the agents by status.
    /// </summary>
    public Dictionary<string, int> ByStatus { get; init; } = new();
}

/// <summary>
/// Tenant command metrics.
/// </summary>
public record TenantCommandMetrics
{
    /// <summary>
    /// Gets the total commands executed.
    /// </summary>
    public long TotalExecuted { get; init; }
    
    /// <summary>
    /// Gets the successful commands count.
    /// </summary>
    public long Successful { get; init; }
    
    /// <summary>
    /// Gets the failed commands count.
    /// </summary>
    public long Failed { get; init; }
    
    /// <summary>
    /// Gets the average execution time in milliseconds.
    /// </summary>
    public double AverageExecutionTime { get; init; }
}

/// <summary>
/// Tenant performance metrics.
/// </summary>
public record TenantPerformanceMetrics
{
    /// <summary>
    /// Gets the average CPU usage.
    /// </summary>
    public double AverageCpu { get; init; }
    
    /// <summary>
    /// Gets the average memory usage.
    /// </summary>
    public double AverageMemory { get; init; }
    
    /// <summary>
    /// Gets the average disk usage.
    /// </summary>
    public double AverageDisk { get; init; }
    
    /// <summary>
    /// Gets the performance trends.
    /// </summary>
    public List<TimeSeriesDataPoint> Trends { get; init; } = new();
}

/// <summary>
/// Tenant usage metrics.
/// </summary>
public record TenantUsageMetrics
{
    /// <summary>
    /// Gets the data transferred in bytes.
    /// </summary>
    public long DataTransferred { get; init; }
    
    /// <summary>
    /// Gets the API calls count.
    /// </summary>
    public long ApiCalls { get; init; }
    
    /// <summary>
    /// Gets the storage used in bytes.
    /// </summary>
    public long StorageUsed { get; init; }
}

/// <summary>
/// Tenant cost metrics.
/// </summary>
public record TenantCostMetrics
{
    /// <summary>
    /// Gets the total cost for the period.
    /// </summary>
    public decimal TotalCost { get; init; }
    
    /// <summary>
    /// Gets the cost breakdown by category.
    /// </summary>
    public Dictionary<string, decimal> Breakdown { get; init; } = new();
    
    /// <summary>
    /// Gets the projected monthly cost.
    /// </summary>
    public decimal ProjectedMonthlyCost { get; init; }
}

/// <summary>
/// Tenant security metrics.
/// </summary>
public record TenantSecurityMetrics
{
    /// <summary>
    /// Gets the number of security incidents.
    /// </summary>
    public int SecurityIncidents { get; init; }
    
    /// <summary>
    /// Gets the compliance score percentage.
    /// </summary>
    public double ComplianceScore { get; init; }
    
    /// <summary>
    /// Gets the vulnerabilities count.
    /// </summary>
    public int Vulnerabilities { get; init; }
}

/// <summary>
/// Tenant trend analysis.
/// </summary>
public record TenantTrends
{
    /// <summary>
    /// Gets the growth trends.
    /// </summary>
    public Dictionary<string, double> Growth { get; init; } = new();
    
    /// <summary>
    /// Gets the forecasted values.
    /// </summary>
    public Dictionary<string, double> Forecasts { get; init; } = new();
}

/// <summary>
/// Agent analytics response containing detailed metrics for a specific agent.
/// </summary>
public record AgentAnalyticsResponse : BaseDto<Guid>
{
    /// <summary>
    /// Gets the agent ID.
    /// </summary>
    public required Guid AgentId { get; init; }
    
    /// <summary>
    /// Gets the time range for the analytics data.
    /// </summary>
    public required string TimeRange { get; init; }
    
    /// <summary>
    /// Gets when this analytics data was generated.
    /// </summary>
    public DateTime GeneratedAt { get; init; } = DateTime.UtcNow;
    
    /// <summary>
    /// Gets the performance metrics.
    /// </summary>
    public required AgentPerformanceMetrics PerformanceMetrics { get; init; }
    
    /// <summary>
    /// Gets the availability metrics.
    /// </summary>
    public required AgentAvailabilityMetrics AvailabilityMetrics { get; init; }
    
    /// <summary>
    /// Gets the command analytics.
    /// </summary>
    public required AgentCommandAnalytics CommandAnalytics { get; init; }
    
    /// <summary>
    /// Gets the resource analytics.
    /// </summary>
    public required AgentResourceAnalytics ResourceAnalytics { get; init; }
    
    /// <summary>
    /// Gets the error analytics.
    /// </summary>
    public required AgentErrorAnalytics ErrorAnalytics { get; init; }
    
    /// <summary>
    /// Gets the trend analysis.
    /// </summary>
    public required AgentTrends Trends { get; init; }
}

/// <summary>
/// Agent performance metrics.
/// </summary>
public record AgentPerformanceMetrics
{
    /// <summary>
    /// Gets the CPU usage statistics.
    /// </summary>
    public required PerformanceStats CpuStats { get; init; }
    
    /// <summary>
    /// Gets the memory usage statistics.
    /// </summary>
    public required PerformanceStats MemoryStats { get; init; }
    
    /// <summary>
    /// Gets the disk usage statistics.
    /// </summary>
    public required PerformanceStats DiskStats { get; init; }
}

/// <summary>
/// Performance statistics.
/// </summary>
public record PerformanceStats
{
    /// <summary>
    /// Gets the average value.
    /// </summary>
    public double Average { get; init; }
    
    /// <summary>
    /// Gets the minimum value.
    /// </summary>
    public double Min { get; init; }
    
    /// <summary>
    /// Gets the maximum value.
    /// </summary>
    public double Max { get; init; }
    
    /// <summary>
    /// Gets the 95th percentile value.
    /// </summary>
    public double P95 { get; init; }
}

/// <summary>
/// Agent availability metrics.
/// </summary>
public record AgentAvailabilityMetrics
{
    /// <summary>
    /// Gets the uptime percentage.
    /// </summary>
    public double UptimePercentage { get; init; }
    
    /// <summary>
    /// Gets the total downtime in minutes.
    /// </summary>
    public int DowntimeMinutes { get; init; }
    
    /// <summary>
    /// Gets the availability events.
    /// </summary>
    public List<AvailabilityEvent> Events { get; init; } = new();
}

/// <summary>
/// Availability event.
/// </summary>
public record AvailabilityEvent
{
    /// <summary>
    /// Gets the event type.
    /// </summary>
    public required string Type { get; init; }
    
    /// <summary>
    /// Gets when the event occurred.
    /// </summary>
    public required DateTime Timestamp { get; init; }
    
    /// <summary>
    /// Gets the duration in minutes.
    /// </summary>
    public int DurationMinutes { get; init; }
}

/// <summary>
/// Agent command analytics.
/// </summary>
public record AgentCommandAnalytics
{
    /// <summary>
    /// Gets the total commands executed.
    /// </summary>
    public int TotalExecuted { get; init; }
    
    /// <summary>
    /// Gets the success rate percentage.
    /// </summary>
    public double SuccessRate { get; init; }
    
    /// <summary>
    /// Gets the average execution time.
    /// </summary>
    public double AverageExecutionTime { get; init; }
    
    /// <summary>
    /// Gets the commands by type.
    /// </summary>
    public Dictionary<string, int> ByType { get; init; } = new();
}

/// <summary>
/// Agent resource analytics.
/// </summary>
public record AgentResourceAnalytics
{
    /// <summary>
    /// Gets the CPU utilization trends.
    /// </summary>
    public List<TimeSeriesDataPoint> CpuTrends { get; init; } = new();
    
    /// <summary>
    /// Gets the memory utilization trends.
    /// </summary>
    public List<TimeSeriesDataPoint> MemoryTrends { get; init; } = new();
    
    /// <summary>
    /// Gets the disk utilization trends.
    /// </summary>
    public List<TimeSeriesDataPoint> DiskTrends { get; init; } = new();
}

/// <summary>
/// Agent error analytics.
/// </summary>
public record AgentErrorAnalytics
{
    /// <summary>
    /// Gets the total errors count.
    /// </summary>
    public int TotalErrors { get; init; }
    
    /// <summary>
    /// Gets the errors by type.
    /// </summary>
    public Dictionary<string, int> ByType { get; init; } = new();
    
    /// <summary>
    /// Gets the error trends.
    /// </summary>
    public List<TimeSeriesDataPoint> Trends { get; init; } = new();
}

/// <summary>
/// Agent trend analysis.
/// </summary>
public record AgentTrends
{
    /// <summary>
    /// Gets the performance trends.
    /// </summary>
    public Dictionary<string, string> PerformanceTrends { get; init; } = new();
    
    /// <summary>
    /// Gets the reliability score.
    /// </summary>
    public double ReliabilityScore { get; init; }
}

/// <summary>
/// Request for generating reports.
/// </summary>
public record ReportGenerationRequest : BaseDto<Guid>
{
    /// <summary>
    /// Gets the report type.
    /// </summary>
    [Required]
    [RegularExpression("^(performance|security|usage|compliance)$", ErrorMessage = "Type must be performance, security, usage, or compliance")]
    public required string Type { get; init; }
    
    /// <summary>
    /// Gets the start date for the report.
    /// </summary>
    [Required]
    public required DateTime StartDate { get; init; }
    
    /// <summary>
    /// Gets the end date for the report.
    /// </summary>
    [Required]
    public required DateTime EndDate { get; init; }
    
    /// <summary>
    /// Gets the tenant ID to filter by.
    /// </summary>
    public Guid? TenantId { get; init; }
    
    /// <summary>
    /// Gets the output format.
    /// </summary>
    [RegularExpression("^(json|pdf|excel|csv)$", ErrorMessage = "Format must be json, pdf, excel, or csv")]
    public string Format { get; init; } = "json";
    
    /// <summary>
    /// Gets additional parameters for the report.
    /// </summary>
    public Dictionary<string, object> Parameters { get; init; } = new();
}

/// <summary>
/// Response for report generation.
/// </summary>
public record ReportGenerationResponse : BaseDto<Guid>
{
    /// <summary>
    /// Gets the generated report ID.
    /// </summary>
    public required Guid ReportId { get; init; }
    
    /// <summary>
    /// Gets the report type.
    /// </summary>
    public required string Type { get; init; }
    
    /// <summary>
    /// Gets the report status.
    /// </summary>
    public required string Status { get; init; }
    
    /// <summary>
    /// Gets when the report was generated.
    /// </summary>
    public DateTime GeneratedAt { get; init; } = DateTime.UtcNow;
    
    /// <summary>
    /// Gets the download URL for the report.
    /// </summary>
    public string? DownloadUrl { get; init; }
}

/// <summary>
/// Report template information.
/// </summary>
public record ReportTemplateResponse : BaseDto<Guid>
{
    /// <summary>
    /// Gets the template name.
    /// </summary>
    public required string Name { get; init; }
    
    /// <summary>
    /// Gets the template description.
    /// </summary>
    public required string Description { get; init; }
    
    /// <summary>
    /// Gets the template category.
    /// </summary>
    public required string Category { get; init; }
    
    /// <summary>
    /// Gets the available parameters.
    /// </summary>
    public List<string> Parameters { get; init; } = new();
}

/// <summary>
/// Scheduled report information.
/// </summary>
public record ScheduledReportResponse : BaseDto<Guid>
{
    /// <summary>
    /// Gets the scheduled report ID.
    /// </summary>
    
    
    /// <summary>
    /// Gets the report name.
    /// </summary>
    public required string Name { get; init; }
    
    /// <summary>
    /// Gets the template ID.
    /// </summary>
    public required Guid TemplateId { get; init; }
    
    /// <summary>
    /// Gets the schedule (cron expression).
    /// </summary>
    public required string Schedule { get; init; }
    
    /// <summary>
    /// Gets whether the schedule is active.
    /// </summary>
    public bool IsActive { get; init; }
    
    /// <summary>
    /// Gets when the report was last run.
    /// </summary>
    public DateTime? LastRun { get; init; }
    
    /// <summary>
    /// Gets when the report will next run.
    /// </summary>
    public DateTime? NextRun { get; init; }
    
    /// <summary>
    /// Gets the report parameters.
    /// </summary>
    public Dictionary<string, object> Parameters { get; init; } = new();
}

/// <summary>
/// Custom analytics query request.
/// </summary>
public record CustomAnalyticsQueryRequest : BaseDto<Guid>
{
    /// <summary>
    /// Gets the query type.
    /// </summary>
    [Required]
    [RegularExpression("^(aggregation|filter|join)$", ErrorMessage = "QueryType must be aggregation, filter, or join")]
    public required string QueryType { get; init; }
    
    /// <summary>
    /// Gets the data source.
    /// </summary>
    [Required]
    [RegularExpression("^(agents|commands|telemetry)$", ErrorMessage = "DataSource must be agents, commands, or telemetry")]
    public required string DataSource { get; init; }
    
    /// <summary>
    /// Gets the query filters.
    /// </summary>
    public Dictionary<string, object> Filters { get; init; } = new();
    
    /// <summary>
    /// Gets the fields to group by.
    /// </summary>
    public List<string> GroupBy { get; init; } = new();
    
    /// <summary>
    /// Gets the aggregations to apply.
    /// </summary>
    public Dictionary<string, string> Aggregations { get; init; } = new();
    
    /// <summary>
    /// Gets the result limit.
    /// </summary>
    [Range(1, 10000, ErrorMessage = "Limit must be between 1 and 10000")]
    public int Limit { get; init; } = 1000;
}

/// <summary>
/// Analytics export request.
/// </summary>
public record AnalyticsExportRequest : BaseDto<Guid>
{
    /// <summary>
    /// Gets the data type to export.
    /// </summary>
    [Required]
    [RegularExpression("^(agents|tenants|analytics)$", ErrorMessage = "DataType must be agents, tenants, or analytics")]
    public required string DataType { get; init; }
    
    /// <summary>
    /// Gets the export format.
    /// </summary>
    [Required]
    [RegularExpression("^(csv|json|excel)$", ErrorMessage = "Format must be csv, json, or excel")]
    public required string Format { get; init; }
    
    /// <summary>
    /// Gets the start date for the export.
    /// </summary>
    public DateTime? StartDate { get; init; }
    
    /// <summary>
    /// Gets the end date for the export.
    /// </summary>
    public DateTime? EndDate { get; init; }
    
    /// <summary>
    /// Gets the tenant ID to filter by.
    /// </summary>
    public Guid? TenantId { get; init; }
    
    /// <summary>
    /// Gets additional filters for the export.
    /// </summary>
    public Dictionary<string, object> Filters { get; init; } = new();
}

/// <summary>
/// Analytics export response.
/// </summary>
public record AnalyticsExportResponse : BaseDto<Guid>
{
    /// <summary>
    /// Gets the export job ID.
    /// </summary>
    public required string JobId { get; init; }
    
    /// <summary>
    /// Gets the export status.
    /// </summary>
    public required string Status { get; init; }
    
    /// <summary>
    /// Gets the download URL when ready.
    /// </summary>
    public string? DownloadUrl { get; init; }
    
    /// <summary>
    /// Gets when the export expires.
    /// </summary>
    public DateTime? ExpiresAt { get; init; }
}


