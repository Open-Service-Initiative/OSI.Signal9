using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;
using OSI.Signal9.Server.Data;
using OSI.Signal9.Server.Identity;

namespace OSI.Signal9.API.Authorization;

/// <summary>
/// What the caller's role assignments grant them, read live at the start of each request (story 71) and kept for the
/// rest of it. A role assignment grants its role's scopes at its tenant and every descendant (ADR-0010), so the scopes
/// at an assigned tenant are those of its own assignments and every assignment above it.
/// </summary>
public sealed class CallerAccess
{
    private readonly List<IReadOnlyList<string>> _required = [];
    private Dictionary<Guid, HashSet<string>> _scopesByTenant = [];
    private List<Assignment> _assignments = [];
    private bool _loaded;

    private sealed record Assignment(Guid TenantId, Guid RoleId, string[] Scopes);

    /// <summary>
    /// Narrows the rest of the request to tenants where the caller may also use one of <paramref name="anyOf"/>, on top
    /// of the scopes the endpoint requires: for a scope that depends on what is asked for.
    /// </summary>
    public void Require(params string[] anyOf) => _required.Add(anyOf);

    /// <summary>
    /// The assigned tenants whose subtrees the caller may act in with <paramref name="token"/>: those where, for each
    /// requirement, the role grants one of its scopes that the token also carries. Effective access is the token's
    /// scopes ∩ the role's scopes at the target tenant ∩ that tenant's subtree (ADR-0010); the tenant filter applies
    /// the subtree.
    /// </summary>
    public IReadOnlyCollection<Guid> TenantsGranting(ClaimsPrincipal token, IEnumerable<IReadOnlyList<string>> endpointRequirements)
    {
        var requirements = endpointRequirements.Concat(_required)
            .Select(anyOf => anyOf.Where(token.HasScope).ToList())
            .ToList();
        if (requirements.Count == 0)
            return [];

        return [.. _scopesByTenant
            .Where(tenant => requirements.All(anyOf => anyOf.Any(tenant.Value.Contains)))
            .Select(tenant => tenant.Key)];
    }

    /// <summary>
    /// The scopes the caller's role assignments grant at <paramref name="tenantId"/>: those of every assignment at it or
    /// above it. Empty for a tenant outside their reach.
    /// </summary>
    public async Task<IReadOnlySet<string>> RoleScopesAtAsync(Signal9DbContext db, Guid tenantId, CancellationToken cancellationToken) =>
        (await AssignmentsAtOrAboveAsync(db, tenantId, cancellationToken))
            .SelectMany(a => a.Assignment.Scopes).ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// Whether the caller is an Administrator above <paramref name="tenantId"/>: they hold the Administrator role at
    /// one of its ancestors, not only at the tenant itself (stories 21-22, 64).
    /// </summary>
    public async Task<bool> IsAdministratorAboveAsync(Signal9DbContext db, Guid tenantId, CancellationToken cancellationToken) =>
        (await AssignmentsAtOrAboveAsync(db, tenantId, cancellationToken))
            .Any(a => a.Depth > 0 && a.Assignment.RoleId == BuiltInRoles.Administrator.Id);

    /// <summary>Whether any of the caller's role assignments covers <paramref name="tenantId"/>.</summary>
    public async Task<bool> ReachesAsync(Signal9DbContext db, Guid tenantId, CancellationToken cancellationToken) =>
        (await AssignmentsAtOrAboveAsync(db, tenantId, cancellationToken)).Count > 0;

    // The caller's assignments at the tenant (depth 0) and its ancestors (depth 1 and up).
    private async Task<List<(Assignment Assignment, int Depth)>> AssignmentsAtOrAboveAsync(Signal9DbContext db, Guid tenantId,
        CancellationToken cancellationToken)
    {
        if (_assignments.Count == 0)
            return [];
        var assigned = _assignments.Select(a => a.TenantId).Distinct().ToArray();
        var ancestors = await db.TenantClosures.AsNoTracking()
            .Where(c => c.DescendantId == tenantId && assigned.Contains(c.AncestorId))
            .Select(c => new { c.AncestorId, c.Depth })
            .ToListAsync(cancellationToken);
        return [.. from c in ancestors from a in _assignments where a.TenantId == c.AncestorId select (a, c.Depth)];
    }

    /// <summary>
    /// Reads the role assignments of the person <paramref name="caller"/>'s token names, if it names one, once per
    /// request.
    /// </summary>
    internal async Task LoadAsync(Signal9DbContext db, ClaimsPrincipal caller, CancellationToken cancellationToken)
    {
        if (_loaded)
            return;
        _loaded = true;
        // A Superadministrator already has every tenant tree.
        if (Superadministrator.Is(caller) || !PersonSubject.TryParse(caller, out var entraTenantId, out var entraObjectId))
            return;

        var assignments = await db.RoleAssignments.AsNoTracking()
            .IgnoreTenantScope(TenantScopeBypass.CallerRoleAssignments)
            .Where(a => a.User!.EntraTenantId == entraTenantId && a.User.EntraObjectId == entraObjectId)
            .Select(a => new Assignment(a.TenantId, a.RoleId, a.Role!.Scopes))
            .ToListAsync(cancellationToken);
        if (assignments.Count == 0)
            return;
        _assignments = assignments;

        // Which assigned tenants lie beneath which (each is beneath itself), to inherit the assignments above them.
        var assigned = assignments.Select(a => a.TenantId).Distinct().ToArray();
        var links = await db.TenantClosures.AsNoTracking()
            .Where(c => assigned.Contains(c.AncestorId) && assigned.Contains(c.DescendantId))
            .Select(c => new { c.AncestorId, c.DescendantId })
            .ToListAsync(cancellationToken);

        _scopesByTenant = assigned.ToDictionary(
            tenant => tenant,
            tenant => links.Where(l => l.DescendantId == tenant)
                .SelectMany(l => assignments.Where(a => a.TenantId == l.AncestorId).SelectMany(a => a.Scopes))
                .ToHashSet(StringComparer.Ordinal));
    }
}

internal static class CallerAccessExtensions
{
    /// <summary>Reads each authenticated caller's role assignments once, after authorization and before the endpoint.</summary>
    public static IApplicationBuilder UseCallerAccess(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        if (context.User.Identity?.IsAuthenticated == true)
        {
            var services = context.RequestServices;
            await services.GetRequiredService<CallerAccess>()
                .LoadAsync(services.GetRequiredService<Signal9DbContext>(), context.User, context.RequestAborted);
        }
        await next(context);
    });
}
