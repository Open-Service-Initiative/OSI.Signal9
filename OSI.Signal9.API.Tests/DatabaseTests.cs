using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OSI.Signal9.Server.Data;

namespace OSI.Signal9.API.Tests;

[TestClass]
public sealed class DatabaseTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void Migrations_match_the_model()
    {
        using var factory = new Signal9ApiFactory();
        using var scope = factory.Services.CreateScope();

        Assert.IsFalse(scope.ServiceProvider.GetRequiredService<Signal9DbContext>().Database.HasPendingModelChanges());
    }

    [TestMethod]
    [DataRow("Development")]
    [DataRow("Production")]
    public async Task Startup_never_applies_migrations(string environment)
    {
        using var factory = new Signal9ApiFactory(migrated: false);
        using var host = factory.WithWebHostBuilder(builder => builder.UseEnvironment(environment));
        using var client = host.CreateClient();
        await client.GetAsync("health", TestContext.CancellationToken);

        using var scope = host.Services.CreateScope();
        var applied = await scope.ServiceProvider.GetRequiredService<Signal9DbContext>().Database
            .GetAppliedMigrationsAsync(TestContext.CancellationToken);

        Assert.IsEmpty(applied);
    }
}
