using Microsoft.EntityFrameworkCore;
using Signal9.Shared.Models;

namespace Signal9.Shared.Data;

/// <summary>
/// Clean Entity Framework DbContext for Signal9 RMM Platform
/// Focuses only on core entities that actually exist
/// Multi-tenant with strict tenant isolation
/// </summary>
public class Signal9DbContext(DbContextOptions<Signal9DbContext> options) : DbContext(options)
{

    #region DbSets - Core Entities Only

    /// <summary>
    /// Tenants in the multi-tenant system
    /// </summary>
    public DbSet<Tenant> Tenants { get; set; } = null!;

    /// <summary>
    /// Agents registered with tenants
    /// </summary>
    public DbSet<Agent> Agents { get; set; } = null!;

    /// <summary>
    /// Commands sent to agents
    /// </summary>
    public DbSet<AgentCommand> AgentCommands { get; set; } = null!;

    #endregion

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Configure core entities only
        ConfigureTenantEntities(modelBuilder);
        ConfigureAgentEntities(modelBuilder);
        ConfigureCommandEntities(modelBuilder);

        // Configure global query filters for multi-tenant isolation
        ConfigureGlobalFilters(modelBuilder);

        // Configure indexes for performance
        ConfigureIndexes(modelBuilder);
    }

    #region Entity Configuration

    private static void ConfigureTenantEntities(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Tenant>(entity =>
        {
            entity.HasKey(t => t.Id);
            entity.Property(t => t.Name).IsRequired().HasMaxLength(255);
            entity.Property(t => t.TenantCode).IsRequired().HasMaxLength(50);
            entity.HasIndex(t => t.TenantCode).IsUnique();
            entity.Property(t => t.CreatedAt).HasDefaultValueSql("GETUTCDATE()");
            entity.Property(t => t.UpdatedAt).HasDefaultValueSql("GETUTCDATE()");
        });
    }

    private static void ConfigureAgentEntities(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Agent>(entity =>
        {
            entity.HasKey(a => a.Id);
            entity.Property(a => a.MachineName).IsRequired().HasMaxLength(255);
            entity.Property(a => a.OperatingSystem).IsRequired().HasMaxLength(50);
            entity.Property(a => a.OSVersion).HasMaxLength(50);
            entity.Property(a => a.Architecture).HasMaxLength(20);
            entity.Property(a => a.IpAddress).IsRequired().HasMaxLength(45);
            entity.Property(a => a.MacAddress).HasMaxLength(17);
            entity.Property(a => a.CreatedAt).HasDefaultValueSql("GETUTCDATE()");
            entity.Property(a => a.UpdatedAt).HasDefaultValueSql("GETUTCDATE()");

            // Foreign key to Tenant
            entity.HasOne<Tenant>()
                  .WithMany()
                  .HasForeignKey(a => a.TenantId)
                  .OnDelete(DeleteBehavior.Cascade);

            // Unique constraint on TenantId + MachineName
            entity.HasIndex(a => new { a.TenantId, a.MachineName }).IsUnique();
            entity.HasIndex(a => new { a.TenantId, a.Status });
        });
    }

    private static void ConfigureCommandEntities(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AgentCommand>(entity =>
        {
            entity.HasKey(c => c.Id);
            entity.Property(c => c.AgentId).IsRequired().HasMaxLength(100);
            entity.Property(c => c.CommandType).IsRequired();
            entity.Property(c => c.Parameters).HasMaxLength(4000);
            entity.Property(c => c.Result).HasMaxLength(4000);
            entity.Property(c => c.ErrorMessage).HasMaxLength(2000);
            entity.Property(c => c.CreatedAt).HasDefaultValueSql("GETUTCDATE()");
            entity.Property(c => c.UpdatedAt).HasDefaultValueSql("GETUTCDATE()");

            // Foreign key to Tenant
            entity.HasOne<Tenant>()
                  .WithMany()
                  .HasForeignKey(c => c.TenantId)
                  .OnDelete(DeleteBehavior.Cascade);

            // Index for command queries
            entity.HasIndex(c => new { c.TenantId, c.AgentId, c.Status });
        });
    }

    #endregion

    #region Global Configuration

    private static void ConfigureGlobalFilters(ModelBuilder _)
    {
        // Multi-tenant isolation - filters will be applied at the service layer
        // using proper tenant context to ensure data isolation
        // modelBuilder.Entity<Agent>().HasQueryFilter(a => a.TenantId == CurrentTenantId);
        // modelBuilder.Entity<AgentCommand>().HasQueryFilter(c => c.TenantId == CurrentTenantId);
        // Note: Tenant entity doesn't need a filter as it's the root tenant data
    }

    private static void ConfigureIndexes(ModelBuilder modelBuilder)
    {
        // Additional performance indexes
        modelBuilder.Entity<Agent>()
            .HasIndex(a => a.LastSeen)
            .HasDatabaseName("IX_Agent_LastSeen");

        modelBuilder.Entity<AgentCommand>()
            .HasIndex(c => c.ScheduledAt)
            .HasDatabaseName("IX_AgentCommand_ScheduledAt");
    }

    #endregion
}
