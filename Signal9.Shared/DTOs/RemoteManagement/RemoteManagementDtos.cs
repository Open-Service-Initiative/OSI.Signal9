using System.ComponentModel.DataAnnotations;
using Signal9.Shared.DTOs.Base;

namespace Signal9.Shared.DTOs.RemoteManagement;

/// <summary>
/// Request to initiate a remote session with an agent.
/// </summary>
public record RemoteSessionRequest : BaseDto<Guid>
{
    /// <summary>
    /// Gets the agent ID to connect to.
    /// </summary>
    public required Guid AgentId { get; init; }
    
    /// <summary>
    /// Gets the type of remote session.
    /// </summary>
    public required RemoteSessionType SessionType { get; init; }
    
    /// <summary>
    /// Gets the protocol to use for the connection.
    /// </summary>
    public RemoteProtocol Protocol { get; init; } = RemoteProtocol.RDP;
    
    /// <summary>
    /// Gets the session timeout in minutes.
    /// </summary>
    public int TimeoutMinutes { get; init; } = 60;
    
    /// <summary>
    /// Gets whether to request elevated privileges.
    /// </summary>
    public bool RequestElevation { get; init; }
    
    /// <summary>
    /// Gets the screen resolution for the session.
    /// </summary>
    public ScreenResolution? Resolution { get; init; }
    
    /// <summary>
    /// Gets additional session configuration options.
    /// </summary>
    public Dictionary<string, object>? Options { get; init; }
    
    /// <summary>
    /// Gets the reason for the remote session.
    /// </summary>
    [MaxLength(500)]
    public string? Reason { get; init; }
}

/// <summary>
/// Response containing remote session information and connection details.
/// </summary>
public record RemoteSessionResponse : BaseDto<Guid>
{
    /// <summary>
    /// Gets the unique identifier for the remote session.
    /// </summary>
    
    
    /// <summary>
    /// Gets the agent ID for the session.
    /// </summary>
    public required Guid AgentId { get; init; }
    
    /// <summary>
    /// Gets the agent name for display purposes.
    /// </summary>
    public string? AgentName { get; init; }
    
    /// <summary>
    /// Gets the type of remote session.
    /// </summary>
    public required RemoteSessionType SessionType { get; init; }
    
    /// <summary>
    /// Gets the protocol being used for the connection.
    /// </summary>
    public required RemoteProtocol Protocol { get; init; }
    
    /// <summary>
    /// Gets the current status of the session.
    /// </summary>
    public required SessionStatus Status { get; init; }
    
    /// <summary>
    /// Gets the connection details for establishing the session.
    /// </summary>
    public ConnectionDetails? ConnectionDetails { get; init; }
    
    /// <summary>
    /// Gets when the session ended.
    /// </summary>
    public DateTime? EndedAt { get; init; }
    
    /// <summary>
    /// Gets the duration of the session.
    /// </summary>
    public TimeSpan? Duration { get; init; }
    
    /// <summary>
    /// Gets the user who initiated the session.
    /// </summary>
    public string? InitiatedBy { get; init; }
    
    /// <summary>
    /// Gets the reason for the remote session.
    /// </summary>
    public string? Reason { get; init; }
    
    /// <summary>
    /// Gets any error message if the session failed.
    /// </summary>
    public string? ErrorMessage { get; init; }
    
    /// <summary>
    /// Gets session metrics and statistics.
    /// </summary>
    public SessionMetrics? Metrics { get; init; }
}

/// <summary>
/// Request to capture a screenshot from an agent.
/// </summary>
public record ScreenshotRequest : BaseDto<Guid>
{
    /// <summary>
    /// Gets the agent ID to capture screenshot from.
    /// </summary>
    public required Guid AgentId { get; init; }
    
    /// <summary>
    /// Gets the quality of the screenshot (1-100).
    /// </summary>
    [Range(1, 100)]
    public int Quality { get; init; } = 80;
    
    /// <summary>
    /// Gets the format for the screenshot.
    /// </summary>
    public ImageFormat Format { get; init; } = ImageFormat.PNG;
    
