using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using OSI.Signal9.Contracts.Agents;
using OSI.Signal9.Contracts.Audit;
using OSI.Signal9.Contracts;
using OSI.Signal9.Contracts.Roles;
using OSI.Signal9.Contracts.Serialization;
using OSI.Signal9.Contracts.Tenants;
using OSI.Signal9.Contracts.Users;
using OSI.Signal9.Server.Identity;

namespace OSI.Signal9.IntegrationTests;

/// <summary>
/// Tenant transfers (#122, stories 5-10, 21): an Administrator above a tenant proposes moving it, with its
/// descendants, to a new parent; the receiving side accepts or declines; acceptance moves the subtree at once.
/// </summary>
[TestClass]
public sealed class TenantTransferTests : IDisposable
{
    private readonly Signal9ApiFactory _factory = new();
    private readonly HttpClient _superadministrator;
    private readonly TestContext _testContext;

    public TenantTransferTests(TestContext testContext)
    {
        _testContext = testContext;
        _superadministrator = _factory.CreateSuperadministratorClient();
    }

    private CancellationToken Ct => _testContext.CancellationToken;

    [TestMethod]
    public async Task Accepting_moves_the_subtree_and_access_follows_at_once()
    {
        var tree = await TreeAsync();
        using var mspA = await PersonAsync(tree.MspA, ("Administrator", tree.MspA));
        using var mspB = await PersonAsync(tree.MspB, ("Administrator", tree.MspB));

        var proposed = await ProposeAsync(mspA, tree.Client, tree.MspB);
        Assert.AreEqual(HttpStatusCode.Created, proposed.StatusCode, await proposed.Content.ReadAsStringAsync(Ct));
        var transfer = (await proposed.Content.ReadFromJsonAsync<TenantTransferDto>(Signal9Json.Options, Ct))!;
        Assert.AreEqual(TenantTransferStatus.Pending, transfer.Status);
        StringAssert.EndsWith(proposed.Headers.Location!.ToString(), $"/api/v1/tenants/{tree.Client}/transfers/{transfer.Id}");
        // Nothing moves until the receiving side accepts.
        Assert.AreEqual(HttpStatusCode.OK, (await mspA.GetAsync($"api/v1/tenants/{tree.Site}", Ct)).StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, (await mspB.GetAsync($"api/v1/tenants/{tree.Client}", Ct)).StatusCode);

        var accepted = await mspB.PostAsync($"api/v1/tenants/{tree.Client}/transfers/{transfer.Id}:accept", null, Ct);

        Assert.AreEqual(HttpStatusCode.OK, accepted.StatusCode, await accepted.Content.ReadAsStringAsync(Ct));
        Assert.AreEqual(TenantTransferStatus.Accepted,
            (await accepted.Content.ReadFromJsonAsync<TenantTransferDto>(Signal9Json.Options, Ct))!.Status);
        Assert.AreEqual(tree.MspB, (await GetTenantAsync(_superadministrator, tree.Client)).ParentTenantId);
        Assert.AreEqual(HttpStatusCode.OK, (await mspB.GetAsync($"api/v1/tenants/{tree.Client}", Ct)).StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, (await mspB.GetAsync($"api/v1/tenants/{tree.Site}", Ct)).StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, (await mspA.GetAsync($"api/v1/tenants/{tree.Client}", Ct)).StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, (await mspA.GetAsync($"api/v1/tenants/{tree.Site}", Ct)).StatusCode);
    }

    [TestMethod]
    public async Task An_administrator_cant_propose_or_accept_a_transfer_of_their_own_assigned_tenant()
    {
        var tree = await TreeAsync();
        using var atClient = await PersonAsync(tree.Client, ("Administrator", tree.Client));
        // Administrator at the tenant being transferred, and at the receiving tenant too.
        using var atClientAndMspB = await PersonAsync(tree.Client, ("Administrator", tree.Client), ("Administrator", tree.MspB));
        using var mspA = await PersonAsync(tree.MspA, ("Administrator", tree.MspA));

        var byOwnAdministrator = await ProposeAsync(atClient, tree.Client, tree.MspB);
        var transfer = await ProposedAsync(mspA, tree.Client, tree.MspB);
        var acceptedByOwnAdministrator = await AcceptAsync(atClientAndMspB, transfer);

        Assert.AreEqual(HttpStatusCode.Forbidden, byOwnAdministrator.StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, acceptedByOwnAdministrator.StatusCode);
        Assert.AreEqual(TenantTransferStatus.Pending, (await GetTransferAsync(_superadministrator, transfer)).Status);
        Assert.AreEqual(tree.MspA, (await GetTenantAsync(_superadministrator, tree.Client)).ParentTenantId);
    }

