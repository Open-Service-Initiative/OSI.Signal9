# Signal9 RMM DTO Architecture Documentation

## Overview

The Signal9 RMM system uses a comprehensive Data Transfer Object (DTO) architecture to ensure type safety, validation consistency, and clear data contracts across all API endpoints and internal communications. This document provides a complete guide to understanding and working with the DTO system.

## Architecture Principles

### 1. Inheritance Hierarchy

The DTO system is built on a solid inheritance foundation:

```text
IBaseDto (interface)
├── BaseDto (abstract record)
    ├── PagedRequest
    ├── PagedResponse<T>
    └── TenantScopedDto (abstract record) + ITenantScoped
        ├── AgentRegistrationRequest
        ├── TelemetryData
        ├── AlertRequest
        ├── NetworkInterfaceInfo
        └── ... (all tenant-specific DTOs)
```

### 2. Base Interfaces and Classes

#### IBaseDto

- **Purpose**: Provides common timestamp functionality
- **Properties**: `DateTime Timestamp`
- **Usage**: All DTOs implement this interface directly or indirectly

#### BaseDto

- **Purpose**: Abstract base record implementing IBaseDto
- **Properties**: Auto-initialized `Timestamp` with `DateTime.UtcNow`
- **Usage**: Foundation for all concrete DTOs

#### ITenantScoped

- **Purpose**: Ensures multi-tenant isolation
- **Properties**: `Guid TenantId`
- **Usage**: All tenant-specific operations must implement this

#### TenantScopedDto

- **Purpose**: Combines BaseDto with ITenantScoped
- **Validation**: TenantId is required and validated
- **Usage**: Base for all tenant-specific DTOs

### 3. Pagination Support

#### PagedRequest

- **Purpose**: Standardized pagination parameters
- **Properties**:
  - `Page` (1-based, range: 1 to int.MaxValue)
  - `PageSize` (range: 1 to 1000)
  - `SortBy` (optional, validated property name)
  - `SortDescending` (boolean)

#### `PagedResponse<T>`

- **Purpose**: Standardized pagination response
- **Properties**:
  - `Items` (required, can be empty)
  - `Page`, `PageSize`, `TotalCount` (all validated)
  - Calculated: `TotalPages`, `HasNextPage`, `HasPreviousPage`

## DTO Categories

### 1. Agent Management DTOs (`AgentDTOs.cs`)

#### Agent Management Core DTOs

- **AgentRegistrationRequest**: Agent registration with comprehensive validation
- **TelemetryData**: Performance metrics with range validation
- **SystemInfo**: Detailed system information
- **AgentHeartbeatRequest**: Periodic status updates
- **AgentCommandRequest**: Command execution requests
- **AgentCommandResponse**: Command execution results
- **LogEntry**: Structured logging with level validation
- **AgentConfigurationResponse**: Agent configuration settings

#### Agent Management Key Validation Rules

- AgentId: Alphanumeric with hyphens/underscores, 1-100 chars
- Architecture: Must be x86, x64, ARM, ARM64, or IA64
- Version: Semantic versioning format required
- CPU/Memory Usage: 0-100% range validation
- Log Levels: Restricted to standard levels (Trace, Debug, etc.)

### 2. Analytics DTOs (`Analytics/AnalyticsDtos.cs`)

#### Analytics Core DTOs

- **DashboardAnalyticsResponse**: Comprehensive system metrics
- **KPIMetrics**: Key performance indicators with range validation
- **TimeSeriesDataPoint**: Time-based data points
- **TenantAnalyticsResponse**: Tenant-specific analytics
- **AgentAnalyticsResponse**: Agent-specific metrics

#### Analytics Key Validation Rules

- TimeRange: Predefined values (1h, 6h, 12h, 24h, 7d, 30d, 90d, 1y)
- Usage Percentages: 0-100% range validation
- Counts: Non-negative integers
- Timestamps: Required and validated

### 3. Asset Management DTOs (`Assets/AssetDtos.cs`)

#### Asset Management Core DTOs

- **SoftwareInventoryItem**: Installed software tracking
- **HardwareInventoryItem**: Hardware component information
- **LicenseInfo**: Software license management
- **AssetChangeEvent**: Change tracking and auditing
- **PatchStatusInfo**: Update and patch management

