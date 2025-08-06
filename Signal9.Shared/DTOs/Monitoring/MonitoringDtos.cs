using System.ComponentModel.DataAnnotations;
using Signal9.Shared.DTOs.Base;

namespace Signal9.Shared.DTOs.Monitoring;

/// <summary>
/// Request to create or update an alert.
/// </summary>
public record AlertRequest : BaseDto<Guid>
{
    /// <summary>
    /// Gets the agent ID this alert is associated with.
    /// </summary>
    public required Guid AgentId { get; init; }
    
    /// <summary>
    /// Gets the alert rule ID that triggered this alert.
    /// </summary>
    public Guid? AlertRuleId { get; init; }
    
    /// <summary>
    /// Gets the title of the alert.
    /// </summary>
    [MaxLength(200)]
    public required string Title { get; init; }
    
    /// <summary>
    /// Gets the detailed description of the alert.
    /// </summary>
    [MaxLength(1000)]
    public required string Description { get; init; }
    
    /// <summary>
    /// Gets the severity level of the alert.
    /// </summary>
    public required AlertSeverity Severity { get; init; }
    
    /// <summary>
    /// Gets the category of the alert.
    /// </summary>
    public required AlertCategory Category { get; init; }
    
    /// <summary>
    /// Gets the source that generated the alert.
    /// </summary>
    [MaxLength(100)]
    public required string Source { get; init; }
    
    /// <summary>
    /// Gets the metric value that triggered the alert, if applicable.
    /// </summary>
    public double? MetricValue { get; init; }
    
    /// <summary>
    /// Gets the threshold value that was exceeded, if applicable.
    /// </summary>
    public double? ThresholdValue { get; init; }
    
    /// <summary>
    /// Gets additional alert data and context.
    /// </summary>
    public Dictionary<string, object>? Data { get; init; }
    
    /// <summary>
    /// Gets the tags associated with this alert.
    /// </summary>
    public List<string> Tags { get; init; } = [];
}

/// <summary>
/// Response containing alert information.
/// </summary>
public record AlertResponse : BaseDto<Guid>
{
    /// <summary>
    /// Gets the unique identifier for the alert.
    /// </summary>
    
    
    /// <summary>
    /// Gets the agent ID this alert is associated with.
    /// </summary>
    public required Guid AgentId { get; init; }
    
    /// <summary>
    /// Gets the agent name for display purposes.
    /// </summary>
    public string? AgentName { get; init; }
    
    /// <summary>
    /// Gets the alert rule ID that triggered this alert.
    /// </summary>
    public Guid? AlertRuleId { get; init; }
    
    /// <summary>
    /// Gets the alert rule name for display purposes.
    /// </summary>
    public string? AlertRuleName { get; init; }
    
    /// <summary>
    /// Gets the title of the alert.
    /// </summary>
    public required string Title { get; init; }
    
    /// <summary>
    /// Gets the detailed description of the alert.
    /// </summary>
    public required string Description { get; init; }
    
    /// <summary>
    /// Gets the severity level of the alert.
    /// </summary>
    public required AlertSeverity Severity { get; init; }
    
    /// <summary>
    /// Gets the category of the alert.
    /// </summary>
    public required AlertCategory Category { get; init; }
    
    /// <summary>
    /// Gets the current status of the alert.
    /// </summary>
    public required AlertStatus Status { get; init; }
    
    /// <summary>
    /// Gets the source that generated the alert.
    /// </summary>
    public required string Source { get; init; }
    
    /// <summary>
    /// Gets the metric value that triggered the alert.
    /// </summary>
    public double? MetricValue { get; init; }
    
    /// <summary>
    /// Gets the threshold value that was exceeded.
    /// </summary>
    public double? ThresholdValue { get; init; }
    
    /// <summary>
    /// Gets when the alert was first triggered.
    /// </summary>
    public DateTime TriggeredAt { get; init; }
    
    /// <summary>
    /// Gets when the alert was acknowledged.
    /// </summary>
    public DateTime? AcknowledgedAt { get; init; }
    
    /// <summary>
    /// Gets who acknowledged the alert.
    /// </summary>
    public string? AcknowledgedBy { get; init; }
    
    /// <summary>
    /// Gets when the alert was resolved.
    /// </summary>
    public DateTime? ResolvedAt { get; init; }
    
    /// <summary>
    /// Gets who resolved the alert.
    /// </summary>
    public string? ResolvedBy { get; init; }
    
    /// <summary>
    /// Gets the resolution notes.
    /// </summary>
    public string? ResolutionNotes { get; init; }
    
