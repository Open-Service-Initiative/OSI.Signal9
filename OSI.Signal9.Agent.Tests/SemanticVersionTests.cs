using OSI.Signal9.Contracts.Agents;

namespace OSI.Signal9.Agent.Tests;

/// <summary>The semantic versions agents send and api checks (SemVer 2.0.0).</summary>
[TestClass]
public sealed class SemanticVersionTests
{
    [TestMethod]
    public void Versions_order_by_semver_precedence()
    {
        // SemVer 2.0.0 §11's own example, in ascending order.
        string[] ordered =
            ["1.0.0-alpha", "1.0.0-alpha.1", "1.0.0-alpha.beta", "1.0.0-beta", "1.0.0-beta.2", "1.0.0-beta.11", "1.0.0-rc.1", "1.0.0", "1.0.1", "1.1.0", "2.0.0"];

        var versions = ordered.Select(v => SemanticVersion.Parse(v)).ToArray();

        for (var i = 1; i < versions.Length; i++)
            Assert.IsLessThan(0, versions[i - 1].CompareTo(versions[i]), $"{ordered[i - 1]} < {ordered[i]}");
    }

    [TestMethod]
    public void Build_metadata_is_ignored_for_precedence()
    {
        Assert.AreEqual(0, SemanticVersion.Parse("1.2.3+abc").CompareTo(SemanticVersion.Parse("1.2.3+def")));
        Assert.AreEqual("1.2.3-rc.1+abc", SemanticVersion.Parse("1.2.3-rc.1+abc").ToString());
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("1.2")]
    [DataRow("v1.2.3")]
    [DataRow("01.2.3")]
    [DataRow("1.2.3-")]
    [DataRow("1.2.3-01")]
    [DataRow("1.2.3-a..b")]
    [DataRow("1.2.3 ")]
    public void Malformed_versions_are_rejected(string value)
    {
        Assert.IsFalse(SemanticVersion.TryParse(value, out _));
    }
}
