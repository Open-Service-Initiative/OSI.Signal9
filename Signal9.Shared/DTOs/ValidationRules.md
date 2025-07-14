# Signal9 RMM DTO Validation Rules

## Overview

This document provides a comprehensive reference for all validation rules applied to DTOs in the Signal9 RMM system. It serves as both a development guide and troubleshooting reference for understanding validation requirements and constraints.

## Base DTO Validation Rules

### BaseDto

- **Timestamp**: Auto-populated with `DateTime.UtcNow`, no validation required

### TenantScopedDto

- **TenantId**:
  - **Required**: Must be provided for all tenant-scoped operations
  - **Type**: Valid GUID
  - **Error Message**: "TenantId is required for proper multi-tenant isolation"

### PagedRequest

- **Page**:
  - **Range**: 1 to int.MaxValue
  - **Default**: 1
  - **Error Message**: "Page must be greater than 0"
- **PageSize**:
  - **Range**: 1 to 1000
  - **Default**: 50
  - **Error Message**: "PageSize must be between 1 and 1000"
- **SortBy**:
  - **Optional**: Can be null
  - **MaxLength**: 100 characters
  - **Pattern**: Must match valid property name pattern (`^[a-zA-Z][a-zA-Z0-9_\.]*$`)
  - **Error Messages**:
    - "SortBy cannot exceed 100 characters"
    - "SortBy must be a valid property name"

### `PagedResponse<T>`

- **Items**:
  - **Required**: Cannot be null (but can be empty)
  - **Error Message**: "Items collection is required"
- **Page**:
  - **Range**: 1 to int.MaxValue
  - **Error Message**: "Page must be greater than 0"
- **PageSize**:
  - **Range**: 1 to 1000
  - **Error Message**: "PageSize must be between 1 and 1000"
- **TotalCount**:
  - **Range**: 0 to int.MaxValue
  - **Error Message**: "TotalCount cannot be negative"

## Agent Management DTO Validation Rules

### AgentRegistrationRequest

- **AgentId**:
  - **Required**: Yes
  - **Length**: 1-100 characters
  - **Pattern**: Alphanumeric, hyphens, and underscores only (`^[a-zA-Z0-9\-_]+$`)
  - **Error Messages**:
    - "AgentId is required"
    - "AgentId must be between 1 and 100 characters"
    - "AgentId can only contain alphanumeric characters, hyphens, and underscores"
- **TenantCode**:
  - **Required**: Yes
  - **Length**: 1-50 characters
  - **Error Messages**:
    - "TenantCode is required"
    - "TenantCode must be between 1 and 50 characters"
- **MachineName**:
  - **Required**: Yes
  - **Length**: 1-100 characters
  - **Error Messages**:
    - "MachineName is required"
    - "MachineName must be between 1 and 100 characters"
- **OperatingSystem**:
  - **Required**: Yes
  - **Length**: 1-100 characters
  - **Error Messages**:
    - "OperatingSystem is required"
    - "OperatingSystem must be between 1 and 100 characters"
- **Architecture**:
  - **Required**: Yes
  - **Length**: 1-20 characters
  - **Valid Values**: x86, x64, ARM, ARM64, IA64
  - **Error Messages**:
    - "Architecture is required"
    - "Architecture must be between 1 and 20 characters"
    - "Architecture must be one of: x86, x64, ARM, ARM64, IA64"
- **Version**:
  - **Required**: Yes
  - **Length**: 1-50 characters
  - **Pattern**: Semantic version format (`^\d+\.\d+\.\d+(\.\d+)?(-[a-zA-Z0-9\-\.]+)?$`)
  - **Error Messages**:
    - "Version is required"
    - "Version must be between 1 and 50 characters"
    - "Version must be in semantic version format (e.g., 1.0.0 or 1.0.0-beta)"

### TelemetryData

- **AgentId**:
  - **Required**: Yes
  - **Length**: 1-100 characters
  - **Error Messages**:
    - "AgentId is required"
    - "AgentId must be between 1 and 100 characters"
- **TenantCode**:
  - **Required**: Yes
  - **Length**: 1-50 characters
  - **Error Messages**:
    - "TenantCode is required"
    - "TenantCode must be between 1 and 50 characters"
- **CpuUsage**:
  - **Range**: 0.0 to 100.0
  - **Error Message**: "CpuUsage must be between 0 and 100"
- **MemoryUsage**:
  - **Range**: 0.0 to 100.0
  - **Error Message**: "MemoryUsage must be between 0 and 100"
- **DiskUsage**:
  - **Range**: 0.0 to 100.0
  - **Error Message**: "DiskUsage must be between 0 and 100"
- **NetworkIn**:
  - **Range**: 0.0 to double.MaxValue
  - **Error Message**: "NetworkIn cannot be negative"