    /// <summary>
    /// Gets additional alert data and context.
    /// </summary>
    public Dictionary<string, object>? Data { get; init; }
    
    /// <summary>
    /// Gets the tags associated with this alert.
    /// </summary>
    public List<string> Tags { get; init; } = [];
}

/// <summary>
/// Request to create or update an alert rule.
/// </summary>
public record AlertRuleRequest : BaseDto<Guid>
{
    /// <summary>
    /// Gets the name of the alert rule.
    /// </summary>
    [MaxLength(200)]
    public required string Name { get; init; }
    
    /// <summary>
    /// Gets the description of the alert rule.
    /// </summary>
    [MaxLength(500)]
    public string? Description { get; init; }
    
    /// <summary>
    /// Gets the metric or condition being monitored.
    /// </summary>
    [MaxLength(100)]
    public required string Metric { get; init; }
    
    /// <summary>
    /// Gets the comparison operator for the rule.
    /// </summary>
    public required ComparisonOperator Operator { get; init; }
    
    /// <summary>
    /// Gets the threshold value for triggering the alert.
    /// </summary>
    public required double Threshold { get; init; }
    
    /// <summary>
    /// Gets the time window for evaluation in minutes.
    /// </summary>
    public int EvaluationWindowMinutes { get; init; } = 5;
    
    /// <summary>
    /// Gets the frequency of evaluation in minutes.
    /// </summary>
    public int EvaluationFrequencyMinutes { get; init; } = 1;
    
    /// <summary>
    /// Gets the severity level for alerts generated by this rule.
    /// </summary>
    public required AlertSeverity Severity { get; init; }
    
    /// <summary>
    /// Gets the category for alerts generated by this rule.
    /// </summary>
    public required AlertCategory Category { get; init; }
    
    /// <summary>
    /// Gets whether the rule is currently enabled.
    /// </summary>
    public bool IsEnabled { get; init; } = true;
    
    /// <summary>
    /// Gets the agent IDs this rule applies to (empty means all agents).
    /// </summary>
    public List<Guid> AgentIds { get; init; } = [];
    
    /// <summary>
    /// Gets the notification targets for this rule.
    /// </summary>
    public List<Guid> NotificationIds { get; init; } = [];
    
    /// <summary>
    /// Gets the tags for filtering agents this rule applies to.
    /// </summary>
    public List<string> AgentTags { get; init; } = [];
    
    /// <summary>
    /// Gets additional rule configuration.
    /// </summary>
    public Dictionary<string, object>? Configuration { get; init; }
}

/// <summary>
/// Response containing alert rule information.
/// </summary>
public record AlertRuleResponse : BaseDto<Guid>
{
    /// <summary>
    /// Gets the unique identifier for the alert rule.
    /// </summary>
    
    
    /// <summary>
    /// Gets the name of the alert rule.
    /// </summary>
    public required string Name { get; init; }
    
    /// <summary>
    /// Gets the description of the alert rule.
    /// </summary>
    public string? Description { get; init; }
    
    /// <summary>
    /// Gets the metric or condition being monitored.
    /// </summary>
    public required string Metric { get; init; }
    
    /// <summary>
    /// Gets the comparison operator for the rule.
    /// </summary>
    public required ComparisonOperator Operator { get; init; }
    
    /// <summary>
    /// Gets the threshold value for triggering the alert.
    /// </summary>
    public required double Threshold { get; init; }
    
    /// <summary>
    /// Gets the time window for evaluation in minutes.
    /// </summary>
    public int EvaluationWindowMinutes { get; init; }
    
    /// <summary>
    /// Gets the frequency of evaluation in minutes.
    /// </summary>
    public int EvaluationFrequencyMinutes { get; init; }
    
    /// <summary>
    /// Gets the severity level for alerts generated by this rule.
    /// </summary>
    public required AlertSeverity Severity { get; init; }
    
    /// <summary>
    /// Gets the category for alerts generated by this rule.
    /// </summary>
    public required AlertCategory Category { get; init; }
    
    /// <summary>
    /// Gets whether the rule is currently enabled.
    /// </summary>
    public bool IsEnabled { get; init; }
    
    /// <summary>
    /// Gets when the rule was last evaluated.
    /// </summary>
    public DateTime? LastEvaluated { get; init; }
    
    /// <summary>
    /// Gets the number of times this rule has been triggered.
    /// </summary>
    public long TriggerCount { get; init; }
    
