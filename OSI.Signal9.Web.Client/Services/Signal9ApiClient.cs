using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using OSI.Signal9.Contracts;
using OSI.Signal9.Contracts.Agents;
using OSI.Signal9.Contracts.ApiApplications;
using OSI.Signal9.Contracts.Audit;
using OSI.Signal9.Contracts.Commands;
using OSI.Signal9.Contracts.Devices;
using OSI.Signal9.Contracts.Enrollment;
using OSI.Signal9.Contracts.Roles;
using OSI.Signal9.Contracts.Serialization;
using OSI.Signal9.Contracts.Tenants;
using OSI.Signal9.Contracts.Users;
using OSI.Signal9.Web.Client.Auth;

namespace OSI.Signal9.Web.Client.Services;

/// <summary>
/// The parts of the Signal9 API the dashboard uses, called through web's proxy on the dashboard's own origin. A refusal
/// throws <see cref="ApiException"/>; a 401 also asks web again who is signed in, because it means the session ended,
/// unless it's RFC 9470's step-up challenge, which sends the person to sign in again with <c>max_age</c> (#120).
/// </summary>
public sealed class Signal9ApiClient(HttpClient http, BffAuthenticationStateProvider authentication, BffSession session)
{
    private const string MergePatch = "application/merge-patch+json";

    // Tenants

    public Task<PagedResult<TenantDto>> ListTenantsAsync(int page, int pageSize, CancellationToken cancellationToken = default) =>
        SendAsync<PagedResult<TenantDto>>(HttpMethod.Get, $"api/v1/tenants?page={page}&pageSize={pageSize}", null, cancellationToken);

