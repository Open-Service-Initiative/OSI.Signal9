using Microsoft.AspNetCore.Authentication.OpenIdConnect;
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
    /// <param name="entra">The stand-in for Entra that people sign in at, if not a new one.</param>
    public Signal9AuthFactory(string connectionString, FakeTimeProvider time, string? kekPath = null,
        IReadOnlyDictionary<string, string?>? settings = null, FakeEntra? entra = null)
    {
        Entra = entra ?? new FakeEntra(time);
        _settings = settings ?? new Dictionary<string, string?>();
        _connectionString = connectionString;
        _time = time;
        _kekPath = kekPath;
        ClientOptions.BaseAddress = Signal9ApiFactory.HttpsLocalhost;
        // Redirects (to Entra, to a client) are what tests check, so they aren't followed.
        ClientOptions.AllowAutoRedirect = false;
    }

    /// <summary>The stand-in for Entra that every outbound call of this host reaches.</summary>
    public FakeEntra Entra { get; }

    public const string Issuer = "https://auth.signal9.test/";
    public const string ApiUri = "https://api.signal9.test/";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // The operator's app registration, with a certificate credential as in local development (story 125).
        builder.UseSetting("Entra:ClientId", Entra.ClientId.ToString());
        builder.UseSetting("Entra:DeployingTenantId", Entra.DeployingTenantId.ToString());
        builder.UseSetting("Entra:ClientCredentials:0:SourceType", "Path");
        builder.UseSetting("Entra:ClientCredentials:0:CertificateDiskPath", Entra.CredentialPath);
        Signal9ApiFactory.ConfigureSharedHost(builder, _connectionString, _time, _settings, _kekPath);
        // Tests start the pruning job themselves, so it never runs in the middle of another test.
        builder.ConfigureTestServices(services =>
        {
            // Every outbound call (Microsoft.Identity.Web, MSAL and the OpenID Connect handler) reaches the fake Entra.
            services.ConfigureHttpClientDefaults(client => client.ConfigurePrimaryHttpMessageHandler(Entra.CreateHandler));
            services.Configure<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme,
                options => options.BackchannelHttpHandler = Entra.CreateHandler());

            foreach (var quartz in services.Where(IsQuartzHostedService).ToList())
                services.Remove(quartz);
        });
    }

    private static bool IsQuartzHostedService(ServiceDescriptor service) =>
        service.ServiceType == typeof(IHostedService)
        && service.ImplementationType?.Namespace?.StartsWith("Quartz", StringComparison.Ordinal) == true;
}