    /// <summary>
    /// Gets the screen to capture (for multi-monitor systems).
    /// </summary>
    public int ScreenIndex { get; init; } = 0;
    
    /// <summary>
    /// Gets whether to capture all screens.
    /// </summary>
    public bool CaptureAllScreens { get; init; }
    
    /// <summary>
    /// Gets the region to capture (null for full screen).
    /// </summary>
    public ScreenRegion? Region { get; init; }
    
    /// <summary>
    /// Gets whether to include cursor in the screenshot.
    /// </summary>
    public bool IncludeCursor { get; init; } = true;
    
    /// <summary>
    /// Gets the maximum width for the screenshot.
    /// </summary>
    public int? MaxWidth { get; init; }
    
    /// <summary>
    /// Gets the maximum height for the screenshot.
    /// </summary>
    public int? MaxHeight { get; init; }
}

/// <summary>
/// Response containing screenshot data and metadata.
/// </summary>
public record ScreenshotResponse : BaseDto<Guid>
{
    /// <summary>
    /// Gets the unique identifier for the screenshot.
    /// </summary>
    
    
    /// <summary>
    /// Gets the agent ID the screenshot was taken from.
    /// </summary>
    public required Guid AgentId { get; init; }
    
    /// <summary>
    /// Gets the screenshot image data as base64 encoded string.
    /// </summary>
    [MaxLength(10000000)] // ~7.5MB limit for base64 encoded image
    public required string ImageData { get; init; }
    
    /// <summary>
    /// Gets the format of the screenshot image.
    /// </summary>
    public required ImageFormat Format { get; init; }
    
    /// <summary>
    /// Gets the width of the screenshot in pixels.
    /// </summary>
    public int Width { get; init; }
    
    /// <summary>
    /// Gets the height of the screenshot in pixels.
    /// </summary>
    public int Height { get; init; }
    
    /// <summary>
    /// Gets the size of the image data in bytes.
    /// </summary>
    public long SizeBytes { get; init; }
    
    /// <summary>
    /// Gets when the screenshot was captured.
    /// </summary>
    public DateTime CapturedAt { get; init; }
    
    /// <summary>
    /// Gets the screen index that was captured.
    /// </summary>
    public int ScreenIndex { get; init; }
    
    /// <summary>
    /// Gets whether all screens were captured.
    /// </summary>
    public bool AllScreensCaptured { get; init; }
    
    /// <summary>
    /// Gets the region that was captured.
    /// </summary>
    public ScreenRegion? CapturedRegion { get; init; }
    
    /// <summary>
    /// Gets the time taken to capture the screenshot.
    /// </summary>
    public TimeSpan CaptureTime { get; init; }
}

/// <summary>
/// Request to transfer files to or from an agent.
/// </summary>
public record FileTransferRequest : BaseDto<Guid>
{
    /// <summary>
    /// Gets the agent ID for the file transfer.
    /// </summary>
    public required Guid AgentId { get; init; }
    
    /// <summary>
    /// Gets the direction of the file transfer.
    /// </summary>
    public required TransferDirection Direction { get; init; }
    
    /// <summary>
    /// Gets the source path for the file transfer.
    /// </summary>
    [MaxLength(500)]
    public required string SourcePath { get; init; }
    
    /// <summary>
    /// Gets the destination path for the file transfer.
    /// </summary>
    [MaxLength(500)]
    public required string DestinationPath { get; init; }
    
    /// <summary>
    /// Gets whether to recursively transfer directories.
    /// </summary>
    public bool Recursive { get; init; }
    
    /// <summary>
    /// Gets whether to overwrite existing files.
    /// </summary>
    public bool Overwrite { get; init; }
    
    /// <summary>
    /// Gets whether to preserve file timestamps.
    /// </summary>
    public bool PreserveTimestamps { get; init; } = true;
    
    /// <summary>
    /// Gets whether to preserve file permissions.
    /// </summary>
    public bool PreservePermissions { get; init; } = true;
    
    /// <summary>
    /// Gets the maximum transfer speed in bytes per second (0 for unlimited).
    /// </summary>
    public long MaxSpeedBytesPerSecond { get; init; }
    
