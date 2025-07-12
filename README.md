# Signal9 - Remote Monitoring and Management Platform

A modern RMM (Remote Monitoring and Management) platform built with .NET 9, Blazor Server, and Azure Functions.

## Architecture

- **Signal9.Agent**: Windows service that runs on client machines to collect telemetry and execute commands
- **Signal9.Agent.Functions**: Azure Functions app for agent communication and management  
- **Signal9.Web**: Blazor Server web portal for system management and monitoring
- **Signal9.Web.Functions**: Azure Functions app providing CRUD APIs for the web portal
- **Signal9.Shared**: Common models, DTOs, and interfaces shared across all projects

## Getting Started

### Prerequisites
- .NET 9 SDK
- Azure Functions Core Tools v4
- Azure Storage Emulator or Azure Storage Account

### Running the Platform

#### Option 1: PowerShell Script (Recommended)
```powershell
# Starts all platform services with coordinated startup
.\start-platform.ps1
```

#### Option 2: VS Code Compound Launch
Use the "Launch Signal9 Platform" configuration which starts:
- Signal9.Web (Blazor Server) on https://localhost:7001
- Signal9.Web.Functions (CRUD API) on http://localhost:7072  
- Signal9.Agent.Functions (Agent API) on http://localhost:7071

#### Option 3: Visual Studio Multiple Startup Projects
The solution is configured for multiple startup projects. In Visual Studio:
1. Right-click the solution → "Configure Startup Projects"
2. Select "Multiple startup projects" 
3. Set Signal9.Web, Signal9.Web.Functions, and Signal9.Agent.Functions to "Start"

#### Option 4: Individual Services
```bash
# Web Functions (port 7072)
cd src/Signal9.Web.Functions
func start --port 7072

# Agent Functions (port 7071)  
cd src/Signal9.Agent.Functions
func start --port 7071

# Web Portal (port 7001)
cd src/Signal9.Web
dotnet run
```

### Agent Deployment (Separate)
The Signal9.Agent is designed to be deployed separately on client machines:
```bash
dotnet run --project src/Signal9.Agent
```

## Platform Services

### Web Portal (https://localhost:7001)
- Blazor Server application with Bootstrap 5 UI
- Real-time dashboard with SignalR integration
- Tenant and device management interface

### Web Functions API (http://localhost:7072/api)
- **GET /tenants** - List all tenants
- **POST /tenants** - Create new tenant
- **PUT /tenants/{id}** - Update tenant
- **DELETE /tenants/{id}** - Delete tenant
- **GET /agents** - List all agents/devices
- **POST /agents** - Register new agent
- **PUT /agents/{id}** - Update agent
- **DELETE /agents/{id}** - Delete agent

### Agent Functions API (http://localhost:7071/api)
- Agent communication endpoints
- Command execution API
- Telemetry collection endpoints

## Development

### Building
```bash
# Build entire solution
dotnet build --configuration Release

# Or use VS Code task
Ctrl+Shift+P → "Tasks: Run Task" → "build-solution"
```

### Testing
```bash
dotnet test --configuration Release --logger trx --collect:"XPlat Code Coverage"
```

### Debugging
- Use compound launch "Launch Signal9 Platform" for full debugging
- Individual service debugging available for each project
- Agent runs separately for real-world testing scenarios

## Docker Support

```bash
# Build web portal container
docker build -f src/Signal9.Web/Dockerfile -t signal9-web .

# Or use VS Code task
Ctrl+Shift+P → "Tasks: Run Task" → "docker-build-webportal"
```

## Azure Deployment

### Full Platform Deployment
```bash
azd up
```

### Infrastructure Only
```bash
azd provision
```

### Code Deployment Only
```bash
azd deploy
```

The platform includes full Bicep infrastructure templates for production Azure deployment with:
- Azure Container Apps for web services
- Azure Functions for serverless APIs
- Azure Storage for telemetry data
- Application Insights for monitoring

## Platform Architecture

```
┌─────────────────┐    ┌──────────────────┐    ┌─────────────────┐
│   Blazor Web    │    │  Web Functions   │    │ Agent Functions │
│   Port: 7001    │◄──►│   Port: 7072     │    │   Port: 7071    │
│                 │    │                  │    │                 │
│ - Dashboard     │    │ - Tenant CRUD    │    │ - Agent Comms   │
│ - Real-time UI  │    │ - Device CRUD    │    │ - Commands      │
│ - SignalR       │    │ - Bootstrap API  │    │ - Telemetry     │
└─────────────────┘    └──────────────────┘    └─────────────────┘
                                                        ▲
                                                        │
                                              ┌─────────────────┐
                                              │  Remote Agents  │
                                              │                 │
                                              │ - System Info   │
                                              │ - Monitoring    │
                                              │ - Commands      │
                                              └─────────────────┘
```
   ```bash
   azd up
   ```

3. **Configure SQL password when prompted**

## Configuration

### Agent Configuration (appsettings.json)

```json
{
  "AgentConfiguration": {
    "HubUrl": "https://your-hub-url/agentHub",
    "TenantCode": "your-tenant",
    "GroupName": "default",
    "HeartbeatInterval": 30,
    "TelemetryInterval": 60,
    "ReconnectDelay": 5,
    "MaxReconnectAttempts": 10
  }
}
```

### Hub Configuration

The hub automatically configures itself using Azure services when deployed to Azure:
- SignalR connection from Azure SignalR Service
- Key Vault for secrets
- Application Insights for monitoring

## Project Structure

```
Signal9/
├── src/
│   ├── Signal9.Agent/          # RMM Agent console application
│   ├── Signal9.Hub/            # SignalR Hub web service
│   ├── Signal9.WebPortal/      # Management web application
│   ├── Signal9.Functions/      # Azure Functions
│   └── Signal9.Shared/         # Shared libraries
├── infra/                      # Bicep infrastructure templates
├── tests/                      # Unit and integration tests
├── docs/                       # Documentation
├── azure.yaml                  # Azure Developer CLI configuration
└── Signal9.sln                 # Solution file
```

## Development

### Adding New Telemetry Metrics

1. Update `TelemetryDto` in `Signal9.Shared/DTOs/AgentDTOs.cs`
2. Implement collection logic in `TelemetryCollector.cs`
3. Update the hub to handle new metrics

### Adding New Agent Commands

1. Define command type in `AgentCommand.cs`
2. Implement execution logic in `AgentService.ExecuteCommandAsync`
3. Add command dispatching in the hub or web portal

### Database Migrations

Entity Framework migrations are handled automatically during deployment.

## Monitoring

- **Application Insights** - Performance monitoring and diagnostics
- **Azure Monitor** - Infrastructure monitoring
- **Log Analytics** - Centralized logging
- **Azure Alerts** - Proactive notifications

## Security Considerations

- All inter-service communication uses Managed Identity
- Secrets are stored in Azure Key Vault
- Network traffic is encrypted with TLS
- SQL connections use Azure AD authentication
- RBAC controls access to Azure resources

## Contributing

1. Fork the repository
2. Create a feature branch
3. Make your changes
4. Add tests if applicable
5. Submit a pull request

## License

This project is licensed under the MIT License - see the LICENSE file for details.

## Support

For issues and questions:
- Create an issue in the repository
- Review the documentation in the `/docs` folder
- Check Azure Monitor logs for runtime issues
