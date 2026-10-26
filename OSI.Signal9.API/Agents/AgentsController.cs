using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using OpenIddict.Abstractions;
using OSI.Signal9.API.Audit;
using OSI.Signal9.Contracts.Audit;
using OSI.Signal9.Contracts.Enrollment;
using OSI.Signal9.Server.Hosting;
using OSI.Signal9.API.Authorization;
using OSI.Signal9.Server.Identity;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using OSI.Signal9.Server.Data;
using OSI.Signal9.API.Devices;
using OSI.Signal9.API.Hubs;
using OSI.Signal9.API.Infrastructure;
using OSI.Signal9.Contracts;
using OSI.Signal9.Contracts.Agents;
using OSI.Signal9.Contracts.Devices;
using OSI.Signal9.Contracts.Commands;
using OSI.Signal9.Contracts.Hubs;
using OSI.Signal9.Contracts.Tenants;
using OSI.Signal9.API.Tenants;

namespace OSI.Signal9.API.Agents;

/// <summary>
/// Agents: enrolled installations of the agent software, each on one device, each its own OAuth client (ADR-0005).
/// Requests an agent sends about itself take its identity from its access token.
/// </summary>
[ApiController]
[Route("api/v1/agents")]
public sealed class AgentsController(
    Signal9DbContext db,
    DevicePresence presence,
    TimeProvider time,
    CallerAudit audit,
    RequestRateLimits limits,
    IOpenIddictApplicationManager applications,
    IOpenIddictTokenManager tokens,
    AgentHubConnections hubConnections,
    IHubContext<AgentHub, IAgentHubClient> agentHub,
    DashboardEvents dashboard) : ControllerBase
{
    [RequireScope(Scopes.Devices.Read)]
    [HttpGet("{agentId:guid}", Name = nameof(GetAgent))]
    [OptimisticConcurrency]
    [ProducesResponseType<AgentDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AgentDto>> GetAgent(Guid agentId, CancellationToken cancellationToken)
    {
        var agent = await db.Agents.AsNoTracking().Include(a => a.Device).Include(a => a.PendingIdentification)
            .SingleOrDefaultAsync(a => a.Id == agentId, cancellationToken);
        return agent is null ? NotFound() : this.Conditional(agent.ToDto(), agent.ETag());
    }

    /// <summary>
    /// Revokes the agent for good (story 101): it stops being its device's active agent, its access tokens stop working
    /// at once, its hub connection closes, and the token endpoint answers it with <c>agent_revoked</c>. Its device then
    /// requires approval for its next agent, whatever the tenant's approval setting, so reinstalling from the same
    /// enrollment token can't silently undo the revocation. Revoking a held fresh-OS claim rejects it, leaving its device
    /// and the device's agent alone. A revoked agent can't be revoked again (409).
    /// </summary>
    [RequireScope(Scopes.Devices.Write)]
    [HttpPost("{agentId:guid}:revoke")]
    [OptimisticConcurrency]
    [ProducesResponseType<AgentDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status412PreconditionFailed)]
    public async Task<ActionResult<AgentDto>> Revoke(Guid agentId, CancellationToken cancellationToken)
    {
        var agent = await db.Agents.Include(a => a.Device).Include(a => a.PendingIdentification)
            .SingleOrDefaultAsync(a => a.Id == agentId, cancellationToken);
        if (agent is null)
            return NotFound();
        var device = agent.Device;
        if (this.Preconditions(agent.ETag()) is { } refused)
            return refused;
        if (agent.RevokedAt is not null)
            return Problem(statusCode: StatusCodes.Status409Conflict, title: "Transition not allowed",
                detail: "The agent is already revoked.");

        // An agent awaiting identification has no device yet; it stays in the queue, revoked. Rejecting a held fresh-OS
        // claim leaves its device and the device's agent alone.
        var heldClaim = agent.IsHeldClaim();
        agent.RevokedAt = time.GetUtcNow().UtcDateTime;
        if (device is not null && !heldClaim)
        {
            if (device.ActiveAgentId == agent.Id)
                device.ActiveAgentId = null;
            device.RequiresAgentApproval = true;
        }
        audit.Record(new AuditEntry
        {
            Action = AuditActions.AgentRevoked,
            TenantId = agent.ToDto().TenantId,
            TargetType = "agent",
            TargetId = agent.Id,
            DeviceId = device?.Id,
        });
        await db.SaveChangesAsync(cancellationToken);
        // Once saved, the agent is no longer active, which every agent request checks; its tokens and hub connection
        // go next, so nothing it already holds keeps working.
        await CutOffAsync(agent.Id, cancellationToken);

        if (device is not null)
        {
            await dashboard.DeviceChangedAsync(device.TenantId, device.Id, cancellationToken);
        }
        this.SetETag(agent.ETag());
        return agent.ToDto();
    }

    /// <summary>
    /// Approves an agent that is awaiting approval (story 97): from then on it receives work, and its doorbell rings if
    /// work is already waiting. Its device no longer requires approval. Approving a held fresh-OS claim makes the agent
    /// its device's active agent, bringing the device's facts up to date, and revokes the agent it replaces. Only an
    /// agent awaiting approval can be approved (409).
    /// </summary>
    [RequireScope(Scopes.Devices.Write)]
    [HttpPost("{agentId:guid}:approve")]
    [OptimisticConcurrency]
    [ProducesResponseType<AgentDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status412PreconditionFailed)]
    public async Task<ActionResult<AgentDto>> Approve(Guid agentId, CancellationToken cancellationToken)
    {
        var agent = await db.Agents.Include(a => a.Device!).ThenInclude(d => d.ActiveAgent).Include(a => a.PendingIdentification)
            .SingleOrDefaultAsync(a => a.Id == agentId, cancellationToken);
        if (agent is null)
            return NotFound();
        if (this.Preconditions(agent.ETag()) is { } refused)
            return refused;
        if (agent.Status() != AgentStatus.AwaitingApproval)
            return Problem(statusCode: StatusCodes.Status409Conflict, title: "Transition not allowed",
                detail: $"The agent is {agent.Status()}, not awaiting approval.");
        var device = agent.Device!;

        Agent? replaced = null;
        if (agent.IsHeldClaim())
        {
            if (device.ActiveAgent is { } previous)
            {
                previous.RevokedAt = time.GetUtcNow().UtcDateTime;
                replaced = previous;
                audit.Record(new AuditEntry
                {
                    Action = AuditActions.AgentRevoked,
                    TenantId = device.TenantId,
                    TargetType = "agent",
                    TargetId = previous.Id,
                    DeviceId = device.Id,
                    Detail = $"replaced by agent {agent.Id}",
                });
            }
            var claim = await db.PendingIdentifications.IgnoreTenantScope(TenantScopeBypass.HeldClaimFacts)
                .SingleAsync(p => p.AgentId == agent.Id, cancellationToken);
            await BindAsync(agent, device, claim, cancellationToken);
            db.PendingIdentifications.Remove(claim);
        }
        agent.AwaitingApproval = false;
        if (device.ActiveAgent == agent)
            device.RequiresAgentApproval = false;
        audit.Record(new AuditEntry
        {
            Action = AuditActions.AgentApproved,
            TenantId = device.TenantId,
            TargetType = "agent",
            TargetId = agent.Id,
            DeviceId = device.Id,
        });
        await db.SaveChangesAsync(cancellationToken);
        if (replaced is not null)
            await CutOffAsync(replaced.Id, cancellationToken);

        await RingIfWorkWaitingAsync(agent, cancellationToken);
        await dashboard.DeviceChangedAsync(device.TenantId, device.Id, cancellationToken);
        this.SetETag(agent.ETag());
        return agent.ToDto();
    }

    /// <summary>Rings the agent's doorbell if it may receive work and its device has some waiting.</summary>
    private async Task RingIfWorkWaitingAsync(Agent agent, CancellationToken cancellationToken)
    {
        if (agent.Status() == AgentStatus.Active && agent.Device!.ActiveAgentId == agent.Id && await db.Commands.AnyAsync(
                c => c.DeviceId == agent.DeviceId && c.Status == CommandStatus.Pending, cancellationToken))
            await agentHub.Clients.Group(AgentHub.GroupFor(agent.Id)).WorkAvailable();
    }

    /// <summary>
    /// Decides an agent in the Needs-decision queue (story 105). <see cref="IdentificationDecision.Reinstalled"/>
    /// rebinds it to one of its candidate devices, which keeps its telemetry and command history; that device's active
    /// agent, if it still has one, is revoked, since the person says the machine was reinstalled.
    /// <see cref="IdentificationDecision.KeepBoth"/> gives it a new device in its enrollment token's tenant. Either way
    /// it then awaits approval if its device requires approval or its tenant holds new agents. Only an agent that needs
    /// a decision can be decided (409).
    /// </summary>
    /// <response code="400">The decision is incomplete, or the device isn't one of the agent's candidates.</response>
    /// <response code="409">The agent doesn't need a decision, or Keep both would exceed the tenant's device limit.</response>
    [RequireScope(Scopes.Devices.Write)]
    [HttpPost("{agentId:guid}:identify")]
    [OptimisticConcurrency]
    [Consumes("application/json")]
    [ProducesResponseType<AgentDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status412PreconditionFailed)]
    public async Task<ActionResult<AgentDto>> Identify(Guid agentId, IdentifyAgentRequest request,
        CancellationToken cancellationToken)
    {
        var agent = await db.Agents.Include(a => a.Device).Include(a => a.PendingIdentification)
            .SingleOrDefaultAsync(a => a.Id == agentId, cancellationToken);
        if (agent is null)
            return NotFound();
        if (this.Preconditions(agent.ETag()) is { } refused)
            return refused;
        if (agent.Status() != AgentStatus.NeedsDecision)
            return Problem(statusCode: StatusCodes.Status409Conflict, title: "Transition not allowed",
                detail: $"The agent is {agent.Status()}; it doesn't need an identification decision.");

        var pending = agent.PendingIdentification!;
        Device device;
        Agent? replaced = null;
        if (request.Decision == IdentificationDecision.Reinstalled)
        {
            var candidate = await CandidateDevices(db.Devices.Include(d => d.ActiveAgent), pending)
                .SingleOrDefaultAsync(d => d.Id == request.DeviceId, cancellationToken);
            if (candidate is null)
            {
                ModelState.AddModelError(nameof(request.DeviceId), "The device isn't one of the agent's candidate devices.");
                return ValidationProblem(ModelState);
            }
            device = candidate;
            if (device.ActiveAgent is { } previous)
            {
                previous.RevokedAt = time.GetUtcNow().UtcDateTime;
                replaced = previous;
                audit.Record(new AuditEntry
                {
                    Action = AuditActions.AgentRevoked,
                    TenantId = device.TenantId,
                    TargetType = "agent",
                    TargetId = previous.Id,
                    DeviceId = device.Id,
                    Detail = $"replaced by agent {agent.Id}",
                });
            }
        }
        else
        {
            var tenant = await db.Tenants.SingleAsync(t => t.Id == pending.TenantId, cancellationToken);
            if (await DeviceLimitReachedAsync(tenant, cancellationToken) is { } limit)
                return limit;
            device = NewDevice(pending);
            db.Devices.Add(device);
        }

        await BindAsync(agent, device, pending, cancellationToken);
        db.PendingIdentifications.Remove(pending);
        audit.Record(new AuditEntry
        {
            Action = AuditActions.AgentIdentified,
            TenantId = device.TenantId,
            TargetType = "agent",
            TargetId = agent.Id,
            DeviceId = device.Id,
            Detail = request.Decision == IdentificationDecision.Reinstalled ? "reinstalled" : "keep-both",
        });
        await db.SaveChangesAsync(cancellationToken);
        if (replaced is not null)
            await CutOffAsync(replaced.Id, cancellationToken);

        await RingIfWorkWaitingAsync(agent, cancellationToken);
        await dashboard.DeviceChangedAsync(device.TenantId, device.Id, cancellationToken);
        this.SetETag(agent.ETag());
        return agent.ToDto();
    }

    /// <summary>
    /// The devices an agent in the Needs-decision queue might be a reinstall of (story 105): those in its enrollment
    /// token's root tenant tree, as far as the caller can see, sharing its SMBIOS UUID or its manufacturer and serial.
    /// Empty for an agent that doesn't need a decision.
    /// </summary>
    [RequireScope(Scopes.Devices.Read)]
    [HttpGet("{agentId:guid}/candidate-devices")]
    [ProducesResponseType<PagedResult<DeviceDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PagedResult<DeviceDto>>> ListCandidateDevices(Guid agentId,
        [FromQuery] PageQuery paging, CancellationToken cancellationToken)
    {
        var agent = await db.Agents.AsNoTracking().Include(a => a.PendingIdentification)
            .SingleOrDefaultAsync(a => a.Id == agentId, cancellationToken);
        if (agent is null)
            return NotFound();

        var devices = db.Devices.AsNoTracking().Include(d => d.ActiveAgent);
        var candidates = agent.Status() == AgentStatus.NeedsDecision
            ? CandidateDevices(devices, agent.PendingIdentification!)
            : devices.Where(_ => false);
        return await candidates.OrderBy(d => d.CreatedAt).ThenBy(d => d.Id)
            .ToPagedResultAsync(paging, d => d.ToDto(presence), cancellationToken);
    }

    /// <summary>
    /// The devices in <paramref name="reported"/>'s tenant's root tenant tree, in any subtree, that share its normalized
    /// SMBIOS UUID, or its manufacturer and serial (story 103). Identification never crosses root trees: two tenants
    /// share an ancestor only within one tree. Machine names play no part (story 106).
    /// </summary>
    private IQueryable<Device> CandidateDevices(IQueryable<Device> devices, PendingIdentification reported) =>
        devices.Where(d => db.TenantClosures.Any(mine => mine.DescendantId == reported.TenantId
                && db.TenantClosures.Any(theirs => theirs.AncestorId == mine.AncestorId && theirs.DescendantId == d.TenantId)))
            .Where(new HardwareIdentifiers(reported.SmbiosUuid, reported.Manufacturer, reported.SerialNumber).MatchingDevices());

    /// <summary>The certificate the agent authenticates with: its thumbprint and validity.</summary>
    [RequireScope(Scopes.Devices.Read)]
    [HttpGet("{agentId:guid}/certificate")]
    [OptimisticConcurrency]
    [ProducesResponseType<AgentCertificateDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AgentCertificateDto>> GetCertificate(Guid agentId, CancellationToken cancellationToken)
    {
        if (!await db.Agents.AnyAsync(a => a.Id == agentId, cancellationToken)
            || await CurrentCertificateAsync(agentId, cancellationToken) is not { } current)
            return NotFound();
        using (current)
        {
            var dto = current.ToCertificateDto();
            return this.Conditional(dto, CertificateETag(dto));
        }
    }

    /// <summary>
    /// Replaces the agent's certificate with a new key's, so long-lived installations don't depend on one key forever
    /// (story 102). Only the agent itself may, with a token bound to its current certificate. The new certificate must
    /// meet the same rules as at enrollment. From then on only the new certificate authenticates: the old one is no
    /// longer the agent's credential, and every token bound to it is revoked and its hub connection closed. Putting the
    /// current certificate again changes nothing.
    /// </summary>
    /// <response code="400">The certificate isn't acceptable.</response>
    /// <response code="403">The caller isn't this agent, or it's revoked.</response>
    /// <response code="409">The agent isn't its device's active agent.</response>
    [RequireScope(Scopes.Agent)]
    [HttpPut("{agentId:guid}/certificate")]
    [OptimisticConcurrency]
    [Consumes("application/json")]
    [ProducesResponseType<AgentCertificateDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status412PreconditionFailed)]
    public async Task<ActionResult<AgentCertificateDto>> PutCertificate(Guid agentId, AgentCertificateRequest request,
        CancellationToken cancellationToken)
    {
        var (agent, refusal) = await db.FindActiveAgentAsync(this, agentId, cancellationToken);
        if (agent is null)
            return (ActionResult)refusal!;
        var client = await applications.FindByClientIdAsync(agentId.ToString(), cancellationToken)
            ?? throw new InvalidOperationException($"Agent {agentId} has no client.");
        using var current = await CurrentCertificateAsync(agentId, cancellationToken)
            ?? throw new InvalidOperationException($"Agent {agentId} has no certificate.");
        var currentDto = current.ToCertificateDto();
        if (this.Preconditions(CertificateETag(currentDto)) is { } refused)
            return refused;

        using var certificate = AgentCredentials.Read(request.Certificate, time.GetUtcNow(), out var problem);
        if (certificate is null)
        {
            ModelState.AddModelError(nameof(request.Certificate), problem!);
            return ValidationProblem(ModelState);
        }

        var dto = certificate.ToCertificateDto();
        if (!certificate.RawData.AsSpan().SequenceEqual(current.RawData))
        {
            audit.Record(new AuditEntry
            {
                Action = AuditActions.AgentCertificateRotated,
                TenantId = agent.ToDto().TenantId,
                TargetType = "agent",
                TargetId = agentId,
                DeviceId = agent.DeviceId,
                Detail = $"{currentDto.Thumbprint} -> {dto.Thumbprint}",
            });
            // The client's key set becomes the new certificate alone; saving it saves the audit entry with it.
            var descriptor = new OpenIddictApplicationDescriptor();
            await applications.PopulateAsync(descriptor, client, cancellationToken);
            descriptor.JsonWebKeySet = AgentCredentials.Client(agentId, certificate).JsonWebKeySet;
            await applications.UpdateAsync(client, descriptor, cancellationToken);
            await CutOffAsync(agentId, cancellationToken);
        }

        this.SetETag(CertificateETag(dto));
        return dto;
    }

    private async Task<System.Security.Cryptography.X509Certificates.X509Certificate2?> CurrentCertificateAsync(
        Guid agentId, CancellationToken cancellationToken) =>
        await applications.FindByClientIdAsync(agentId.ToString(), cancellationToken) is { } client
        && await applications.GetJsonWebKeySetAsync(client, cancellationToken) is { Keys: [var key] }
            ? Server.Identity.ClientCertificateKey.Certificate(key)
            : null;

    private static Microsoft.Net.Http.Headers.EntityTagHeaderValue CertificateETag(AgentCertificateDto certificate) =>
        ConditionalRequests.ETag(0, certificate.Thumbprint);

    /// <summary>Revokes every token the agent's client holds and closes its hub connections.</summary>
    private async Task CutOffAsync(Guid agentId, CancellationToken cancellationToken)
    {
        if (await applications.FindByClientIdAsync(agentId.ToString(), cancellationToken) is { } client
            && await applications.GetIdAsync(client, cancellationToken) is { } clientKey)
            await tokens.RevokeByApplicationIdAsync(clientKey, cancellationToken);
        hubConnections.Close(agentId);
    }

    /// <summary>
    /// The tenant's "recently enrolled" feed (story 95): its agents, newest enrollment first, and by default those of its
    /// descendants too (<c>includeDescendants</c>), as far as the caller can see. Each carries its enrollment token, so
    /// a leaked token's enrollments are easy to spot. <c>status</c> narrows it to agents in that status, such as those
    /// awaiting approval.
    /// </summary>
    [RequireScope(Scopes.Devices.Read)]
    [HttpGet("/api/v1/tenants/{tenantId:guid}/agents")]
    [ProducesResponseType<PagedResult<AgentDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PagedResult<AgentDto>>> ListTenantAgents(Guid tenantId, [FromQuery] PageQuery paging,
        CancellationToken cancellationToken, [FromQuery] bool includeDescendants = true, [FromQuery] AgentStatus? status = null)
    {
        if (!await db.Tenants.AnyAsync(t => t.Id == tenantId, cancellationToken))
            return NotFound();

        var agents = db.Agents.AsNoTracking().Include(a => a.Device).Include(a => a.PendingIdentification).AsQueryable();
        agents = status switch
        {
            AgentStatus.Revoked => agents.Where(a => a.RevokedAt != null),
            AgentStatus.NeedsDecision => agents.Where(a => a.RevokedAt == null && a.DeviceId == null),
            AgentStatus.AwaitingApproval => agents.Where(a => a.RevokedAt == null && a.DeviceId != null && a.AwaitingApproval),
            AgentStatus.Active => agents.Where(a => a.RevokedAt == null && a.DeviceId != null && !a.AwaitingApproval),
            _ => agents,
        };
        // An agent's tenant is its device's, or while it awaits identification, its pending identification's.
        var query = includeDescendants
            ? agents.Where(a => db.TenantClosures.Any(c => c.AncestorId == tenantId
                && (c.DescendantId == a.Device!.TenantId || c.DescendantId == a.PendingIdentification!.TenantId)))
            : agents.Where(a => a.Device!.TenantId == tenantId || a.PendingIdentification!.TenantId == tenantId);
        return await query.OrderByDescending(a => a.EnrolledAt).ThenByDescending(a => a.Id)
            .ToPagedResultAsync(paging, a => a.ToDto(), cancellationToken);
    }

    /// <summary>
    /// Enrolls a new agent with an enrollment token and the self-signed certificate of its new non-exportable key
    /// (stories 95, 98). The certificate becomes the agent's credential: it then gets <c>agent</c> tokens with client
    /// credentials at the mTLS token endpoint, its ID as client ID. The enrollment counts as one use of the token.
    /// Identification (stories 103-106): an enrollment whose hardware identifiers match no device in the token's root
    /// tenant tree creates one in the token's tenant; one matching exactly one device whose previous agent was revoked
    /// rejoins it, keeping its history; so does one matching exactly one device from an operating system installed
    /// within <see cref="FreshOsWindow"/>, as a held fresh-OS claim (<see cref="Mappings.IsHeldClaim"/>); any other
    /// match waits, with no device, in the Needs-decision queue (<see cref="AgentStatus.NeedsDecision"/>). A device stays
    /// in its tenant, and an agent that joins it belongs there. Zeroed and placeholder identifiers are ignored, and
    /// devices are never matched by name. The agent awaits approval if its tenant or an ancestor holds new agents, or
    /// its device requires approval.
    /// </summary>
    /// <response code="400">The certificate or machine facts aren't acceptable, or the enrollment token is unknown,
    /// revoked, expired or used up.</response>
    /// <response code="409">The tenant isn't active, or has reached its device limit.</response>
    /// <response code="429">Too many enrollments from this address or with this token.</response>
    [AllowAnonymous]
    [EnableRateLimiting(RateLimiting.Enrollment)]
    [HttpPost]
    [OptimisticConcurrency]
    [Consumes("application/json")]
    [ProducesResponseType<AgentDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<AgentDto>> Enroll(EnrollAgentRequest request, CancellationToken cancellationToken)
    {
        if (this.Preconditions(current: null) is { } refused)
            return refused;

        var secretHash = EnrollmentSecret.Hash(request.EnrollmentToken);
        using var lease = limits.Acquire(RateLimiting.EnrollmentToken, Convert.ToHexString(secretHash));
        if (!lease.IsAcquired)
            return TooManyRequests(lease);

        var now = time.GetUtcNow();
        using var certificate = AgentCredentials.Read(request.Certificate, now, out var problem);
        if (certificate is null)
        {
            ModelState.AddModelError(nameof(request.Certificate), problem!);
            return ValidationProblem(ModelState);
        }

        var token = await db.EnrollmentTokens.AsNoTracking().IgnoreTenantScope(TenantScopeBypass.Enrollment)
            .SingleOrDefaultAsync(t => t.SecretHash == secretHash, cancellationToken);
        if (token is null || token.StatusAt(now.UtcDateTime) != EnrollmentTokenStatus.Active)
            return InvalidToken();
        var tenant = await db.Tenants.AsNoTracking().IgnoreTenantScope(TenantScopeBypass.Enrollment)
            .SingleAsync(t => t.Id == token.TenantId, cancellationToken);
        if (tenant.Status != TenantStatus.Active)
            return Problem(statusCode: StatusCodes.Status409Conflict, title: "Tenant not active",
                detail: $"The tenant is {tenant.Status}; agents can enroll only into an active tenant.");

        // The token's use, the new device and agent (which point at each other, so they're saved in two steps) and the
        // agent's client are saved in one transaction.
        var strategy = db.Database.CreateExecutionStrategy();
        var enrolled = await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var result = await EnrollAsync(tenant, token.Id, secretHash, certificate, request, now.UtcDateTime, cancellationToken);
            if (result.Value is not null)
                await transaction.CommitAsync(cancellationToken);
            return result;
        });
        if (enrolled.Value is not (var device, { } agent))
            return enrolled.Problem!;

        if (device is not null)
            await dashboard.DeviceChangedAsync(device.TenantId, device.Id, cancellationToken);
        this.SetETag(agent.ETag());
        return CreatedAtRoute(nameof(GetAgent), new { agentId = agent.Id }, agent.ToDto());
    }

    private async Task<(ActionResult? Problem, (Device?, Agent)? Value)> EnrollAsync(Tenant tenant, Guid tokenId,
        byte[] secretHash, System.Security.Cryptography.X509Certificates.X509Certificate2 certificate, EnrollAgentRequest request,
        DateTime now, CancellationToken cancellationToken)
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
            return (InvalidToken(), null);

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
            AgentVersion = request.AgentVersion,
            EnrolledAt = now,
            LastSeenAt = now,
        };
        Device? device;
        string identification;
        var heldClaim = false;
        if (candidates is [])
        {
            if (await DeviceLimitReachedAsync(tenant, cancellationToken) is { } limit)
                return (limit, null);
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
        return (null, (device, agent));
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

    private Device NewDevice(PendingIdentification reported) => new()
    {
        Id = Guid.CreateVersion7(time.GetUtcNow()),
        TenantId = reported.TenantId,
        MachineName = reported.MachineName,
        OperatingSystem = reported.OperatingSystem,
        CreatedAt = time.GetUtcNow().UtcDateTime,
    };

    /// <summary>
    /// Makes <paramref name="agent"/> <paramref name="device"/>'s active agent, bringing the device's facts up to date
    /// with what the agent reported. Hardware identifiers it couldn't report keep their previous values. The agent
    /// awaits approval if the device requires it or its tenant holds new agents (story 97).
    /// </summary>
    private async Task BindAsync(Agent agent, Device device, PendingIdentification reported, CancellationToken cancellationToken)
    {
        device.MachineName = reported.MachineName;
        device.Domain = reported.Domain;
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

    private async Task<ActionResult?> DeviceLimitReachedAsync(Tenant tenant, CancellationToken cancellationToken)
    {
        var devices = await db.Devices.IgnoreTenantScope(TenantScopeBypass.Enrollment).CountAsync(d => d.TenantId == tenant.Id, cancellationToken);
        return devices < tenant.MaxDevices ? null : Problem(statusCode: StatusCodes.Status409Conflict,
            title: "Device limit reached", detail: $"Tenant '{tenant.Name}' allows at most {tenant.MaxDevices} devices.");
    }

    private ActionResult InvalidToken()
    {
        ModelState.AddModelError(nameof(EnrollAgentRequest.EnrollmentToken),
            "The enrollment token is unknown, revoked, expired or used up.");
        return ValidationProblem(ModelState);
    }

    private ObjectResult TooManyRequests(System.Threading.RateLimiting.RateLimitLease lease)
    {
        if (lease.TryGetMetadata(System.Threading.RateLimiting.MetadataName.RetryAfter, out var retryAfter))
            Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return Problem(statusCode: StatusCodes.Status429TooManyRequests, title: "Too many requests",
            detail: "Too many enrollments with this enrollment token; try again later.");
    }

    /// <summary>
    /// Lists the pending commands of the agent's device, oldest first. The agent calls it when its doorbell rings, every
    /// time it (re)connects and when a heartbeat says work is waiting, then reports each result at the command's URL.
    /// Only the agent the access token names may call it (403 otherwise); a replaced agent gets 409. An agent awaiting
    /// approval or an identification decision receives no work, so its list is empty.
    /// </summary>
    [RequireScope(Scopes.Agent)]
    [HttpGet("{agentId:guid}/pending-commands")]
    [ProducesResponseType<PagedResult<CommandDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<PagedResult<CommandDto>>> ListPendingCommands(
        Guid agentId, [FromQuery] PageQuery query, CancellationToken cancellationToken)
    {
        var (agent, refusal) = await db.FindActiveAgentAsync(this, agentId, cancellationToken);
        if (agent is null)
            return (ActionResult)refusal!;

        var deviceId = agent.DeviceId;
        var mayReceiveWork = agent.Status() == AgentStatus.Active;
        return await db.Commands.AsNoTracking().IgnoreTenantScope(TenantScopeBypass.AgentSelfService)
            .Where(c => mayReceiveWork && c.DeviceId == deviceId && c.Status == CommandStatus.Pending)
            .OrderBy(c => c.CreatedAt).ThenBy(c => c.Id)
            .ToPagedResultAsync(query, c => c.ToDto(), cancellationToken);
    }

    /// <summary>
    /// Records that the agent is alive and tells it whether work is waiting. Sent by the agent on a fixed interval.
    /// Only the agent the access token names may send it (403 otherwise); a replaced agent gets 409. An agent awaiting
    /// approval or an identification decision is accepted too, but never told work is waiting.
    /// </summary>
    [RequireScope(Scopes.Agent)]
    [HttpPut("{agentId:guid}/heartbeat")]
    [OptimisticConcurrency(Representation = false)]
    [Consumes("application/json")]
    [ProducesResponseType<HeartbeatResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<HeartbeatResponse>> Heartbeat(
        Guid agentId, HeartbeatRequest request, CancellationToken cancellationToken)
    {
        var (agent, refusal) = await db.FindActiveAgentAsync(this, agentId, cancellationToken);
        if (agent is null)
            return (ActionResult)refusal!;
        // The heartbeat is write-only: it has no representation, so it has no entity tag for If-Match to match.
        if (this.Preconditions(current: null) is { } refused)
            return refused;

        // An agent awaiting identification has no device to bring online yet.
        var device = agent.Device;
        var wasOnline = device is not null && presence.StatusOf(device) == Contracts.Devices.DeviceStatus.Online;
        agent.LastSeenAt = time.GetUtcNow().UtcDateTime;
        agent.AgentVersion = request.AgentVersion ?? agent.AgentVersion;
        if (device is not null)
            device.IpAddress = request.IpAddress ?? device.IpAddress;
        await db.RecordCheckInAsync(agent.Id, agent.LastSeenAt, cancellationToken, request.AgentVersion, request.IpAddress);

        if (device is not null && !wasOnline)
            await dashboard.DeviceChangedAsync(device.TenantId, device.Id, cancellationToken);
        var hasPendingWork = agent.Status() == AgentStatus.Active && await db.Commands
            .IgnoreTenantScope(TenantScopeBypass.AgentSelfService)
            .AnyAsync(c => c.DeviceId == agent.DeviceId && c.Status == CommandStatus.Pending, cancellationToken);
        return new HeartbeatResponse(hasPendingWork);
    }
}
