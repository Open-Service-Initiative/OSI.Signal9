using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
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
using OSI.Signal9.Server.Identity;
using AuthorizationTypes = OpenIddict.Abstractions.OpenIddictConstants.AuthorizationTypes;
using Statuses = OpenIddict.Abstractions.OpenIddictConstants.Statuses;

namespace OSI.Signal9.API.ApiApplications;

/// <summary>
/// Connected applications (#126): each person's consent to a delegated API application, kept by auth as a permanent
/// authorization. A connection belongs to its application. The person can list and revoke their own (story 88), and an
/// Administrator those of the applications in their subtree (story 89). Revoking one ends every token the application
/// holds for the person, on its next request.
/// </summary>
[ApiController]
public sealed class ConnectionsController(
    Signal9DbContext db, CallerAudit audit, CallerAccess access,
    IOpenIddictAuthorizationManager authorizations, IOpenIddictTokenManager tokens) : ControllerBase
{
    private const string ApplicationConnections =
        "/api/v1/tenants/{tenantId:guid}/api-applications/{apiApplicationId:guid}/connections";

    /// <summary>The caller's own connected applications.</summary>
    [RequireScope(Scopes.ApiApplications.Read)]
    [HttpGet("/api/v1/me/connections")]
    [ProducesResponseType<PagedResult<ConnectionDto>>(StatusCodes.Status200OK)]
    public async Task<PagedResult<ConnectionDto>> ListMine([FromQuery] PageQuery paging, CancellationToken cancellationToken) =>
        CallerSubject() is { } subject
            ? await PageAsync(Rows(OwnReach(), subject: subject), paging, cancellationToken)
            : new PagedResult<ConnectionDto>([], paging.Page, paging.PageSize, 0);

    /// <summary>
    /// The connections to the API applications registered at the tenant, and by default at its descendants too
    /// (<c>includeDescendants</c>), as far as the caller can see.
    /// </summary>
    [RequireScope(Scopes.ApiApplications.Read)]
    [HttpGet("/api/v1/tenants/{tenantId:guid}/connections")]
    [ProducesResponseType<PagedResult<ConnectionDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PagedResult<ConnectionDto>>> ListTenant(Guid tenantId, [FromQuery] PageQuery paging,
        CancellationToken cancellationToken, [FromQuery] bool includeDescendants = true)
    {
        if (!await db.Tenants.AnyAsync(t => t.Id == tenantId, cancellationToken))
            return NotFound();
        var applications = includeDescendants
            ? db.ApiApplications.Where(a => db.TenantClosures.Any(c => c.AncestorId == tenantId && c.DescendantId == a.TenantId))
            : db.ApiApplications.Where(a => a.TenantId == tenantId);
        return await PageAsync(Rows(applications), paging, cancellationToken);
    }

    /// <summary>The people connected to the API application.</summary>
    [RequireScope(Scopes.ApiApplications.Read)]
    [HttpGet(ApplicationConnections)]
    [ProducesResponseType<PagedResult<ConnectionDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PagedResult<ConnectionDto>>> ListApplication(Guid tenantId, Guid apiApplicationId,
        [FromQuery] PageQuery paging, CancellationToken cancellationToken)
    {
        var applications = db.ApiApplications.Where(a => a.Id == apiApplicationId && a.TenantId == tenantId);
        if (!await applications.AnyAsync(cancellationToken))
            return NotFound();
        return await PageAsync(Rows(applications), paging, cancellationToken);
    }

    /// <summary>A connection: the caller's own, or one to an application in their reach.</summary>
    [RequireScope(Scopes.ApiApplications.Read)]
    [HttpGet(ApplicationConnections + "/{connectionId:guid}", Name = nameof(GetConnection))]
    [OptimisticConcurrency]
    [ProducesResponseType<ConnectionDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ConnectionDto>> GetConnection(Guid tenantId, Guid apiApplicationId, Guid connectionId,
        CancellationToken cancellationToken)
    {
        if (await FindAsync(tenantId, apiApplicationId, connectionId, cancellationToken) is not { } connection)
            return NotFound();
        return this.Conditional(connection.Dto, connection.ETag);
    }

    /// <summary>
    /// Revokes the connection: the person's consent and every token the application holds for them stop working at
    /// once, and the application must ask the person to consent again.
    /// </summary>
    [RequireScope(Scopes.ApiApplications.Write)]
    [HttpDelete(ApplicationConnections + "/{connectionId:guid}")]
    [OptimisticConcurrency]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Revoke(Guid tenantId, Guid apiApplicationId, Guid connectionId,
        CancellationToken cancellationToken)
    {
        if (await FindAsync(tenantId, apiApplicationId, connectionId, cancellationToken) is not { } connection)
            return NotFound();
        if (this.Preconditions(connection.ETag) is { } refused)
            return refused;

        // OpenIddict 7 doesn't revoke an authorization's tokens with it (#63), so both go: every authorization and token
        // the application holds for the person, whichever consent they came from.
        var row = connection.Row;
        await authorizations.RevokeAsync(row.Subject, row.ClientKey, status: null, type: null, cancellationToken);
        await tokens.RevokeAsync(row.Subject, row.ClientKey, status: null, type: null, cancellationToken);
        await audit.WriteAsync(new AuditEntry
        {
            Action = AuditActions.ApiApplicationConsentRevoked,
            TenantId = row.TenantId,
            TargetType = "api-application",
            TargetId = row.ApiApplicationId,
            UserId = connection.Dto.UserId,
            Detail = $"subject: {row.Subject}",
        }, cancellationToken);
        return NoContent();
    }

    /// <summary>A connection as stored: the authorization and the API application it was given to.</summary>
    private sealed record Row(
        string Id, string Subject, string? Scopes, DateTime? CreatedAt, string? ConcurrencyToken, string ClientKey,
        Guid ApiApplicationId, Guid TenantId, string ApiApplicationName);

    private sealed record Connection(Row Row, ConnectionDto Dto, EntityTagHeaderValue ETag);

    /// <summary>
    /// The connection, if it's the caller's own (within a delegated application's subtree) or its application is in
    /// the caller's reach.
    /// </summary>
    private async Task<Connection?> FindAsync(Guid tenantId, Guid apiApplicationId, Guid connectionId,
        CancellationToken cancellationToken)
    {
        var row = await Rows(OwnReach().Where(a => a.Id == apiApplicationId && a.TenantId == tenantId),
            id: connectionId.ToString("D")).SingleOrDefaultAsync(cancellationToken);
        if (row is null)
            return null;
        if (row.Subject != CallerSubject()
            && !await db.ApiApplications.AnyAsync(a => a.Id == apiApplicationId && a.TenantId == tenantId, cancellationToken))
            return null;
        var dto = (await ToDtosAsync([row], cancellationToken)).Single();
        return new Connection(row, dto,
            ConditionalRequests.ETag(0, row.ConcurrencyToken, dto.ApiApplicationName, dto.UserDisplayName));
    }

    /// <summary>
    /// The API applications whose connections the caller may have: any, found past the tenant filter, except through
    /// a delegated application, which holds the person to its subtree.
    /// </summary>
    private IQueryable<ApiApplication> OwnReach()
    {
        var applications = db.ApiApplications.IgnoreTenantScope(TenantScopeBypass.CallerConnections);
        return access.Delegated is { } delegated
            ? applications.Where(a => db.TenantClosures.Any(c => c.AncestorId == delegated.TenantId && c.DescendantId == a.TenantId))
            : applications;
    }

    /// <summary>The valid consents to <paramref name="applications"/>, newest first.</summary>
    private IQueryable<Row> Rows(IQueryable<ApiApplication> applications, string? subject = null, string? id = null)
    {
        var consents = db.Set<OpenIddictEntityFrameworkCoreAuthorization>().AsNoTracking()
            .Where(a => a.Status == Statuses.Valid && a.Type == AuthorizationTypes.Permanent && a.Subject != null);
        if (subject is not null)
            consents = consents.Where(a => a.Subject == subject);
        if (id is not null)
            consents = consents.Where(a => a.Id == id);
        return from consent in consents
               join application in applications on consent.Application!.ClientId equals application.ClientId
               orderby consent.CreationDate descending, consent.Id
               select new Row(consent.Id!, consent.Subject!, consent.Scopes, consent.CreationDate, consent.ConcurrencyToken,
                   consent.Application!.Id!, application.Id, application.TenantId, application.DisplayName);
    }

    private async Task<PagedResult<ConnectionDto>> PageAsync(IQueryable<Row> rows, PageQuery paging,
        CancellationToken cancellationToken)
    {
        var total = await rows.CountAsync(cancellationToken);
        var page = await rows.Skip((paging.Page - 1) * paging.PageSize).Take(paging.PageSize).ToListAsync(cancellationToken);
        return new PagedResult<ConnectionDto>(await ToDtosAsync(page, cancellationToken), paging.Page, paging.PageSize, total);
    }

    /// <summary>The connections, naming each person's user where the caller can see it.</summary>
    private async Task<List<ConnectionDto>> ToDtosAsync(IReadOnlyList<Row> rows, CancellationToken cancellationToken)
    {
        var objectIds = rows
            .Select(r => PersonSubject.TryParse(r.Subject, out _, out var objectId) ? objectId : Guid.Empty)
            .Where(id => id != Guid.Empty).Distinct().ToArray();
        var users = await db.Users.AsNoTracking()
            .Where(u => objectIds.Contains(u.EntraObjectId))
            .Select(u => new { u.Id, u.DisplayName, Subject = u.EntraTenantId.ToString() + ":" + u.EntraObjectId.ToString() })
            .ToListAsync(cancellationToken);
        return
        [
            .. rows.Select(r =>
            {
                var user = users.FirstOrDefault(u => string.Equals(u.Subject, r.Subject, StringComparison.OrdinalIgnoreCase));
                return new ConnectionDto(
                    Guid.Parse(r.Id),
                    $"/api/v1/tenants/{r.TenantId}/api-applications/{r.ApiApplicationId}/connections/{r.Id}",
                    r.TenantId,
                    r.ApiApplicationId,
                    r.ApiApplicationName,
                    r.Subject,
                    user?.Id,
                    user?.DisplayName,
                    r.Scopes is null ? [] : JsonSerializer.Deserialize<string[]>(r.Scopes) ?? [],
                    (r.CreatedAt ?? DateTime.UnixEpoch).AsUtcOffset());
            }),
        ];
    }

    /// <summary>The person the caller's token names, as their subject, or null for an application acting as itself.</summary>
    private string? CallerSubject() =>
        PersonSubject.TryParse(User, out var entraTenantId, out var entraObjectId)
            ? PersonSubject.Format(entraTenantId, entraObjectId)
            : null;
}