    /// <summary>
    /// Gets the file patterns to include in the transfer.
    /// </summary>
    public List<string> IncludePatterns { get; init; } = new();
    
    /// <summary>
    /// Gets the file patterns to exclude from the transfer.
    /// </summary>
    public List<string> ExcludePatterns { get; init; } = new();
    
    /// <summary>
    /// Gets the reason for the file transfer.
    /// </summary>
    [MaxLength(500)]
    public string? Reason { get; init; }
}

/// <summary>
/// Response containing file transfer status and progress information.
/// </summary>
public record FileTransferResponse : BaseDto<Guid>
{
    /// <summary>
    /// Gets the unique identifier for the file transfer.
    /// </summary>
    
    
    /// <summary>
    /// Gets the agent ID for the file transfer.
    /// </summary>
    public required Guid AgentId { get; init; }
    
    /// <summary>
    /// Gets the direction of the file transfer.
    /// </summary>
    public required TransferDirection Direction { get; init; }
    
    /// <summary>
    /// Gets the source path for the file transfer.
    /// </summary>
    public required string SourcePath { get; init; }
    
    /// <summary>
    /// Gets the destination path for the file transfer.
    /// </summary>
    public required string DestinationPath { get; init; }
    
    /// <summary>
    /// Gets the current status of the file transfer.
    /// </summary>
    public required TransferStatus Status { get; init; }
    
    /// <summary>
    /// Gets the progress percentage (0-100).
    /// </summary>
    [Range(0, 100)]
    public double ProgressPercentage { get; init; }
    
    /// <summary>
    /// Gets the number of bytes transferred.
    /// </summary>
    public long BytesTransferred { get; init; }
    
    /// <summary>
    /// Gets the total number of bytes to transfer.
    /// </summary>
    public long TotalBytes { get; init; }
    
    /// <summary>
    /// Gets the current transfer speed in bytes per second.
    /// </summary>
    public long CurrentSpeedBytesPerSecond { get; init; }
    
    /// <summary>
    /// Gets the average transfer speed in bytes per second.
    /// </summary>
    public long AverageSpeedBytesPerSecond { get; init; }
    
    /// <summary>
    /// Gets the estimated time remaining for the transfer.
    /// </summary>
    public TimeSpan? EstimatedTimeRemaining { get; init; }
    
    /// <summary>
    /// Gets when the transfer was started.
    /// </summary>
    public DateTime StartedAt { get; init; }
    
    /// <summary>
    /// Gets when the transfer was completed.
    /// </summary>
    public DateTime? CompletedAt { get; init; }
    
    /// <summary>
    /// Gets the number of files transferred successfully.
    /// </summary>
    public int FilesTransferred { get; init; }
    
    /// <summary>
    /// Gets the number of files that failed to transfer.
    /// </summary>
    public int FilesFailed { get; init; }
    
    /// <summary>
    /// Gets the total number of files to transfer.
    /// </summary>
    public int TotalFiles { get; init; }
    
    /// <summary>
    /// Gets any error message if the transfer failed.
    /// </summary>
    public string? ErrorMessage { get; init; }
    
    /// <summary>
    /// Gets the list of files that failed to transfer.
    /// </summary>
    public List<string> FailedFiles { get; init; } = new();
}

/// <summary>
/// Request to execute a script on an agent.
/// </summary>
public record ScriptExecutionRequest : BaseDto<Guid>
{
    /// <summary>
    /// Gets the agent ID to execute the script on.
    /// </summary>
    public required Guid AgentId { get; init; }
    
    /// <summary>
    /// Gets the type of script to execute.
    /// </summary>
    public required ScriptType ScriptType { get; init; }
    
    /// <summary>
    /// Gets the script content to execute.
    /// </summary>
    [MaxLength(100000)]
    public required string ScriptContent { get; init; }
    
    /// <summary>
    /// Gets the arguments to pass to the script.
    /// </summary>
    public List<string> Arguments { get; init; } = new();
    
    /// <summary>
    /// Gets the working directory for script execution.
    /// </summary>
    [MaxLength(500)]
    public string? WorkingDirectory { get; init; }
    
    /// <summary>
    /// Gets the timeout for script execution in seconds.
    /// </summary>
    public int TimeoutSeconds { get; init; } = 300;
    
