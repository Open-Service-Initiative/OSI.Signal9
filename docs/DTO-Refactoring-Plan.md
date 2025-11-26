# Signal9 RMM System - DTO Refactoring Plan

## Executive Summary

This document outlines a comprehensive refactoring plan for the Signal9 RMM system's Data Transfer Objects (DTOs). The plan addresses current architectural issues, implements modern C# features, and establishes a scalable foundation for multi-tenant operations.

## Current State Analysis

### Identified Issues

1. **Duplicate DTOs**: Multiple definitions exist across different files (TenantAgentDTOs.cs and WebFunctionDTOs.cs)
2. **Inconsistent Multi-tenancy**: Mixed use of TenantId (Guid) and TenantCode (string)
3. **No Inheritance Hierarchy**: DTOs lack proper base classes and shared interfaces
4. **Missing Modern C# Features**: No use of records, required properties, or collection expressions
5. **Incomplete Coverage**: Missing DTOs for analytics, monitoring, security, and system management
6. **Poor Validation**: Limited use of data annotations and validation attributes
7. **No Tenant Isolation Pattern**: Lack of interfaces to enforce tenant-scoped operations

## New DTO Architecture Design

### 1. Core Base Classes and Interfaces

```csharp
// Base Interfaces
namespace Signal9.Shared.DTOs.Core;

/// <summary>
/// Interface for all tenant-scoped DTOs
/// </summary>
public interface ITenantScoped
{
    Guid TenantId { get; }
}

/// <summary>
/// Interface for timestamped DTOs
/// </summary>
public interface ITimestamped
{
    DateTime CreatedAt { get; }
    DateTime? UpdatedAt { get; }
}

/// <summary>
/// Interface for auditable DTOs
/// </summary>
public interface IAuditable : ITimestamped
{
    string? CreatedBy { get; }
    string? UpdatedBy { get; }
}

/// <summary>
/// Base class for all DTOs
/// </summary>
public abstract record BaseDto
{
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Base class for tenant-scoped DTOs
/// </summary>
public abstract record TenantScopedDto : BaseDto, ITenantScoped
{
    public required Guid TenantId { get; init; }
}

/// <summary>
/// Base class for paginated requests
/// </summary>
public record PaginationRequest
{
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 50;
    public string? SortBy { get; init; }
    public bool SortDescending { get; init; }
}

/// <summary>
/// Generic paginated response
/// </summary>
public record PaginatedResponse<T>
{
    public required IReadOnlyList<T> Items { get; init; }
    public required int Page { get; init; }
    public required int PageSize { get; init; }
    public required int TotalCount { get; init; }
    public int TotalPages => (int)Math.Ceiling(TotalCount / (double)PageSize);
}
```

### 2. Directory Structure

```
src/Signal9.Shared/DTOs/
├── Core/
│   ├── BaseDto.cs
│   ├── Interfaces.cs
│   └── Common.cs
├── Agents/
│   ├── AgentRegistrationDto.cs
│   ├── AgentStatusDto.cs
│   ├── AgentCommandDto.cs
│   └── AgentTelemetryDto.cs
├── Tenants/
│   ├── TenantDto.cs
│   ├── TenantRequestDto.cs
│   └── TenantHierarchyDto.cs
├── Analytics/
│   ├── DashboardAnalyticsDto.cs
│   ├── PerformanceMetricsDto.cs
│   ├── ReportDto.cs
│   └── BenchmarkDto.cs
├── Monitoring/
│   ├── AlertDto.cs
│   ├── HealthStatusDto.cs
│   ├── MetricDto.cs
│   └── ThresholdDto.cs
├── Security/
│   ├── AuditLogDto.cs
│   ├── UserDto.cs
│   ├── RoleDto.cs
│   └── PermissionDto.cs
├── System/
│   ├── SystemConfigDto.cs
│   ├── BackupDto.cs
│   ├── MaintenanceDto.cs
│   └── SystemStatsDto.cs
└── RemoteManagement/
    ├── RemoteSessionDto.cs
    ├── FileTransferDto.cs
    ├── ScriptExecutionDto.cs
    └── PowerManagementDto.cs
```

## Multi-tenancy Strategy

### 1. Consistent Tenant Identification

```csharp
// All tenant-scoped DTOs will implement ITenantScoped
public interface ITenantScoped
{
    Guid TenantId { get; }
}

// Extension methods for tenant validation
public static class TenantExtensions
{
    public static void ValidateTenantAccess(this ITenantScoped dto, Guid userTenantId)
    {
        if (dto.TenantId != userTenantId)
            throw new UnauthorizedAccessException("Access denied to different tenant's data");
    }
}
```

