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

    /// <summary>A person signed out of the dashboard, revoking every token behind their session (story 53).</summary>
    public const string SignOut = "sign-out";

    /// <summary>
    /// A person's session was revoked with all its tokens without them signing out (#119): Entra's front-channel logout
    /// (story 51), Entra refusing the person at the hourly check (story 52), or their last role assignment going (story
    /// 27). The detail says which.
    /// </summary>
    public const string SessionRevoked = "session.revoked";

    /// <summary>Authentication failed repeatedly from one client address (story 121). Deployment-level.</summary>
    public const string RepeatedAuthenticationFailures = "authentication.repeated-failures";

    /// <summary>A request was refused for lack of a scope or authority (403).</summary>
    public const string AuthorizationDenied = "authorization.denied";

    /// <summary>A Superadministrator's request that changed something and no more specific entry records (story 60).</summary>
    public const string SuperadministratorRequest = "superadministrator.request";

    /// <summary>A deliberate, audited bypass of the tenant filter (story 70). Deployment-level.</summary>
    public const string TenantScopeBypassed = "tenant-scope.bypassed";

    public const string TenantCreated = "tenant.created";

    /// <summary>
    /// A tenant transfer was proposed (story 7). Each transfer entry targets the transfer (<c>tenant-transfer</c>) and is
    /// stored at both sides, the tenant's former parent and its new one, once if one is above the other; a root
    /// tenant's former side is deployment-level. <see cref="AuditEntryDto.Detail"/> names the tenant and both parents
    /// by ID.
    /// </summary>
    public const string TenantTransferProposed = "tenant-transfer.proposed";

    /// <summary>The receiving side accepted a tenant transfer (story 8), which completed it in the same step.</summary>
    public const string TenantTransferAccepted = "tenant-transfer.accepted";

    /// <summary>The receiving side declined a tenant transfer (story 8).</summary>
    public const string TenantTransferDeclined = "tenant-transfer.declined";

    /// <summary>The proposing side withdrew a pending tenant transfer.</summary>
    public const string TenantTransferCancelled = "tenant-transfer.cancelled";

    /// <summary>An accepted tenant transfer moved the tenant, with its descendants, under its new parent (story 10).</summary>
    public const string TenantTransferCompleted = "tenant-transfer.completed";
    public const string TenantDeleted = "tenant.deleted";

    /// <summary>
    /// A person turned holding new agents for approval on or off at a tenant (story 96). <see cref="AuditEntryDto.Detail"/>
    /// is <c>on</c> or <c>off</c>.
    /// </summary>
    public const string TenantApprovalHoldChanged = "tenant.approval-hold-changed";

    /// <summary>A user was created, stored at their owning tenant (story 15).</summary>
    public const string UserCreated = "user.created";

    /// <summary>A user's editable fields changed (stories 15-16), stored at their owning tenant.</summary>
    public const string UserUpdated = "user.updated";

    public const string RoleAssignmentCreated = "role-assignment.created";
    public const string RoleAssignmentDeleted = "role-assignment.deleted";

    public const string EnrollmentTokenCreated = "enrollment-token.created";
    public const string EnrollmentTokenRotated = "enrollment-token.rotated";
    public const string EnrollmentTokenRevoked = "enrollment-token.revoked";

    /// <summary>An API application was registered at a tenant.</summary>
    public const string ApiApplicationRegistered = "api-application.registered";

    /// <summary>An API application's name or <c>tls_client_auth</c> subject changed.</summary>
    public const string ApiApplicationUpdated = "api-application.updated";

    /// <summary>
    /// An API application was given its role and scopes, at registration or later. <c>Detail</c> lists the scopes, and
    /// says <c>recent-mfa</c> when ones needing a recent MFA were granted with it.
    /// </summary>
    public const string ApiApplicationAccessGranted = "api-application.access-granted";

    public const string ApiApplicationDeleted = "api-application.deleted";

    /// <summary>
    /// A person consented at auth to a delegated API application acting for them (story 87), stored at the
    /// application's tenant. <c>Detail</c> lists every scope the consent now covers.
    /// </summary>
    public const string ApiApplicationConsentGranted = "api-application.consent-granted";

    /// <summary>
    /// A person's connection to a delegated API application was revoked (stories 88-89), by the person or an
    /// Administrator, stored at the application's tenant. <c>Detail</c> names the person by subject.
    /// </summary>
    public const string ApiApplicationConsentRevoked = "api-application.consent-revoked";
    public const string ApiApplicationCredentialAdded = "api-application.credential-added";
    public const string ApiApplicationCredentialRemoved = "api-application.credential-removed";

    /// <summary>An agent enrolled with an enrollment token, which <see cref="AuditEntryDto.Detail"/> names by ID.</summary>
    public const string AgentEnrolled = "agent.enrolled";

    /// <summary>
    /// An agent was identified (stories 103-105): <see cref="AuditEntryDto.Detail"/> is <c>new-device</c>,
    /// <c>matched smbios-uuid</c> or <c>matched manufacturer-serial</c> when it enrolled, or a person's decision,
    /// <c>reinstalled</c> or <c>keep-both</c>. <see cref="AuditEntryDto.DeviceId"/> is the device it now belongs to.
    /// </summary>
    public const string AgentIdentified = "agent.identified";

    /// <summary>
    /// An enrolling agent's hardware matched devices it couldn't be matched to automatically, so it waits in the
    /// Needs-decision queue for a person (story 105). Stored at its enrollment token's tenant.
    /// </summary>
    public const string AgentIdentificationPending = "agent.identification-pending";

    /// <summary>A person revoked an agent, or decided it was replaced by a reinstall (<c>Detail</c> names the new one).</summary>
    public const string AgentRevoked = "agent.revoked";

    /// <summary>A person approved an agent that was awaiting approval, so it may now receive work (story 97).</summary>
    public const string AgentApproved = "agent.approved";

    /// <summary>
    /// An agent replaced its certificate with a new key's (story 102). <see cref="AuditEntryDto.Detail"/> names both by
    /// SHA-256 thumbprint.
    /// </summary>
    public const string AgentCertificateRotated = "agent.certificate-rotated";

    /// <summary>
    /// A person moved a device to another tenant (story 75). Recorded twice, so both sides can read it: at the tenant it
    /// left, with <see cref="AuditEntryDto.Detail"/> <c>to tenant {id}</c>, and at the tenant it joined, with
    /// <c>from tenant {id}</c>.
    /// </summary>
    public const string DeviceTransferred = "device.transferred";

    public const string CommandCreated = "command.created";
    public const string CommandCancelled = "command.cancelled";

    /// <summary>The agent reported a command's result: <see cref="AuditOutcome.Succeeded"/> or <see cref="AuditOutcome.Failed"/>.</summary>
    public const string CommandResult = "command.result";
}
