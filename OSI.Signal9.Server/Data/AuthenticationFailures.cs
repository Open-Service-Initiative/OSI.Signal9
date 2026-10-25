using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OSI.Signal9.Contracts.Audit;

namespace OSI.Signal9.Server.Data;

public sealed class RepeatedFailureOptions
{
    public const string SectionName = "Audit:RepeatedAuthenticationFailures";

    /// <summary>How many failures from one client address, within <see cref="Window"/>, make an audit entry.</summary>
    [Range(1, 10_000)]
    public int Threshold { get; set; } = 5;

    public TimeSpan Window { get; set; } = TimeSpan.FromMinutes(15);
}

/// <summary>
/// Counts failed authentications by client address (the one forwarded headers trusted, story 129) and records a
/// deployment-level audit entry each time one address reaches the threshold within the window, so brute-force attempts
/// are visible (story 121) without an entry per failure. Counts are kept per host instance.
/// </summary>
public sealed class AuthenticationFailures(TimeProvider time, IOptions<RepeatedFailureOptions> options)
{
    private const int MaxTrackedAddresses = 100_000;

    private readonly ConcurrentDictionary<string, Failures> _byAddress = new();

    /// <summary>Counts a failed authentication by the caller of <paramref name="context"/>.</summary>
    /// <param name="clientId">The client the caller tried to authenticate as, if it named one.</param>
    public async Task RecordAsync(HttpContext context, string? clientId, CancellationToken cancellationToken)
    {
        var address = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var now = time.GetUtcNow();
        var window = options.Value.Window;
        if (_byAddress.Count >= MaxTrackedAddresses)
            Prune(now - window);

        var failures = _byAddress.GetOrAdd(address, _ => new Failures());
        if (!failures.Add(now, window, options.Value.Threshold))
            return;

        await context.RequestServices.GetRequiredService<AuditLog>().WriteAsync(new AuditEntry
        {
            Action = AuditActions.RepeatedAuthenticationFailures,
            Outcome = AuditOutcome.Failed,
            ActorKind = AuditActorKind.Anonymous,
            ActorClientId = clientId,
            ClientIp = address,
            Detail = $"{options.Value.Threshold} failed authentications within {window}.",
        }, cancellationToken);
    }

    private void Prune(DateTimeOffset before)
    {
        foreach (var (address, failures) in _byAddress)
        {
            if (failures.LastAt < before)
                _byAddress.TryRemove(address, out _);
        }
    }

    private sealed class Failures
    {
        private readonly Queue<DateTimeOffset> _times = new();

        public DateTimeOffset LastAt { get; private set; }

        /// <summary>Adds a failure, returning true when it reaches the threshold; the count then starts again.</summary>
        public bool Add(DateTimeOffset at, TimeSpan window, int threshold)
        {
            lock (_times)
            {
                LastAt = at;
                while (_times.TryPeek(out var oldest) && oldest <= at - window)
                    _times.Dequeue();
                _times.Enqueue(at);
                if (_times.Count < threshold)
                    return false;
                _times.Clear();
                return true;
            }
        }
    }
}

public static class AuditExtensions
{
    /// <summary>The audit log and the repeated-authentication-failure count, for auth and api.</summary>
    public static IServiceCollection AddSignal9Audit(this IServiceCollection services)
    {
        services.AddScoped<AuditLog>();
        services.AddOptions<RepeatedFailureOptions>().BindConfiguration(RepeatedFailureOptions.SectionName)
            .ValidateDataAnnotations().ValidateOnStart();
        services.AddSingleton<AuthenticationFailures>();
        return services;
    }
}
