using System.ComponentModel.DataAnnotations;

namespace OSI.Signal9.Contracts.Users;

/// <summary>
/// A person known to Signal9. A user belongs to exactly one owning tenant and is identified by their Entra directory
/// (<c>tid</c>) and object (<c>oid</c>), never by email or UPN (story 41).
/// </summary>
public sealed record UserDto(
    Guid Id,
    Guid TenantId,
    Guid EntraTenantId,
    Guid EntraObjectId,
    string DisplayName,
    DateTimeOffset CreatedAt);

public sealed record CreateUserRequest
{
    /// <summary>The user's owning tenant.</summary>
    [Required]
    public Guid? TenantId { get; init; }

    /// <summary>The person's Entra directory (<c>tid</c>).</summary>
    [Required]
    public Guid? EntraTenantId { get; init; }

    /// <summary>The person's object in their Entra directory (<c>oid</c>).</summary>
    [Required]
    public Guid? EntraObjectId { get; init; }

    [Required, StringLength(200, MinimumLength = 1)]
    public string DisplayName { get; init; } = string.Empty;
}