    /// <summary>
    /// Gets whether to run the script with elevated privileges.
    /// </summary>
    public bool RunAsElevated { get; init; }
    
    /// <summary>
    /// Gets the username to run the script as (if different from agent user).
    /// </summary>
    [MaxLength(100)]
    public string? RunAsUser { get; init; }
    
    /// <summary>
    /// Gets environment variables to set for the script execution.
    /// </summary>
    public Dictionary<string, string> EnvironmentVariables { get; init; } = new();
    
    /// <summary>
    /// Gets whether to capture the script output.
    /// </summary>
    public bool CaptureOutput { get; init; } = true;
    
    /// <summary>
    /// Gets the reason for executing the script.
    /// </summary>
    [MaxLength(500)]
    public string? Reason { get; init; }
}

/// <summary>
/// Response containing script execution results and output.
/// </summary>
public record ScriptExecutionResponse : BaseDto<Guid>
{
    /// <summary>
    /// Gets the unique identifier for the script execution.
    /// </summary>
    
    
    /// <summary>
    /// Gets the agent ID where the script was executed.
    /// </summary>
    public required Guid AgentId { get; init; }
    
    /// <summary>
    /// Gets the type of script that was executed.
    /// </summary>
    public required ScriptType ScriptType { get; init; }
    
    /// <summary>
    /// Gets the current status of the script execution.
    /// </summary>
    public required ExecutionStatus Status { get; init; }
    
    /// <summary>
    /// Gets the exit code returned by the script.
    /// </summary>
    public int? ExitCode { get; init; }
    
    /// <summary>
    /// Gets the standard output from the script.
    /// </summary>
    [MaxLength(1000000)]
    public string? StandardOutput { get; init; }
    
    /// <summary>
    /// Gets the standard error output from the script.
    /// </summary>
    [MaxLength(1000000)]
    public string? StandardError { get; init; }
    
    /// <summary>
    /// Gets when the script execution was started.
    /// </summary>
    public DateTime StartedAt { get; init; }
    
    /// <summary>
    /// Gets when the script execution completed.
    /// </summary>
    public DateTime? CompletedAt { get; init; }
    
    /// <summary>
    /// Gets the duration of the script execution.
    /// </summary>
    public TimeSpan? Duration { get; init; }
    
    /// <summary>
    /// Gets the process ID of the executed script.
    /// </summary>
    public int? ProcessId { get; init; }
    
    /// <summary>
    /// Gets the working directory where the script was executed.
    /// </summary>
    public string? WorkingDirectory { get; init; }
    
    /// <summary>
    /// Gets any error message if the execution failed.
    /// </summary>
    public string? ErrorMessage { get; init; }
    
    /// <summary>
    /// Gets execution statistics and metrics.
    /// </summary>
    public ExecutionMetrics? Metrics { get; init; }
}

/// <summary>
/// Request to perform a registry operation on an agent.
/// </summary>
public record RegistryOperationRequest : BaseDto<Guid>
{
    /// <summary>
    /// Gets the agent ID to perform the registry operation on.
    /// </summary>
    public required Guid AgentId { get; init; }
    
    /// <summary>
    /// Gets the type of registry operation to perform.
    /// </summary>
    public required RegistryOperation Operation { get; init; }
    
    /// <summary>
    /// Gets the registry hive (e.g., HKEY_LOCAL_MACHINE).
    /// </summary>
    public required RegistryHive Hive { get; init; }
    
    /// <summary>
    /// Gets the registry key path.
    /// </summary>
    [MaxLength(500)]
    public required string KeyPath { get; init; }
    
    /// <summary>
    /// Gets the registry value name (null for default value).
    /// </summary>
    [MaxLength(255)]
    public string? ValueName { get; init; }
    
    /// <summary>
    /// Gets the registry value data (for set operations).
    /// </summary>
    public string? ValueData { get; init; }
    
    /// <summary>
    /// Gets the registry value type (for set operations).
    /// </summary>
    public RegistryValueType? ValueType { get; init; }
    
