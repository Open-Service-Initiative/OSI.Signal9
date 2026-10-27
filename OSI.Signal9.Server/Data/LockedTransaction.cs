using Microsoft.EntityFrameworkCore;

namespace OSI.Signal9.Server.Data;

/// <summary>
/// The one way to run an explicit transaction. It runs inside the context's execution strategy, as Microsoft's
/// connection-resiliency guidance requires of a user-initiated transaction, so a transient failure retries it whole.
/// Each attempt starts from an empty change tracker, and an attempt that doesn't commit leaves nothing tracked to be
/// saved later in the request.
/// </summary>
public static class LockedTransaction
{
    /// <summary>
    /// Runs <paramref name="work"/> in one transaction holding the transaction-level advisory lock
    /// <paramref name="lockKey"/>, so transactions on the same key take turns across every replica.
    /// </summary>
    /// <inheritdoc cref="InTransactionAsync{T}" path="/param"/>
    public static Task<T> InLockedTransactionAsync<T>(this DbContext db, long lockKey, Func<Task<T>> work,
        Func<T, bool> commit, CancellationToken cancellationToken) =>
        db.InTransactionAsync(async () =>
        {
            await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({lockKey})", cancellationToken);
            return await work();
        }, commit, cancellationToken);

    /// <summary>Runs <paramref name="work"/> in one transaction under the advisory lock <paramref name="lockKey"/>, and commits it.</summary>
    public static Task InLockedTransactionAsync(this DbContext db, long lockKey, Func<Task> work,
        CancellationToken cancellationToken) =>
        db.InLockedTransactionAsync(lockKey, async () =>
        {
            await work();
            return true;
        }, _ => true, cancellationToken);

    /// <summary>
    /// Runs <paramref name="work"/> in one transaction, which takes any locks it needs itself (such as a row lock).
    /// </summary>
    /// <param name="work">The transaction's reads and changes. It may run more than once.</param>
    /// <param name="commit">
    /// Whether to save and commit, given what <paramref name="work"/> returned. Otherwise the transaction rolls back,
    /// with anything it already saved.
    /// </param>
    public static Task<T> InTransactionAsync<T>(this DbContext db, Func<Task<T>> work, Func<T, bool> commit,
        CancellationToken cancellationToken) =>
        db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            var committed = false;
            try
            {
                await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
                var result = await work();
                if (commit(result))
                {
                    await db.SaveChangesAsync(cancellationToken);
                    await transaction.CommitAsync(cancellationToken);
                    committed = true;
                }
                return result;
            }
            finally
            {
                if (!committed)
                    db.ChangeTracker.Clear();
            }
        });
}
