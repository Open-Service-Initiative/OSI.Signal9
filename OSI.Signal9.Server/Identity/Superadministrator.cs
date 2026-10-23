using System.Security.Claims;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace OSI.Signal9.Server.Identity;

/// <summary>
/// The single deployment-wide authority (ADR-0006): an app role on the operator's Entra app registration, honoured
/// only for people from the deploying directory. auth re-reads it from Entra each time it issues a token, and the
/// token carries it, the one exception to tokens carrying identity only (ADR-0007).
/// </summary>
public static class Superadministrator
{
    /// <summary>The value of the app role on the operator's app registration, as Entra sends it in <c>roles</c>.</summary>
    public const string AppRole = "Superadministrator";

    /// <summary>The <c>role</c> claim value an access token carries for a Superadministrator.</summary>
    public const string Role = "Superadministrator";

    /// <summary>Whether a validated access token is a Superadministrator's.</summary>
    public static bool Is(ClaimsPrincipal principal) => principal.HasClaim(Claims.Role, Role);
}
