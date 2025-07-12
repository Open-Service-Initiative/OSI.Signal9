using Signal9.Shared.Models;
using Signal9.Shared.DTOs;

namespace Signal9.Web.Services;

/// <summary>
/// Service interface for dashboard operations
/// </summary>
public interface IDashboardService
{
    Task<IEnumerable<TenantDto>> GetTenantsAsync();
    Task<IEnumerable<AgentDto>> GetDevicesAsync();
    Task<TenantDto?> GetTenantAsync(Guid tenantId);
    Task<AgentDto?> GetDeviceAsync(string agentId);
    Task<TenantDto> CreateTenantAsync(CreateTenantRequest request);
    Task<TenantDto> UpdateTenantAsync(Guid tenantId, UpdateTenantRequest request);
    Task DeleteTenantAsync(Guid tenantId);
    Task<AgentDto> CreateDeviceAsync(CreateAgentRequest request);
    Task<AgentDto> UpdateDeviceAsync(string agentId, UpdateAgentRequest request);
    Task DeleteDeviceAsync(string agentId);
}
