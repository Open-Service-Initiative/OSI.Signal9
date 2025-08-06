using Signal9.Shared.DTOs.Tenants;
using Signal9.Shared.DTOs;
using System.Text.Json;
using System.Text;
using Signal9.Shared.DTOs.Core;

namespace Signal9.Web.Services;

/// <summary>
/// Dashboard service implementation with Azure Functions integration
/// </summary>
public class DashboardService(
  HttpClient httpClient,
  ILogger<DashboardService> logger) : IDashboardService
{
  private readonly string _functionsBaseUrl = "/api";
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
      PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
      WriteIndented = true
    };

    // For Static Web Apps, use relative API paths that will be proxied

    public async Task<IEnumerable<TenantResponse>> GetTenantsAsync()
    {
        try
        {
            var response = await httpClient.GetAsync($"{_functionsBaseUrl}/tenants");
            
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync();
                var paginatedResponse = JsonSerializer.Deserialize<PaginatedResponse<TenantResponse>>(json, _jsonOptions);
                return paginatedResponse?.Items ?? new List<TenantResponse>();
            }
            
            logger.LogWarning("Failed to get tenants. Status: {StatusCode}", response.StatusCode);
            return new List<TenantResponse>();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error getting tenants");
            return new List<TenantResponse>();
        }
    }

    public async Task<IEnumerable<AgentDto>> GetDevicesAsync()
    {
        try
        {
            var response = await httpClient.GetAsync($"{_functionsBaseUrl}/agents");
            
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync();
                var paginatedResponse = JsonSerializer.Deserialize<PaginatedResponse<AgentDto>>(json, _jsonOptions);
                return paginatedResponse?.Items ?? new List<AgentDto>();
            }
            
            logger.LogWarning("Failed to get devices. Status: {StatusCode}", response.StatusCode);
            return new List<AgentDto>();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error getting devices");
            return new List<AgentDto>();
        }
    }

    public async Task<TenantResponse?> GetTenantAsync(Guid tenantId)
    {
        try
        {
            var response = await httpClient.GetAsync($"{_functionsBaseUrl}/tenants/{tenantId}");
            
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync();
                return JsonSerializer.Deserialize<TenantResponse>(json, _jsonOptions);
            }
            
            return null;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error getting tenant {TenantId}", tenantId);
            return null;
        }
    }

    public async Task<AgentDto?> GetDeviceAsync(string agentId)
    {
        try
        {
            var response = await httpClient.GetAsync($"{_functionsBaseUrl}/agents/{agentId}");
            
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync();
                return JsonSerializer.Deserialize<AgentDto>(json, _jsonOptions);
            }
            
            return null;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error getting device {AgentId}", agentId);
            return null;
        }
    }

    public async Task<TenantResponse> CreateTenantAsync(CreateTenantRequest request)
    {
        try
        {
            var json = JsonSerializer.Serialize(request, _jsonOptions);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            
            var response = await httpClient.PostAsync($"{_functionsBaseUrl}/tenants", content);
            response.EnsureSuccessStatusCode();
            
            var responseJson = await response.Content.ReadAsStringAsync();
            var tenant = JsonSerializer.Deserialize<TenantResponse>(responseJson, _jsonOptions);
            
            if (tenant == null)
                throw new InvalidOperationException("Failed to deserialize tenant response");
                
            return tenant;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error creating tenant");
            throw;
        }
    }

    public async Task<TenantResponse> UpdateTenantAsync(Guid tenantId, UpdateTenantRequest request)
    {
        try
        {
            var json = JsonSerializer.Serialize(request, _jsonOptions);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            
            var response = await httpClient.PutAsync($"{_functionsBaseUrl}/tenants/{tenantId}", content);
            response.EnsureSuccessStatusCode();
            
            var responseJson = await response.Content.ReadAsStringAsync();
            var tenant = JsonSerializer.Deserialize<TenantResponse>(responseJson, _jsonOptions);
            
            if (tenant == null)
                throw new InvalidOperationException("Failed to deserialize tenant response");
                
            return tenant;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error updating tenant {TenantId}", tenantId);
            throw;
        }
    }

    public async Task DeleteTenantAsync(Guid tenantId)
    {
        try
        {
            var response = await httpClient.DeleteAsync($"{_functionsBaseUrl}/tenants/{tenantId}");
            response.EnsureSuccessStatusCode();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error deleting tenant {TenantId}", tenantId);
            throw;
        }
    }

    public async Task<AgentDto> CreateDeviceAsync(AgentRegistrationRequest request)
    {
        try
        {
            var json = JsonSerializer.Serialize(request, _jsonOptions);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            
            var response = await httpClient.PostAsync($"{_functionsBaseUrl}/agents", content);
            response.EnsureSuccessStatusCode();
            
            var responseJson = await response.Content.ReadAsStringAsync();
            var agent = JsonSerializer.Deserialize<AgentDto>(responseJson, _jsonOptions);
            
            if (agent == null)
                throw new InvalidOperationException("Failed to deserialize agent response");
                
            return agent;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error creating device");
            throw;
        }
    }

    public async Task<AgentDto> UpdateDeviceAsync(string agentId, AgentUpdateRequest request)
    {
        try
        {
            var json = JsonSerializer.Serialize(request, _jsonOptions);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            
            var response = await httpClient.PutAsync($"{_functionsBaseUrl}/agents/{agentId}", content);
            response.EnsureSuccessStatusCode();
            
            var responseJson = await response.Content.ReadAsStringAsync();
            var agent = JsonSerializer.Deserialize<AgentDto>(responseJson, _jsonOptions);
            
            if (agent == null)
                throw new InvalidOperationException("Failed to deserialize agent response");
                
            return agent;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error updating device {AgentId}", agentId);
            throw;
        }
    }

    public async Task DeleteDeviceAsync(string agentId)
    {
        try
        {
            var response = await httpClient.DeleteAsync($"{_functionsBaseUrl}/agents/{agentId}");
            response.EnsureSuccessStatusCode();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error deleting device {AgentId}", agentId);
            throw;
        }
    }
}
