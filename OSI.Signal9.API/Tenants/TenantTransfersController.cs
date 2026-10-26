using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;
using OSI.Signal9.API.Audit;
using OSI.Signal9.API.Authorization;
using OSI.Signal9.API.Hubs;
using OSI.Signal9.API.Infrastructure;
using OSI.Signal9.Contracts;
using OSI.Signal9.Contracts.Audit;
using OSI.Signal9.Contracts.Tenants;
using OSI.Signal9.Server.Data;
using OSI.Signal9.Server.Identity;

namespace OSI.Signal9.API.Tenants;

/// <summary>
/// Tenant transfers (#122, ADR-0001), at the tenant they move: proposed by an Administrator above the tenant, then
/// accepted or declined by the receiving side, at the new parent. Accepting completes the transfer: the tenant and its
/// descendants move in the same transaction, so access changes at once (story 10). Both sides see the transfer.
/// </summary>
[ApiController]
[Route("api/v1/tenants/{tenantId:guid}/transfers")]
public sealed class TenantTransfersController(
    Signal9DbContext db, TenantTree tree, CallerAudit audit, TimeProvider time, DashboardAccess dashboard)
    : ControllerBase
{
    /// <summary>
    /// Lists the transfers on both sides of the tenant (story 9), newest first: outgoing ones, of the tenant or of
    /// tenants beneath it when proposed, and incoming ones, to it or to tenants beneath it. With
    /// <c>includeDescendants=false</c>, only transfers of the tenant itself and to it.
    /// </summary>
    [RequireScope(Scopes.Tenants.Read)]
    [HttpGet]
    [ProducesResponseType<PagedResult<TenantTransferDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PagedResult<TenantTransferDto>>> List(
        Guid tenantId,
        [FromQuery] PageQuery paging,
        [FromQuery] TenantTransferDirection? direction,
        [FromQuery] TenantTransferStatus? status,
        CancellationToken cancellationToken,
        [FromQuery] bool includeDescendants = true)
    {
        if (!await db.Tenants.AnyAsync(t => t.Id == tenantId, cancellationToken))
            return NotFound();

        var query = db.TenantTransfers.AsNoTracking();
        var outgoing = direction is null or TenantTransferDirection.Outgoing;
        var incoming = direction is null or TenantTransferDirection.Incoming;
        query = query.Where(t =>
            (outgoing && (t.TenantId == tenantId || (includeDescendants
                && db.TenantClosures.Any(c => c.AncestorId == tenantId && c.DescendantId == t.FromParentTenantId))))
            || (incoming && db.TenantClosures.Any(c => c.AncestorId == tenantId && c.DescendantId == t.ToParentTenantId
                && (includeDescendants || c.Depth == 0))));
        if (status is not null)
            query = query.Where(t => t.Status == status);

        var page = await query.OrderByDescending(t => t.ProposedAt).ThenByDescending(t => t.Id)
            .ToPagedResultAsync(paging, t => t, cancellationToken);
        var names = await NamesAsync(page.Items, cancellationToken);
        return new PagedResult<TenantTransferDto>([.. page.Items.Select(t => ToDto(t, names))],
            page.Page, page.PageSize, page.TotalCount);
    }

    [RequireScope(Scopes.Tenants.Read)]
    [HttpGet("{transferId:guid}", Name = nameof(GetTenantTransfer))]
    [OptimisticConcurrency]
    [ProducesResponseType<TenantTransferDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TenantTransferDto>> GetTenantTransfer(Guid tenantId, Guid transferId,
        CancellationToken cancellationToken)
    {
        var transfer = await FindAsync(tenantId, transferId, cancellationToken);
        if (transfer is null)
            return NotFound();
        var names = await NamesAsync([transfer], cancellationToken);
        return this.Conditional(ToDto(transfer, names), ETag(transfer, names));
    }

    /// <summary>
    /// Proposes transferring the tenant, with all its descendants, to a new parent tenant, which may be in another
    /// tenant tree (story 7). Only an Administrator above the tenant, or a Superadministrator, may propose it (story 21,
    /// 403). Nothing moves until the receiving side accepts. Refused (409) if the new parent is already its parent, is
    /// the tenant or one of its descendants (a cycle, story 6), would put the subtree beyond the maximum depth (story 5),
    /// or if a transfer of the tenant is already pending.
    /// </summary>
    [RequireScope(Scopes.Tenants.Transfer)]
    [RequireAncestorAdministrator]
    [HttpPost]
    [OptimisticConcurrency]
    [Consumes("application/json")]
    [ProducesResponseType<TenantTransferDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<TenantTransferDto>> Propose(Guid tenantId, ProposeTenantTransferRequest request,
        CancellationToken cancellationToken)
    {
        var tenant = await db.Tenants.AsNoTracking().SingleOrDefaultAsync(t => t.Id == tenantId, cancellationToken);
        if (tenant is null)
            return NotFound();
        if (this.Preconditions(current: null) is { } refused)
            return refused;

        var toParentId = request.ToParentTenantId!.Value;
        if (toParentId == tenant.ParentTenantId)
            return Problem(statusCode: StatusCodes.Status409Conflict, title: "Already its parent",
                detail: "The tenant is already beneath that parent tenant.");
        switch (await tree.CheckMoveAsync(tenantId, toParentId, cancellationToken))
        {
            case TenantTreeError.ParentNotFound:
                ModelState.AddModelError(nameof(request.ToParentTenantId), "Tenant does not exist.");
                return ValidationProblem(ModelState);
            case TenantTreeError.Cycle:
                return Cycle();
            case TenantTreeError.TooDeep:
                return TooDeep();
        }
        if (await db.TenantTransfers.AnyAsync(t => t.TenantId == tenantId && t.Status == TenantTransferStatus.Pending, cancellationToken))
            return AlreadyPending();

        var transfer = new TenantTransfer
        {
            Id = Guid.CreateVersion7(time.GetUtcNow()),
            TenantId = tenantId,
            FromParentTenantId = tenant.ParentTenantId,
            ToParentTenantId = toParentId,
            Status = TenantTransferStatus.Pending,
            ProposedAt = time.GetUtcNow().UtcDateTime,
        };
        db.TenantTransfers.Add(transfer);
        await RecordAsync(AuditActions.TenantTransferProposed, transfer, cancellationToken);
        // The unique index catches a transfer proposed at the same moment.
        if (!await db.TrySaveUniqueAsync(cancellationToken))
            return AlreadyPending();

        var names = await NamesAsync([transfer], cancellationToken);
        this.SetETag(ETag(transfer, names));
        return CreatedAtRoute(nameof(GetTenantTransfer), new { tenantId, transferId = transfer.Id }, ToDto(transfer, names));
    }

    /// <summary>
    /// Accepts a pending transfer, on the receiving side (story 8): the caller needs <c>tenants.transfer</c> at the new
    /// parent (403 otherwise), and, like the proposer, can't accept a transfer of their own assigned tenant (story 21,
    /// 403). Accepting completes it: the tenant and its descendants move under the new parent at once (story 10).
    /// Refused (409) if it's no longer pending, if the tree has changed so that the move would now create a cycle or go
    /// beyond the maximum depth, or if it would leave two devices for the same hardware in one root tenant tree; that
    /// refusal lists them in <c>duplicateDevices</c>.
    /// </summary>
    [RequireScope(Scopes.Tenants.Transfer)]
    [RequireAncestorAdministrator]
    [HttpPost("{transferId:guid}:accept")]
    [OptimisticConcurrency]
    [ProducesResponseType<TenantTransferDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<TenantTransferDto>> Accept(Guid tenantId, Guid transferId, CancellationToken cancellationToken)
    {
        var (read, refused) = await DecidableAsync(tenantId, transferId, Side.Receiving, cancellationToken);
        if (refused is not null)
            return refused;

        TenantTransfer? accepted = null;
        List<DuplicateDeviceDto> duplicates = [];
        var changed = false;
        var moved = await tree.MoveAsync(tenantId, read!.ToParentTenantId, async () =>
        {
            // Read again under the tree lock: a decision made since the first read wins.
            var transfer = await db.TenantTransfers.SingleOrDefaultAsync(t => t.Id == transferId, cancellationToken);
            if (transfer is null || transfer.Version != read.Version)
            {
                changed = true;
                return false;
            }
            duplicates = await DuplicateDevicesAsync(transfer, cancellationToken);
            if (duplicates.Count > 0)
                return false;

            transfer.Status = TenantTransferStatus.Accepted;
            transfer.DecidedAt = time.GetUtcNow().UtcDateTime;
            await RecordAsync(AuditActions.TenantTransferAccepted, transfer, cancellationToken);
            await RecordAsync(AuditActions.TenantTransferCompleted, transfer, cancellationToken);
            accepted = transfer;
            return true;
        }, cancellationToken);

        switch (moved)
        {
            case TenantTreeError.None:
                // Who hears the subtree follows at once, since events go by the live tree; regrouping only keeps a
                // connection from joining a tenant now beneath another of its own, and hearing its events twice.
                await dashboard.RecheckAllAsync(cancellationToken);
                var names = await NamesAsync([accepted!], cancellationToken);
                this.SetETag(ETag(accepted!, names));
                return ToDto(accepted!, names);
            case TenantTreeError.Refused when changed:
                throw new DbUpdateConcurrencyException("The transfer changed after it was read.");
            case TenantTreeError.Refused:
                return DuplicateDevices(duplicates);
            case TenantTreeError.Cycle:
                return Cycle();
            case TenantTreeError.TooDeep:
                return TooDeep();
            default:
                return NotFound();
        }
    }

    /// <summary>
    /// Declines a pending transfer, on the receiving side (story 8): the caller needs <c>tenants.transfer</c> at the new
    /// parent (403 otherwise). Nothing moves. A transfer that's no longer pending can't be declined (409).
    /// </summary>
    [RequireScope(Scopes.Tenants.Transfer)]
    [HttpPost("{transferId:guid}:decline")]
    [OptimisticConcurrency]
    [ProducesResponseType<TenantTransferDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public Task<ActionResult<TenantTransferDto>> Decline(Guid tenantId, Guid transferId, CancellationToken cancellationToken) =>
        DecideAsync(tenantId, transferId, Side.Receiving, TenantTransferStatus.Declined, AuditActions.TenantTransferDeclined,
            cancellationToken);

    /// <summary>
    /// Withdraws a pending transfer, on the proposing side: the caller needs what proposing it needs, an Administrator
    /// role above the tenant (403 otherwise). Nothing moves. A transfer that's no longer pending can't be cancelled (409).
    /// </summary>
    [RequireScope(Scopes.Tenants.Transfer)]
    [RequireAncestorAdministrator]
    [HttpPost("{transferId:guid}:cancel")]
    [OptimisticConcurrency]
    [ProducesResponseType<TenantTransferDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public Task<ActionResult<TenantTransferDto>> Cancel(Guid tenantId, Guid transferId, CancellationToken cancellationToken) =>
        DecideAsync(tenantId, transferId, Side.Proposing, TenantTransferStatus.Cancelled, AuditActions.TenantTransferCancelled,
            cancellationToken);

    private enum Side { Proposing, Receiving }

    private async Task<ActionResult<TenantTransferDto>> DecideAsync(Guid tenantId, Guid transferId, Side side,
        TenantTransferStatus to, string action, CancellationToken cancellationToken)
    {
        var (read, refused) = await DecidableAsync(tenantId, transferId, side, cancellationToken);
        if (refused is not null)
            return refused;

        var transfer = await db.TenantTransfers.SingleAsync(t => t.Id == transferId, cancellationToken);
        // Saving fails (412) if the transfer was decided since it was read.
        db.Entry(transfer).Property(t => t.Version).OriginalValue = read!.Version;
        transfer.Status = to;
        transfer.DecidedAt = time.GetUtcNow().UtcDateTime;
        await RecordAsync(action, transfer, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);

        var names = await NamesAsync([transfer], cancellationToken);
        this.SetETag(ETag(transfer, names));
        return ToDto(transfer, names);
    }

    /// <summary>
    /// The transfer, if the caller can see it, its preconditions hold, they're on <paramref name="side"/> of it with
    /// <c>tenants.transfer</c> (the tenant filter here is keyed on it), and it's still pending; otherwise the refusal.
    /// </summary>
    private async Task<(TenantTransfer? Transfer, ActionResult? Refused)> DecidableAsync(Guid tenantId, Guid transferId,
        Side side, CancellationToken cancellationToken)
    {
        var transfer = await FindAsync(tenantId, transferId, cancellationToken);
        if (transfer is null)
            return (null, NotFound());
        var names = await NamesAsync([transfer], cancellationToken);
        if (this.Preconditions(ETag(transfer, names)) is { } refused)
            return (null, refused);

        var sideTenant = side == Side.Receiving ? transfer.ToParentTenantId : transfer.TenantId;
        if (!await db.Tenants.AnyAsync(t => t.Id == sideTenant, cancellationToken))
            return (null, side == Side.Receiving
                ? Problem(statusCode: StatusCodes.Status403Forbidden, title: "Receiving side only",
                    detail: "Only someone who can transfer tenants at the new parent tenant can accept or decline the transfer.")
                : Problem(statusCode: StatusCodes.Status403Forbidden, title: "Proposing side only",
                    detail: "Only someone above the transferred tenant can cancel the transfer."));
        if (transfer.Status != TenantTransferStatus.Pending)
            return (null, Problem(statusCode: StatusCodes.Status409Conflict, title: "Transfer already decided",
                detail: $"The transfer is {transfer.Status}, so it can't change."));
        return (transfer, null);
    }

    private Task<TenantTransfer?> FindAsync(Guid tenantId, Guid transferId, CancellationToken cancellationToken) =>
        db.TenantTransfers.AsNoTracking().SingleOrDefaultAsync(t => t.TenantId == tenantId && t.Id == transferId, cancellationToken);

    /// <summary>
    /// Records <paramref name="action"/> at both sides of the transfer, the former parent and the new one, which, unlike
    /// the tenant itself, don't move with it: each side keeps the whole trail. Recorded once when one side is above the
    /// other, since an entry is read at its tenant and above. A root tenant's former side is deployment-level: only a
    /// Superadministrator is above a root.
    /// </summary>
    private async Task RecordAsync(string action, TenantTransfer transfer, CancellationToken cancellationToken)
    {
        var (from, to) = (transfer.FromParentTenantId, transfer.ToParentTenantId);
        Guid?[] sides = from is null ? [null, to]
            : await AboveAsync(from.Value, to) ? [to]
            : await AboveAsync(to, from.Value) ? [from]
            : [from, to];

        var detail = $"Tenant {transfer.TenantId} from parent {from?.ToString() ?? "none"} to {to}.";
        foreach (var at in sides)
            audit.Record(new AuditEntry
            {
                Action = action,
                TenantId = at,
                TargetType = "tenant-transfer",
                TargetId = transfer.Id,
                Detail = detail,
            });

        Task<bool> AboveAsync(Guid ancestor, Guid descendant) =>
            db.TenantClosures.AnyAsync(c => c.AncestorId == ancestor && c.DescendantId == descendant, cancellationToken);
    }

    /// <summary>
    /// The devices the transfer would bring into a root tenant tree that already has a device for the same hardware,
    /// paired with that device: one device per machine per root tree (maintainer decision on #63). A transfer within
    /// one root tree brings no machine into it, so it never has any.
    /// </summary>
    private async Task<List<DuplicateDeviceDto>> DuplicateDevicesAsync(TenantTransfer transfer, CancellationToken cancellationToken)
    {
        var fromRoot = await RootOfAsync(transfer.TenantId, cancellationToken);
        var toRoot = await RootOfAsync(transfer.ToParentTenantId, cancellationToken);
        if (fromRoot == toRoot)
            return [];

        var devices = db.Devices.AsNoTracking().IgnoreTenantScope(TenantScopeBypass.TenantTransferDuplicateDevices);
        var moving = await devices
            .Where(d => db.TenantClosures.Any(c => c.AncestorId == transfer.TenantId && c.DescendantId == d.TenantId))
            .Where(d => d.SmbiosUuid != null || (d.SerialNumber != null && d.Manufacturer != null))
            .Select(d => new { d.Id, d.SmbiosUuid, d.Manufacturer, d.SerialNumber })
            .ToListAsync(cancellationToken);
        if (moving.Count == 0)
            return [];

        var uuids = moving.Select(d => d.SmbiosUuid).OfType<Guid>().Distinct().ToList();
        var serials = moving.Where(d => d.Manufacturer != null).Select(d => d.SerialNumber).OfType<string>().Distinct().ToList();
        var receiving = await devices
            .Where(d => db.TenantClosures.Any(c => c.AncestorId == toRoot && c.DescendantId == d.TenantId))
            .Where(d => (d.SmbiosUuid != null && uuids.Contains(d.SmbiosUuid.Value))
                || (d.SerialNumber != null && d.Manufacturer != null && serials.Contains(d.SerialNumber)))
            .Select(d => new { d.Id, d.SmbiosUuid, d.Manufacturer, d.SerialNumber })
            .ToListAsync(cancellationToken);

        return
        [
            .. from m in moving
               from r in receiving
               where (m.SmbiosUuid != null && m.SmbiosUuid == r.SmbiosUuid)
                   || (m.SerialNumber != null && m.Manufacturer != null && m.SerialNumber == r.SerialNumber && m.Manufacturer == r.Manufacturer)
               orderby m.Id, r.Id
               select new DuplicateDeviceDto(m.Id, r.Id),
        ];
    }

    private Task<Guid> RootOfAsync(Guid tenantId, CancellationToken cancellationToken) =>
        db.TenantClosures.Where(c => c.DescendantId == tenantId)
            .OrderByDescending(c => c.Depth).Select(c => c.AncestorId).FirstAsync(cancellationToken);

    /// <summary>The names of the tenant each transfer moves and its new parent, which both sides of it see.</summary>
    private async Task<Dictionary<Guid, string>> NamesAsync(IReadOnlyCollection<TenantTransfer> transfers,
        CancellationToken cancellationToken)
    {
        var ids = transfers.SelectMany(t => new[] { t.TenantId, t.ToParentTenantId }).Distinct().ToList();
        return await db.Tenants.AsNoTracking().IgnoreTenantScope(TenantScopeBypass.TenantTransferParties)
            .Where(t => ids.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, t => t.Name, cancellationToken);
    }

    private static TenantTransferDto ToDto(TenantTransfer transfer, Dictionary<Guid, string> names) => new(
        transfer.Id,
        transfer.TenantId,
        $"/api/v1/tenants/{transfer.TenantId}/transfers/{transfer.Id}",
        names.GetValueOrDefault(transfer.TenantId),
        transfer.FromParentTenantId,
        transfer.ToParentTenantId,
        names.GetValueOrDefault(transfer.ToParentTenantId),
        transfer.Status,
        transfer.ProposedAt.AsUtcOffset(),
        transfer.DecidedAt.AsUtcOffset());

    private static EntityTagHeaderValue ETag(TenantTransfer transfer, Dictionary<Guid, string> names) =>
        ConditionalRequests.ETag(transfer.Version, names.GetValueOrDefault(transfer.TenantId),
            names.GetValueOrDefault(transfer.ToParentTenantId));

    private ObjectResult Cycle() => Problem(statusCode: StatusCodes.Status409Conflict, title: "Transfer would create a cycle",
        detail: "A tenant can't move beneath itself or one of its descendants.");

    private ObjectResult TooDeep() => Problem(statusCode: StatusCodes.Status409Conflict, title: "Tenant tree too deep",
        detail: "Beneath that parent, the tenant's subtree would go beyond the maximum depth of the tenant tree.");

    private ObjectResult AlreadyPending() => Problem(statusCode: StatusCodes.Status409Conflict, title: "Transfer already pending",
        detail: "The tenant already has a pending transfer. It must be accepted, declined or cancelled first.");

    private ObjectResult DuplicateDevices(List<DuplicateDeviceDto> duplicates)
    {
        var problem = ProblemDetailsFactory.CreateProblemDetails(HttpContext, StatusCodes.Status409Conflict,
            title: "Duplicate devices",
            detail: "The transfer would leave two devices for the same machine in one root tenant tree. Delete or transfer one of each pair first.");
        problem.Extensions["duplicateDevices"] = duplicates;
        return new ObjectResult(problem) { StatusCode = StatusCodes.Status409Conflict, ContentTypes = { "application/problem+json" } };
    }
}
