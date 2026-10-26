using System.Data.Common;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using OSI.Signal9.Contracts.Tenants;
using OSI.Signal9.Server.Data;

namespace OSI.Signal9.IntegrationTests;

/// <summary>
/// The one way to run a transaction under an advisory lock: inside the host's retrying execution strategy, each attempt
/// from an empty change tracker, and nothing tracked by an attempt that didn't commit is saved later.
/// </summary>
[TestClass]
public sealed class LockedTransactionTests : IDisposable
{
    private const long LockKey = 0x5167_6E39_5465_7374; // "Sig9Test"

    private readonly Signal9ApiFactory _factory = new();
    private readonly TestContext _testContext;

    public LockedTransactionTests(TestContext testContext) => _testContext = testContext;

    private CancellationToken Ct => _testContext.CancellationToken;

    [TestMethod]
    public async Task A_transient_failure_retries_the_whole_transaction_from_an_empty_change_tracker()
    {
        var failOnce = new FailFirstInsert("INSERT INTO \"Tenants\"");
        using var api = _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.ConfigureDbContext<Signal9DbContext>(options => options.AddInterceptors(failOnce))));
        await using var scope = api.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Signal9DbContext>();
        var tenant = Guid.NewGuid();
        var attempts = 0;

        var result = await db.InLockedTransactionAsync(LockKey, () =>
        {
            attempts++;
            db.Tenants.Add(NewTenant(tenant));
            return Task.FromResult("done");
        }, _ => true, Ct);

        Assert.AreEqual("done", result);
        Assert.AreEqual(2, attempts, "The transient failure retried the transaction.");
        Assert.IsTrue(failOnce.Failed);
        Assert.AreEqual(1L, await CountAsync(tenant));
    }

    [TestMethod]
    public async Task Only_what_the_transaction_tracks_is_saved()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Signal9DbContext>();
        var stray = Guid.NewGuid();
        var tenant = Guid.NewGuid();
        db.Tenants.Add(NewTenant(stray));

        await db.InLockedTransactionAsync(LockKey, () =>
        {
            db.Tenants.Add(NewTenant(tenant));
            return Task.FromResult(true);
        }, committed => committed, Ct);

        Assert.AreEqual(1L, await CountAsync(tenant));
        Assert.AreEqual(0L, await CountAsync(stray), "Something tracked before the transaction was saved with it.");
    }

    [TestMethod]
    public async Task A_declined_transaction_saves_nothing_and_leaves_nothing_tracked()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Signal9DbContext>();
        var tenant = Guid.NewGuid();

        var refused = await db.InLockedTransactionAsync(LockKey, async () =>
        {
            db.Tenants.Add(NewTenant(tenant));
            await db.SaveChangesAsync(Ct);
            db.Tenants.Add(NewTenant(Guid.NewGuid()));
            return "refused";
        }, outcome => outcome != "refused", Ct);
        await db.SaveChangesAsync(Ct);

        Assert.AreEqual("refused", refused);
        Assert.IsEmpty(db.ChangeTracker.Entries().ToList());
        Assert.AreEqual(0L, await CountAsync(tenant), "The declined transaction's saved changes were rolled back.");
    }

    [TestMethod]
    public async Task A_transaction_waits_for_its_lock()
    {
        await using var connection = new NpgsqlConnection(_factory.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var transaction = await connection.BeginTransactionAsync(Ct);
        await using (var takeLock = new NpgsqlCommand($"SELECT pg_advisory_xact_lock({LockKey})", connection, transaction))
            await takeLock.ExecuteNonQueryAsync(Ct);
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Signal9DbContext>();

        var locked = db.InLockedTransactionAsync(LockKey, () => Task.FromResult(true), _ => true, Ct);
        await Task.Delay(TimeSpan.FromMilliseconds(500), Ct);
        Assert.IsFalse(locked.IsCompleted, "The transaction ran while another held its lock.");

        await transaction.CommitAsync(Ct);
        Assert.IsTrue(await locked);
    }

    private static Tenant NewTenant(Guid id) =>
        new() { Id = id, Name = $"Tenant {id:N}", ContactEmail = "ops@example.com", Status = TenantStatus.Active };

    private async Task<long> CountAsync(Guid tenantId)
    {
        await using var connection = new NpgsqlConnection(_factory.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var count = new NpgsqlCommand("""SELECT count(*) FROM "Tenants" WHERE "Id" = @id""", connection);
        count.Parameters.AddWithValue("id", tenantId);
        return (long)(await count.ExecuteScalarAsync(Ct))!;
    }

    /// <summary>Fails the first command matching <paramref name="marker"/> as PostgreSQL does a serialization failure.</summary>
    private sealed class FailFirstInsert(string marker) : DbCommandInterceptor
    {
        private int _failed;

        public bool Failed => _failed == 1;

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default) =>
            command.CommandText.Contains(marker, StringComparison.Ordinal) && Interlocked.Exchange(ref _failed, 1) == 0
                ? throw new PostgresException("could not serialize access", "ERROR", "ERROR", PostgresErrorCodes.SerializationFailure)
                : ValueTask.FromResult(result);
    }

    public void Dispose() => _factory.Dispose();
}
