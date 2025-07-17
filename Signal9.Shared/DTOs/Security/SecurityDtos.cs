using System.ComponentModel.DataAnnotations;
using Signal9.Shared.DTOs.Base;
using Signal9.Shared.DTOs.Monitoring;

namespace Signal9.Shared.DTOs.Security;

/// <summary>
/// Request to create or update a user account.
/// </summary>
public record UserAccountRequest : BaseDto<Guid>
{
    /// <summary>
    /// Gets the username for the account.
    /// </summary>
    [MaxLength(100)]
    public required string Username { get; init; }
    
    /// <summary>
    /// Gets the email address for the account.
    /// </summary>
    [MaxLength(255)]
    [EmailAddress]
    public required string Email { get; init; }
    
    /// <summary>
    /// Gets the full name of the user.
    /// </summary>
    [MaxLength(200)]
    public required string FullName { get; init; }
    
    /// <summary>
    /// Gets the role IDs to assign to the user.
    /// </summary>
    public List<Guid> RoleIds { get; init; } = new();
    
    /// <summary>
    /// Gets whether the account is active.
    /// </summary>
    public bool IsActive { get; init; } = true;
    
    /// <summary>
    /// Gets additional user metadata.
    /// </summary>
    public Dictionary<string, object>? Metadata { get; init; }
}

/// <summary>
/// Response containing user account information.
/// </summary>
public record UserAccountResponse : BaseDto<Guid>
{
    /// <summary>
    /// Gets the unique identifier for the user account.
    /// </summary>
    
    
    /// <summary>
    /// Gets the username for the account.
    /// </summary>
    public required string Username { get; init; }
    
    /// <summary>
    /// Gets the email address for the account.
    /// </summary>
    public required string Email { get; init; }
    
    /// <summary>
    /// Gets the full name of the user.
    /// </summary>
    public required string FullName { get; init; }
    
    /// <summary>
    /// Gets the roles assigned to the user.
    /// </summary>
    public List<RoleResponse> Roles { get; init; } = new();
    
    /// <summary>
    /// Gets whether the account is active.
    /// </summary>
    public bool IsActive { get; init; }
    
    /// <summary>
    /// Gets additional user metadata.
    /// </summary>
    public Dictionary<string, object>? Metadata { get; init; }
}

/// <summary>
/// Request to create or update a role.
/// </summary>
public record RoleRequest : BaseDto<Guid>
{
    /// <summary>
    /// Gets the name of the role.
    /// </summary>
    [MaxLength(100)]
    public required string Name { get; init; }
    
    /// <summary>
    /// Gets the description of the role.
    /// </summary>
    [MaxLength(500)]
    public string? Description { get; init; }
    
    /// <summary>
    /// Gets the permission IDs to assign to the role.
    /// </summary>
    public List<Guid> PermissionIds { get; init; } = new();
    
    /// <summary>
    /// Gets whether the role is active.
    /// </summary>
    public bool IsActive { get; init; } = true;
}

/// <summary>
/// Response containing role information.
/// </summary>
public record RoleResponse : BaseDto<Guid>
{
    /// <summary>
    /// Gets the unique identifier for the role.
    /// </summary>
    
    
    /// <summary>
    /// Gets the name of the role.
    /// </summary>
    public required string Name { get; init; }
    
    /// <summary>
    /// Gets the description of the role.
    /// </summary>
    public string? Description { get; init; }
    
    /// <summary>
    /// Gets the permissions assigned to the role.
    /// </summary>
    public List<PermissionDto> Permissions { get; init; } = new();
    
    /// <summary>
    /// Gets whether the role is active.
    /// </summary>
    public bool IsActive { get; init; }
    
    /// <summary>
    /// Gets the role creation timestamp.
    /// </summary>
    }

/// <summary>
/// Permission information.
/// </summary>
public record PermissionDto : BaseDto<Guid>
{
    /// <summary>
    /// Gets the unique identifier for the permission.
    /// </summary>
    
    
    /// <summary>
    /// Gets the name of the permission.
    /// </summary>
    public required string Name { get; init; }
    
