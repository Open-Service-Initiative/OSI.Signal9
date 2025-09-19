using System.ComponentModel.DataAnnotations;

namespace Signal9.Shared.DTOs.Core;

/// <summary>
/// Configuration that an agent receives after successful registration/handshake.
/// Designed for Azure Functions isolated worker (no ASP.NET dependency).
/// </summary>
public record AgentConfigurationResponse
{
    [Required]
    public Guid AgentId { get; init; }

    [Required]
    [StringLength(500, MinimumLength = 32)]
    public required string ApiKey { get; init; }

    [Required]
    [StringLength(500, MinimumLength = 10)]
    [Url]
    public required string HubUrl { get; init; }

    [Required]
    public required TimeSpan ReportingInterval { get; init; }

    [Required]
    [StringLength(128, MinimumLength = 32)]
    [RegularExpression("^[a-fA-F0-9]+$")]
    public required string ConfigurationHash { get; init; }
}
