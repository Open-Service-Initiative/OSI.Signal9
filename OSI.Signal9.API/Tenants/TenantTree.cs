using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OSI.Signal9.API.Audit;
using OSI.Signal9.Contracts.Audit;
using OSI.Signal9.Server.Data;

namespace OSI.Signal9.API.Tenants;

public sealed class TenantTreeOptions
{
    public const string SectionName = "TenantTree";

    /// <summary>
    /// The deepest a tenant may sit below its root. Depth is zero-indexed: a root tenant has depth 0, and a
    /// tenant's depth is its closure-table distance from its root. Zero allows root tenants only (a flat deployment).
    /// </summary>
    [Range(0, 1000)]
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

    /// <summary>The check the caller ran alongside the change refused it.</summary>
    Refused,
}

/// <summary>
/// The only writer of the tenant tree. Each change runs in its own transaction, one at a time under a
/// PostgreSQL advisory lock, and updates the parent pointer and the closure table together (ADR-0001), with the audit
/// entry that records it.
/// </summary>
public sealed class TenantTree(Signal9DbContext db, CallerAudit audit, IOptions<TenantTreeOptions> options, TimeProvider time)
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
            Audit(AuditActions.TenantCreated, at: tenant.Id, tenant.Id);
            return TenantTreeError.None;
        }, cancellationToken);

    /// <summary>
    /// Whether <paramref name="tenantId"/> could move under <paramref name="newParentId"/> as the tree stands now, without
    /// taking the tree lock: for refusing a tenant transfer when it's proposed. <see cref="MoveAsync"/> checks again.
    /// </summary>
    public async Task<TenantTreeError> CheckMoveAsync(Guid tenantId, Guid newParentId, CancellationToken cancellationToken) =>
        (await PlanMoveAsync(tenantId, newParentId, cancellationToken)).Error;

    /// <summary>
    /// Moves a tenant, with its descendants, under a new parent tenant, rewriting only the subtree's closure rows. Only
    /// a tenant transfer moves tenants: <paramref name="alongside"/> runs in the same transaction once the move is known
    /// to keep the tree a tree within the maximum depth, makes the transfer's own checks and records it, including its
    /// audit entries. If it returns false, nothing changes and the result is <see cref="TenantTreeError.Refused"/>.
    /// </summary>
    public Task<TenantTreeError> MoveAsync(Guid tenantId, Guid newParentId, Func<Task<bool>> alongside,
        CancellationToken cancellationToken) =>
        ChangeAsync(async () =>
        {
            var plan = await PlanMoveAsync(tenantId, newParentId, cancellationToken);
            if (plan.Error != TenantTreeError.None)
                return plan.Error;
            if (!await alongside())
                return TenantTreeError.Refused;

            // Unlink the subtree from its old ancestors, then link it under the new parent's ancestors.
            var subtreeIds = plan.Subtree.Select(c => c.DescendantId).ToList();
            await db.TenantClosures
                .Where(c => subtreeIds.Contains(c.DescendantId) && !subtreeIds.Contains(c.AncestorId))
                .ExecuteDeleteAsync(cancellationToken);
            db.TenantClosures.AddRange(
                from a in plan.ParentAncestors
                from d in plan.Subtree
                select new TenantClosure { AncestorId = a.AncestorId, DescendantId = d.DescendantId, Depth = a.Depth + d.Depth + 1 });
            plan.Tenant!.ParentTenantId = newParentId;
            return TenantTreeError.None;
        }, cancellationToken);

    private sealed record MovePlan(TenantTreeError Error, Tenant? Tenant = null, List<TenantClosure>? ParentAncestorRows = null,
        List<TenantClosure>? SubtreeRows = null)
    {
        public List<TenantClosure> ParentAncestors => ParentAncestorRows ?? [];
        public List<TenantClosure> Subtree => SubtreeRows ?? [];
    }

    private async Task<MovePlan> PlanMoveAsync(Guid tenantId, Guid newParentId, CancellationToken cancellationToken)
    {
        // Read past the filter: the receiving side can't see the tenant until it has moved. The closure table isn't
        // filtered, so the checks below see the whole tree whoever asks.
        var tenant = await db.Tenants.IgnoreTenantScope(TenantScopeBypass.TenantTransferMove).SingleOrDefaultAsync(t => t.Id == tenantId, cancellationToken);
        if (tenant is null)
            return new MovePlan(TenantTreeError.TenantNotFound);
        var parentAncestors = await db.TenantClosures.Where(c => c.DescendantId == newParentId).ToListAsync(cancellationToken);
        if (parentAncestors.Count == 0)
            return new MovePlan(TenantTreeError.ParentNotFound);
        if (parentAncestors.Any(a => a.AncestorId == tenantId))
            return new MovePlan(TenantTreeError.Cycle);

        var subtree = await db.TenantClosures.Where(c => c.AncestorId == tenantId).ToListAsync(cancellationToken);
        // The subtree's deepest tenant would sit one below the new parent, plus its own height.
        if (parentAncestors.Max(c => c.Depth) + 1 + subtree.Max(c => c.Depth) > options.Value.MaxDepth)
            return new MovePlan(TenantTreeError.TooDeep);
        return new MovePlan(TenantTreeError.None, tenant, parentAncestors, subtree);
    }

    /// <summary>
    /// Removes a tenant that has no child tenants, leaving its anonymous record with the ancestors it has now (story 13).
    /// Its closure rows go with it. <paramref name="alongside"/> runs in the same transaction once the tenant is known to
    /// have no child tenants, and removes what the tenant owns, with its audit entries; if it returns false, nothing
    /// changes and the result is <see cref="TenantTreeError.Refused"/>.
    /// </summary>
    /// <param name="version">The <see cref="Tenant.Version"/> the caller read; if the row has changed since,
    /// the removal fails with <see cref="DbUpdateConcurrencyException"/>.</param>
    public Task<TenantTreeError> RemoveAsync(Guid tenantId, uint version, Func<Task<bool>> alongside,
        CancellationToken cancellationToken) =>
        ChangeAsync(async () =>
        {
            var tenant = await db.Tenants.SingleOrDefaultAsync(t => t.Id == tenantId, cancellationToken);
            if (tenant is null)
                return TenantTreeError.TenantNotFound;
            if (await db.TenantClosures.AnyAsync(c => c.AncestorId == tenant.Id && c.Depth > 0, cancellationToken))
                return TenantTreeError.HasChildTenants;
            if (!await alongside())
                return TenantTreeError.Refused;

            var ancestors = await db.TenantClosures.Where(c => c.DescendantId == tenant.Id && c.Depth > 0)
                .OrderBy(c => c.Depth).Select(c => c.AncestorId).ToArrayAsync(cancellationToken);
            db.DeletedTenants.Add(new DeletedTenant { Id = tenant.Id, AncestorIds = ancestors, DeletedAt = time.GetUtcNow().UtcDateTime });
            db.Entry(tenant).Property(t => t.Version).OriginalValue = version;
            db.Tenants.Remove(tenant);
            // At the parent, whose Administrators can still read it; a root's deletion is deployment-level.
            Audit(AuditActions.TenantDeleted, at: tenant.ParentTenantId, tenant.Id);
            return TenantTreeError.None;
        }, cancellationToken);

    private void Audit(string action, Guid? at, Guid tenantId, string? detail = null) =>
        audit.Record(new AuditEntry { Action = action, TenantId = at, TargetType = "tenant", TargetId = tenantId, Detail = detail });

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
            {
                // Nothing the refused change tracked may be saved later in the request.
                db.ChangeTracker.Clear();
                return error;
            }

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return TenantTreeError.None;
        });
    }
}
