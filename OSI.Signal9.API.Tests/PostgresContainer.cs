using Microsoft.EntityFrameworkCore;
using Npgsql;
using OSI.Signal9.Server.Data;
using Testcontainers.PostgreSql;

namespace OSI.Signal9.API.Tests;

/// <summary>
/// One PostgreSQL container shared by every test in the run. Each <see cref="Signal9ApiFactory"/> gets its
/// own database, cloned from a template that the migrations were applied to once.
/// </summary>
[TestClass]
public static class PostgresContainer
{
    private const string TemplateDatabase = "signal9_template";

    private static PostgreSqlContainer? _container;

    [AssemblyInitialize]
    public static async Task StartAsync(TestContext testContext)
    {
        _container = new PostgreSqlBuilder("postgres:18").Build();
        await _container.StartAsync(testContext.CancellationToken);

        await ExecuteAsync($"CREATE DATABASE {TemplateDatabase}", testContext.CancellationToken);
        var options = new DbContextOptionsBuilder<Signal9DbContext>().UseNpgsql(ConnectionStringFor(TemplateDatabase)).Options;
        await using (var db = new Signal9DbContext(options))
            await db.Database.MigrateAsync(testContext.CancellationToken);

        // A template database can't be copied while anyone is connected to it.
        NpgsqlConnection.ClearAllPools();
    }

    [AssemblyCleanup]
    public static async Task StopAsync()
    {
        if (_container is not null)
            await _container.DisposeAsync();
    }

    /// <summary>Creates a new database, with the schema applied or empty, and returns its connection string.</summary>
    public static string CreateDatabase(bool migrated)
    {
        var name = $"test_{Guid.NewGuid():N}";
        ExecuteAsync(migrated ? $"CREATE DATABASE {name} TEMPLATE {TemplateDatabase}" : $"CREATE DATABASE {name}",
            CancellationToken.None).GetAwaiter().GetResult();
        return ConnectionStringFor(name);
    }

    private static string ConnectionStringFor(string database) =>
        new NpgsqlConnectionStringBuilder(Container.GetConnectionString()) { Database = database }.ConnectionString;

    private static async Task ExecuteAsync(string sql, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(Container.GetConnectionString());
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static PostgreSqlContainer Container =>
        _container ?? throw new InvalidOperationException("The PostgreSQL container hasn't started.");
}
