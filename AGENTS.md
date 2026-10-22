# Signal9

Signal9 is a multi-tenant RMM platform. See `README.md` for the project layout, `GLOSSARY.md` for domain vocabulary, and `docs/adr/` for the architecture decisions the codebase is moving towards. Where today's code disagrees with an ADR, the ADR is the target.

## Principles

These are Open Service Initiative tenets. Hold every change to them:

- **API-first.** The public API is the product. Every dashboard capability must also be reachable through the API, by a third-party API application.
- **TDD.** Write the failing test first. See Testing below.
- **KISS.** Prefer the simplest design that meets the requirement.
- **Linear git history.** Rebase or squash; never merge commits.

## Projects

- `OSI.Signal9.API`: ASP.NET Core controller API plus SignalR hubs, using EF Core. The code still uses SQL Server; the target is PostgreSQL (ADR-0011).
- `OSI.Signal9.Web`: Blazor WebAssembly dashboard.
- `OSI.Signal9.Agent`: Windows Service on managed devices. It enrolls, sends heartbeats and telemetry, and runs commands.
- `OSI.Signal9.Contracts`: DTOs, enums and hub interfaces shared by the other projects. It has no server-side dependencies, because it ships in the WASM bundle.
- `OSI.Signal9.AppHost` / `OSI.Signal9.ServiceDefaults`: Aspire orchestration, used for local development and deployment.

## API conventions

These rules are Signal9's own, drawn from Google's AIP-122, AIP-159 and AIP-216 and Microsoft's Azure REST API guidelines; they don't claim full conformance with any of them. Local choices include: tenant views instead of AIP-159's wildcard parents, 409 (not AIP-216's 400) for a transition the current state doesn't allow, and kebab-case path segments. Where today's code differs, these conventions are the target.

### URLs

- Everything is under `/api/v1/` (major version only, #76). Collections are plural nouns.
- **One canonical URL per resource**, nested under its deepest *permanent* owner: `/devices/{deviceId}/alerts/{alertId}`, `/tenants/{tenantId}/scripts/{scriptId}`.
- Resources that can move or may have no owner are top-level: `/tenants/{id}` (tenant transfer), `/devices/{id}` (device transfer), `/users/{id}` (user transfer), `/agents/{id}` (an agent may await identification).
- **Views:** a collection of device-owned items can also be listed from the tenant, for example `/tenants/{tenantId}/alerts`. Tenant views take `includeDescendants` (default `true`), are read-only, and return each item's canonical URL. Changes always go to the canonical URL.
- Agents use their own `/agents/{agentId}/...` URLs only for what they send themselves (telemetry, monitor states, hardware) and for listing their own pending work. Each listed item carries its canonical URL, and agents claim and report on it there (`:claim`, `/result`).
- Responses always use canonical URLs, whatever URL was called.

### Methods and status codes

- GET returns 200.
- POST to a collection creates and returns 201 with a `Location` header (`CreatedAtRoute`).
- **PATCH** with `application/merge-patch+json` (RFC 7396) updates fields and returns 200. PUT only replaces a whole resource or a genuine sub-resource (an agent's command result), and is idempotent.
- DELETE returns 204.
- **Lifecycle transitions are actions, not field edits.** Status fields are read-only. Transitions are `POST {canonical-url}:{verb}` (`:acknowledge`, `:resolve`, `:cancel`, `:claim`), return 200, and return 409 if the transition isn't allowed from the current state. Don't model actions as sub-resources (`/cancellation`).
- Return an `ETag`, honour `If-Match`, and return 412 when it doesn't match.
- Errors are `application/problem+json`: 400 for validation, 404 when missing, 409 for state conflicts, 412 for failed preconditions. Never return 200 for a failure.

### Payloads

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
