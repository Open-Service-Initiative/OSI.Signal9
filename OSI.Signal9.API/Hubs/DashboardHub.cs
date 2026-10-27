using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;
using OSI.Signal9.API.Authorization;
using OSI.Signal9.Contracts.Hubs;
using OSI.Signal9.Server.Data;
using OSI.Signal9.Server.Identity;

namespace OSI.Signal9.API.Hubs;

/// <summary>
/// The dashboard's live updates (ADR-0009, stories 113-115). Server-to-client only: it carries small change notices,
/// and the dashboard fetches details through web. Each connection joins a group per tenant its caller is assigned at
/// (a Superadministrator: the whole deployment), and events go to the owning tenant's group and each ancestor's, so a
/// connection hears its caller's subtree and nothing else. It stays open only while that access lasts.
/// </summary>
[RequireScope(Scopes.Devices.Read, Scopes.Telemetry.Read)]
public sealed class DashboardHub(DashboardAccess access) : Hub<IDashboardHubClient>
{
    /// <summary>The scopes, any one of which lets a caller listen (#63).</summary>
    public static readonly string[] AnyOfScopes = [Scopes.Devices.Read, Scopes.Telemetry.Read];

    /// <summary>The group of Superadministrators, who hear every tenant tree.</summary>
    public const string DeploymentGroup = "deployment";

    public static string GroupFor(Guid tenantId) => $"tenant:{tenantId}";

    public override async Task OnConnectedAsync()
    {
        if (!await access.OpenAsync(Context))
            return;
        await base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        access.Forget(Context);
        return base.OnDisconnectedAsync(exception);
    }
}

/// <summary>
/// Keeps each dashboard hub connection to its caller's live access: it joins the groups of the tenants the caller's
/// role assignments grant a hub scope at, follows changes to them, and closes once the token is revoked or expires
/// (sign-out, front-channel logout, story 115) or no such tenant is left (story 27).
/// </summary>
public sealed class DashboardAccess(
    Signal9DbContext db,
    HubConnections<DashboardHub> connections,
    IHubContext<DashboardHub, IDashboardHubClient> hub,
    TimeProvider time)
{
    /// <summary>
    /// Records a new connection and puts it in its groups; false when it was closed instead. It's recorded before its
    /// access is read, so a change committed meanwhile is either read here or re-checked there.
    /// </summary>
    internal Task<bool> OpenAsync(HubCallerContext context) => CheckAsync(connections.Add(context), context.ConnectionAborted);

    internal void Forget(HubCallerContext context) => connections.Remove(context);

    /// <summary>Re-checks the connections of <paramref name="subject"/>, such as after their role assignments change.</summary>
    public Task RecheckAsync(string subject, CancellationToken cancellationToken) =>
        RecheckAsync(connections.Where(c => c.Subject == subject), cancellationToken);

    /// <summary>Re-checks every connection, such as after the tenant tree changes.</summary>
    public Task RecheckAllAsync(CancellationToken cancellationToken) =>
        RecheckAsync(connections.Where(_ => true), cancellationToken);

    private async Task RecheckAsync(IReadOnlyList<OpenHubConnection> open, CancellationToken cancellationToken)
    {
        foreach (var connection in open)
            await CheckAsync(connection, cancellationToken);
    }

    private async Task<bool> CheckAsync(OpenHubConnection connection, CancellationToken cancellationToken)
    {
        await connection.Checking.WaitAsync(cancellationToken);
        try
        {
            if (connection.Closed)
                return false;
            var groups = connection.Context.User is { } user && await HubTokens.IsLiveAsync(db, user, time, cancellationToken)
                ? await GroupsForAsync(user, cancellationToken)
                : new HashSet<string>();
            if (groups.Count == 0)
            {
                connections.Close(connection);
                return false;
            }

            var id = connection.Context.ConnectionId;
            foreach (var left in connection.Groups.Except(groups).ToList())
            {
                await hub.Groups.RemoveFromGroupAsync(id, left, cancellationToken);
                connection.Groups.Remove(left);
            }
            foreach (var joined in groups.Except(connection.Groups).ToList())
            {
                await hub.Groups.AddToGroupAsync(id, joined, cancellationToken);
                connection.Groups.Add(joined);
            }
            return true;
        }
        finally
        {
            connection.Checking.Release();
        }
    }

    /// <summary>
    /// The groups the caller hears: the deployment's for a Superadministrator; otherwise those of the assigned tenants
    /// where their access grants a hub scope (ADR-0010), leaving out any beneath another, whose events it already hears.
    /// </summary>
    private async Task<IReadOnlySet<string>> GroupsForAsync(System.Security.Claims.ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        var access = new CallerAccess();
        await access.LoadAsync(db, user, cancellationToken);
        var scopes = DashboardHub.AnyOfScopes.Where(s => user.HasScope(s) && access.Permits(s)).ToArray();
        if (scopes.Length == 0)
            return new HashSet<string>();
        if (Server.Identity.Superadministrator.Is(user) && access.Delegated is null)
            return new HashSet<string> { DashboardHub.DeploymentGroup };

        var tenants = access.TenantsGranting(user, [scopes]).ToArray();
        var beneath = await db.TenantClosures.AsNoTracking()
            .Where(c => c.Depth > 0 && tenants.Contains(c.AncestorId) && tenants.Contains(c.DescendantId))
            .Select(c => c.DescendantId)
            .ToListAsync(cancellationToken);
        return tenants.Except(beneath).Select(DashboardHub.GroupFor).ToHashSet();
    }
}

