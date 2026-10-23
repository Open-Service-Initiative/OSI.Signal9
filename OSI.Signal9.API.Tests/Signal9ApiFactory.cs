using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using OSI.Signal9.Contracts.Agents;
using OSI.Signal9.Contracts.Serialization;
using OSI.Signal9.Contracts.Tenants;

namespace OSI.Signal9.API.Tests;

/// <summary>
/// Hosts the API in memory against its own database on the shared PostgreSQL container, with a controllable clock.
/// </summary>
/// <param name="migrated">Whether the database starts with the schema applied, or empty.</param>
public sealed class Signal9ApiFactory(bool migrated = true) : WebApplicationFactory<Program>
{
    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));

    /// <summary>Connection string of this factory's database.</summary>
    public string ConnectionString { get; } = PostgresContainer.CreateDatabase(migrated);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:signal9", ConnectionString);
        builder.UseSetting("Logging:LogLevel:Default", "Warning");
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Time);
        });
    }

    public async Task<TenantDto> CreateTenantAsync(HttpClient client, string name, int maxAgents = 25)
    {
        var response = await client.PostAsJsonAsync("api/tenants",
            new CreateTenantRequest { Name = name, ContactEmail = $"it@{name.ToLowerInvariant()}.example", MaxAgents = maxAgents },
            Signal9Json.Options);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<TenantDto>(Signal9Json.Options))!;
    }

    public static async Task<HttpResponseMessage> RegisterAsync(HttpClient client, Guid tenantId, string machineName) =>
        await client.PostAsJsonAsync("api/agents",
            new RegisterAgentRequest { TenantId = tenantId, MachineName = machineName, OperatingSystem = "Windows 11 Pro", ProcessorCores = 8 },
            Signal9Json.Options);
}
