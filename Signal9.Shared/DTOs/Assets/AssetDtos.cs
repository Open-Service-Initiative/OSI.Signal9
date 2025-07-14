using System.ComponentModel.DataAnnotations;
using Signal9.Shared.DTOs.Base;

namespace Signal9.Shared.DTOs.Assets;

/// <summary>
/// Software inventory item representing installed software on an agent.
/// </summary>
public record SoftwareInventoryItem : TenantScopedDto
{
    /// <summary>
    /// Gets the unique identifier for the software inventory item.
    /// </summary>
    public required Guid Id { get; init; }
    
    /// <summary>
    /// Gets the agent ID where this software is installed.
    /// </summary>
    public required Guid AgentId { get; init; }
    
    /// <summary>
    /// Gets the name of the software.
    /// </summary>
    [MaxLength(200)]
    public required string Name { get; init; }
    
    /// <summary>
    /// Gets the version of the software.
    /// </summary>
    [MaxLength(50)]
    public required string Version { get; init; }
    
    /// <summary>
    /// Gets the publisher/vendor of the software.
    /// </summary>
    [MaxLength(200)]
    public string? Publisher { get; init; }
    
    /// <summary>
    /// Gets the installation date of the software.
    /// </summary>
    public DateTime? InstallDate { get; init; }
    
    /// <summary>
    /// Gets the size of the software installation in bytes.
    /// </summary>
    public long? SizeBytes { get; init; }
    
    /// <summary>
    /// Gets the installation path of the software.
    /// </summary>
    [MaxLength(500)]
    public string? InstallPath { get; init; }
    
    /// <summary>
    /// Gets the uninstall string for the software.
    /// </summary>
    [MaxLength(500)]
    public string? UninstallString { get; init; }
    
    /// <summary>
    /// Gets the software category.
    /// </summary>
    public SoftwareCategory Category { get; init; }
    
    /// <summary>
    /// Gets whether the software is currently active/running.
    /// </summary>
    public bool IsActive { get; init; }
    
    /// <summary>
    /// Gets the license information if available.
    /// </summary>
    public Guid? LicenseId { get; init; }
    
    /// <summary>
    /// Gets when this inventory item was last updated.
    /// </summary>
    public DateTime LastUpdated { get; init; }
    
    /// <summary>
    /// Gets additional software metadata.
    /// </summary>
    public Dictionary<string, object>? Metadata { get; init; }
}

/// <summary>
/// Hardware inventory item representing physical hardware components.
/// </summary>
public record HardwareInventoryItem : TenantScopedDto
{
    /// <summary>
    /// Gets the unique identifier for the hardware inventory item.
    /// </summary>
    public required Guid Id { get; init; }
    
    /// <summary>
    /// Gets the agent ID where this hardware is located.
    /// </summary>
    public required Guid AgentId { get; init; }
    
    /// <summary>
    /// Gets the type of hardware component.
    /// </summary>
    public required HardwareType Type { get; init; }
    
    /// <summary>
    /// Gets the manufacturer of the hardware.
    /// </summary>
    [MaxLength(200)]
    public string? Manufacturer { get; init; }
    
    /// <summary>
    /// Gets the model of the hardware.
    /// </summary>
    [MaxLength(200)]
    public string? Model { get; init; }
    
    /// <summary>
    /// Gets the serial number of the hardware.
    /// </summary>
    [MaxLength(100)]
    public string? SerialNumber { get; init; }
    
    /// <summary>
    /// Gets the part number of the hardware.
    /// </summary>
    [MaxLength(100)]
    public string? PartNumber { get; init; }
    
    /// <summary>
    /// Gets the capacity or size of the hardware (e.g., RAM size, disk size).
    /// </summary>
    public long? Capacity { get; init; }
    
    /// <summary>
    /// Gets the unit of measurement for capacity.
    /// </summary>
    [MaxLength(20)]
    public string? CapacityUnit { get; init; }
    
    /// <summary>
    /// Gets the current status of the hardware.
    /// </summary>
    public required HardwareStatus Status { get; init; }
    
    /// <summary>
    /// Gets the health status of the hardware.
    /// </summary>
    public HardwareHealth Health { get; init; }
    
    /// <summary>
    /// Gets the temperature reading if applicable.
    /// </summary>
    public double? Temperature { get; init; }
    
    /// <summary>
    /// Gets the firmware version if applicable.
    /// </summary>
    [MaxLength(50)]
    public string? FirmwareVersion { get; init; }
    
    /// <summary>
    /// Gets the driver version if applicable.
    /// </summary>
    [MaxLength(50)]
    public string? DriverVersion { get; init; }
    
    /// <summary>
    /// Gets when this hardware was first detected.
    /// </summary>
    public DateTime FirstDetected { get; init; }
    
    /// <summary>
    /// Gets when this inventory item was last updated.
    /// </summary>
    public DateTime LastUpdated { get; init; }
    
    /// <summary>
    /// Gets additional hardware specifications.
    /// </summary>
    public Dictionary<string, object>? Specifications { get; init; }
}