- **NetworkOut**:
  - **Range**: 0.0 to double.MaxValue
  - **Error Message**: "NetworkOut cannot be negative"
- **SystemInfo**:
  - **Required**: Yes
  - **Error Message**: "SystemInfo is required"

### SystemInfo

- **OperatingSystem**:
  - **Required**: Yes
  - **Length**: 1-200 characters
  - **Error Messages**:
    - "OperatingSystem is required"
    - "OperatingSystem must be between 1 and 200 characters"
- **Architecture**:
  - **Required**: Yes
  - **Length**: 1-20 characters
  - **Valid Values**: x86, x64, ARM, ARM64, IA64
  - **Error Messages**:
    - "Architecture is required"
    - "Architecture must be between 1 and 20 characters"
    - "Architecture must be one of: x86, x64, ARM, ARM64, IA64"
- **ProcessorCount**:
  - **Range**: 1 to 256
  - **Error Message**: "ProcessorCount must be between 1 and 256"
- **TotalMemory**:
  - **Range**: 1 to long.MaxValue
  - **Error Message**: "TotalMemory must be greater than 0"
- **AvailableMemory**:
  - **Range**: 0 to long.MaxValue
  - **Error Message**: "AvailableMemory cannot be negative"
- **MachineName**:
  - **Required**: Yes
  - **Length**: 1-100 characters
  - **Error Messages**:
    - "MachineName is required"
    - "MachineName must be between 1 and 100 characters"
- **UserName**:
  - **Required**: Yes
  - **Length**: 1-100 characters
  - **Error Messages**:
    - "UserName is required"
    - "UserName must be between 1 and 100 characters"

### DriveInfo

- **Name**:
  - **Required**: Yes
  - **Length**: 1-50 characters
  - **Error Messages**:
    - "Drive Name is required"
    - "Drive Name must be between 1 and 50 characters"
- **DriveType**:
  - **Required**: Yes
  - **Length**: 1-50 characters
  - **Error Messages**:
    - "DriveType is required"
    - "DriveType must be between 1 and 50 characters"
- **TotalSize**:
  - **Range**: 0 to long.MaxValue
  - **Error Message**: "TotalSize cannot be negative"
- **AvailableSpace**:
  - **Range**: 0 to long.MaxValue
  - **Error Message**: "AvailableSpace cannot be negative"

### AgentHeartbeatRequest

- **AgentId**:
  - **Required**: Yes
  - **Length**: 1-100 characters
  - **Error Messages**:
    - "AgentId is required"
    - "AgentId must be between 1 and 100 characters"
- **TenantCode**:
  - **Required**: Yes
  - **Length**: 1-50 characters
  - **Error Messages**:
    - "TenantCode is required"
    - "TenantCode must be between 1 and 50 characters"
- **Status**:
  - **Required**: Yes
  - **Type**: Valid AgentStatus enum value
  - **Error Message**: "Status is required"

### AgentCommandRequest

- **CommandId**:
  - **Required**: Yes
  - **Length**: 1-100 characters
  - **Error Messages**:
    - "CommandId is required"
    - "CommandId must be between 1 and 100 characters"
- **CommandType**:
  - **Required**: Yes
  - **Length**: 1-100 characters
  - **Error Messages**:
    - "CommandType is required"
    - "CommandType must be between 1 and 100 characters"
- **Priority**:
  - **Range**: -100 to 100
  - **Default**: 0
  - **Error Message**: "Priority must be between -100 and 100"

### AgentCommandResponse

- **CommandId**:
  - **Required**: Yes
  - **Length**: 1-100 characters
  - **Error Messages**:
    - "CommandId is required"
    - "CommandId must be between 1 and 100 characters"
- **Status**:
  - **Required**: Yes
  - **Length**: 1-50 characters
  - **Error Messages**:
    - "Status is required"
    - "Status must be between 1 and 50 characters"
- **Result**:
  - **Optional**: Can be null
  - **MaxLength**: 10,000 characters
  - **Error Message**: "Result cannot exceed 10,000 characters"
- **ErrorMessage**:
  - **Optional**: Can be null
  - **MaxLength**: 2,000 characters
  - **Error Message**: "ErrorMessage cannot exceed 2,000 characters"

### LogEntry

- **Level**:
  - **Required**: Yes
  - **Length**: 1-20 characters
  - **Valid Values**: Trace, Debug, Information, Warning, Error, Critical
  - **Error Messages**:
    - "Level is required"
    - "Level must be between 1 and 20 characters"
    - "Level must be one of: Trace, Debug, Information, Warning, Error, Critical"
