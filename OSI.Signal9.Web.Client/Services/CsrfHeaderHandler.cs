using OSI.Signal9.Contracts.Bff;

namespace OSI.Signal9.Web.Client.Services;

/// <summary>
/// Adds web's CSRF header to every request. Web only requires it on state-changing methods, but sending it always is
/// simpler and harmless.
/// </summary>
public sealed class CsrfHeaderHandler : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.Remove(BffRoutes.CsrfHeader);
        request.Headers.Add(BffRoutes.CsrfHeader, BffRoutes.CsrfHeaderValue);
        return base.SendAsync(request, cancellationToken);
    }
}
