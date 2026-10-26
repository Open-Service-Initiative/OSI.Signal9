using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using OSI.Signal9.Contracts;
using OSI.Signal9.Contracts.ApiApplications;
using OSI.Signal9.Contracts.Roles;
using OSI.Signal9.Contracts.Serialization;
using OSI.Signal9.Contracts.Tenants;
using OSI.Signal9.Contracts.Users;

namespace OSI.Signal9.IntegrationTests;

/// <summary>
/// App-only API applications (#124, stories 78-80, 84, 90): registered at a tenant by an Administrator there, acting as
/// themselves with their own role, and confined to that tenant's subtree.
/// </summary>
[TestClass]
public sealed class ApiApplicationTests : IDisposable
{
    private readonly Signal9ApiFactory _factory = new();
    private readonly HttpClient _superadministrator;
    private readonly TestContext _testContext;

    public ApiApplicationTests(TestContext testContext)
    {
        _testContext = testContext;
        _superadministrator = _factory.CreateClient();
    }

    private CancellationToken Ct => _testContext.CancellationToken;

    [TestMethod]
    public async Task An_administrator_registers_an_application_that_sees_only_its_tenants_subtree()
    {
        var tree = await TreeAsync();
        using var administrator = await PersonAsync(tree.Msp, ("Administrator", tree.Msp));

        var registered = await administrator.PostAsJsonAsync(ApplicationsUrl(tree.Msp),
            Registration("Reporting", await RoleIdAsync("Viewer"), TokenEndpointAuthMethods.ClientSecret, "tenants.read"),
            Signal9Json.Options, Ct);
        Assert.AreEqual(HttpStatusCode.Created, registered.StatusCode, await registered.Content.ReadAsStringAsync(Ct));
        var application = (await registered.Content.ReadFromJsonAsync<ApiApplicationDto>(Signal9Json.Options, Ct))!;
        Assert.AreEqual(new Uri($"https://localhost/{ApplicationsUrl(tree.Msp)}/{application.Id}"), registered.Headers.Location);
        Assert.AreEqual(tree.Msp, application.TenantId);

        var secret = await AddSecretAsync(administrator, application);
        using var asApplication = await SecretClientAsync(application.ClientId, secret, "tenants.read");
        var tenants = await asApplication.GetFromJsonAsync<PagedResult<TenantDto>>("api/v1/tenants?pageSize=100", Signal9Json.Options, Ct);
        var sibling = await asApplication.GetAsync($"api/v1/tenants/{tree.Sibling}", Ct);

        CollectionAssert.AreEquivalent(new[] { tree.Msp, tree.Client }, tenants!.Items.Select(t => t.Id).ToArray());
        Assert.AreEqual(HttpStatusCode.NotFound, sibling.StatusCode);
    }