    /// <summary>
    /// Gets the agent IDs this rule applies to.
    /// </summary>
    public List<Guid> AgentIds { get; init; } = [];
    
    /// <summary>
    /// Gets the notification targets for this rule.
    /// </summary>
    public List<Guid> NotificationIds { get; init; } = [];
    
    /// <summary>
    /// Gets the tags for filtering agents this rule applies to.
    /// </summary>
    public List<string> AgentTags { get; init; } = [];
    
    /// <summary>
    /// Gets additional rule configuration.
    /// </summary>
    public Dictionary<string, object>? Configuration { get; init; }
}

/// <summary>
/// Request to create or update a notification configuration.
/// </summary>
public record NotificationRequest : BaseDto<Guid>
{
    /// <summary>
    /// Gets the name of the notification configuration.
    /// </summary>
    [MaxLength(200)]
    public required string Name { get; init; }
    
    /// <summary>
    /// Gets the type of notification.
    /// </summary>
    public required NotificationType Type { get; init; }
    
    /// <summary>
    /// Gets whether the notification is enabled.
    /// </summary>
    public bool IsEnabled { get; init; } = true;
    
    /// <summary>
    /// Gets the configuration for the notification.
    /// </summary>
    public required Dictionary<string, object> Configuration { get; init; }
    
    /// <summary>
    /// Gets the template for the notification message.
    /// </summary>
    [MaxLength(2000)]
    public string? MessageTemplate { get; init; }
    
    /// <summary>
    /// Gets the minimum severity level to trigger notifications.
    /// </summary>
    public AlertSeverity MinimumSeverity { get; init; } = AlertSeverity.Low;
    
    /// <summary>
    /// Gets the categories of alerts to notify about.
    /// </summary>
    public List<AlertCategory> Categories { get; init; } = [];
    
    /// <summary>
    /// Gets the quiet hours configuration.
    /// </summary>
    public QuietHours? QuietHours { get; init; }
}

/// <summary>
/// Response containing notification configuration information.
/// </summary>
public record NotificationResponse : BaseDto<Guid>
{
    /// <summary>
    /// Gets the unique identifier for the notification configuration.
    /// </summary>
    
    
    /// <summary>
    /// Gets the name of the notification configuration.
    /// </summary>
    public required string Name { get; init; }
    
    /// <summary>
    /// Gets the type of notification.
    /// </summary>
    public required NotificationType Type { get; init; }
    
    /// <summary>
    /// Gets whether the notification is enabled.
    /// </summary>
    public bool IsEnabled { get; init; }
    
    /// <summary>
    /// Gets the configuration for the notification.
    /// </summary>
    public required Dictionary<string, object> Configuration { get; init; }
    
    /// <summary>
    /// Gets the template for the notification message.
    /// </summary>
    public string? MessageTemplate { get; init; }
    
    /// <summary>
    /// Gets the minimum severity level to trigger notifications.
    /// </summary>
    public AlertSeverity MinimumSeverity { get; init; }
    
    /// <summary>
    /// Gets the categories of alerts to notify about.
    /// </summary>
    public List<AlertCategory> Categories { get; init; } = [];
    
    /// <summary>
    /// Gets the quiet hours configuration.
    /// </summary>
    public QuietHours? QuietHours { get; init; }
    
    /// <summary>
    /// <summary>
    /// Gets the number of notifications sent.
    /// </summary>
    public long NotificationsSent { get; init; }
    
    /// <summary>
    /// Gets when the last notification was sent.
    /// </summary>
    public DateTime? LastNotificationSent { get; init; }
}

/// <summary>
/// System event for tracking significant system occurrences.
/// </summary>
public record SystemEvent : BaseDto<Guid>
{
    /// <summary>
    /// Gets the unique identifier for the system event.
    /// </summary>
    
    
    /// <summary>
    /// Gets the agent ID where the event occurred.
    /// </summary>
    public Guid? AgentId { get; init; }
    
    /// <summary>
    /// Gets the type of system event.
    /// </summary>
    public required SystemEventType EventType { get; init; }
    
    /// <summary>
    /// Gets the severity level of the event.
    /// </summary>
    public required EventSeverity Severity { get; init; }
    
    /// <summary>
    /// Gets the source component that generated the event.
    /// </summary>
    [MaxLength(100)]
    public required string Source { get; init; }
    
    /// <summary>
    /// Gets the title or summary of the event.
    /// </summary>
    [MaxLength(200)]
    public required string Title { get; init; }
    
