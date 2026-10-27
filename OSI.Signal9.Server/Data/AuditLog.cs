using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OSI.Signal9.Contracts.Audit;

namespace OSI.Signal9.Server.Data;

/// <summary>
/// Writes the append-only audit log (#115), for auth and api alike. An entry recording a change is added to the same
/// unit of work as the change (<see cref="Add"/>), so the two are saved together or not at all; an entry about
/// something that changed nothing, such as a refusal, is written on its own (<see cref="WriteAsync"/>).
/// </summary>
public sealed class AuditLog(Signal9DbContext db, IServiceScopeFactory scopes, TimeProvider time)
{
    /// <summary>How many entries this scope (a request) has recorded.</summary>
    public int Recorded { get; private set; }

    /// <summary>Adds <paramref name="entry"/> to the current unit of work, saved with the change it records.</summary>
    public AuditEntry Add(AuditEntry entry)
    {
        db.AuditEntries.Add(Stamp(entry));
        Recorded++;
        return entry;
    }

    /// <summary>Writes <paramref name="entry"/> now, whatever becomes of the current unit of work.</summary>
    public async Task WriteAsync(AuditEntry entry, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var own = scope.ServiceProvider.GetRequiredService<Signal9DbContext>();
        own.AuditEntries.Add(Stamp(entry));
        await own.SaveChangesAsync(cancellationToken);
        Recorded++;
    }

    /// <summary>
    /// Lifts the tenant filter for an <see cref="AuditedAttribute">audited</see> <paramref name="reason"/>, recording the
    /// use as a deployment-level entry (story 70).
    /// </summary>
    public async Task<IQueryable<T>> IgnoreTenantScopeAsync<T>(IQueryable<T> source, TenantScopeBypass reason,
        CancellationToken cancellationToken) where T : class
    {
        await WriteAsync(new AuditEntry { Action = AuditActions.TenantScopeBypassed, Detail = reason.ToString() }, cancellationToken);
        return TenantScope.Bypass(source, reason);
    }

    // Placement: which tenant an entry belongs at. Each lookup reads only a tenant ID and never answers the caller.

    /// <summary>The tenant, if it exists.</summary>
    public async Task<Guid?> ExistingTenantAsync(Guid tenantId, CancellationToken cancellationToken) =>
        await db.Tenants.AsNoTracking().IgnoreTenantScope(TenantScopeBypass.AuditPlacement)
            .AnyAsync(t => t.Id == tenantId, cancellationToken) ? tenantId : null;

    /// <summary>The tenant that owns the device, if it exists.</summary>
    public Task<Guid?> TenantOfDeviceAsync(Guid deviceId, CancellationToken cancellationToken) =>
        db.Devices.AsNoTracking().IgnoreTenantScope(TenantScopeBypass.AuditPlacement)
            .Where(d => d.Id == deviceId).Select(d => (Guid?)d.TenantId).SingleOrDefaultAsync(cancellationToken);

    /// <summary>The tenant that owns the user, if it exists.</summary>
    public Task<Guid?> TenantOfUserAsync(Guid userId, CancellationToken cancellationToken) =>
        db.Users.AsNoTracking().IgnoreTenantScope(TenantScopeBypass.AuditPlacement)
            .Where(u => u.Id == userId).Select(u => (Guid?)u.TenantId).SingleOrDefaultAsync(cancellationToken);

    /// <summary>The owning tenant of the user linked to this Entra identity, if there is one.</summary>
    public Task<Guid?> TenantOfPersonAsync(Guid entraTenantId, Guid entraObjectId, CancellationToken cancellationToken) =>
        db.Users.AsNoTracking().IgnoreTenantScope(TenantScopeBypass.AuditPlacement)
            .Where(u => u.EntraTenantId == entraTenantId && u.EntraObjectId == entraObjectId)
            .Select(u => (Guid?)u.TenantId).SingleOrDefaultAsync(cancellationToken);

    private AuditEntry Stamp(AuditEntry entry)
    {
        if (entry.Id == Guid.Empty)
            entry.Id = Guid.CreateVersion7(time.GetUtcNow());
        if (entry.OccurredAt == default)
            entry.OccurredAt = time.GetUtcNow().UtcDateTime;
        if (entry.Detail is { Length: > MaxDetail })
            entry.Detail = entry.Detail[..MaxDetail];
        return entry;
    }

    private const int MaxDetail = 500;
}