    public Task<TenantDto> CreateTenantAsync(CreateTenantRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<TenantDto>(HttpMethod.Post, "api/v1/tenants", JsonContent.Create(request, options: Signal9Json.Options), cancellationToken);

    /// <summary>Sends every editable field as a JSON merge patch.</summary>
    public Task<TenantDto> UpdateTenantAsync(Guid tenantId, UpdateTenantRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<TenantDto>(HttpMethod.Patch, $"api/v1/tenants/{tenantId}", Patch(request), cancellationToken);

    /// <summary>A lifecycle action: <c>suspend</c>, <c>activate</c> or <c>cancel</c>.</summary>
    public Task<TenantDto> TransitionTenantAsync(Guid tenantId, string action, CancellationToken cancellationToken = default) =>
        SendAsync<TenantDto>(HttpMethod.Post, $"api/v1/tenants/{tenantId}:{action}", null, cancellationToken);

    public Task DeleteTenantAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
        SendAsync<object>(HttpMethod.Delete, $"api/v1/tenants/{tenantId}", null, cancellationToken);

    // Devices

    public Task<PagedResult<DeviceDto>> ListDevicesAsync(
        int page, int pageSize, DeviceStatus? status = null, string? search = null, CancellationToken cancellationToken = default)
    {
        var uri = $"api/v1/devices?page={page}&pageSize={pageSize}";
        if (status is not null)
            uri += $"&status={status}";
        if (!string.IsNullOrWhiteSpace(search))
            uri += $"&search={Uri.EscapeDataString(search.Trim())}";
        return SendAsync<PagedResult<DeviceDto>>(HttpMethod.Get, uri, null, cancellationToken);
    }

    /// <summary>Sends every editable field as a JSON merge patch.</summary>
    public Task<DeviceDto> UpdateDeviceAsync(Guid deviceId, UpdateDeviceRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<DeviceDto>(HttpMethod.Patch, $"api/v1/devices/{deviceId}", Patch(request), cancellationToken);

    public Task DeleteDeviceAsync(Guid deviceId, CancellationToken cancellationToken = default) =>
        SendAsync<object>(HttpMethod.Delete, $"api/v1/devices/{deviceId}", null, cancellationToken);

    public Task<CommandDto> QueueCommandAsync(Guid deviceId, CreateCommandRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<CommandDto>(HttpMethod.Post, $"api/v1/devices/{deviceId}/commands",
            JsonContent.Create(request, options: Signal9Json.Options), cancellationToken);

    // Agents

    /// <summary>The agents of a tenant and its descendants in <paramref name="status"/>, newest enrollment first.</summary>
    public Task<PagedResult<AgentDto>> ListAgentsAsync(Guid tenantId, AgentStatus status, int page, int pageSize,
        CancellationToken cancellationToken = default) =>
        SendAsync<PagedResult<AgentDto>>(HttpMethod.Get,
            $"api/v1/tenants/{tenantId}/agents?status={status}&page={page}&pageSize={pageSize}", null, cancellationToken);

    /// <summary>Approves the agent at its canonical <paramref name="url"/>, which then receives work.</summary>
    public Task<AgentDto> ApproveAgentAsync(string url, CancellationToken cancellationToken = default) =>
        SendAsync<AgentDto>(HttpMethod.Post, $"{url.TrimStart('/')}:approve", null, cancellationToken);

    /// <summary>The devices the agent at <paramref name="url"/>, which needs a decision, might be a reinstall of.</summary>
    public Task<PagedResult<DeviceDto>> ListCandidateDevicesAsync(string url, CancellationToken cancellationToken = default) =>
        SendAsync<PagedResult<DeviceDto>>(HttpMethod.Get, $"{url.TrimStart('/')}/candidate-devices?pageSize=100", null, cancellationToken);

    /// <summary>Decides which device the agent at <paramref name="url"/> belongs to (story 105).</summary>
    public Task<AgentDto> IdentifyAgentAsync(string url, IdentifyAgentRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<AgentDto>(HttpMethod.Post, $"{url.TrimStart('/')}:identify",
            JsonContent.Create(request, options: Signal9Json.Options), cancellationToken);

    // Users and role assignments

    public Task<PagedResult<UserDto>> ListUsersAsync(int page, int pageSize, CancellationToken cancellationToken = default) =>
        SendAsync<PagedResult<UserDto>>(HttpMethod.Get, $"api/v1/users?page={page}&pageSize={pageSize}", null, cancellationToken);

    public Task<PagedResult<RoleDto>> ListRolesAsync(CancellationToken cancellationToken = default) =>
        SendAsync<PagedResult<RoleDto>>(HttpMethod.Get, "api/v1/roles?pageSize=100", null, cancellationToken);

    /// <summary>The role assignments made at a tenant (not those inherited from above it).</summary>
    public Task<PagedResult<RoleAssignmentDto>> ListRoleAssignmentsAsync(Guid tenantId, int page, int pageSize,
        CancellationToken cancellationToken = default) =>
        SendAsync<PagedResult<RoleAssignmentDto>>(HttpMethod.Get,
            $"api/v1/tenants/{tenantId}/role-assignments?page={page}&pageSize={pageSize}", null, cancellationToken);

    public Task<RoleAssignmentDto> CreateRoleAssignmentAsync(Guid tenantId, CreateRoleAssignmentRequest request,
        CancellationToken cancellationToken = default) =>
        SendAsync<RoleAssignmentDto>(HttpMethod.Post, $"api/v1/tenants/{tenantId}/role-assignments",
            JsonContent.Create(request, options: Signal9Json.Options), cancellationToken);

    public Task DeleteRoleAssignmentAsync(Guid tenantId, Guid roleAssignmentId, CancellationToken cancellationToken = default) =>
        SendAsync<object>(HttpMethod.Delete, $"api/v1/tenants/{tenantId}/role-assignments/{roleAssignmentId}", null, cancellationToken);

    // Enrollment tokens

    /// <summary>The tenant's enrollment tokens and its descendants', newest first.</summary>
    public Task<PagedResult<EnrollmentTokenDto>> ListEnrollmentTokensAsync(Guid tenantId, int page, int pageSize,
        CancellationToken cancellationToken = default) =>
        SendAsync<PagedResult<EnrollmentTokenDto>>(HttpMethod.Get,
            $"api/v1/tenants/{tenantId}/enrollment-tokens?page={page}&pageSize={pageSize}", null, cancellationToken);

    /// <summary>Issues a token; the result carries its secret, the only time it's shown.</summary>
    public Task<EnrollmentTokenDto> CreateEnrollmentTokenAsync(Guid tenantId, CreateEnrollmentTokenRequest request,
        CancellationToken cancellationToken = default) =>
        SendAsync<EnrollmentTokenDto>(HttpMethod.Post, $"api/v1/tenants/{tenantId}/enrollment-tokens",
            JsonContent.Create(request, options: Signal9Json.Options), cancellationToken);

    /// <summary>A lifecycle action on the token at its canonical <paramref name="url"/>: <c>rotate</c> or <c>revoke</c>.</summary>
    public Task<EnrollmentTokenDto> TransitionEnrollmentTokenAsync(string url, string action, CancellationToken cancellationToken = default) =>
        SendAsync<EnrollmentTokenDto>(HttpMethod.Post, $"{url.TrimStart('/')}:{action}", null, cancellationToken);

    // Audit

    /// <summary>The audit entries the signed-in person can read, newest first.</summary>
    public Task<PagedResult<AuditEntryDto>> ListAuditEntriesAsync(int page, int pageSize, CancellationToken cancellationToken = default) =>
        SendAsync<PagedResult<AuditEntryDto>>(HttpMethod.Get, $"api/v1/audit-entries?page={page}&pageSize={pageSize}", null, cancellationToken);

    public Task<PagedResult<ApiApplicationDto>> ListApiApplicationsAsync(Guid tenantId, int page, int pageSize,
        CancellationToken cancellationToken = default) =>
        SendAsync<PagedResult<ApiApplicationDto>>(HttpMethod.Get,
            $"api/v1/tenants/{tenantId}/api-applications?page={page}&pageSize={pageSize}", null, cancellationToken);

    public Task<ApiApplicationDto> CreateApiApplicationAsync(Guid tenantId, CreateApiApplicationRequest request,
        CancellationToken cancellationToken = default) =>
        SendAsync<ApiApplicationDto>(HttpMethod.Post, $"api/v1/tenants/{tenantId}/api-applications",
            JsonContent.Create(request, options: Signal9Json.Options), cancellationToken);

    public Task DeleteApiApplicationAsync(Guid tenantId, Guid apiApplicationId, CancellationToken cancellationToken = default) =>
        SendAsync<object>(HttpMethod.Delete, $"api/v1/tenants/{tenantId}/api-applications/{apiApplicationId}", null, cancellationToken);

    public Task<PagedResult<ApiApplicationCredentialDto>> ListApiApplicationCredentialsAsync(Guid tenantId, Guid apiApplicationId,
        CancellationToken cancellationToken = default) =>
        SendAsync<PagedResult<ApiApplicationCredentialDto>>(HttpMethod.Get,
            $"api/v1/tenants/{tenantId}/api-applications/{apiApplicationId}/credentials?pageSize=100", null, cancellationToken);

    public Task<ApiApplicationCredentialDto> CreateApiApplicationCredentialAsync(Guid tenantId, Guid apiApplicationId,
        CreateApiApplicationCredentialRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<ApiApplicationCredentialDto>(HttpMethod.Post,
            $"api/v1/tenants/{tenantId}/api-applications/{apiApplicationId}/credentials",
            JsonContent.Create(request, options: Signal9Json.Options), cancellationToken);

    public Task DeleteApiApplicationCredentialAsync(Guid tenantId, Guid apiApplicationId, Guid credentialId,
        CancellationToken cancellationToken = default) =>
        SendAsync<object>(HttpMethod.Delete,
            $"api/v1/tenants/{tenantId}/api-applications/{apiApplicationId}/credentials/{credentialId}", null, cancellationToken);

    // Connected applications

    /// <summary>The applications connected to the signed-in person's account.</summary>
    public Task<PagedResult<ConnectionDto>> ListMyConnectionsAsync(int page, int pageSize, CancellationToken cancellationToken = default) =>
        SendAsync<PagedResult<ConnectionDto>>(HttpMethod.Get, $"api/v1/me/connections?page={page}&pageSize={pageSize}", null, cancellationToken);

    /// <summary>The connections to the applications registered at the tenant and its descendants.</summary>
    public Task<PagedResult<ConnectionDto>> ListTenantConnectionsAsync(Guid tenantId, int page, int pageSize,
        CancellationToken cancellationToken = default) =>
        SendAsync<PagedResult<ConnectionDto>>(HttpMethod.Get,
            $"api/v1/tenants/{tenantId}/connections?page={page}&pageSize={pageSize}", null, cancellationToken);

    /// <summary>Revokes the connection at its canonical <paramref name="url"/>.</summary>
    public Task RevokeConnectionAsync(string url, CancellationToken cancellationToken = default) =>
        SendAsync<object>(HttpMethod.Delete, url.TrimStart('/'), null, cancellationToken);

    private static JsonContent Patch<T>(T request) =>
        JsonContent.Create(request, new MediaTypeHeaderValue(MergePatch), Signal9Json.Options);

    private async Task<T> SendAsync<T>(HttpMethod method, string uri, HttpContent? content, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(method, uri) { Content = content };
        using var response = await http.SendAsync(message, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            return response.StatusCode == HttpStatusCode.NoContent
                ? default!
                : (await response.Content.ReadFromJsonAsync<T>(Signal9Json.Options, cancellationToken))!;
        }

        if (StepUpChallenge.MaxAge(response) is { } maxAge)
        {
            session.StepUp(maxAge);
            throw await ToExceptionAsync(response, sessionEnded: false, cancellationToken, steppingUp: true);
        }

        var sessionEnded = response.StatusCode == HttpStatusCode.Unauthorized && !await authentication.RefreshAsync();
        throw await ToExceptionAsync(response, sessionEnded, cancellationToken);
    }

    private static async Task<ApiException> ToExceptionAsync(
        HttpResponseMessage response, bool sessionEnded, CancellationToken cancellationToken, bool steppingUp = false)
    {
        Problem? problem = null;
        if (response.Content.Headers.ContentType?.MediaType is "application/problem+json" or "application/json")
        {
            try
            {
                problem = await response.Content.ReadFromJsonAsync<Problem>(Signal9Json.Options, cancellationToken);
            }
            catch (JsonException)
            {
                // Not problem details after all; fall back to the status line.
            }
        }

        var errors = problem?.Errors?.SelectMany(field => field.Value).ToList() ?? [];
        var title = problem?.Title ?? response.ReasonPhrase ?? $"HTTP {(int)response.StatusCode}";
        return new ApiException(response.StatusCode, title, problem?.Detail, errors, sessionEnded, steppingUp);
    }

    private sealed record Problem(string? Title, string? Detail, Dictionary<string, string[]>? Errors);
}
