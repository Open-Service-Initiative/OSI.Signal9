using System.ComponentModel.DataAnnotations;

namespace OSI.Signal9.Agent;

/// <summary>
/// One agent instance's configuration. A machine runs one instance per deployment and root tenant tree, each with its
/// own <see cref="InstanceId"/>: its key, certificate and state are kept apart by it.
/// </summary>
public sealed class AgentOptions
{
    public const string SectionName = "Agent";

    /// <summary>Base address of the deployment's api host, e.g. https://api.signal9.example.</summary>
    [Required]
    public Uri ApiBaseUrl { get; set; } = null!;

    /// <summary>The enrollment token the instance enrolls with. Needed only until it has enrolled.</summary>
    public string? EnrollmentToken { get; set; }

    /// <summary>
    /// Names this instance among the machine's agent instances, one per deployment and root tenant tree: its key and
    /// state are stored under this name.
    /// </summary>
    [Required, RegularExpression("^[A-Za-z0-9][A-Za-z0-9-]{0,62}$")]
    public string InstanceId { get; set; } = "default";

    /// <summary>
    /// Where instances keep their state, each in a folder named after it. By default, the platform's: ProgramData on
    /// Windows, <c>/var/lib/signal9-agent</c> on Linux, <c>/Library/Application Support/Signal9 Agent</c> on macOS.
    /// </summary>
    [Required]
    public string? StateDirectory { get; set; }

    [Range(typeof(TimeSpan), "00:00:05", "01:00:00")]
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(30);

    [Range(typeof(TimeSpan), "00:00:10", "01:00:00")]
    public TimeSpan TelemetryInterval { get; set; } = TimeSpan.FromMinutes(1);
}