    /// <summary>
    /// Gets whether to create the key if it doesn't exist.
    /// </summary>
    public bool CreateKeyIfNotExists { get; init; }
    
    /// <summary>
    /// Gets whether to include subkeys in enumeration operations.
    /// </summary>
    public bool IncludeSubkeys { get; init; }
    
    /// <summary>
    /// Gets whether to include values in enumeration operations.
    /// </summary>
    public bool IncludeValues { get; init; } = true;
    
    /// <summary>
    /// Gets the reason for the registry operation.
    /// </summary>
    [MaxLength(500)]
    public string? Reason { get; init; }
}

/// <summary>
/// Response containing registry operation results.
/// </summary>
public record RegistryOperationResponse : BaseDto<Guid>
{
    /// <summary>
    /// Gets the unique identifier for the registry operation.
    /// </summary>
    
    
    /// <summary>
    /// Gets the agent ID where the operation was performed.
    /// </summary>
    public required Guid AgentId { get; init; }
    
    /// <summary>
    /// Gets the type of registry operation that was performed.
    /// </summary>
    public required RegistryOperation Operation { get; init; }
    
    /// <summary>
    /// Gets the registry hive that was accessed.
    /// </summary>
    public required RegistryHive Hive { get; init; }
    
    /// <summary>
    /// Gets the registry key path that was accessed.
    /// </summary>
    public required string KeyPath { get; init; }
    
    /// <summary>
    /// Gets the registry value name that was accessed.
    /// </summary>
    public string? ValueName { get; init; }
    
    /// <summary>
    /// Gets the current status of the operation.
    /// </summary>
    public required OperationStatus Status { get; init; }
    
    /// <summary>
    /// Gets the registry value data (for get operations).
    /// </summary>
    public string? ValueData { get; init; }
    
    /// <summary>
    /// Gets the registry value type (for get operations).
    /// </summary>
    public RegistryValueType? ValueType { get; init; }
    
    /// <summary>
    /// Gets the list of subkeys (for enumeration operations).
    /// </summary>
    public List<string> Subkeys { get; init; } = new();
    
    /// <summary>
    /// Gets the list of values (for enumeration operations).
    /// </summary>
    public List<RegistryValue> Values { get; init; } = new();
    
    /// <summary>
    /// Gets when the operation was executed.
    /// </summary>
    public DateTime ExecutedAt { get; init; }
    
    /// <summary>
    /// Gets any error message if the operation failed.
    /// </summary>
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Request to manage a service on an agent.
/// </summary>
public record ServiceManagementRequest : BaseDto<Guid>
{
    /// <summary>
    /// Gets the agent ID to manage the service on.
    /// </summary>
    public required Guid AgentId { get; init; }
    
    /// <summary>
    /// Gets the name of the service to manage.
    /// </summary>
    [MaxLength(100)]
    public required string ServiceName { get; init; }
    
    /// <summary>
    /// Gets the action to perform on the service.
    /// </summary>
    public required ServiceAction Action { get; init; }
    
    /// <summary>
    /// Gets the timeout for the service operation in seconds.
    /// </summary>
    public int TimeoutSeconds { get; init; } = 30;
    
    /// <summary>
    /// Gets the startup type to set (for configure action).
    /// </summary>
    public ServiceStartupType? StartupType { get; init; }
    
    /// <summary>
    /// Gets the service account to run as (for configure action).
    /// </summary>
    [MaxLength(200)]
    public string? ServiceAccount { get; init; }
    
    /// <summary>
    /// Gets the service description (for configure action).
    /// </summary>
    [MaxLength(500)]
    public string? Description { get; init; }
    
    /// <summary>
    /// Gets the reason for the service management operation.
    /// </summary>
    [MaxLength(500)]
    public string? Reason { get; init; }
}

/// <summary>
/// Response containing service management operation results.
/// </summary>
public record ServiceManagementResponse : BaseDto<Guid>
{
    /// <summary>
    /// Gets the unique identifier for the service operation.
    /// </summary>
    
    
    /// <summary>
    /// Gets the agent ID where the operation was performed.
    /// </summary>
    public required Guid AgentId { get; init; }
    
