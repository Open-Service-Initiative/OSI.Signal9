using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Signal9.Shared.Data;
using Signal9.Shared.DTOs;
using Signal9.Shared.DTOs.Base;
using Signal9.Shared.DTOs.Core;
using Signal9.Shared.Interfaces;
using Signal9.Shared.Models;
using System.Security.Cryptography;
using System.Text;

namespace Signal9.Shared.Services;

/// <summary>
/// Agent service backed by Azure SQL (EF Core) and Azure Table Storage.
/// Optimized for tenant isolation, async EF patterns, and table storage for telemetry.
/// </summary>
public class AgentService(Signal9DbContext db, ITableStorageService tables, ILogger<AgentService> logger)
    : IAgentService
{
    private readonly ILogger<AgentService> _logger = logger;

    #region Queries

    public async Task<PagedResponse<AgentDto>> GetAgentsAsync(AgentQueryRequest request, CancellationToken cancellationToken = default)
    {
        var tenantId = request.ParentId != Guid.Empty
            ? request.ParentId
            : Guid.Empty;

        IQueryable<Agent> query = db.Agents.AsNoTracking();

        if (tenantId != Guid.Empty)
        {
            query = query.Where(a => a.ParentId == tenantId);
        }

        if (request.Status.HasValue)
        {
            query = query.Where(a => a.Status == request.Status.Value);
        }

        if (!string.IsNullOrWhiteSpace(request.Platform))
        {
            var platform = request.Platform.ToLowerInvariant();
            query = query.Where(a => a.OperatingSystem.Contains(platform, StringComparison.CurrentCultureIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var s = request.Search.ToLowerInvariant();
            query = query.Where(a => a.MachineName.Contains(s, StringComparison.CurrentCultureIgnoreCase) || a.IpAddress.Contains(s, StringComparison.CurrentCultureIgnoreCase));
        }

        // Sorting
        var sortBy = request.SortBy?.ToLowerInvariant() ?? "machinename";
        var sortDesc = string.Equals(request.SortOrder, "desc", StringComparison.OrdinalIgnoreCase);
        query = sortBy switch
        {
            "status" => sortDesc ? query.OrderByDescending(a => a.Status) : query.OrderBy(a => a.Status),
            "lastseen" => sortDesc ? query.OrderByDescending(a => a.LastSeen) : query.OrderBy(a => a.LastSeen),
            _ => sortDesc ? query.OrderByDescending(a => a.MachineName) : query.OrderBy(a => a.MachineName)
        };

        var totalCount = await query.CountAsync(cancellationToken);
        var page = Math.Max(1, request.Page);
        var pageSize = Math.Clamp(request.PageSize, 1, 1000);

        var items = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        var dtoItems = items.Select(MapToDto).ToList();

        return new PagedResponse<AgentDto>
        {
            Id = Guid.NewGuid(),
            Items = dtoItems,
            Page = page,
            PageSize = pageSize,
            TotalCount = totalCount
        };
    }

    public async Task<PagedResponse<object>> GetAgentsAsync(int page = 1, int pageSize = 50, CancellationToken cancellationToken = default)
    {
        var query = db.Agents.AsNoTracking().OrderBy(a => a.MachineName);
        var totalCount = await query.CountAsync(cancellationToken);
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 1000);
        var items = await query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);
        var dtoItems = items.Select(a => (object)MapToDto(a)).ToList();

        return new PagedResponse<object>
        {
            Id = Guid.NewGuid(),
            Items = dtoItems,
            Page = page,
            PageSize = pageSize,
            TotalCount = totalCount
        };
    }

    public async Task<AgentDto?> GetAgentByIdAsync(Guid agentId, bool includeMetrics = false, bool includeTelemetry = false, CancellationToken cancellationToken = default)
    {
        var entity = await db.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.Id == agentId, cancellationToken);
        return entity is null ? null : MapToDto(entity);
    }

    #endregion

    #region Mutations

    public async Task<AgentDto> RegisterAgentAsync(AgentDto agentDto, CancellationToken cancellationToken = default)
    {
        // Resolve tenant id
        if (agentDto.ParentId == Guid.Empty)
        {
            throw new ValidationException("ParentId (tenant) is required for agent registration");
        }

        var tenantId = agentDto.ParentId;
        var tenantExists = await db.Tenants.AsNoTracking().AnyAsync(t => t.Id == tenantId, cancellationToken);
        if (!tenantExists)
        {
            throw new InvalidOperationException("Tenant not found");
        }

        var entity = new Agent(tenantId)
        {
            MachineName = agentDto.MachineName,
            Domain = agentDto.Domain,
            OperatingSystem = agentDto.OperatingSystem,
            OSVersion = agentDto.OSVersion,
            Architecture = agentDto.Architecture,
            TotalMemoryMB = agentDto.TotalMemoryMB,
            ProcessorCores = agentDto.ProcessorCores,
            ProcessorName = agentDto.ProcessorName,
            IpAddress = agentDto.IpAddress,
            MacAddress = agentDto.MacAddress,
            FirstSeen = DateTime.UtcNow,
            LastSeen = DateTime.UtcNow,
            Status = agentDto.Status,
            Version = agentDto.Version,
            Tags = agentDto.Tags
        };

        db.Agents.Add(entity);
        await db.SaveChangesAsync(cancellationToken);

        return MapToDto(entity);
    }

    public async Task<AgentDto?> UpdateAgentAsync(Guid agentId, AgentUpdateRequest updateRequest, CancellationToken cancellationToken = default)
    {
        var entity = await db.Agents.FirstOrDefaultAsync(a => a.Id == agentId, cancellationToken);
        if (entity is null)
        {
            return null;
        }

        if (updateRequest.ParentId.HasValue && updateRequest.ParentId.Value != Guid.Empty)
        {
            entity.ParentId = (Guid)updateRequest.ParentId;
        }
        if (!string.IsNullOrWhiteSpace(updateRequest.MachineName)) entity.MachineName = updateRequest.MachineName;
        if (!string.IsNullOrWhiteSpace(updateRequest.IpAddress)) entity.IpAddress = updateRequest.IpAddress;
        if (!string.IsNullOrWhiteSpace(updateRequest.MacAddress)) entity.MacAddress = updateRequest.MacAddress;
        if (!string.IsNullOrWhiteSpace(updateRequest.OperatingSystem)) entity.OperatingSystem = updateRequest.OperatingSystem;
        if (!string.IsNullOrWhiteSpace(updateRequest.OSVersion)) entity.OSVersion = updateRequest.OSVersion;
        if (!string.IsNullOrWhiteSpace(updateRequest.Architecture)) entity.Architecture = updateRequest.Architecture;
        if (updateRequest.TotalMemoryMB.HasValue) entity.TotalMemoryMB = updateRequest.TotalMemoryMB.Value;
        if (updateRequest.ProcessorCores.HasValue) entity.ProcessorCores = updateRequest.ProcessorCores.Value;
        if (!string.IsNullOrWhiteSpace(updateRequest.ProcessorName)) entity.ProcessorName = updateRequest.ProcessorName;
        if (!string.IsNullOrWhiteSpace(updateRequest.Domain)) entity.Domain = updateRequest.Domain;
        if (!string.IsNullOrWhiteSpace(updateRequest.Version)) entity.Version = updateRequest.Version;
        if (!string.IsNullOrWhiteSpace(updateRequest.Tags)) entity.Tags = updateRequest.Tags;
        if (updateRequest.LastSeen.HasValue) entity.LastSeen = updateRequest.LastSeen.Value;
        if (updateRequest.Status.HasValue) entity.Status = updateRequest.Status.Value;
        entity.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(cancellationToken);
        return MapToDto(entity);
    }

    public async Task<bool> DeleteAgentAsync(Guid agentId, bool preserveData = false, CancellationToken cancellationToken = default)
    {
        var entity = await db.Agents.FirstOrDefaultAsync(a => a.Id == agentId, cancellationToken);
        if (entity is null) return false;

        if (preserveData)
        {
            entity.IsDeleted = true;
            entity.DeletedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        else
        {
            db.Agents.Remove(entity);
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
    }

    #endregion

    #region Telemetry & Commands

    public async Task<PagedResponse<TelemetryDataDto>> GetAgentTelemetryAsync(Guid agentId, DateTime? from = null, DateTime? to = null, int page = 1, int pageSize = 50, CancellationToken cancellationToken = default)
    {
        // Resolve tenant id from agent
        var agent = await db.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.Id == agentId, cancellationToken);
        if (agent is null)
        {
            return new PagedResponse<TelemetryDataDto>
            {
                Id = Guid.NewGuid(),
                Items = (List<TelemetryDataDto>)[],
                Page = page,
                PageSize = pageSize,
                TotalCount = 0
            };
        }

        var tenantId = agent.ParentId;
        var rows = await tables.GetTelemetryByAgentAsync(tenantId, agent.Id, from, to);
        // Client-side page since Azure Tables SDK returns async enumerable without server-side skip/take combining custom filter easily
        var ordered = rows.OrderByDescending(t => t.CreatedAt).ToList();
        var total = ordered.Count;
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 1000);
        var pageItems = ordered.Skip((page - 1) * pageSize).Take(pageSize).Select(MapTelemetryToDto).ToList();

        return new PagedResponse<TelemetryDataDto>
        {
            Id = Guid.NewGuid(),
            Items = pageItems,
            Page = page,
            PageSize = pageSize,
            TotalCount = total
        };
    }

    public async Task<PagedResponse<AgentCommandDto>> GetAgentCommandsAsync(Guid agentId, int page = 1, int pageSize = 50, CancellationToken cancellationToken = default)
    {
        var id = agentId.ToString();
        var query = db.AgentCommands.AsNoTracking().Where(c => c.AgentId == id).OrderByDescending(c => c.ScheduledAt);
        var totalCount = await query.CountAsync(cancellationToken);
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 1000);
        var items = await query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);
        var dto = items.Select(MapCommandToDto).ToList();

        return new PagedResponse<AgentCommandDto>
        {
            Id = Guid.NewGuid(),
            Items = dto,
            Page = page,
            PageSize = pageSize,
            TotalCount = totalCount
        };
    }

    #endregion

    #region Configuration

    public async Task<AgentConfigurationResponse> GenerateAgentConfigurationAsync(Guid agentId, CancellationToken cancellationToken = default)
    {
        // Ensure agent exists and get tenant
        var agent = await db.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.Id == agentId, cancellationToken)
            ?? throw new InvalidOperationException("Agent not found");

        var apiKey = GenerateApiKey();
        var hubUrl = ResolveHubUrl(agent.ParentId.ToString());
        var reporting = TimeSpan.FromMinutes(1);
        var configHash = ComputeHash($"{agent.Id}|{agent.ParentId}|{hubUrl}|{reporting}|{apiKey}");

        return new AgentConfigurationResponse
        {
            AgentId = agentId,
            ApiKey = apiKey,
            HubUrl = hubUrl,
            ReportingInterval = reporting,
            ConfigurationHash = configHash
        };
    }

    private static string GenerateApiKey()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes);
    }

    private static string ResolveHubUrl(string tenantId)
    {
        // Prefer environment configuration, fall back to conventional route
        var url = Environment.GetEnvironmentVariable("SIGNALR_HUB_URL");
        if (!string.IsNullOrWhiteSpace(url)) return url;
        var host = Environment.GetEnvironmentVariable("WEBSITE_HOSTNAME");
        if (!string.IsNullOrWhiteSpace(host)) return $"https://{host}/api";
        // Local default for dev
        return "http://localhost:7071/api";
    }

    private static string ComputeHash(string input)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash);
    }

    #endregion

    #region Mapping

    private static AgentDto MapToDto(Agent a)
    {
        return new AgentDto
        {
            Id = a.Id,
            ParentId = a.ParentId,
            MachineName = a.MachineName,
            Domain = a.Domain,
            OperatingSystem = a.OperatingSystem,
            OSVersion = a.OSVersion,
            Architecture = a.Architecture,
            TotalMemoryMB = a.TotalMemoryMB,
            ProcessorCores = a.ProcessorCores,
            ProcessorName = a.ProcessorName,
            IpAddress = a.IpAddress,
            MacAddress = a.MacAddress,
            FirstSeen = a.FirstSeen,
            LastSeen = a.LastSeen,
            Status = a.Status,
            Version = a.Version,
            Tags = a.Tags
        };
    }

    private static TelemetryDataDto MapTelemetryToDto(TelemetryData t)
    {
        return new TelemetryDataDto
        {
            Id = Guid.NewGuid(),
            //ParentId = null,
            AgentId = t.AgentId,
            TelemetryType = t.TelemetryType,
            CpuUsagePercent = t.CpuUsagePercent,
            MemoryUsageMB = t.MemoryUsageMB,
            AvailableMemoryMB = t.AvailableMemoryMB,
            DiskUsage = t.DiskUsage,
            NetworkInterfaces = t.NetworkInterfaces,
            ProcessCount = t.ProcessCount,
            UptimeSeconds = t.UptimeSeconds,
            LoadAverage = t.LoadAverage,
            TemperatureReadings = t.TemperatureReadings,
            CustomMetrics = t.CustomMetrics,
            ErrorMessage = t.ErrorMessage,
            CreatedAt = t.CreatedAt
        };
    }

    private static AgentCommandDto MapCommandToDto(AgentCommand c)
    {
        return new AgentCommandDto
        {
            Id = c.Id,
            //ParentId = null,
            AgentId = c.AgentId,
            CommandType = c.CommandType,
            Parameters = c.Parameters,
            Status = c.Status,
            ScheduledAt = c.ScheduledAt,
            StartedAt = c.StartedAt,
            CompletedAt = c.CompletedAt,
            Result = c.Result,
            ErrorMessage = c.ErrorMessage,
            InitiatedBy = c.InitiatedBy,
            TimeoutSeconds = c.TimeoutSeconds,
            Priority = c.Priority,
            CreatedAt = c.CreatedAt
        };
    }

    #endregion
}
