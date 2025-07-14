using System.ComponentModel.DataAnnotations;
using Signal9.Shared.DTOs.Base;

namespace Signal9.Shared.DTOs.System;

/// <summary>
/// System user DTO for platform management
/// </summary>
public record SystemUser : BaseDto
{
    [Required]
    public string Username { get; set; } = string.Empty;
    
    [Required] 
    [EmailAddress]
    public string Email { get; set; } = string.Empty;
    
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastLoginAt { get; set; }
    public string[] Roles { get; set; } = Array.Empty<string>();
}

/// <summary>
/// System alert DTO for platform notifications
/// </summary>
public record SystemAlert : BaseDto
{
    [Required]
    public string Title { get; set; } = string.Empty;
    
    [Required]
    public string Message { get; set; } = string.Empty;
    
    public string Level { get; set; } = "Info";
    public bool IsResolved { get; set; } = false;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ResolvedAt { get; set; }
    public string? ResolvedBy { get; set; }
    public string Source { get; set; } = "System";
    public Dictionary<string, object> Metadata { get; set; } = new();
}
