using System.Security.Cryptography.X509Certificates;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;
using OSI.Signal9.API.Audit;
using OSI.Signal9.API.Infrastructure;
using OSI.Signal9.API.Tenants;
using OSI.Signal9.Contracts.Agents;
using OSI.Signal9.Contracts.Audit;
using OSI.Signal9.Contracts.Enrollment;
using OSI.Signal9.Contracts.Tenants;
using OSI.Signal9.Server.Data;

namespace OSI.Signal9.API.Agents;

/// <summary>
/// Enrolling agents (stories 98-100, #128) and identifying the device each one runs on (stories 103-106, #131): an
/// enrollment token's use, the agent, its device or pending identification, its OAuth client and the audit entries, in
/// one transaction. <see cref="AgentsController"/> turns each <see cref="Outcome"/> into its HTTP response.
/// </summary>
public sealed class AgentEnrollment(Signal9DbContext db, TimeProvider time, CallerAudit audit,
    IOpenIddictApplicationManager applications)
{
    /// <summary>How an enrollment ended.</summary>
    public abstract record Outcome
    {
        /// <summary>The agent enrolled, on <paramref name="Device"/>, or awaiting an identification decision without one.</summary>
        public sealed record Enrolled(Device? Device, Agent Agent) : Outcome;

        /// <summary>The enrollment token is unknown, revoked, expired or used up.</summary>
        public sealed record InvalidToken : Outcome;

        /// <summary>The token's tenant isn't active.</summary>
        public sealed record TenantNotActive : Outcome;

        /// <summary>The agent needs a new device, and its tenant is at its device limit.</summary>
        public sealed record DeviceLimitReached : Outcome;
    }

    /// <summary>
    /// Enrolls an agent with the enrollment token whose secret hashes to <paramref name="secretHash"/> and its
    /// already-validated <paramref name="certificate"/>.
    /// </summary>
    public async Task<Outcome> EnrollAsync(EnrollAgentRequest request, byte[] secretHash, X509Certificate2 certificate,
        string agentVersion, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var token = await db.EnrollmentTokens.AsNoTracking().IgnoreTenantScope(TenantScopeBypass.Enrollment)
            .SingleOrDefaultAsync(t => t.SecretHash == secretHash, cancellationToken);
        if (token is null || token.StatusAt(now.UtcDateTime) != EnrollmentTokenStatus.Active)
            return new Outcome.InvalidToken();
        var tenant = await db.Tenants.AsNoTracking().IgnoreTenantScope(TenantScopeBypass.Enrollment)
            .SingleAsync(t => t.Id == token.TenantId, cancellationToken);
        if (tenant.Status != TenantStatus.Active)
            return new Outcome.TenantNotActive();

        // The token's use, the new device and agent (which point at each other, so they're saved in two steps) and the
        // agent's client are saved in one transaction.
        return await db.InTransactionAsync(
            () => EnrollInTransactionAsync(tenant, token.Id, secretHash, certificate, request, agentVersion, now.UtcDateTime,
                cancellationToken),
            outcome => outcome is Outcome.Enrolled, cancellationToken);
    }

    private async Task<Outcome> EnrollInTransactionAsync(Tenant tenant, Guid tokenId, byte[] secretHash,
        X509Certificate2 certificate, EnrollAgentRequest request, string agentVersion, DateTime now,
        CancellationToken cancellationToken)
    {
        var reported = Reported(request, tenant.Id);
        await SerializeIdentificationAsync(reported, cancellationToken);

        // One use, unless a concurrent enrollment took the last one or the token changed since it was read: a rotation
        // retires the presented secret (story 94).
        var used = await db.EnrollmentTokens.IgnoreTenantScope(TenantScopeBypass.Enrollment)
            .Where(t => t.Id == tokenId && t.SecretHash == secretHash && t.RevokedAt == null && (t.ExpiresAt == null || t.ExpiresAt > now)
                && (t.MaxUses == null || t.UseCount < t.MaxUses))
            .ExecuteUpdateAsync(set => set
                .SetProperty(t => t.UseCount, t => t.UseCount + 1)
                .SetProperty(t => t.LastUsedAt, now), cancellationToken);
        if (used == 0)
            return new Outcome.InvalidToken();

        // Identification (stories 103-106): a new device when the hardware matches none in the token's root tree; the
        // existing device when exactly one matches and its previous agent was revoked, or, held for approval, when
        // exactly one matches and the operating system is fresh; otherwise a person decides.
        var candidates = await CandidateDevices(db.Devices.IgnoreTenantScope(TenantScopeBypass.Enrollment), reported)
            .Select(d => new
            {
                Device = d,
                PreviousRevoked = d.ActiveAgentId == null
                    && db.Agents.IgnoreTenantScope(TenantScopeBypass.Enrollment).Any(a => a.DeviceId == d.Id && a.RevokedAt != null),
            })
            .Take(2).ToListAsync(cancellationToken);

        var agent = new Agent
        {
            Id = Guid.CreateVersion7(time.GetUtcNow()),
            EnrollmentTokenId = tokenId,
            AgentVersion = agentVersion,
            EnrolledAt = now,
            LastSeenAt = now,
        };
        Device? device;
        string identification;
        var heldClaim = false;
        if (candidates is [])
        {
            if (await DeviceLimitReachedAsync(tenant, cancellationToken))
                return new Outcome.DeviceLimitReached();
            device = NewDevice(reported);
            db.Devices.Add(device);
            await db.SaveChangesAsync(cancellationToken);
            identification = "new-device";
        }
        else if (candidates is [{ PreviousRevoked: true, Device: var match }])
        {
            device = match;
            identification = $"matched {MatchedBy(match, reported)}";
        }
        else if (candidates is [{ Device: var claimed }] && IsFreshOs(request.OsInstalledAt, now))
        {
            device = claimed;
            heldClaim = true;
            identification = $"matched {MatchedBy(claimed, reported)} fresh-os";
        }
        else
        {
            device = null;
            identification = "needs-decision";
        }

        db.Agents.Add(agent);
        if (heldClaim)
        {
            // Agent-reported facts can be forged by anyone with a leaked installer and the machine's identifiers, so
            // the claim changes nothing until a person approves it: the device keeps its facts and its active agent.
            agent.DeviceId = device!.Id;
            agent.Device = device;
            agent.PendingIdentification = reported;
            agent.AwaitingApproval = true;
        }
        else if (device is not null)
            await BindAsync(agent, device, reported, cancellationToken);
        else
        {
            agent.PendingIdentification = reported;
            agent.AwaitingApproval = await db.HoldsNewAgentsAsync(tenant.Id, cancellationToken);
        }
        audit.Record(new AuditEntry
        {
            Action = AuditActions.AgentEnrolled,
            TenantId = tenant.Id,
            TargetType = "agent",
            TargetId = agent.Id,
            DeviceId = device?.Id,
            Detail = $"enrollment-token {tokenId}",
        });
        audit.Record(new AuditEntry
        {
            Action = device is null ? AuditActions.AgentIdentificationPending : AuditActions.AgentIdentified,
            TenantId = device?.TenantId ?? tenant.Id,
            TargetType = "agent",
            TargetId = agent.Id,
            DeviceId = device?.Id,
            Detail = identification,
        });
        await db.SaveChangesAsync(cancellationToken);
        await applications.CreateAsync(AgentCredentials.Client(agent.Id, certificate), cancellationToken);
        return new Outcome.Enrolled(device, agent);
    }

    /// <summary>
    /// Makes enrollments of one machine into one root tenant tree take turns, so each sees the device the one before
    /// it made, whichever enrollment tokens they use (one device per machine per root tree, #131). Tenant and device
    /// transfers, which hold the tree lock exclusively, can't change the root tree or move a device into it meanwhile.
    /// Held until the enrollment's transaction ends.
    /// </summary>
    private async Task SerializeIdentificationAsync(PendingIdentification reported, CancellationToken cancellationToken)
    {
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock_shared({TenantTree.LockKey})", cancellationToken);
        var hardware = new HardwareIdentifiers(reported.SmbiosUuid, reported.Manufacturer, reported.SerialNumber);
        if (hardware.IsEmpty)
            return;
        var root = await TenantTree.RootOfAsync(db, reported.TenantId, cancellationToken);
        foreach (var key in hardware.LockKeys(root))
            await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({key})", cancellationToken);
    }

    /// <summary>
    /// How long after its operating system was installed an enrolling agent counts as being on a wiped or re-imaged
    /// machine.
    /// </summary>
    public static readonly TimeSpan FreshOsWindow = TimeSpan.FromHours(24);

    /// <summary>
    /// Whether an operating system installed at <paramref name="installedAt"/> is fresh at <paramref name="now"/>. An
    /// unknown or future install time never is.
    /// </summary>
    private static bool IsFreshOs(DateTimeOffset? installedAt, DateTime now) =>
        installedAt?.UtcDateTime is { } at && at <= now && now - at < FreshOsWindow;

    /// <summary>Which identifier matched: for the audit log, which never records the identifiers themselves.</summary>
    private static string MatchedBy(Device device, PendingIdentification reported) =>
        device.SmbiosUuid is not null && device.SmbiosUuid == reported.SmbiosUuid ? "smbios-uuid" : "manufacturer-serial";

    /// <summary>What an enrolling agent reported, with its hardware identifiers normalized (story 104).</summary>
    private static PendingIdentification Reported(EnrollAgentRequest request, Guid tenantId)
    {
        var hardware = HardwareIdentifiers.From(request.SmbiosUuid, request.Manufacturer, request.SerialNumber);
        return new PendingIdentification
        {
            TenantId = tenantId,
            MachineName = request.MachineName,
            Domain = request.Domain,
            OsFamily = request.OsFamily!.Value,
            OperatingSystem = request.OperatingSystem,
            OsVersion = request.OsVersion,
            Architecture = request.Architecture,
            ProcessorName = request.ProcessorName,
            ProcessorCores = request.ProcessorCores,
            TotalMemoryMb = request.TotalMemoryMb,
            IpAddress = request.IpAddress,
            MacAddress = request.MacAddress,
            SmbiosUuid = hardware.SmbiosUuid,
            Manufacturer = hardware.Manufacturer,
            SerialNumber = hardware.SerialNumber,
        };
    }

    public Device NewDevice(PendingIdentification reported) => new()
    {
        Id = Guid.CreateVersion7(time.GetUtcNow()),
        TenantId = reported.TenantId,
        MachineName = reported.MachineName,
        OsFamily = reported.OsFamily,
        OperatingSystem = reported.OperatingSystem,
        CreatedAt = time.GetUtcNow().UtcDateTime,
    };

    /// <summary>
    /// Makes <paramref name="agent"/> <paramref name="device"/>'s active agent, bringing the device's facts up to date
    /// with what the agent reported. Hardware identifiers it couldn't report keep their previous values. The agent
    /// awaits approval if the device requires it or its tenant holds new agents (story 97).
    /// </summary>
    public async Task BindAsync(Agent agent, Device device, PendingIdentification reported, CancellationToken cancellationToken)
    {
        device.MachineName = reported.MachineName;
        device.Domain = reported.Domain;
        device.OsFamily = reported.OsFamily;
        device.OperatingSystem = reported.OperatingSystem;
        device.OsVersion = reported.OsVersion;
        device.Architecture = reported.Architecture;
        device.ProcessorName = reported.ProcessorName;
        device.ProcessorCores = reported.ProcessorCores;
        device.TotalMemoryMb = reported.TotalMemoryMb;
        device.IpAddress = reported.IpAddress;
        device.MacAddress = reported.MacAddress;
        device.SmbiosUuid = reported.SmbiosUuid ?? device.SmbiosUuid;
        if (reported.SerialNumber is not null)
            (device.Manufacturer, device.SerialNumber) = (reported.Manufacturer, reported.SerialNumber);

        agent.DeviceId = device.Id;
        agent.Device = device;
        device.ActiveAgent = agent;
        agent.AwaitingApproval = device.RequiresAgentApproval || await db.HoldsNewAgentsAsync(device.TenantId, cancellationToken);
    }

    /// <summary>Whether <paramref name="tenant"/> already has as many devices as it may.</summary>
    public async Task<bool> DeviceLimitReachedAsync(Tenant tenant, CancellationToken cancellationToken) =>
        await db.Devices.IgnoreTenantScope(TenantScopeBypass.Enrollment).CountAsync(d => d.TenantId == tenant.Id, cancellationToken)
            >= tenant.MaxDevices;

    /// <summary>
    /// The devices in <paramref name="reported"/>'s tenant's root tenant tree, in any subtree, that share its normalized
    /// SMBIOS UUID, or its manufacturer and serial (story 103). Identification never crosses root trees: two tenants
    /// share an ancestor only within one tree. Machine names play no part (story 106).
    /// </summary>
    public IQueryable<Device> CandidateDevices(IQueryable<Device> devices, PendingIdentification reported) =>
        devices.Where(d => db.TenantClosures.Any(mine => mine.DescendantId == reported.TenantId
                && db.TenantClosures.Any(theirs => theirs.AncestorId == mine.AncestorId && theirs.DescendantId == d.TenantId)))
            .Where(new HardwareIdentifiers(reported.SmbiosUuid, reported.Manufacturer, reported.SerialNumber).MatchingDevices());
}
