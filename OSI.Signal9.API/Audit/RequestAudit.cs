using System.Text.RegularExpressions;
using Microsoft.Net.Http.Headers;
using OSI.Signal9.Contracts.Audit;
using OSI.Signal9.Server.Data;
using OSI.Signal9.Server.Identity;

namespace OSI.Signal9.API.Audit;

/// <summary>
/// What api audits about requests as a whole, whatever the endpoint: authorization denials (403), failed access tokens
/// toward the repeated-failure count (story 121), and every change a Superadministrator makes that no more specific
/// entry records (story 60).
/// </summary>
internal static partial class RequestAudit
{
    public static IApplicationBuilder UseRequestAudit(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        await next(context);

        var services = context.RequestServices;
        var cancellationToken = context.RequestAborted;
        var status = context.Response.StatusCode;
        if (status == StatusCodes.Status403Forbidden)
        {
            var audit = services.GetRequiredService<CallerAudit>();
            await audit.WriteAsync(new AuditEntry
            {
                Action = AuditActions.AuthorizationDenied,
                Outcome = AuditOutcome.Denied,
                TenantId = await PlaceAsync(context, audit.Log, orCallersOwn: true, cancellationToken),
                Detail = Describe(context) + MissingScope(context),
            }, cancellationToken);
        }
        // Credentials were presented and refused; a request with none is merely anonymous.
        else if (status == StatusCodes.Status401Unauthorized && context.Request.Headers.Authorization.Count > 0
            && context.User.Identity?.IsAuthenticated != true)
        {
            await services.GetRequiredService<AuthenticationFailures>().RecordAsync(context, clientId: null, cancellationToken);
        }
        else if (status is >= 200 and < 300 && !HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method)
            && !HttpMethods.IsOptions(context.Request.Method)
            && context.User.Identity?.IsAuthenticated == true
            // A Superadministrator's changes, through a delegated application too, though it holds them to its subtree.
            && (services.GetRequiredService<IAssignedTenants>().IsSuperadministrator || Superadministrator.Is(context.User)))
        {
            var audit = services.GetRequiredService<CallerAudit>();
            if (audit.Recorded > 0)
                return;
            await audit.WriteAsync(new AuditEntry
            {
                Action = AuditActions.SuperadministratorRequest,
                TenantId = await PlaceAsync(context, audit.Log, orCallersOwn: false, cancellationToken),
                Detail = Describe(context),
            }, cancellationToken);
        }
    });

    /// <summary>
    /// The tenant a request targeted, by the tenant, device or user its route names; failing that, the calling person's
    /// owning tenant if <paramref name="orCallersOwn"/>; otherwise none (deployment-level).
    /// </summary>
    private static async Task<Guid?> PlaceAsync(HttpContext context, AuditLog log, bool orCallersOwn, CancellationToken cancellationToken)
    {
        var route = context.Request.RouteValues;
        if (Id(route, "tenantId") is { } tenantId && await log.ExistingTenantAsync(tenantId, cancellationToken) is { } tenant)
            return tenant;
        if (Id(route, "deviceId") is { } deviceId && await log.TenantOfDeviceAsync(deviceId, cancellationToken) is { } ofDevice)
            return ofDevice;
        if (Id(route, "userId") is { } userId && await log.TenantOfUserAsync(userId, cancellationToken) is { } ofUser)
            return ofUser;
        if (orCallersOwn && PersonSubject.TryParse(context.User, out var entraTenantId, out var entraObjectId))
            return await log.TenantOfPersonAsync(entraTenantId, entraObjectId, cancellationToken);
        return null;
    }

    private static Guid? Id(RouteValueDictionary route, string name) =>
        route.TryGetValue(name, out var value) && Guid.TryParse(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture), out var id)
            ? id : null;

    /// <summary>The method and route template, never the raw path or query, which can carry names.</summary>
    private static string Describe(HttpContext context) =>
        $"{context.Request.Method} /{(context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText?.TrimStart('/') ?? "(no endpoint)"}";

    /// <summary>The scopes an <c>insufficient_scope</c> refusal named (RFC 6750 §3.1).</summary>
    private static string MissingScope(HttpContext context) =>
        ScopeParameter().Match(context.Response.Headers[HeaderNames.WWWAuthenticate].ToString()) is { Success: true } match
            ? $" (needs {match.Groups[1].Value})"
            : string.Empty;

    [GeneratedRegex("""scope="([^"]*)"\s*(,|$)""")]
    private static partial Regex ScopeParameter();
}
