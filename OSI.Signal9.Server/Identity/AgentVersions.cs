using System.ComponentModel.DataAnnotations;
using System.Reflection;
using Microsoft.Extensions.Options;
using OSI.Signal9.Contracts.Agents;

namespace OSI.Signal9.Server.Identity;

/// <summary>
/// Which agent versions the deployment supports (<c>AgentVersions</c>), inclusive. By default, agents of this build's
/// major and minor version, up to this build: agent and api versions are paired, and api is updated first.
/// </summary>
public sealed class AgentVersionOptions : IValidatableObject
{
    public const string SectionName = "AgentVersions";

    /// <summary>The oldest supported agent version; this build's <c>major.minor.0</c> by default.</summary>
    public string? Minimum { get; set; }

    /// <summary>The newest supported agent version; this build's by default.</summary>
    public string? Maximum { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Minimum is not null && !SemanticVersion.TryParse(Minimum, out _))
            yield return new ValidationResult("Not a semantic version.", [nameof(Minimum)]);
        else if (Maximum is not null && !SemanticVersion.TryParse(Maximum, out _))
            yield return new ValidationResult("Not a semantic version.", [nameof(Maximum)]);
        else if (Range() is var (minimum, maximum) && minimum > maximum)
            yield return new ValidationResult($"The minimum agent version {minimum} is newer than the maximum {maximum}.",
                [nameof(Minimum), nameof(Maximum)]);
    }

    /// <summary>The supported range, with this build's defaults for what isn't set.</summary>
    internal (SemanticVersion Minimum, SemanticVersion Maximum) Range()
    {
        var build = AgentVersions.Build;
        var defaultMinimum = SemanticVersion.Parse($"{build.Major}.{build.Minor}.0");
        return (
            Minimum is { } minimum ? SemanticVersion.Parse(minimum) : defaultMinimum < build ? defaultMinimum : build,
            Maximum is { } maximum ? SemanticVersion.Parse(maximum) : build);
    }
}

/// <summary>Judges the version an agent names in <see cref="AgentProtocol.VersionHeader"/> against the supported range.</summary>
public sealed class AgentVersions
{
    /// <summary>This build's version, which agents built with it share.</summary>
    public static SemanticVersion Build { get; } = SemanticVersion.Parse(
        typeof(AgentVersions).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0]);

    public AgentVersions(IOptions<AgentVersionOptions> options)
    {
        (Minimum, Maximum) = options.Value.Range();
    }

    public SemanticVersion Minimum { get; }

    public SemanticVersion Maximum { get; }

    /// <summary>The version <paramref name="header"/> names, if it's a supported one; null if it isn't, or names none.</summary>
    public SemanticVersion? Supported(string? header) =>
        SemanticVersion.TryParse(header, out var version) && version >= Minimum && version <= Maximum ? version : null;

    /// <summary>Why the agent naming <paramref name="header"/> is refused.</summary>
    public string Refusal(string? header) =>
        $"{(string.IsNullOrEmpty(header) ? "The agent named no version" : $"Agent version '{header}' is not supported")}; this deployment supports agents from {Minimum} to {Maximum}. Update the agent.";
}