/// <summary>
/// Tells the dashboards that can see a tenant that something in it changed: the tenant's group and each ancestor's,
/// read from the closure table when sent, so a transfer changes who hears at once. The tree's depth cap bounds the
/// groups (ADR-0001).
/// </summary>
public sealed class DashboardEvents(Signal9DbContext db, IHubContext<DashboardHub, IDashboardHubClient> hub)
{
    public Task DeviceChangedAsync(Guid tenantId, Guid deviceId, CancellationToken cancellationToken) =>
        DeviceChangedAsync([tenantId], deviceId, cancellationToken);

    /// <summary>A device changed in any of <paramref name="tenantIds"/>, such as both sides of a device transfer.</summary>
    public async Task DeviceChangedAsync(Guid[] tenantIds, Guid deviceId, CancellationToken cancellationToken) =>
        await (await ToAsync(tenantIds, cancellationToken)).DeviceChanged(new DeviceNotice(deviceId, DeviceUrl(deviceId)));

    public async Task DeviceDeletedAsync(Guid tenantId, Guid deviceId, CancellationToken cancellationToken) =>
        await (await ToAsync(tenantId, cancellationToken)).DeviceDeleted(new DeviceNotice(deviceId, DeviceUrl(deviceId)));

    public async Task CommandChangedAsync(Guid tenantId, Contracts.Commands.CommandDto command, CancellationToken cancellationToken) =>
        await (await ToAsync(tenantId, cancellationToken)).CommandChanged(new CommandNotice(command.DeviceId, command.Id, command.Url));

    public async Task TelemetryReceivedAsync(Guid tenantId, Guid deviceId, Guid sampleId, CancellationToken cancellationToken) =>
        await (await ToAsync(tenantId, cancellationToken)).TelemetryReceived(
            new TelemetryNotice(deviceId, sampleId, $"{DeviceUrl(deviceId)}/telemetry/{sampleId}"));

    private static string DeviceUrl(Guid deviceId) => $"/api/v1/devices/{deviceId}";

    private Task<IDashboardHubClient> ToAsync(Guid tenantId, CancellationToken cancellationToken) =>
        ToAsync([tenantId], cancellationToken);

    private async Task<IDashboardHubClient> ToAsync(Guid[] tenantIds, CancellationToken cancellationToken)
    {
        var ancestors = await db.TenantClosures.AsNoTracking()
            .Where(c => tenantIds.Contains(c.DescendantId))
            .Select(c => c.AncestorId)
            .Distinct()
            .ToListAsync(cancellationToken);
        return hub.Clients.Groups([DashboardHub.DeploymentGroup, .. ancestors.Select(DashboardHub.GroupFor)]);
    }
}
