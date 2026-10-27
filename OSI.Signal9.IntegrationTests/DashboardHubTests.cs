using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using OSI.Signal9.API.Hubs;
using OSI.Signal9.Contracts.Bff;
using OSI.Signal9.Contracts.Commands;
using OSI.Signal9.Contracts.Hubs;
using OSI.Signal9.Contracts.Roles;
using OSI.Signal9.Contracts.Serialization;
using OSI.Signal9.Contracts.Tenants;
using OSI.Signal9.Contracts.Users;
using OSI.Signal9.Server.Identity;

namespace OSI.Signal9.IntegrationTests;

/// <summary>
/// The dashboard hub (ADR-0009, stories 113-115): each connection hears small change notices from the tenants its
/// caller can see, and only those, for as long as the caller's access lasts.
/// </summary>
[TestClass]
public sealed class DashboardHubTests : IDisposable
{
    private readonly Signal9ApiFactory _factory = new();
    private readonly HttpClient _superadministrator;
    private readonly TestContext _testContext;

    public DashboardHubTests(TestContext testContext)
    {
        _testContext = testContext;
        _superadministrator = _factory.CreateSuperadministratorClient();
    }

    private CancellationToken Ct => _testContext.CancellationToken;

    [TestMethod]
    public async Task Events_reach_connections_at_the_owning_tenant_and_its_ancestors_only()
    {
        var root = await TenantAsync("Root");
        var msp = await TenantAsync("Msp", root);
        var client = await TenantAsync("Client", msp);
        var sibling = await TenantAsync("Sibling", msp);
        var elsewhere = await TenantAsync("Elsewhere");
        var device = await DeviceAsync(client);
        var siblingDevice = await DeviceAsync(sibling);
        var elsewhereDevice = await DeviceAsync(elsewhere);
        await using var atRoot = await ConnectAsync(await PersonAsync(root, ("Viewer", root)));
        await using var atClient = await ConnectAsync(await PersonAsync(client, ("Viewer", client)));
        await using var atSibling = await ConnectAsync(await PersonAsync(sibling, ("Viewer", sibling)));
        await using var atElsewhere = await ConnectAsync(await PersonAsync(elsewhere, ("Viewer", elsewhere)));

        var command = await QueueCommandAsync(device);
        // A later event each connection may hear: once it has arrived, anything sent before it would have too.
        await QueueCommandAsync(siblingDevice);
        await QueueCommandAsync(elsewhereDevice);

        await atRoot.WaitForCommandAsync(command.Id);
        await atClient.WaitForCommandAsync(command.Id);
        await atSibling.WaitForDeviceAsync(siblingDevice);
        await atElsewhere.WaitForDeviceAsync(elsewhereDevice);
        Assert.IsFalse(atSibling.Heard(device), "A sibling tenant's Viewer heard another subtree's event.");
        Assert.IsFalse(atElsewhere.Heard(device), "Another tree's Viewer heard the event.");
        Assert.IsFalse(atClient.Heard(siblingDevice), "The client's Viewer heard its sibling's event.");
    }

    [TestMethod]
    public async Task A_superadministrator_hears_every_tenant_tree()
    {
        var one = await DeviceAsync(await TenantAsync("One"));
        var other = await DeviceAsync(await TenantAsync("Other", await TenantAsync("Parent")));
        await using var superadministrator = await ConnectAsync(_factory.Superadministrator());

        await QueueCommandAsync(one);
        await QueueCommandAsync(other);

        await superadministrator.WaitForDeviceAsync(one);
        await superadministrator.WaitForDeviceAsync(other);
    }

    [TestMethod]
    public async Task A_caller_without_a_role_assignment_granting_a_hub_scope_is_disconnected()
    {
        var tenant = await TenantAsync("Tenant");
        var person = await PersonAsync(tenant);
        var token = await _factory.PersonTokenAsync(person.User);
        var hub = Build(token);
        await using var listener = new DashboardListener(hub, Ct);

        await hub.StartAsync(Ct);

        await listener.Closed.WaitAsync(TimeSpan.FromSeconds(15), Ct);
    }

    [TestMethod]
    public async Task An_event_is_a_small_notice_with_the_items_canonical_url()
    {
        var tenant = await TenantAsync("Tenant");
        var device = await DeviceAsync(tenant);
        await using var viewer = await ConnectAsync(await PersonAsync(tenant, ("Viewer", tenant)));

        var command = await QueueCommandAsync(device);

        var notice = await viewer.WaitForCommandAsync(command.Id);
        Assert.AreEqual(new CommandNotice(device, command.Id, $"/api/v1/devices/{device}/commands/{command.Id}"), notice);
    }

