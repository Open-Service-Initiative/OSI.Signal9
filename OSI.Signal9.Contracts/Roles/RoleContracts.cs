using System.ComponentModel.DataAnnotations;

namespace OSI.Signal9.Contracts.Roles;

/// <summary>A named bundle of scopes (ADR-0010). Built-in roles can't be changed.</summary>
public sealed record RoleDto(
    Guid Id,
    string Name,
    string Description,
    bool BuiltIn,
    IReadOnlyList<string> Scopes);

/// <summary>Grants one role to one user at one tenant, covering that tenant and its descendants (ADR-0010).</summary>
public sealed record RoleAssignmentDto(
    Guid Id,
    Guid TenantId,
    Guid UserId,
    Guid RoleId,
    DateTimeOffset CreatedAt);

public sealed record CreateRoleAssignmentRequest
{
    [Required]
    public Guid? UserId { get; init; }

    [Required]
    public Guid? RoleId { get; init; }
}