- **Source**:
  - **Required**: Yes
  - **Length**: 1-100 characters
  - **Error Messages**:
    - "Source is required"
    - "Source must be between 1 and 100 characters"
- **Message**:
  - **Required**: Yes
  - **Length**: 1-5,000 characters
  - **Error Messages**:
    - "Message is required"
    - "Message must be between 1 and 5,000 characters"
- **Exception**:
  - **Optional**: Can be null
  - **MaxLength**: 10,000 characters
  - **Error Message**: "Exception details cannot exceed 10,000 characters"

### AgentConfigurationResponse

- **AgentId**:
  - **Required**: Yes
  - **Type**: Valid GUID
  - **Error Message**: "AgentId is required"
- **ApiKey**:
  - **Required**: Yes
  - **Length**: 32-500 characters
  - **Error Messages**:
    - "ApiKey is required"
    - "ApiKey must be between 32 and 500 characters"
- **HubUrl**:
  - **Required**: Yes
  - **Length**: 10-500 characters
  - **Format**: Valid URL
  - **Error Messages**:
    - "HubUrl is required"
    - "HubUrl must be between 10 and 500 characters"
    - "HubUrl must be a valid URL"
- **ReportingInterval**:
  - **Required**: Yes
  - **Type**: Valid TimeSpan
  - **Error Message**: "ReportingInterval is required"
- **ConfigurationHash**:
  - **Required**: Yes
  - **Length**: 32-128 characters
  - **Pattern**: Hexadecimal string (`^[a-fA-F0-9]+$`)
  - **Error Messages**:
    - "ConfigurationHash is required"
    - "ConfigurationHash must be between 32 and 128 characters"
    - "ConfigurationHash must be a valid hexadecimal string"

## Analytics DTO Validation Rules

### DashboardAnalyticsResponse

- **TimeRange**:
  - **Required**: Yes
  - **Length**: 1-20 characters
  - **Valid Values**: 1h, 6h, 12h, 24h, 7d, 30d, 90d, 1y
  - **Error Messages**:
    - "TimeRange is required"
    - "TimeRange must be between 1 and 20 characters"
    - "TimeRange must be one of: 1h, 6h, 12h, 24h, 7d, 30d, 90d, 1y"
- **KPIs**:
  - **Required**: Yes
  - **Error Message**: "KPIs are required"
- **AgentStatus**:
  - **Required**: Yes
  - **Error Message**: "AgentStatus is required"
- **Performance**:
  - **Required**: Yes
  - **Error Message**: "Performance metrics are required"
- **Alerts**:
  - **Required**: Yes
  - **Error Message**: "Alerts summary is required"
- **UsageTrends**:
  - **Required**: Yes
  - **Error Message**: "UsageTrends are required"
- **Geographic**:
  - **Required**: Yes
  - **Error Message**: "Geographic distribution is required"

### KPIMetrics

- **TotalAgents**:
  - **Range**: 0 to int.MaxValue
  - **Error Message**: "TotalAgents cannot be negative"
- **OnlineAgents**:
  - **Range**: 0 to int.MaxValue
  - **Error Message**: "OnlineAgents cannot be negative"
- **OfflineAgents**:
  - **Range**: 0 to int.MaxValue
  - **Error Message**: "OfflineAgents cannot be negative"
- **AverageCpuUsage**:
  - **Range**: 0.0 to 100.0
  - **Error Message**: "AverageCpuUsage must be between 0 and 100"
- **AverageMemoryUsage**:
  - **Range**: 0.0 to 100.0
  - **Error Message**: "AverageMemoryUsage must be between 0 and 100"
- **TotalAlerts**:
  - **Range**: 0 to int.MaxValue
  - **Error Message**: "TotalAlerts cannot be negative"
- **CriticalAlerts**:
  - **Range**: 0 to int.MaxValue
  - **Error Message**: "CriticalAlerts cannot be negative"

### TimeSeriesDataPoint

- **Timestamp**:
  - **Required**: Yes
  - **Type**: Valid DateTime
  - **Error Message**: "Timestamp is required"
- **Value**:
  - **Required**: Yes
  - **Type**: Finite double (not NaN or infinity)
  - **Error Message**: "Value is required"

## Tenant Management DTO Validation Rules

### CreateTenantRequest

- **Name**:
  - **Required**: Yes
  - **Length**: 1-100 characters
  - **Error Messages**:
    - "Name is required"
    - "Name cannot exceed 100 characters"
- **Code**:
  - **Optional**: Can be null
  - **MaxLength**: 20 characters
  - **Error Message**: "Code cannot exceed 20 characters"
- **Description**:
  - **Optional**: Can be null
  - **MaxLength**: 500 characters
  - **Error Message**: "Description cannot exceed 500 characters"