    [TestMethod]
    public void Notices_carry_identifiers_and_a_url_only()
    {
        foreach (var method in typeof(IDashboardHubClient).GetMethods())
        {
            var parameter = method.GetParameters().Single();
            foreach (var property in parameter.ParameterType.GetProperties())
                Assert.IsTrue(property.PropertyType == typeof(Guid) || property.Name == "Url",
                    $"{method.Name} carries {property.Name}, which is a detail for the dashboard to fetch.");
        }
    }

    [TestMethod]
    public async Task Losing_one_of_several_role_assignments_stops_that_tenants_events_at_once()
    {
        var kept = await TenantAsync("Kept");
        var lost = await TenantAsync("Lost");
        var keptDevice = await DeviceAsync(kept);
        var lostDevice = await DeviceAsync(lost);
        var person = await PersonAsync(kept, ("Viewer", kept), ("Viewer", lost));
        await using var viewer = await ConnectAsync(person);

        await RemoveAsync(person, lost);
        await QueueCommandAsync(lostDevice);
        await QueueCommandAsync(keptDevice);

        await viewer.WaitForDeviceAsync(keptDevice);
        Assert.IsFalse(viewer.Heard(lostDevice), "The connection still hears a tenant the person no longer has.");
        Assert.AreEqual(HubConnectionState.Connected, viewer.Hub.State);
    }

    [TestMethod]
    public async Task Removing_the_last_role_assignment_closes_the_connection_at_once()
    {
        var tenant = await TenantAsync("Tenant");
        var person = await PersonAsync(tenant, ("Viewer", tenant));
        await using var viewer = await ConnectAsync(person);

        await RemoveAsync(person, tenant);

        await viewer.Closed.WaitAsync(TimeSpan.FromSeconds(15), Ct);
    }

    [TestMethod]
    public async Task A_new_role_assignment_starts_that_tenants_events_at_once()
    {
        var first = await TenantAsync("First");
        var added = await TenantAsync("Added");
        var addedDevice = await DeviceAsync(added);
        var person = await PersonAsync(first, ("Viewer", first));
        await using var viewer = await ConnectAsync(person);

        await AssignAsync(person, "Viewer", added);
        await QueueCommandAsync(addedDevice);

        await viewer.WaitForDeviceAsync(addedDevice);
    }

    [TestMethod]
    public async Task After_a_tenant_transfer_events_reach_the_new_ancestors_and_not_the_old()
    {
        var oldParent = await TenantAsync("OldParent");
        var newParent = await TenantAsync("NewParent");
        var moved = await TenantAsync("Moved", oldParent);
        var device = await DeviceAsync(moved);
        var oldParentDevice = await DeviceAsync(oldParent);
        await using var atOld = await ConnectAsync(await PersonAsync(oldParent, ("Viewer", oldParent)));
        await using var atNew = await ConnectAsync(await PersonAsync(newParent, ("Viewer", newParent)));

        await TransferAsync(moved, newParent);
        var command = await QueueCommandAsync(device);
        await QueueCommandAsync(oldParentDevice);

        await atNew.WaitForCommandAsync(command.Id);
        await atOld.WaitForDeviceAsync(oldParentDevice);
        Assert.IsFalse(atOld.Heard(device), "The former parent's Viewer still hears the transferred tenant.");
    }

    [TestMethod]
    public async Task A_device_transfer_is_heard_on_both_sides_and_later_events_only_on_the_new()
    {
        var from = await TenantAsync("From");
        var to = await TenantAsync("To");
        var device = await DeviceAsync(from);
        var fromDevice = await DeviceAsync(from);
        await using var atFrom = await ConnectAsync(await PersonAsync(from, ("Viewer", from)));
        await using var atTo = await ConnectAsync(await PersonAsync(to, ("Viewer", to)));

        var moved = await _superadministrator.PostAsJsonAsync($"api/v1/devices/{device}:transfer",
            new OSI.Signal9.Contracts.Devices.TransferDeviceRequest { TenantId = to }, Signal9Json.Options, Ct);
        Assert.AreEqual(HttpStatusCode.OK, moved.StatusCode, await moved.Content.ReadAsStringAsync(Ct));

        await atFrom.WaitForAsync(h => h.Notice is DeviceNotice d && d.DeviceId == device);
        await atTo.WaitForAsync(h => h.Notice is DeviceNotice d && d.DeviceId == device);
        var command = await QueueCommandAsync(device);
        await QueueCommandAsync(fromDevice);
        await atTo.WaitForCommandAsync(command.Id);
        await atFrom.WaitForDeviceAsync(fromDevice);
        Assert.IsFalse(atFrom.Heard(command.Id, command: true), "The former tenant still hears the transferred device.");
    }

