# Signal9 Web Functions API

This document describes the CRUD operations available in the Signal9 Web Functions for managing tenants and devices (agents).

## Base URL
All endpoints are accessible via the Azure Functions runtime at:
- Local development: `http://localhost:7071/api`
- Production: `https://{function-app-name}.azurewebsites.net/api`

## Tenant Management

### Get All Tenants
```http
GET /tenants?parentTenantId={guid}&tenantType={string}&isActive={bool}&page={int}&pageSize={int}
```

**Query Parameters:**
- `parentTenantId` (optional): Filter by parent tenant ID
- `tenantType` (optional): Filter by tenant type (Organization, Site, Department)
- `isActive` (optional): Filter by active status
- `page` (optional): Page number (default: 1)
- `pageSize` (optional): Items per page (default: 20, max: 100)

**Response:**
```json
{
  "tenants": [...],
  "page": 1,
  "pageSize": 20,
  "totalCount": 100,
  "totalPages": 5
}
```

### Get Tenant by ID
```http
GET /tenants/{tenantId}
```

### Create Tenant
```http
POST /tenants
Content-Type: application/json

{
  "name": "Acme Corp",
  "code": "ACME",
  "description": "Main organization",
  "parentTenantId": null,
  "tenantType": "Organization",
  "contactEmail": "admin@acme.com",
  "contactPhone": "+1-555-0123",
  "plan": "Professional",
  "maxAgents": 100
}
```

### Update Tenant
```http
PUT /tenants/{tenantId}
Content-Type: application/json

{
  "name": "Updated Name",
  "description": "Updated description",
  "maxAgents": 150,
  "isActive": true
}
```

### Delete Tenant (Soft Delete)
```http
DELETE /tenants/{tenantId}
```

## Device/Agent Management

### Get All Agents
```http
GET /agents?tenantId={guid}&status={string}&groupName={string}&page={int}&pageSize={int}
```

**Query Parameters:**
- `tenantId` (optional): Filter by tenant ID
- `status` (optional): Filter by status (Online, Offline, Maintenance, Error, Unregistered)
- `groupName` (optional): Filter by group name
- `page` (optional): Page number (default: 1)
- `pageSize` (optional): Items per page (default: 20, max: 100)

### Get Agents for Tenant
```http
GET /tenants/{tenantId}/agents?status={string}&page={int}&pageSize={int}
```

### Get Agent by ID
```http
GET /agents/{agentId}
```

### Create/Register Agent
```http
POST /agents
Content-Type: application/json

{
  "machineName": "WORKSTATION-01",
  "domain": "ACME.LOCAL",
  "operatingSystem": "Windows 11",
  "osVersion": "10.0.22631",
  "architecture": "x64",
  "totalMemoryMB": 16384,
  "processorCores": 8,
  "processorName": "Intel Core i7-12700",
  "ipAddress": "192.168.1.100",
  "macAddress": "00:11:22:33:44:55",
  "tenantId": "12345678-1234-1234-1234-123456789012",
  "groupName": "Workstations",
  "version": "1.0.0",
  "tags": {
    "department": "IT",
    "location": "Building A"
  },
  "customProperties": {
    "purchaseDate": "2023-01-15",
    "warranty": "3 years"
  }
}
```

### Update Agent
```http
PUT /agents/{agentId}
Content-Type: application/json

{
  "machineName": "WORKSTATION-01-UPDATED",
  "groupName": "Updated Group",
  "status": "Maintenance",
  "tags": {
    "department": "Sales"
  }
}
```

### Update Agent Status (Heartbeat)
```http
PATCH /agents/{agentId}/status
Content-Type: application/json

{
  "status": "Online"
}
```

### Delete Agent
```http
DELETE /agents/{agentId}
```

## Authentication

All endpoints require function-level authentication. Include the function key in the `x-functions-key` header or as a `code` query parameter.

## Response Status Codes

- `200 OK` - Successful GET/PUT operations
- `201 Created` - Successful POST operations
- `204 No Content` - Successful DELETE operations
- `400 Bad Request` - Invalid input data
- `401 Unauthorized` - Missing or invalid authentication
- `404 Not Found` - Resource not found
- `409 Conflict` - Resource conflict (e.g., trying to delete tenant with dependencies)
- `500 Internal Server Error` - Server error

## Data Models

### Tenant Plan Enum
- `Basic`
- `Professional`
- `Enterprise`
- `Custom`

### Agent Status Enum
- `Online`
- `Offline`
- `Maintenance`
- `Error`
- `Unregistered`

## Notes

- All endpoints support JSON request/response format
- Date/time values are in UTC format
- Pagination is zero-indexed (page 1 = first page)
- Soft deletion is used for tenants (sets `isActive` to false)
- Hard deletion is used for agents
- All GUID parameters must be valid UUIDs
- Database operations are currently stubbed with TODO comments for actual implementation

## Future Enhancements

The current implementation includes placeholders for:
- Entity Framework Core database integration
- Real-time SignalR notifications
- Bulk operations
- Advanced filtering and search
- Audit logging
- Role-based access control
