using System.ComponentModel.DataAnnotations;

namespace Signal9.Shared.Models;

/// <summary>
/// Result of a bulk operation for MSP platform
/// Tracks success/failure of individual operations within a bulk request
/// </summary>
public class BulkOperationResult<T>
{
    /// <summary>
    /// Unique identifier for the operation
    /// </summary>
    [Required]
    public Guid Id { get; set; }

    /// <summary>
    /// Tenant ID for multi-tenant isolation
    /// </summary>
    [Required]
    public Guid TenantId { get; set; }

    /// <summary>
    /// Operation type (e.g., "create", "update", "delete")
    /// </summary>
    [Required]
    [MaxLength(50)]
    public string Operation { get; set; } = string.Empty;

    /// <summary>
    /// Whether the operation was successful
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Error message if the operation failed
    /// </summary>
    [MaxLength(1000)]
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Result data from the operation
    /// </summary>
    public T? Result { get; set; }

    /// <summary>
    /// Additional metadata about the operation
    /// </summary>
    public Dictionary<string, object>? Metadata { get; set; }

    /// <summary>
    /// When the operation was processed
    /// </summary>
    public DateTime ProcessedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// How long the operation took to process
    /// </summary>
    public TimeSpan ProcessingTime { get; set; }

    /// <summary>
    /// Convenience properties for backward compatibility
    /// </summary>
    public string? Error 
    { 
        get => ErrorMessage; 
        set => ErrorMessage = value; 
    }
    
    public string? Message 
    { 
        get => ErrorMessage; 
        set => ErrorMessage = value; 
    }
}
