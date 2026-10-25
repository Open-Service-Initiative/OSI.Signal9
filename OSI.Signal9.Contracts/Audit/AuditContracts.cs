namespace OSI.Signal9.Contracts.Audit;

/// <summary>How the audited attempt ended.</summary>
public enum AuditOutcome
{
    Succeeded,

    /// <summary>The attempt failed, such as a sign-in Entra didn't complete or a command that failed.</summary>
    Failed,

    /// <summary>The caller wasn't allowed to do it.</summary>
    Denied,
}

/// <summary>Who acted.</summary>
public enum AuditActorKind
{
    /// <summary>A person, directly or through an application acting for them.</summary>
    Person,

    /// <summary>An API application acting as itself.</summary>
    Application,

    /// <summary>An enrolled agent.</summary>
    Agent,

    /// <summary>A caller who didn't authenticate.</summary>
    Anonymous,
}

/// <summary>
/// Who acted. <see cref="Subject"/> is the token's subject (a person's <c>{tid}:{oid}</c>, or an application's client
/// ID); <see cref="UserId"/> and <see cref="DisplayName"/> are resolved when the entry is read, when the reader can see
/// that user.
/// </summary>
public sealed record AuditActorDto(
    AuditActorKind Kind,
    string? Subject,
    string? ClientId,
    bool Superadministrator,
    Guid? UserId,
    string? DisplayName);

/// <summary>
/// One entry of the append-only audit log. It is stored at <see cref="TenantId"/>, the tenant where the event happened,
/// or has none for a deployment-level event, which only Superadministrators can read. Entries refer to tenants,
/// devices, users and roles by ID; the names beside them are resolved when the entry is read, and an entry about a
/// device that can't be resolved (erased, or outside the reader's reach) returns IDs only. <see cref="Detail"/> never
/// holds names, serials or output.
/// </summary>
public sealed record AuditEntryDto(
    Guid Id,
    DateTimeOffset OccurredAt,
    string Action,
    AuditOutcome Outcome,
    AuditActorDto Actor,
    Guid? TenantId,
    string? TenantName,
    string? TargetType,
    Guid? TargetId,
    Guid? DeviceId,
    string? DeviceName,
    Guid? UserId,
    string? UserDisplayName,
    Guid? RoleId,
    string? RoleName,
    string? Detail,
    string? ClientIp);

/// <summary>The audited actions, as <see cref="AuditEntryDto.Action"/> names them.</summary>
public static class AuditActions
{
    /// <summary>A person signed in at Entra through auth, or was refused (<see cref="AuditOutcome.Failed"/>).</summary>
    public const string SignIn = "sign-in";

    /// <summary>Authentication failed repeatedly from one client address (story 121). Deployment-level.</summary>
    public const string RepeatedAuthenticationFailures = "authentication.repeated-failures";

    /// <summary>A request was refused for lack of a scope or authority (403).</summary>
    public const string AuthorizationDenied = "authorization.denied";

    /// <summary>A Superadministrator's request that changed something and no more specific entry records (story 60).</summary>
    public const string SuperadministratorRequest = "superadministrator.request";

    /// <summary>A deliberate, audited bypass of the tenant filter (story 70). Deployment-level.</summary>
    public const string TenantScopeBypassed = "tenant-scope.bypassed";

    public const string TenantCreated = "tenant.created";
    public const string TenantMoved = "tenant.moved";
    public const string TenantDeleted = "tenant.deleted";

    public const string RoleAssignmentCreated = "role-assignment.created";

    public const string CommandCreated = "command.created";
    public const string CommandCancelled = "command.cancelled";

    /// <summary>The agent reported a command's result: <see cref="AuditOutcome.Succeeded"/> or <see cref="AuditOutcome.Failed"/>.</summary>
    public const string CommandResult = "command.result";
}
