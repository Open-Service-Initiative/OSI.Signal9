using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.Options;
using OSI.Signal9.Server.Hosting;

namespace OSI.Signal9.API.Infrastructure;

/// <summary>
/// Where api gets the client certificate that OpenIddict authenticates clients with and checks tokens' <c>cnf</c>
/// against: the TLS handshake when self-hosted, or <c>X-Forwarded-Client-Cert</c> from Container Apps' ingress.
/// </summary>
internal static class ClientCertificates
{
    public const string ForwardedHeader = "X-Forwarded-Client-Cert";

    public static WebApplicationBuilder AddSignal9ClientCertificates(this WebApplicationBuilder builder)
    {
        // Self-hosted: ask for a certificate in the handshake, but don't require one, and leave validating it to
        // OpenIddict, which knows each client's registered certificates and trusted authorities.
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.ConfigureHttpsDefaults(https =>
        {
            https.ClientCertificateMode = ClientCertificateMode.AllowCertificate;
            https.AllowAnyClientCertificate();
        }));

        builder.Services.AddCertificateForwarding(options =>
        {
            options.CertificateHeader = ForwardedHeader;
            options.HeaderConverter = ContainerAppsClientCertificateHeader.Parse;
        });
        return builder;
    }

    /// <summary>
    /// Takes the forwarded certificate only when forwarding is on and the connection comes from a trusted proxy;
    /// otherwise the header is removed. Must run before forwarded headers replace the peer address.
    /// </summary>
    public static IApplicationBuilder UseSignal9ClientCertificates(this IApplicationBuilder app)
    {
        var settings = app.ApplicationServices.GetRequiredService<IOptions<ForwardedHeadersSettings>>().Value;
        var proxies = app.ApplicationServices.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;

        app.Use((context, next) =>
        {
            if (!settings.ForwardClientCertificate || !proxies.IsTrustedProxy(context.Connection.RemoteIpAddress))
                context.Request.Headers.Remove(ForwardedHeader);
            return next(context);
        });
        return app.UseCertificateForwarding();
    }
}
