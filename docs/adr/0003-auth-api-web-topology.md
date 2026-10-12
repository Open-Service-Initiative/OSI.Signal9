# Three hosts: auth, api and a web BFF (no Static Web Apps)

The browser-facing authorization server lives in `auth`, and resources plus the mTLS token-endpoint aliases (RFC 8705) live in `api`. `web` serves the WASM app as a backend-for-frontend (RFC 10017 §6.1): a confidential client using authorization code + PKCE, holding tokens on the server and proxying `/api`, `/hubs` and `/docs` with YARP. The split exists because Azure Container Apps sets client-certificate handling per app, not per hostname. Browsers must never reach a host that asks for client certificates, or corporate machines will show a certificate picker. Moving `web` off Static Web Apps keeps every token out of the browser.

## Considered Options

- **Authorization server inside `web`.** Rejected: it mixes identity provider and client roles in the dashboard host.
- **Authorization server inside `api`.** Rejected: on Container Apps the authorize page would sit behind the client-certificate prompt. Avoiding that would mean Kestrel terminating TLS over TCP ingress.
- **Token exchange from a web session signed in with Entra.** Rejected: RFC 10017 says a BFF must use the authorization code grant.

## Consequences

`auth` and `api` both run the OpenIddict server against one database, one set of keys and one static issuer, each exposing only its own endpoints. Agents only ever talk to `api` and the real-time service.
