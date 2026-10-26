# Signal9

Remote monitoring and management (RMM) for MSPs. Pre-alpha: not ready for production use. Authentication is being built (#63): every API endpoint now requires an access token carrying its scope. The dashboard signs people in through web, which holds their tokens; agents enroll with an enrollment token and then authenticate with their own certificate.

## Projects

| Project | What it is |
| --- | --- |
| `OSI.Signal9.Auth` | The OAuth authorization server (OpenIddict): discovery and token endpoints. |
| `OSI.Signal9.API` | ASP.NET Core REST API and SignalR hubs, plus the authorization server's mTLS token, revocation and introspection endpoints. Deployed to Azure Container Apps. |
| `OSI.Signal9.Server` | Server code shared by Auth and API: EF Core on PostgreSQL, migrations, the OpenIddict token rules and the stored keys. |
| `OSI.Signal9.Web` | The dashboard's host and backend-for-frontend: signs people in at auth, keeps their tokens server-side and forwards `/api` and `/hubs/dashboard` to api. Serves the public API reference (Scalar) at `/docs`. |
| `OSI.Signal9.Web.Client` | The dashboard: a Blazor WebAssembly app on Fluent UI Blazor v5, served by web. |
| `OSI.Signal9.Agent` | Agent installed on managed machines: a Windows service, systemd service or launchd daemon. |
| `OSI.Signal9.Contracts` | Request/response types shared by the API, Web and Agent. |
| `OSI.Signal9.AppHost` | Aspire orchestration for local development. |
| `OSI.Signal9.ServiceDefaults` | Aspire defaults: OpenTelemetry, health checks, resilience. |
| `OSI.Signal9.IntegrationTests` | Integration tests: Auth and API in memory against PostgreSQL, and the agent end to end against them. |
| `OSI.Signal9.Agent.Tests` | Agent unit tests, with no API host or database. |
| `OSI.Signal9.Web.Tests` | Dashboard component tests with bUnit. |

## Running locally

Prerequisites: .NET 10 SDK and Docker (for the PostgreSQL container).

```sh
dotnet run --project OSI.Signal9.AppHost
```

This starts PostgreSQL (a `postgres:18` container), the authorization server (`https://localhost:7101`), the API (`https://localhost:7201`) and web, the dashboard (`https://localhost:7001`). The API reference is at `https://localhost:7001/docs` ("try it" uses your dashboard session), the OpenAPI document at `https://localhost:7201/docs/openapi/v1.json`, and `OSI.Signal9.API/OSI.Signal9.API.http` has sample requests. The hosts take their addresses (`Identity:*`), probe port and real-time hosting from the AppHost alone, and refuse to start without them, so run them through the AppHost.

The hosts never apply migrations at startup. The first time, and after pulling new migrations, copy the `signal9` database's connection string from the Aspire dashboard and apply them:

```sh
dotnet tool restore
dotnet ef database update --project OSI.Signal9.Server --connection "<connection string>"
```

Signal9 generates its own token signing and encryption keys and its Data Protection keys, and stores them in the database, wrapped by a key-encryption key (ADR-0008). Locally that's a certificate the AppHost creates on first run at `Signal9/local-kek.pfx` under your local application data folder. Keep it: the keys in your local database can't be read without it. If you lose it, delete the PostgreSQL data volume too.

To enroll a machine, issue an enrollment token for a tenant (`POST /api/v1/tenants/{tenantId}/enrollment-tokens`), then run the agent with its secret. The agent enrolls once and keeps its identity for every later start:

```sh
dotnet run --project OSI.Signal9.Agent -- --Agent:EnrollmentToken=<secret>
```

### Agent enrollment and instances

An agent enrolls with an enrollment token and the self-signed certificate of a key it has just generated: non-exportable, in the TPM where there is one and otherwise protected by DPAPI (Windows only so far). Signal9 registers the certificate as the agent's OAuth client, whose client ID is the agent's ID. From then on the agent gets access tokens with client credentials at api's mTLS token endpoint (`self_signed_tls_client_auth`): they carry the `agent` scope, are bound to the certificate, and come without refresh tokens. Every agent request, and the agent hub, takes the agent's identity from its token alone; the hub closes when the token expires and the agent reconnects with a new one. Rotating or revoking the enrollment token doesn't affect agents already enrolled with it.

A machine runs one agent instance per deployment and root tenant tree. Each has its own `Agent:InstanceId` (default `default`), under which its key, certificate and state are kept (`Agent:StateDirectory`, by default `Signal9/Agent` under the machine's common application data folder), so instances never collide.

| Setting | Purpose |
| --- | --- |
| `Agent:ApiBaseUrl` | The deployment's api host. |
| `Agent:EnrollmentToken` | The enrollment token's secret. Needed only until the instance has enrolled. |
| `Agent:InstanceId` | Names the instance among the machine's instances: letters, digits and hyphens. |
| `Agent:StateDirectory` | Where instances keep their state, each in a folder named after it. |

**TLS inspection must exclude the api host.** Agents authenticate with mutual TLS, which a TLS-inspecting proxy breaks (ADR-0004), and there's no fallback: exclude the api host from TLS inspection on every network agents run on.

## Keys

Signing and encryption keys rotate on their own. Each key is active for 90 days from its certificate's start date; the next one is generated and published in the key set 14 days before it activates, and retired keys are kept for 90 days more, so tokens they signed or encrypted keep validating, then deleted. Every running `auth` and `api` instance reloads its keys at each of those moments, without a restart.

The `KeyEncryption` section chooses the key-encryption key on `auth` and `api`:

| Setting | Meaning |
| --- | --- |
| `Provider` | `KeyVault` (Azure) or `Certificate` (on-prem and local development). |
| `KeyVault:KeyId` | The Key Vault RSA key, without a version: `https://{vault}.vault.azure.net/keys/{name}`. Reached by managed identity only; the identity needs wrap and unwrap permission on the key. |
| `KeyVault:ManagedIdentityClientId` | A user-assigned identity's client ID, or empty for the system-assigned identity. |
| `Certificate:Path` | A PKCS#12 file, or a PEM certificate with `Certificate:KeyPath`. The key must be RSA-3072 or larger. |
| `Certificate:KeyPath`, `Certificate:Password` | The PEM private key, and the password of the PKCS#12 file or encrypted PEM key, if any. |

## Dashboard (web)

Browsers only ever talk to web and auth (ADR-0003). web is a confidential client of auth (`signal9-web`) using authorization code with PKCE. It redeems codes at api's mTLS token alias with its own client certificate, so its access tokens are bound to it, and keeps them in the database, encrypted with Data Protection. The browser gets only a `__Host-Http-signal9` cookie (Secure, HttpOnly, SameSite=Strict) holding an encrypted session ticket. web generates its certificate itself, stores it wrapped by the key-encryption key, rotates it with the signing keys' schedule and registers it at the authorization server on its own.

- Every state-changing request to web must send `X-CSRF: 1`. There's no CORS on any host.
- The dashboard hub's handshake through web must come from web's own origin (`Identity:WebUri`): a WebSocket handshake without a matching `Origin` is refused with 403.
- web forwards only `/api/*`, `/hubs/dashboard` and the OpenAPI document (`/docs/openapi/*`) to api, at `Identity:ApiUri`.
- web sends a strict Content Security Policy: scripts from web only (plus WebAssembly and the import map's hash), no other origins.
- `Identity:WebUri` is web's public address; its redirect URI is `{WebUri}signin-oidc`.

## Sign-in with Entra

People sign in at auth through your own multi-tenant Entra app registration, with MFA mandatory (ADR-0002). Register an app for "Accounts in any organizational directory", with the web redirect URI `https://<auth host>/signin-oidc`, and add `amr`, `auth_time` (and `sid`) as optional claims of its ID token. Set its front-channel logout URL to `https://<auth host>/entra/front-channel-logout`, so signing out of Entra in the browser ends the person's Signal9 sessions. A sign-in is refused unless its `amr` contains `mfa` and it carries `auth_time`. Sensitive actions need MFA within the last 15 minutes: the API answers RFC 9470's `insufficient_user_authentication` challenge with `max_age=900`, and auth then sends the person to Entra with `prompt=login`, so give the registration a Conditional Access policy that requires MFA at every sign-in. People are identified by their directory and object IDs, never by email or UPN, and signing in alone creates no user record.

The app registration's credential is never a secret. Locally it's a certificate the AppHost creates on first run at `Signal9/entra-credential.pfx` under your local application data folder: upload `entra-credential.cer`, next to it, to the registration's certificates, and enter the registration's client ID as the AppHost's `entra-client-id` parameter. When published, it's a federated identity credential backed by auth's managed identity.

The `Entra` section on auth:

| Setting | Meaning |
| --- | --- |
| `ClientId` | The app registration's application (client) ID. |
| `ClientCredentials` | Microsoft.Identity.Web credential descriptions: `SignedAssertionFromManagedIdentity` (with `ManagedIdentityClientId` for a user-assigned identity) on Azure, or a certificate (`Path`, `StoreWithThumbprint`, `KeyVault`, ...). auth refuses to start with a client secret. |
| `TenantId` | `organizations` by default: work and school accounts from any directory. |
| `AllowedTenantIds` | Optional, recommended: the Entra directory IDs whose people may sign in. Empty allows every directory. |
| `DeployingTenantId` | Required: the directory ID of your own Entra tenant, which holds the app registration. Only its people can be Superadministrators. |

### Sessions

Entra stays the authority over people. Every refresh of a person's tokens checks that their Entra session hasn't ended and that they still hold a role assignment (or are a Superadministrator), and removing a user's last role assignment ends their sessions at once. Hourly, auth refreshes each signed-in person's tokens at Entra, bypassing any cached token: if Entra answers `invalid_grant` (the person was disabled or deleted, or their sessions revoked) or `interaction_required`, their sessions end, so a change in Entra takes effect within the hour. Anything else, such as Entra being unreachable, keeps the session and asks again five minutes later. Signing out of the dashboard revokes every token behind the session, and Entra's front-channel logout ends every session from that Entra session.

### Superadministrator

Superadministrator is the single deployment-wide authority: full authority over every tenant tree, including the `deployment.*` scopes, and the only person who can create root tenants (ADR-0006). It's the app role `Superadministrator` on your app registration, honoured only for people from `DeployingTenantId`: any consenting directory can assign the same role to its own people, and that's ignored. auth reads the role from Entra each time it signs the person in, and again at every session check: hourly, auth refreshes each signed-in person's tokens at Entra, so removing the assignment (or letting a PIM activation lapse) takes effect within the hour, and their open sessions lose it at once.

Until first-run setup (#77) makes the deployer Superadministrator, assign it by hand in local development:

1. In the app registration's **App roles**, create a role with the value `Superadministrator`, allowed for **Users/Groups**.
2. In **Enterprise applications**, open the registration's application in your own tenant, and under **Users and groups** assign yourself (or a PIM-eligible group) to the role.
3. Enter your tenant's directory ID as the AppHost's `entra-deploying-tenant-id` parameter, then sign in again.

## Client certificates (mTLS)

Agents and API applications that authenticate with a certificate use the api host's RFC 8705 aliases (`connect/token`, `connect/revoke`, `connect/introspect`), which auth's discovery document lists under `mtls_endpoint_aliases`. Access tokens issued there are bound to the certificate (`cnf`), and api refuses them without it. auth never asks for client certificates.

| Setting | Purpose |
| --- | --- |
| `ForwardedHeaders:KnownNetworks` | CIDR networks whose `X-Forwarded-For`, `X-Forwarded-Proto` and client-certificate headers are believed, such as the Container Apps infrastructure subnet. Empty by default: forwarded headers are ignored. |
| `ForwardedHeaders:ForwardClientCertificate` | api only. `true` behind Container Apps ingress, which forwards the client certificate in `X-Forwarded-Client-Cert`. Leave it `false` when self-hosted: Kestrel reads the certificate from the TLS handshake. |
| `Identity:TlsClientCertificateAuthorities` | PEM root (and intermediate) certificates trusted to issue `tls_client_auth` client certificates. Without them only `self_signed_tls_client_auth` is offered. |

A `tls_client_auth` client registers exactly one RFC 8705 subject (`tls_client_auth_subject_dn`, `_san_dns`, `_san_uri`, `_san_ip` or `_san_email`); a `self_signed_tls_client_auth` client registers its certificate in its key set.

## API

| Resource | Methods |
| --- | --- |
| `/api/v1/tenants` | `GET` (paged), `POST` |
| `/api/v1/tenants/{tenantId}` | `GET`, `PATCH` (`application/merge-patch+json`: name, description, contact, max devices, approval hold), `DELETE` |
| `/api/v1/tenants/{tenantId}:suspend`, `:activate`, `:cancel` | `POST` (status transitions; 409 when the current status doesn't allow it) |
| `/api/v1/tenants/{tenantId}/transfers` | `GET` (paged, newest first; both sides of the tenant's transfers, `direction`, `status` and `includeDescendants`, default `true`), `POST` (propose moving the tenant and its descendants to `toParentTenantId`; an Administrator above the tenant) |
| `/api/v1/tenants/{tenantId}/transfers/{transferId}` | `GET` |
| `/api/v1/tenants/{tenantId}/transfers/{transferId}:accept`, `:decline`, `:cancel` | `POST` (the new parent's side accepts, moving the tenant at once, or declines; the proposing side cancels; 409 once decided) |
| `/api/v1/devices` | `GET` (paged; `tenantId`, `status`, `group`, `search` filters) |
| `/api/v1/devices/{deviceId}` | `GET`, `PATCH` (`application/merge-patch+json`: group, tags), `DELETE` |
| `/api/v1/devices/{deviceId}/telemetry` | `GET` (paged; `from`, `to`) |
| `/api/v1/devices/{deviceId}/telemetry/{sampleId}` | `GET` |
| `/api/v1/devices/{deviceId}/commands` | `GET` (paged; `status`), `POST` |
| `/api/v1/devices/{deviceId}/commands/{commandId}` | `GET` |
| `/api/v1/devices/{deviceId}/commands/{commandId}/result` | `PUT` (the agent reports the outcome) |
| `/api/v1/devices/{deviceId}/commands/{commandId}:cancel` | `POST` |
| `/api/v1/agents` | `POST` (enrollment, with no access token: an enrollment token and the agent's self-signed certificate; a new device and agent) |
| `/api/v1/agents/{agentId}` | `GET` |
| `/api/v1/agents/{agentId}/heartbeat` | `PUT` (sent by the agent; the response says whether work is waiting) |
| `/api/v1/agents/{agentId}/pending-commands` | `GET` (paged, oldest first; the agent's pending work, each with its canonical URL) |
| `/api/v1/agents/{agentId}/telemetry` | `POST` (sent by the agent; stored on its device) |
| `/api/v1/tenants/{tenantId}/agents` | `GET` (paged, newest enrollment first; the tenant's "recently enrolled" feed, `includeDescendants` defaults to `true`) |
| `/api/v1/tenants/{tenantId}/enrollment-tokens` | `GET` (paged), `POST`; each token shows its use count and last use |
| `/api/v1/tenants/{tenantId}/invitations` | `GET` (paged, newest first, `includeDescendants` defaults to `true`), `POST` (one role at the tenant, optionally for an existing `userId` or pinned to an Entra directory `entraTenantId`; the response carries the link, once) |
| `/api/v1/tenants/{tenantId}/invitations/{invitationId}` | `GET` |
| `/api/v1/tenants/{tenantId}/invitations/{invitationId}:revoke` | `POST` (409 once redeemed or revoked) |

An invitation offers one role at one tenant to whoever redeems its link, which carries only an opaque reference and works once, within 72 hours. Signal9 sends no email: share the link yourself. The person opens it on auth, signs in with Entra and accepts; that creates their user, owned by the invitation's tenant, if they have none. Invitations are matched only by Entra identity (`tid`, `oid`), never by email.

Single resources and write responses carry a strong `ETag`. Send it back in `If-Match` on a `PUT`, `PATCH`, `DELETE` or `:action` to get `412 Precondition Failed` instead of overwriting someone else's change, and in `If-None-Match` on a `GET` to get `304 Not Modified` when nothing changed.

A device is the managed machine; an agent is one enrolled installation on it. Telemetry and command history belong to the device and survive the agent being replaced. The agent's own requests (`/api/v1/agents/{agentId}/...` and command results) are refused with 403 unless `{agentId}` is the agent the access token belongs to.

Hubs: `/hubs/agents` and `/hubs/dashboard` (live updates for the dashboard, reached through web). The agent hub is a doorbell (ADR-0009): it rings a device's active agent when a command is queued, carries no command content and accepts no calls. The agent fetches its pending commands over REST when the doorbell rings, every time it (re)connects and when a heartbeat response says work is waiting.

The dashboard hub needs `devices.read` or `telemetry.read`, and any API application holding one can listen too. A connection hears changes from the tenants its caller's role assignments reach (every tenant tree for a Superadministrator), as small notices: `DeviceChanged`/`DeviceDeleted` (`deviceId`, `url`), `CommandChanged` (`deviceId`, `commandId`, `url`) and `TelemetryReceived` (`deviceId`, `sampleId`, `url`). Fetch the details from `url` over REST. A connection closes when its token expires or is revoked (sign-out, front-channel logout) or its caller loses their last role assignment; reconnect with a fresh token. Losing or gaining one of several role assignments, or a tenant transfer, changes what it hears at once. api re-checks open connections every `Hubs:AccessCheckInterval` (default 10 seconds) for changes made elsewhere.

## Operations

Both hosts serve their liveness (`/alive`) and readiness (`/health`) probes only on the internal port in `HealthProbes:Port`, in every environment; it's required, and that port serves nothing else. The AppHost assigns it.

Each rate limiting policy has a `RateLimiting:<policy>` section with `PermitLimit` (requests per window) and `Window` (default `00:01:00`). The anonymous endpoints that make the database work have conservative defaults, against unrestricted resource consumption (OWASP API Security Top 10 API4:2023, ASVS anti-automation); every other policy is off until its `PermitLimit` is set:

| Policy | Applies to | Partitioned by | Default |
| --- | --- | --- | --- |
| `Global` | every request on the host | client IP | off |
| `Token` | the token endpoint on auth and its mTLS alias on api | `client_id` and client IP | 300 a minute |
| `Authorize` | the authorization endpoint on auth | client IP | off |
| `Api` | api's resources and hubs | caller (the access token's subject), or client IP without a token | off |
| `Enrollment` | agent enrollment on api | client IP | 60 a minute |
| `EnrollmentToken` | agent enrollment on api | enrollment token | off |
| `InvitationRedemption` | invitation links on auth | client IP | 30 a minute |

The `Token` default is shared by every person signed in to the dashboard, since web refreshes their tokens as one client from one address: raise it for more than a few thousand people signed in at once. Agents enrolling faster than `Enrollment` allows from one site retry with backoff. Set a limit, or set `PermitLimit` to empty (`null` in JSON, `RateLimiting__Enrollment__PermitLimit=` in the environment) to turn a default off:

```json
"RateLimiting": { "Token": { "PermitLimit": 600, "Window": "00:01:00" }, "Enrollment": { "PermitLimit": null } }
```

Rejected requests get `429` ProblemDetails with `Retry-After`. Errors are ProblemDetails, without exception details outside Development, and HSTS (two years, `includeSubDomains`, `preload`) is sent outside Development.

### Real-time

The agent and dashboard hubs (ADR-0009) are hosted as the AppHost's `Realtime:Hosting` says. The AppHost passes it to api and web in every mode, and neither starts without it:

- `AzureSignalR` (the default when published): both hubs run on Azure SignalR Service. api negotiates and hands the client a token for the service that carries only what the hubs read and lasts `Realtime:ServiceTokenLifetime` (default two minutes). The dashboard reaches the service through web, at `/hubs/dashboard/service/`, so browsers stay on web's origin; agents connect to it directly. Clients can't connect to api's hubs directly.
- `SelfHosted` (the default locally): both hubs run in api. With `Realtime:ApiReplicas` above 1, the AppHost adds a Redis container and api's replicas share it as their backplane (`Realtime:BackplaneChannelPrefix`, default `signal9`); the hubs then take WebSockets only and answer negotiation without keeping state, so no sticky sessions are needed. A single replica needs no Redis.

Each replica re-checks its hub connections every `Hubs:AccessCheckInterval` (default 10 seconds), so a revocation made on another replica closes a connection within that time.

## Development

```sh
dotnet build OSI.Signal9.slnx
dotnet test --solution OSI.Signal9.slnx
dotnet tool restore
dotnet ef migrations add <Name> --project OSI.Signal9.Server --output-dir Data/Migrations
```

The tests need Docker: they start one PostgreSQL container (Testcontainers) and give each test its own database. Each test boots Auth and API in memory together; test clients call the API with a token minted at Auth.

Package versions are managed centrally in `Directory.Packages.props`. CI treats warnings, including vulnerable packages, as errors.

## Acknowledgements

- [CyberDrain/CIPP](https://github.com/KelvinTegelaar/CIPP) (AGPL-3.0): our PR title check is adapted from theirs. Gratefully stolen.

## License

See [LICENSE](LICENSE).
