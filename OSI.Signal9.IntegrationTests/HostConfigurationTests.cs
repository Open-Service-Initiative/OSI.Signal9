namespace OSI.Signal9.IntegrationTests;

/// <summary>
/// The hosts' topology comes only from the AppHost (or this fixture): without it a host refuses to start, naming the
/// setting, rather than falling back to a default that may be wrong.
/// </summary>
[TestClass]
public sealed class HostConfigurationTests
{
    [TestMethod]
    [DataRow("auth", "Identity:Issuer")]
    [DataRow("api", "Identity:Issuer")]
    [DataRow("web", "Identity:Issuer")]
    [DataRow("auth", "Identity:ApiUri")]
    [DataRow("api", "Identity:ApiUri")]
    [DataRow("web", "Identity:ApiUri")]
    [DataRow("api", "Realtime:Hosting")]
    [DataRow("web", "Realtime:Hosting")]
    public void A_host_without_a_setting_the_AppHost_provides_fails_at_startup_saying_so(string host, string setting)
    {
        // An empty value stands in for a missing one: configuration can't remove what the fixture set.
        using var factory = new Signal9ApiFactory(migrated: false, settings: new Dictionary<string, string?> { [setting] = "" });

        var failure = Assert.Throws<Exception>(() => _ = host switch
        {
            "auth" => factory.Auth.Server,
            "web" => factory.Web.Server,
            _ => factory.Server,
        });

        StringAssert.Contains(Flatten(failure), $"{setting} is required");
    }

    private static string Flatten(Exception exception) =>
        exception.InnerException is null ? exception.Message : exception.Message + " | " + Flatten(exception.InnerException);
}
