# Signal9 API Integration

This document describes the successful integration between the Signal9 Web Portal and the Functions API.

## Overview

The Signal9 RMM system now has a fully functional API integration where:

- **Web Portal** (Blazor Server): Running at `https://localhost:7001/`
- **Functions API**: Running at `http://localhost:7072/api`
- **Real-time Dashboard**: Shows live data from the API
- **OpenAPI Documentation**: Available at `/swagger`, `/openapi.json`, and `/openapi.yaml`

## Integration Components

- HTTP client-based service for API communication
- Handles pagination responses from Functions API
- Configured to use `WebFunctionsUrl` from appsettings.json
- Implements all CRUD operations for tenants and agents

### 2. Functions API (`DashboardFunctions.cs`)

- Provides REST endpoints for tenant and agent management
- Returns paginated responses with proper JSON serialization
- Includes mock data for development/testing
- Authorization level set to `Function` for security

### 3. OpenAPI Documentation

- **Interactive Swagger UI**: `http://localhost:7072/api/swagger`
- **JSON Specification**: `http://localhost:7072/api/openapi.json`
- **YAML Specification**: `http://localhost:7072/api/openapi.yaml`
- **Static Files Generator**: `POST /api/openapi/generate`

## API Endpoints

### Tenants

- `GET /api/tenants` - Get all tenants (with filtering and pagination)
- `GET /api/tenants/{id}` - Get specific tenant
- `POST /api/tenants` - Create new tenant
- `PUT /api/tenants/{id}` - Update tenant
- `DELETE /api/tenants/{id}` - Delete tenant

### Agents/Devices

- `GET /api/agents` - Get all agents (with filtering and pagination)
- `GET /api/agents/{id}` - Get specific agent
- `POST /api/agents` - Create new agent
- `PUT /api/agents/{id}` - Update agent
- `DELETE /api/agents/{id}` - Delete agent
- `PATCH /api/agents/{id}/status` - Update agent status

### Documentation

- `GET /api/swagger` - Interactive Swagger UI
- `GET /api/openapi.json` - OpenAPI JSON specification
- `GET /api/openapi.yaml` - OpenAPI YAML specification
- `POST /api/openapi/generate` - Generate static spec files

## Configuration

### Web Portal (`appsettings.json`)

```json
{
  "WebFunctionsUrl": "https://localhost:7072"
}
```

### Functions (`local.settings.json`)

```json
{
  "Host": {
    "LocalHttpPort": 7072,
    "CORS": "*"
  }
}
```

## Data Flow

1. **Dashboard Load**: Web portal calls `GET /api/tenants` and `GET /api/agents`
2. **API Response**: Functions return paginated data with mock tenant/agent information
3. **Dashboard Update**: Real-time counts and device lists are displayed
4. **Error Handling**: Graceful fallback to empty state if API is unavailable

## Mock Data

The API currently returns realistic mock data including:

### Sample Tenant

```json
{
  "id": "guid",
  "name": "Demo Organization",
  "code": "DEMO",
  "tenantType": "Organization",
  "plan": "Professional",
  "maxAgents": 100,
  "isActive": true,
  "agentCount": 5
}
```

### Sample Agent

```json
{
  "id": "agent-001",
  "name": "Development Workstation",
  "machineName": "DEV-WS-01",
  "operatingSystem": "Windows 11 Pro",
  "status": "Online",
  "isOnline": true,
  "lastSeen": "2025-07-12T17:28:00Z"
}
```

## Development Workflow

1. **Start Functions**: `func start` in `src/Signal9.Web.Functions`
2. **Start Web Portal**: `dotnet run` in `src/Signal9.Web`
3. **View Dashboard**: Navigate to `https://localhost:7001/`
4. **API Documentation**: Visit `http://localhost:7072/api/swagger`
5. **Generate Specs**: `POST` to `/api/openapi/generate`

## Next Steps

- Replace mock data with actual database integration
- Add authentication/authorization
- Implement real-time SignalR updates
- Add comprehensive error handling
- Extend API coverage for all RMM operations

## Generated Files

Static OpenAPI specifications are automatically generated in:

- `docs/openapi.json` - JSON format
- `docs/openapi.yaml` - YAML format

These files are version-controlled and updated via the `/api/openapi/generate` endpoint.
