using Signal9.Shared.Models;

namespace Signal9.Shared.DTOs;

/// <summary>
/// Request model for creating a new tenant
/// </summary>
public class CreateTenantRequest
{
    public string Name { get; set; } = string.Empty;
    public string? Code { get; set; }
    public string? Description { get; set; }
    public Guid? ParentTenantId { get; set; }
    public string? TenantType { get; set; }
    public string? ContactEmail { get; set; }
    public string? ContactPhone { get; set; }
    public TenantPlan? Plan { get; set; }
    public int? MaxAgents { get; set; }
}

/// <summary>
/// Request model for updating a tenant
/// </summary>
public class UpdateTenantRequest
{
    public string? Name { get; set; }
    public string? Code { get; set; }
    public string? Description { get; set; }
    public string? ContactEmail { get; set; }
    public string? ContactPhone { get; set; }
    public TenantPlan? Plan { get; set; }
    public int? MaxAgents { get; set; }
    public bool? IsActive { get; set; }
}

/// <summary>
/// Request model for creating a new agent
/// </summary>
public class CreateAgentRequest
{
    public string MachineName { get; set; } = string.Empty;
    public string? Domain { get; set; }
    public string? OperatingSystem { get; set; }
    public string? OSVersion { get; set; }
    public string? Architecture { get; set; }
    public long? TotalMemoryMB { get; set; }
    public int? ProcessorCores { get; set; }
    public string? ProcessorName { get; set; }
    public string? IpAddress { get; set; }
    public string? MacAddress { get; set; }
    public Guid? TenantId { get; set; }
    public string? GroupName { get; set; }
    public string? Version { get; set; }
    public Dictionary<string, object>? Tags { get; set; }
    public Dictionary<string, object>? CustomProperties { get; set; }
}

/// <summary>
/// Request model for updating an agent
/// </summary>
public class UpdateAgentRequest
{
    public string? MachineName { get; set; }
    public string? Domain { get; set; }
    public string? OperatingSystem { get; set; }
    public string? OSVersion { get; set; }
    public string? Architecture { get; set; }
    public long? TotalMemoryMB { get; set; }
    public int? ProcessorCores { get; set; }
    public string? ProcessorName { get; set; }
    public string? IpAddress { get; set; }
    public string? MacAddress { get; set; }
    public Guid? TenantId { get; set; }
    public string? GroupName { get; set; }
    public string? Version { get; set; }
    public AgentStatus? Status { get; set; }
    public Dictionary<string, object>? Tags { get; set; }
    public Dictionary<string, object>? CustomProperties { get; set; }
}

/// <summary>
/// Request model for updating agent status
/// </summary>
public class UpdateAgentStatusRequest
{
    public AgentStatus Status { get; set; } = AgentStatus.Online;
}

/// <summary>
/// Response model for paginated results
/// </summary>
public class PaginatedResponse<T>
{
    public IEnumerable<T> Items { get; set; } = new List<T>();
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalCount { get; set; }
    public int TotalPages { get; set; }
}

/// <summary>
/// Data transfer object for tenant information
/// </summary>
public class TenantDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Code { get; set; }
    public string? Description { get; set; }
    public Guid? ParentTenantId { get; set; }
    public string? TenantType { get; set; }
    public string? ContactEmail { get; set; }
    public string? ContactPhone { get; set; }
    public TenantPlan Plan { get; set; }
    public int MaxAgents { get; set; }
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public int AgentCount { get; set; }
    public List<TenantDto> ChildTenants { get; set; } = new();
}

/// <summary>
/// Data transfer object for agent/device information
/// </summary>
public class AgentDto
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string MachineName { get; set; } = string.Empty;
    public string? Domain { get; set; }
    public string OperatingSystem { get; set; } = string.Empty;
    public string? OSVersion { get; set; }
    public string? Architecture { get; set; }
    public long? TotalMemoryMB { get; set; }
    public int? ProcessorCores { get; set; }
    public string? ProcessorName { get; set; }
    public string? IpAddress { get; set; }
    public string? MacAddress { get; set; }
    public Guid? TenantId { get; set; }
    public string? TenantName { get; set; }
    public string? GroupName { get; set; }
    public string Version { get; set; } = string.Empty;
    public AgentStatus Status { get; set; }
    public bool IsOnline { get; set; }
    public DateTime? LastSeen { get; set; }
    public DateTime RegisteredAt { get; set; }
    public Dictionary<string, object> Tags { get; set; } = new();
    public Dictionary<string, object> CustomProperties { get; set; } = new();
}