#### Asset Management Key Validation Rules

- Names/Titles: MaxLength validation (200-500 chars)
- Sizes: Non-negative values
- Categories: Enumerated values
- Paths: MaxLength 500 characters

### 4. Security DTOs (`Security/SecurityDtos.cs`)

#### Security Core DTOs

- **UserAccountRequest**: User management with email validation
- **RoleRequest**: Role-based access control
- **AuditLogEntry**: Security audit trails
- **ThreatDetectionAlert**: Security threat notifications
- **ComplianceReport**: Regulatory compliance tracking

#### Security Key Validation Rules

- Email: Valid email format required
- Usernames: 1-100 characters
- Phone: Valid phone format
- Security levels: Enumerated values

### 5. Monitoring DTOs (`Monitoring/MonitoringDtos.cs`)

#### Monitoring Core DTOs

- **AlertRequest**: Alert creation and management
- **AlertRuleRequest**: Automated alerting rules
- **NotificationRequest**: Notification configurations
- **SystemEvent**: System event tracking
- **PerformanceBaseline**: Performance benchmarking

#### Monitoring Key Validation Rules

- Alert titles: MaxLength 200
- Descriptions: MaxLength 1000
- Thresholds: Numeric validation
- Frequencies: Positive integers

### 6. Network DTOs (`Network/NetworkDtos.cs`)

#### Network Core DTOs

- **NetworkInterfaceInfo**: Network interface details
- **NetworkConfiguration**: Network settings
- **BandwidthUsageMetrics**: Network performance tracking
- **IPv4AddressInfo/IPv6AddressInfo**: IP address management

#### Network Key Validation Rules

- MAC addresses: 17 character limit
- IP addresses: Format validation
- Usage percentages: 0-100% range
- Speeds: Non-negative values

### 7. Remote Management DTOs (`RemoteManagement/RemoteManagementDtos.cs`)

#### Remote Management Core DTOs

- **RemoteSessionRequest**: Remote access sessions
- **ScreenshotRequest**: Screen capture functionality
- **FileTransferRequest**: File transfer operations
- **ScriptExecutionRequest**: Remote script execution

#### Remote Management Key Validation Rules

- Quality: 1-100 range for screenshots
- Timeouts: Positive integers
- Paths: MaxLength 500 characters
- Script content: MaxLength 100,000 characters

### 8. Common DTOs (`Common/CommonDtos.cs`)

#### Common Core DTOs

- **AgentResponse**: Agent information responses
- **ErrorResponse**: Standardized error handling
- **SuccessResponse**: Success confirmations
- **HealthCheckResponse**: System health monitoring

#### Common Key Validation Rules

- Standard field validations
- Consistent error message formats
- Health status enumerations

### 9. Tenant Management DTOs (`Tenants/TenantDtos.cs`)

#### Tenant Management Core DTOs

- **CreateTenantRequest**: New tenant creation
- **UpdateTenantRequest**: Tenant modifications
- **TenantResponse**: Tenant information
- **GetTenantsRequest**: Tenant queries

#### Tenant Management Key Validation Rules

- Names: Required, MaxLength 100
- Codes: MaxLength 20
- Email: Valid email format
- Phone: Valid phone format
- MaxAgents: Range 1-10,000

## Validation Framework

### 1. Validation Attributes Used

- **[Required]**: Ensures non-null values with custom error messages
- **[StringLength]**: Enforces minimum and maximum string lengths
- **[Range]**: Validates numeric ranges for integers and doubles
- **[RegularExpression]**: Pattern matching for specific formats
- **[EmailAddress]**: Email format validation
- **[Phone]**: Phone number format validation
- **[Url]**: URL format validation
- **[MaxLength]**: Maximum length constraints

### 2. Custom Validation Patterns

#### Semantic Versioning

```csharp
[RegularExpression(@"^\d+\.\d+\.\d+(\.\d+)?(-[a-zA-Z0-9\-\.]+)?$")]
```

#### Agent Identifiers

```csharp
[RegularExpression(@"^[a-zA-Z0-9\-_]+$")]
```

