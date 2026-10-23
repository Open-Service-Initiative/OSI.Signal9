using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using IPNetwork = System.Net.IPNetwork;

namespace OSI.Signal9.Server.Hosting;

/// <summary>Which proxies a host trusts to forward the client's address, scheme and certificate (story 129).</summary>
public sealed class ForwardedHeadersSettings
{
    public const string SectionName = "ForwardedHeaders";

    /// <summary>
    /// The networks (CIDR, such as Container Apps' infrastructure subnet) whose connections may set forwarded headers.
    /// Empty by default: then every forwarded header is ignored.
    /// </summary>
    public string[] KnownNetworks { get; set; } = [];

    /// <summary>
    /// Whether <c>api</c> takes the client certificate from <c>X-Forwarded-Client-Cert</c> (Container Apps). Only
    /// connections from <see cref="KnownNetworks"/> are believed. Off when self-hosted, where Kestrel reads the
    /// certificate from the TLS handshake.
    /// </summary>
    public bool ForwardClientCertificate { get; set; }
}

public static class ForwardedHeaderTrust
{
    /// <summary>Trusts <c>X-Forwarded-For</c> and <c>X-Forwarded-Proto</c> only from the configured networks.</summary>
    public static IHostApplicationBuilder AddSignal9ForwardedHeaders(this IHostApplicationBuilder builder)
    {
        var settings = builder.Configuration.GetSection(ForwardedHeadersSettings.SectionName)
            .Get<ForwardedHeadersSettings>() ?? new ForwardedHeadersSettings();
        var networks = settings.KnownNetworks.Select(IPNetwork.Parse).ToList();

        builder.Services.AddSingleton(Options.Create(settings));
        builder.Services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            // Drop the loopback defaults: only what's configured is trusted.
            options.KnownProxies.Clear();
            options.KnownIPNetworks.Clear();
            foreach (var network in networks)
                options.KnownIPNetworks.Add(network);
        });
        return builder;
    }

    /// <summary>Applies forwarded headers from trusted proxies. Call it first in the pipeline.</summary>
    public static IApplicationBuilder UseSignal9ForwardedHeaders(this IApplicationBuilder app) =>
        app.UseForwardedHeaders();

    /// <summary>
    /// Whether a connection's own peer address (before forwarded headers are applied) is a trusted proxy. A missing
    /// address is never trusted.
    /// </summary>
    public static bool IsTrustedProxy(this ForwardedHeadersOptions options, IPAddress? peer)
    {
        if (peer is null)
            return false;
        if (peer.IsIPv4MappedToIPv6)
            peer = peer.MapToIPv4();
        return options.KnownProxies.Contains(peer) || options.KnownIPNetworks.Any(network => network.Contains(peer));
    }
}
