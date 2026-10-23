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

    /// <summary>Reads the role assignments of the person <paramref name="caller"/>'s token names, if it names one.</summary>
    internal async Task LoadAsync(Signal9DbContext db, ClaimsPrincipal caller, CancellationToken cancellationToken)
    {
        // A Superadministrator already has every tenant tree.
        if (Superadministrator.Is(caller) || !PersonSubject.TryParse(caller, out var entraTenantId, out var entraObjectId))
            return;

        var assignments = await db.RoleAssignments.AsNoTracking()
            .IgnoreTenantScope(TenantScopeBypass.CallerRoleAssignments)
            .Where(a => a.User!.EntraTenantId == entraTenantId && a.User.EntraObjectId == entraObjectId)
            .Select(a => new { a.TenantId, a.Role!.Scopes })
            .ToListAsync(cancellationToken);
        if (assignments.Count == 0)
            return;

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
