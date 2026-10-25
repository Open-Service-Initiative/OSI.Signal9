using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Reflection.Emit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;
using OSI.Signal9.Contracts;
using OSI.Signal9.Contracts.Agents;
using OSI.Signal9.Contracts.Commands;
using OSI.Signal9.Contracts.Devices;
using OSI.Signal9.Contracts.Serialization;
using OSI.Signal9.Contracts.Telemetry;
using OSI.Signal9.Contracts.Tenants;
using OSI.Signal9.Server.Data;

namespace OSI.Signal9.IntegrationTests;

/// <summary>
/// The central tenant filter (<see cref="TenantScope"/>, ADR-0001): a caller sees only rows owned by their assigned
/// tenants and those tenants' descendants.
/// </summary>
[TestClass]
public sealed class TenantScopeTests : IDisposable
{
    private readonly Signal9ApiFactory _factory = new();
    private readonly HttpClient _client;
    private readonly TestContext _testContext;
    private readonly Dictionary<Guid, TestAgent> _agents = [];

    public TenantScopeTests(TestContext testContext)
    {
        _testContext = testContext;
        _client = _factory.CreateClient();
    }

    private CancellationToken Ct => _testContext.CancellationToken;

    [TestMethod]
    public async Task A_caller_sees_their_assigned_tenant_and_every_descendant_but_no_sibling_or_ancestor()
    {
        var tree = await SeedAsync();
        using var caller = await _factory.CreateAssignedClientAsync([tree.Msp.Tenant], Signal9ApiFactory.GrantableScopes);

        CollectionAssert.AreEquivalent(new[] { tree.Msp.Tenant, tree.Client.Tenant, tree.Site.Tenant }, await TenantIdsAsync(caller));
        CollectionAssert.AreEquivalent(new[] { tree.Msp.Device, tree.Client.Device, tree.Site.Device }, await DeviceIdsAsync(caller));
        foreach (var visible in new[] { tree.Msp, tree.Client, tree.Site })
            await AssertVisibleAsync(caller, visible, HttpStatusCode.OK);
        foreach (var hidden in new[] { tree.Root, tree.Sibling })
            await AssertVisibleAsync(caller, hidden, HttpStatusCode.NotFound);
    }

    [TestMethod]
    public async Task Assignments_at_several_tenants_see_the_union_of_their_subtrees()
    {
        var tree = await SeedAsync();
        using var caller = await _factory.CreateAssignedClientAsync([tree.Client.Tenant, tree.Sibling.Tenant], Signal9ApiFactory.GrantableScopes);

        CollectionAssert.AreEquivalent(new[] { tree.Client.Tenant, tree.Site.Tenant, tree.Sibling.Tenant }, await TenantIdsAsync(caller));
        CollectionAssert.AreEquivalent(new[] { tree.Client.Device, tree.Site.Device, tree.Sibling.Device }, await DeviceIdsAsync(caller));
    }

    [TestMethod]
    public async Task A_caller_with_no_assigned_tenants_sees_nothing()
    {
        var tree = await SeedAsync();
        using var caller = await _factory.CreateAssignedClientAsync([], Signal9ApiFactory.GrantableScopes);

        Assert.IsEmpty(await TenantIdsAsync(caller));
        Assert.IsEmpty(await DeviceIdsAsync(caller));
        foreach (var hidden in new[] { tree.Root, tree.Msp, tree.Client, tree.Site, tree.Sibling })
            await AssertVisibleAsync(caller, hidden, HttpStatusCode.NotFound);
    }

    [TestMethod]
    public async Task Without_role_assignments_the_hosts_give_callers_no_assigned_tenants()
    {
        await SeedAsync();

        // auth keeps the hosts' default; the fixture replaces it in api only.
        await using var scope = _factory.Auth.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Signal9DbContext>();

        Assert.IsEmpty(db.AssignedTenantIds);
        Assert.AreEqual(0, await db.Tenants.CountAsync(Ct));
        Assert.AreEqual(0, await db.Devices.CountAsync(Ct));
        Assert.AreEqual(0, await db.Agents.CountAsync(Ct));
        Assert.AreEqual(0, await db.Commands.CountAsync(Ct));
        Assert.AreEqual(0, await db.TelemetrySamples.CountAsync(Ct));
    }

