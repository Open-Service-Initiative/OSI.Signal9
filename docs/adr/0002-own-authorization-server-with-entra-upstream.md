# Signal9 runs its own OAuth authorization server (OpenIddict), with Entra as upstream login for people

API applications, agents and our own dashboard all need OAuth governed by Signal9: tenant-confined registrations, our scopes, certificate-bound tokens, and revocation we control. Entra can't issue tokens on those terms for a self-hosted, multi-tenant product. So Signal9 issues its own tokens with OpenIddict, and people still sign in through the operator's own multi-tenant Entra app registration.

## Considered Options

- **Entra as the only authorization server.** Rejected: it can't model tenant-confined third-party applications, agent credentials or our scope model.
- **Duende IdentityServer.** Rejected: its licensing charges per installation, which doesn't fit operators self-hosting.
- **A Microsoft-native authorization server.** None exists. ASP.NET Core only validates tokens; it doesn't issue them.