    [TestMethod]
    public async Task A_person_assigned_at_a_tenant_and_beneath_it_hears_each_event_once()
    {
        var parent = await TenantAsync("Parent");
        var child = await TenantAsync("Child", parent);
        var device = await DeviceAsync(child);
        var person = await PersonAsync(parent, ("Viewer", parent), ("Viewer", child));
        await using var viewer = await ConnectAsync(person);

        var first = await QueueCommandAsync(device);
        var second = await QueueCommandAsync(device);

        await viewer.WaitForCommandAsync(second.Id);
        Assert.AreEqual(1, viewer.Count(h => h.Notice is CommandNotice c && c.CommandId == first.Id));
    }

    [TestMethod]
    public async Task After_a_transfer_brings_one_assigned_tenant_beneath_another_each_event_arrives_once()
    {
        var upper = await TenantAsync("Upper");
        var lower = await TenantAsync("Lower");
        var device = await DeviceAsync(lower);
        var person = await PersonAsync(upper, ("Viewer", upper), ("Viewer", lower));
        await using var viewer = await ConnectAsync(person);

        await TransferAsync(lower, upper);
        var first = await QueueCommandAsync(device);
        var second = await QueueCommandAsync(device);

        await viewer.WaitForCommandAsync(second.Id);
        Assert.AreEqual(1, viewer.Count(h => h.Notice is CommandNotice c && c.CommandId == first.Id));
    }

    // Through web, as the dashboard connects.

    [TestMethod]
    public async Task The_dashboard_hears_its_subtree_through_web()
    {
        var (browser, tenant) = await SignedInAsync();
        using var _ = browser;
        var device = await DeviceAsync(tenant);
        await using var dashboard = await ConnectThroughWebAsync(browser);

        var command = await QueueCommandAsync(device);

        await dashboard.WaitForCommandAsync(command.Id);
    }

    [TestMethod]
    [DataRow("https://evil.example")]
    [DataRow("http://web.signal9.test")]
    [DataRow("https://web.signal9.test:8443")]
    [DataRow("null")]
    public async Task Web_refuses_a_hub_handshake_from_another_origin(string origin)
    {
        var (browser, _) = await SignedInAsync();
        using var __ = browser;
        var before = _factory.Web.ApiRequests.Count;

        var negotiate = await browser.FetchAsync(HttpMethod.Post, "hubs/dashboard/negotiate?negotiateVersion=1",
            headers: h => h.Add("Origin", origin));
        var webSocket = await browser.FetchAsync(HttpMethod.Get, "hubs/dashboard?id=x", csrfHeader: false,
            headers: h => WebSocketHandshake(h, origin));

        Assert.AreEqual(HttpStatusCode.Forbidden, negotiate.StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, webSocket.StatusCode);
        Assert.AreEqual(before, _factory.Web.ApiRequests.Count, "web forwarded a cross-origin hub request.");
    }

    [TestMethod]
    public async Task Web_refuses_a_websocket_handshake_without_an_origin()
    {
        var (browser, _) = await SignedInAsync();
        using var __ = browser;
        var before = _factory.Web.ApiRequests.Count;

        var webSocket = await browser.FetchAsync(HttpMethod.Get, "hubs/dashboard", csrfHeader: false,
            headers: h => WebSocketHandshake(h, origin: null));

        Assert.AreEqual(HttpStatusCode.Forbidden, webSocket.StatusCode);
        Assert.AreEqual(before, _factory.Web.ApiRequests.Count);
    }