    [TestMethod]
    public async Task An_agent_reaches_its_own_records_though_it_is_assigned_no_tenants()
    {
        var tree = await SeedAsync();
        var agent = _agents[tree.Site.Agent].Client;

        var heartbeat = await agent.PutAsJsonAsync($"api/v1/agents/{tree.Site.Agent}/heartbeat", new HeartbeatRequest(), Signal9Json.Options, Ct);
        var pending = await agent.GetFromJsonAsync<PagedResult<CommandDto>>($"api/v1/agents/{tree.Site.Agent}/pending-commands", Signal9Json.Options, Ct);
        var telemetry = await agent.PostAsJsonAsync($"api/v1/agents/{tree.Site.Agent}/telemetry",
            new CreateTelemetrySampleRequest { CollectedAt = _factory.Time.GetUtcNow() }, Signal9Json.Options, Ct);
        var result = await agent.PutAsJsonAsync($"api/v1/devices/{tree.Site.Device}/commands/{tree.Site.Command}/result",
            new CommandResultRequest { Succeeded = true }, Signal9Json.Options, Ct);

        Assert.AreEqual(HttpStatusCode.OK, heartbeat.StatusCode);
        Assert.AreEqual(tree.Site.Command, Assert.ContainsSingle(pending!.Items).Id);
        Assert.AreEqual(HttpStatusCode.Created, telemetry.StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, result.StatusCode);
    }

    [TestMethod]
    public void The_filter_probes_the_closure_table_with_one_array_of_assigned_tenants()
    {
        using var db = CreateContext(new FixedAssignedTenants([Guid.NewGuid(), Guid.NewGuid()]));

        var sql = db.Devices.ToQueryString();

        StringAssert.Contains(sql, "\"TenantClosures\"");
        StringAssert.Contains(sql, "= ANY (");
    }

    [TestMethod]
    public async Task A_bypass_sees_every_tenant_and_names_its_reason_in_the_sql()
    {
        await SeedAsync();
        await using var db = CreateContext(NoAssignedTenants.Instance);

        var all = db.Tenants.IgnoreTenantScope(TenantScopeBypass.AgentSelfService);

        Assert.AreEqual(5, await all.CountAsync(Ct));
        Assert.AreEqual(0, await db.Tenants.CountAsync(Ct));
        StringAssert.Contains(all.ToQueryString(), $"TenantScope bypass: {nameof(TenantScopeBypass.AgentSelfService)}");
    }

    [TestMethod]
    public void Every_tenant_owned_entity_has_TenantScope()
    {
        using var db = CreateContext(NoAssignedTenants.Instance);

        var missing = TenantOwnedEntities(db.Model)
            .Where(t => t.FindDeclaredQueryFilter(TenantScope.Filter) is null)
            .Select(t => t.ClrType.Name)
            .ToList();

        Assert.IsEmpty(missing, $"Add TenantScope to: {string.Join(", ", missing)}");
    }

    [TestMethod]
    public void Tenant_owned_entities_are_found_through_their_foreign_keys_to_tenants()
    {
        using var db = CreateContext(NoAssignedTenants.Instance);

        var owned = TenantOwnedEntities(db.Model).Select(t => t.ClrType).ToList();

        CollectionAssert.AreEquivalent(
            new[] { typeof(Tenant), typeof(Device), typeof(Server.Data.Agent), typeof(Command), typeof(TelemetrySample), typeof(User), typeof(RoleAssignment), typeof(EnrollmentToken), typeof(ApiApplication) }, owned);
    }

    /// <summary>
    /// Tenants, and every entity reaching a tenant through its foreign keys, except the closure table: it is the tree
    /// itself, which the filter queries, so filtering it would make the filter recursive.
    /// </summary>
    private static IEnumerable<IEntityType> TenantOwnedEntities(IModel model)
    {
        var tenant = model.FindEntityType(typeof(Tenant))!;
        bool ReachesTenant(IEntityType type, HashSet<IEntityType> seen) =>
            type == tenant
            || (seen.Add(type) && type.GetForeignKeys().Any(fk => ReachesTenant(fk.PrincipalEntityType, seen)));

        return model.GetEntityTypes()
            .Where(t => !t.IsOwned() && t.BaseType is null && t.ClrType != typeof(TenantClosure))
            .Where(t => ReachesTenant(t, []));
    }