    /// <summary>
    /// Gets the description of the permission.
    /// </summary>
    public string? Description { get; init; }
    
    /// <summary>
    /// Gets the resource this permission applies to.
    /// </summary>
    public required string Resource { get; init; }
    
    /// <summary>
    /// Gets the action this permission allows.
    /// </summary>
    public required string Action { get; init; }
}

/// <summary>
/// Audit log entry for security tracking.
/// </summary>
public record AuditLogEntry : BaseDto<Guid>
{
    /// <summary>
    /// Gets the unique identifier for the audit log entry.
    /// </summary>
    
    
    /// <summary>
    /// Gets the user ID who performed the action.
    /// </summary>
    public Guid? UserId { get; init; }
    
    /// <summary>
    /// Gets the username who performed the action.
    /// </summary>
    public string? Username { get; init; }
    
    /// <summary>
    /// Gets the action that was performed.
    /// </summary>
    public required string Action { get; init; }
    
    /// <summary>
    /// Gets the resource that was affected.
    /// </summary>
    public required string Resource { get; init; }
    
    /// <summary>
    /// Gets the resource ID that was affected.
    /// </summary>
    public string? ResourceId { get; init; }
    
    /// <summary>
    /// Gets the IP address of the user.
    /// </summary>
    public string? IpAddress { get; init; }
    
    /// <summary>
    /// Gets the user agent string.
    /// </summary>
    public string? UserAgent { get; init; }
    
    /// <summary>
    /// Gets the result of the action.
    /// </summary>
    public required AuditResult Result { get; init; }
    
    /// <summary>
    /// Gets additional details about the action.
    /// </summary>
    public string? Details { get; init; }
    
    /// <summary>
    /// Gets the timestamp when the action occurred.
    /// </summary>
    public DateTime OccurredAt { get; init; }
}

/// <summary>
/// Compliance report for security and regulatory requirements.
/// </summary>
public record ComplianceReport : BaseDto<Guid>
{
    /// <summary>
    /// Gets the unique identifier for the compliance report.
    /// </summary>
    
    
    /// <summary>
    /// Gets the name of the compliance framework.
    /// </summary>
    public required string Framework { get; init; }
    
    /// <summary>
    /// Gets the overall compliance score percentage.
    /// </summary>
    public required double ComplianceScore { get; init; }
    
    /// <summary>
    /// Gets the compliance status.
    /// </summary>
    public required ComplianceStatus Status { get; init; }
    
    /// <summary>
    /// Gets the report generation date.
    /// </summary>
    public DateTime GeneratedAt { get; init; }
    
    /// <summary>
    /// Gets the period covered by the report.
    /// </summary>
    public required DateRange Period { get; init; }
    
    /// <summary>
    /// Gets the compliance findings.
    /// </summary>
    public List<ComplianceFinding> Findings { get; init; } = new();
    
    /// <summary>
    /// Gets compliance metrics by category.
    /// </summary>
    public Dictionary<string, double> MetricsByCategory { get; init; } = new();
}

/// <summary>
/// Threat detection alert for security monitoring.
/// </summary>
public record ThreatDetectionAlert : BaseDto<Guid>
{
    /// <summary>
    /// Gets the unique identifier for the alert.
    /// </summary>
    
    
    /// <summary>
    /// Gets the agent ID where the threat was detected.
    /// </summary>
    public required Guid AgentId { get; init; }
    
    /// <summary>
    /// Gets the type of threat detected.
    /// </summary>
    public required ThreatType ThreatType { get; init; }
    
    /// <summary>
    /// Gets the severity level of the threat.
    /// </summary>
    public required ThreatSeverity Severity { get; init; }
    
    /// <summary>
    /// Gets the title of the alert.
    /// </summary>
    public required string Title { get; init; }
    
    /// <summary>
    /// Gets the detailed description of the threat.
    /// </summary>
    public required string Description { get; init; }
    
    /// <summary>
    /// Gets the source of the threat detection.
    /// </summary>
    public required string Source { get; init; }
    
    /// <summary>
    /// Gets the status of the alert.
    /// </summary>
    public required AlertStatus Status { get; init; }
    
