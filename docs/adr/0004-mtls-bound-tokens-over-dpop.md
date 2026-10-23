# Sender-constrained tokens use mTLS (RFC 8705), not DPoP, for now

Agents and confidential API applications get access tokens bound to their client certificate. Most are self-signed, and agent keys are non-exportable. Neither OpenIddict nor ASP.NET Core supports DPoP yet; ASP.NET plans it for .NET 12. Writing our own proof-of-possession validator would be custom security code we'd rather not own. The accepted cost is that corporate TLS inspection breaks mTLS, so Signal9 hosts must be excluded from it, with no fallback.

## Consequences

On Container Apps, ingress terminates TLS and forwards the certificate in `X-Forwarded-Client-Cert`. That relies on ingress stripping any copy of the header sent by the client, which a proof of concept has to confirm. Our own code only converts the header into a normal client certificate; OpenIddict's validation checks the token's certificate binding (`cnf`). The binding check sits behind one seam, so DPoP can be added later for public clients such as an MCP server, or once .NET 12 ships, without a redesign.