    [TestMethod]
    public async Task An_application_gets_no_role_higher_than_its_creators_and_no_scope_beyond_its_role()
    {
        var tree = await TreeAsync();
        using var technician = await PersonAsync(tree.Msp, ("Technician", tree.Msp), ("Administrator", tree.Client));
        using var administrator = await PersonAsync(tree.Msp, ("Administrator", tree.Msp));
        // An Administrator whose token leaves out audit.read has less than the Administrator role.
        using var narrowed = await PersonAsync(EntraUser.WithMfa(), tree.Msp,
            [.. Signal9ApiFactory.PersonScopes.Where(s => s != "audit.read")], ("Administrator", tree.Msp));

        var byTechnician = await RegisterAsync(technician, tree.Msp,
            Registration("Reporting", await RoleIdAsync("Viewer"), TokenEndpointAuthMethods.PrivateKeyJwt, "tenants.read"));
        var higherThanOwn = await RegisterAsync(narrowed, tree.Msp,
            Registration("Reporting", await RoleIdAsync("Administrator"), TokenEndpointAuthMethods.PrivateKeyJwt, "tenants.read"));
        var beyondRole = await RegisterAsync(administrator, tree.Msp,
            Registration("Reporting", await RoleIdAsync("Viewer"), TokenEndpointAuthMethods.PrivateKeyJwt, "tenants.write"));
        var agentScope = await RegisterAsync(administrator, tree.Msp,
            Registration("Fake agent", await RoleIdAsync("Administrator"), TokenEndpointAuthMethods.PrivateKeyJwt, "agent"));
        var outsideReach = await RegisterAsync(administrator, tree.Sibling,
            Registration("Reporting", await RoleIdAsync("Viewer"), TokenEndpointAuthMethods.PrivateKeyJwt, "tenants.read"));
        var withinOwn = await RegisterAsync(technician, tree.Client,
            Registration("Reporting", await RoleIdAsync("Technician"), TokenEndpointAuthMethods.PrivateKeyJwt, "devices.read"));

        // A Technician at the MSP lacks apiapplications.write there.
        Assert.AreEqual(HttpStatusCode.NotFound, byTechnician.StatusCode);
        Assert.AreEqual(HttpStatusCode.Created, withinOwn.StatusCode, await withinOwn.Content.ReadAsStringAsync(Ct));
        Assert.AreEqual(HttpStatusCode.Forbidden, higherThanOwn.StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest, beyondRole.StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest, agentScope.StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, outsideReach.StatusCode);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task A_self_signed_application_gets_tokens_bound_to_its_certificate(bool ecdsa)
    {
        var tree = await TreeAsync();
        using var certificate = ecdsa ? EcdsaSelfSigned("reporting") : TestCertificates.SelfSigned("reporting");
        using var other = TestCertificates.SelfSigned("other");
        var application = await RegisterAsSuperadministratorAsync(tree.Msp,
            Registration("Reporting", await RoleIdAsync("Viewer"), TokenEndpointAuthMethods.SelfSignedTlsClientAuth, "tenants.read"));
        await AddCredentialAsync(application, new CreateApiApplicationCredentialRequest { CertificatePem = certificate.ExportCertificatePem() });

        var token = await MtlsTokenAsync(application.ClientId, certificate, "tenants.read");

        Assert.AreEqual(HttpStatusCode.OK, (await GetWithCertificateAsync(token, certificate, $"api/v1/tenants/{tree.Msp}")).StatusCode);
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await GetWithCertificateAsync(token, other, $"api/v1/tenants/{tree.Msp}")).StatusCode);
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await GetWithCertificateAsync(token, null, $"api/v1/tenants/{tree.Msp}")).StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, (await GetWithCertificateAsync(token, certificate, $"api/v1/tenants/{tree.Sibling}")).StatusCode);
    }

    [TestMethod]
    public async Task A_ca_issued_application_gets_tokens_bound_to_its_certificate()
    {
        var tree = await TreeAsync();
        using var certificate = TestCertificates.IssuedByAuthority("CN=reporting.example, O=Example Ltd", "reporting.example",
            "https://reporting.example/app", "192.0.2.10", "ops@reporting.example");
        using var other = TestCertificates.IssuedByAuthority("CN=other.example", "other.example", "https://other.example/",
            "192.0.2.11", "ops@other.example");
        var application = await RegisterAsSuperadministratorAsync(tree.Msp,
            Registration("Reporting", await RoleIdAsync("Viewer"), TokenEndpointAuthMethods.TlsClientAuth, "tenants.read") with
            {
                TlsClientAuthSubject = new TlsClientAuthSubject("tls_client_auth_san_dns", "reporting.example"),
            });

        var token = await MtlsTokenAsync(application.ClientId, certificate, "tenants.read");
        using var asOther = _factory.CreateForwardedCertificateClient(other);
        var otherToken = await asOther.PostAsync("connect/token", Signal9ApiFactory.MtlsClientCredentials(application.ClientId), Ct);

        Assert.AreEqual(HttpStatusCode.OK, (await GetWithCertificateAsync(token, certificate, $"api/v1/tenants/{tree.Msp}")).StatusCode);
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await GetWithCertificateAsync(token, other, $"api/v1/tenants/{tree.Msp}")).StatusCode);
        Assert.AreEqual(HttpStatusCode.Unauthorized, otherToken.StatusCode);
        Assert.IsNull(application.TlsClientAuthSubject is null ? "missing subject" : null);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task A_private_key_jwt_application_authenticates_with_a_signed_assertion(bool fromCertificate)
    {
        var tree = await TreeAsync();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var otherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var application = await RegisterAsSuperadministratorAsync(tree.Msp,
            Registration("Reporting", await RoleIdAsync("Viewer"), TokenEndpointAuthMethods.PrivateKeyJwt, "tenants.read"));
        await AddCredentialAsync(application, fromCertificate
            ? new CreateApiApplicationCredentialRequest { CertificatePem = EcdsaSelfSigned("reporting", key).ExportCertificatePem() }
            : new CreateApiApplicationCredentialRequest { PublicKeyPem = key.ExportSubjectPublicKeyInfoPem() });

        using var asApplication = await AssertionClientAsync(application.ClientId, key, "tenants.read");
        var forged = await AssertionTokenResponseAsync(application.ClientId, otherKey, "tenants.read");

        Assert.AreEqual(HttpStatusCode.OK, (await asApplication.GetAsync($"api/v1/tenants/{tree.Msp}", Ct)).StatusCode);
        Assert.AreEqual(HttpStatusCode.Unauthorized, forged.StatusCode);
    }

    [TestMethod]
    public async Task An_application_authenticates_only_with_the_method_it_registered()
    {
        var tree = await TreeAsync();
        using var certificate = TestCertificates.SelfSigned("reporting");
        using var key = certificate.GetRSAPrivateKey()!;
        var selfSigned = await RegisterAsSuperadministratorAsync(tree.Msp,
            Registration("Reporting", await RoleIdAsync("Viewer"), TokenEndpointAuthMethods.SelfSignedTlsClientAuth, "tenants.read"));
        await AddCredentialAsync(selfSigned, new CreateApiApplicationCredentialRequest { CertificatePem = certificate.ExportCertificatePem() });
        var withSecret = await RegisterAsSuperadministratorAsync(tree.Msp,
            Registration("Legacy", await RoleIdAsync("Viewer"), TokenEndpointAuthMethods.ClientSecret, "tenants.read"));
        var secret = (await AddCredentialAsync(withSecret, new CreateApiApplicationCredentialRequest())).Secret!;
        var certificateForSecretApp = await AddCredentialResponseAsync(withSecret,
            new CreateApiApplicationCredentialRequest { CertificatePem = certificate.ExportCertificatePem() });

        // The self-signed certificate's key signs a valid assertion, but the application registered mTLS.
        var assertion = await AssertionTokenResponseAsync(selfSigned.ClientId, new RsaSecurityKey(key), "tenants.read");
        var withItsSecret = await SecretTokenResponseAsync(withSecret.ClientId, secret, "tenants.read");

        Assert.AreEqual(HttpStatusCode.Unauthorized, assertion.StatusCode, await assertion.Content.ReadAsStringAsync(Ct));
        Assert.AreEqual(HttpStatusCode.BadRequest, certificateForSecretApp.StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, withItsSecret.StatusCode);
    }

    [TestMethod]
    public async Task Granting_a_scope_that_needs_a_recent_mfa_needs_the_administrators_recent_mfa()
    {
        var tree = await TreeAsync();
        var stale = EntraUser.WithMfa() with { AuthenticatedAt = _factory.Time.GetUtcNow().AddMinutes(-20) };
        using var staleAdministrator = await PersonAsync(stale, tree.Msp, [], ("Administrator", tree.Msp));
        using var administrator = await PersonAsync(tree.Msp, ("Administrator", tree.Msp));
        var role = await RoleIdAsync("Administrator");

        var withoutMfa = await RegisterAsync(staleAdministrator, tree.Msp,
            Registration("Cleanup", role, TokenEndpointAuthMethods.PrivateKeyJwt, "tenants.read", "tenants.delete"));
        var ungatedWithoutMfa = await RegisterAsync(staleAdministrator, tree.Msp,
            Registration("Reporting", role, TokenEndpointAuthMethods.PrivateKeyJwt, "tenants.read"));
        var withMfa = await RegisterAsync(administrator, tree.Msp,
            Registration("Cleanup", role, TokenEndpointAuthMethods.PrivateKeyJwt, "tenants.read", "tenants.delete"));

        Assert.AreEqual(HttpStatusCode.Unauthorized, withoutMfa.StatusCode);
        StringAssert.Contains(withoutMfa.Headers.WwwAuthenticate.ToString(), "error=\"insufficient_user_authentication\"");
        StringAssert.Contains(withoutMfa.Headers.WwwAuthenticate.ToString(), "max_age=900");
        Assert.AreEqual(HttpStatusCode.Created, ungatedWithoutMfa.StatusCode);
        Assert.AreEqual(HttpStatusCode.Created, withMfa.StatusCode, await withMfa.Content.ReadAsStringAsync(Ct));
    }

    [TestMethod]
    public async Task An_application_granted_a_recent_mfa_scope_with_mfa_can_use_it()
    {
        var tree = await TreeAsync();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var application = await RegisterAsSuperadministratorAsync(tree.Msp, Registration("Cleanup",
            await RoleIdAsync("Administrator"), TokenEndpointAuthMethods.PrivateKeyJwt, "tenants.read", "tenants.delete"));
        await AddCredentialAsync(application, new CreateApiApplicationCredentialRequest { PublicKeyPem = key.ExportSubjectPublicKeyInfoPem() });
        using var asApplication = await AssertionClientAsync(application.ClientId, key, "tenants.read", "tenants.delete");

        var deleted = await asApplication.DeleteAsync($"api/v1/tenants/{tree.Client}", Ct);
        var outside = await asApplication.DeleteAsync($"api/v1/tenants/{tree.Sibling}", Ct);

        Assert.AreEqual(HttpStatusCode.NoContent, deleted.StatusCode, await deleted.Content.ReadAsStringAsync(Ct));
        Assert.AreEqual(HttpStatusCode.NotFound, outside.StatusCode);
    }

    [TestMethod]
    public async Task A_recent_mfa_scope_is_never_granted_to_an_application_that_authenticates_with_a_secret()
    {
        var tree = await TreeAsync();
        var role = await RoleIdAsync("Administrator");
        var registered = await RegisterAsync(await MfaSuperadministratorAsync(), tree.Msp,
            Registration("Legacy", role, TokenEndpointAuthMethods.ClientSecret, "tenants.read", "tenants.delete"));
        var application = await RegisterAsSuperadministratorAsync(tree.Msp,
            Registration("Legacy", role, TokenEndpointAuthMethods.ClientSecret, "tenants.read"));
        var patched = await PatchAsync(await MfaSuperadministratorAsync(), application, """{"scopes":["tenants.read","scripts.execute"]}""");

        Assert.AreEqual(HttpStatusCode.BadRequest, registered.StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest, patched.StatusCode);
    }

    [TestMethod]
    public async Task An_application_acting_as_itself_cannot_grant_a_recent_mfa_scope()
    {
        var tree = await TreeAsync();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var role = await RoleIdAsync("Administrator");
        string[] scopes = ["apiapplications.write", "tenants.delete", .. ViewerScopes];
        var manager = await RegisterAsSuperadministratorAsync(tree.Msp, Registration("Provisioning", role,
            TokenEndpointAuthMethods.PrivateKeyJwt, scopes));
        await AddCredentialAsync(manager, new CreateApiApplicationCredentialRequest { PublicKeyPem = key.ExportSubjectPublicKeyInfoPem() });
        using var asManager = await AssertionClientAsync(manager.ClientId, key, scopes);

        var gated = await RegisterAsync(asManager, tree.Client,
            Registration("Cleanup", role, TokenEndpointAuthMethods.PrivateKeyJwt, "tenants.delete"));
        var ungated = await RegisterAsync(asManager, tree.Client,
            Registration("Reporting", await RoleIdAsync("Viewer"), TokenEndpointAuthMethods.PrivateKeyJwt, "tenants.read"));

        Assert.AreEqual(HttpStatusCode.Forbidden, gated.StatusCode);
        Assert.AreEqual(HttpStatusCode.Created, ungated.StatusCode, await ungated.Content.ReadAsStringAsync(Ct));
    }

    [TestMethod]
    public async Task Changes_to_an_applications_access_take_effect_on_its_next_request()
    {
        var tree = await TreeAsync();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var application = await RegisterAsSuperadministratorAsync(tree.Msp, Registration("Cleanup",
            await RoleIdAsync("Administrator"), TokenEndpointAuthMethods.PrivateKeyJwt, "tenants.read", "tenants.delete"));
        await AddCredentialAsync(application, new CreateApiApplicationCredentialRequest { PublicKeyPem = key.ExportSubjectPublicKeyInfoPem() });
        using var asApplication = await AssertionClientAsync(application.ClientId, key, "tenants.read", "tenants.delete");

        var patched = await PatchAsync(await MfaSuperadministratorAsync(), application,
            $$"""{"roleId":"{{await RoleIdAsync("Viewer")}}","scopes":["tenants.read"]}""");
        var deleteAfter = await asApplication.DeleteAsync($"api/v1/tenants/{tree.Client}", Ct);
        var readAfter = await asApplication.GetAsync($"api/v1/tenants/{tree.Client}", Ct);
        var removed = await (await MfaSuperadministratorAsync()).DeleteAsync($"{ApplicationsUrl(tree.Msp)}/{application.Id}", Ct);
        var readAfterRemoval = await asApplication.GetAsync($"api/v1/tenants/{tree.Client}", Ct);

        Assert.AreEqual(HttpStatusCode.OK, patched.StatusCode, await patched.Content.ReadAsStringAsync(Ct));
        Assert.AreEqual(HttpStatusCode.Forbidden, deleteAfter.StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, readAfter.StatusCode);
        Assert.AreEqual(HttpStatusCode.NoContent, removed.StatusCode);
        Assert.AreEqual(HttpStatusCode.Unauthorized, readAfterRemoval.StatusCode);
    }

    [TestMethod]
    public async Task Secrets_are_shown_once_expire_and_rotate()
    {
        var tree = await TreeAsync();
        var application = await RegisterAsSuperadministratorAsync(tree.Msp,
            Registration("Legacy", await RoleIdAsync("Viewer"), TokenEndpointAuthMethods.ClientSecret, "tenants.read"));
        var now = _factory.Time.GetUtcNow();
        var shortLived = await AddCredentialAsync(application, new CreateApiApplicationCredentialRequest { ExpiresAt = now.AddDays(1) });
        var longLived = await AddCredentialAsync(application, new CreateApiApplicationCredentialRequest());
        var tooLong = await AddCredentialResponseAsync(application, new CreateApiApplicationCredentialRequest { ExpiresAt = now.AddDays(400) });
        var past = await AddCredentialResponseAsync(application, new CreateApiApplicationCredentialRequest { ExpiresAt = now.AddMinutes(-1) });
        var credentialsUrl = $"{ApplicationsUrl(tree.Msp)}/{application.Id}/credentials";
        var listed = await (await MfaSuperadministratorAsync()).GetFromJsonAsync<PagedResult<ApiApplicationCredentialDto>>(
            credentialsUrl, Signal9Json.Options, Ct);

        Assert.AreEqual(HttpStatusCode.BadRequest, tooLong.StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest, past.StatusCode);
        Assert.AreEqual(now.AddDays(180), longLived.ExpiresAt);
        Assert.AreEqual(2, listed!.TotalCount);
        Assert.IsTrue(listed.Items.All(c => c.Secret is null && c.Type == ApiApplicationCredentialTypes.Secret));
        Assert.AreEqual(HttpStatusCode.OK, (await SecretTokenResponseAsync(application.ClientId, shortLived.Secret!, "tenants.read")).StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, (await SecretTokenResponseAsync(application.ClientId, longLived.Secret!, "tenants.read")).StatusCode);

        _factory.Time.Advance(TimeSpan.FromDays(2));
        // The Superadministrator signs in again: their access token has expired too.
        _mfaSuperadministrator?.Dispose();
        _mfaSuperadministrator = null;
        var expired = await SecretTokenResponseAsync(application.ClientId, shortLived.Secret!, "tenants.read");
        var removed = await (await MfaSuperadministratorAsync()).DeleteAsync($"{credentialsUrl}/{longLived.Id}", Ct);
        var afterRemoval = await SecretTokenResponseAsync(application.ClientId, longLived.Secret!, "tenants.read");

        Assert.AreEqual(HttpStatusCode.Unauthorized, expired.StatusCode);
        Assert.AreEqual(HttpStatusCode.NoContent, removed.StatusCode);
        Assert.AreEqual(HttpStatusCode.Unauthorized, afterRemoval.StatusCode);
    }

    [TestMethod]
    public async Task Removing_a_credential_revokes_the_tokens_already_issued_to_the_application()
    {
        var tree = await TreeAsync();
        var application = await RegisterAsSuperadministratorAsync(tree.Msp,
            Registration("Leaked", await RoleIdAsync("Viewer"), TokenEndpointAuthMethods.ClientSecret, "tenants.read"));
        var leaked = await AddCredentialAsync(application, new CreateApiApplicationCredentialRequest());
        var issued = await SecretTokenResponseAsync(application.ClientId, leaked.Secret!, "tenants.read");
        Assert.AreEqual(HttpStatusCode.OK, issued.StatusCode, await issued.Content.ReadAsStringAsync(Ct));
        using var api = _factory.CreateAnonymousClient();
        api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            (await issued.Content.ReadFromJsonAsync<AccessToken>(Ct))!.Value);
        Assert.AreEqual(HttpStatusCode.OK, (await api.GetAsync($"api/v1/tenants/{tree.Msp}", Ct)).StatusCode);

        var removed = await (await MfaSuperadministratorAsync()).DeleteAsync(
            $"{ApplicationsUrl(tree.Msp)}/{application.Id}/credentials/{leaked.Id}", Ct);
        var afterRemoval = await api.GetAsync($"api/v1/tenants/{tree.Msp}", Ct);

        Assert.AreEqual(HttpStatusCode.NoContent, removed.StatusCode);
        Assert.AreEqual(HttpStatusCode.Unauthorized, afterRemoval.StatusCode, "A rotation after a leak takes effect at once.");
    }

    [TestMethod]
    public async Task A_vendors_product_is_registered_separately_at_each_tenant()
    {
        var tree = await TreeAsync();
        using var certificate = TestCertificates.SelfSigned("vendor-product");
        var role = await RoleIdAsync("Viewer");
        var atMsp = await RegisterAsSuperadministratorAsync(tree.Msp,
            Registration("Vendor", role, TokenEndpointAuthMethods.SelfSignedTlsClientAuth, "tenants.read"));
        var atSibling = await RegisterAsSuperadministratorAsync(tree.Sibling,
            Registration("Vendor", role, TokenEndpointAuthMethods.SelfSignedTlsClientAuth, "tenants.read"));
        foreach (var application in new[] { atMsp, atSibling })
            await AddCredentialAsync(application, new CreateApiApplicationCredentialRequest { CertificatePem = certificate.ExportCertificatePem() });

        var mspToken = await MtlsTokenAsync(atMsp.ClientId, certificate, "tenants.read");
        var siblingToken = await MtlsTokenAsync(atSibling.ClientId, certificate, "tenants.read");

        Assert.AreNotEqual(atMsp.ClientId, atSibling.ClientId);
        Assert.AreEqual(HttpStatusCode.OK, (await GetWithCertificateAsync(mspToken, certificate, $"api/v1/tenants/{tree.Msp}")).StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, (await GetWithCertificateAsync(mspToken, certificate, $"api/v1/tenants/{tree.Sibling}")).StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, (await GetWithCertificateAsync(siblingToken, certificate, $"api/v1/tenants/{tree.Sibling}")).StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, (await GetWithCertificateAsync(siblingToken, certificate, $"api/v1/tenants/{tree.Msp}")).StatusCode);
    }

    [TestMethod]
    public async Task Registration_credential_changes_and_role_grants_are_audited_at_the_tenant()
    {
        var tree = await TreeAsync();
        using var administrator = await PersonAsync(tree.Msp, ("Administrator", tree.Msp));
        using var siblingAdministrator = await PersonAsync(tree.Sibling, ("Administrator", tree.Sibling));
        var registered = await RegisterAsync(administrator, tree.Client,
            Registration("Reporting", await RoleIdAsync("Viewer"), TokenEndpointAuthMethods.ClientSecret, "tenants.read"));
        var application = (await registered.Content.ReadFromJsonAsync<ApiApplicationDto>(Signal9Json.Options, Ct))!;
        var secret = await AddCredentialAsync(application, new CreateApiApplicationCredentialRequest(), administrator);
        var regranted = await PatchAsync(administrator, application,
            $$"""{"roleId":"{{await RoleIdAsync("Technician")}}","scopes":["tenants.read","devices.read"]}""");
        var removed = await administrator.DeleteAsync($"{ApplicationsUrl(tree.Client)}/{application.Id}/credentials/{secret.Id}", Ct);
        var deleted = await administrator.DeleteAsync($"{ApplicationsUrl(tree.Client)}/{application.Id}", Ct);
        Assert.AreEqual(HttpStatusCode.OK, regranted.StatusCode, await regranted.Content.ReadAsStringAsync(Ct));
        Assert.AreEqual(HttpStatusCode.NoContent, removed.StatusCode);
        Assert.AreEqual(HttpStatusCode.NoContent, deleted.StatusCode);

        var entries = (await administrator.GetFromJsonAsync<PagedResult<OSI.Signal9.Contracts.Audit.AuditEntryDto>>(
            "api/v1/audit-entries?pageSize=100", Signal9Json.Options, Ct))!.Items
            .Where(e => e.TargetId == application.Id).Reverse().ToList();
        var outsider = (await siblingAdministrator.GetFromJsonAsync<PagedResult<OSI.Signal9.Contracts.Audit.AuditEntryDto>>(
            "api/v1/audit-entries?pageSize=100", Signal9Json.Options, Ct))!.Items;

        // Entries saved together have no order between them; each request's come after the last's.
        CollectionAssert.AreEquivalent(new[]
        {
            "api-application.registered", "api-application.access-granted", "api-application.credential-added",
            "api-application.access-granted", "api-application.credential-removed", "api-application.deleted",
        }, entries.Select(e => e.Action).ToArray());
        Assert.IsTrue(entries.All(e => e.TenantId == tree.Client && e.Actor.Kind == OSI.Signal9.Contracts.Audit.AuditActorKind.Person));
        var grants = entries.Where(e => e.Action == "api-application.access-granted").ToList();
        Assert.AreEqual("Viewer", grants[0].RoleName);
        Assert.AreEqual("Technician", grants[1].RoleName);
        Assert.AreEqual("scopes: tenants.read devices.read", grants[1].Detail);
        Assert.AreEqual($"secret {secret.Id}", entries.Single(e => e.Action == "api-application.credential-added").Detail);
        Assert.IsFalse(outsider.Any(e => e.TargetId == application.Id));
    }

    /// <summary>
    /// Scope parity (story 72): every scope a role can grant works for an application acting as itself under the same
    /// scope, role and subtree checks a person faces, through the probe that decides any endpoint's tenant access. Only
    /// scopes no role grants at a tenant (agent, deployment, OpenID Connect) can't be granted.
    /// </summary>
    [TestMethod]
    public async Task Every_scope_a_role_grants_works_for_an_application_under_the_same_checks()
    {
        var tree = await TreeAsync();
        var administrator = await RoleIdAsync("Administrator");
        using var api = _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddControllers().AddApplicationPart(typeof(RoleProbeController).Assembly)));
        using var probe = api.CreateClient();

        foreach (var scope in OSI.Signal9.Server.Identity.Scopes.All.Keys)
        {
            var registration = Registration($"Parity {scope}", administrator, TokenEndpointAuthMethods.PrivateKeyJwt,
                scope == "tenants.read" ? ["tenants.read"] : [scope, "tenants.read"]);
            var registered = await RegisterAsync(await MfaSuperadministratorAsync(), tree.Msp, registration);
            if (!AdministratorScopes.Contains(scope))
            {
                Assert.AreEqual(HttpStatusCode.BadRequest, registered.StatusCode, scope);
                continue;
            }
            Assert.AreEqual(HttpStatusCode.Created, registered.StatusCode, $"{scope}: {await registered.Content.ReadAsStringAsync(Ct)}");
            var application = (await registered.Content.ReadFromJsonAsync<ApiApplicationDto>(Signal9Json.Options, Ct))!;
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            await AddCredentialAsync(application, new CreateApiApplicationCredentialRequest { PublicKeyPem = key.ExportSubjectPublicKeyInfoPem() });
            using var withScope = await AssertionClientAsync(application.ClientId, key, scope);
            using var withoutScope = await AssertionClientAsync(application.ClientId, key, "tenants.read");

            async Task<HttpStatusCode> ProbeAsync(HttpClient caller, Guid tenantId)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, $"test/role-probe/{scope}/{tenantId}");
                request.Headers.Authorization = caller.DefaultRequestHeaders.Authorization;
                return (await probe.SendAsync(request, Ct)).StatusCode;
            }

            Assert.AreEqual(HttpStatusCode.OK, await ProbeAsync(withScope, tree.Msp), scope);
            Assert.AreEqual(HttpStatusCode.OK, await ProbeAsync(withScope, tree.Client), scope);
            Assert.AreEqual(HttpStatusCode.NotFound, await ProbeAsync(withScope, tree.Root), scope);
            Assert.AreEqual(HttpStatusCode.NotFound, await ProbeAsync(withScope, tree.Sibling), scope);
            if (scope != "tenants.read")
                Assert.AreEqual(HttpStatusCode.NotFound, await ProbeAsync(withoutScope, tree.Msp), scope);
        }
    }

    [TestMethod]
    public async Task Only_a_caller_whose_own_access_covers_an_applications_role_can_add_its_credentials_or_edit_it()
    {
        var tree = await TreeAsync();
        var application = await RegisterAsSuperadministratorAsync(tree.Msp, Registration("Automation",
            await RoleIdAsync("Administrator"), TokenEndpointAuthMethods.ClientSecret, "tenants.read", "audit.read"));
        // An Administrator whose token leaves out audit.read must not be able to become the application.
        using var narrowed = await PersonAsync(EntraUser.WithMfa(), tree.Msp,
            [.. Signal9ApiFactory.PersonScopes.Where(s => s != "audit.read")], ("Administrator", tree.Msp));
        using var administrator = await PersonAsync(tree.Msp, ("Administrator", tree.Msp));

        var addedByNarrowed = await AddCredentialResponseAsync(application, new CreateApiApplicationCredentialRequest(), narrowed);
        var renamedByNarrowed = await PatchAsync(narrowed, application, """{"displayName":"Mine now"}""");
        var addedByAdministrator = await AddCredentialResponseAsync(application, new CreateApiApplicationCredentialRequest(), administrator);

        Assert.AreEqual(HttpStatusCode.Forbidden, addedByNarrowed.StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, renamedByNarrowed.StatusCode);
        Assert.AreEqual(HttpStatusCode.Created, addedByAdministrator.StatusCode, await addedByAdministrator.Content.ReadAsStringAsync(Ct));
    }

    [TestMethod]
    public async Task A_peer_administrator_application_is_removed_or_downgraded_only_from_above_its_tenant()
    {
        var tree = await TreeAsync();
        var administratorRole = await RoleIdAsync("Administrator");
        var peerApplication = await RegisterAsSuperadministratorAsync(tree.Msp, Registration("Automation",
            administratorRole, TokenEndpointAuthMethods.ClientSecret, "tenants.read", "audit.read"));
        var viewerApplication = await RegisterAsSuperadministratorAsync(tree.Msp, Registration("Reporting",
            await RoleIdAsync("Viewer"), TokenEndpointAuthMethods.ClientSecret, "tenants.read"));
        using var peer = await PersonAsync(tree.Msp, ("Administrator", tree.Msp));
        using var above = await PersonAsync(tree.Root, ("Administrator", tree.Root));

        var deletedByPeer = await peer.DeleteAsync($"{ApplicationsUrl(tree.Msp)}/{peerApplication.Id}", Ct);
        var demotedByPeer = await PatchAsync(peer, peerApplication,
            $$"""{"roleId":"{{await RoleIdAsync("Viewer")}}","scopes":["tenants.read"]}""");
        var narrowedByPeer = await PatchAsync(peer, peerApplication, """{"scopes":["tenants.read"]}""");
        var renamedByPeer = await PatchAsync(peer, peerApplication, """{"displayName":"Automation (prod)"}""");
        var widenedByPeer = await PatchAsync(peer, peerApplication, """{"scopes":["tenants.read","audit.read","devices.read"]}""");
        var viewerDeletedByPeer = await peer.DeleteAsync($"{ApplicationsUrl(tree.Msp)}/{viewerApplication.Id}", Ct);

        Assert.AreEqual(HttpStatusCode.Forbidden, deletedByPeer.StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, demotedByPeer.StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, narrowedByPeer.StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, renamedByPeer.StatusCode, await renamedByPeer.Content.ReadAsStringAsync(Ct));
        Assert.AreEqual(HttpStatusCode.OK, widenedByPeer.StatusCode, await widenedByPeer.Content.ReadAsStringAsync(Ct));
        Assert.AreEqual(HttpStatusCode.NoContent, viewerDeletedByPeer.StatusCode);

        var narrowedFromAbove = await PatchAsync(above, peerApplication, """{"scopes":["tenants.read"]}""");
        var deletedFromAbove = await above.DeleteAsync($"{ApplicationsUrl(tree.Msp)}/{peerApplication.Id}", Ct);
        Assert.AreEqual(HttpStatusCode.OK, narrowedFromAbove.StatusCode, await narrowedFromAbove.Content.ReadAsStringAsync(Ct));
        Assert.AreEqual(HttpStatusCode.NoContent, deletedFromAbove.StatusCode);
    }

    [TestMethod]
    public async Task An_administrator_application_can_remove_itself_but_not_a_peer_administrator_application()
    {
        var tree = await TreeAsync();
        var administratorRole = await RoleIdAsync("Administrator");
        var peerApplication = await RegisterAsSuperadministratorAsync(tree.Msp, Registration("Automation",
            administratorRole, TokenEndpointAuthMethods.ClientSecret, "tenants.read"));
        var application = await RegisterAsSuperadministratorAsync(tree.Msp, Registration("Provisioning",
            administratorRole, TokenEndpointAuthMethods.ClientSecret, "apiapplications.read", "apiapplications.write"));
        using var asApplication = await SecretClientAsync(application.ClientId,
            await AddSecretAsync(await MfaSuperadministratorAsync(), application), "apiapplications.read", "apiapplications.write");

        var peerDeleted = await asApplication.DeleteAsync($"{ApplicationsUrl(tree.Msp)}/{peerApplication.Id}", Ct);
        var selfDeleted = await asApplication.DeleteAsync($"{ApplicationsUrl(tree.Msp)}/{application.Id}", Ct);

        Assert.AreEqual(HttpStatusCode.Forbidden, peerDeleted.StatusCode);
        Assert.AreEqual(HttpStatusCode.NoContent, selfDeleted.StatusCode, await selfDeleted.Content.ReadAsStringAsync(Ct));
    }

    [TestMethod]
    public async Task An_administrator_application_does_not_count_as_a_tenants_remaining_administrator()
    {
        var tree = await TreeAsync();
        await RegisterAsSuperadministratorAsync(tree.Root, Registration("Automation", await RoleIdAsync("Administrator"),
            TokenEndpointAuthMethods.ClientSecret, "tenants.read"));
        var person = EntraUser.WithMfa();
        using var administrator = await PersonAsync(person, tree.Root, [], ("Administrator", tree.Root));
        var assignments = await administrator.GetFromJsonAsync<PagedResult<RoleAssignmentDto>>(
            $"api/v1/tenants/{tree.Root}/role-assignments", Signal9Json.Options, Ct);

        var leaving = await administrator.DeleteAsync(
            $"api/v1/tenants/{tree.Root}/role-assignments/{assignments!.Items.Single().Id}", Ct);

        Assert.AreEqual(HttpStatusCode.Conflict, leaving.StatusCode);
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private static readonly string[] ViewerScopes =
        ["tenants.read", "devices.read", "commands.read", "scripts.read", "telemetry.read", "users.read"];

    // The Administrator bundle as #63 defines it, written out independently of the seeded roles.
    private static readonly string[] AdministratorScopes =
    [
        .. ViewerScopes, "devices.write", "devices.transfer", "commands.execute", "scripts.write", "scripts.delete",
        "scripts.execute", "terminal.execute", "users.write", "enrollment.read", "enrollment.write", "tenants.write",
        "tenants.delete", "tenants.transfer", "devices.delete", "roleassignments.read", "roleassignments.write", "roles.read",
        "roles.write", "audit.read", "apiapplications.read", "apiapplications.write", "integrations.read",
        "integrations.write", "users.transfer",
    ];

    private static string ApplicationsUrl(Guid tenantId) => $"api/v1/tenants/{tenantId}/api-applications";

    private static CreateApiApplicationRequest Registration(string name, Guid roleId, string method, params string[] scopes) =>
        new() { DisplayName = name, RoleId = roleId, TokenEndpointAuthMethod = method, Scopes = scopes };


    private Task<HttpResponseMessage> RegisterAsync(HttpClient caller, Guid tenantId, CreateApiApplicationRequest request) =>
        caller.PostAsJsonAsync(ApplicationsUrl(tenantId), request, Signal9Json.Options, Ct);

    private async Task<HttpResponseMessage> PatchAsync(HttpClient caller, ApiApplicationDto application, string json)
    {
        using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/merge-patch+json");
        return await caller.PatchAsync($"{ApplicationsUrl(application.TenantId)}/{application.Id}", content, Ct);
    }

    private HttpClient? _mfaSuperadministrator;

    /// <summary>A Superadministrator person who just signed in with MFA, so may grant any scope.</summary>
    private async Task<HttpClient> MfaSuperadministratorAsync() =>
        _mfaSuperadministrator ??= await _factory.CreatePersonClientAsync(_factory.Superadministrator());

    private async Task<ApiApplicationDto> RegisterAsSuperadministratorAsync(Guid tenantId, CreateApiApplicationRequest request)
    {
        var response = await RegisterAsync(await MfaSuperadministratorAsync(), tenantId, request);
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<ApiApplicationDto>(Signal9Json.Options, Ct))!;
    }

    private async Task<HttpResponseMessage> AddCredentialResponseAsync(ApiApplicationDto application,
        CreateApiApplicationCredentialRequest request, HttpClient? caller = null) =>
        await (caller ?? await MfaSuperadministratorAsync()).PostAsJsonAsync(
            $"{ApplicationsUrl(application.TenantId)}/{application.Id}/credentials", request, Signal9Json.Options, Ct);

    private async Task<ApiApplicationCredentialDto> AddCredentialAsync(ApiApplicationDto application,
        CreateApiApplicationCredentialRequest request, HttpClient? caller = null)
    {
        var response = await AddCredentialResponseAsync(application, request, caller);
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<ApiApplicationCredentialDto>(Signal9Json.Options, Ct))!;
    }

    /// <summary>An access token by client credentials at api's mTLS alias, over the forwarded certificate.</summary>
    private async Task<string> MtlsTokenAsync(string clientId, X509Certificate2 certificate, params string[] scopes)
    {
        using var api = _factory.CreateForwardedCertificateClient(certificate);
        var response = await api.PostAsync("connect/token", Signal9ApiFactory.MtlsClientCredentials(clientId, scopes), Ct);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<AccessToken>(Ct))!.Value;
    }

    private async Task<HttpResponseMessage> GetWithCertificateAsync(string token, X509Certificate2? certificate, string url)
    {
        using var api = _factory.CreateForwardedCertificateClient(certificate);
        api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await api.GetAsync(url, Ct);
    }

    private static X509Certificate2 EcdsaSelfSigned(string commonName, ECDsa? key = null)
    {
        using var owned = key is null ? ECDsa.Create(ECCurve.NamedCurves.nistP256) : null;
        var request = new CertificateRequest($"CN={commonName}", key ?? owned!, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.2")], critical: false));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(30));
    }

    /// <summary>A private_key_jwt client assertion (RFC 7523 §3) for auth's token endpoint.</summary>
    private string Assertion(string clientId, SecurityKey key)
    {
        var now = _factory.Time.GetUtcNow().UtcDateTime;
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            // draft-ietf-oauth-rfc7523bis, which OpenIddict follows: an explicit type, and the issuer as the audience.
            TokenType = "client-authentication+jwt",
            Issuer = clientId,
            Audience = Signal9AuthFactory.Issuer,
            Claims = new Dictionary<string, object> { ["sub"] = clientId, ["jti"] = Guid.NewGuid().ToString("N") },
            IssuedAt = now,
            NotBefore = now,
            Expires = now.AddMinutes(2),
            SigningCredentials = new SigningCredentials(key,
                key is ECDsaSecurityKey ? SecurityAlgorithms.EcdsaSha256 : SecurityAlgorithms.RsaSha256),
        });
    }

    private Task<HttpResponseMessage> AssertionTokenResponseAsync(string clientId, ECDsa key, params string[] scopes) =>
        AssertionTokenResponseAsync(clientId, new ECDsaSecurityKey(key), scopes);

    private async Task<HttpResponseMessage> AssertionTokenResponseAsync(string clientId, SecurityKey key, params string[] scopes)
    {
        using var auth = _factory.Auth.CreateClient();
        return await auth.PostAsync("connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = clientId,
            ["client_assertion_type"] = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer",
            ["client_assertion"] = Assertion(clientId, key),
            ["scope"] = string.Join(' ', scopes),
        }), Ct);
    }

    private async Task<HttpClient> AssertionClientAsync(string clientId, ECDsa key, params string[] scopes)
    {
        var response = await AssertionTokenResponseAsync(clientId, key, scopes);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
        var client = _factory.CreateAnonymousClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            (await response.Content.ReadFromJsonAsync<AccessToken>(Ct))!.Value);
        return client;
    }

    private async Task<string> AddSecretAsync(HttpClient caller, ApiApplicationDto application)
    {
        var response = await caller.PostAsJsonAsync($"{ApplicationsUrl(application.TenantId)}/{application.Id}/credentials",
            new CreateApiApplicationCredentialRequest(), Signal9Json.Options, Ct);
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
        Assert.IsTrue(response.Headers.CacheControl?.NoStore, "A response carrying the secret is never stored.");
        return (await response.Content.ReadFromJsonAsync<ApiApplicationCredentialDto>(Signal9Json.Options, Ct))!.Secret!;
    }

    private async Task<HttpResponseMessage> SecretTokenResponseAsync(string clientId, string secret, params string[] scopes)
    {
        using var auth = _factory.Auth.CreateClient();
        return await auth.PostAsync("connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = clientId,
            ["client_secret"] = secret,
            ["scope"] = string.Join(' ', scopes),
        }), Ct);
    }

    private async Task<HttpClient> SecretClientAsync(string clientId, string secret, params string[] scopes)
    {
        var response = await SecretTokenResponseAsync(clientId, secret, scopes);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
        var token = (await response.Content.ReadFromJsonAsync<AccessToken>(Ct))!;
        var client = _factory.CreateAnonymousClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Value);
        return client;
    }

    private sealed record Tree(Guid Root, Guid Msp, Guid Client, Guid Sibling);

    /// <summary>Root ─ MSP ─ Client, and Root ─ Sibling.</summary>
    private async Task<Tree> TreeAsync()
    {
        var root = await _factory.CreateTenantAsync(_superadministrator, "Root");
        var msp = await _factory.CreateTenantAsync(_superadministrator, "Msp", parentTenantId: root.Id);
        var client = await _factory.CreateTenantAsync(_superadministrator, "Client", parentTenantId: msp.Id);
        var sibling = await _factory.CreateTenantAsync(_superadministrator, "Sibling", parentTenantId: root.Id);
        return new Tree(root.Id, msp.Id, client.Id, sibling.Id);
    }

    /// <summary>A newly signed-in person with MFA, owned by <paramref name="owner"/>, holding these role assignments.</summary>
    private async Task<HttpClient> PersonAsync(Guid owner, params (string Role, Guid Tenant)[] assignments) =>
        await PersonAsync(EntraUser.WithMfa(), owner, [], assignments);

    /// <summary>A person signing in with these scopes (every person scope if none), holding these role assignments.</summary>
    private async Task<HttpClient> PersonAsync(EntraUser person, Guid owner, string[] scopes, params (string Role, Guid Tenant)[] assignments)
    {
        var created = await _superadministrator.PostAsJsonAsync("api/v1/users", new CreateUserRequest
        {
            TenantId = owner,
            EntraTenantId = person.TenantId,
            EntraObjectId = person.ObjectId,
            DisplayName = "Alex Wilber",
        }, Signal9Json.Options, Ct);
        Assert.AreEqual(HttpStatusCode.Created, created.StatusCode, await created.Content.ReadAsStringAsync(Ct));
        var user = (await created.Content.ReadFromJsonAsync<UserDto>(Signal9Json.Options, Ct))!;
        foreach (var (role, tenant) in assignments)
        {
            var assigned = await _superadministrator.PostAsJsonAsync($"api/v1/tenants/{tenant}/role-assignments",
                new CreateRoleAssignmentRequest { UserId = user.Id, RoleId = await RoleIdAsync(role) }, Signal9Json.Options, Ct);
            Assert.AreEqual(HttpStatusCode.Created, assigned.StatusCode, await assigned.Content.ReadAsStringAsync(Ct));
        }
        return await _factory.CreatePersonClientAsync(person, scopes);
    }

    private List<RoleDto>? _roles;

    private async Task<Guid> RoleIdAsync(string name)
    {
        _roles ??= [.. (await _superadministrator.GetFromJsonAsync<PagedResult<RoleDto>>("api/v1/roles", Signal9Json.Options, Ct))!.Items];
        return _roles.Single(r => r.Name == name).Id;
    }

    public void Dispose()
    {
        _mfaSuperadministrator?.Dispose();
        _superadministrator.Dispose();
        _factory.Dispose();
    }
}
