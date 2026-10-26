using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using OSI.Signal9.Server.Data;

namespace OSI.Signal9.IntegrationTests;

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

    /// <summary>
    /// The audit log is append-only in the database itself (#115), so not even a bug or a direct connection can rewrite
    /// the trail. No API offers to change an entry, so this is checked at the database.
    /// </summary>
    [TestMethod]
    [DataRow("UPDATE \"AuditEntries\" SET \"Detail\" = 'rewritten'")]
    [DataRow("DELETE FROM \"AuditEntries\"")]
    [DataRow("TRUNCATE \"AuditEntries\"")]
    public async Task The_database_refuses_to_change_or_remove_audit_entries(string sql)
    {
        using var factory = new Signal9ApiFactory();
        using var client = factory.CreateSuperadministratorClient();
        await factory.CreateTenantAsync(client, "Contoso");
        await using var connection = new NpgsqlConnection(factory.ConnectionString);
        await connection.OpenAsync(TestContext.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);

        var refused = await Assert.ThrowsExactlyAsync<PostgresException>(() => command.ExecuteNonQueryAsync(TestContext.CancellationToken));

        Assert.AreEqual(PostgresErrorCodes.InsufficientPrivilege, refused.SqlState);
    }
}
