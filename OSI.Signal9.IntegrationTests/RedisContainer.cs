using Testcontainers.Redis;

namespace OSI.Signal9.IntegrationTests;

/// <summary>
/// One Redis container for the run, started by the first test that scales api out (story 117). Each fixture gets its
/// own channel prefix on it, so tests running side by side never hear each other.
/// </summary>
public static class RedisContainer
{
    private static readonly Lazy<Task<Testcontainers.Redis.RedisContainer>> Container = new(async () =>
    {
        var container = new RedisBuilder("redis:8").Build();
        await container.StartAsync();
        return container;
    });

    /// <summary>The settings that put api's hubs on the Redis backplane, under a channel prefix of their own.</summary>
    public static async Task<Dictionary<string, string?>> BackplaneSettingsAsync() => new()
    {
        ["ConnectionStrings:redis"] = (await Container.Value).GetConnectionString(),
        ["Realtime:BackplaneChannelPrefix"] = $"test-{Guid.NewGuid():N}",
    };

    /// <summary>Stops the container, if a test started it; <see cref="PostgresContainer"/>'s cleanup calls it.</summary>
    public static async Task StopAsync()
    {
        if (Container.IsValueCreated)
            await (await Container.Value).DisposeAsync();
    }
}