    [TestMethod]
    public void Only_TenantScope_ignores_query_filters()
    {
        Assembly[] production = [typeof(OSI.Signal9.API.ApiHost).Assembly, typeof(Signal9DbContext).Assembly, typeof(OSI.Signal9.Auth.AuthHost).Assembly];

        var violations = production.SelectMany(a => a.GetTypes())
            .Where(t => t != typeof(TenantScope))
            .SelectMany(IgnoreQueryFiltersCallers)
            .ToList();

        Assert.IsEmpty(violations, $"Bypass with IgnoreTenantScope(reason) instead: {string.Join(", ", violations)}");
    }

    [TestMethod]
    public void Every_bypass_declares_whether_its_uses_are_audited()
    {
        var undeclared = Enum.GetNames<TenantScopeBypass>()
            .Where(name =>
            {
                var field = typeof(TenantScopeBypass).GetField(name)!;
                var audited = field.IsDefined(typeof(AuditedAttribute), inherit: false);
                var notAudited = field.GetCustomAttribute<NotAuditedAttribute>();
                return audited == (notAudited is not null) || (notAudited is not null && string.IsNullOrWhiteSpace(notAudited.Reason));
            })
            .ToList();

        Assert.IsEmpty(undeclared, $"Mark each bypass [Audited] or [NotAudited(reason)]: {string.Join(", ", undeclared)}");
    }

    [TestMethod]
    public void A_bypass_that_declares_nothing_is_treated_as_audited()
    {
        using var db = CreateContext(NoAssignedTenants.Instance);

        Assert.IsTrue(TenantScope.IsAudited((TenantScopeBypass)999));
        Assert.ThrowsExactly<ArgumentException>(() => db.Tenants.IgnoreTenantScope((TenantScopeBypass)999));
    }

    [TestMethod]
    public void The_bypass_rule_catches_other_calls()
    {
        var violations = IgnoreQueryFiltersCallers(typeof(BypassesDirectly)).ToList();

        CollectionAssert.AreEquivalent(new[] { "BypassesDirectly.All", "BypassesDirectly.Named" }, violations);
    }

    private static class BypassesDirectly
    {
        public static IQueryable<Tenant> All(Signal9DbContext db) => db.Tenants.IgnoreQueryFilters();

        public static IQueryable<Tenant> Named(Signal9DbContext db) => db.Tenants.IgnoreQueryFilters([TenantScope.Filter]);

        public static IQueryable<Tenant> Scoped(Signal9DbContext db) => db.Tenants.IgnoreTenantScope(TenantScopeBypass.AgentSelfService);
    }

