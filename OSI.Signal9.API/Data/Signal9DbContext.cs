using Microsoft.EntityFrameworkCore;

namespace OSI.Signal9.API.Data;

public sealed class Signal9DbContext(DbContextOptions<Signal9DbContext> options) : DbContext(options)
{
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<Agent> Agents => Set<Agent>();
    public DbSet<AgentCommand> AgentCommands => Set<AgentCommand>();
    public DbSet<TelemetrySample> TelemetrySamples => Set<TelemetrySample>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Tenant>(tenant =>
        {
            tenant.Property(t => t.Code).HasMaxLength(32);
            tenant.HasIndex(t => t.Code).IsUnique();
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

        modelBuilder.Entity<Agent>(agent =>
        {
            agent.Property(a => a.MachineName).HasMaxLength(255);
            agent.HasIndex(a => new { a.TenantId, a.MachineName }).IsUnique();
            agent.HasIndex(a => a.LastSeenAt);
            agent.Property(a => a.Domain).HasMaxLength(255);
            agent.Property(a => a.OperatingSystem).HasMaxLength(100);
            agent.Property(a => a.OsVersion).HasMaxLength(100);
            agent.Property(a => a.Architecture).HasMaxLength(20);
            agent.Property(a => a.ProcessorName).HasMaxLength(255);
            agent.Property(a => a.IpAddress).HasMaxLength(45);
            agent.Property(a => a.MacAddress).HasMaxLength(17);
            agent.Property(a => a.AgentVersion).HasMaxLength(50);
            agent.Property(a => a.GroupName).HasMaxLength(100);
            agent.HasOne(a => a.Tenant)
                .WithMany(t => t.Agents)
                .HasForeignKey(a => a.TenantId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<AgentCommand>(command =>
        {
            command.Property(c => c.Type).HasConversion<string>().HasMaxLength(50);
            command.Property(c => c.Priority).HasConversion<string>().HasMaxLength(20);
            command.Property(c => c.Status).HasConversion<string>().HasMaxLength(20);
            command.Property(c => c.Parameters).HasMaxLength(4000);
            command.Property(c => c.Output).HasMaxLength(16_000);
            command.Property(c => c.Error).HasMaxLength(4000);
            command.HasIndex(c => new { c.AgentId, c.Status });
            command.HasOne<Agent>()
                .WithMany()
                .HasForeignKey(c => c.AgentId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TelemetrySample>(sample =>
        {
            sample.HasIndex(s => new { s.AgentId, s.CollectedAt });
            sample.OwnsMany(s => s.Disks, disks => disks.ToJson());
            sample.HasOne<Agent>()
                .WithMany()
                .HasForeignKey(s => s.AgentId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
