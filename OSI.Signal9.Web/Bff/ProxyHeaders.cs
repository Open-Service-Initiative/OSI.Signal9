using OSI.Signal9.Contracts.Bff;

namespace OSI.Signal9.Web.Bff;

/// <summary>
/// What web never passes between the browser and a service behind it (api, or Azure SignalR Service), whichever proxy
/// carries the request. Each proxy decides <c>Authorization</c> itself.
/// </summary>
internal static class ProxyHeaders
{
    /// <summary>Removes web's own headers from a proxied request: the browser's cookies, the CSRF header, and any forwarding headers a client made up.</summary>
    public static void RemoveFromRequest(HttpRequestMessage request)
    {
        request.Headers.Remove("Cookie");
        request.Headers.Remove(BffRoutes.CsrfHeader);
        request.Headers.Remove("Forwarded");
        foreach (var name in request.Headers.Select(h => h.Key)
                     .Where(name => name.StartsWith("X-Forwarded-", StringComparison.OrdinalIgnoreCase)).ToList())
            request.Headers.Remove(name);
    }

    /// <summary>Removes cookies from a proxied response: only web sets cookies on its own origin.</summary>
    public static void RemoveFromResponse(HttpContext context) => context.Response.Headers.Remove("Set-Cookie");
}