#### System Architecture

```csharp
[RegularExpression(@"^(x86|x64|ARM|ARM64|IA64)$")]
```

#### Log Levels

```csharp
[RegularExpression(@"^(Trace|Debug|Information|Warning|Error|Critical)$")]
```

### 3. Range Validations

- **Percentages**: 0-100 range for CPU, memory, disk usage
- **Counts**: Non-negative integers for agent counts, alert counts
- **Page Numbers**: 1-based with maximum limits
- **Page Sizes**: 1-1000 to prevent performance issues
- **Priorities**: -100 to 100 for command priorities

## Usage Guidelines

### 1. Creating New DTOs

1. **Inherit from appropriate base class**:
   - Use `BaseDto` for non-tenant-specific DTOs
   - Use `TenantScopedDto` for tenant-specific DTOs

2. **Add appropriate validation attributes**:

   ```csharp
   public record MyDto : TenantScopedDto
   {
       [Required(ErrorMessage = "Name is required")]
       [StringLength(100, MinimumLength = 1)]
       public required string Name { get; init; }
       
       [Range(0, 100, ErrorMessage = "Value must be between 0 and 100")]
       public double Value { get; init; }
   }
   ```

3. **Use descriptive XML documentation**:

   ```csharp
   /// <summary>
   /// Gets the name of the entity.
   /// Must be a non-empty string with maximum 100 characters.
   /// </summary>
   ```

### 2. Validation Best Practices

1. **Always provide meaningful error messages**
2. **Use appropriate data types** (records for immutability)
3. **Validate business rules** at the DTO level
4. **Keep validation close to the data definition**
5. **Use enums for restricted value sets**

### 3. API Integration

DTOs are designed to work seamlessly with ASP.NET Core:

```csharp
[HttpPost]
public async Task<IActionResult> CreateAgent([FromBody] AgentRegistrationRequest request)
{
    // Validation is automatically performed by model binding
    if (!ModelState.IsValid)
    {
        return BadRequest(ModelState);
    }
    
    // Process the validated request
    var response = await _agentService.RegisterAgent(request);
    return Ok(response);
}
```

## Performance Considerations

### 1. Record Types

- All DTOs use C# records for immutability and built-in equality
- Records provide value-based equality and efficient copying

### 2. Pagination

- `PagedResponse<T>` includes calculated properties to avoid repetitive calculations
- Page size limits prevent excessive data transfer

### 3. Validation Efficiency

- Validation attributes are processed at compile-time where possible
- Regular expressions are compiled for performance

## Testing Guidelines

### 1. Unit Testing DTOs

```csharp
[Test]
public void AgentRegistrationRequest_ShouldFailValidation_WhenAgentIdIsEmpty()
{
    var request = new AgentRegistrationRequest
    {
        AgentId = "", // Invalid
        TenantCode = "TENANT001",
        // ... other properties
    };
    
    var validationResults = ValidateModel(request);
    Assert.That(validationResults.Any(v => v.MemberNames.Contains(nameof(request.AgentId))));
}
```

### 2. Integration Testing

Ensure DTOs work correctly with the complete validation pipeline in API controllers.

## Migration and Versioning

### 1. Adding New Properties

- Use nullable properties for backward compatibility
- Provide default values where appropriate

### 2. Breaking Changes

- Increment API version numbers
- Maintain backward compatibility for at least one version
- Document all breaking changes

## Troubleshooting

### Common Validation Issues

1. **Required Field Errors**: Ensure all required properties are set
2. **Range Validation Failures**: Check that numeric values are within specified ranges
3. **String Length Violations**: Verify string properties don't exceed maximum lengths
4. **Format Validation Errors**: Ensure values match required patterns (email, phone, etc.)

### Debugging Tips

1. Use ModelState.IsValid in controllers to check validation status
2. Examine ModelState.Values for specific validation errors
3. Test DTOs in isolation using validation contexts
4. Use debugger to inspect validation attribute processing

## Conclusion

The Signal9 RMM DTO architecture provides a robust, type-safe, and well-validated foundation for all data operations. By following the established patterns and guidelines, developers can ensure consistency, reliability, and maintainability across the entire system.
