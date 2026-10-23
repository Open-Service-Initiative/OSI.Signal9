using System.ComponentModel.DataAnnotations;

namespace OSI.Signal9.Agent;

public sealed class AgentOptions
{
    public const string SectionName = "Agent";

    /// <summary>Base address of the Signal9 API, e.g. https://api.signal9.example.</summary>
    [Required]
    public Uri ApiBaseUrl { get; set; } = null!;

    /// <summary>Code of the tenant this machine enrolls into.</summary>
    [Required, MinLength(2)]
    public string TenantCode { get; set; } = string.Empty;

    [Range(typeof(TimeSpan), "00:00:05", "01:00:00")]
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(30);

    [Range(typeof(TimeSpan), "00:00:10", "01:00:00")]
    public TimeSpan TelemetryInterval { get; set; } = TimeSpan.FromMinutes(1);
}
