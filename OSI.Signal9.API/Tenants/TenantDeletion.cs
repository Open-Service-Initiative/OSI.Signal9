using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;
using OSI.Signal9.API.Audit;
using OSI.Signal9.API.Hubs;
using OSI.Signal9.API.Infrastructure;
using OSI.Signal9.API.Roles;
using OSI.Signal9.Contracts.Audit;
using OSI.Signal9.Contracts.Tenants;
using OSI.Signal9.Server.Data;
using OSI.Signal9.Server.Identity;

namespace OSI.Signal9.API.Tenants;

/// <summary>The outcome of <see cref="TenantDeletion.DeleteAsync"/>.</summary>
public enum TenantDeletionResult
{
    Deleted,
    NotFound,

    /// <summary>Child tenants or live devices remain (<see cref="TenantDeletion.Remaining"/> says which).</summary>
    NotEmpty,

    /// <summary>It would leave a tenant with no Administrator at it or above it.</summary>
    WouldLeaveWithoutAdministrator,
}

/// <summary>
/// Deletes a tenant and everything it owns (#134, stories 11-13). The owning tenant governs its users: they're deleted with
/// all their role assignments, wherever those are, and users owned elsewhere lose only their assignments at the tenant.
/// Each removal is audited where it was, with the detail <see cref="Detail"/>; the tenant's own trail stays readable at
/// the ancestors it had (<see cref="DeletedTenant"/>). Everything runs in the tree's transaction, under its lock, so the
/// checks can't race a transfer or a role assignment change.
/// </summary>
public sealed class TenantDeletion(
    Signal9DbContext db, TenantTree tree, CallerAudit audit, IAssignedTenants caller,
    IOpenIddictApplicationManager applications, IOpenIddictAuthorizationManager authorizations, SignInSessions sessions,
    HubConnections<AgentHub> hubConnections)
{
    /// <summary>The detail of every audit entry the cascade records.</summary>
    public const string Detail = "tenant-deleted";

    private sealed record Assignment(RoleAssignment Row, Guid OwnerTenantId, string Subject);

    private sealed record Plan(
        List<Guid> ChildTenantIds,
        List<Guid> DeviceIds,
        List<Assignment> Assignments,
        List<string> OwnedSubjects,
        List<Guid> TenantsLeftWithoutAdministrator);

    private Plan? _plan;

    /// <summary>What blocked the last <see cref="DeleteAsync"/>, or would be removed by it.</summary>
    public TenantDeletionPreviewDto? Remaining { get; private set; }

    /// <summary>What deleting <paramref name="tenantId"/> would do now, without taking the tree lock.</summary>
    public async Task<TenantDeletionPreviewDto> PreviewAsync(Guid tenantId, CancellationToken cancellationToken) =>
        ToDto(tenantId, await PlanAsync(tenantId, cancellationToken));

    /// <summary>
    /// Deletes <paramref name="tenantId"/>, last read at <paramref name="version"/>, once nothing blocks it, then ends
    /// the sessions of everyone it leaves with no role assignment.
    /// </summary>
    public async Task<TenantDeletionResult> DeleteAsync(Guid tenantId, uint version, SessionEnd end,
        CancellationToken cancellationToken)
    {
        var result = TenantDeletionResult.Deleted;
        List<Guid> agentIds = [];
        var error = await tree.RemoveAsync(tenantId, version, async () =>
        {
            var plan = _plan = await PlanAsync(tenantId, cancellationToken);
            Remaining = ToDto(tenantId, plan);
            if (plan.ChildTenantIds.Count > 0 || plan.DeviceIds.Count > 0)
                result = TenantDeletionResult.NotEmpty;
            else if (plan.TenantsLeftWithoutAdministrator.Count > 0 && !caller.IsSuperadministrator)
                result = TenantDeletionResult.WouldLeaveWithoutAdministrator;
            else
            {
                result = TenantDeletionResult.Deleted;
                agentIds = await CascadeAsync(tenantId, plan, cancellationToken);
            }
            return result == TenantDeletionResult.Deleted;
        }, cancellationToken);

        switch (error)
        {
            case TenantTreeError.TenantNotFound:
                return TenantDeletionResult.NotFound;
            case TenantTreeError.HasChildTenants:
                Remaining = await PreviewAsync(tenantId, cancellationToken);
                return TenantDeletionResult.NotEmpty;
            case TenantTreeError.Refused:
                return result;
        }

        foreach (var agentId in agentIds)
            hubConnections.Close(agentId);
        await EndSessionsAsync(tenantId, _plan!, end, cancellationToken);
        return TenantDeletionResult.Deleted;
    }

    private async Task<Plan> PlanAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var children = await db.TenantClosures.AsNoTracking()
            .Where(c => c.AncestorId == tenantId && c.Depth == 1)
            .Select(c => c.DescendantId).ToListAsync(cancellationToken);
        var devices = await db.Devices.AsNoTracking().IgnoreTenantScope(TenantScopeBypass.TenantDeletion)
            .Where(d => d.TenantId == tenantId).OrderBy(d => d.Id)
            .Select(d => d.Id).ToListAsync(cancellationToken);

        // Assignments at the tenant, and every assignment of the users it owns.
        var assignments = await db.RoleAssignments.AsNoTracking().IgnoreTenantScope(TenantScopeBypass.TenantDeletion)
            .Where(a => a.TenantId == tenantId || a.User!.TenantId == tenantId)
            .OrderBy(a => a.CreatedAt).ThenBy(a => a.Id)
            .Select(a => new { Row = a, OwnerTenantId = a.User!.TenantId, a.User.EntraTenantId, a.User.EntraObjectId })
            .ToListAsync(cancellationToken);
        var planned = assignments
            .Select(a => new Assignment(a.Row, a.OwnerTenantId, PersonSubject.Format(a.EntraTenantId, a.EntraObjectId)))
            .ToList();

        var owned = await db.Users.AsNoTracking().IgnoreTenantScope(TenantScopeBypass.TenantDeletion)
            .Where(u => u.TenantId == tenantId).Select(u => new { u.EntraTenantId, u.EntraObjectId })
            .ToListAsync(cancellationToken);

        return new Plan([.. children.Order()], devices, planned,
            [.. owned.Select(u => PersonSubject.Format(u.EntraTenantId, u.EntraObjectId))],
            await TenantsLeftWithoutAdministratorAsync(tenantId, planned, cancellationToken));
    }

    // Tenants outside the deleted one where an Administrator assignment goes with its user, and no other Administrator
    // assignment remains at the tenant or above it.
    private async Task<List<Guid>> TenantsLeftWithoutAdministratorAsync(Guid tenantId, List<Assignment> removed,
        CancellationToken cancellationToken)
    {
        var subtree = await db.TenantClosures.AsNoTracking().Where(c => c.AncestorId == tenantId)
            .Select(c => c.DescendantId).ToListAsync(cancellationToken);
        Guid[] removedIds = [.. removed.Select(a => a.Row.Id)];
        List<Guid> left = [];
        foreach (var at in removed.Where(a => a.Row.RoleId == BuiltInRoles.Administrator.Id && !subtree.Contains(a.Row.TenantId))
                     .Select(a => a.Row.TenantId).Distinct())
        {
            if (!await AdministratorCoverage.RemainsAsync(db, at, removedIds, cancellationToken))
                left.Add(at);
        }
        return [.. left.Order()];
    }

    // Removes what the tenant owns, auditing each removal where it was, and saves. Returns the agents removed.
    private async Task<List<Guid>> CascadeAsync(Guid tenantId, Plan plan, CancellationToken cancellationToken)
    {
        var ownedUsers = await db.Users.IgnoreTenantScope(TenantScopeBypass.TenantDeletion)
            .Where(u => u.TenantId == tenantId).OrderBy(u => u.Id).ToListAsync(cancellationToken);
        var ownedUserIds = ownedUsers.Select(u => u.Id).ToList();

        // Enrollment tokens and invitations still usable are revoked by deleting them, with the tenant or their user.
        var tokens = await db.EnrollmentTokens.AsNoTracking().IgnoreTenantScope(TenantScopeBypass.TenantDeletion)
            .Where(t => t.TenantId == tenantId && t.RevokedAt == null).OrderBy(t => t.Id).ToListAsync(cancellationToken);
        foreach (var token in tokens)
            Record(AuditActions.EnrollmentTokenRevoked, token.TenantId, "enrollment-token", token.Id);
        var invitations = await db.Invitations.AsNoTracking().IgnoreTenantScope(TenantScopeBypass.TenantDeletion)
            .Where(i => (i.TenantId == tenantId || (i.UserId != null && ownedUserIds.Contains(i.UserId.Value)))
                && i.RedeemedAt == null && i.RevokedAt == null)
            .OrderBy(i => i.Id).ToListAsync(cancellationToken);
        foreach (var invitation in invitations)
            Record(AuditActions.InvitationRevoked, invitation.TenantId, "invitation", invitation.Id, invitation.UserId,
                invitation.RoleId);

        // Agents still awaiting identification here have no device to block the deletion.
        var agents = await db.PendingIdentifications.AsNoTracking().IgnoreTenantScope(TenantScopeBypass.TenantDeletion)
            .Where(p => p.TenantId == tenantId).OrderBy(p => p.AgentId).Select(p => p.AgentId).ToListAsync(cancellationToken);
        foreach (var agentId in agents)
            Record(AuditActions.AgentRevoked, tenantId, "agent", agentId);
        await db.Agents.IgnoreTenantScope(TenantScopeBypass.TenantDeletion).Where(a => agents.Contains(a.Id))
            .ExecuteDeleteAsync(cancellationToken);

        foreach (var assignment in plan.Assignments.Select(a => a.Row))
        {
            Record(AuditActions.RoleAssignmentDeleted, assignment.TenantId, "role-assignment", assignment.Id,
                assignment.UserId, assignment.RoleId);
            db.RoleAssignments.Attach(assignment);
            db.RoleAssignments.Remove(assignment);
        }
        foreach (var user in ownedUsers)
        {
            Record(AuditActions.UserDeleted, tenantId, "user", user.Id, user.Id);
            db.Users.Remove(user);
        }

        // Each API application with its OAuth client, its authorizations and tokens, as DELETE api-applications does.
        var registered = await db.ApiApplications.IgnoreTenantScope(TenantScopeBypass.TenantDeletion)
            .Where(a => a.TenantId == tenantId).OrderBy(a => a.Id).ToListAsync(cancellationToken);
        foreach (var application in registered)
        {
            Record(AuditActions.ApiApplicationDeleted, tenantId, "api-application", application.Id, roleId: application.RoleId);
            db.ApiApplications.Remove(application);
        }
        await db.SaveChangesAsync(cancellationToken);
        foreach (var clientId in registered.Select(a => a.ClientId).Concat(agents.Select(a => a.ToString())))
        {
            if (await applications.FindByClientIdAsync(clientId, cancellationToken) is { } client)
                await applications.DeleteAsync(client, cancellationToken);
        }
        return agents;
    }

    // People deleted with the tenant lose every session and token at once; anyone else who's left with no role
    // assignment loses their sessions as when their last one is revoked (story 27).
    private async Task EndSessionsAsync(Guid tenantId, Plan plan, SessionEnd end, CancellationToken cancellationToken)
    {
        foreach (var subject in plan.OwnedSubjects)
        {
            await sessions.EndForPersonAsync(subject, end with { Detail = "User deleted with their tenant" }, cancellationToken);
            await authorizations.RevokeBySubjectAsync(subject, cancellationToken);
        }
        foreach (var subject in plan.Assignments.Where(a => a.OwnerTenantId != tenantId).Select(a => a.Subject).Distinct())
        {
            if (!await sessions.HoldsRoleAssignmentAsync(subject, cancellationToken))
                await sessions.EndForPersonAsync(subject, end with { Detail = "No role assignment left" }, cancellationToken);
        }
    }

    private void Record(string action, Guid at, string targetType, Guid targetId, Guid? userId = null, Guid? roleId = null) =>
        audit.Record(new AuditEntry
        {
            Action = action,
            TenantId = at,
            TargetType = targetType,
            TargetId = targetId,
            UserId = userId,
            RoleId = roleId,
            Detail = Detail,
        });

    private static TenantDeletionPreviewDto ToDto(Guid tenantId, Plan plan) => new(
        tenantId,
        plan.ChildTenantIds,
        plan.DeviceIds,
        [.. plan.Assignments.Where(a => a.OwnerTenantId == tenantId && a.Row.TenantId != tenantId).Select(a => a.Row.ToDto())],
        plan.TenantsLeftWithoutAdministrator);
}
