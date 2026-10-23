using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using OSI.Signal9.Server.Data;
using OSI.Signal9.API.Tenants;
using OSI.Signal9.Contracts.Serialization;
using OSI.Signal9.Contracts.Tenants;

namespace OSI.Signal9.IntegrationTests;

[TestClass]
public sealed class TenantTreeTests : IDisposable
{
    private readonly Signal9ApiFactory _factory = new();
    private readonly HttpClient _client;
    private readonly TestContext _testContext;

    public TenantTreeTests(TestContext testContext)
    {
        _testContext = testContext;
        _client = _factory.CreateClient();
    }

    private CancellationToken Ct => _testContext.CancellationToken;

    [TestMethod]
    public async Task Moving_a_tenant_under_itself_or_a_descendant_is_refused_as_a_cycle()
    {
        var msp = await CreateAsync("MSP");
        var client = await CreateAsync("Client", msp);
        var site = await CreateAsync("Site", client);

        Assert.AreEqual(TenantTreeError.Cycle, await MoveAsync(msp, site));
        Assert.AreEqual(TenantTreeError.Cycle, await MoveAsync(client, client));
        Assert.AreEqual(msp, (await GetAsync(client)).ParentTenantId);
    }

    [TestMethod]
    public async Task Moving_a_subtree_beyond_the_maximum_depth_is_refused()
    {
        // Right's leaf is 11 below its root. Moved under Left's depth-9 leaf, that leaf would sit at depth 21;
        // under Left's depth-8 tenant, at exactly the maximum of 20.
        var left = await ChainAsync("Left", deepest: 9);
        var right = await ChainAsync("Right", deepest: 11);

        Assert.AreEqual(TenantTreeError.TooDeep, await MoveAsync(right[0], left[^1]));
        Assert.AreEqual(TenantTreeError.None, await MoveAsync(right[0], left[^2]));
    }

    [TestMethod]
    public async Task A_maximum_depth_of_zero_refuses_moving_a_root_under_another()
    {
        using var host = _factory.WithWebHostBuilder(builder => builder.UseSetting("TenantTree:MaxDepth", "0"));
        var left = await CreateAsync("Left");
        var right = await CreateAsync("Right");

        using var scope = host.Services.CreateScope();
        var tree = scope.ServiceProvider.GetRequiredService<TenantTree>();

        Assert.AreEqual(TenantTreeError.TooDeep, await tree.MoveAsync(right, left, Ct));
        Assert.IsNull((await GetAsync(right)).ParentTenantId);
    }

    [TestMethod]
    [DataRow(-1, false)]
    [DataRow(0, true)]
    [DataRow(1000, true)]
    [DataRow(1001, false)]
    public void Maximum_depth_must_be_between_0_and_1000(int maxDepth, bool valid)
    {
        var options = new TenantTreeOptions { MaxDepth = maxDepth };

        var isValid = Validator.TryValidateObject(options, new ValidationContext(options), null, validateAllProperties: true);

        Assert.AreEqual(valid, isValid);
    }

    [TestMethod]
    public async Task Tree_changes_wait_for_the_tree_lock()
    {
        var root = await CreateAsync("Root");
        await using var connection = new NpgsqlConnection(_factory.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var transaction = await connection.BeginTransactionAsync(Ct);
        await using (var takeLock = new NpgsqlCommand($"SELECT pg_advisory_xact_lock({TenantTree.LockKey})", connection, transaction))
            await takeLock.ExecuteNonQueryAsync(Ct);

        var create = PostAsync("Child", root);
        await Task.Delay(TimeSpan.FromMilliseconds(500), Ct);
        Assert.IsFalse(create.IsCompleted, "The change ran while another transaction held the tree lock.");

        await transaction.CommitAsync(Ct);
        Assert.AreEqual(HttpStatusCode.Created, (await create).StatusCode);
    }

    [TestMethod]
    public async Task Closure_table_matches_one_rebuilt_from_parent_pointers()
    {
        var consultancy = await CreateAsync("Consultancy");
        var mspA = await CreateAsync("MSP A", consultancy);
        var mspB = await CreateAsync("MSP B", consultancy);
        var client1 = await CreateAsync("Client 1", mspA);
        var client2 = await CreateAsync("Client 2", mspA);
        await CreateAsync("Site 1a", client1);
        await CreateAsync("Site 1b", client1);
        var site2 = await CreateAsync("Site 2", client2);
        await CreateAsync("Standalone");
        Assert.AreEqual(HttpStatusCode.NoContent, (await _client.DeleteAsync($"api/v1/tenants/{site2}", Ct)).StatusCode);
        Assert.AreEqual(TenantTreeError.None, await MoveAsync(client1, mspB));

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Signal9DbContext>();
        var parents = await db.Tenants.ToDictionaryAsync(t => t.Id, t => t.ParentTenantId, Ct);
        var maintained = (await db.TenantClosures.ToListAsync(Ct))
            .Select(c => (c.AncestorId, c.DescendantId, c.Depth)).Order().ToList();

        var rebuilt = new List<(Guid, Guid, int)>();
        foreach (var tenant in parents.Keys)
        {
            Guid? ancestor = tenant;
            for (var depth = 0; ancestor is { } id; depth++, ancestor = parents[id])
                rebuilt.Add((id, tenant, depth));
        }

        Assert.AreEqual(8, parents.Count);
        CollectionAssert.AreEqual(rebuilt.Order().ToList(), maintained);
    }

    private async Task<HttpResponseMessage> PostAsync(string name, Guid? parentTenantId = null) =>
        await _client.PostAsJsonAsync("api/v1/tenants",
            new CreateTenantRequest { Name = name, ContactEmail = "it@tenant.example", ParentTenantId = parentTenantId },
            Signal9Json.Options, Ct);

    private async Task<Guid> CreateAsync(string name, Guid? parentTenantId = null)
    {
        var response = await PostAsync(name, parentTenantId);
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, name);
        return (await response.Content.ReadFromJsonAsync<TenantDto>(Signal9Json.Options, Ct))!.Id;
    }

    // A chain of tenants from a root at depth 0 down to depth `deepest`.
    private async Task<List<Guid>> ChainAsync(string name, int deepest)
    {
        List<Guid> chain = [];
        for (var depth = 0; depth <= deepest; depth++)
            chain.Add(await CreateAsync($"{name} {depth}", chain.Count == 0 ? null : chain[^1]));
        return chain;
    }

    private Task<TenantDto> GetAsync(Guid tenantId) =>
        _client.GetFromJsonAsync<TenantDto>($"api/v1/tenants/{tenantId}", Signal9Json.Options, Ct)!;

    // Re-parenting has no endpoint until tenant transfers; until then, the tree service is driven directly.
    private async Task<TenantTreeError> MoveAsync(Guid tenantId, Guid newParentId)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<TenantTree>().MoveAsync(tenantId, newParentId, Ct);
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }
}
