# Signal9

Signal9 is a multi-tenant RMM platform. See `README.md` for the project layout, `GLOSSARY.md` for domain vocabulary, and `docs/adr/` for the architecture decisions the codebase is moving towards. Where today's code disagrees with an ADR, the ADR is the target.

## Projects

- `OSI.Signal9.API`: ASP.NET Core controller API plus SignalR hubs, using EF Core on SQL Server.
- `OSI.Signal9.Web`: Blazor WebAssembly dashboard.
- `OSI.Signal9.Agent`: Windows Service on managed devices. It enrolls, sends heartbeats and telemetry, and runs commands.
- `OSI.Signal9.Contracts`: DTOs, enums and hub interfaces shared by the other projects. It has no server-side dependencies, because it ships in the WASM bundle.
- `OSI.Signal9.AppHost` / `OSI.Signal9.ServiceDefaults`: Aspire orchestration, used for local development and deployment.

## API conventions

- Routes are resource-oriented under `/api`, with plural nouns, and nest only to show ownership (`/api/agents/{agentId}/commands`).
- Status codes:
  - GET returns 200.
  - POST returns 201 with a `Location` header (`CreatedAtRoute`).
  - PUT replaces the resource and returns 200.
  - DELETE returns 204.
- State changes that aren't CRUD are modelled as sub-resources (`PUT .../commands/{id}/result`, `PUT .../cancellation`), not as verbs in the URL.
- Errors are `application/problem+json`: 400 for validation, 404 when missing, 409 for state conflicts. Never return 200 for a failure.
- Collections take `?page=&pageSize=` (maximum 100) and return `PagedResult<T>`. Don't name an action parameter `page`; it breaks query binding.
- Validate requests with data annotations on the contract types. On positional records, validation attributes go on the constructor parameters.
- Enums are serialized as strings everywhere, through `Signal9Json`.
- Inject `TimeProvider`; never call `DateTime.UtcNow` directly, so tests can control time.

## Data

- When you change the model, add a migration: `dotnet ef migrations add <Name> --project OSI.Signal9.API --output-dir Data/Migrations`.
- Entities store UTC `DateTime`; contracts expose `DateTimeOffset`.

## Testing

- `OSI.Signal9.API.Tests` hosts the API in memory, with a `FakeTimeProvider`. Every new endpoint gets a test covering its status codes.
- Run the tests with `dotnet test --solution OSI.Signal9.slnx`.

## Commits and pull requests

- Commit messages and PR titles follow [Conventional Commits](https://www.conventionalcommits.org/): `type(scope): summary`, for example `feat(api): add tenant transfer endpoint` or `docs(adr): record realtime doorbell`.

## Agent skills

### Issue tracker

Issues and specs are tracked in GitHub Issues on Open-Service-Initiative/OSI.Signal9, via the `gh` CLI. See `docs/agents/issue-tracker.md`.

### Triage labels

The default five labels: `needs-triage`, `needs-info`, `ready-for-agent`, `ready-for-human` and `wontfix`. See `docs/agents/triage-labels.md`.

### Domain docs

Single-context: `GLOSSARY.md` and `docs/adr/` at the repo root. See `docs/agents/domain.md`.