    [TestMethod]
    public async Task Signing_out_drops_the_dashboards_connection()
    {
        var (browser, _) = await SignedInAsync();
        using var __ = browser;
        await using var dashboard = await ConnectThroughWebAsync(browser);

        Assert.AreEqual(HttpStatusCode.NoContent, (await browser.FetchAsync(HttpMethod.Post, BffRoutes.Logout)).StatusCode);
        _factory.Time.Advance(new HubAccessOptions().AccessCheckInterval);

        await dashboard.Closed.WaitAsync(TimeSpan.FromSeconds(15), Ct);
    }

    [TestMethod]
    public async Task Entras_front_channel_logout_drops_the_dashboards_connection()
    {
        var user = EntraUser.WithMfa() with { SessionId = $"sid-{Guid.NewGuid():N}" };
        var (browser, _) = await SignedInAsync(user);
        using var __ = browser;
        await using var dashboard = await ConnectThroughWebAsync(browser);

        using var entra = _factory.Auth.CreateClient();
        var logout = await entra.GetAsync($"{OSI.Signal9.Auth.FrontChannelLogout.Path}" +
            $"?iss={Uri.EscapeDataString($"{FakeEntra.Instance}{user.TenantId}/v2.0")}&sid={Uri.EscapeDataString(user.SessionId!)}", Ct);
        Assert.AreEqual(HttpStatusCode.OK, logout.StatusCode);
        _factory.Time.Advance(new HubAccessOptions().AccessCheckInterval);

        await dashboard.Closed.WaitAsync(TimeSpan.FromSeconds(15), Ct);
    }

    [TestMethod]
    public async Task When_its_token_expires_the_connection_closes_and_reconnects_through_web_with_a_fresh_token()
    {
        var (browser, tenant) = await SignedInAsync();
        using var _ = browser;
        var device = await DeviceAsync(tenant);
        await using var dashboard = await ConnectThroughWebAsync(browser);
        var first = HubTokens().Last();

        _factory.Time.Advance(AuthorizationServer.AccessTokenLifetime);
        await dashboard.Closed.SignalledAsync("the connection closing");
        // The dashboard connects again when the hub closes; web refreshes the session's token on the way.
        await dashboard.Hub.StartAsync(Ct);
        await Assert.ThrowsExactlyAsync<HubException>(() => dashboard.Hub.InvokeAsync("Ping", Ct));
        var command = await QueueCommandAsync(device);

        await dashboard.WaitForCommandAsync(command.Id);
        Assert.AreNotEqual(first, HubTokens().Last(), "The connection came back with the expired token.");
    }

    // Helpers

    // The access tokens web sent api on the hub's requests, in order.
    private List<string?> HubTokens() =>
        [.. _factory.Web.ApiRequests.Where(r => r.Uri.AbsolutePath.StartsWith(HubRoutes.Dashboard, StringComparison.Ordinal))
            .Select(r => r.Authorization)];

    private static void WebSocketHandshake(System.Net.Http.Headers.HttpRequestHeaders headers, string? origin)
    {
        headers.Connection.Add("Upgrade");
        headers.Upgrade.Add(new System.Net.Http.Headers.ProductHeaderValue("websocket"));
        headers.Add("Sec-WebSocket-Version", "13");
        headers.Add("Sec-WebSocket-Key", Convert.ToBase64String(Guid.NewGuid().ToByteArray()));
        if (origin is not null)
            headers.Add("Origin", origin);
    }

    /// <summary>A person signed in to the dashboard, holding a Viewer assignment at a tenant of their own.</summary>
    private async Task<(DashboardBrowser Browser, Guid TenantId)> SignedInAsync(EntraUser? user = null)
    {
        var assigned = await _factory.AssignedPersonAsync(user);
        var browser = new DashboardBrowser(_factory);
        var landing = await browser.SignInAsync(assigned.User);
        Assert.AreEqual(HttpStatusCode.OK, landing.StatusCode, await landing.Content.ReadAsStringAsync(Ct));
        return (browser, assigned.TenantId);
    }

    /// <summary>Connects as the dashboard does: to web's hub path, with the session cookie and the CSRF header.</summary>
    private async Task<DashboardListener> ConnectThroughWebAsync(DashboardBrowser browser)
    {
        var hub = new HubConnectionBuilder()
            .WithUrl(new Uri(Signal9WebFactory.BaseUri, HubRoutes.Dashboard.TrimStart('/')), options =>
            {
                options.HttpMessageHandlerFactory = _ => browser.HubHandler();
                options.Transports = HttpTransportType.LongPolling;
                options.Headers[BffRoutes.CsrfHeader] = BffRoutes.CsrfHeaderValue;
            })
            .AddJsonProtocol(o => Signal9Json.Configure(o.PayloadSerializerOptions))
            .Build();
        var listener = new DashboardListener(hub, Ct);
        await hub.StartAsync(Ct);
        await Assert.ThrowsExactlyAsync<HubException>(() => hub.InvokeAsync("Ping", Ct));
        return listener;
    }

