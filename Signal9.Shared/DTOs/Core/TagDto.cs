using System.ComponentModel.DataAnnotations;
using Signal9.Shared.DTOs.Base;

namespace Signal9.Shared.DTOs;

/// <summary>
/// Tag DTO - represents organizational labels for agents and other entities.
/// Uses unified hierarchy where ParentId can represent either:
/// - Parent tag for hierarchical tag structures
/// - Tenant for tenant-scoped tags
/// </summary>
public record TagDto : BaseDto<Guid>
{
    /// <summary>
    /// Tag name
    /// </summary>
    [Required(ErrorMessage = "Name is required")]
    [StringLength(100, MinimumLength = 1, ErrorMessage = "Name must be between 1 and 100 characters")]
    public required string Name { get; init; }
    
    /// <summary>
    /// Tag description
    /// </summary>
    [StringLength(500, ErrorMessage = "Description cannot exceed 500 characters")]
    public string? Description { get; init; }
    
    /// <summary>
    /// Tag color for UI display (hex color code)
    /// </summary>
    [StringLength(7, ErrorMessage = "Color must be a valid hex color code")]
    [RegularExpression(@"^#[0-9A-Fa-f]{6}$", ErrorMessage = "Color must be a valid hex color code (e.g., #FF0000)")]
    public string? Color { get; init; } = "#007bff";
    
    /// <summary>
    /// Tag icon for UI display
    /// </summary>
    [StringLength(50, ErrorMessage = "Icon cannot exceed 50 characters")]
    public string? Icon { get; init; }
    
    /// <summary>
    /// Whether this tag is active
    /// </summary>
    public bool IsActive { get; init; } = true;
    
    /// <summary>
    /// Tag metadata (JSON serialized)
    /// </summary>
    public string? Metadata { get; init; }
    
    // ParentId inherited from BaseDto<Guid> represents:
    // - Parent tag for hierarchical tag structures
    // - Tenant for tenant-scoped tags
}