    /// <summary>
    /// Gets the detailed description of the event.
    /// </summary>
    [MaxLength(1000)]
    public required string Description { get; init; }
    
    /// <summary>
    /// Gets when the event occurred.
    /// </summary>
    public DateTime OccurredAt { get; init; }
    
    /// <summary>
    /// Gets the user associated with the event, if applicable.
    /// </summary>
    [MaxLength(100)]
    public string? UserId { get; init; }
    
    /// <summary>
    /// Gets the username associated with the event, if applicable.
    /// </summary>
    [MaxLength(100)]
    public string? Username { get; init; }
    
    /// <summary>
    /// Gets additional event data and context.
    /// </summary>
    public Dictionary<string, object>? Data { get; init; }
    
    /// <summary>
    /// Gets the correlation ID for related events.
    /// </summary>
    public Guid? CorrelationId { get; init; }
    
    /// <summary>
    /// Gets the tags associated with this event.
    /// </summary>
    public List<string> Tags { get; init; } = [];
}

/// <summary>
/// Performance baseline for establishing expected system behavior.
/// </summary>
public record PerformanceBaseline : BaseDto<Guid>
{
    /// <summary>
    /// Gets the unique identifier for the performance baseline.
    /// </summary>
    
    
    /// <summary>
    /// Gets the agent ID this baseline applies to.
    /// </summary>
    public required Guid AgentId { get; init; }
    
    /// <summary>
    /// Gets the metric name this baseline is for.
    /// </summary>
    [MaxLength(100)]
    public required string MetricName { get; init; }
    
    /// <summary>
    /// Gets the baseline value.
    /// </summary>
    public required double BaselineValue { get; init; }
    
    /// <summary>
    /// Gets the minimum acceptable value.
    /// </summary>
    public required double MinValue { get; init; }
    
    /// <summary>
    /// Gets the maximum acceptable value.
    /// </summary>
    public required double MaxValue { get; init; }
    
    /// <summary>
    /// Gets the standard deviation of the baseline.
    /// </summary>
    public double StandardDeviation { get; init; }
    
    /// <summary>
    /// Gets the confidence level of the baseline.
    /// </summary>
    public double ConfidenceLevel { get; init; }
    
    /// <summary>
    /// Gets the time period this baseline covers.
    /// </summary>
    public required DateTimeRange Period { get; init; }
    
    /// <summary>
    /// Gets when the baseline was calculated.
    /// </summary>
    public DateTime CalculatedAt { get; init; }
    
    /// <summary>
    /// Gets the number of data points used to calculate the baseline.
    /// </summary>
    public int DataPointCount { get; init; }
    
    /// <summary>
    /// Gets whether the baseline is currently active.
    /// </summary>
    public bool IsActive { get; init; }
    
    /// <summary>
    /// Gets additional baseline metadata.
    /// </summary>
    public Dictionary<string, object>? Metadata { get; init; }
}

/// <summary>
/// Health status information for system components.
/// </summary>
public record HealthStatus : BaseDto<Guid>
{
    /// <summary>
    /// Gets the unique identifier for the health status.
    /// </summary>
    
    
    /// <summary>
    /// Gets the agent ID this health status applies to.
    /// </summary>
    public required Guid AgentId { get; init; }
    
    /// <summary>
    /// Gets the component or service being monitored.
    /// </summary>
    [MaxLength(100)]
    public required string Component { get; init; }
    
    /// <summary>
    /// Gets the overall health status.
    /// </summary>
    public required HealthState Status { get; init; }
    
    /// <summary>
    /// Gets the health score as a percentage.
    /// </summary>
    public double HealthScore { get; init; }
    
    /// <summary>
    /// Gets the status message or description.
    /// </summary>
    [MaxLength(500)]
    public string? StatusMessage { get; init; }
    
    /// <summary>
    /// Gets when the health status was last checked.
    /// </summary>
    public DateTime LastChecked { get; init; }
    
    /// <summary>
    /// Gets when the status last changed.
    /// </summary>
    public DateTime? LastStatusChange { get; init; }
    
    /// <summary>
    /// Gets the uptime percentage.
    /// </summary>
    public double UptimePercentage { get; init; }
    
    /// <summary>
    /// Gets the individual health checks and their results.
    /// </summary>
    public List<HealthCheck> HealthChecks { get; init; } = [];
    
    /// <summary>
    /// Gets performance metrics related to health.
    /// </summary>
    public Dictionary<string, double> Metrics { get; init; } = [];
    
    /// <summary>
    /// Gets the trends in health status.
    /// </summary>
    public HealthTrend? Trend { get; init; }
}