    /// <summary>
    /// Gets the name of the service that was managed.
    /// </summary>
    public required string ServiceName { get; init; }
    
    /// <summary>
    /// Gets the action that was performed on the service.
    /// </summary>
    public required ServiceAction Action { get; init; }
    
    /// <summary>
    /// Gets the current status of the operation.
    /// </summary>
    public required OperationStatus Status { get; init; }
    
    /// <summary>
    /// Gets the current state of the service.
    /// </summary>
    public ServiceState? ServiceState { get; init; }
    
    /// <summary>
    /// Gets the startup type of the service.
    /// </summary>
    public ServiceStartupType? StartupType { get; init; }
    
    /// <summary>
    /// Gets the service account the service runs as.
    /// </summary>
    public string? ServiceAccount { get; init; }
    
    /// <summary>
    /// Gets the display name of the service.
    /// </summary>
    public string? DisplayName { get; init; }
    
    /// <summary>
    /// Gets the description of the service.
    /// </summary>
    public string? Description { get; init; }
    
    /// <summary>
    /// Gets the process ID of the service (if running).
    /// </summary>
    public int? ProcessId { get; init; }
    
    /// <summary>
    /// Gets when the operation was executed.
    /// </summary>
    public DateTime ExecutedAt { get; init; }
    
    /// <summary>
    /// Gets the duration of the operation.
    /// </summary>
    public TimeSpan? Duration { get; init; }
    
    /// <summary>
    /// Gets any error message if the operation failed.
    /// </summary>
    public string? ErrorMessage { get; init; }
}

#region Supporting Types and Enums

/// <summary>
/// Remote session type enumeration.
/// </summary>
public enum RemoteSessionType
{
    Desktop,
    CommandLine,
    PowerShell,
    FileManager,
    RegistryEditor,
    ServiceManager
}

/// <summary>
/// Remote protocol enumeration.
/// </summary>
public enum RemoteProtocol
{
    RDP,
    VNC,
    SSH,
    Custom
}

/// <summary>
/// Session status enumeration.
/// </summary>
public enum SessionStatus
{
    Requested,
    Connecting,
    Connected,
    Active,
    Disconnected,
    Failed,
    Timeout
}

/// <summary>
/// Screen resolution configuration.
/// </summary>
public record ScreenResolution
{
    /// <summary>
    /// Gets the width in pixels.
    /// </summary>
    public required int Width { get; init; }
    
    /// <summary>
    /// Gets the height in pixels.
    /// </summary>
    public required int Height { get; init; }
    
    /// <summary>
    /// Gets the color depth in bits.
    /// </summary>
    public int ColorDepth { get; init; } = 32;
}

/// <summary>
/// Connection details for remote sessions.
/// </summary>
public record ConnectionDetails
{
    /// <summary>
    /// Gets the host address to connect to.
    /// </summary>
    public required string Host { get; init; }
    
    /// <summary>
    /// Gets the port number for the connection.
    /// </summary>
    public required int Port { get; init; }
    
    /// <summary>
    /// Gets the username for authentication.
    /// </summary>
    public string? Username { get; init; }
    
    /// <summary>
    /// Gets the password for authentication.
    /// </summary>
    public string? Password { get; init; }
    
    /// <summary>
    /// Gets the connection token or key.
    /// </summary>
    public string? Token { get; init; }
    
    /// <summary>
    /// Gets additional connection parameters.
    /// </summary>
    public Dictionary<string, object>? Parameters { get; init; }
}

/// <summary>
/// Session metrics and statistics.
/// </summary>
public record SessionMetrics
{
    /// <summary>
    /// Gets the number of bytes transmitted.
    /// </summary>
    public long BytesTransmitted { get; init; }
    
    /// <summary>
    /// Gets the number of bytes received.
    /// </summary>
    public long BytesReceived { get; init; }
    
    /// <summary>
    /// Gets the average latency in milliseconds.
    /// </summary>
    public double AverageLatencyMs { get; init; }
    