/// <summary>
/// License information for software assets.
/// </summary>
public record LicenseInfo : TenantScopedDto
{
    /// <summary>
    /// Gets the unique identifier for the license.
    /// </summary>
    public required Guid Id { get; init; }
    
    /// <summary>
    /// Gets the name of the licensed software.
    /// </summary>
    [MaxLength(200)]
    public required string SoftwareName { get; init; }
    
    /// <summary>
    /// Gets the license key or identifier.
    /// </summary>
    [MaxLength(500)]
    public required string LicenseKey { get; init; }
    
    /// <summary>
    /// Gets the type of license.
    /// </summary>
    public required LicenseType Type { get; init; }
    
    /// <summary>
    /// Gets the total number of licenses purchased.
    /// </summary>
    public int TotalLicenses { get; init; }
    
    /// <summary>
    /// Gets the number of licenses currently in use.
    /// </summary>
    public int LicensesInUse { get; init; }
    
    /// <summary>
    /// Gets the number of available licenses.
    /// </summary>
    public int AvailableLicenses => TotalLicenses - LicensesInUse;
    
    /// <summary>
    /// Gets the purchase date of the license.
    /// </summary>
    public DateTime? PurchaseDate { get; init; }
    
    /// <summary>
    /// Gets the expiration date of the license.
    /// </summary>
    public DateTime? ExpirationDate { get; init; }
    
    /// <summary>
    /// Gets the renewal date for the license.
    /// </summary>
    public DateTime? RenewalDate { get; init; }
    
    /// <summary>
    /// Gets the cost of the license.
    /// </summary>
    public decimal? Cost { get; init; }
    
    /// <summary>
    /// Gets the currency of the license cost.
    /// </summary>
    [MaxLength(3)]
    public string? Currency { get; init; }
    
    /// <summary>
    /// Gets the vendor or publisher of the software.
    /// </summary>
    [MaxLength(200)]
    public string? Vendor { get; init; }
    
    /// <summary>
    /// Gets the current status of the license.
    /// </summary>
    public required LicenseStatus Status { get; init; }
    
    /// <summary>
    /// Gets the compliance status of the license.
    /// </summary>
    public LicenseCompliance Compliance { get; init; }
    
    /// <summary>
    /// Gets additional license terms and conditions.
    /// </summary>
    public Dictionary<string, object>? Terms { get; init; }
}

/// <summary>
/// Asset change event for tracking modifications to assets.
/// </summary>
public record AssetChangeEvent : TenantScopedDto
{
    /// <summary>
    /// Gets the unique identifier for the change event.
    /// </summary>
    public required Guid Id { get; init; }
    
    /// <summary>
    /// Gets the agent ID where the change occurred.
    /// </summary>
    public required Guid AgentId { get; init; }
    
    /// <summary>
    /// Gets the type of asset that changed.
    /// </summary>
    public required AssetType AssetType { get; init; }
    
    /// <summary>
    /// Gets the unique identifier of the asset that changed.
    /// </summary>
    public required Guid AssetId { get; init; }
    
    /// <summary>
    /// Gets the name of the asset that changed.
    /// </summary>
    [MaxLength(200)]
    public required string AssetName { get; init; }
    
    /// <summary>
    /// Gets the type of change that occurred.
    /// </summary>
    public required ChangeType ChangeType { get; init; }
    
    /// <summary>
    /// Gets the description of the change.
    /// </summary>
    [MaxLength(1000)]
    public string? Description { get; init; }
    
    /// <summary>
    /// Gets the previous state of the asset before the change.
    /// </summary>
    public Dictionary<string, object>? PreviousState { get; init; }
    
    /// <summary>
    /// Gets the new state of the asset after the change.
    /// </summary>
    public Dictionary<string, object>? NewState { get; init; }
    
    /// <summary>
    /// Gets when the change was detected.
    /// </summary>
    public DateTime DetectedAt { get; init; }
    
    /// <summary>
    /// Gets the source that detected the change.
    /// </summary>
    [MaxLength(100)]
    public string? DetectionSource { get; init; }
    
    /// <summary>
    /// Gets the user who initiated the change, if known.
    /// </summary>
    [MaxLength(100)]
    public string? ChangedBy { get; init; }
    
    /// <summary>
    /// Gets the impact level of the change.
    /// </summary>
    public ChangeImpact Impact { get; init; }
}

/// <summary>
/// Patch status information for software updates.
/// </summary>
public record PatchStatusInfo : TenantScopedDto
{
    /// <summary>
    /// Gets the unique identifier for the patch status.
    /// </summary>
    public required Guid Id { get; init; }
    
    /// <summary>
    /// Gets the agent ID this patch status applies to.
    /// </summary>
    public required Guid AgentId { get; init; }
    
    /// <summary>
    /// Gets the patch or update identifier.
    /// </summary>
    [MaxLength(100)]
    public required string PatchId { get; init; }
    
