using System.Text.RegularExpressions;
using OSI.Signal9.Auth.Pages;
using OSI.Signal9.Server.Identity;

namespace OSI.Signal9.Auth;

/// <summary>
/// Entra's front-channel logout (OpenID Connect Front-Channel Logout 1.0, story 51): when the person signs out of
/// Entra, Entra loads this page in a hidden frame with the Entra session that ended (<c>sid</c>) and its issuer
/// (<c>iss</c>), which names the directory. auth's sign-in cookie isn't sent to a cross-site frame (SameSite=Lax), so
/// the session is found by <c>sid</c> within that directory, never by cookie, and every session from it is revoked
/// with its tokens. It's best-effort: Entra only calls it when the person signs out in the same browser, and the hourly
/// Entra check catches the rest (story 52). Operators register this address as the app registration's front-channel
/// logout URL.
/// </summary>
public static partial class FrontChannelLogout
{
    public const string Path = "entra/front-channel-logout";

    public static IEndpointConventionBuilder MapFrontChannelLogout(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet(Path, async (HttpContext context, string? iss, string? sid, SignInSessions sessions,
            IConfiguration configuration, ILogger<SignInSessions> logger) =>
        {
            var instance = new Uri(configuration[$"{EntraSignIn.SectionName}:Instance"] ?? EntraSignIn.DefaultInstance);
            // iss is Entra's v2.0 issuer for the person's directory: {instance}{tid}/v2.0.
            if (string.IsNullOrEmpty(sid) || sid.Length > 200 || !TryParseDirectory(iss, instance, out var entraTenantId))
            {
                logger.LogWarning("Entra front-channel logout without a usable iss and sid (iss present: {HasIssuer}).",
                    !string.IsNullOrEmpty(iss));
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Not a front-channel logout",
                    detail: "Entra's front-channel logout sends iss (its issuer for the directory) and sid.");
            }

            await sessions.EndEntraSessionAsync(entraTenantId, sid,
                new SessionEnd("Front-channel logout from Entra", ClientIp: context.Connection.RemoteIpAddress?.ToString()),
                context.RequestAborted);
            // The same answer whether or not a session was found, so the page tells nobody which sessions exist. The frame
            // is Entra's, on its own origin, and nothing of it may be cached (Front-Channel Logout §4).
            return AuthPages.Message(StatusCodes.Status200OK, "Signed out", "You're signed out of Signal9.",
                formAction: "'none'", frameAncestors: instance.GetLeftPart(UriPartial.Authority));
        })
        .AllowAnonymous()
        // Entra's endpoint to call, not part of the API.
        .ExcludeFromDescription();

    private static bool TryParseDirectory(string? issuer, Uri instance, out Guid entraTenantId)
    {
        entraTenantId = Guid.Empty;
        return issuer is not null
            && issuer.StartsWith(instance.ToString(), StringComparison.OrdinalIgnoreCase)
            && Issuer().Match(issuer[instance.ToString().Length..]) is { Success: true } match
            && Guid.TryParseExact(match.Groups["tid"].Value, "D", out entraTenantId)
            && entraTenantId != Guid.Empty;
    }

    [GeneratedRegex("^(?<tid>[0-9a-fA-F-]{36})/v2\\.0/?$")]
    private static partial Regex Issuer();
}
