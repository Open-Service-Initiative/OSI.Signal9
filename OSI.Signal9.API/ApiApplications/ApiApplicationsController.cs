using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Net.Http.Headers;
using OpenIddict.Abstractions;
using OpenIddict.EntityFrameworkCore.Models;
using OSI.Signal9.API.Audit;
using OSI.Signal9.API.Authorization;
using OSI.Signal9.API.Infrastructure;
using OSI.Signal9.Contracts;
using OSI.Signal9.Contracts.ApiApplications;
using OSI.Signal9.Contracts.Audit;
using OSI.Signal9.Server.Data;
using OSI.Signal9.Server.Hosting;
using OSI.Signal9.Server.Identity;
using ClientTypes = OpenIddict.Abstractions.OpenIddictConstants.ClientTypes;
using Permissions = OpenIddict.Abstractions.OpenIddictConstants.Permissions;
using Credential = OSI.Signal9.Server.Identity.ApiApplicationClient.Credential;

namespace OSI.Signal9.API.ApiApplications;

/// <summary>
/// API applications acting as themselves (#124), at the tenant they're registered at: each is confined to that tenant
/// and its descendants (story 79), with its own role no higher than its creator's (story 80). A vendor's product is
/// registered separately at each tenant that uses it (story 90).
/// </summary>
[ApiController]
[Route("api/v1/tenants/{tenantId:guid}/api-applications")]
public sealed class ApiApplicationsController(
    Signal9DbContext db, CallerAudit audit, CallerAccess access, IAssignedTenants caller, TimeProvider time,
    IOpenIddictApplicationManager applications, IOpenIddictTokenManager tokens) : ControllerBase
{
    /// <summary>Lists the API applications registered at the tenant.</summary>
    [RequireScope(Scopes.ApiApplications.Read)]
    [HttpGet]
    [ProducesResponseType<PagedResult<ApiApplicationDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PagedResult<ApiApplicationDto>>> List(Guid tenantId, [FromQuery] PageQuery paging,
        CancellationToken cancellationToken)
    {
        if (!await db.Tenants.AnyAsync(t => t.Id == tenantId, cancellationToken))
            return NotFound();
        var page = await db.ApiApplications.AsNoTracking()
            .Where(a => a.TenantId == tenantId)
            .OrderBy(a => a.CreatedAt).ThenBy(a => a.Id)
            .ToPagedResultAsync(paging, a => a, cancellationToken);
        var items = new List<ApiApplicationDto>(page.Items.Count);
        foreach (var application in page.Items)
            items.Add(ToDto(application, await SubjectAsync(application, cancellationToken)));
        return new PagedResult<ApiApplicationDto>(items, page.Page, page.PageSize, page.TotalCount);
    }

    [RequireScope(Scopes.ApiApplications.Read)]
    [HttpGet("{apiApplicationId:guid}", Name = nameof(GetApiApplication))]
    [OptimisticConcurrency]
    [ProducesResponseType<ApiApplicationDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiApplicationDto>> GetApiApplication(Guid tenantId, Guid apiApplicationId,
        CancellationToken cancellationToken)
    {
        if (await FindAsync(tenantId, apiApplicationId, tracking: false, cancellationToken) is not { } application)
            return NotFound();
        var subject = await SubjectAsync(application, cancellationToken);
        return this.Conditional(ToDto(application, subject), ETag(application, subject));
    }

    /// <summary>
    /// Registers an API application at the tenant (story 78). An app-only application acts as itself with its own role
    /// (story 80): the role must be no higher than the caller's own access at the tenant (403), and its scopes within the
    /// role's; the <c>agent</c> and OpenID Connect scopes can't be granted (400). Granting a scope that needs a recent MFA
    /// needs the caller's own recent MFA (401 with RFC 9470's challenge for a person; 403 for an application), and is
    /// refused for an application that authenticates with a client secret (400). A delegated application has no role and
    /// acts for people who consent to it (story 81): it needs HTTPS redirect URIs, and may have any scope a person can
    /// hold but <c>agent</c>. Its PKCE is on unless <c>requireNonce</c> is set with it (story 86). Add credentials
    /// afterwards; a <c>tls_client_auth</c> application registers its certificate subject here instead.
    /// </summary>
    [RequireScope(Scopes.ApiApplications.Write)]
    [HttpPost]
    [OptimisticConcurrency]
    [Consumes("application/json")]
    [ProducesResponseType<ApiApplicationDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiApplicationDto>> Create(Guid tenantId, CreateApiApplicationRequest request,
        CancellationToken cancellationToken)
    {
        if (!await db.Tenants.AnyAsync(t => t.Id == tenantId, cancellationToken))
            return NotFound();
        if (this.CollectionPreconditions() is { } refused)
            return refused;
        ValidateSubject(request.TokenEndpointAuthMethod, request.TlsClientAuthSubject);
        ValidateKind(request.Kind, request.RoleId, request.RedirectUris, request.RequirePkce, request.RequireNonce);
        if (await GrantRefusalAsync(tenantId, request.Kind, request.RoleId, request.Scopes, request.TokenEndpointAuthMethod,
                previous: [], cancellationToken) is { } grantRefused)
            return grantRefused;

        var id = Guid.CreateVersion7(time.GetUtcNow());
        var delegated = request.Kind == ApiApplicationKinds.Delegated;
        var application = new ApiApplication
        {
            Id = id,
            TenantId = tenantId,
            ClientId = id.ToString("D"),
            DisplayName = request.DisplayName,
            Kind = request.Kind,
            RoleId = request.RoleId,
            Scopes = [.. request.Scopes.Distinct(StringComparer.Ordinal)],
            TokenEndpointAuthMethod = request.TokenEndpointAuthMethod,
            RedirectUris = delegated ? [.. request.RedirectUris.Distinct(StringComparer.Ordinal)] : [],
            RequirePkce = !delegated || request.RequirePkce,
            RequireNonce = delegated && request.RequireNonce,
            CreatedAt = time.GetUtcNow().UtcDateTime,
        };
        db.ApiApplications.Add(application);
        Audit(AuditActions.ApiApplicationRegistered, application);
        AuditGrant(application, previous: []);

        var descriptor = new OpenIddictApplicationDescriptor();
        ApiApplicationClient.Describe(descriptor, application);
        descriptor.Properties[ApiApplicationClient.AuthMethodProperty] = JsonSerializer.SerializeToElement(application.TokenEndpointAuthMethod);
        descriptor.Properties[ApiApplicationClient.CredentialsProperty] = ApiApplicationClient.Serialize([]);
        SetSubject(descriptor, request.TlsClientAuthSubject);
        // Saves the application, its OAuth client and the audit entries together.
        await applications.CreateAsync(descriptor, cancellationToken);

        this.SetETag(ETag(application, request.TlsClientAuthSubject));
        return CreatedAtRoute(nameof(GetApiApplication), new { tenantId, apiApplicationId = application.Id },
            ToDto(application, request.TlsClientAuthSubject));
    }

    /// <summary>
    /// Updates an API application with a JSON merge patch (RFC 7396): its name, its role and scopes (under the same
    /// rules as registering it), and a <c>tls_client_auth</c> application's certificate subject. Only a caller whose own
    /// access covers the application's current role can change it (403), and only an Administrator above its tenant can
    /// take a role or scope away from an application holding the Administrator role (story 22, 403). Its token endpoint
    /// authentication method can't change. Changes take effect on its next request.
    /// </summary>
    [RequireScope(Scopes.ApiApplications.Write)]
    [HttpPatch("{apiApplicationId:guid}")]
    [OptimisticConcurrency]
    [Consumes(MergePatch.MediaType)]
    [ProducesResponseType<ApiApplicationDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status415UnsupportedMediaType)]
    public async Task<ActionResult<ApiApplicationDto>> Update(Guid tenantId, Guid apiApplicationId,
        [FromBody] JsonElement patch, CancellationToken cancellationToken)
    {
        if (await FindAsync(tenantId, apiApplicationId, tracking: true, cancellationToken) is not { } application)
            return NotFound();
        var client = await ClientAsync(application, cancellationToken);
        var subject = TlsClientSubject.From(await applications.GetPropertiesAsync(client, cancellationToken)) is { } s
            ? new TlsClientAuthSubject(s.Parameter, s.Value) : null;
        if (this.Preconditions(ETag(application, subject)) is { } refused)
            return refused;
        if (await BeyondCallerAsync(application, cancellationToken) is { } beyondCaller)
            return beyondCaller;

        UpdateApiApplicationRequest request;
        try
        {
            request = MergePatch.Apply(new UpdateApiApplicationRequest
            {
                DisplayName = application.DisplayName,
                RoleId = application.RoleId,
                Scopes = application.Scopes,
                TlsClientAuthSubject = subject,
                RedirectUris = application.RedirectUris,
                RequirePkce = application.RequirePkce,
                RequireNonce = application.RequireNonce,
            }, patch);
        }
        catch (JsonException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            return ValidationProblem(ModelState);
        }
        if (!TryValidateModel(request))
            return ValidationProblem(ModelState);
        ValidateSubject(application.TokenEndpointAuthMethod, request.TlsClientAuthSubject);
        var delegated = application.Kind == ApiApplicationKinds.Delegated;
        ValidateKind(application.Kind, request.RoleId, request.RedirectUris, request.RequirePkce, request.RequireNonce);

        var previous = application.Scopes;
        var regranted = request.RoleId != application.RoleId || !request.Scopes.ToHashSet().SetEquals(previous);
        var downgraded = request.RoleId != application.RoleId || previous.Except(request.Scopes).Any();
        if (downgraded && await PeerAdministratorRefusalAsync(application, "downgrade") is { } peer)
            return peer;
        if (regranted && await GrantRefusalAsync(tenantId, application.Kind, request.RoleId, request.Scopes,
                application.TokenEndpointAuthMethod, previous, cancellationToken) is { } grantRefused)
            return grantRefused;
        if (!ModelState.IsValid)
            return ValidationProblem(ModelState);

        var renamed = request.DisplayName != application.DisplayName;
        var resubjected = request.TlsClientAuthSubject != subject;
        var reconfigured = delegated && (!request.RedirectUris.SequenceEqual(application.RedirectUris)
            || request.RequirePkce != application.RequirePkce || request.RequireNonce != application.RequireNonce);
        application.DisplayName = request.DisplayName;
        application.RoleId = request.RoleId;
        application.Scopes = [.. request.Scopes.Distinct(StringComparer.Ordinal)];
        if (delegated)
        {
            application.RedirectUris = [.. request.RedirectUris.Distinct(StringComparer.Ordinal)];
            application.RequirePkce = request.RequirePkce;
            application.RequireNonce = request.RequireNonce;
        }
        if (renamed || resubjected || reconfigured)
            Audit(AuditActions.ApiApplicationUpdated, application, resubjected ? "tls-client-auth-subject"
                : reconfigured ? $"redirect-uris: {string.Join(' ', application.RedirectUris)}; pkce: {application.RequirePkce}; nonce: {application.RequireNonce}"
                : null);
        if (regranted)
            AuditGrant(application, previous);

        var descriptor = await DescriptorAsync(client, cancellationToken);
        ApiApplicationClient.Describe(descriptor, application);
        SetSubject(descriptor, request.TlsClientAuthSubject);
        // Saves the application, its OAuth client and the audit entries together.
        await applications.UpdateAsync(client, descriptor, cancellationToken);

        this.SetETag(ETag(application, request.TlsClientAuthSubject));
        return ToDto(application, request.TlsClientAuthSubject);
    }

    /// <summary>
    /// Removes an API application and its OAuth client. Its tokens stop working on its next request. An application
    /// holding the Administrator role is a peer of the Administrators at its tenant, so only an Administrator above the
    /// tenant (or a Superadministrator, or the application itself) can remove it (story 22, 403).
    /// </summary>
    [RequireScope(Scopes.ApiApplications.Write)]
    [HttpDelete("{apiApplicationId:guid}")]
    [OptimisticConcurrency]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid tenantId, Guid apiApplicationId, CancellationToken cancellationToken)
    {
        if (await FindAsync(tenantId, apiApplicationId, tracking: true, cancellationToken) is not { } application)
            return NotFound();
        if (this.Preconditions(ETag(application, await SubjectAsync(application, cancellationToken))) is { } refused)
            return refused;
        if (await PeerAdministratorRefusalAsync(application, "remove") is { } peer)
            return peer;

        db.ApiApplications.Remove(application);
        Audit(AuditActions.ApiApplicationDeleted, application);
        if (await applications.FindByClientIdAsync(application.ClientId, cancellationToken) is { } client)
        {
            // The manager deletes its tokens and authorizations in bulk; one this request has already read (its own
            // token, when the application removes itself) mustn't then be deleted a second time by cascade.
            foreach (var entry in db.ChangeTracker.Entries()
                         .Where(e => e.Entity is OpenIddictEntityFrameworkCoreToken or OpenIddictEntityFrameworkCoreAuthorization)
                         .ToList())
                entry.State = EntityState.Detached;
            // Deletes its tokens and authorizations, then saves the removal and the audit entry.
            await applications.DeleteAsync(client, cancellationToken);
        }
        else
            await db.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    /// <summary>Lists the application's credentials. A secret's value is never shown again after it's created.</summary>
    [RequireScope(Scopes.ApiApplications.Read)]
    [HttpGet("{apiApplicationId:guid}/credentials")]
    [ProducesResponseType<PagedResult<ApiApplicationCredentialDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PagedResult<ApiApplicationCredentialDto>>> ListCredentials(Guid tenantId,
        Guid apiApplicationId, [FromQuery] PageQuery paging, CancellationToken cancellationToken)
    {
        if (await FindAsync(tenantId, apiApplicationId, tracking: false, cancellationToken) is not { } application)
            return NotFound();
        var credentials = await CredentialsAsync(await ClientAsync(application, cancellationToken), cancellationToken);
        return new PagedResult<ApiApplicationCredentialDto>(
            [.. credentials.OrderBy(c => c.CreatedAt).Skip((paging.Page - 1) * paging.PageSize).Take(paging.PageSize).Select(ToDto)],
            paging.Page, paging.PageSize, credentials.Count);
    }

    [RequireScope(Scopes.ApiApplications.Read)]
    [HttpGet("{apiApplicationId:guid}/credentials/{credentialId:guid}", Name = nameof(GetCredential))]
    [OptimisticConcurrency]
    [ProducesResponseType<ApiApplicationCredentialDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ApiApplicationCredentialDto>> GetCredential(Guid tenantId, Guid apiApplicationId,
        Guid credentialId, CancellationToken cancellationToken)
    {
        if (await FindAsync(tenantId, apiApplicationId, tracking: false, cancellationToken) is not { } application)
            return NotFound();
        var credential = (await CredentialsAsync(await ClientAsync(application, cancellationToken), cancellationToken))
            .SingleOrDefault(c => c.Id == credentialId);
        return credential is null ? NotFound() : this.Conditional(ToDto(credential), ETag(credential));
    }

    /// <summary>
    /// Adds a credential of the kind the application's method takes (story 84): a self-signed certificate
    /// (<c>self_signed_tls_client_auth</c>, under the same policy as an agent's: ECDSA P-256 or stronger or RSA 3072+,
    /// digitalSignature and clientAuth usages, not a CA, SHA-2 signed, valid now for at most 398 days), an RSA (2048 bits or more) or EC (P-256, P-384, P-521) public key or
    /// certificate (<c>private_key_jwt</c>), or a generated client secret, returned only in this response and stored as a
    /// hash, which expires within a year (180 days by default). A <c>tls_client_auth</c> application's credential is its
    /// certificate subject, changed on the application itself (400). Several can be live at once, for rotation. Only a
    /// caller whose own access covers the application's role can add one (403): a credential lets its holder act as it.
    /// </summary>
    [RequireScope(Scopes.ApiApplications.Write)]
    [HttpPost("{apiApplicationId:guid}/credentials")]
    [OptimisticConcurrency]
    [Consumes("application/json")]
    [ProducesResponseType<ApiApplicationCredentialDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ApiApplicationCredentialDto>> CreateCredential(Guid tenantId, Guid apiApplicationId,
        CreateApiApplicationCredentialRequest request, CancellationToken cancellationToken)
    {
        if (await FindAsync(tenantId, apiApplicationId, tracking: false, cancellationToken) is not { } application)
            return NotFound();
        if (this.CollectionPreconditions() is { } refused)
            return refused;
        // A credential lets its holder act as the application, so only someone at least as powerful may add one.
        if (await BeyondCallerAsync(application, cancellationToken) is { } beyondCaller)
            return beyondCaller;

        var manager = (Signal9ApplicationManager<OpenIddictEntityFrameworkCoreApplication>)applications;
        var (credential, secret, problem) = await ApiApplicationCredentials.CreateAsync(application.TokenEndpointAuthMethod,
            request, time.GetUtcNow(), manager.HashClientSecretAsync, cancellationToken);
        if (problem is not null)
            ModelState.AddModelError(problem.Field, problem.Message);
        if (credential is null || !ModelState.IsValid)
            return ValidationProblem(ModelState);

        var client = await ClientAsync(application, cancellationToken);
        var credentials = await CredentialsAsync(client, cancellationToken);
        if (credential.Thumbprint is { } thumbprint && credentials.Any(c => c.Thumbprint == thumbprint))
            return Problem(statusCode: StatusCodes.Status409Conflict, title: "Credential already registered",
                detail: "This application already has this certificate or key.");

        Audit(AuditActions.ApiApplicationCredentialAdded, application, $"{credential.Type} {credential.Id}");
        await SaveCredentialsAsync(client, [.. credentials, credential], cancellationToken);

        this.SetETag(ETag(credential));
        // A generated secret is shown only here.
        Response.NoStore();
        return CreatedAtRoute(nameof(GetCredential), new { tenantId, apiApplicationId, credentialId = credential.Id },
            ToDto(credential) with { Secret = secret });
    }

    /// <summary>
    /// Removes a credential. It stops authenticating at once, and every token already issued to the application is
    /// revoked, since any of them may have been obtained with it (RFC 9700 §4.14): the application gets new ones with its
    /// remaining credentials, and people who signed in through it keep their consent.
    /// </summary>
    [RequireScope(Scopes.ApiApplications.Write)]
    [HttpDelete("{apiApplicationId:guid}/credentials/{credentialId:guid}")]
    [OptimisticConcurrency]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteCredential(Guid tenantId, Guid apiApplicationId, Guid credentialId,
        CancellationToken cancellationToken)
    {
        if (await FindAsync(tenantId, apiApplicationId, tracking: false, cancellationToken) is not { } application)
            return NotFound();
        var client = await ClientAsync(application, cancellationToken);
        var credentials = await CredentialsAsync(client, cancellationToken);
        if (credentials.SingleOrDefault(c => c.Id == credentialId) is not { } credential)
            return NotFound();
        if (this.Preconditions(ETag(credential)) is { } refused)
            return refused;

        Audit(AuditActions.ApiApplicationCredentialRemoved, application, $"{credential.Type} {credential.Id}");
        await SaveCredentialsAsync(client, [.. credentials.Where(c => c.Id != credentialId)], cancellationToken);
        await tokens.RevokeByApplicationIdAsync((await applications.GetIdAsync(client, cancellationToken))!, cancellationToken);
        return NoContent();
    }

    // ── access ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Why the caller can't give an application <paramref name="roleId"/> with <paramref name="scopes"/>, or null if they
    /// can: the same anti-escalation as role assignments (story 18), plus the rules for scopes that need a recent MFA.
    /// </summary>
    private async Task<ActionResult?> GrantRefusalAsync(Guid tenantId, string kind, Guid? roleId, IReadOnlyList<string> scopes,
        string method, IReadOnlyCollection<string> previous, CancellationToken cancellationToken)
    {
        // A delegated application never acts beyond the person using it, so its scopes are only checked to exist; the
        // person's access, recent MFA and Superadministrator role are checked on each request (story 72).
        if (kind == ApiApplicationKinds.Delegated)
        {
            foreach (var scope in scopes.Distinct(StringComparer.Ordinal).Where(s => !Scopes.IsGrantableToDelegatedApplication(s)))
                ModelState.AddModelError(nameof(CreateApiApplicationRequest.Scopes),
                    $"'{scope}' can't be granted to a delegated API application.");
            return ModelState.IsValid ? null : ValidationProblem(ModelState);
        }

        var role = roleId is null ? null : await db.Roles.AsNoTracking().SingleOrDefaultAsync(r => r.Id == roleId, cancellationToken);
        if (role is null && roleId is not null)
            ModelState.AddModelError(nameof(CreateApiApplicationRequest.RoleId), "Role does not exist.");
        foreach (var scope in scopes.Distinct(StringComparer.Ordinal))
        {
            if (!Scopes.IsGrantableToApplication(scope))
                ModelState.AddModelError(nameof(CreateApiApplicationRequest.Scopes),
                    $"'{scope}' can't be granted to an API application acting as itself.");
            else if (role is not null && !role.Scopes.Contains(scope))
                ModelState.AddModelError(nameof(CreateApiApplicationRequest.Scopes), $"The {role.Name} role doesn't grant '{scope}'.");
        }

        var mfaGated = scopes.Where(s => Scopes.RequiringRecentMfa.Contains(s) && !previous.Contains(s)).Distinct().ToList();
        if (mfaGated.Count > 0 && method == TokenEndpointAuthMethods.ClientSecret)
            ModelState.AddModelError(nameof(CreateApiApplicationRequest.Scopes),
                $"{string.Join(", ", mfaGated)} need a recent MFA, which an application authenticating with a client secret can never hold: use a certificate or private_key_jwt.");
        if (!ModelState.IsValid)
            return ValidationProblem(ModelState);

        if (await BeyondCallerAsync(tenantId, role!, "give an application", cancellationToken) is { } beyond)
            return beyond;

        // Only a person can approve an MFA-gated scope, with an MFA in the last 15 minutes (#63).
        if (mfaGated.Count > 0 && !(RecentMfa.IsPerson(User) && RecentMfa.IsRecent(User, time)))
            return RecentMfa.IsPerson(User)
                ? new StepUpResult()
                : Problem(statusCode: StatusCodes.Status403Forbidden, title: "Recent MFA required",
                    detail: $"Granting {string.Join(", ", mfaGated)} needs a person who performed MFA within the last 15 minutes.");
        return null;
    }

    /// <summary>
    /// A 403 when <paramref name="role"/> grants more than the caller's own access at the tenant (their roles' scopes
    /// there ∩ their token's), so nobody can give, edit or take on an application above themselves (story 18).
    /// </summary>
    private async Task<ActionResult?> BeyondCallerAsync(Guid tenantId, Role role, string what, CancellationToken cancellationToken)
    {
        if (caller.IsSuperadministrator)
            return null;
        var own = await access.RoleScopesAtAsync(db, tenantId, cancellationToken);
        var beyond = role.Scopes.Where(scope => !own.Contains(scope) || !User.HasScope(scope)).ToList();
        return beyond.Count == 0 ? null : Problem(statusCode: StatusCodes.Status403Forbidden, title: "Role higher than your own",
            detail: $"You can {what} only a role within your own access at this tenant; {role.Name} also grants {string.Join(", ", beyond)}.");
    }

    /// <summary>A 403 unless the caller's own access covers the application's current role.</summary>
    private async Task<ActionResult?> BeyondCallerAsync(ApiApplication application, CancellationToken cancellationToken)
    {
        // A delegated application has no authority of its own to escalate to.
        if (application.RoleId is null)
            return null;
        var role = await db.Roles.AsNoTracking().SingleAsync(r => r.Id == application.RoleId, cancellationToken);
        return await BeyondCallerAsync(application.TenantId, role, "manage an application with", cancellationToken);
    }

    /// <summary>
    /// A 403 when <paramref name="application"/> holds the Administrator role and the caller is a peer Administrator at
    /// its tenant: like a person's Administrator assignment, it can be removed or downgraded only by an Administrator
    /// above the tenant or a Superadministrator (#63 "Restrictions on Administrators at their own assigned tenant", story
    /// 22). The application itself may give up its own authority, as a person may revoke their own assignment.
    /// </summary>
    private async Task<ActionResult?> PeerAdministratorRefusalAsync(ApiApplication application, string what)
    {
        if (application.RoleId != BuiltInRoles.Administrator.Id
            || await AncestorAdministrator.AdmitsAsync(HttpContext, application.TenantId)
            || access.Application?.Id == application.Id)
            return null;
        return Problem(statusCode: StatusCodes.Status403Forbidden, title: "Peer Administrator",
            detail: $"Only an Administrator above this tenant can {what} an application holding the Administrator role at it.");
    }

    /// <summary>Answers with RFC 9470's step-up challenge.</summary>
    private sealed class StepUpResult : ActionResult
    {
        public override Task ExecuteResultAsync(ActionContext context) => RecentMfa.ChallengeAsync(context.HttpContext);
    }

    /// <summary>
    /// An app-only application needs a role and has no redirect URIs; a delegated one has no role, at least one HTTPS
    /// redirect URI without a fragment (RFC 6749 §3.1.2, RFC 9700 §2.1), and nonce whenever PKCE is off (story 86).
    /// </summary>
    private void ValidateKind(string kind, Guid? roleId, IReadOnlyList<string> redirectUris, bool requirePkce, bool requireNonce)
    {
        if (kind == ApiApplicationKinds.AppOnly)
        {
            if (roleId is null)
                ModelState.AddModelError(nameof(CreateApiApplicationRequest.RoleId), "An app-only application needs a role.");
            if (redirectUris.Count > 0)
                ModelState.AddModelError(nameof(CreateApiApplicationRequest.RedirectUris),
                    "Only a delegated application has redirect URIs.");
            if (!requirePkce || requireNonce)
                ModelState.AddModelError(nameof(CreateApiApplicationRequest.RequirePkce),
                    "PKCE and nonce apply only to a delegated application.");
            return;
        }

        if (roleId is not null)
            ModelState.AddModelError(nameof(CreateApiApplicationRequest.RoleId),
                "A delegated application has no role: it acts with the signed-in person's access.");
        if (redirectUris.Count == 0)
            ModelState.AddModelError(nameof(CreateApiApplicationRequest.RedirectUris), "A delegated application needs a redirect URI.");
        foreach (var uri in redirectUris)
        {
            if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed) || parsed.Scheme != Uri.UriSchemeHttps
                || !string.IsNullOrEmpty(parsed.Fragment) || !string.IsNullOrEmpty(parsed.UserInfo))
                ModelState.AddModelError(nameof(CreateApiApplicationRequest.RedirectUris),
                    $"'{uri}' isn't an absolute HTTPS URI without a fragment or user information.");
        }
        if (!requirePkce && !requireNonce)
            ModelState.AddModelError(nameof(CreateApiApplicationRequest.RequirePkce),
                "PKCE can be turned off only by making nonce mandatory: set requireNonce too.");
    }

    private void ValidateSubject(string method, TlsClientAuthSubject? subject)
    {
        if (method == TokenEndpointAuthMethods.TlsClientAuth && subject is null)
            ModelState.AddModelError(nameof(CreateApiApplicationRequest.TlsClientAuthSubject),
                "A tls_client_auth application must register its certificate's subject (RFC 8705 §2.1.2).");
        else if (method != TokenEndpointAuthMethods.TlsClientAuth && subject is not null)
            ModelState.AddModelError(nameof(CreateApiApplicationRequest.TlsClientAuthSubject),
                "Only a tls_client_auth application registers a certificate subject.");
        else if (subject is { Parameter: TlsClientSubject.SubjectDn } && !IsDistinguishedName(subject.Value))
            ModelState.AddModelError(nameof(CreateApiApplicationRequest.TlsClientAuthSubject), "The subject isn't a distinguished name.");
        else if (subject is { Parameter: TlsClientSubject.SanIp } && !System.Net.IPAddress.TryParse(subject.Value, out _))
            ModelState.AddModelError(nameof(CreateApiApplicationRequest.TlsClientAuthSubject), "The subject isn't an IP address.");
    }

    private static bool IsDistinguishedName(string value)
    {
        try
        {
            _ = new X500DistinguishedName(value);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or CryptographicException)
        {
            return false;
        }
    }

    // ── storage ──────────────────────────────────────────────────────────────

    private Task<ApiApplication?> FindAsync(Guid tenantId, Guid id, bool tracking, CancellationToken cancellationToken)
    {
        var query = tracking ? db.ApiApplications : db.ApiApplications.AsNoTracking();
        return query.SingleOrDefaultAsync(a => a.TenantId == tenantId && a.Id == id, cancellationToken);
    }

    private async Task<object> ClientAsync(ApiApplication application, CancellationToken cancellationToken) =>
        await applications.FindByClientIdAsync(application.ClientId, cancellationToken)
        ?? throw new InvalidOperationException($"API application {application.Id} has no OAuth client.");

    private async Task<TlsClientAuthSubject?> SubjectAsync(ApiApplication application, CancellationToken cancellationToken)
    {
        if (application.TokenEndpointAuthMethod != TokenEndpointAuthMethods.TlsClientAuth)
            return null;
        var properties = await applications.GetPropertiesAsync(await ClientAsync(application, cancellationToken), cancellationToken);
        return TlsClientSubject.From(properties) is { } subject ? new TlsClientAuthSubject(subject.Parameter, subject.Value) : null;
    }

    private async Task<IReadOnlyList<Credential>> CredentialsAsync(object client, CancellationToken cancellationToken) =>
        ApiApplicationClient.Credentials(await applications.GetPropertiesAsync(client, cancellationToken));

    private async Task<OpenIddictApplicationDescriptor> DescriptorAsync(object client, CancellationToken cancellationToken)
    {
        var descriptor = new OpenIddictApplicationDescriptor();
        await applications.PopulateAsync(descriptor, client, cancellationToken);
        // The key set is derived from the credentials each time it's read; it isn't stored.
        descriptor.JsonWebKeySet = null;
        return descriptor;
    }

    private async Task SaveCredentialsAsync(object client, IEnumerable<Credential> credentials, CancellationToken cancellationToken)
    {
        var descriptor = await DescriptorAsync(client, cancellationToken);
        descriptor.Properties[ApiApplicationClient.CredentialsProperty] = ApiApplicationClient.Serialize(credentials);
        // Saves the credentials and the audit entry together.
        await applications.UpdateAsync(client, descriptor, cancellationToken);
    }

    private static void SetSubject(OpenIddictApplicationDescriptor descriptor, TlsClientAuthSubject? subject)
    {
        foreach (var parameter in new[] { TlsClientSubject.SubjectDn, TlsClientSubject.SanDns, TlsClientSubject.SanUri,
                     TlsClientSubject.SanIp, TlsClientSubject.SanEmail })
            descriptor.Properties.Remove(parameter);
        if (subject is not null)
            descriptor.Properties[subject.Parameter] = JsonSerializer.SerializeToElement(subject.Value);
    }

    // ── audit and mapping ────────────────────────────────────────────────────

    private void Audit(string action, ApiApplication application, string? detail = null) => audit.Record(new AuditEntry
    {
        Action = action,
        TenantId = application.TenantId,
        TargetType = "api-application",
        TargetId = application.Id,
        RoleId = application.RoleId,
        Detail = detail,
    });

    private void AuditGrant(ApiApplication application, IReadOnlyCollection<string> previous)
    {
        var withMfa = application.Scopes.Any(s => Scopes.RequiringRecentMfa.Contains(s) && !previous.Contains(s));
        Audit(AuditActions.ApiApplicationAccessGranted, application,
            $"scopes: {string.Join(' ', application.Scopes)}{(withMfa ? "; recent-mfa" : "")}");
    }

    private static ApiApplicationDto ToDto(ApiApplication application, TlsClientAuthSubject? subject) => new(
        application.Id, application.TenantId, application.ClientId, application.DisplayName, application.Kind, application.RoleId,
        application.Scopes, application.TokenEndpointAuthMethod, subject, application.RedirectUris, application.RequirePkce,
        application.RequireNonce, application.CreatedAt.AsUtcOffset());

    private static ApiApplicationCredentialDto ToDto(Credential credential) => new(
        credential.Id, credential.Type, credential.Thumbprint, credential.Subject, credential.CreatedAt, credential.ExpiresAt);

    private static EntityTagHeaderValue ETag(ApiApplication application, TlsClientAuthSubject? subject) =>
        ConditionalRequests.ETag(application.Version, subject?.Parameter, subject?.Value);

    private static EntityTagHeaderValue ETag(Credential credential) =>
        ConditionalRequests.ETag(0, credential.Id, credential.ExpiresAt);
}
