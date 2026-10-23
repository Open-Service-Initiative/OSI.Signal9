using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using OSI.Signal9.Server.Keys;

namespace OSI.Signal9.Server.Data;

public sealed class Signal9DbContext(DbContextOptions<Signal9DbContext> options) : DbContext(options), IDataProtectionKeyContext
{
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<TenantClosure> TenantClosures => Set<TenantClosure>();
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<Agent> Agents => Set<Agent>();
    public DbSet<Command> Commands => Set<Command>();
    public DbSet<TelemetrySample> TelemetrySamples => Set<TelemetrySample>();
    public DbSet<StoredKey> StoredKeys => Set<StoredKey>();

    /// <summary>The ASP.NET Core Data Protection key ring, each key wrapped by the key-encryption key (ADR-0008).</summary>
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // OpenIddict's applications, authorizations, scopes and tokens, shared by auth and api (ADR-0003).
        modelBuilder.UseOpenIddict();

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

        modelBuilder.Entity<TelemetrySample>(sample =>
        {
            sample.HasIndex(s => new { s.DeviceId, s.CollectedAt });
            sample.OwnsMany(s => s.Disks, disks => disks.ToJson());
            sample.HasOne<Device>()
                .WithMany()
                .HasForeignKey(s => s.DeviceId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
