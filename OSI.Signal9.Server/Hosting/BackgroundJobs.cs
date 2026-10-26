using System.Diagnostics;

namespace OSI.Signal9.Server.Hosting;

/// <summary>
/// Traces every run of a background job (the Entra session check, the session and hub sweepers) as a span of its own,
/// so operators see when each job ran, how long it took and whether it failed. Every host exports
/// <see cref="SourceName"/> with its traces.
/// </summary>
public static class BackgroundJobs
{
    public const string SourceName = "OSI.Signal9.BackgroundJobs";

    private static readonly ActivitySource Source = new(SourceName);

    /// <summary>Starts the span of one run of <paramref name="job"/>, a root of its own; dispose it when the run ends.</summary>
    public static Activity? StartRun(string job) =>
        Source.StartActivity(job, ActivityKind.Internal, parentContext: default(ActivityContext));

    /// <summary>Marks the run as failed with <paramref name="exception"/>.</summary>
    public static void Failed(this Activity? run, Exception exception)
    {
        run?.AddException(exception);
        run?.SetStatus(ActivityStatusCode.Error, exception.Message);
    }
}
