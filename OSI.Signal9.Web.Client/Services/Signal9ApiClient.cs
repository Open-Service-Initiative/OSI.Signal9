using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using OSI.Signal9.Contracts;
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
