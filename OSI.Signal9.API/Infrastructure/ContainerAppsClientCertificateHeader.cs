using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace OSI.Signal9.API.Infrastructure;

/// <summary>
/// Reads the client certificate from Container Apps' <c>X-Forwarded-Client-Cert</c> header, which its Envoy ingress
/// sends as <c>Hash=…;Cert="…";Chain="…";</c>. ASP.NET Core's default converter expects bare base64 DER and can't read
/// it (ADR-0004's custom converter). Only the leaf in <c>Cert</c> is used: OpenIddict builds chains from its own trust
/// configuration, and the <c>Hash</c> is recomputed from the certificate itself.
/// </summary>
internal static class ContainerAppsClientCertificateHeader
{
    public static X509Certificate2 Parse(string header)
    {
        // Envoy separates proxy hops with ','. Container Apps replaces the header, so there's exactly one element;
        // more than one means a client-supplied value got through, and none of them can be trusted.
        var elements = Split(header, ',');
        if (elements.Count != 1)
            throw new FormatException("X-Forwarded-Client-Cert must contain exactly one element.");

        X509Certificate2? certificate = null;
        foreach (var pair in Split(elements[0], ';'))
        {
            var separator = pair.IndexOf('=');
            if (separator <= 0 || !pair.AsSpan(0, separator).Trim().Equals("Cert", StringComparison.OrdinalIgnoreCase))
                continue;
            if (certificate is not null)
                throw new FormatException("X-Forwarded-Client-Cert contains more than one Cert.");

            var value = Unquote(pair[(separator + 1)..].Trim());
            // Envoy URL-encodes the PEM. Uri.UnescapeDataString keeps '+' (WebUtility.UrlDecode would turn it into a
            // space and corrupt the base64).
            var pem = Uri.UnescapeDataString(value);
            certificate = X509Certificate2.CreateFromPem(pem);
        }

        return certificate ?? throw new FormatException("X-Forwarded-Client-Cert has no Cert.");
    }

    private static string Unquote(string value)
    {
        if (value.Length < 2 || value[0] != '"' || value[^1] != '"')
            return value;
        return value[1..^1].Replace("\\\"", "\"", StringComparison.Ordinal);
    }

    /// <summary>Splits on <paramref name="separator"/> outside double quotes, honouring <c>\"</c> escapes.</summary>
    private static List<string> Split(string value, char separator)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c == '\\' && quoted && i + 1 < value.Length)
            {
                current.Append(c).Append(value[++i]);
                continue;
            }

            if (c == '"')
                quoted = !quoted;
            else if (c == separator && !quoted)
            {
                AddPart();
                continue;
            }

            current.Append(c);
        }

        if (quoted)
            throw new FormatException("X-Forwarded-Client-Cert has an unterminated quoted value.");
        AddPart();
        return parts;

        void AddPart()
        {
            // Container Apps ends the header with ';', which leaves an empty last pair.
            if (current.ToString().Trim() is { Length: > 0 } part)
                parts.Add(part);
            current.Clear();
        }
    }
}
