namespace Signal9.Shared.Configuration;

/// <summary>
/// Configuration for Azure services
/// </summary>
public class AzureConfiguration
{
    public string? KeyVaultUrl { get; set; }
    public string? ApplicationInsightsConnectionString { get; set; }
    public CosmosDbConfiguration CosmosDb { get; set; } = new();
    public SqlDatabaseConfiguration SqlDatabase { get; set; } = new();
    public ServiceBusConfiguration ServiceBus { get; set; } = new();
    public SignalRConfiguration SignalR { get; set; } = new();
}

public class CosmosDbConfiguration
{
    public string? ConnectionString { get; set; }
    public string? DatabaseName { get; set; } = "Signal9";
    public string TelemetryContainer { get; set; } = "telemetry";
    public string LogsContainer { get; set; } = "logs";
    public string EventsContainer { get; set; } = "events";
}

public class SqlDatabaseConfiguration
{
    public string? ConnectionString { get; set; }
    public bool EnableSensitiveDataLogging { get; set; } = false;
    public int CommandTimeout { get; set; } = 30;
}

public class ServiceBusConfiguration
{
    public string? ConnectionString { get; set; }
    public string CommandQueue { get; set; } = "agent-commands";
    public string TelemetryTopic { get; set; } = "telemetry";
    public string EventsTopic { get; set; } = "events";
}

public class SignalRConfiguration
{
    public string? ConnectionString { get; set; }
    public string HubName { get; set; } = "AgentHub";
    public int KeepAliveInterval { get; set; } = 15;
    public int ClientTimeoutInterval { get; set; } = 30;
}

/// <summary>
/// Configuration for agent behavior
/// </summary>
public class AgentConfiguration
{
    public string? AgentFunctionsUrl { get; set; }
    public string? FunctionKey { get; set; }
    public string? TenantCode { get; set; }
    public string? GroupName { get; set; }
    public int HeartbeatInterval { get; set; } = 30;
    public int TelemetryInterval { get; set; } = 60;
    public int ReconnectDelay { get; set; } = 5;
    public int MaxReconnectAttempts { get; set; } = 10;
    public TelemetryConfiguration Telemetry { get; set; } = new();
    public LoggingConfiguration Logging { get; set; } = new();
    public Dictionary<string, object> CustomSettings { get; set; } = new();
}

public class TelemetryConfiguration
{
    public bool EnablePerformanceMetrics { get; set; } = true;
    public bool EnableSystemHealth { get; set; } = true;
    public bool EnableEventLogs { get; set; } = true;
    public bool EnableNetworkMetrics { get; set; } = true;
    public bool EnableStorageMetrics { get; set; } = true;
    public string[] PerformanceCounters { get; set; } = Array.Empty<string>();
    public string[] EventLogSources { get; set; } = Array.Empty<string>();
    public Dictionary<string, object> CustomMetrics { get; set; } = new();
}

public class LoggingConfiguration
{
    public string LogLevel { get; set; } = "Information";
    public bool EnableConsoleLogging { get; set; } = true;
    public bool EnableFileLogging { get; set; } = true;
    public bool EnableRemoteLogging { get; set; } = true;
    public string? LogFilePath { get; set; }
    public long MaxLogFileSizeMB { get; set; } = 10;
    public int MaxLogFiles { get; set; } = 5;
}