    [TestMethod]
    public async Task Only_the_receiving_side_accepts_or_declines()
    {
        var tree = await TreeAsync();
        using var mspA = await PersonAsync(tree.MspA, ("Administrator", tree.MspA));
        using var viewerAtMspB = await PersonAsync(tree.MspB, ("Viewer", tree.MspB));
        using var outsider = await PersonAsync(tree.RootB, ("Administrator", tree.RootB));
        var transfer = await ProposedAsync(mspA, tree.Client, tree.MspB);
        using var unrelated = await PersonAsync(tree.RootA, ("Administrator", (await _factory.CreateTenantAsync(_superadministrator, "Other")).Id));

        // The proposer sees the transfer, but deciding it is for the receiving side.
        Assert.AreEqual(HttpStatusCode.Forbidden, (await AcceptAsync(mspA, transfer)).StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await DeclineAsync(mspA, transfer)).StatusCode);
        // A Viewer can't transfer tenants, and someone outside both sides can't see the transfer.
        Assert.AreEqual(HttpStatusCode.NotFound, (await AcceptAsync(viewerAtMspB, transfer)).StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, (await AcceptAsync(unrelated, transfer)).StatusCode);
        Assert.AreEqual(TenantTransferStatus.Pending, (await GetTransferAsync(_superadministrator, transfer)).Status);

        // An Administrator above the receiving tenant is on the receiving side too.
        var declined = await DeclineAsync(outsider, transfer);

