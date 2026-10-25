using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;
using OSI.Signal9.Contracts.ApiApplications;
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

    /// <summary>An API application acting as itself, as the caller (#124).</summary>
    public sealed record CallerApplication(Guid Id, Guid TenantId, string TokenEndpointAuthMethod, IReadOnlySet<string> Scopes);

    /// <summary>The API application the token was issued to, when it acts as itself; null for anyone else.</summary>
    public CallerApplication? Application { get; private set; }

    /// <summary>A delegated API application acting for the person the token names (#125).</summary>
    public sealed record DelegatedApplication(Guid Id, Guid TenantId, IReadOnlySet<string> Scopes);

    /// <summary>
    /// The delegated API application the token was issued to, which confines the person to its scopes and its tenant's
    /// subtree; null for anyone else, including the dashboard.
    /// </summary>
    public DelegatedApplication? Delegated { get; private set; }

    /// <summary>
    /// Whether the token's <paramref name="scope"/> still counts: an API application's scopes are read live, so
    /// narrowing them takes effect on its tokens' next request (story 71).
    /// </summary>
    public bool Permits(string scope) =>
        (Application?.Scopes.Contains(scope) ?? true) && (Delegated?.Scopes.Contains(scope) ?? true);

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
        if (!PersonSubject.TryParse(caller, out var entraTenantId, out var entraObjectId))
        {
            await LoadApplicationAsync(db, caller, cancellationToken);
            return;
        }

        // A person signed in through a delegated API application gets their access ∩ its scopes ∩ its subtree (story 81).
        if (caller.GetClaim(OpenIddictConstants.Claims.ClientId) is { } clientId)
        {
            var application = await db.ApiApplications.AsNoTracking()
                .IgnoreTenantScope(TenantScopeBypass.CallerApiApplication)
                .Where(a => a.ClientId == clientId)
                .Select(a => new { a.Id, a.TenantId, a.Kind, a.Scopes })
                .SingleOrDefaultAsync(cancellationToken);
            if (application is not null)
                // An app-only application can't sign people in; if a token says otherwise, it reaches nothing.
                Delegated = new DelegatedApplication(application.Id, application.TenantId,
                    application.Kind == ApiApplicationKinds.Delegated ? application.Scopes.ToHashSet(StringComparer.Ordinal) : []);
        }

        if (Superadministrator.Is(caller))
        {
            // A Superadministrator already has every tenant tree, except through a delegated application, which holds
            // them to its subtree with all of its scopes.
            if (Delegated is not null)
                SetAssignments([new Assignment(Delegated.TenantId, Guid.Empty, [.. Delegated.Scopes])], []);
            return;
        }

        var assignments = await db.RoleAssignments.AsNoTracking()
            .IgnoreTenantScope(TenantScopeBypass.CallerRoleAssignments)
            .Where(a => a.User!.EntraTenantId == entraTenantId && a.User.EntraObjectId == entraObjectId)
            .Select(a => new Assignment(a.TenantId, a.RoleId, a.Role!.Scopes))
            .ToListAsync(cancellationToken);
        if (Delegated is not null)
            assignments = await ConfineAsync(db, assignments, Delegated, cancellationToken);
        if (assignments.Count == 0)
            return;

        // Which assigned tenants lie beneath which (each is beneath itself), to inherit the assignments above them.
        var assigned = assignments.Select(a => a.TenantId).Distinct().ToArray();
        var links = await db.TenantClosures.AsNoTracking()
            .Where(c => assigned.Contains(c.AncestorId) && assigned.Contains(c.DescendantId))
            .Select(c => new { c.AncestorId, c.DescendantId })
            .ToListAsync(cancellationToken);
        SetAssignments(assignments, [.. links.Select(l => (l.AncestorId, l.DescendantId))]);
    }

    private void SetAssignments(List<Assignment> assignments, List<(Guid AncestorId, Guid DescendantId)> links)
    {
        _assignments = assignments;
        _scopesByTenant = assignments.Select(a => a.TenantId).Distinct().ToDictionary(
            tenant => tenant,
            tenant => assignments
                .Where(a => a.TenantId == tenant || links.Contains((a.TenantId, tenant)))
                .SelectMany(a => a.Scopes)
                .ToHashSet(StringComparer.Ordinal));
    }

    /// <summary>
    /// The person's assignments as they reach through a delegated application: the subtree of an assigned tenant meets
    /// the application's subtree either at the application's tenant (when assigned at or above it) or at the assigned
    /// tenant (when beneath it), and nowhere if neither lies beneath the other. Each keeps only the application's scopes.
    /// </summary>
    private static async Task<List<Assignment>> ConfineAsync(Signal9DbContext db, List<Assignment> assignments,
        DelegatedApplication application, CancellationToken cancellationToken)
    {
        var assigned = assignments.Select(a => a.TenantId).Distinct().ToArray();
        var related = await db.TenantClosures.AsNoTracking()
            .Where(c => (c.DescendantId == application.TenantId && assigned.Contains(c.AncestorId))
                || (c.AncestorId == application.TenantId && assigned.Contains(c.DescendantId)))
            .Select(c => new { c.AncestorId, c.DescendantId })
            .ToListAsync(cancellationToken);
        var atOrAbove = related.Where(c => c.DescendantId == application.TenantId).Select(c => c.AncestorId).ToHashSet();
        var beneath = related.Where(c => c.AncestorId == application.TenantId).Select(c => c.DescendantId).ToHashSet();

        string[] Narrow(string[] scopes) => [.. scopes.Where(application.Scopes.Contains)];
        return
        [
            .. assignments.Where(a => atOrAbove.Contains(a.TenantId))
                .Select(a => a with { TenantId = application.TenantId, Scopes = Narrow(a.Scopes) }),
            .. assignments.Where(a => !atOrAbove.Contains(a.TenantId) && beneath.Contains(a.TenantId))
                .Select(a => a with { Scopes = Narrow(a.Scopes) }),
        ];
    }

    /// <summary>
    /// An application acting as itself is its own subject (its client ID). If it's a registered API application, it
    /// has one assignment-like grant: its role at its tenant, narrowed to the scopes it was granted (#124).
    /// </summary>
    private async Task LoadApplicationAsync(Signal9DbContext db, ClaimsPrincipal caller, CancellationToken cancellationToken)
    {
        var clientId = caller.GetClaim(OpenIddictConstants.Claims.ClientId);
        if (clientId is null || caller.GetClaim(OpenIddictConstants.Claims.Subject) != clientId)
            return;
        var application = await db.ApiApplications.AsNoTracking()
            .IgnoreTenantScope(TenantScopeBypass.CallerApiApplication)
            .Where(a => a.ClientId == clientId)
            .Select(a => new { a.Id, a.TenantId, a.Kind, a.RoleId, a.TokenEndpointAuthMethod, a.Scopes, RoleScopes = a.Role!.Scopes })
            .SingleOrDefaultAsync(cancellationToken);
        if (application is null)
            return;
        // A delegated application never acts as itself; if a token says otherwise, it reaches nothing.
        if (application.Kind != ApiApplicationKinds.AppOnly || application.RoleId is not { } roleId)
        {
            Application = new CallerApplication(application.Id, application.TenantId, application.TokenEndpointAuthMethod,
                new HashSet<string>());
            return;
        }

        var scopes = application.Scopes.Intersect(application.RoleScopes, StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        Application = new CallerApplication(application.Id, application.TenantId, application.TokenEndpointAuthMethod, scopes);
        SetAssignments([new Assignment(application.TenantId, roleId, [.. scopes])], []);
    }
}

internal static class CallerAccessExtensions
{
    /// <summary>
    /// Reads each authenticated caller's role assignments once, before authorization, whose scope check and
    /// Superadministrator test depend on the API application the token was issued to.
    /// </summary>
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