- **TenantType**:
  - **Optional**: Can be null
  - **MaxLength**: 50 characters
  - **Error Message**: "TenantType cannot exceed 50 characters"
- **ContactEmail**:
  - **Optional**: Can be null
  - **Format**: Valid email address
  - **Error Message**: "Invalid email format"
- **ContactPhone**:
  - **Optional**: Can be null
  - **Format**: Valid phone number
  - **Error Message**: "Invalid phone format"
- **MaxAgents**:
  - **Range**: 1 to 10,000
  - **Default**: 10
  - **Error Message**: "MaxAgents must be between 1 and 10000"

### UpdateTenantRequest

- **Name**:
  - **Optional**: Can be null
  - **MaxLength**: 100 characters
  - **Error Message**: "Name cannot exceed 100 characters"
- **Code**:
  - **Optional**: Can be null
  - **MaxLength**: 20 characters
  - **Error Message**: "Code cannot exceed 20 characters"
- **Description**:
  - **Optional**: Can be null
  - **MaxLength**: 500 characters
  - **Error Message**: "Description cannot exceed 500 characters"
- **ContactEmail**:
  - **Optional**: Can be null
  - **Format**: Valid email address
  - **Error Message**: "Invalid email format"
- **ContactPhone**:
  - **Optional**: Can be null
  - **Format**: Valid phone number
  - **Error Message**: "Invalid phone format"
- **MaxAgents**:
  - **Optional**: Can be null
  - **Range**: 1 to 10,000
  - **Error Message**: "MaxAgents must be between 1 and 10000"

## Common Validation Patterns

### Percentage Values

- **Range**: 0.0 to 100.0
- **Usage**: CPU usage, memory usage, disk usage, utilization percentages
- **Error Message**: "[PropertyName] must be between 0 and 100"

### Non-negative Counts

- **Range**: 0 to int.MaxValue
- **Usage**: Agent counts, alert counts, file counts
- **Error Message**: "[PropertyName] cannot be negative"

### Non-negative Long Values

- **Range**: 0 to long.MaxValue
- **Usage**: Byte counts, file sizes, memory values
- **Error Message**: "[PropertyName] cannot be negative"

### String Length Patterns

- **Short Identifiers**: 1-50 characters (codes, names)
- **Medium Text**: 1-200 characters (titles, descriptions)
- **Long Text**: 1-1,000 characters (detailed descriptions)
- **Very Long Text**: 1-10,000 characters (content, results, exceptions)

### Required GUID Fields

- **Type**: System.Guid
- **Validation**: Automatic (non-nullable Guid is always valid)
- **Usage**: IDs, tenant identifiers, agent identifiers

## Error Handling Guidelines

### Validation Error Response Format

```json
{
  "type": "https://tools.ietf.org/html/rfc7231#section-6.5.1",
  "title": "One or more validation errors occurred.",
  "status": 400,
  "errors": {
    "PropertyName": [
      "Error message describing the validation failure"
    ]
  }
}
```

### Common HTTP Status Codes for Validation

- **400 Bad Request**: Validation failures, malformed requests
- **422 Unprocessable Entity**: Business rule violations
- **409 Conflict**: Constraint violations (duplicate keys, etc.)

## Testing Validation Rules

### Unit Testing Example

```csharp
[Test]
public void Should_Fail_Validation_When_Required_Field_Missing()
{
    // Arrange
    var dto = new AgentRegistrationRequest
    {
        AgentId = "", // Invalid - empty string
        TenantCode = "VALID_CODE",
        // ... other valid properties
    };

    // Act
    var validationResults = ValidateModel(dto);

    // Assert
    Assert.That(validationResults.Count, Is.GreaterThan(0));
    Assert.That(validationResults.Any(v => 
        v.MemberNames.Contains(nameof(dto.AgentId)) && 
        v.ErrorMessage.Contains("AgentId is required")));
}
```

### Integration Testing

Ensure validation works correctly in the complete request pipeline by testing API endpoints with invalid data and verifying proper error responses.

## Troubleshooting Common Issues

### 1. "Required field" errors

- **Cause**: Property marked as `required` but not set or set to null/empty
- **Solution**: Ensure all required properties have valid values

### 2. "Range validation" errors

- **Cause**: Numeric values outside specified range
- **Solution**: Check min/max values in validation attributes

### 3. "String length" errors

- **Cause**: Strings too short or too long
- **Solution**: Verify string length constraints

### 4. "Format validation" errors

- **Cause**: Values don't match required patterns (email, phone, regex)
- **Solution**: Ensure values match expected formats

### 5. "Enum validation" errors

- **Cause**: Invalid enum values
- **Solution**: Use only defined enum values

This validation rules reference should be updated whenever new DTOs are added or existing validation rules are modified.