    private async Task RemoveAsync(Person person, Guid tenantId)
    {
        var response = await _superadministrator.DeleteAsync(person.Assignments[tenantId], Ct);
        Assert.AreEqual(HttpStatusCode.NoContent, response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
    }

    /// <summary>Moves a tenant as a Superadministrator, who is on both sides of every transfer.</summary>
    private async Task TransferAsync(Guid tenantId, Guid toParentTenantId)
    {
        var proposed = await _superadministrator.PostAsJsonAsync($"api/v1/tenants/{tenantId}/transfers",
            new ProposeTenantTransferRequest { ToParentTenantId = toParentTenantId }, Signal9Json.Options, Ct);
        Assert.AreEqual(HttpStatusCode.Created, proposed.StatusCode, await proposed.Content.ReadAsStringAsync(Ct));
        var accepted = await _superadministrator.PostAsync($"{proposed.Headers.Location}:accept", null, Ct);
        Assert.AreEqual(HttpStatusCode.OK, accepted.StatusCode, await accepted.Content.ReadAsStringAsync(Ct));
    }

    private async Task<Guid> TenantAsync(string name, Guid? parent = null) =>
        (await _factory.CreateTenantAsync(_superadministrator, $"{name}{Guid.NewGuid():N}"[..20], parentTenantId: parent)).Id;

    private async Task<Guid> DeviceAsync(Guid tenantId)
    {
        using var agent = await _factory.EnrollAgentAsync(tenantId, $"PC{Random.Shared.Next(1000, 9999)}");
        return agent.DeviceId;
    }

    private async Task<CommandDto> QueueCommandAsync(Guid deviceId)
    {
        var response = await _superadministrator.PostAsJsonAsync($"api/v1/devices/{deviceId}/commands",
            new CreateCommandRequest { Type = CommandType.CollectSystemInfo }, Signal9Json.Options, Ct);
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<CommandDto>(Signal9Json.Options, Ct))!;
    }

    /// <summary>A person owned by <paramref name="owner"/>, holding these role assignments, and their user ID.</summary>
    private async Task<Person> PersonAsync(Guid owner, params (string Role, Guid Tenant)[] assignments)
    {
        var entraUser = EntraUser.WithMfa();
        var created = await _superadministrator.PostAsJsonAsync("api/v1/users", new CreateUserRequest
        {
            TenantId = owner,
            EntraTenantId = entraUser.TenantId,
            EntraObjectId = entraUser.ObjectId,
            DisplayName = "Alex Wilber",
        }, Signal9Json.Options, Ct);
        Assert.AreEqual(HttpStatusCode.Created, created.StatusCode, await created.Content.ReadAsStringAsync(Ct));
        var person = new Person(entraUser, (await created.Content.ReadFromJsonAsync<UserDto>(Signal9Json.Options, Ct))!.Id, []);
        foreach (var (role, tenant) in assignments)
            await AssignAsync(person, role, tenant);
        return person;
    }

    private async Task<Uri> AssignAsync(Person person, string role, Guid tenantId)
    {
        var response = await _superadministrator.PostAsJsonAsync($"api/v1/tenants/{tenantId}/role-assignments",
            new CreateRoleAssignmentRequest { UserId = person.UserId, RoleId = BuiltInRoles.All.Single(r => r.Name == role).Id },
            Signal9Json.Options, Ct);
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
        person.Assignments[tenantId] = response.Headers.Location!;
        return response.Headers.Location!;
    }

    private sealed record Person(EntraUser User, Guid UserId, Dictionary<Guid, Uri> Assignments);

    /// <summary>Connects to api's dashboard hub as <paramref name="person"/>, as a third-party application would.</summary>
    private Task<DashboardListener> ConnectAsync(Person person, params string[] scopes) => ConnectAsync(person.User, scopes);