    /// <summary>
    /// Gets when the threat was first detected.
    /// </summary>
    public DateTime DetectedAt { get; init; }
    
    /// <summary>
    /// Gets when the alert was resolved, if applicable.
    /// </summary>
    public DateTime? ResolvedAt { get; init; }
    
    /// <summary>
    /// Gets additional threat indicators.
    /// </summary>
    public Dictionary<string, object>? Indicators { get; init; }
}

/// <summary>
/// Antivirus status information for an agent.
/// </summary>
public record AntivirusStatus : BaseDto<Guid>
{
    /// <summary>
    /// Gets the agent ID this status applies to.
    /// </summary>
    public required Guid AgentId { get; init; }
    
    /// <summary>
    /// Gets the name of the antivirus software.
    /// </summary>
    public required string AntivirusName { get; init; }
    
    /// <summary>
    /// Gets the version of the antivirus software.
    /// </summary>
    public required string Version { get; init; }
    
    /// <summary>
    /// Gets whether real-time protection is enabled.
    /// </summary>
    public bool RealTimeProtectionEnabled { get; init; }
    
    /// <summary>
    /// Gets the last scan date.
    /// </summary>
    public DateTime? LastScanDate { get; init; }
    
    /// <summary>
    /// Gets the scan type of the last scan.
    /// </summary>
    public ScanType? LastScanType { get; init; }
    
    /// <summary>
    /// Gets the number of threats found in the last scan.
    /// </summary>
    public int ThreatsFound { get; init; }
    
    /// <summary>
    /// Gets the date of the last definition update.
    /// </summary>
    public DateTime? LastDefinitionUpdate { get; init; }
    
    /// <summary>
    /// Gets the overall antivirus status.
    /// </summary>
    public required AntivirusHealthStatus HealthStatus { get; init; }
    
    /// <summary>
    /// Gets when this status was last updated.
    /// </summary>
    public DateTime LastUpdated { get; init; }
}

#region Supporting Types

/// <summary>
/// Audit result enumeration.
/// </summary>
public enum AuditResult
{
    Success,
    Failure,
    Warning,
    Information
}

/// <summary>
/// Compliance status enumeration.
/// </summary>
public enum ComplianceStatus
{
    Compliant,
    NonCompliant,
    PartiallyCompliant,
    Unknown
}

/// <summary>
/// Date range for compliance reporting.
/// </summary>
public record DateRange
{
    /// <summary>
    /// Gets the start date of the range.
    /// </summary>
    public required DateTime StartDate { get; init; }
    
    /// <summary>
    /// Gets the end date of the range.
    /// </summary>
    public required DateTime EndDate { get; init; }
}

/// <summary>
/// Compliance finding information.
/// </summary>
public record ComplianceFinding
{
    /// <summary>
    /// Gets the unique identifier for the finding.
    /// </summary>
    
    
    /// <summary>
    /// Gets the category of the finding.
    /// </summary>
    public required string Category { get; init; }
    
    /// <summary>
    /// Gets the description of the finding.
    /// </summary>
    public required string Description { get; init; }
    
    /// <summary>
    /// Gets the severity of the finding.
    /// </summary>
    public required string Severity { get; init; }
    
    /// <summary>
    /// Gets the status of the finding.
    /// </summary>
    public required string Status { get; init; }
}

/// <summary>
/// Threat type enumeration.
/// </summary>
public enum ThreatType
{
    Malware,
    Virus,
    Trojan,
    Ransomware,
    Spyware,
    Adware,
    Rootkit,
    Worm,
    SuspiciousActivity,
    UnauthorizedAccess,
    DataBreach,
    Other
}

/// <summary>
/// Threat severity enumeration.
/// </summary>
public enum ThreatSeverity
{
    Low,
    Medium,
    High,
    Critical
}

/// <summary>
/// Scan type enumeration.
/// </summary>
public enum ScanType
{
    Quick,
    Full,
    Custom,
    RealTime
}

/// <summary>
/// Antivirus health status enumeration.
/// </summary>
public enum AntivirusHealthStatus
{
    Healthy,
    Warning,
    Critical,
    Disabled,
    Unknown
}

#endregion


