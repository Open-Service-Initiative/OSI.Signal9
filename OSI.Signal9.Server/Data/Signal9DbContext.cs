using System.Reflection;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using OSI.Signal9.Server.Identity;
using OSI.Signal9.Server.Keys;

namespace OSI.Signal9.Server.Data;

public sealed class Signal9DbContext(DbContextOptions<Signal9DbContext> options, IAssignedTenants assignedTenants)
    : DbContext(options), IDataProtectionKeyContext
{
    /// <summary>
    /// The caller's assigned tenants, read by <see cref="TenantScope"/> each time a query runs. A context member, so EF
    /// sends it as a query parameter instead of freezing the first caller's tenants into the cached model.
    /// </summary>
    public Guid[] AssignedTenantIds => [.. assignedTenants.TenantIds ?? []];

    /// <summary>Whether the caller sees every tenant tree, read by <see cref="TenantScope"/> like <see cref="AssignedTenantIds"/>.</summary>
    public bool SeesEveryTenant => assignedTenants.IsSuperadministrator;

    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<TenantClosure> TenantClosures => Set<TenantClosure>();
    public DbSet<TenantTransfer> TenantTransfers => Set<TenantTransfer>();
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<Agent> Agents => Set<Agent>();
    public DbSet<PendingIdentification> PendingIdentifications => Set<PendingIdentification>();
    public DbSet<Command> Commands => Set<Command>();
    public DbSet<TelemetrySample> TelemetrySamples => Set<TelemetrySample>();
    public DbSet<StoredKey> StoredKeys => Set<StoredKey>();
    public DbSet<DashboardSession> DashboardSessions => Set<DashboardSession>();
    public DbSet<User> Users => Set<User>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<RoleAssignment> RoleAssignments => Set<RoleAssignment>();
    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();
    public DbSet<EnrollmentToken> EnrollmentTokens => Set<EnrollmentToken>();
    public DbSet<ApiApplication> ApiApplications => Set<ApiApplication>();
    public DbSet<SignInSession> SignInSessions => Set<SignInSession>();
    public DbSet<EndedEntraSession> EndedEntraSessions => Set<EndedEntraSession>();
    public DbSet<EntraTokenCacheEntry> EntraTokenCache => Set<EntraTokenCacheEntry>();

    /// <summary>The ASP.NET Core Data Protection key ring, each key wrapped by the key-encryption key (ADR-0008).</summary>
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // OpenIddict's applications, authorizations, scopes and tokens, shared by auth and api (ADR-0003).
        modelBuilder.UseOpenIddict();

        // Every resource's Version is PostgreSQL's xmin, so any write to its row changes its ETag, and EF refuses an
        // update or delete whose row changed since it was read (DbUpdateConcurrencyException).
        modelBuilder.Entity<Tenant>().Property(t => t.Version).IsRowVersion();
        modelBuilder.Entity<TenantTransfer>().Property(t => t.Version).IsRowVersion();
        modelBuilder.Entity<Device>().Property(d => d.Version).IsRowVersion();
        modelBuilder.Entity<Agent>().Property(a => a.Version).IsRowVersion();
        modelBuilder.Entity<Command>().Property(c => c.Version).IsRowVersion();
        modelBuilder.Entity<TelemetrySample>().Property(s => s.Version).IsRowVersion();
        modelBuilder.Entity<User>().Property(u => u.Version).IsRowVersion();
        modelBuilder.Entity<RoleAssignment>().Property(a => a.Version).IsRowVersion();
        modelBuilder.Entity<EnrollmentToken>().Property(t => t.Version).IsRowVersion();
        modelBuilder.Entity<ApiApplication>().Property(a => a.Version).IsRowVersion();

        modelBuilder.Entity<Tenant>(tenant =>
        {
            tenant.Property(t => t.Name).HasMaxLength(200);
            tenant.Property(t => t.Description).HasMaxLength(1000);
            tenant.Property(t => t.ContactEmail).HasMaxLength(255);
            tenant.Property(t => t.ContactPhone).HasMaxLength(32);
            tenant.Property(t => t.Plan).HasConversion<string>().HasMaxLength(20);
            tenant.Property(t => t.Status).HasConversion<string>().HasMaxLength(20);
            tenant.HasOne(t => t.ParentTenant)
                .WithMany()
                .HasForeignKey(t => t.ParentTenantId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<TenantClosure>(closure =>
        {
            closure.HasKey(c => new { c.AncestorId, c.DescendantId });
            closure.HasIndex(c => c.DescendantId);
            closure.HasOne<Tenant>().WithMany().HasForeignKey(c => c.AncestorId).OnDelete(DeleteBehavior.Cascade);
            closure.HasOne<Tenant>().WithMany().HasForeignKey(c => c.DescendantId).OnDelete(DeleteBehavior.Cascade);
        });

        // A transfer goes with the tenant it moves or the parent it would move to; the parent it came from may go first.
        modelBuilder.Entity<TenantTransfer>(transfer =>
        {
            transfer.Property(t => t.Status).HasConversion<string>().HasMaxLength(20);
            transfer.HasOne<Tenant>().WithMany().HasForeignKey(t => t.TenantId).OnDelete(DeleteBehavior.Cascade);
            transfer.HasOne<Tenant>().WithMany().HasForeignKey(t => t.FromParentTenantId).OnDelete(DeleteBehavior.SetNull);
            transfer.HasOne<Tenant>().WithMany().HasForeignKey(t => t.ToParentTenantId).OnDelete(DeleteBehavior.Cascade);
            // One pending transfer per tenant at a time.
            transfer.HasIndex(t => t.TenantId).IsUnique().HasFilter("\"Status\" = 'Pending'");
            transfer.HasIndex(t => t.FromParentTenantId);
            transfer.HasIndex(t => t.ToParentTenantId);
        });

        modelBuilder.Entity<Device>(device =>
        {
            device.Property(d => d.MachineName).HasMaxLength(255);
            device.HasIndex(d => new { d.TenantId, d.MachineName });
            device.Property(d => d.Domain).HasMaxLength(255);
            device.Property(d => d.OperatingSystem).HasMaxLength(100);
            device.Property(d => d.OsVersion).HasMaxLength(100);
            device.Property(d => d.Architecture).HasMaxLength(20);
            device.Property(d => d.ProcessorName).HasMaxLength(255);
            device.Property(d => d.IpAddress).HasMaxLength(45);
            device.Property(d => d.MacAddress).HasMaxLength(17);
            device.Property(d => d.GroupName).HasMaxLength(100);
            device.Property(d => d.Manufacturer).HasMaxLength(255);
            device.Property(d => d.SerialNumber).HasMaxLength(255);
            // Hardware identifiers aren't unique: a person may keep two devices that share them ("Keep both").
            device.HasIndex(d => d.SmbiosUuid);
            device.HasIndex(d => new { d.SerialNumber, d.Manufacturer });
            device.HasOne(d => d.Tenant)
                .WithMany(t => t.Devices)
                .HasForeignKey(d => d.TenantId)
                .OnDelete(DeleteBehavior.Restrict);
            // A single pointer is what makes "at most one active agent" hold by construction.
            device.HasOne(d => d.ActiveAgent)
                .WithOne()
                .HasForeignKey<Device>(d => d.ActiveAgentId)
                .OnDelete(DeleteBehavior.ClientSetNull);
        });

        modelBuilder.Entity<Agent>(agent =>
        {
            agent.Property(a => a.AgentVersion).HasMaxLength(50);
            agent.HasOne(a => a.Device)
                .WithMany()
                .HasForeignKey(a => a.DeviceId)
                .OnDelete(DeleteBehavior.Cascade);
            agent.HasOne<EnrollmentToken>()
                .WithMany()
                .HasForeignKey(a => a.EnrollmentTokenId)
                .OnDelete(DeleteBehavior.SetNull);
            agent.HasIndex(a => a.EnrolledAt);
            agent.HasOne(a => a.PendingIdentification)
                .WithOne()
                .HasForeignKey<PendingIdentification>(p => p.AgentId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PendingIdentification>(pending =>
        {
            pending.HasKey(p => p.AgentId);
            pending.HasOne<Tenant>()
                .WithMany()
                .HasForeignKey(p => p.TenantId)
                .OnDelete(DeleteBehavior.Restrict);
            pending.HasIndex(p => p.TenantId);
            pending.Property(p => p.MachineName).HasMaxLength(255);
            pending.Property(p => p.Domain).HasMaxLength(255);
            pending.Property(p => p.OperatingSystem).HasMaxLength(100);
            pending.Property(p => p.OsVersion).HasMaxLength(100);
            pending.Property(p => p.Architecture).HasMaxLength(20);
            pending.Property(p => p.ProcessorName).HasMaxLength(255);
            pending.Property(p => p.IpAddress).HasMaxLength(45);
            pending.Property(p => p.MacAddress).HasMaxLength(17);
            pending.Property(p => p.Manufacturer).HasMaxLength(255);
            pending.Property(p => p.SerialNumber).HasMaxLength(255);
        });

        modelBuilder.Entity<Command>(command =>
        {
            command.Property(c => c.Type).HasConversion<string>().HasMaxLength(50);
            command.Property(c => c.Priority).HasConversion<string>().HasMaxLength(20);
            command.Property(c => c.Status).HasConversion<string>().HasMaxLength(20);
            command.Property(c => c.Parameters).HasMaxLength(4000);
            command.Property(c => c.Output).HasMaxLength(16_000);
            command.Property(c => c.Error).HasMaxLength(4000);
            command.HasIndex(c => new { c.DeviceId, c.Status });
            command.HasOne<Device>()
                .WithMany()
                .HasForeignKey(c => c.DeviceId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<StoredKey>(key =>
        {
            key.Property(k => k.Use).HasConversion<string>().HasMaxLength(20);
            key.Property(k => k.KekId).HasMaxLength(500);
            key.HasIndex(k => new { k.Use, k.ActivatesAt });
        });

        modelBuilder.Entity<DashboardSession>(session =>
        {
            session.Property(s => s.Subject).HasMaxLength(200);
            // Sessions are looked up by person for sign-out everywhere (#119).
            session.HasIndex(s => s.Subject);
        });

        modelBuilder.Entity<SignInSession>(session =>
        {
            session.HasKey(s => s.AuthorizationId);
            session.Property(s => s.AuthorizationId).HasMaxLength(100);
            session.Property(s => s.Subject).HasMaxLength(200);
            session.Property(s => s.ClientId).HasMaxLength(100);
            session.Property(s => s.EntraSessionId).HasMaxLength(200);
            session.Property(s => s.EntraAccountId).HasMaxLength(200);
            // Looked up by person and by Entra session (front-channel logout, live-connection teardown), and by account
            // for the hourly Entra check.
            session.HasIndex(s => s.Subject);
            session.HasIndex(s => new { s.EntraTenantId, s.EntraSessionId });
            session.HasIndex(s => new { s.EntraAccountId, s.EntraCheckedAt });
        });

        modelBuilder.Entity<EndedEntraSession>(ended =>
        {
            ended.HasKey(e => new { e.EntraTenantId, e.EntraSessionId });
            ended.Property(e => e.EntraSessionId).HasMaxLength(200);
        });

        modelBuilder.Entity<EntraTokenCacheEntry>(entry =>
        {
            entry.HasKey(e => e.Key);
            entry.Property(e => e.Key).HasMaxLength(300);
        });

        modelBuilder.Entity<TelemetrySample>(sample =>
        {
            sample.HasIndex(s => new { s.DeviceId, s.CollectedAt });
            sample.OwnsMany(s => s.Disks, disks => disks.ToJson());
            sample.HasOne<Device>()
                .WithMany()
                .HasForeignKey(s => s.DeviceId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // The owning tenant governs its users: deleting it deletes them, with all their role assignments (#63).
        modelBuilder.Entity<User>(user =>
        {
            user.Property(u => u.DisplayName).HasMaxLength(200);
            // One user per Entra identity (story 41).
            user.HasIndex(u => new { u.EntraTenantId, u.EntraObjectId }).IsUnique();
            user.HasOne<Tenant>().WithMany().HasForeignKey(u => u.TenantId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Role>(role =>
        {
            role.Property(r => r.Name).HasMaxLength(100);
            role.HasIndex(r => r.Name).IsUnique();
            role.Property(r => r.Description).HasMaxLength(500);
            role.HasData(BuiltInRoles.All.Select(r => new Role
            {
                Id = r.Id,
                Name = r.Name,
                Description = r.Description,
                BuiltIn = true,
                Scopes = [.. r.Scopes],
            }));
        });

        modelBuilder.Entity<RoleAssignment>(assignment =>
        {
            assignment.HasIndex(a => new { a.UserId, a.TenantId, a.RoleId }).IsUnique();
            assignment.HasIndex(a => a.TenantId);
            assignment.HasOne<Tenant>().WithMany().HasForeignKey(a => a.TenantId).OnDelete(DeleteBehavior.Cascade);
            assignment.HasOne(a => a.User).WithMany().HasForeignKey(a => a.UserId).OnDelete(DeleteBehavior.Cascade);
            assignment.HasOne(a => a.Role).WithMany().HasForeignKey(a => a.RoleId).OnDelete(DeleteBehavior.Restrict);
        });

        // Deleting a tenant revokes its enrollment tokens by deleting them (#63, Tenant deletion).
        modelBuilder.Entity<EnrollmentToken>(token =>
        {
            token.Property(t => t.Name).HasMaxLength(200);
            token.Property(t => t.SecretHash).HasMaxLength(32);
            token.HasIndex(t => t.SecretHash).IsUnique();
            token.HasIndex(t => t.TenantId);
            token.HasOne<Tenant>().WithMany().HasForeignKey(t => t.TenantId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ApiApplication>(application =>
        {
            application.Property(a => a.ClientId).HasMaxLength(100);
            application.HasIndex(a => a.ClientId).IsUnique();
            application.HasIndex(a => a.TenantId);
            application.Property(a => a.DisplayName).HasMaxLength(200);
            application.Property(a => a.TokenEndpointAuthMethod).HasMaxLength(50);
            application.Property(a => a.Kind).HasMaxLength(20);
            application.HasOne<Tenant>().WithMany().HasForeignKey(a => a.TenantId).OnDelete(DeleteBehavior.Cascade);
            application.HasOne(a => a.Role).WithMany().HasForeignKey(a => a.RoleId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<AuditEntry>(entry =>
        {
            entry.Property(e => e.Sequence).UseIdentityAlwaysColumn();
            entry.HasIndex(e => e.Sequence).IsUnique();
            entry.Property(e => e.Action).HasMaxLength(100);
            entry.Property(e => e.Outcome).HasConversion<string>().HasMaxLength(20);
            entry.Property(e => e.ActorKind).HasConversion<string>().HasMaxLength(20);
            entry.Property(e => e.ActorSubject).HasMaxLength(200);
            entry.Property(e => e.ActorClientId).HasMaxLength(200);
            entry.Property(e => e.TargetType).HasMaxLength(50);
            entry.Property(e => e.Detail).HasMaxLength(500);
            entry.Property(e => e.ClientIp).HasMaxLength(45);
            // No foreign keys: entries outlive the tenants, devices and users they mention (#63, Tenant deletion).
            entry.HasIndex(e => new { e.TenantId, e.Sequence });
        });

        ApplyTenantScope(modelBuilder);
    }

    // TenantScope keeps every row for a Superadministrator, and otherwise a row when its owning tenant's closure rows
    // include one of the caller's assigned tenants as an ancestor. The closure table itself is never filtered, so the
    // filter can't recurse.
    private void ApplyTenantScope(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Tenant>().HasQueryFilter(TenantScope.Filter, t => SeesEveryTenant ||
            TenantClosures.Any(c => c.DescendantId == t.Id && AssignedTenantIds.Contains(c.AncestorId)));

        // An entry is read at its tenant and above; a deployment-level entry (no tenant) only by a Superadministrator.
        modelBuilder.Entity<AuditEntry>().HasQueryFilter(TenantScope.Filter, e => SeesEveryTenant ||
            (e.TenantId != null && TenantClosures.Any(c => c.DescendantId == e.TenantId && AssignedTenantIds.Contains(c.AncestorId))));

        // A transfer is visible to both sides: from the tenant it moves, its parent when proposed, or its new parent.
        modelBuilder.Entity<TenantTransfer>().HasQueryFilter(TenantScope.Filter, t => SeesEveryTenant ||
            TenantClosures.Any(c => (c.DescendantId == t.TenantId || c.DescendantId == t.FromParentTenantId
                || c.DescendantId == t.ToParentTenantId) && AssignedTenantIds.Contains(c.AncestorId)));

        // An agent is visible with its device, or, while it awaits identification and has none, with its pending
        // identification, which its enrollment token's tenant owns.
        modelBuilder.Entity<Agent>().HasQueryFilter(TenantScope.Filter, a =>
            Devices.Any(d => d.Id == a.DeviceId) || PendingIdentifications.Any(p => p.AgentId == a.Id));

        foreach (var type in modelBuilder.Model.GetEntityTypes().Where(t => t.BaseType is null && !t.IsOwned()).ToList())
        {
            if (type.ClrType.IsAssignableTo(typeof(ITenantOwned)))
                ScopeMethod(nameof(ScopeTenantOwned), type.ClrType).Invoke(this, [modelBuilder]);
            else if (type.ClrType.IsAssignableTo(typeof(IDeviceOwned)))
                ScopeMethod(nameof(ScopeDeviceOwned), type.ClrType).Invoke(this, [modelBuilder]);
        }
    }

    private static MethodInfo ScopeMethod(string name, Type entity) =>
        typeof(Signal9DbContext).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.MakeGenericMethod(entity);

    // The filters must reference this context's members, never locals: EF parameterizes members per query, but would
    // freeze a captured value into the cached model.
    private void ScopeTenantOwned<T>(ModelBuilder modelBuilder) where T : class, ITenantOwned =>
        modelBuilder.Entity<T>().HasQueryFilter(TenantScope.Filter, e => SeesEveryTenant ||
            TenantClosures.Any(c =>
                c.DescendantId == EF.Property<Guid>(e, nameof(ITenantOwned.TenantId)) && AssignedTenantIds.Contains(c.AncestorId)));

    // A device-owned row is visible when its device is: the Devices query below carries the device's own TenantScope,
    // so it follows the device to whichever tenant owns it now.
    private void ScopeDeviceOwned<T>(ModelBuilder modelBuilder) where T : class, IDeviceOwned =>
        modelBuilder.Entity<T>().HasQueryFilter(TenantScope.Filter, e =>
            Devices.Any(d => d.Id == EF.Property<Guid>(e, nameof(IDeviceOwned.DeviceId))));
}
