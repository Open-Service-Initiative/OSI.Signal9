using System.Globalization;
using System.Text.RegularExpressions;

namespace OSI.Signal9.Contracts.Agents;

/// <summary>A semantic version (SemVer 2.0.0), as agents report theirs. Build metadata plays no part in precedence.</summary>
public sealed partial record SemanticVersion : IComparable<SemanticVersion>
{
    private SemanticVersion(ulong major, ulong minor, ulong patch, string[] prerelease, string? build)
    {
        (Major, Minor, Patch, Prerelease, Build) = (major, minor, patch, prerelease, build);
    }

    public ulong Major { get; }
    public ulong Minor { get; }
    public ulong Patch { get; }

    /// <summary>The pre-release identifiers, if any.</summary>
    public IReadOnlyList<string> Prerelease { get; }

    public string? Build { get; }

    public static SemanticVersion Parse(string value) =>
        TryParse(value, out var version) ? version : throw new FormatException($"'{value}' is not a semantic version.");

    public static bool TryParse(string? value, out SemanticVersion version)
    {
        version = null!;
        var match = value is null ? null : Grammar().Match(value);
        if (match is not { Success: true }
            || !ulong.TryParse(match.Groups["major"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var major)
            || !ulong.TryParse(match.Groups["minor"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var minor)
            || !ulong.TryParse(match.Groups["patch"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var patch))
            return false;
        var prerelease = match.Groups["pre"].Success ? match.Groups["pre"].Value.Split('.') : [];
        version = new SemanticVersion(major, minor, patch, prerelease, match.Groups["build"].Success ? match.Groups["build"].Value : null);
        return true;
    }

    /// <summary>Precedence (SemVer 2.0.0 §11).</summary>
    public int CompareTo(SemanticVersion? other)
    {
        if (other is null)
            return 1;
        var result = (Major, Minor, Patch).CompareTo((other.Major, other.Minor, other.Patch));
        if (result != 0)
            return result;
        // A pre-release precedes its normal version.
        if (Prerelease.Count == 0 || other.Prerelease.Count == 0)
            return other.Prerelease.Count.CompareTo(Prerelease.Count);
        for (var i = 0; i < Math.Min(Prerelease.Count, other.Prerelease.Count); i++)
        {
            result = CompareIdentifiers(Prerelease[i], other.Prerelease[i]);
            if (result != 0)
                return result;
        }
        return Prerelease.Count.CompareTo(other.Prerelease.Count);
    }

    /// <summary>Numeric identifiers compare numerically and precede alphanumeric ones, which compare in ASCII order.</summary>
    private static int CompareIdentifiers(string left, string right)
    {
        var leftNumeric = left.All(char.IsAsciiDigit);
        var rightNumeric = right.All(char.IsAsciiDigit);
        return (leftNumeric, rightNumeric) switch
        {
            (true, true) => left.Length != right.Length ? left.Length.CompareTo(right.Length) : string.CompareOrdinal(left, right),
            (true, false) => -1,
            (false, true) => 1,
            _ => string.CompareOrdinal(left, right),
        };
    }

    public bool Equals(SemanticVersion? other) => other is not null && CompareTo(other) == 0;

    public override int GetHashCode() => HashCode.Combine(Major, Minor, Patch, string.Join('.', Prerelease));

    public override string ToString() =>
        $"{Major}.{Minor}.{Patch}{(Prerelease.Count > 0 ? "-" + string.Join('.', Prerelease) : "")}{(Build is null ? "" : "+" + Build)}";

    public static bool operator <(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) < 0;
    public static bool operator >(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) > 0;
    public static bool operator <=(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) <= 0;
    public static bool operator >=(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) >= 0;

    // SemVer 2.0.0's grammar (semver.org's recommended expression).
    [GeneratedRegex(@"^(?<major>0|[1-9]\d*)\.(?<minor>0|[1-9]\d*)\.(?<patch>0|[1-9]\d*)(?:-(?<pre>(?:0|[1-9]\d*|\d*[a-zA-Z-][0-9a-zA-Z-]*)(?:\.(?:0|[1-9]\d*|\d*[a-zA-Z-][0-9a-zA-Z-]*))*))?(?:\+(?<build>[0-9a-zA-Z-]+(?:\.[0-9a-zA-Z-]+)*))?\z", RegexOptions.CultureInvariant)]
    private static partial Regex Grammar();
}
