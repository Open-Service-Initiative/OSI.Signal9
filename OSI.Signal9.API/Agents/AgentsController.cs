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
using OSI.Signal9.Contracts.Commands;
using OSI.Signal9.Contracts.Hubs;
using OSI.Signal9.Contracts.Tenants;

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
    IHubContext<DashboardHub, IDashboardHubClient> dashboard) : ControllerBase
{
    [RequireScope(Scopes.Devices.Read)]
    [HttpGet("{agentId:guid}", Name = nameof(GetAgent))]
    [OptimisticConcurrency]
    [ProducesResponseType<AgentDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AgentDto>> GetAgent(Guid agentId, CancellationToken cancellationToken)
    {
        var agent = await db.Agents.AsNoTracking().Include(a => a.Device)
            .SingleOrDefaultAsync(a => a.Id == agentId, cancellationToken);
        return agent is null ? NotFound() : this.Conditional(agent.ToDto(agent.Device!), agent.ETag(agent.Device!));
    }

    /// <summary>
    /// The tenant's "recently enrolled" feed (story 95): its agents, newest enrollment first, and by default those of its
    /// descendants too (<c>includeDescendants</c>), as far as the caller can see. Each carries its enrollment token, so
    /// a leaked token's enrollments are easy to spot.
    /// </summary>
    [RequireScope(Scopes.Devices.Read)]
    [HttpGet("/api/v1/tenants/{tenantId:guid}/agents")]
    [ProducesResponseType<PagedResult<AgentDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PagedResult<AgentDto>>> ListTenantAgents(Guid tenantId, [FromQuery] PageQuery paging,
        CancellationToken cancellationToken, [FromQuery] bool includeDescendants = true)
    {
        if (!await db.Tenants.AnyAsync(t => t.Id == tenantId, cancellationToken))
            return NotFound();

        var agents = db.Agents.AsNoTracking().Include(a => a.Device);
        var query = includeDescendants
            ? agents.Where(a => db.TenantClosures.Any(c => c.AncestorId == tenantId && c.DescendantId == a.Device!.TenantId))
            : agents.Where(a => a.Device!.TenantId == tenantId);
        return await query.OrderByDescending(a => a.EnrolledAt).ThenByDescending(a => a.Id)
            .ToPagedResultAsync(paging, a => a.ToDto(a.Device!), cancellationToken);
    }

    /// <summary>
    /// Enrolls a new agent with an enrollment token and the self-signed certificate of its new non-exportable key
    /// (stories 95, 98). The certificate becomes the agent's credential: it then gets <c>agent</c> tokens with client
    /// credentials at the mTLS token endpoint, its ID as client ID. The enrollment counts as one use of the token. An
    /// enrollment that matches no existing device creates one in the token's tenant; devices are never matched by name.
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
            var result = await EnrollAsync(tenant, token.Id, certificate, request, now.UtcDateTime, cancellationToken);
            if (result.Value is not null)
                await transaction.CommitAsync(cancellationToken);
            return result;
        });
        if (enrolled.Value is not ({ } device, { } agent))
            return enrolled.Problem!;

        await dashboard.Clients.All.DeviceChanged(device.ToDto(presence));
        this.SetETag(agent.ETag(device));
        return CreatedAtRoute(nameof(GetAgent), new { agentId = agent.Id }, agent.ToDto(device));
    }

    private async Task<(ActionResult? Problem, (Device, Agent)? Value)> EnrollAsync(Tenant tenant, Guid tokenId,
        System.Security.Cryptography.X509Certificates.X509Certificate2 certificate, EnrollAgentRequest request,
        DateTime now, CancellationToken cancellationToken)
    {
        // One use, unless a concurrent enrollment took the last one or the token changed since it was read.
        var used = await db.EnrollmentTokens.IgnoreTenantScope(TenantScopeBypass.Enrollment)
            .Where(t => t.Id == tokenId && t.RevokedAt == null && (t.ExpiresAt == null || t.ExpiresAt > now)
                && (t.MaxUses == null || t.UseCount < t.MaxUses))
            .ExecuteUpdateAsync(set => set
                .SetProperty(t => t.UseCount, t => t.UseCount + 1)
                .SetProperty(t => t.LastUsedAt, now), cancellationToken);
        if (used == 0)
            return (InvalidToken(), null);

        var devices = await db.Devices.IgnoreTenantScope(TenantScopeBypass.Enrollment).CountAsync(d => d.TenantId == tenant.Id, cancellationToken);
        if (devices >= tenant.MaxDevices)
            return (Problem(statusCode: StatusCodes.Status409Conflict, title: "Device limit reached",
                detail: $"Tenant '{tenant.Name}' allows at most {tenant.MaxDevices} devices."), null);

        // Identification (matching an existing device) is #131; until then every enrollment is a new device.
        var device = new Device
        {
            Id = Guid.CreateVersion7(time.GetUtcNow()),
            TenantId = tenant.Id,
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
            CreatedAt = now,
        };
        db.Devices.Add(device);
        await db.SaveChangesAsync(cancellationToken);

        var agent = new Agent
        {
            Id = Guid.CreateVersion7(time.GetUtcNow()),
            DeviceId = device.Id,
            EnrollmentTokenId = tokenId,
            AgentVersion = request.AgentVersion,
            EnrolledAt = now,
            LastSeenAt = now,
        };
        db.Agents.Add(agent);
        device.ActiveAgent = agent;
        audit.Record(new AuditEntry
        {
            Action = AuditActions.AgentEnrolled,
            TenantId = tenant.Id,
            TargetType = "agent",
            TargetId = agent.Id,
            DeviceId = device.Id,
            Detail = $"enrollment-token {tokenId}",
        });
        await db.SaveChangesAsync(cancellationToken);
        await applications.CreateAsync(AgentCredentials.Client(agent.Id, certificate), cancellationToken);
        return (null, (device, agent));
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
    /// Only the agent the access token names may call it (403 otherwise); a replaced agent gets 409.
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
        var (device, refusal) = await db.FindDeviceOfActiveAgentAsync(this, agentId, cancellationToken);
        if (device is null)
            return (ActionResult)refusal!;

        return await db.Commands.AsNoTracking().IgnoreTenantScope(TenantScopeBypass.AgentSelfService)
            .Where(c => c.DeviceId == device.Id && c.Status == CommandStatus.Pending)
            .OrderBy(c => c.CreatedAt).ThenBy(c => c.Id)
            .ToPagedResultAsync(query, c => c.ToDto(), cancellationToken);
    }

    /// <summary>
    /// Records that the agent is alive and tells it whether work is waiting. Sent by the agent on a fixed interval.
    /// Only the agent the access token names may send it (403 otherwise); a replaced agent gets 409.
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
        var (device, refusal) = await db.FindDeviceOfActiveAgentAsync(this, agentId, cancellationToken);
        if (device is null)
            return (ActionResult)refusal!;
        // The heartbeat is write-only: it has no representation, so it has no entity tag for If-Match to match.
        if (this.Preconditions(current: null) is { } refused)
            return refused;

        var agent = device.ActiveAgent!;
        var wasOnline = presence.StatusOf(device) == Contracts.Devices.DeviceStatus.Online;
        agent.LastSeenAt = time.GetUtcNow().UtcDateTime;
        agent.AgentVersion = request.AgentVersion ?? agent.AgentVersion;
        device.IpAddress = request.IpAddress ?? device.IpAddress;
        await db.RecordCheckInAsync(agent.Id, agent.LastSeenAt, cancellationToken, request.AgentVersion, request.IpAddress);

        if (!wasOnline)
            await dashboard.Clients.All.DeviceChanged(device.ToDto(presence));
        var hasPendingWork = await db.Commands.IgnoreTenantScope(TenantScopeBypass.AgentSelfService).AnyAsync(
            c => c.DeviceId == device.Id && c.Status == CommandStatus.Pending, cancellationToken);
        return new HeartbeatResponse(hasPendingWork);
    }
}
