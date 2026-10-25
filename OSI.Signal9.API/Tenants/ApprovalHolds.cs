using Microsoft.EntityFrameworkCore;
using OSI.Signal9.Server.Data;

namespace OSI.Signal9.API.Tenants;

/// <summary>
/// Holding new agents for approval (story 96): a tenant's hold applies to it and every descendant, and a descendant
/// can't turn it off. Ancestors are read past the tenant filter, since a hold set above the caller's reach still applies.
/// </summary>
internal static class ApprovalHolds
{
    /// <summary>Whether agents newly enrolling into <paramref name="tenantId"/> are held: it or an ancestor holds.</summary>
    public static Task<bool> HoldsNewAgentsAsync(this Signal9DbContext db, Guid tenantId, CancellationToken cancellationToken) =>
        db.TenantClosures.IgnoreTenantScope(TenantScopeBypass.InheritedTenantSettings).AnyAsync(c => c.DescendantId == tenantId
            && db.Tenants.Any(t => t.Id == c.AncestorId && t.HoldsNewAgentsForApproval), cancellationToken);

    /// <summary>Those of <paramref name="tenantIds"/> that inherit a hold from an ancestor.</summary>
    public static async Task<HashSet<Guid>> InheritingHoldAsync(this Signal9DbContext db, IReadOnlyCollection<Guid> tenantIds,
        CancellationToken cancellationToken) =>
        [.. await db.TenantClosures.IgnoreTenantScope(TenantScopeBypass.InheritedTenantSettings)
            .Where(c => tenantIds.Contains(c.DescendantId) && c.Depth > 0
                && db.Tenants.Any(t => t.Id == c.AncestorId && t.HoldsNewAgentsForApproval))
            .Select(c => c.DescendantId).Distinct().ToListAsync(cancellationToken)];

    /// <summary>Whether <paramref name="tenantId"/> inherits a hold from an ancestor.</summary>
    public static async Task<bool> InheritsHoldAsync(this Signal9DbContext db, Guid tenantId, CancellationToken cancellationToken) =>
        (await db.InheritingHoldAsync([tenantId], cancellationToken)).Count > 0;
}