    /// <summary>
    /// Gets the connection quality score (0-100).
    /// </summary>
    public int QualityScore { get; init; }
}

/// <summary>
/// Image format enumeration.
/// </summary>
public enum ImageFormat
{
    PNG,
    JPEG,
    BMP,
    GIF
}

/// <summary>
/// Screen region for screenshot capture.
/// </summary>
public record ScreenRegion
{
    /// <summary>
    /// Gets the X coordinate of the top-left corner.
    /// </summary>
    public required int X { get; init; }
    
    /// <summary>
    /// Gets the Y coordinate of the top-left corner.
    /// </summary>
    public required int Y { get; init; }
    
    /// <summary>
    /// Gets the width of the region.
    /// </summary>
    public required int Width { get; init; }
    
    /// <summary>
    /// Gets the height of the region.
    /// </summary>
    public required int Height { get; init; }
}

/// <summary>
/// Transfer direction enumeration.
/// </summary>
public enum TransferDirection
{
    Upload,
    Download
}

/// <summary>
/// Transfer status enumeration.
/// </summary>
public enum TransferStatus
{
    Queued,
    InProgress,
    Completed,
    Failed,
    Cancelled,
    Paused
}

/// <summary>
/// Script type enumeration.
/// </summary>
public enum ScriptType
{
    Batch,
    PowerShell,
    Python,
    JavaScript,
    VBScript,
    Bash,
    Shell
}

/// <summary>
/// Execution status enumeration.
/// </summary>
public enum ExecutionStatus
{
    Queued,
    Running,
    Completed,
    Failed,
    Timeout,
    Cancelled
}

/// <summary>
/// Execution metrics and statistics.
/// </summary>
public record ExecutionMetrics
{
    /// <summary>
    /// Gets the peak memory usage in bytes.
    /// </summary>
    public long PeakMemoryUsage { get; init; }
    
    /// <summary>
    /// Gets the total CPU time used.
    /// </summary>
    public TimeSpan CpuTime { get; init; }
    
    /// <summary>
    /// Gets the number of handles opened.
    /// </summary>
    public int HandleCount { get; init; }
    
    /// <summary>
    /// Gets the number of threads created.
    /// </summary>
    public int ThreadCount { get; init; }
}

/// <summary>
/// Registry operation enumeration.
/// </summary>
public enum RegistryOperation
{
    GetValue,
    SetValue,
    DeleteValue,
    CreateKey,
    DeleteKey,
    EnumerateKeys,
    EnumerateValues
}

/// <summary>
/// Registry hive enumeration.
/// </summary>
public enum RegistryHive
{
    HKEY_CLASSES_ROOT,
    HKEY_CURRENT_USER,
    HKEY_LOCAL_MACHINE,
    HKEY_USERS,
    HKEY_CURRENT_CONFIG
}

/// <summary>
/// Registry value type enumeration.
/// </summary>
public enum RegistryValueType
{
    String,
    ExpandString,
    Binary,
    DWord,
    QWord,
    MultiString
}

/// <summary>
/// Operation status enumeration.
/// </summary>
public enum OperationStatus
{
    Pending,
    InProgress,
    Completed,
    Failed,
    Cancelled
}

/// <summary>
/// Registry value information.
/// </summary>
public record RegistryValue
{
    /// <summary>
    /// Gets the name of the registry value.
    /// </summary>
    public required string Name { get; init; }
    
    /// <summary>
    /// Gets the type of the registry value.
    /// </summary>
    public required RegistryValueType Type { get; init; }
    
    /// <summary>
    /// Gets the data of the registry value.
    /// </summary>
    public string? Data { get; init; }
    
    /// <summary>
    /// Gets the size of the value data in bytes.
    /// </summary>
    public long Size { get; init; }
}

/// <summary>
/// Service action enumeration.
/// </summary>
public enum ServiceAction
{
    Start,
    Stop,
    Restart,
    Pause,
    Resume,
    GetStatus,
    Configure,
    Install,
    Uninstall
}

/// <summary>
/// Service startup type enumeration.
/// </summary>
public enum ServiceStartupType
{
    Automatic,
    Manual,
    Disabled,
    DelayedAutomatic
}

/// <summary>
/// Service state enumeration.
/// </summary>
public enum ServiceState
{
    Stopped,
    Running,
    Paused,
    Pending,
    Unknown
}

#endregion


