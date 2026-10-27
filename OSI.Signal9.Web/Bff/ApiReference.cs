using System.Buffers.Text;
using System.Security.Cryptography;
using OSI.Signal9.Server.Hosting;
using Scalar.AspNetCore;

namespace OSI.Signal9.Web.Bff;

/// <summary>
/// The public API reference (#135): Scalar at <see cref="ApiDocs.Path"/>, open to anyone (story 132), over the OpenAPI
/// document api generates, which <see cref="ApiProxy"/> forwards. "Try it" calls web's own <c>/api</c>, so they go out
/// with the signed-in person's token and api allows only what that person may do (story 133); without a session web
/// answers 401.
/// </summary>
public static class ApiReference
{
    /// <summary>Where the page's per-request CSP nonce is kept for <see cref="BrowserHardening"/>.</summary>
    public const string NonceItem = "Signal9.CspNonce";

    /// <summary>The module that adds web's CSRF header to "try it" calls.</summary>
    public const string ConfigurationModule = ApiDocs.Path + "/scalar-config.js";

    public static WebApplication MapApiReference(this WebApplication app)
    {
        app.MapScalarApiReference(ApiDocs.Path, (options, context) =>
        {
            // Scalar's page bootstraps with an inline script and injects its stylesheet, so both carry a fresh nonce
            // (CSP Level 3 §6.7.2.3), which the stylesheet reads from the csp-nonce meta element.
            var nonce = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(16));
            context.Items[NonceItem] = nonce;
            options
                .WithTitle("Signal9 API")
                .WithOpenApiRoutePattern(ApiDocs.DocumentPattern)
                .WithNonce(nonce)
                .AddHeadContent($"<meta property=\"csp-nonce\" content=\"{nonce}\">")
                .WithJavaScriptConfiguration(ConfigurationModule)
                // Nothing leaves web's origin: no font CDN, telemetry, AI agent or request proxy.
                .DisableDefaultFonts()
                .DisableTelemetry()
                .DisableAgent()
                .DisableMcp();
            options.ProxyUrl = null;
        }).AllowAnonymous();
        return app;
    }
}