    /// <summary>
    /// Methods of <paramref name="type"/> (and the lambdas and async state machines nested in it) that call
    /// <see cref="EntityFrameworkQueryableExtensions.IgnoreQueryFilters{TEntity}(IQueryable{TEntity})"/>.
    /// </summary>
    private static IEnumerable<string> IgnoreQueryFiltersCallers(Type type)
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        var methods = type.GetMethods(all).Cast<MethodBase>().Concat(type.GetConstructors(all));
        var callers = from method in methods
                      where CalledMethods(method).Any(m =>
                          m.DeclaringType == typeof(EntityFrameworkQueryableExtensions) && m.Name == nameof(EntityFrameworkQueryableExtensions.IgnoreQueryFilters))
                      select $"{type.Name}.{method.Name}";
        return callers.Concat(type.GetNestedTypes(all).SelectMany(IgnoreQueryFiltersCallers)).Distinct();
    }

    private static readonly Dictionary<short, OpCode> OpCodesByValue = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(f => (OpCode)f.GetValue(null)!)
        .ToDictionary(o => o.Value);

    private static IEnumerable<MethodBase> CalledMethods(MethodBase method)
    {
        if (method.GetMethodBody()?.GetILAsByteArray() is not { } il)
            yield break;
        var typeArguments = method.DeclaringType is { IsGenericType: true } declaring ? declaring.GetGenericArguments() : null;
        var methodArguments = method.IsGenericMethod ? method.GetGenericArguments() : null;
        for (var i = 0; i < il.Length;)
        {
            var value = (short)il[i++];
            if (value == 0xFE)
                value = unchecked((short)(0xFE00 | il[i++]));
            var opCode = OpCodesByValue[value];
            if (opCode.OperandType == OperandType.InlineMethod)
            {
                MethodBase? called = null;
                try
                {
                    called = method.Module.ResolveMethod(BitConverter.ToInt32(il, i), typeArguments, methodArguments);
                }
                catch (ArgumentException)
                {
                }
                if (called is not null)
                    yield return called;
            }
            i += opCode.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => 4 + (4 * BitConverter.ToInt32(il, i)),
                _ => 4,
            };
        }
    }

    private Signal9DbContext CreateContext(IAssignedTenants assigned) =>
        new(new DbContextOptionsBuilder<Signal9DbContext>().UseNpgsql(_factory.ConnectionString).Options, assigned);

    private sealed class FixedAssignedTenants(Guid[] ids) : IAssignedTenants
    {
        public bool IsSuperadministrator => false;

        public IReadOnlyCollection<Guid> TenantIds => ids;
    }

    private sealed record Branch(Guid Tenant, Guid Device, Guid Agent, Guid Command, Guid Sample);

    /// <summary>Root ─ MSP ─ Client ─ Site, and Root ─ Sibling; each tenant has a device with an agent, a command and a sample.</summary>
    private sealed record Tree(Branch Root, Branch Msp, Branch Client, Branch Site, Branch Sibling);

    private async Task<Tree> SeedAsync()
    {
        var root = await BranchAsync("Root", parent: null);
        var msp = await BranchAsync("Msp", root.Tenant);
        var client = await BranchAsync("Client", msp.Tenant);
        var site = await BranchAsync("Site", client.Tenant);
        var sibling = await BranchAsync("Sibling", root.Tenant);
        return new Tree(root, msp, client, site, sibling);
    }

    private async Task<Branch> BranchAsync(string name, Guid? parent)
    {
        var tenant = await _factory.CreateTenantAsync(_client, name, parentTenantId: parent);
        var enrolled = await _factory.EnrollAgentAsync(tenant.Id, $"{name}-PC");
        _agents[enrolled.Id] = enrolled;
        var agent = enrolled.Agent;
        var command = await _client.PostAsJsonAsync($"api/v1/devices/{agent.DeviceId}/commands",
            new CreateCommandRequest { Type = CommandType.CollectSystemInfo }, Signal9Json.Options, Ct);
        Assert.AreEqual(HttpStatusCode.Created, command.StatusCode);
        var sample = await enrolled.Client.PostAsJsonAsync($"api/v1/agents/{agent.Id}/telemetry",
            new CreateTelemetrySampleRequest { CollectedAt = _factory.Time.GetUtcNow() }, Signal9Json.Options, Ct);
        Assert.AreEqual(HttpStatusCode.Created, sample.StatusCode);
        return new Branch(tenant.Id, agent.DeviceId, agent.Id,
            (await command.Content.ReadFromJsonAsync<CommandDto>(Signal9Json.Options, Ct))!.Id,
            (await sample.Content.ReadFromJsonAsync<TelemetrySampleDto>(Signal9Json.Options, Ct))!.Id);
    }

    private async Task<Guid[]> TenantIdsAsync(HttpClient caller) =>
        [.. (await caller.GetFromJsonAsync<PagedResult<TenantDto>>("api/v1/tenants?pageSize=100", Signal9Json.Options, Ct))!.Items.Select(t => t.Id)];

    private async Task<Guid[]> DeviceIdsAsync(HttpClient caller) =>
        [.. (await caller.GetFromJsonAsync<PagedResult<DeviceDto>>("api/v1/devices?pageSize=100", Signal9Json.Options, Ct))!.Items.Select(d => d.Id)];

    /// <summary>Every canonical URL of the branch's records answers <paramref name="expected"/>.</summary>
    private async Task AssertVisibleAsync(HttpClient caller, Branch branch, HttpStatusCode expected)
    {
        string[] urls =
        [
            $"api/v1/tenants/{branch.Tenant}",
            $"api/v1/devices/{branch.Device}",
            $"api/v1/agents/{branch.Agent}",
            $"api/v1/devices/{branch.Device}/commands",
            $"api/v1/devices/{branch.Device}/commands/{branch.Command}",
            $"api/v1/devices/{branch.Device}/telemetry",
            $"api/v1/devices/{branch.Device}/telemetry/{branch.Sample}",
        ];
        foreach (var url in urls)
            Assert.AreEqual(expected, (await caller.GetAsync(url, Ct)).StatusCode, url);
    }

    public void Dispose()
    {
        foreach (var agent in _agents.Values)
            agent.Dispose();
        _client.Dispose();
        _factory.Dispose();
    }
}
