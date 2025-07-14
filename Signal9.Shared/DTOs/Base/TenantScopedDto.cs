using System;
using System.ComponentModel.DataAnnotations;

namespace Signal9.Shared.DTOs.Base;

/// <summary>
/// Abstract base record for all tenant-scoped DTOs in the Signal9 RMM system.
/// Ensures proper multi-tenant isolation by requiring a tenant identifier.
/// </summary>
public abstract record TenantScopedDto : BaseDto, ITenantScoped
{
    /// <summary>
    /// Gets the unique identifier of the tenant this DTO belongs to.
    /// This property is required and must be set during initialization.
    /// Must be a valid, non-empty GUID to ensure proper tenant isolation.
    /// </summary>
    [Required(ErrorMessage = "TenantId is required for proper multi-tenant isolation")]
    public required Guid TenantId { get; set; }
}