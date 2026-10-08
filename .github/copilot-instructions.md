# Signal9 RMM Platform - AI Coding Instructions

## Interaction Protocol

## Tool Usage Guidelines - CRITICALLY IMPORTANT, USE TOOLS RELIGIOUSLY

As a large language model, you have access to a variety of tools that can assist in coding tasks. Use these tools to enhance your coding experience and ensure best practices are followed. You are also extremely dumb and not nearly as effective without them.

### Memory and Context Management
- **User Identification**: Always attempt to identify the user you're working with via the native IDE tools
- **Memory Retrieval**: Begin interactions by saying "Remembering..." and retrieving relevant information from memory
- **Information Tracking**: Capture and store:
   - Identity details (role, preferences, experience level)
   - Behaviors (coding patterns, tool preferences)
   - Goals (project objectives, feature requirements)
   - Relationships (team members, external dependencies)
   - Decisions (design choices, architectural patterns)
   - Observations (performance metrics, user feedback)
   - Complex problems (bugs, architectural challenges)
- **Memory Updates**: After each interaction, update memory with new entities, relations, and observations

### Azure Development Workflow
- **Always call `azure_development-get_code_gen_best_practices`** before generating Azure-related code
- **Use `azure_development-get_deployment_best_practices`** when preparing deployments
- **Call `azure_development-get_azure_function_code_gen_best_practices`** for Function Apps
- **Leverage `azure_check_predeploy`** before infrastructure deployment

### Research and Documentation
- **Context7**: Query for library documentation with `mcp_context7_resolve-library-id` then `mcp_context7_get-library-docs`
- **Microsoft Docs**: Use `mcp_microsoft-doc_microsoft_docs_search` for official Azure/Microsoft guidance (especially .NET)
- **Web Search**: Use `vscode-websearchforcopilot_webSearch` for wider web searches when needed

### Problem Solving
- **Sequential Thinking**: Use `mcp_sequentialthi_sequentialthinking` for complex architectural decisions
- **Memory Management**: Store insights with `mcp_memory_add_observation` and retrieve with `mcp_memory_search_entities`

### Testing and Validation
- **Playwright**: Use browser automation tools for UI testing
- **Error Checking**: Always run `get_errors` after code changes
- **Task Execution**: Use `run_vs_code_task` for build and test operations

### File Operations
- **Read First**: Use `read_file` or `semantic_search` before editing
- **Targeted Edits**: Use `replace_string_in_file` for precise changes
- **Bulk Changes**: Use `insert_edit_into_file` for larger modifications

## Architecture Overview

Signal9 is a multi-tenant RMM platform. See README.md for the project layout.

- `OSI.Signal9.API`: ASP.NET Core controller API (strict REST) plus SignalR hubs, EF Core on Azure SQL. Hosted on Azure Container Apps.
- `OSI.Signal9.Web`: Blazor WebAssembly dashboard, hosted on Azure Static Web Apps.
- `OSI.Signal9.Agent`: Windows Service on managed machines. Enrolls, heartbeats, sends telemetry, runs commands.
- `OSI.Signal9.Contracts`: DTOs, enums and hub interfaces shared by all three. No server-side dependencies (it ships in the WASM bundle).
- `OSI.Signal9.AppHost` / `OSI.Signal9.ServiceDefaults`: Aspire orchestration for local development and deployment.

## API Conventions

- Resource-oriented routes under `/api`, plural nouns, nested only for ownership (`/api/agents/{agentId}/commands`).
- GET 200, POST 201 with a `Location` header (`CreatedAtRoute`), PUT replaces and returns 200, DELETE returns 204.
- State changes that aren't CRUD are modeled as sub-resources (`PUT .../commands/{id}/result`, `PUT .../cancellation`), not verbs in the URL.
- Errors are `application/problem+json`: 400 validation, 404 missing, 409 state conflicts. Never return 200 for a failure.
- Collections take `?page=&pageSize=` (max 100) and return `PagedResult<T>`. Don't name an action parameter `page`; it breaks query binding.
- Validate requests with data annotations on the contract types; validation attributes on positional records go on the constructor parameters.
- Enums are serialized as strings everywhere via `Signal9Json`.
- Inject `TimeProvider`; never call `DateTime.UtcNow` directly, so tests can control time.

## Data

- EF Core with SQL Server. Change the model, then add a migration: `dotnet ef migrations add <Name> --project OSI.Signal9.API --output-dir Data/Migrations`.
- Store UTC `DateTime` in entities; expose `DateTimeOffset` in contracts.
- Agent status is derived (maintenance flag + last heartbeat), not stored.

## Testing

- `OSI.Signal9.API.Tests` hosts the API in memory against SQLite with a `FakeTimeProvider`. Add a test for every new endpoint covering its status codes.
- Run with `dotnet test --solution OSI.Signal9.slnx`.

## Not Built Yet

Authentication (Entra ID for users, enrollment secrets and credentials for agents), tenant isolation per user, and remote script execution. Look for `TODO(auth)` markers. Do not add commands that execute arbitrary code until agents are authenticated and commands are signed.
