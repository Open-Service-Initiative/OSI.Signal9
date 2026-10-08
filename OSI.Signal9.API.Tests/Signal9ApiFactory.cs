using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using OSI.Signal9.API.Data;
using OSI.Signal9.Contracts.Agents;
using OSI.Signal9.Contracts.Serialization;
using OSI.Signal9.Contracts.Tenants;

namespace OSI.Signal9.API.Tests;

/// <summary>
/// Hosts the API in memory against a private SQLite database, with a controllable clock.
/// </summary>
public sealed class Signal9ApiFactory : WebApplicationFactory<Program>
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:signal9", "Server=unused;Database=unused");
        builder.UseSetting("Logging:LogLevel:Default", "Warning");
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<DbContextOptions<Signal9DbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<Signal9DbContext>>();
            _connection.Open();
            services.AddDbContext<Signal9DbContext>(options => options.UseSqlite(_connection));

            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Time);
        });
    }

    protected override void ConfigureClient(HttpClient client)
    {
        base.ConfigureClient(client);
        using var scope = Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<Signal9DbContext>().Database.EnsureCreated();
    }

    public async Task<TenantDto> CreateTenantAsync(HttpClient client, string code, int maxAgents = 25)
    {
        var response = await client.PostAsJsonAsync("api/tenants",
            new CreateTenantRequest { Code = code, Name = code.ToUpperInvariant(), ContactEmail = $"it@{code}.example", MaxAgents = maxAgents },
            Signal9Json.Options);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<TenantDto>(Signal9Json.Options))!;
    }

    public static async Task<HttpResponseMessage> RegisterAsync(HttpClient client, string tenantCode, string machineName) =>
        await client.PostAsJsonAsync("api/agents",
            new RegisterAgentRequest { TenantCode = tenantCode, MachineName = machineName, OperatingSystem = "Windows 11 Pro", ProcessorCores = 8 },
            Signal9Json.Options);

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
            _connection.Dispose();
    }
}
