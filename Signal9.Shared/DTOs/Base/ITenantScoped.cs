using System;

namespace Signal9.Shared.DTOs.Base;

/// <summary>
/// Interface for all tenant-scoped DTOs in the Signal9 RMM system.
/// Ensures proper multi-tenant isolation by requiring a tenant identifier.
/// </summary>
public interface ITenantScoped
{
    /// <summary>
    /// Gets the unique identifier of the tenant this DTO belongs to.
    /// </summary>
    Guid TenantId { get; }
}