using System.ComponentModel.DataAnnotations;
using Signal9.Shared.DTOs.Base;
using Signal9.Shared.DTOs.Core;
using Signal9.Shared.Models;

namespace Signal9.Shared.DTOs.Common;

/// <summary>
/// Modern Agent Response DTO - Clean Entity Framework integration
/// </summary>
public record AgentResponse : BaseDto<Guid>
{
    /// <summary>
    /// The agent data
    /// </summary>
    public AgentDto? Agent { get; init; }
    
    /// <summary>
    /// Tenant name for display
    /// </summary>
    public string? TenantName { get; init; }
    
    /// <summary>
    /// Group name for organization
    /// </summary>
    public string? GroupName { get; init; }
    
    /// <summary>
    /// Whether the agent is online (computed from status)
    /// </summary>
    public bool IsOnline => Agent?.Status == AgentStatus.Online;
    
    /// <summary>
    /// Additional metadata
    /// </summary>
    public Dictionary<string, object> Metadata { get; init; } = [];

    /// <summary>
    /// Factory method from AgentDto
    /// </summary>
    public static AgentResponse FromAgentDto(AgentDto agent, string? tenantName = null, string? groupName = null)
    {
        return new AgentResponse
        {
            Id = Guid.NewGuid(),
            Agent = agent,
            TenantName = tenantName,
            GroupName = groupName
        };
    }
}

/// <summary>
/// Modern System Information DTO - Clean agent data collection
/// Follows C# naming conventions and modern patterns
/// </summary>
public record SystemInfo
{
    public string MachineName { get; init; } = string.Empty;
    public string OperatingSystem { get; init; } = string.Empty;
    public string? OsVersion { get; init; }
    public string? Architecture { get; init; }
    public string? IpAddress { get; init; }
    public string? MacAddress { get; init; }
    public long TotalMemoryMb { get; init; }
    public int ProcessorCores { get; init; }
    public string? ProcessorName { get; init; }
    public string? Domain { get; init; }
    
    // Compatibility properties for existing SystemInfoProvider
    public int ProcessorCount => ProcessorCores;
    public string? UserName { get; init; }
    public long TotalMemory => TotalMemoryMb * 1024 * 1024; // Convert MB to bytes
    public long AvailableMemory { get; init; }
    public TimeSpan Uptime { get; init; }
    public Dictionary<string, object> Drives { get; init; } = [];
}