        Assert.AreEqual(HttpStatusCode.OK, declined.StatusCode, await declined.Content.ReadAsStringAsync(Ct));
        Assert.AreEqual(TenantTransferStatus.Declined, (await GetTransferAsync(mspA, transfer)).Status);
        Assert.AreEqual(tree.MspA, (await GetTenantAsync(_superadministrator, tree.Client)).ParentTenantId);
    }

    [TestMethod]
    public async Task A_decided_transfer_cant_be_decided_again()
    {
        var tree = await TreeAsync();
        using var mspA = await PersonAsync(tree.MspA, ("Administrator", tree.MspA));
        using var mspB = await PersonAsync(tree.MspB, ("Administrator", tree.MspB));
        var declined = await ProposedAsync(mspA, tree.Client, tree.MspB);
        Assert.AreEqual(HttpStatusCode.OK, (await DeclineAsync(mspB, declined)).StatusCode);

        var acceptDeclined = await AcceptAsync(mspB, declined);
        var accepted = await ProposedAsync(mspA, tree.Client, tree.MspB);
        Assert.AreEqual(HttpStatusCode.OK, (await AcceptAsync(mspB, accepted)).StatusCode);
        var declineAccepted = await DeclineAsync(mspB, accepted);

        Assert.AreEqual(HttpStatusCode.Conflict, acceptDeclined.StatusCode);
        Assert.AreEqual("application/problem+json", acceptDeclined.Content.Headers.ContentType?.MediaType);
        Assert.AreEqual(HttpStatusCode.Conflict, declineAccepted.StatusCode);
        Assert.AreEqual(TenantTransferStatus.Declined, (await GetTransferAsync(_superadministrator, declined)).Status);
    }

    [TestMethod]
    public async Task A_transfer_that_would_create_a_cycle_is_refused()
    {
        var tree = await TreeAsync();
        using var rootA = await PersonAsync(tree.RootA, ("Administrator", tree.RootA));

        var underDescendant = await ProposeAsync(rootA, tree.MspA, tree.Site);
        var underItself = await ProposeAsync(rootA, tree.Client, tree.Client);

        Assert.AreEqual(HttpStatusCode.Conflict, underDescendant.StatusCode);
        Assert.AreEqual("application/problem+json", underDescendant.Content.Headers.ContentType?.MediaType);
        Assert.AreEqual(HttpStatusCode.Conflict, underItself.StatusCode);
        Assert.AreEqual(tree.RootA, (await GetTenantAsync(_superadministrator, tree.MspA)).ParentTenantId);
    }

    [TestMethod]
    public async Task Accepting_is_refused_when_the_tree_has_changed_so_the_move_would_now_create_a_cycle()
    {
        var tree = await TreeAsync();
        using var mspA = await PersonAsync(tree.MspA, ("Administrator", tree.MspA));
        using var mspB = await PersonAsync(tree.MspB, ("Administrator", tree.MspB));
        var transfer = await ProposedAsync(mspA, tree.Client, tree.MspB);
        // Meanwhile Root B's whole tree moves beneath Site, so MSP B is now beneath Client.
        await TransferAsync(tree.RootB, tree.Site);

        var accepted = await AcceptAsync(mspB, transfer);

        Assert.AreEqual(HttpStatusCode.Conflict, accepted.StatusCode);
        Assert.AreEqual(TenantTransferStatus.Pending, (await GetTransferAsync(_superadministrator, transfer)).Status);
        Assert.AreEqual(tree.MspA, (await GetTenantAsync(_superadministrator, tree.Client)).ParentTenantId);
    }

    [TestMethod]
    public async Task A_transfer_beyond_the_maximum_depth_is_refused()
    {
        // Right's leaf is 11 below its root. Moved under Left's depth-9 leaf, that leaf would sit at depth 21;
        // under Left's depth-8 tenant, at exactly the maximum of 20.
        var left = await ChainAsync("Left", deepest: 9);
        var right = await ChainAsync("Right", deepest: 11);

        var tooDeep = await ProposeAsync(_superadministrator, right[0], left[^1]);
        var deepest = await ProposeAsync(_superadministrator, right[0], left[^2]);

        Assert.AreEqual(HttpStatusCode.Conflict, tooDeep.StatusCode);
        Assert.AreEqual("application/problem+json", tooDeep.Content.Headers.ContentType?.MediaType);
        Assert.AreEqual(HttpStatusCode.Created, deepest.StatusCode, await deepest.Content.ReadAsStringAsync(Ct));
    }

    [TestMethod]
    public async Task Accepting_is_refused_when_the_tree_has_grown_so_the_move_would_now_be_too_deep()
    {
        var left = await ChainAsync("Left", deepest: 8);
        var right = await ChainAsync("Right", deepest: 11);
        var transfer = await ProposedAsync(_superadministrator, right[0], left[^1]);
        // Right's subtree grows by one level: beneath Left's depth-8 tenant, its leaf would now sit at depth 21.
        await _factory.CreateTenantAsync(_superadministrator, "Right 12", parentTenantId: right[^1]);

        var accepted = await AcceptAsync(_superadministrator, transfer);

        Assert.AreEqual(HttpStatusCode.Conflict, accepted.StatusCode);
        Assert.IsNull((await GetTenantAsync(_superadministrator, right[0])).ParentTenantId);
    }

    [TestMethod]
    public async Task A_maximum_depth_of_zero_refuses_any_transfer_beneath_another_tenant()
    {
        using var host = _factory.WithWebHostBuilder(builder => builder.UseSetting("TenantTree:MaxDepth", "0"));
        using var superadministrator = _factory.CreateSuperadministratorClient(host);
        var left = await _factory.CreateTenantAsync(superadministrator, "Left");
        var right = await _factory.CreateTenantAsync(superadministrator, "Right");

        var proposed = await ProposeAsync(superadministrator, right.Id, left.Id);

        Assert.AreEqual(HttpStatusCode.Conflict, proposed.StatusCode);
    }

    [TestMethod]
    public async Task A_transfer_that_would_leave_two_devices_for_one_machine_in_a_root_tree_is_refused_listing_them()
    {
        var tree = await TreeAsync();
        using var mspA = await PersonAsync(tree.MspA, ("Administrator", tree.MspA));
        using var mspB = await PersonAsync(tree.MspB, ("Administrator", tree.MspB));
        var uuid = Guid.NewGuid();
        using var movingByUuid = await EnrollAsync(tree.Site, r => r with { SmbiosUuid = uuid });
        using var movingBySerial = await EnrollAsync(tree.Client, r => r with { Manufacturer = "Contoso", SerialNumber = "SN-1" });
        using var unique = await EnrollAsync(tree.Client, r => r with { SmbiosUuid = Guid.NewGuid() });
        // Elsewhere in Root B's tree, not beneath MSP B: the rule is per root tree.
        var elsewhere = await _factory.CreateTenantAsync(_superadministrator, "Elsewhere", parentTenantId: tree.RootB);
        using var existingByUuid = await EnrollAsync(elsewhere.Id, r => r with { SmbiosUuid = uuid });
        using var existingBySerial = await EnrollAsync(tree.MspB, r => r with { Manufacturer = "contoso ", SerialNumber = "sn-1" });
        var transfer = await ProposedAsync(mspA, tree.Client, tree.MspB);

        var accepted = await AcceptAsync(mspB, transfer);

        Assert.AreEqual(HttpStatusCode.Conflict, accepted.StatusCode);
        Assert.AreEqual("application/problem+json", accepted.Content.Headers.ContentType?.MediaType);
        var problem = await accepted.Content.ReadFromJsonAsync<JsonElement>(Signal9Json.Options, Ct);
        var duplicates = problem.GetProperty("duplicateDevices").Deserialize<DuplicateDeviceDto[]>(Signal9Json.Options)!;
        CollectionAssert.AreEquivalent(
            new[]
            {
                new DuplicateDeviceDto(movingByUuid.DeviceId, existingByUuid.DeviceId),
                new DuplicateDeviceDto(movingBySerial.DeviceId, existingBySerial.DeviceId),
            },
            duplicates);
        Assert.AreEqual(TenantTransferStatus.Pending, (await GetTransferAsync(_superadministrator, transfer)).Status);
        Assert.AreEqual(tree.MspA, (await GetTenantAsync(_superadministrator, tree.Client)).ParentTenantId);
    }

    [TestMethod]
    public async Task Within_one_root_tree_a_transfer_brings_in_no_machine_so_shared_hardware_doesnt_refuse_it()
    {
        var tree = await TreeAsync();
        var mspA2 = await _factory.CreateTenantAsync(_superadministrator, "MspA2", parentTenantId: tree.RootA);
        var uuid = Guid.NewGuid();
        using var moving = await EnrollAsync(tree.Site, r => r with { SmbiosUuid = uuid });
        using var kept = await EnrollAsync(mspA2.Id, r => r with { SmbiosUuid = uuid });
        var transfer = await ProposedAsync(_superadministrator, tree.Client, mspA2.Id);

        var accepted = await AcceptAsync(_superadministrator, transfer);

        Assert.AreEqual(HttpStatusCode.OK, accepted.StatusCode, await accepted.Content.ReadAsStringAsync(Ct));
    }

    [TestMethod]
    public async Task Proposal_acceptance_decline_and_completion_are_audited_once_for_each_side()
    {
        var tree = await TreeAsync();
        using var mspA = await PersonAsync(tree.MspA, ("Administrator", tree.MspA));
        using var mspB = await PersonAsync(tree.MspB, ("Administrator", tree.MspB));
        var declined = await ProposedAsync(mspA, tree.Client, tree.MspB);
        Assert.AreEqual(HttpStatusCode.OK, (await DeclineAsync(mspB, declined)).StatusCode);
        var accepted = await ProposedAsync(mspA, tree.Client, tree.MspB);
        Assert.AreEqual(HttpStatusCode.OK, (await AcceptAsync(mspB, accepted)).StatusCode);

        // Both sides keep the whole trail, even after the tenant has left one of them.
        foreach (var (side, at) in new[] { (mspA, tree.MspA), (mspB, tree.MspB) })
        {
            var trail = await AuditTrailAsync(side);
            CollectionAssert.AreEquivalent(
                new[] { AuditActions.TenantTransferProposed, AuditActions.TenantTransferDeclined },
                trail.Where(e => e.TargetId == declined.Id).Select(e => e.Action).ToArray());
            CollectionAssert.AreEquivalent(
                new[] { AuditActions.TenantTransferProposed, AuditActions.TenantTransferAccepted, AuditActions.TenantTransferCompleted },
                trail.Where(e => e.TargetId == accepted.Id).Select(e => e.Action).ToArray());
            Assert.IsTrue(trail.Where(e => e.TargetType == "tenant-transfer").All(e => e.TenantId == at));
        }
        var proposal = (await AuditTrailAsync(mspB)).Single(e => e.TargetId == accepted.Id && e.Action == AuditActions.TenantTransferProposed);
        Assert.AreEqual(AuditOutcome.Succeeded, proposal.Outcome);
        Assert.AreEqual(AuditActorKind.Person, proposal.Actor.Kind);
        StringAssert.Contains(proposal.Detail, tree.Client.ToString());
    }

    [TestMethod]
    public async Task Each_side_lists_its_pending_accepted_and_declined_transfers()
    {
        var tree = await TreeAsync();
        var client2 = await _factory.CreateTenantAsync(_superadministrator, "Client2", parentTenantId: tree.MspA);
        var client3 = await _factory.CreateTenantAsync(_superadministrator, "Client3", parentTenantId: tree.MspA);
        using var mspA = await PersonAsync(tree.MspA, ("Administrator", tree.MspA));
        using var mspB = await PersonAsync(tree.MspB, ("Administrator", tree.MspB));
        using var rootB = await PersonAsync(tree.RootB, ("Administrator", tree.RootB));
        var pending = await ProposedAsync(mspA, tree.Client, tree.MspB);
        var declined = await ProposedAsync(mspA, client2.Id, tree.MspB);
        var accepted = await ProposedAsync(mspA, client3.Id, tree.MspB);
        Assert.AreEqual(HttpStatusCode.OK, (await DeclineAsync(mspB, declined)).StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, (await AcceptAsync(mspB, accepted)).StatusCode);

        var outgoing = await ListAsync(mspA, tree.MspA, "direction=outgoing");
        var incoming = await ListAsync(mspB, tree.MspB, "direction=incoming");

        CollectionAssert.AreEquivalent(
            new[] { (pending.Id, TenantTransferStatus.Pending), (declined.Id, TenantTransferStatus.Declined), (accepted.Id, TenantTransferStatus.Accepted) },
            outgoing.Select(t => (t.Id, t.Status)).ToArray());
        CollectionAssert.AreEquivalent(outgoing.Select(t => t.Id).ToArray(), incoming.Select(t => t.Id).ToArray());
        // Each names its canonical URL, under the transferred tenant, whichever side listed it.
        foreach (var transfer in outgoing.Concat(incoming))
            Assert.AreEqual($"/api/v1/tenants/{transfer.TenantId}/transfers/{transfer.Id}", transfer.Url);
        // The receiving side sees what it's being sent, by name, before it can see the tenant itself.
        var offered = incoming.Single(t => t.Id == pending.Id);
        Assert.AreEqual("Client", offered.TenantName);
        Assert.AreEqual("MspB", offered.ToParentTenantName);
        Assert.AreEqual(tree.MspA, offered.FromParentTenantId);
        Assert.IsEmpty(await ListAsync(mspA, tree.MspA, "direction=incoming"));
        Assert.IsEmpty(await ListAsync(mspB, tree.MspB, "direction=outgoing"));
        // From above the receiving tenant, its descendants' transfers are included unless asked otherwise.
        Assert.HasCount(3, await ListAsync(rootB, tree.RootB, "direction=incoming"));
        Assert.IsEmpty(await ListAsync(rootB, tree.RootB, "includeDescendants=false"));
        Assert.HasCount(1, await ListAsync(mspB, tree.MspB, "status=pending"));
        Assert.AreEqual(HttpStatusCode.NotFound, (await mspB.GetAsync($"api/v1/tenants/{tree.MspA}/transfers", Ct)).StatusCode);
    }

    [TestMethod]
    public async Task The_proposing_side_can_cancel_a_pending_transfer_and_then_propose_another()
    {
        var tree = await TreeAsync();
        using var mspA = await PersonAsync(tree.MspA, ("Administrator", tree.MspA));
        using var mspB = await PersonAsync(tree.MspB, ("Administrator", tree.MspB));
        var transfer = await ProposedAsync(mspA, tree.Client, tree.MspB);

        var second = await ProposeAsync(mspA, tree.Client, tree.RootB);
        var cancelledByReceiver = await CancelAsync(mspB, transfer);
        var cancelled = await CancelAsync(mspA, transfer);
        var again = await ProposeAsync(mspA, tree.Client, tree.RootB);

        Assert.AreEqual(HttpStatusCode.Conflict, second.StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, cancelledByReceiver.StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, cancelled.StatusCode, await cancelled.Content.ReadAsStringAsync(Ct));
        Assert.AreEqual(TenantTransferStatus.Cancelled, (await GetTransferAsync(mspB, transfer)).Status);
        Assert.AreEqual(HttpStatusCode.Conflict, (await AcceptAsync(mspB, transfer)).StatusCode);
        Assert.AreEqual(HttpStatusCode.Created, again.StatusCode);
        CollectionAssert.Contains((await AuditTrailAsync(mspB)).Where(e => e.TargetId == transfer.Id).Select(e => e.Action).ToArray(),
            AuditActions.TenantTransferCancelled);
    }

    [TestMethod]
    public async Task Proposing_needs_an_existing_new_parent_and_a_tenant_within_reach()
    {
        var tree = await TreeAsync();
        using var mspA = await PersonAsync(tree.MspA, ("Administrator", tree.MspA));

        var missingParent = await ProposeAsync(mspA, tree.Client, Guid.NewGuid());
        var noParent = await mspA.PostAsJsonAsync($"api/v1/tenants/{tree.Client}/transfers", new { }, Ct);
        var outOfReach = await ProposeAsync(mspA, tree.MspB, tree.Client);
        var alreadyParent = await ProposeAsync(mspA, tree.Client, tree.MspA);

        Assert.AreEqual(HttpStatusCode.BadRequest, missingParent.StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest, noParent.StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, outOfReach.StatusCode);
        Assert.AreEqual(HttpStatusCode.Conflict, alreadyParent.StatusCode);
    }

    [TestMethod]
    public async Task Accepting_with_a_stale_etag_is_refused()
    {
        var tree = await TreeAsync();
        var transfer = await ProposedAsync(_superadministrator, tree.Client, tree.MspB);
        var read = await _superadministrator.GetAsync(UrlOf(transfer), Ct);
        Assert.AreEqual(HttpStatusCode.OK, (await CancelAsync(_superadministrator, transfer)).StatusCode);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{UrlOf(transfer)}:accept");
        request.Headers.IfMatch.Add(read.Headers.ETag!);
        var accepted = await _superadministrator.SendAsync(request, Ct);

        Assert.AreEqual(HttpStatusCode.PreconditionFailed, accepted.StatusCode);
    }

    private sealed record Tree(Guid RootA, Guid MspA, Guid Client, Guid Site, Guid RootB, Guid MspB);

    /// <summary>Root A ─ MSP A ─ Client ─ Site, and Root B ─ MSP B: two separate tenant trees.</summary>
    private async Task<Tree> TreeAsync()
    {
        var rootA = await _factory.CreateTenantAsync(_superadministrator, "RootA");
        var mspA = await _factory.CreateTenantAsync(_superadministrator, "MspA", parentTenantId: rootA.Id);
        var client = await _factory.CreateTenantAsync(_superadministrator, "Client", parentTenantId: mspA.Id);
        var site = await _factory.CreateTenantAsync(_superadministrator, "Site", parentTenantId: client.Id);
        var rootB = await _factory.CreateTenantAsync(_superadministrator, "RootB");
        var mspB = await _factory.CreateTenantAsync(_superadministrator, "MspB", parentTenantId: rootB.Id);
        return new Tree(rootA.Id, mspA.Id, client.Id, site.Id, rootB.Id, mspB.Id);
    }

    private async Task<TenantTransferDto> ProposedAsync(HttpClient caller, Guid tenantId, Guid toParentTenantId)
    {
        var response = await ProposeAsync(caller, tenantId, toParentTenantId);
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<TenantTransferDto>(Signal9Json.Options, Ct))!;
    }

    private static string UrlOf(TenantTransferDto transfer) => transfer.Url.TrimStart('/');

    private Task<HttpResponseMessage> AcceptAsync(HttpClient caller, TenantTransferDto transfer) =>
        caller.PostAsync($"{UrlOf(transfer)}:accept", null, Ct);

    private Task<HttpResponseMessage> DeclineAsync(HttpClient caller, TenantTransferDto transfer) =>
        caller.PostAsync($"{UrlOf(transfer)}:decline", null, Ct);

    private Task<HttpResponseMessage> CancelAsync(HttpClient caller, TenantTransferDto transfer) =>
        caller.PostAsync($"{UrlOf(transfer)}:cancel", null, Ct);

    private async Task<TenantTransferDto> GetTransferAsync(HttpClient caller, TenantTransferDto transfer) =>
        (await caller.GetFromJsonAsync<TenantTransferDto>(UrlOf(transfer), Signal9Json.Options, Ct))!;

    /// <summary>Moves a tenant as a Superadministrator, who is on both sides of every transfer.</summary>
    private async Task TransferAsync(Guid tenantId, Guid toParentTenantId)
    {
        var transfer = await ProposedAsync(_superadministrator, tenantId, toParentTenantId);
        var accepted = await AcceptAsync(_superadministrator, transfer);
        Assert.AreEqual(HttpStatusCode.OK, accepted.StatusCode, await accepted.Content.ReadAsStringAsync(Ct));
    }

    // A chain of tenants from a root at depth 0 down to depth `deepest`.
    private async Task<List<Guid>> ChainAsync(string name, int deepest)
    {
        List<Guid> chain = [];
        for (var depth = 0; depth <= deepest; depth++)
            chain.Add((await _factory.CreateTenantAsync(_superadministrator, $"{name} {depth}",
                parentTenantId: chain.Count == 0 ? null : chain[^1])).Id);
        return chain;
    }

    private async Task<List<TenantTransferDto>> ListAsync(HttpClient caller, Guid tenantId, string query) =>
        [.. (await caller.GetFromJsonAsync<PagedResult<TenantTransferDto>>($"api/v1/tenants/{tenantId}/transfers?{query}",
            Signal9Json.Options, Ct))!.Items];

    private async Task<List<AuditEntryDto>> AuditTrailAsync(HttpClient caller) =>
        [.. (await caller.GetFromJsonAsync<PagedResult<AuditEntryDto>>("api/v1/audit-entries?pageSize=100",
            Signal9Json.Options, Ct))!.Items];

    private Task<TestAgent> EnrollAsync(Guid tenantId, Func<EnrollAgentRequest, EnrollAgentRequest> hardware) =>
        _factory.EnrollAgentAsync(tenantId, hardware: hardware);

    private Task<HttpResponseMessage> ProposeAsync(HttpClient caller, Guid tenantId, Guid toParentTenantId) =>
        caller.PostAsJsonAsync($"api/v1/tenants/{tenantId}/transfers",
            new ProposeTenantTransferRequest { ToParentTenantId = toParentTenantId }, Signal9Json.Options, Ct);

    private async Task<TenantDto> GetTenantAsync(HttpClient caller, Guid tenantId) =>
        (await caller.GetFromJsonAsync<TenantDto>($"api/v1/tenants/{tenantId}", Signal9Json.Options, Ct))!;

    /// <summary>A newly signed-in person, owned by <paramref name="owner"/>, holding these role assignments.</summary>
    private async Task<HttpClient> PersonAsync(Guid owner, params (string Role, Guid Tenant)[] assignments)
    {
        var person = EntraUser.WithMfa();
        var user = await CreateUserAsync(owner, person);
        foreach (var (role, tenant) in assignments)
            await AssignAsync(tenant, user.Id, role);
        return await _factory.CreatePersonClientAsync(person);
    }

    private async Task<UserDto> CreateUserAsync(Guid tenantId, EntraUser person)
    {
        var response = await _superadministrator.PostAsJsonAsync("api/v1/users", new CreateUserRequest
        {
            TenantId = tenantId,
            EntraTenantId = person.TenantId,
            EntraObjectId = person.ObjectId,
            DisplayName = "Alex Wilber",
        }, Signal9Json.Options, Ct);
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<UserDto>(Signal9Json.Options, Ct))!;
    }

    private async Task AssignAsync(Guid tenantId, Guid userId, string role)
    {
        var response = await _superadministrator.PostAsJsonAsync($"api/v1/tenants/{tenantId}/role-assignments",
            new CreateRoleAssignmentRequest { UserId = userId, RoleId = BuiltInRoles.All.Single(r => r.Name == role).Id },
            Signal9Json.Options, Ct);
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
    }

    public void Dispose()
    {
        _superadministrator.Dispose();
        _factory.Dispose();
    }
}
