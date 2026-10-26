using Microsoft.EntityFrameworkCore;
using OSI.Signal9.Server.Data;
using OSI.Signal9.Server.Identity;

namespace OSI.Signal9.API.Roles;

/// <summary>
/// Every tenant keeps an Administrator at it or above it (story 23): revoking an Administrator assignment and deleting a
/// tenant whose users hold one both check it.
/// </summary>
public static class AdministratorCoverage
{
    /// <summary>
    /// Whether an Administrator assignment other than <paramref name="removed"/> covers <paramref name="tenantId"/>: at
    /// it or at an ancestor, which may lie outside the caller's reach.
    /// </summary>
    public static Task<bool> RemainsAsync(Signal9DbContext db, Guid tenantId, Guid[] removed,
        CancellationToken cancellationToken) =>
        db.RoleAssignments.AsNoTracking()
            .IgnoreTenantScope(TenantScopeBypass.AdministratorLockoutGuard)
            .AnyAsync(a => !removed.Contains(a.Id) && a.RoleId == BuiltInRoles.Administrator.Id
                && db.TenantClosures.Any(c => c.DescendantId == tenantId && c.AncestorId == a.TenantId), cancellationToken);
}
