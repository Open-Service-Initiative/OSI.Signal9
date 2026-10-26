using System.Security.Claims;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace OSI.Signal9.Server.Identity;

/// <summary>
/// The subject of a person's tokens: their Entra directory (<c>tid</c>) and object (<c>oid</c>), as
/// <c>{tid}:{oid}</c>. Stable, unique per person and free of personal data (story 41).
/// </summary>
public static class PersonSubject
{
    public static string Format(Guid entraTenantId, Guid entraObjectId) => $"{entraTenantId:D}:{entraObjectId:D}";

    /// <summary>
    /// The person a validated access token was issued for. An application acting as itself is its own subject
    /// (its client ID), so it never names a person.
    /// </summary>
    public static bool TryParse(ClaimsPrincipal principal, out Guid entraTenantId, out Guid entraObjectId)
    {
        entraTenantId = entraObjectId = Guid.Empty;
        var subject = principal.FindFirst(Claims.Subject)?.Value;
        if (subject is null || subject == principal.FindFirst(Claims.ClientId)?.Value)
            return false;
        return TryParse(subject, out entraTenantId, out entraObjectId);
    }

    /// <summary>The person a subject names, if it's a person's <c>{tid}:{oid}</c>.</summary>
    public static bool TryParse(string subject, out Guid entraTenantId, out Guid entraObjectId)
    {
        entraTenantId = entraObjectId = Guid.Empty;
        var parts = subject.Split(':');
        return parts.Length == 2
            && Guid.TryParseExact(parts[0], "D", out entraTenantId)
            && Guid.TryParseExact(parts[1], "D", out entraObjectId);
    }
}
