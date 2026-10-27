namespace OSI.Signal9.Contracts.ApiApplications;

/// <summary>
/// A connected application: a person's consent to a delegated API application acting for them (stories 87-89), kept
/// until it's revoked. It belongs to the application, at its canonical <see cref="Url"/>; the person and the application's
/// Administrators can list and revoke it. Revoking it ends every token the application holds for the person.
/// </summary>
/// <param name="Url">The connection's canonical URL, under its API application.</param>
/// <param name="TenantId">The tenant the API application is registered at.</param>
/// <param name="Subject">The person, as their tokens name them (<c>{tid}:{oid}</c>).</param>
/// <param name="UserId">The person's user, when the caller can see it.</param>
/// <param name="Scopes">Every scope the person consented to.</param>
/// <param name="ConnectedAt">When the person first consented.</param>
public sealed record ConnectionDto(
    Guid Id,
    string Url,
    Guid TenantId,
    Guid ApiApplicationId,
    string ApiApplicationName,
    string Subject,
    Guid? UserId,
    string? UserDisplayName,
    IReadOnlyList<string> Scopes,
    DateTimeOffset ConnectedAt);
