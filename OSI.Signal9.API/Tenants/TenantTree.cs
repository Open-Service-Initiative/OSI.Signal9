using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OSI.Signal9.API.Data;

namespace OSI.Signal9.API.Tenants;

public sealed class TenantTreeOptions
{
    public const string SectionName = "TenantTree";

    /// <summary>
    /// The deepest a tenant may sit below its root. Depth is zero-indexed: a root tenant has depth 0, and a
    /// tenant's depth is its closure-table distance from its root.
    /// </summary>
    [Range(1, 1000)]
    public int MaxDepth { get; set; } = 20;
}

public enum TenantTreeError
{
    None,
    ParentNotFound,
    TenantNotFound,
    TooDeep,
    Cycle,
    HasChildTenants,
}

/// <summary>
/// The only writer of the tenant tree. Each change runs in its own transaction, one at a time under a
/// PostgreSQL advisory lock, and updates the parent pointer and the closure table together (ADR-0001).
/// </summary>
public sealed class TenantTree(Signal9DbContext db, IOptions<TenantTreeOptions> options)
{
    /// <summary>Key of the transaction-level advisory lock every tree change holds.</summary>
    public const long LockKey = 0x5167_6E39_5472_6565; // "Sig9Tree"

    /// <summary>Adds <paramref name="tenant"/> under its <see cref="Tenant.ParentTenantId"/>, or as a root.</summary>
    public Task<TenantTreeError> AddAsync(Tenant tenant, CancellationToken cancellationToken) =>
        ChangeAsync(async () =>
        {
            List<TenantClosure> parentAncestors = [];
            if (tenant.ParentTenantId is { } parentId)
            {
                parentAncestors = await db.TenantClosures.Where(c => c.DescendantId == parentId).ToListAsync(cancellationToken);
                if (parentAncestors.Count == 0)
                    return TenantTreeError.ParentNotFound;
                if (parentAncestors.Max(c => c.Depth) + 1 > options.Value.MaxDepth)
                    return TenantTreeError.TooDeep;
            }

            db.Tenants.Add(tenant);
            db.TenantClosures.Add(new TenantClosure { AncestorId = tenant.Id, DescendantId = tenant.Id, Depth = 0 });
            db.TenantClosures.AddRange(parentAncestors.Select(a =>
                new TenantClosure { AncestorId = a.AncestorId, DescendantId = tenant.Id, Depth = a.Depth + 1 }));
            return TenantTreeError.None;
        }, cancellationToken);

    /// <summary>Moves a tenant, with its descendants, under a new parent tenant.</summary>
    public Task<TenantTreeError> MoveAsync(Guid tenantId, Guid newParentId, CancellationToken cancellationToken) =>
        ChangeAsync(async () =>
        {
            var tenant = await db.Tenants.SingleOrDefaultAsync(t => t.Id == tenantId, cancellationToken);
            if (tenant is null)
                return TenantTreeError.TenantNotFound;
            var parentAncestors = await db.TenantClosures.Where(c => c.DescendantId == newParentId).ToListAsync(cancellationToken);
            if (parentAncestors.Count == 0)
                return TenantTreeError.ParentNotFound;
            if (parentAncestors.Any(a => a.AncestorId == tenantId))
                return TenantTreeError.Cycle;

            var subtree = await db.TenantClosures.Where(c => c.AncestorId == tenantId).ToListAsync(cancellationToken);
            // The subtree's deepest tenant would sit one below the new parent, plus its own height.
            if (parentAncestors.Max(c => c.Depth) + 1 + subtree.Max(c => c.Depth) > options.Value.MaxDepth)
                return TenantTreeError.TooDeep;

            // Unlink the subtree from its old ancestors, then link it under the new parent's ancestors.
            var subtreeIds = subtree.Select(c => c.DescendantId).ToList();
            await db.TenantClosures
                .Where(c => subtreeIds.Contains(c.DescendantId) && !subtreeIds.Contains(c.AncestorId))
                .ExecuteDeleteAsync(cancellationToken);
            db.TenantClosures.AddRange(
                from a in parentAncestors
                from d in subtree
                select new TenantClosure { AncestorId = a.AncestorId, DescendantId = d.DescendantId, Depth = a.Depth + d.Depth + 1 });
            tenant.ParentTenantId = newParentId;
            return TenantTreeError.None;
        }, cancellationToken);

    /// <summary>Removes a tenant that has no child tenants. Its closure rows go with it.</summary>
    public Task<TenantTreeError> RemoveAsync(Guid tenantId, CancellationToken cancellationToken) =>
        ChangeAsync(async () =>
        {
            var tenant = await db.Tenants.SingleOrDefaultAsync(t => t.Id == tenantId, cancellationToken);
            if (tenant is null)
                return TenantTreeError.TenantNotFound;
            if (await db.Tenants.AnyAsync(t => t.ParentTenantId == tenant.Id, cancellationToken))
                return TenantTreeError.HasChildTenants;
            db.Tenants.Remove(tenant);
            return TenantTreeError.None;
        }, cancellationToken);

    // Runs inside the execution strategy, because Aspire's retrying strategy refuses a user-started transaction
    // outside it. A retry starts from a clean change tracker, so it doesn't carry entities from the failed attempt.
    private Task<TenantTreeError> ChangeAsync(Func<Task<TenantTreeError>> change, CancellationToken cancellationToken)
    {
        var attempt = 0;
        return db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            if (attempt++ > 0)
                db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({LockKey})", cancellationToken);

            var error = await change();
            if (error != TenantTreeError.None)
                return error;

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return TenantTreeError.None;
        });
    }
}
