using System.Security.Claims;
using OpenIddict.Abstractions;
using OSI.Signal9.Contracts.Audit;
using OSI.Signal9.Server.Data;
using OSI.Signal9.Server.Identity;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace OSI.Signal9.API.Audit;

/// <summary>
/// The audit log as api's endpoints use it: each entry names the current caller as its actor, and the client address
/// the forwarded-headers middleware trusted (story 129).
/// </summary>
public sealed class CallerAudit(AuditLog log, IHttpContextAccessor http, IAssignedTenants caller)
{
    /// <summary>How many entries this request has recorded.</summary>
    public int Recorded => log.Recorded;

    /// <summary>The underlying log, for placing entries.</summary>
    public AuditLog Log => log;

    /// <summary>Adds <paramref name="entry"/>, by the caller, to the unit of work of the change it records.</summary>
    public AuditEntry Record(AuditEntry entry) => log.Add(ByCaller(entry));

    /// <summary>Writes <paramref name="entry"/>, by the caller, now.</summary>
    public Task WriteAsync(AuditEntry entry, CancellationToken cancellationToken) =>
        log.WriteAsync(ByCaller(entry), cancellationToken);

    private AuditEntry ByCaller(AuditEntry entry)
    {
        var context = http.HttpContext;
        entry.ClientIp ??= context?.Connection.RemoteIpAddress?.ToString();
        if (context?.User is not { Identity.IsAuthenticated: true } user)
        {
            entry.ActorKind = AuditActorKind.Anonymous;
            return entry;
        }

        entry.ActorKind = KindOf(user);
        entry.ActorSubject = user.FindFirst(Claims.Subject)?.Value;
        entry.ActorClientId = user.FindFirst(Claims.ClientId)?.Value;
        entry.ActorSuperadministrator = caller.IsSuperadministrator;
        return entry;
    }

    private static AuditActorKind KindOf(ClaimsPrincipal user) =>
        PersonSubject.TryParse(user, out _, out _) ? AuditActorKind.Person
        : user.HasScope(Server.Identity.Scopes.Agent) ? AuditActorKind.Agent
        : AuditActorKind.Application;
}