    private async Task<DashboardListener> ConnectAsync(EntraUser user, params string[] scopes)
    {
        var hub = Build(await _factory.PersonTokenAsync(user, scopes));
        var listener = new DashboardListener(hub, Ct);
        await hub.StartAsync(Ct);
        // The hub handles a connection's messages only after OnConnectedAsync, so this answer means it joined its groups.
        await Assert.ThrowsExactlyAsync<HubException>(() => hub.InvokeAsync("Ping", Ct));
        return listener;
    }

    private HubConnection Build(string token) =>
        new HubConnectionBuilder()
            .WithUrl(new Uri(Signal9ApiFactory.HttpsLocalhost, HubRoutes.Dashboard.TrimStart('/')), options =>
            {
                options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
                options.AccessTokenProvider = () => Task.FromResult<string?>(token);
            })
            .AddJsonProtocol(o => Signal9Json.Configure(o.PayloadSerializerOptions))
            .Build();

    public void Dispose()
    {
        _superadministrator.Dispose();
        _factory.Dispose();
    }
}

/// <summary>Records every notice a dashboard hub connection hears.</summary>
internal sealed class DashboardListener : IAsyncDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);
    private readonly ConcurrentQueue<(string Method, object Notice)> _heard = new();
    private readonly SemaphoreSlim _arrived = new(0);
    private readonly CancellationToken _cancellationToken;
    private readonly TaskCompletionSource<Exception?> _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public DashboardListener(HubConnection hub, CancellationToken cancellationToken)
    {
        Hub = hub;
        _cancellationToken = cancellationToken;
        hub.On<DeviceNotice>(nameof(IDashboardHubClient.DeviceChanged), n => Record(nameof(IDashboardHubClient.DeviceChanged), n));
        hub.On<DeviceNotice>(nameof(IDashboardHubClient.DeviceDeleted), n => Record(nameof(IDashboardHubClient.DeviceDeleted), n));
        hub.On<TelemetryNotice>(nameof(IDashboardHubClient.TelemetryReceived), n => Record(nameof(IDashboardHubClient.TelemetryReceived), n));
        hub.On<CommandNotice>(nameof(IDashboardHubClient.CommandChanged), n => Record(nameof(IDashboardHubClient.CommandChanged), n));
        hub.Closed += error =>
        {
            _closed.TrySetResult(error);
            return Task.CompletedTask;
        };
    }

    public HubConnection Hub { get; }

    /// <summary>Completes when the server closes the connection.</summary>
    public Task Closed => _closed.Task;

    private void Record(string method, object notice)
    {
        _heard.Enqueue((method, notice));
        _arrived.Release();
    }

    /// <summary>How many notices arrived that match.</summary>
    public int Count(Func<(string Method, object Notice), bool> match) => _heard.Count(match);

    /// <summary>Whether a notice about the command <paramref name="commandId"/> arrived.</summary>
    public bool Heard(Guid commandId, bool command) => _heard.Any(h => h.Notice is CommandNotice c && c.CommandId == commandId);

    /// <summary>Whether any notice about <paramref name="deviceId"/> arrived.</summary>
    public bool Heard(Guid deviceId) => _heard.Any(h => DeviceOf(h.Notice) == deviceId);

    private static Guid DeviceOf(object notice) => notice switch
    {
        DeviceNotice d => d.DeviceId,
        CommandNotice c => c.DeviceId,
        TelemetryNotice t => t.DeviceId,
        _ => Guid.Empty,
    };

    public Task<CommandNotice> WaitForCommandAsync(Guid commandId) =>
        WaitForAsync(h => h.Notice is CommandNotice c && c.CommandId == commandId).ContinueWith(t => (CommandNotice)t.Result.Notice, TaskScheduler.Default);

    public Task<(string Method, object Notice)> WaitForDeviceAsync(Guid deviceId) => WaitForAsync(h => DeviceOf(h.Notice) == deviceId);

    public async Task<(string Method, object Notice)> WaitForAsync(Func<(string Method, object Notice), bool> match)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cancellationToken);
        timeout.CancelAfter(Timeout);
        while (true)
        {
            if (_heard.FirstOrDefault(match) is { Notice: not null } found)
                return found;
            try
            {
                await _arrived.WaitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!_cancellationToken.IsCancellationRequested)
            {
                throw new AssertFailedException($"No matching notice arrived; heard {string.Join(", ", _heard.Select(h => $"{h.Method} {h.Notice}"))}.");
            }
        }
    }

    public ValueTask DisposeAsync() => Hub.DisposeAsync();
}