### 2. Tenant Isolation Patterns

- **Request DTOs**: All request DTOs requiring tenant context will inherit from `TenantScopedDto`
- **Response DTOs**: Will include tenant information where relevant
- **Validation**: Automatic tenant validation through middleware/filters
- **Query Filtering**: All queries automatically filtered by tenant context

### 3. Hierarchical Tenant Support

```csharp
public record TenantHierarchyDto : TenantScopedDto
{
    public required string Name { get; init; }
    public string? Code { get; init; }
    public Guid? ParentTenantId { get; init; }
    public required int Level { get; init; }
    public required string HierarchyPath { get; init; }
    public IReadOnlyList<TenantHierarchyDto> ChildTenants { get; init; } = [];
}
```

## Modern C# Feature Implementation

### 1. Record Types

All DTOs will be converted to records for:
- Immutability by default
- Value equality
- Built-in ToString() implementation
- With-expressions support

### 2. Required Properties

```csharp
public record AgentRegistrationDto : TenantScopedDto
{
    public required string MachineName { get; init; }
    public required string OperatingSystem { get; init; }
    public required string Version { get; init; }
    // Optional properties don't use required
    public string? Architecture { get; init; }
}
```

### 3. Primary Constructors (C# 12)

```csharp
public record MetricDto(
    string Name,
    double Value,
    DateTime Timestamp,
    Guid TenantId) : TenantScopedDto
{
    public Dictionary<string, object> Tags { get; init; } = [];
}
```

### 4. Collection Expressions

```csharp
public record DashboardAnalyticsDto : TenantScopedDto
{
    public required KpiDto[] Kpis { get; init; } = [];
    public required MetricDto[] Metrics { get; init; } = [];
    public required AlertSummaryDto[] Alerts { get; init; } = [];
}
```

### 5. Nullable Reference Types

All DTOs will have nullable reference types enabled with proper annotations:

```csharp
#nullable enable

public record UpdateAgentRequest : TenantScopedDto
{
    public string? Description { get; init; }
    public AgentStatus? Status { get; init; }
    public Dictionary<string, object>? Tags { get; init; }
}
```

## Missing DTO Specifications

### 1. Analytics DTOs

```csharp
namespace Signal9.Shared.DTOs.Analytics;

public record DashboardAnalyticsDto : TenantScopedDto
{
    public required string TimeRange { get; init; }
    public required KpiMetricsDto KPIs { get; init; }
    public required AgentStatusDistributionDto AgentStatus { get; init; }
    public required PerformanceMetricsDto Performance { get; init; }
    public required AlertSummaryDto Alerts { get; init; }
    public required UsageTrendsDto UsageTrends { get; init; }
    public ComparisonDataDto? Comparisons { get; init; }
}

public record KpiMetricsDto
{
    public required int TotalAgents { get; init; }
    public required int OnlineAgents { get; init; }
    public required int TotalTenants { get; init; }
    public required int ActiveAlerts { get; init; }
    public required double AverageResponseTime { get; init; }
    public required double SystemUptime { get; init; }
}
```

### 2. Monitoring & Alerting DTOs

```csharp
namespace Signal9.Shared.DTOs.Monitoring;

public record AlertDto : TenantScopedDto, IAuditable
{
    public required Guid AlertId { get; init; }
    public required AlertSeverity Severity { get; init; }
    public required string Type { get; init; }
    public required string Message { get; init; }
    public required string Source { get; init; }
    public Guid? AgentId { get; init; }
    public required AlertStatus Status { get; init; }
    public DateTime? AcknowledgedAt { get; init; }
    public string? AcknowledgedBy { get; init; }
    public DateTime? ResolvedAt { get; init; }
    public string? ResolvedBy { get; init; }
    public Dictionary<string, object> Context { get; init; } = [];
}

public enum AlertSeverity
{
    Information,
    Warning,
    Error,
    Critical
}

public enum AlertStatus
{
    Active,
    Acknowledged,
    Resolved,
    Suppressed
}
```

### 3. Security & Compliance DTOs

