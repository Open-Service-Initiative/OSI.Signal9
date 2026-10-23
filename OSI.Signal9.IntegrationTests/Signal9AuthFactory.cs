using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using OSI.Signal9.Auth;

namespace OSI.Signal9.IntegrationTests;

/// <summary>Hosts <c>auth</c> in memory, sharing the database and clock of the <see cref="Signal9ApiFactory"/> that owns it.</summary>
public sealed class Signal9AuthFactory : WebApplicationFactory<AuthHost>
{
    private readonly string _connectionString;
    private readonly FakeTimeProvider _time;
    private readonly string? _kekPath;
    private readonly IReadOnlyDictionary<string, string?> _settings;

    /// <param name="kekPath">The key-encryption key certificate, if not the one the test run shares.</param>
    /// <param name="settings">Configuration on top of the fixture's own.</param>
    public Signal9AuthFactory(string connectionString, FakeTimeProvider time, string? kekPath = null,
        IReadOnlyDictionary<string, string?>? settings = null)
    {
        _settings = settings ?? new Dictionary<string, string?>();
        _connectionString = connectionString;
        _time = time;
        _kekPath = kekPath;
        ClientOptions.BaseAddress = Signal9ApiFactory.HttpsLocalhost;
    }

    public const string Issuer = "https://auth.signal9.test/";
    public const string ApiUri = "https://api.signal9.test/";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Signal9ApiFactory.ConfigureSharedHost(builder, _connectionString, _time, _settings, _kekPath);
        // Tests start the pruning job themselves, so it never runs in the middle of another test.
        builder.ConfigureTestServices(services =>
        {
            foreach (var quartz in services.Where(IsQuartzHostedService).ToList())
                services.Remove(quartz);
        });
    }

    private static bool IsQuartzHostedService(ServiceDescriptor service) =>
        service.ServiceType == typeof(IHostedService)
        && service.ImplementationType?.Namespace?.StartsWith("Quartz", StringComparison.Ordinal) == true;
}
