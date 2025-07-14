using Signal9.Shared.DTOs.Tenants;
using Signal9.Shared.DTOs;

namespace Signal9.Web.Services;

/// <summary>
/// Service interface for dashboard operations
/// </summary>
public interface IDashboardService
{
    // Tenant operations
    Task<IEnumerable<TenantResponse>> GetTenantsAsync();
    Task<TenantResponse?> GetTenantAsync(Guid tenantId);
    Task<TenantResponse> CreateTenantAsync(CreateTenantRequest request);
    Task<TenantResponse> UpdateTenantAsync(Guid tenantId, UpdateTenantRequest request);
    Task DeleteTenantAsync(Guid tenantId);

    // Device/Agent operations
    Task<IEnumerable<AgentDto>> GetDevicesAsync();
    Task<AgentDto?> GetDeviceAsync(string agentId);
    Task<AgentDto> CreateDeviceAsync(AgentRegistrationRequest request);
    Task<AgentDto> UpdateDeviceAsync(string agentId, AgentUpdateRequest request);
    Task DeleteDeviceAsync(string agentId);
}
