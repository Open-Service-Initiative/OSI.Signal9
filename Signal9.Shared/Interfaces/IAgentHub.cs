namespace Signal9.Shared.Interfaces;

/// <summary>
/// Interface for SignalR hub methods that agents can call
/// </summary>
public interface IAgentHub
{
    /// <summary>
    /// Register an agent with the hub
    /// </summary>
    Task RegisterAgent(string agentId, object registrationData);
    
    /// <summary>
    /// Send telemetry data from agent to hub
    /// </summary>
    Task SendTelemetry(string agentId, object telemetryData);
    
    /// <summary>
    /// Update agent status
    /// </summary>
    Task UpdateStatus(string agentId, string status);
    
    /// <summary>
    /// Send heartbeat from agent
    /// </summary>
    Task Heartbeat(string agentId);
    
    /// <summary>
    /// Report command execution result
    /// </summary>
    Task ReportCommandResult(string commandId, object result);
    
    /// <summary>
    /// Send log entries from agent
    /// </summary>
    Task SendLogs(string agentId, object[] logEntries);
}

/// <summary>
/// Interface for SignalR hub methods that can be called on agents
/// </summary>
public interface IAgentClient
{
    /// <summary>
    /// Send a command to an agent
    /// </summary>
    Task ExecuteCommand(object command);
    
    /// <summary>
    /// Request configuration update
    /// </summary>
    Task UpdateConfiguration(object configuration);
    
    /// <summary>
    /// Request telemetry collection
    /// </summary>
    Task CollectTelemetry(string[] metrics);
    
    /// <summary>
    /// Request agent restart
    /// </summary>
    Task RestartAgent();
    
    /// <summary>
    /// Request agent shutdown
    /// </summary>
    Task ShutdownAgent();
    
    /// <summary>
    /// Notify agent of connection status
    /// </summary>
    Task ConnectionStatusChanged(string status);
}
