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

Signal9 is a **serverless-first RMM (Remote Monitoring and Management)** platform built with .NET 9, Azure Functions, and Blazor Server. The platform follows a **multi-tenant SaaS architecture** with strict tenant isolation.

### Core Components
- **Signal9.Web** - Blazor Server portal (port 7001) for management UI
- **Signal9.Web.Functions** - CRUD API backend (port 7072) for web portal
- **Signal9.Agent.Functions** - Agent communication API (port 7071) for telemetry/commands  
- **Signal9.Agent** - Client-side agent deployed on managed machines
- **Signal9.Shared** - Common DTOs, models, and interfaces

### Key Architectural Patterns

**Multi-Tenant Data Isolation**: Every DTO inherits from `TenantScopedDto` which includes `TenantId`. Never query data without tenant filtering.

**DTO-First Design**: All API contracts are defined in `Signal9.Shared/DTOs/` with comprehensive validation attributes. DTOs use records for immutability and inheritance hierarchy starting from `BaseDto`.

**Function App Separation**: Web functions handle user/dashboard operations while Agent functions handle telemetry collection and command execution. This separation enables independent scaling.

## Development Workflow

### Build & Test Commands
- Solution build: `dotnet build` or VS Code task `build-solution`
- Testing: `dotnet test --logger trx --collect:"XPlat Code Coverage"`

## Critical Development Patterns

### DTO Architecture Rules
1. **Always inherit from `TenantScopedDto`** for tenant-specific data
2. **Use validation attributes** extensively - see existing DTOs in `AgentDTOs.cs`, and `ValidationRules.md`
3. **Records over classes** for DTOs to ensure immutability
4. **Required properties** use `required` keyword, not nullable types

### Function Development
- **HTTP triggers use `AuthorizationLevel.Function`** for security
- **Route patterns**: Web functions use `/api/{resource}`, Agent functions use `/api/agents/{action}`
- **Error handling**: Return proper HTTP status codes with structured error responses
- **Logging**: Use `ILogger<T>` extensively for Application Insights integration

### Multi-Tenant Considerations
- **Never query without TenantId filtering** - this is enforced by `TenantScopedDto`
- **Tenant authentication** via `TenantCode` in agent registration flows
- **Tag-based organization** within tenants for device grouping

### SignalR Integration
Agent communication uses SignalR through `IAgentHub` and `IAgentClient` interfaces. Hub methods handle agent registration, telemetry, and command execution.

## File Organization Conventions

### DTO Structure
```
Signal9.Shared/DTOs/
├── Base/           # BaseDto, TenantScopedDto
├── AgentDTOs.cs    # Agent registration, telemetry
├── TenantAgentDTOs.cs  # Cross-tenant operations
├── {Domain}/       # Domain-specific DTOs (Analytics, Security, etc.)
```

### Function Organization
- **One function class per domain** (e.g., `TenantsFunctions`, `AgentsFunctions`)
- **OpenAPI documentation** via attributes for API discoverability
- **Consistent naming**: `{Action}{Resource}Async` methods

### Configuration Patterns
- **Local development**: Uses `local.settings.json` with `UseDevelopmentStorage=true`
- **Azure deployment**: Key Vault integration for secrets, Managed Identity for auth
- **Environment-specific**: `appsettings.{Environment}.json` pattern

## Common Implementation Tasks

### Adding New Telemetry Metrics
1. Update `TelemetryData` DTO in `AgentDTOs.cs`
2. Implement collection in `Signal9.Agent/Services/TelemetryCollector.cs`
3. Add processing logic in Agent Functions

### Adding New API Endpoints
1. Create DTO in appropriate domain folder
2. Add function method with proper attributes and validation
3. Follow existing patterns for error handling and response formatting

### Agent Command Implementation
1. Define command type in shared models
2. Implement execution in `AgentService.ExecuteCommandAsync`
3. Add SignalR hub method for command dispatch

Remember: This platform emphasizes **serverless scalability** and **strict multi-tenancy** - always consider these factors in any code changes.
