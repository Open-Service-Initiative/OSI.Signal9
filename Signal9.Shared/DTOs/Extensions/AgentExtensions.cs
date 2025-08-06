using Signal9.Shared.DTOs.Common;
using Signal9.Shared.DTOs.Core;
using Signal9.Shared.Models;

namespace Signal9.Shared.DTOs.Extensions;

/// <summary>
/// Extension methods for Agent DTOs to support mapping and conversion operations
/// </summary>
public static class AgentExtensions
{
    /// <summary>
    /// Convert AgentRegistrationRequest to AgentDto
    /// </summary>
    public static AgentDto ToAgentDto(this AgentRegistrationRequest request)
    {
        return new AgentDto
        {
            Id = Guid.NewGuid(),
            ParentId = request.ParentId,
            MachineName = request.MachineName,
            Domain = request.Domain,
            OperatingSystem = request.OperatingSystem,
            OSVersion = request.OSVersion,
            Architecture = request.Architecture,
            TotalMemoryMB = request.TotalMemoryMB,
            ProcessorCores = request.ProcessorCores,
            ProcessorName = request.ProcessorName,
            IpAddress = request.IpAddress ?? "Unknown",
            MacAddress = request.MacAddress,
            FirstSeen = DateTime.UtcNow,
            LastSeen = request.LastSeen,
            Status = request.IsOnline ? AgentStatus.Online : AgentStatus.Offline,
            Version = request.Version,
            Tags = null
        };
    }

    /// <summary>
    /// Create AgentResponse from AgentDto
    /// </summary>
    public static AgentResponse CreateAgentResponse(this AgentDto agent, string? tenantName = null, string? groupName = null)
    {
        return AgentResponse.FromAgentDto(agent, tenantName, groupName);
    }
}