    /// <summary>
    /// Gets the title or name of the patch.
    /// </summary>
    [MaxLength(300)]
    public required string Title { get; init; }
    
    /// <summary>
    /// Gets the description of the patch.
    /// </summary>
    [MaxLength(1000)]
    public string? Description { get; init; }
    
    /// <summary>
    /// Gets the category of the patch.
    /// </summary>
    public required PatchCategory Category { get; init; }
    
    /// <summary>
    /// Gets the severity level of the patch.
    /// </summary>
    public required PatchSeverity Severity { get; init; }
    
    /// <summary>
    /// Gets the current status of the patch.
    /// </summary>
    public required PatchStatus Status { get; init; }
    
    /// <summary>
    /// Gets the size of the patch in bytes.
    /// </summary>
    public long? SizeBytes { get; init; }
    
    /// <summary>
    /// Gets when the patch was released.
    /// </summary>
    public DateTime? ReleaseDate { get; init; }
    
    /// <summary>
    /// Gets when the patch was installed.
    /// </summary>
    public DateTime? InstallDate { get; init; }
    
    /// <summary>
    /// Gets the deadline for installing the patch.
    /// </summary>
    public DateTime? InstallDeadline { get; init; }
    
    /// <summary>
    /// Gets whether the patch requires a reboot.
    /// </summary>
    public bool RequiresReboot { get; init; }
    
    /// <summary>
    /// Gets the vendor or publisher of the patch.
    /// </summary>
    [MaxLength(200)]
    public string? Vendor { get; init; }
    
    /// <summary>
    /// Gets the product this patch applies to.
    /// </summary>
    [MaxLength(200)]
    public string? Product { get; init; }
    
    /// <summary>
    /// Gets the version this patch applies to.
    /// </summary>
    [MaxLength(50)]
    public string? ProductVersion { get; init; }
    
    /// <summary>
    /// Gets additional patch metadata.
    /// </summary>
    public Dictionary<string, object>? Metadata { get; init; }
    
    /// <summary>
    /// Gets when this patch status was last updated.
    /// </summary>
    public DateTime LastUpdated { get; init; }
}

#region Supporting Enums

/// <summary>
/// Software category enumeration.
/// </summary>
public enum SoftwareCategory
{
    Application,
    SystemUtility,
    SecuritySoftware,
    DevelopmentTool,
    GameSoftware,
    BusinessSoftware,
    MediaSoftware,
    EducationalSoftware,
    Driver,
    Service,
    Other
}

/// <summary>
/// Hardware type enumeration.
/// </summary>
public enum HardwareType
{
    Processor,
    Memory,
    Storage,
    NetworkAdapter,
    GraphicsCard,
    Motherboard,
    PowerSupply,
    CoolingSystem,
    AudioDevice,
    InputDevice,
    Display,
    OpticalDrive,
    Other
}

/// <summary>
/// Hardware status enumeration.
/// </summary>
public enum HardwareStatus
{
    Active,
    Inactive,
    Disabled,
    Error,
    Unknown
}

/// <summary>
/// Hardware health enumeration.
/// </summary>
public enum HardwareHealth
{
    Good,
    Warning,
    Critical,
    Failed,
    Unknown
}

/// <summary>
/// License type enumeration.
/// </summary>
public enum LicenseType
{
    Perpetual,
    Subscription,
    Volume,
    OEM,
    Educational,
    Trial,
    Freeware,
    OpenSource
}

/// <summary>
/// License status enumeration.
/// </summary>
public enum LicenseStatus
{
    Active,
    Expired,
    Expiring,
    Suspended,
    Revoked,
    Unknown
}

/// <summary>
/// License compliance enumeration.
/// </summary>
public enum LicenseCompliance
{
    Compliant,
    OverLicensed,
    UnderLicensed,
    NonCompliant,
    Unknown
}

/// <summary>
/// Asset type enumeration.
/// </summary>
public enum AssetType
{
    Software,
    Hardware,
    License,
    Configuration,
    Service,
    Other
}

/// <summary>
/// Change type enumeration.
/// </summary>
public enum ChangeType
{
    Added,
    Modified,
    Removed,
    Upgraded,
    Downgraded,
    Configured,
    Enabled,
    Disabled
}

/// <summary>
/// Change impact enumeration.
/// </summary>
public enum ChangeImpact
{
    Low,
    Medium,
    High,
    Critical
}

/// <summary>
/// Patch category enumeration.
/// </summary>
public enum PatchCategory
{
    SecurityUpdate,
    CriticalUpdate,
    FeatureUpdate,
    BugFix,
    DriverUpdate,
    ServicePack,
    Definition,
    Other
}

/// <summary>
/// Patch severity enumeration.
/// </summary>
public enum PatchSeverity
{
    Low,
    Moderate,
    Important,
    Critical
}

/// <summary>
/// Patch status enumeration.
/// </summary>
public enum PatchStatus
{
    Available,
    Downloaded,
    Installing,
    Installed,
    Failed,
    Superseded,
    NotApplicable
}

#endregion