namespace OSI.Signal9.Server.Hosting;

/// <summary>Where the hubs' client connections are held (ADR-0009, stories 116-117), set by <c>Realtime:Hosting</c>.</summary>
public enum RealtimeHostingMode
{
    /// <summary>In api itself; on a Redis backplane once api scales out.</summary>
    SelfHosted,

    /// <summary>On Azure SignalR Service, which holds every client connection; api only negotiates and sends.</summary>
    AzureSignalR,
}
