# Signal9

Remote monitoring and management (RMM) for MSPs. Pre-alpha: not ready for production use. Authentication is not implemented yet.

## Projects

| Project | What it is |
| --- | --- |
| `OSI.Signal9.API` | ASP.NET Core REST API and SignalR hubs. EF Core on PostgreSQL. Deployed to Azure Container Apps. |
| `OSI.Signal9.Web` | Blazor WebAssembly dashboard. Deployed to Azure Static Web Apps. |
| `OSI.Signal9.Agent` | Windows Service installed on managed machines. |
| `OSI.Signal9.Contracts` | Request/response types shared by the API, Web and Agent. |
| `OSI.Signal9.AppHost` | Aspire orchestration for local development. |
| `OSI.Signal9.ServiceDefaults` | Aspire defaults: OpenTelemetry, health checks, resilience. |
| `OSI.Signal9.API.Tests` | Integration tests for the API. |

## Running locally

Prerequisites: .NET 10 SDK and Docker (for the PostgreSQL container).

```sh
dotnet run --project OSI.Signal9.AppHost
```

This starts PostgreSQL (a `postgres:18` container), the API (`https://localhost:7201`) and the dashboard (`https://localhost:7001`). The OpenAPI document is at `https://localhost:7201/openapi/v1.json`, and `OSI.Signal9.API/OSI.Signal9.API.http` has sample requests.

The API never applies migrations at startup. The first time, and after pulling new migrations, copy the `signal9` database's connection string from the Aspire dashboard and apply them:

```sh
dotnet tool restore
dotnet ef database update --project OSI.Signal9.API --connection "<connection string>"
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
| `/api/agents` | `GET` (paged; `tenantId`, `status`, `group`, `search` filters), `POST` (agent enrollment) |
| `/api/agents/{agentId}` | `GET`, `PUT` (group, tags, maintenance), `DELETE` |
| `/api/agents/{agentId}/heartbeat` | `PUT` |
| `/api/agents/{agentId}/telemetry` | `GET` (paged; `from`, `to`), `POST` |
| `/api/agents/{agentId}/telemetry/{sampleId}` | `GET` |
| `/api/agents/{agentId}/commands` | `GET` (paged; `status`), `POST` |
| `/api/agents/{agentId}/commands/{commandId}` | `GET` |
| `/api/agents/{agentId}/commands/{commandId}/result` | `PUT` (agent reports the outcome) |
| `/api/agents/{agentId}/commands/{commandId}/cancellation` | `PUT` |

Hubs: `/hubs/agents` (commands pushed to agents) and `/hubs/dashboard` (live updates for the dashboard).

## Development

```sh
dotnet build OSI.Signal9.slnx
dotnet test --solution OSI.Signal9.slnx
dotnet tool restore
dotnet ef migrations add <Name> --project OSI.Signal9.API --output-dir Data/Migrations
```

The tests need Docker: they start one PostgreSQL container (Testcontainers) and give each test its own database.

Package versions are managed centrally in `Directory.Packages.props`. CI treats warnings, including vulnerable packages, as errors.

## Acknowledgements

- [CyberDrain/CIPP](https://github.com/KelvinTegelaar/CIPP) (AGPL-3.0): our PR title check is adapted from theirs. Gratefully stolen.

## License

See [LICENSE](LICENSE).
