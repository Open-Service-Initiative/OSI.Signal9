# Signal9

Remote monitoring and management (RMM) for MSPs. Pre-alpha: not ready for production use. Authentication is being built (#63): the API now requires an access token, which the dashboard and agent don't send yet.

## Projects

| Project | What it is |
| --- | --- |
| `OSI.Signal9.Auth` | The OAuth authorization server (OpenIddict): discovery and token endpoints. |
| `OSI.Signal9.API` | ASP.NET Core REST API and SignalR hubs, plus the authorization server's mTLS token, revocation and introspection endpoints. Deployed to Azure Container Apps. |
| `OSI.Signal9.Server` | Server code shared by Auth and API: EF Core on PostgreSQL, migrations and the OpenIddict token rules. |
| `OSI.Signal9.Web` | Blazor WebAssembly dashboard. Deployed to Azure Static Web Apps. |
| `OSI.Signal9.Agent` | Windows Service installed on managed machines. |
| `OSI.Signal9.Contracts` | Request/response types shared by the API, Web and Agent. |
| `OSI.Signal9.AppHost` | Aspire orchestration for local development. |
| `OSI.Signal9.ServiceDefaults` | Aspire defaults: OpenTelemetry, health checks, resilience. |
| `OSI.Signal9.API.Tests` | Integration tests for Auth and API. |

## Running locally

Prerequisites: .NET 10 SDK and Docker (for the PostgreSQL container).

```sh
dotnet run --project OSI.Signal9.AppHost
```

This starts PostgreSQL (a `postgres:18` container), the authorization server (`https://localhost:7101`), the API (`https://localhost:7201`) and the dashboard (`https://localhost:7001`). The OpenAPI document is at `https://localhost:7201/openapi/v1.json`, and `OSI.Signal9.API/OSI.Signal9.API.http` has sample requests.

The hosts never apply migrations at startup. The first time, and after pulling new migrations, copy the `signal9` database's connection string from the Aspire dashboard and apply them:

```sh
dotnet tool restore
dotnet ef database update --project OSI.Signal9.Server --connection "<connection string>"
```

To enroll a machine, create a tenant in the dashboard, then run the agent with the tenant's ID:

```sh
dotnet run --project OSI.Signal9.Agent -- --Agent:TenantId=<tenant id>
```

## API

| Resource | Methods |
| --- | --- |
| `/api/tenants` | `GET` (paged), `POST` |
| `/api/tenants/{tenantId}` | `GET`, `PUT`, `DELETE` |
| `/api/v1/devices` | `GET` (paged; `tenantId`, `status`, `group`, `search` filters) |
| `/api/v1/devices/{deviceId}` | `GET`, `PATCH` (`application/merge-patch+json`: group, tags, maintenance mode), `DELETE` |
| `/api/v1/devices/{deviceId}/telemetry` | `GET` (paged; `from`, `to`) |
| `/api/v1/devices/{deviceId}/telemetry/{sampleId}` | `GET` |
| `/api/v1/devices/{deviceId}/commands` | `GET` (paged; `status`), `POST` |
| `/api/v1/devices/{deviceId}/commands/{commandId}` | `GET` |
| `/api/v1/devices/{deviceId}/commands/{commandId}/result` | `PUT` (the agent reports the outcome) |
| `/api/v1/devices/{deviceId}/commands/{commandId}:cancel` | `POST` |
| `/api/v1/agents` | `POST` (agent registration: a new agent, active on its device) |
| `/api/v1/agents/{agentId}` | `GET` |
| `/api/v1/agents/{agentId}/heartbeat` | `PUT` (sent by the agent; the response says whether work is waiting) |
| `/api/v1/agents/{agentId}/pending-commands` | `GET` (paged, oldest first; the agent's pending work, each with its canonical URL) |
| `/api/v1/agents/{agentId}/telemetry` | `POST` (sent by the agent; stored on its device) |

A device is the managed machine; an agent is one installation on it. Telemetry and command history belong to the device and survive the agent being replaced.

Hubs: `/hubs/agents` and `/hubs/dashboard` (live updates for the dashboard). The agent hub is a doorbell (ADR-0009): it rings a device's active agent when a command is queued, carries no command content and accepts no calls. The agent fetches its pending commands over REST when the doorbell rings, every time it (re)connects and when a heartbeat response says work is waiting.

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