```csharp
namespace Signal9.Shared.DTOs.Security;

public record AuditLogDto : TenantScopedDto
{
    public required Guid AuditId { get; init; }
    public required string Action { get; init; }
    public required string Resource { get; init; }
    public required string UserId { get; init; }
    public required string UserName { get; init; }
    public string? IpAddress { get; init; }
    public required bool Success { get; init; }
    public string? FailureReason { get; init; }
    public Dictionary<string, object> Details { get; init; } = [];
}

public record ComplianceReportDto : TenantScopedDto
{
    public required string ReportType { get; init; }
    public required DateTime GeneratedAt { get; init; }
    public required ComplianceStatus Status { get; init; }
    public required ComplianceCheckDto[] Checks { get; init; }
}
```

### 4. Remote Management DTOs

```csharp
namespace Signal9.Shared.DTOs.RemoteManagement;

public record RemoteSessionDto : TenantScopedDto
{
    public required Guid SessionId { get; init; }
    public required Guid AgentId { get; init; }
    public required string UserId { get; init; }
    public required SessionType Type { get; init; }
    public required DateTime StartedAt { get; init; }
    public DateTime? EndedAt { get; init; }
    public required SessionStatus Status { get; init; }
}

public record ScriptExecutionDto : TenantScopedDto
{
    public required Guid ExecutionId { get; init; }
    public required string ScriptName { get; init; }
    public required string ScriptContent { get; init; }
    public required Guid[] TargetAgentIds { get; init; }
    public Dictionary<string, object> Parameters { get; init; } = [];
    public required ExecutionStatus Status { get; init; }
    public ScriptResultDto[] Results { get; init; } = [];
}
```

## Migration Strategy

### Phase 1: Foundation (Week 1-2)
1. Create new directory structure
2. Implement base classes and interfaces
3. Set up shared validation attributes
4. Configure serialization settings

### Phase 2: Core DTOs (Week 2-3)
1. Migrate Agent DTOs with new inheritance
2. Migrate Tenant DTOs with hierarchy support
3. Update existing function endpoints
4. Add comprehensive unit tests

### Phase 3: New Feature DTOs (Week 3-4)
1. Implement Analytics DTOs
2. Create Monitoring & Alerting DTOs
3. Add Security & Compliance DTOs
4. Build Remote Management DTOs

### Phase 4: Integration (Week 4-5)
1. Update all Azure Functions to use new DTOs
2. Implement DTO mapping/conversion layer
3. Add OpenAPI documentation attributes
4. Update client SDKs

### Phase 5: Cleanup (Week 5-6)
1. Remove old DTO files
2. Update all references
3. Performance testing
4. Documentation updates

## Backward Compatibility

### 1. Versioning Strategy
- Maintain v1 DTOs temporarily in `DTOs.Legacy` namespace
- Implement adapter pattern for conversion
- Use API versioning headers

### 2. Conversion Layer

```csharp
public static class DtoConverter
{
    public static TNew Convert<TOld, TNew>(TOld oldDto) 
        where TOld : class 
        where TNew : class
    {
        // Automated mapping logic
    }
}
```

### 3. Deprecation Timeline
- Month 1-2: Both old and new DTOs supported
- Month 3: Deprecation warnings added
- Month 4-6: Old DTOs removed

## Testing Approach

### 1. Unit Tests
- Validation attribute tests
- Serialization/deserialization tests
- Tenant isolation tests
- Conversion tests

### 2. Integration Tests
- Function endpoint tests with new DTOs
- Multi-tenant scenario tests
- Performance benchmarks

### 3. Contract Tests
- OpenAPI schema validation
- Client SDK compatibility
- Breaking change detection

## Success Criteria

1. **Zero Breaking Changes**: Existing clients continue to work
2. **Performance**: No degradation in serialization performance
3. **Type Safety**: Full nullable reference type coverage
4. **Tenant Isolation**: 100% of tenant-scoped operations validated
5. **Documentation**: Complete OpenAPI specifications
6. **Test Coverage**: >90% coverage on all DTOs

## Risk Mitigation

1. **Risk**: Breaking existing clients
   - **Mitigation**: Comprehensive contract testing and gradual rollout

2. **Risk**: Performance degradation
   - **Mitigation**: Benchmark testing and optimization

3. **Risk**: Complex migration
   - **Mitigation**: Phased approach with rollback capability

4. **Risk**: Incomplete tenant isolation
   - **Mitigation**: Automated validation and security testing

## Conclusion

This refactoring plan provides a comprehensive approach to modernizing the Signal9 RMM system's DTO architecture. By implementing proper inheritance, multi-tenancy patterns, and modern C# features, we'll create a more maintainable, secure, and scalable foundation for the platform's continued growth.

The phased migration approach ensures minimal disruption while delivering immediate benefits in code quality and developer experience.