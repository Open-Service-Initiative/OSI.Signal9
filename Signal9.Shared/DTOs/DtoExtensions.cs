using Signal9.Shared.DTOs.Core;
using Signal9.Shared.Models;

namespace Signal9.Shared.DTOs;

/// <summary>
/// Agent Registration Response DTO
/// </summary>
public record AgentRegistrationResponse
{
    /// <summary>
    /// Parent ID (tenant) that the agent was registered under
    /// </summary>
    public Guid? ParentId { get; init; }
    
    /// <summary>
    /// The registered agent response
    /// </summary>
    public AgentResponse? Agent { get; init; }
    
    /// <summary>
    /// Configuration data for the agent
    /// </summary>
    public object? Configuration { get; init; }
    
    /// <summary>
    /// Registration status message
    /// </summary>
    public string? RegistrationStatus { get; init; }
    
    /// <summary>
    /// Additional message
    /// </summary>
    public string? Message { get; init; }
}