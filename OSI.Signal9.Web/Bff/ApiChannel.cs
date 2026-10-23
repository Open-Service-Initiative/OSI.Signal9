using System.Net;
using System.Security.Cryptography.X509Certificates;

namespace OSI.Signal9.Web.Bff;

/// <summary>Creates the handler web reaches <c>api</c> with, presenting a client certificate.</summary>
public interface IApiHandlerFactory
{
    HttpMessageHandler Create(X509Certificate2 clientCertificate);
}

/// <summary>
/// A direct TLS connection to <c>api</c> that presents web's certificate when api asks for one (RFC 8705), set up
/// the way YARP recommends for forwarding: no cookies, redirects, proxy or decompression.
/// </summary>
internal sealed class SocketsApiHandlerFactory : IApiHandlerFactory
{
    public HttpMessageHandler Create(X509Certificate2 clientCertificate) => new SocketsHttpHandler
    {
        UseProxy = false,
        UseCookies = false,
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        EnableMultipleHttp2Connections = true,
        ConnectTimeout = TimeSpan.FromSeconds(15),
        // SslStream on Linux can't use an ephemeral private key, so the certificate goes through PKCS#12 first.
        SslOptions = { ClientCertificates = [X509CertificateLoader.LoadPkcs12(clientCertificate.Export(X509ContentType.Pkcs12), null)] },
    };
}

/// <summary>
/// The one way web talks to <c>api</c>: redeeming codes at its mTLS token alias and forwarding the dashboard's calls.
/// It always presents web's current certificate, and moves to the next one when it rotates.
/// </summary>
public sealed class ApiChannel(WebClientCertificate certificates, IApiHandlerFactory handlers)
{
    private readonly Lock _gate = new();
    private (string Thumbprint, HttpMessageInvoker Invoker)? _current;

    /// <summary>An invoker presenting the current certificate. Don't dispose it; the channel owns it.</summary>
    public HttpMessageInvoker Invoker
    {
        get
        {
            var certificate = certificates.Current;
            lock (_gate)
            {
                // The previous invoker isn't disposed: requests may still be using it, and it changes every 90 days.
                if (_current is not { } current || current.Thumbprint != certificate.Thumbprint)
                    _current = current = (certificate.Thumbprint, new HttpMessageInvoker(handlers.Create(certificate)));
                return current.Invoker;
            }
        }
    }

    /// <summary>A client for one call, such as a token request, over <see cref="Invoker"/>'s connection.</summary>
    public HttpClient CreateClient() => new(new InvokerHandler(Invoker), disposeHandler: true);

    private sealed class InvokerHandler(HttpMessageInvoker invoker) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            invoker.SendAsync(request, cancellationToken);
    }
}
