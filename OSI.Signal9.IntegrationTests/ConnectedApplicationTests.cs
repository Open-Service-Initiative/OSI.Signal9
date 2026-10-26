using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using OSI.Signal9.Contracts;
using OSI.Signal9.Contracts.ApiApplications;
using OSI.Signal9.Contracts.Audit;
using OSI.Signal9.Contracts.Serialization;

namespace OSI.Signal9.IntegrationTests;

/// <summary>
/// Connected applications (#126, stories 88-89): a person's consents to delegated API applications, which the person
/// and the applications' Administrators can list and revoke.
/// </summary>
public sealed partial class DelegatedApiApplicationTests
{
    private const string MyConnections = "api/v1/me/connections";

    [TestMethod]
    public async Task A_person_lists_and_revokes_their_connected_applications_and_revocation_takes_effect_at_once()
    {
        var tree = await TreeAsync();
        var person = await PersonAsync(tree.Msp, ("Viewer", tree.Msp));
        var (application, secret) = await RegisterDelegatedAsync(tree.Msp, "openid", "offline_access", "tenants.read", "devices.read");
        var tokens = await TokensAsync(application, secret, person, "openid offline_access tenants.read");
        // Consenting to more scopes extends the same connection.
        await TokensAsync(application, secret, person, "openid devices.read");
        var other = await PersonAsync(tree.Msp, ("Viewer", tree.Msp));
        await TokensAsync(application, secret, other, "openid tenants.read");
        using var delegated = _factory.CreateAnonymousClient();
        delegated.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.GetProperty("access_token").GetString());
        using var dashboard = await _factory.CreatePersonClientAsync(person);
        using var otherDashboard = await _factory.CreatePersonClientAsync(other);

        var listed = await ConnectionsAsync(dashboard, MyConnections);
        var connection = listed.Single();
        var fetched = await dashboard.GetAsync(connection.Url, Ct);
        var othersView = await otherDashboard.GetAsync(connection.Url, Ct);
        var othersRevoke = await otherDashboard.DeleteAsync(connection.Url, Ct);
        var beforeRevoke = await delegated.GetAsync("api/v1/tenants", Ct);
        var revoked = await dashboard.DeleteAsync(connection.Url, Ct);

        Assert.AreEqual(application.Id, connection.ApiApplicationId);
        Assert.AreEqual(tree.Msp, connection.TenantId);
        Assert.AreEqual("Delegated reporting", connection.ApiApplicationName);
        Assert.AreEqual($"{person.TenantId}:{person.ObjectId}", connection.Subject);
        CollectionAssert.IsSubsetOf(new[] { "tenants.read", "devices.read" }, connection.Scopes.ToArray());
        Assert.AreEqual($"/api/v1/tenants/{tree.Msp}/api-applications/{application.Id}/connections/{connection.Id}", connection.Url);
        Assert.AreEqual(HttpStatusCode.OK, fetched.StatusCode);
        Assert.IsNotNull(fetched.Headers.ETag);
        Assert.AreEqual(HttpStatusCode.NotFound, othersView.StatusCode, "Another person's connection is invisible.");
        Assert.AreEqual(HttpStatusCode.NotFound, othersRevoke.StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, beforeRevoke.StatusCode);
        Assert.AreEqual(HttpStatusCode.NoContent, revoked.StatusCode);