#region Supporting Types

/// <summary>
/// Alert severity enumeration.
/// </summary>
public enum AlertSeverity
{
    Low,
    Medium,
    High,
    Critical
}

/// <summary>
/// Alert category enumeration.
/// </summary>
public enum AlertCategory
{
    Performance,
    Security,
    Availability,
    Capacity,
    Error,
    Configuration,
    Network,
    Hardware,
    Software,
    Compliance,
    Other
}

/// <summary>
/// Alert status enumeration.
/// </summary>
public enum AlertStatus
{
    Open,
    Acknowledged,
    InProgress,
    Resolved,
    Dismissed,
    Escalated
}

/// <summary>
/// Comparison operator enumeration.
/// </summary>
public enum ComparisonOperator
{
    GreaterThan,
    GreaterThanOrEqual,
    LessThan,
    LessThanOrEqual,
    Equal,
    NotEqual,
    Contains,
    NotContains
}

/// <summary>
/// Notification type enumeration.
/// </summary>
public enum NotificationType
{
    Email,
    SMS,
    Webhook,
    Slack,
    Teams,
    PagerDuty,
    ServiceNow,
    Custom
}

/// <summary>
/// Quiet hours configuration.
/// </summary>
public record QuietHours
{
    /// <summary>
    /// Gets the start time for quiet hours (24-hour format).
    /// </summary>
    public required TimeOnly StartTime { get; init; }
    
    /// <summary>
    /// Gets the end time for quiet hours (24-hour format).
    /// </summary>
    public required TimeOnly EndTime { get; init; }
    
    /// <summary>
    /// Gets the days of the week quiet hours apply to.
    /// </summary>
    public List<DayOfWeek> Days { get; init; } = [];
    
    /// <summary>
    /// Gets the timezone for quiet hours.
    /// </summary>
    [MaxLength(50)]
    public string? TimeZone { get; init; }
}

/// <summary>
/// System event type enumeration.
/// </summary>
public enum SystemEventType
{
    AgentConnected,
    AgentDisconnected,
    AgentRegistered,
    AgentDeregistered,
    CommandExecuted,
    AlertTriggered,
    AlertResolved,
    ConfigurationChanged,
    SecurityIncident,
    PerformanceAnomaly,
    ServiceStarted,
    ServiceStopped,
    BackupCompleted,
    BackupFailed,
    UpdateInstalled,
    UpdateFailed,
    MaintenanceStarted,
    MaintenanceCompleted,
    Other
}

/// <summary>
/// Event severity enumeration.
/// </summary>
public enum EventSeverity
{
    Information,
    Warning,
    Error,
    Critical
}

/// <summary>
/// Date time range.
/// </summary>
public record DateTimeRange
{
    /// <summary>
    /// Gets the start date and time.
    /// </summary>
    public required DateTime Start { get; init; }
    
    /// <summary>
    /// Gets the end date and time.
    /// </summary>
    public required DateTime End { get; init; }
}

/// <summary>
/// Health state enumeration.
/// </summary>
public enum HealthState
{
    Healthy,
    Warning,
    Critical,
    Unknown,
    Maintenance
}

/// <summary>
/// Individual health check result.
/// </summary>
public record HealthCheck
{
    /// <summary>
    /// Gets the name of the health check.
    /// </summary>
    public required string Name { get; init; }
    
    /// <summary>
    /// Gets the status of the health check.
    /// </summary>
    public required HealthState Status { get; init; }
    
    /// <summary>
    /// Gets the response time for the health check.
    /// </summary>
    public TimeSpan ResponseTime { get; init; }
    
    /// <summary>
    /// Gets the message from the health check.
    /// </summary>
    public string? Message { get; init; }
    
    /// <summary>
    /// Gets additional data from the health check.
    /// </summary>
    public Dictionary<string, object>? Data { get; init; }
}

/// <summary>
/// Health trend information.
/// </summary>
public record HealthTrend
{
    /// <summary>
    /// Gets the trend direction.
    /// </summary>
    public required TrendDirection Direction { get; init; }
    
    /// <summary>
    /// Gets the confidence level of the trend.
    /// </summary>
    public double Confidence { get; init; }
    
    /// <summary>
    /// Gets the time period the trend covers.
    /// </summary>
    public required TimeSpan Period { get; init; }
}

/// <summary>
/// Trend direction enumeration.
/// </summary>
public enum TrendDirection
{
    Improving,
    Stable,
    Deteriorating,
    Unknown
}

#endregion