        // The next request with the application's tokens fails, and so does a refresh.
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await delegated.GetAsync("api/v1/tenants", Ct)).StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest,
            (await RefreshAsync(application, secret, tokens.GetProperty("refresh_token").GetString()!)).StatusCode);
        Assert.IsEmpty(await ConnectionsAsync(dashboard, MyConnections));
        Assert.AreEqual(HttpStatusCode.NotFound, (await dashboard.GetAsync(connection.Url, Ct)).StatusCode);
        Assert.HasCount(1, await ConnectionsAsync(otherDashboard, MyConnections), "Only the person's own connection ends.");
        Assert.IsTrue((await AuthorizeAsync(application, person, "openid tenants.read")).AskedForConsent, "Consent is asked again.");
    }

    [TestMethod]
    public async Task An_administrator_lists_and_revokes_connections_across_their_subtree_and_revocations_are_audited()
    {
        var tree = await TreeAsync();
        var person = await PersonAsync(tree.Msp, ("Viewer", tree.Root));
        var (atMsp, mspSecret) = await RegisterDelegatedAsync(tree.Msp, "openid", "tenants.read");
        var (atClient, clientSecret) = await RegisterDelegatedAsync(tree.Client, "openid", "tenants.read");
        var (atSibling, siblingSecret) = await RegisterDelegatedAsync(tree.Sibling, "openid", "tenants.read");
        await TokensAsync(atMsp, mspSecret, person, "openid tenants.read");
        var clientTokens = await TokensAsync(atClient, clientSecret, person, "openid tenants.read");
        await TokensAsync(atSibling, siblingSecret, person, "openid tenants.read");
        using var delegated = _factory.CreateAnonymousClient();
        delegated.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", clientTokens.GetProperty("access_token").GetString());
        using var administrator = await _factory.CreatePersonClientAsync(await PersonAsync(tree.Msp, ("Administrator", tree.Msp)));
        using var clientAdministrator = await _factory.CreatePersonClientAsync(await PersonAsync(tree.Client, ("Administrator", tree.Client)));
        using var technician = await _factory.CreatePersonClientAsync(await PersonAsync(tree.Msp, ("Technician", tree.Msp)));
        using var dashboard = await _factory.CreatePersonClientAsync(person);
        var mine = await ConnectionsAsync(dashboard, MyConnections);
        string Url(ApiApplicationDto application) => mine.Single(c => c.ApiApplicationId == application.Id).Url;

        var subtree = await ConnectionsAsync(administrator, $"api/v1/tenants/{tree.Msp}/connections");
        var mspOnly = await ConnectionsAsync(administrator, $"api/v1/tenants/{tree.Msp}/connections?includeDescendants=false");
        var ofApplication = await ConnectionsAsync(administrator, $"{ApplicationsUrl(tree.Msp)}/{atMsp.Id}/connections");
        var clientView = await ConnectionsAsync(clientAdministrator, $"api/v1/tenants/{tree.Client}/connections");
        var outside = await administrator.GetAsync($"api/v1/tenants/{tree.Sibling}/connections", Ct);
        var aboveClientAdministrator = await clientAdministrator.GetAsync(Url(atMsp), Ct);
        var withoutApplicationsScope = await technician.GetAsync($"api/v1/tenants/{tree.Msp}/connections", Ct);
        var revokeOutside = await administrator.DeleteAsync(Url(atSibling), Ct);
        var staleETag = new HttpRequestMessage(HttpMethod.Delete, Url(atClient));
        staleETag.Headers.IfMatch.Add(new EntityTagHeaderValue("\"stale\""));
        var preconditionFailed = await administrator.SendAsync(staleETag, Ct);
        var revoked = await administrator.DeleteAsync(Url(atClient), Ct);
        var revokedByPerson = await dashboard.DeleteAsync(Url(atMsp), Ct);

        Assert.HasCount(3, mine);
        CollectionAssert.AreEquivalent(new[] { atMsp.Id, atClient.Id }, subtree.Select(c => c.ApiApplicationId).ToArray());
        Assert.IsTrue(subtree.All(c => c.UserDisplayName == "Alex Wilber" && c.UserId is not null), "The administrator sees who connected.");
        Assert.AreEqual(atMsp.Id, mspOnly.Single().ApiApplicationId);
        Assert.AreEqual(atMsp.Id, ofApplication.Single().ApiApplicationId);
        Assert.AreEqual(atClient.Id, clientView.Single().ApiApplicationId);
        Assert.AreEqual(HttpStatusCode.NotFound, outside.StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, aboveClientAdministrator.StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, withoutApplicationsScope.StatusCode, "A Technician's role has no apiapplications.read.");
        Assert.AreEqual(HttpStatusCode.NotFound, revokeOutside.StatusCode);
        Assert.AreEqual(HttpStatusCode.PreconditionFailed, preconditionFailed.StatusCode);
        Assert.AreEqual(HttpStatusCode.NoContent, revoked.StatusCode);
        Assert.AreEqual(HttpStatusCode.NoContent, revokedByPerson.StatusCode);
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await delegated.GetAsync("api/v1/tenants", Ct)).StatusCode);
        Assert.AreEqual(atSibling.Id, (await ConnectionsAsync(dashboard, MyConnections)).Single().ApiApplicationId);

        // Through the public audit API: each revocation at the application's tenant, naming the person and who revoked it.
        var subject = $"{person.TenantId}:{person.ObjectId}";
        var trail = (await TrailAsync(administrator)).Where(e => e.Action == AuditActions.ApiApplicationConsentRevoked).ToList();
        var byAdministrator = trail.Single(e => e.TargetId == atClient.Id);
        var byPerson = trail.Single(e => e.TargetId == atMsp.Id);
        Assert.HasCount(2, trail);
        Assert.AreEqual(tree.Client, byAdministrator.TenantId);
        Assert.AreEqual("api-application", byAdministrator.TargetType);
        Assert.AreEqual(subtree.First().UserId, byAdministrator.UserId);
        Assert.AreEqual($"subject: {subject}", byAdministrator.Detail);
        Assert.AreNotEqual(subject, byAdministrator.Actor.Subject);
        Assert.AreEqual(AuditActorKind.Person, byAdministrator.Actor.Kind);
        Assert.AreEqual(tree.Msp, byPerson.TenantId);
        Assert.AreEqual(subject, byPerson.Actor.Subject);
    }

    [TestMethod]
    public async Task Own_connections_need_the_scope_and_a_delegated_application_sees_only_those_in_its_subtree()
    {
        var tree = await TreeAsync();
        var person = await PersonAsync(tree.Msp, ("Viewer", tree.Root));
        var (atMsp, mspSecret) = await RegisterDelegatedAsync(tree.Msp, "openid", "apiapplications.read");
        var (atSibling, siblingSecret) = await RegisterDelegatedAsync(tree.Sibling, "openid", "tenants.read");
        await TokensAsync(atSibling, siblingSecret, person, "openid tenants.read");
        using var delegated = await DelegatedClientAsync(atMsp, mspSecret, person, "openid apiapplications.read");
        using var dashboard = await _factory.CreatePersonClientAsync(person);
        var sibling = (await ConnectionsAsync(dashboard, MyConnections)).Single(c => c.ApiApplicationId == atSibling.Id);
        using var withoutScope = await _factory.CreatePersonClientAsync(person, "openid", "tenants.read");
        using var appOnly = await _factory.CreateApplicationClientAsync(tree.Msp, "apiapplications.read");
        using var anonymous = _factory.CreateAnonymousClient();

        var throughApplication = await ConnectionsAsync(delegated, MyConnections);
        var siblingThroughApplication = await delegated.GetAsync(sibling.Url, Ct);
        var unauthenticated = await anonymous.GetAsync(MyConnections, Ct);
        var forbidden = await withoutScope.GetAsync(MyConnections, Ct);
        var applicationItself = await ConnectionsAsync(appOnly, MyConnections);

        Assert.AreEqual(atMsp.Id, throughApplication.Single().ApiApplicationId);
        Assert.AreEqual(HttpStatusCode.NotFound, siblingThroughApplication.StatusCode);
        Assert.AreEqual(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.AreEqual("application/problem+json", forbidden.Content.Headers.ContentType?.MediaType);
        Assert.IsEmpty(applicationItself, "An application acting as itself has no connections of its own.");
    }

    private async Task<IReadOnlyList<ConnectionDto>> ConnectionsAsync(HttpClient caller, string url)
    {
        var response = await caller.GetAsync(url, Ct);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<PagedResult<ConnectionDto>>(Signal9Json.Options, Ct))!.Items;
    }
}
