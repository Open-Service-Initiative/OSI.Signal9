using System.ComponentModel.DataAnnotations;
using System.Net;
using Signal9.Shared.DTOs.Base;

namespace Signal9.Shared.DTOs.Network;

/// <summary>
/// Network interface information for an agent.
/// </summary>
public record NetworkInterfaceInfo : TenantScopedDto
{
    /// <summary>
    /// Gets the unique identifier for the network interface record.
    /// </summary>
    public required Guid Id { get; init; }
    
    /// <summary>
    /// Gets the agent ID this network interface belongs to.
    /// </summary>
    public required Guid AgentId { get; init; }
    
    /// <summary>
    /// Gets the name of the network interface.
    /// </summary>
    [MaxLength(100)]
    public required string InterfaceName { get; init; }
    
    /// <summary>
    /// Gets the display name or description of the network interface.
    /// </summary>
    [MaxLength(200)]
    public string? DisplayName { get; init; }
    
    /// <summary>
    /// Gets the type of network interface.
    /// </summary>
    public required NetworkInterfaceType InterfaceType { get; init; }
    
    /// <summary>
    /// Gets the current operational status of the interface.
    /// </summary>
    public required InterfaceStatus Status { get; init; }
    
    /// <summary>
    /// Gets the administrative status of the interface.
    /// </summary>
    public InterfaceAdminStatus AdminStatus { get; init; }
    
    /// <summary>
    /// Gets the MAC address of the network interface.
    /// </summary>
    [MaxLength(17)]
    public string? MacAddress { get; init; }
    
    /// <summary>
    /// Gets the maximum transmission unit (MTU) size.
    /// </summary>
    public int Mtu { get; init; }
    
    /// <summary>
    /// Gets the speed of the network interface in bits per second.
    /// </summary>
    public long SpeedBps { get; init; }
    
    /// <summary>
    /// Gets whether the interface supports multicast.
    /// </summary>
    public bool SupportsMulticast { get; init; }
    
    /// <summary>
    /// Gets the IPv4 addresses assigned to this interface.
    /// </summary>
    public List<IPv4AddressInfo> IPv4Addresses { get; init; } = new();
    
    /// <summary>
    /// Gets the IPv6 addresses assigned to this interface.
    /// </summary>
    public List<IPv6AddressInfo> IPv6Addresses { get; init; } = new();
    
    /// <summary>
    /// Gets the DNS servers configured for this interface.
    /// </summary>
    public List<string> DnsServers { get; init; } = new();
    
    /// <summary>
    /// Gets the default gateway for this interface.
    /// </summary>
    public string? DefaultGateway { get; init; }
    
    /// <summary>
    /// Gets DHCP configuration information.
    /// </summary>
    public DhcpInfo? DhcpInfo { get; init; }
    
    /// <summary>
    /// Gets network traffic statistics.
    /// </summary>
    public required NetworkStatistics Statistics { get; init; }
    
    /// <summary>
    /// Gets when this interface information was last updated.
    /// </summary>
    public DateTime LastUpdated { get; init; }
    
    /// <summary>
    /// Gets additional interface properties and vendor-specific information.
    /// </summary>
    public Dictionary<string, object>? Properties { get; init; }
}

/// <summary>
/// Network configuration settings for an agent.
/// </summary>
public record NetworkConfiguration : TenantScopedDto
{
    /// <summary>
    /// Gets the unique identifier for the network configuration.
    /// </summary>
    public required Guid Id { get; init; }
    
    /// <summary>
    /// Gets the agent ID this configuration applies to.
    /// </summary>
    public required Guid AgentId { get; init; }
    
    /// <summary>
    /// Gets the network interface this configuration applies to.
    /// </summary>
    [MaxLength(100)]
    public required string InterfaceName { get; init; }
    
    /// <summary>
    /// Gets the configuration profile name.
    /// </summary>
    [MaxLength(100)]
    public required string ProfileName { get; init; }
    
    /// <summary>
    /// Gets whether this is the active configuration.
    /// </summary>
    public bool IsActive { get; init; }
    
    /// <summary>
    /// Gets whether DHCP is enabled for IPv4.
    /// </summary>
    public bool DhcpEnabled { get; init; }
    
    /// <summary>
    /// Gets the static IPv4 configuration (if DHCP is disabled).
    /// </summary>
    public StaticIPv4Config? StaticIPv4 { get; init; }
    
    /// <summary>
    /// Gets whether DHCP is enabled for IPv6.
    /// </summary>
    public bool IPv6DhcpEnabled { get; init; }
    
    /// <summary>
    /// Gets the static IPv6 configuration (if DHCP is disabled).
    /// </summary>
    public StaticIPv6Config? StaticIPv6 { get; init; }
    
    /// <summary>
    /// Gets the DNS configuration.
    /// </summary>
    public required DnsConfiguration DnsConfig { get; init; }
    
    /// <summary>
    /// Gets the proxy configuration.
    /// </summary>
    public ProxyConfiguration? ProxyConfig { get; init; }
    
    /// <summary>
    /// Gets the firewall configuration.
    /// </summary>
    public FirewallConfiguration? FirewallConfig { get; init; }
    
    /// <summary>
    /// Gets the wireless configuration (for wireless interfaces).
    /// </summary>
    public WirelessConfiguration? WirelessConfig { get; init; }
    
    /// <summary>
    /// Gets the VPN configuration.
    /// </summary>
    public VpnConfiguration? VpnConfig { get; init; }
    
    /// <summary>
    /// Gets the QoS (Quality of Service) configuration.
    /// </summary>
    public QosConfiguration? QosConfig { get; init; }
    
    /// <summary>
    /// Gets when this configuration was created.
    /// </summary>
    public DateTime CreatedAt { get; init; }
    
    /// <summary>
    /// Gets when this configuration was last modified.
    /// </summary>
    public DateTime LastModified { get; init; }
    
    /// <summary>
    /// Gets who created this configuration.
    /// </summary>
    [MaxLength(100)]
    public string? CreatedBy { get; init; }
    
    /// <summary>
    /// Gets who last modified this configuration.
    /// </summary>
    [MaxLength(100)]
    public string? ModifiedBy { get; init; }
    
    /// <summary>
    /// Gets additional configuration settings.
    /// </summary>
    public Dictionary<string, object>? AdditionalSettings { get; init; }
}

/// <summary>
/// Bandwidth usage metrics for network monitoring.
/// </summary>
public record BandwidthUsageMetrics : TenantScopedDto
{
    /// <summary>
    /// Gets the unique identifier for the bandwidth metrics record.
    /// </summary>
    public required Guid Id { get; init; }
    
    /// <summary>
    /// Gets the agent ID these metrics belong to.
    /// </summary>
    public required Guid AgentId { get; init; }
    
    /// <summary>
    /// Gets the network interface these metrics apply to.
    /// </summary>
    [MaxLength(100)]
    public required string InterfaceName { get; init; }
    
    /// <summary>
    /// Gets the time period these metrics cover.
    /// </summary>
    public required MetricsPeriod Period { get; init; }
    
    /// <summary>
    /// Gets the start time of the measurement period.
    /// </summary>
    public DateTime PeriodStart { get; init; }
    
    /// <summary>
    /// Gets the end time of the measurement period.
    /// </summary>
    public DateTime PeriodEnd { get; init; }
    
    /// <summary>
    /// Gets the total bytes transmitted during the period.
    /// </summary>
    public long BytesTransmitted { get; init; }
    
    /// <summary>
    /// Gets the total bytes received during the period.
    /// </summary>
    public long BytesReceived { get; init; }
    
    /// <summary>
    /// Gets the total bytes transferred (transmitted + received).
    /// </summary>
    public long TotalBytes => BytesTransmitted + BytesReceived;
    
    /// <summary>
    /// Gets the peak transmission rate in bytes per second.
    /// </summary>
    public long PeakTransmissionRate { get; init; }
    
    /// <summary>
    /// Gets the peak reception rate in bytes per second.
    /// </summary>
    public long PeakReceptionRate { get; init; }
    
    /// <summary>
    /// Gets the average transmission rate in bytes per second.
    /// </summary>
    public long AverageTransmissionRate { get; init; }
    
    /// <summary>
    /// Gets the average reception rate in bytes per second.
    /// </summary>
    public long AverageReceptionRate { get; init; }
    
    /// <summary>
    /// Gets the bandwidth utilization percentage (0-100).
    /// </summary>
    [Range(0, 100)]
    public double UtilizationPercentage { get; init; }
    
    /// <summary>
    /// Gets the peak utilization percentage during the period.
    /// </summary>
    [Range(0, 100)]
    public double PeakUtilizationPercentage { get; init; }
    
    /// <summary>
    /// Gets the number of packets transmitted.
    /// </summary>
    public long PacketsTransmitted { get; init; }
    
    /// <summary>
    /// Gets the number of packets received.
    /// </summary>
    public long PacketsReceived { get; init; }
    
    /// <summary>
    /// Gets the number of transmit errors.
    /// </summary>
    public long TransmitErrors { get; init; }
    
    /// <summary>
    /// Gets the number of receive errors.
    /// </summary>
    public long ReceiveErrors { get; init; }
    
    /// <summary>
    /// Gets the number of dropped packets.
    /// </summary>
    public long DroppedPackets { get; init; }
    
    /// <summary>
    /// Gets the number of collisions detected.
    /// </summary>
    public long Collisions { get; init; }
    
    /// <summary>
    /// Gets bandwidth usage by application or protocol.
    /// </summary>
    public List<ApplicationBandwidthUsage> ApplicationUsage { get; init; } = new();
    
    /// <summary>
    /// Gets bandwidth usage by time intervals within the period.
    /// </summary>
    public List<TimeIntervalUsage> TimeIntervals { get; init; } = new();
    
    /// <summary>
    /// Gets quality of service metrics.
    /// </summary>
    public QualityOfServiceMetrics? QosMetrics { get; init; }
    
    /// <summary>
    /// Gets when these metrics were collected.
    /// </summary>
    public DateTime CollectedAt { get; init; }
}

#region Supporting Types and Enums

/// <summary>
/// Network interface type enumeration.
/// </summary>
public enum NetworkInterfaceType
{
    Ethernet,
    Wireless,
    Loopback,
    PPP,
    TokenRing,
    Slip,
    Tunnel,
    Unknown
}

/// <summary>
/// Interface operational status enumeration.
/// </summary>
public enum InterfaceStatus
{
    Up,
    Down,
    Testing,
    Unknown,
    Dormant,
    NotPresent,
    LowerLayerDown
}

/// <summary>
/// Interface administrative status enumeration.
/// </summary>
public enum InterfaceAdminStatus
{
    Up,
    Down,
    Testing
}

/// <summary>
/// IPv4 address information.
/// </summary>
public record IPv4AddressInfo
{
    /// <summary>
    /// Gets the IPv4 address.
    /// </summary>
    public required string Address { get; init; }
    
    /// <summary>
    /// Gets the subnet mask.
    /// </summary>
    public required string SubnetMask { get; init; }
    
    /// <summary>
    /// Gets the prefix length (CIDR notation).
    /// </summary>
    public int PrefixLength { get; init; }
    
    /// <summary>
    /// Gets whether this is the primary address.
    /// </summary>
    public bool IsPrimary { get; init; }
    
    /// <summary>
    /// Gets the address type.
    /// </summary>
    public IPv4AddressType Type { get; init; }
}

/// <summary>
/// IPv6 address information.
/// </summary>
public record IPv6AddressInfo
{
    /// <summary>
    /// Gets the IPv6 address.
    /// </summary>
    public required string Address { get; init; }
    
    /// <summary>
    /// Gets the prefix length.
    /// </summary>
    public int PrefixLength { get; init; }
    
    /// <summary>
    /// Gets the address scope.
    /// </summary>
    public IPv6AddressScope Scope { get; init; }
    
    /// <summary>
    /// Gets the address type.
    /// </summary>
    public IPv6AddressType Type { get; init; }
    
    /// <summary>
    /// Gets whether this is a temporary address.
    /// </summary>
    public bool IsTemporary { get; init; }
}

/// <summary>
/// DHCP configuration information.
/// </summary>
public record DhcpInfo
{
    /// <summary>
    /// Gets whether DHCP is enabled.
    /// </summary>
    public bool IsEnabled { get; init; }
    
    /// <summary>
    /// Gets the DHCP server address.
    /// </summary>
    public string? ServerAddress { get; init; }
    
    /// <summary>
    /// Gets when the lease was obtained.
    /// </summary>
    public DateTime? LeaseObtained { get; init; }
    
    /// <summary>
    /// Gets when the lease expires.
    /// </summary>
    public DateTime? LeaseExpires { get; init; }
    
    /// <summary>
    /// Gets the lease duration.
    /// </summary>
    public TimeSpan? LeaseDuration { get; init; }
}

/// <summary>
/// Network traffic statistics.
/// </summary>
public record NetworkStatistics
{
    /// <summary>
    /// Gets the total bytes sent.
    /// </summary>
    public long BytesSent { get; init; }
    
    /// <summary>
    /// Gets the total bytes received.
    /// </summary>
    public long BytesReceived { get; init; }
    
    /// <summary>
    /// Gets the total packets sent.
    /// </summary>
    public long PacketsSent { get; init; }
    
    /// <summary>
    /// Gets the total packets received.
    /// </summary>
    public long PacketsReceived { get; init; }
    
    /// <summary>
    /// Gets the number of send errors.
    /// </summary>
    public long SendErrors { get; init; }
    
    /// <summary>
    /// Gets the number of receive errors.
    /// </summary>
    public long ReceiveErrors { get; init; }
    
    /// <summary>
    /// Gets the number of dropped packets.
    /// </summary>
    public long DroppedPackets { get; init; }
    
    /// <summary>
    /// Gets when these statistics were last reset.
    /// </summary>
    public DateTime? LastReset { get; init; }
}

/// <summary>
/// Static IPv4 configuration.
/// </summary>
public record StaticIPv4Config
{
    /// <summary>
    /// Gets the static IP address.
    /// </summary>
    public required string IpAddress { get; init; }
    
    /// <summary>
    /// Gets the subnet mask.
    /// </summary>
    public required string SubnetMask { get; init; }
    
    /// <summary>
    /// Gets the default gateway.
    /// </summary>
    public string? DefaultGateway { get; init; }
    
    /// <summary>
    /// Gets the preferred DNS server.
    /// </summary>
    public string? PreferredDns { get; init; }
    
    /// <summary>
    /// Gets the alternate DNS server.
    /// </summary>
    public string? AlternateDns { get; init; }
}

/// <summary>
/// Static IPv6 configuration.
/// </summary>
public record StaticIPv6Config
{
    /// <summary>
    /// Gets the static IPv6 address.
    /// </summary>
    public required string IpAddress { get; init; }
    
    /// <summary>
    /// Gets the prefix length.
    /// </summary>
    public int PrefixLength { get; init; }
    
    /// <summary>
    /// Gets the default gateway.
    /// </summary>
    public string? DefaultGateway { get; init; }
    
    /// <summary>
    /// Gets the preferred DNS server.
    /// </summary>
    public string? PreferredDns { get; init; }
    
    /// <summary>
    /// Gets the alternate DNS server.
    /// </summary>
    public string? AlternateDns { get; init; }
}

/// <summary>
/// DNS configuration settings.
/// </summary>
public record DnsConfiguration
{
    /// <summary>
    /// Gets whether DNS is obtained automatically.
    /// </summary>
    public bool AutomaticDns { get; init; }
    
    /// <summary>
    /// Gets the primary DNS server.
    /// </summary>
    public string? PrimaryDns { get; init; }
    
    /// <summary>
    /// Gets the secondary DNS server.
    /// </summary>
    public string? SecondaryDns { get; init; }
    
    /// <summary>
    /// Gets additional DNS servers.
    /// </summary>
    public List<string> AdditionalDnsServers { get; init; } = new();
    
    /// <summary>
    /// Gets the DNS suffix search list.
    /// </summary>
    public List<string> SearchSuffixes { get; init; } = new();
    
    /// <summary>
    /// Gets whether to register this connection's addresses in DNS.
    /// </summary>
    public bool RegisterInDns { get; init; }
    
    /// <summary>
    /// Gets whether to use this connection's DNS suffix in DNS registration.
    /// </summary>
    public bool UseDnsSuffix { get; init; }
}

/// <summary>
/// Proxy configuration settings.
/// </summary>
public record ProxyConfiguration
{
    /// <summary>
    /// Gets whether proxy is enabled.
    /// </summary>
    public bool IsEnabled { get; init; }
    
    /// <summary>
    /// Gets the proxy type.
    /// </summary>
    public ProxyType Type { get; init; }
    
    /// <summary>
    /// Gets the proxy server address.
    /// </summary>
    public string? ServerAddress { get; init; }
    
    /// <summary>
    /// Gets the proxy server port.
    /// </summary>
    public int Port { get; init; }
    
    /// <summary>
    /// Gets the username for proxy authentication.
    /// </summary>
    public string? Username { get; init; }
    
    /// <summary>
    /// Gets whether authentication is required.
    /// </summary>
    public bool RequiresAuthentication { get; init; }
    
    /// <summary>
    /// Gets the addresses that bypass the proxy.
    /// </summary>
    public List<string> BypassList { get; init; } = new();
    
    /// <summary>
    /// Gets whether to bypass proxy for local addresses.
    /// </summary>
    public bool BypassProxyForLocal { get; init; }
}

/// <summary>
/// Firewall configuration settings.
/// </summary>
public record FirewallConfiguration
{
    /// <summary>
    /// Gets whether the firewall is enabled.
    /// </summary>
    public bool IsEnabled { get; init; }
    
    /// <summary>
    /// Gets the firewall profile.
    /// </summary>
    public FirewallProfile Profile { get; init; }
    
    /// <summary>
    /// Gets the default inbound rule action.
    /// </summary>
    public FirewallAction DefaultInboundAction { get; init; }
    
    /// <summary>
    /// Gets the default outbound rule action.
    /// </summary>
    public FirewallAction DefaultOutboundAction { get; init; }
    
    /// <summary>
    /// Gets whether to block all inbound connections.
    /// </summary>
    public bool BlockAllInbound { get; init; }
    
    /// <summary>
    /// Gets whether notifications are enabled.
    /// </summary>
    public bool NotificationsEnabled { get; init; }
    
    /// <summary>
    /// Gets custom firewall rules.
    /// </summary>
    public List<FirewallRule> CustomRules { get; init; } = new();
}

/// <summary>
/// Wireless network configuration settings.
/// </summary>
public record WirelessConfiguration
{
    /// <summary>
    /// Gets the SSID (network name).
    /// </summary>
    public required string Ssid { get; init; }
    
    /// <summary>
    /// Gets the security type.
    /// </summary>
    public WirelessSecurity SecurityType { get; init; }
    
    /// <summary>
    /// Gets the authentication method.
    /// </summary>
    public WirelessAuthentication AuthenticationMethod { get; init; }
    
    /// <summary>
    /// Gets the encryption method.
    /// </summary>
    public WirelessEncryption EncryptionMethod { get; init; }
    
    /// <summary>
    /// Gets whether this is a hidden network.
    /// </summary>
    public bool IsHiddenNetwork { get; init; }
    
    /// <summary>
    /// Gets whether to connect automatically.
    /// </summary>
    public bool AutoConnect { get; init; }
    
    /// <summary>
    /// Gets the connection priority.
    /// </summary>
    public int Priority { get; init; }
}

/// <summary>
/// VPN configuration settings.
/// </summary>
public record VpnConfiguration
{
    /// <summary>
    /// Gets the VPN connection name.
    /// </summary>
    public required string ConnectionName { get; init; }
    
    /// <summary>
    /// Gets the VPN type.
    /// </summary>
    public VpnType Type { get; init; }
    
    /// <summary>
    /// Gets the VPN server address.
    /// </summary>
    public required string ServerAddress { get; init; }
    
    /// <summary>
    /// Gets the authentication method.
    /// </summary>
    public VpnAuthentication AuthenticationMethod { get; init; }
    
    /// <summary>
    /// Gets whether to use the VPN connection for all traffic.
    /// </summary>
    public bool UseAsDefaultGateway { get; init; }
    
    /// <summary>
    /// Gets whether the connection is always on.
    /// </summary>
    public bool AlwaysOn { get; init; }
    
    /// <summary>
    /// Gets split tunneling configuration.
    /// </summary>
    public SplitTunnelingConfig? SplitTunneling { get; init; }
}

/// <summary>
/// Quality of Service configuration settings.
/// </summary>
public record QosConfiguration
{
    /// <summary>
    /// Gets whether QoS is enabled.
    /// </summary>
    public bool IsEnabled { get; init; }
    
    /// <summary>
    /// Gets the QoS policy name.
    /// </summary>
    public string? PolicyName { get; init; }
    
    /// <summary>
    /// Gets the bandwidth limit in bits per second.
    /// </summary>
    public long? BandwidthLimit { get; init; }
    
    /// <summary>
    /// Gets the traffic shaping rules.
    /// </summary>
    public List<TrafficShapingRule> TrafficShaping { get; init; } = new();
    
    /// <summary>
    /// Gets the priority classes.
    /// </summary>
    public List<QosPriorityClass> PriorityClasses { get; init; } = new();
}

/// <summary>
/// Metrics period enumeration.
/// </summary>
public enum MetricsPeriod
{
    RealTime,
    LastMinute,
    Last5Minutes,
    Last15Minutes,
    LastHour,
    Last6Hours,
    Last24Hours,
    LastWeek,
    LastMonth
}

/// <summary>
/// Application bandwidth usage information.
/// </summary>
public record ApplicationBandwidthUsage
{
    /// <summary>
    /// Gets the application or process name.
    /// </summary>
    public required string ApplicationName { get; init; }
    
    /// <summary>
    /// Gets the process ID.
    /// </summary>
    public int? ProcessId { get; init; }
    
    /// <summary>
    /// Gets the bytes transmitted by this application.
    /// </summary>
    public long BytesTransmitted { get; init; }
    
    /// <summary>
    /// Gets the bytes received by this application.
    /// </summary>
    public long BytesReceived { get; init; }
    
    /// <summary>
    /// Gets the protocol used (TCP, UDP, etc.).
    /// </summary>
    public string? Protocol { get; init; }
    
    /// <summary>
    /// Gets the remote endpoints connected to.
    /// </summary>
    public List<string> RemoteEndpoints { get; init; } = new();
}

/// <summary>
/// Time interval usage information.
/// </summary>
public record TimeIntervalUsage
{
    /// <summary>
    /// Gets the start time of the interval.
    /// </summary>
    public DateTime IntervalStart { get; init; }
    
    /// <summary>
    /// Gets the end time of the interval.
    /// </summary>
    public DateTime IntervalEnd { get; init; }
    
    /// <summary>
    /// Gets the bytes transmitted during this interval.
    /// </summary>
    public long BytesTransmitted { get; init; }
    
    /// <summary>
    /// Gets the bytes received during this interval.
    /// </summary>
    public long BytesReceived { get; init; }
    
    /// <summary>
    /// Gets the utilization percentage for this interval.
    /// </summary>
    public double UtilizationPercentage { get; init; }
}

/// <summary>
/// Quality of Service metrics.
/// </summary>
public record QualityOfServiceMetrics
{
    /// <summary>
    /// Gets the average latency in milliseconds.
    /// </summary>
    public double AverageLatencyMs { get; init; }
    
    /// <summary>
    /// Gets the packet loss percentage.
    /// </summary>
    public double PacketLossPercentage { get; init; }
    
    /// <summary>
    /// Gets the jitter in milliseconds.
    /// </summary>
    public double JitterMs { get; init; }
    
    /// <summary>
    /// Gets the throughput in bits per second.
    /// </summary>
    public long ThroughputBps { get; init; }
    
    /// <summary>
    /// Gets the connection quality score (0-100).
    /// </summary>
    public int QualityScore { get; init; }
}

/// <summary>
/// IPv4 address type enumeration.
/// </summary>
public enum IPv4AddressType
{
    Unicast,
    Multicast,
    Broadcast,
    Anycast
}

/// <summary>
/// IPv6 address scope enumeration.
/// </summary>
public enum IPv6AddressScope
{
    Unknown,
    InterfaceLocal,
    LinkLocal,
    SubnetLocal,
    AdminLocal,
    SiteLocal,
    OrganizationLocal,
    Global
}

/// <summary>
/// IPv6 address type enumeration.
/// </summary>
public enum IPv6AddressType
{
    Unicast,
    Multicast,
    Anycast
}

/// <summary>
/// Proxy type enumeration.
/// </summary>
public enum ProxyType
{
    HTTP,
    HTTPS,
    SOCKS4,
    SOCKS5,
    FTP
}

/// <summary>
/// Firewall profile enumeration.
/// </summary>
public enum FirewallProfile
{
    Domain,
    Private,
    Public
}

/// <summary>
/// Firewall action enumeration.
/// </summary>
public enum FirewallAction
{
    Allow,
    Block
}

/// <summary>
/// Firewall rule definition.
/// </summary>
public record FirewallRule
{
    /// <summary>
    /// Gets the rule name.
    /// </summary>
    public required string Name { get; init; }
    
    /// <summary>
    /// Gets the rule direction.
    /// </summary>
    public FirewallDirection Direction { get; init; }
    
    /// <summary>
    /// Gets the rule action.
    /// </summary>
    public FirewallAction Action { get; init; }
    
    /// <summary>
    /// Gets the protocol.
    /// </summary>
    public string? Protocol { get; init; }
    
    /// <summary>
    /// Gets the local port range.
    /// </summary>
    public string? LocalPorts { get; init; }
    
    /// <summary>
    /// Gets the remote port range.
    /// </summary>
    public string? RemotePorts { get; init; }
    
    /// <summary>
    /// Gets the local address range.
    /// </summary>
    public string? LocalAddresses { get; init; }
    
    /// <summary>
    /// Gets the remote address range.
    /// </summary>
    public string? RemoteAddresses { get; init; }
    
    /// <summary>
    /// Gets whether the rule is enabled.
    /// </summary>
    public bool IsEnabled { get; init; }
}

/// <summary>
/// Firewall direction enumeration.
/// </summary>
public enum FirewallDirection
{
    Inbound,
    Outbound
}

/// <summary>
/// Wireless security type enumeration.
/// </summary>
public enum WirelessSecurity
{
    None,
    WEP,
    WPA,
    WPA2,
    WPA3,
    WPA2Enterprise,
    WPA3Enterprise
}

/// <summary>
/// Wireless authentication method enumeration.
/// </summary>
public enum WirelessAuthentication
{
    Open,
    SharedKey,
    WPA,
    WPAPSK,
    WPA2,
    WPA2PSK,
    WPA3,
    WPA3SAE
}

/// <summary>
/// Wireless encryption method enumeration.
/// </summary>
public enum WirelessEncryption
{
    None,
    WEP,
    TKIP,
    AES,
    CCMP
}

/// <summary>
/// VPN type enumeration.
/// </summary>
public enum VpnType
{
    PPTP,
    L2TP,
    SSTP,
    IKEv2,
    IPSec,
    OpenVPN,
    WireGuard
}

/// <summary>
/// VPN authentication method enumeration.
/// </summary>
public enum VpnAuthentication
{
    Password,
    Certificate,
    SmartCard,
    EAP,
    PreSharedKey
}

/// <summary>
/// Split tunneling configuration.
/// </summary>
public record SplitTunnelingConfig
{
    /// <summary>
    /// Gets whether split tunneling is enabled.
    /// </summary>
    public bool IsEnabled { get; init; }
    
    /// <summary>
    /// Gets the mode of split tunneling.
    /// </summary>
    public SplitTunnelingMode Mode { get; init; }
    
    /// <summary>
    /// Gets the list of addresses or applications for split tunneling.
    /// </summary>
    public List<string> Rules { get; init; } = new();
}

/// <summary>
/// Split tunneling mode enumeration.
/// </summary>
public enum SplitTunnelingMode
{
    IncludeList,
    ExcludeList
}

/// <summary>
/// Traffic shaping rule.
/// </summary>
public record TrafficShapingRule
{
    /// <summary>
    /// Gets the rule name.
    /// </summary>
    public required string Name { get; init; }
    
    /// <summary>
    /// Gets the traffic condition.
    /// </summary>
    public string? Condition { get; init; }
    
    /// <summary>
    /// Gets the bandwidth limit in bits per second.
    /// </summary>
    public long BandwidthLimit { get; init; }
    
    /// <summary>
    /// Gets the priority level.
    /// </summary>
    public int Priority { get; init; }
}

/// <summary>
/// QoS priority class definition.
/// </summary>
public record QosPriorityClass
{
    /// <summary>
    /// Gets the class name.
    /// </summary>
    public required string Name { get; init; }
    
    /// <summary>
    /// Gets the priority level (1-7).
    /// </summary>
    [Range(1, 7)]
    public int Priority { get; init; }
    
    /// <summary>
    /// Gets the bandwidth allocation percentage.
    /// </summary>
    [Range(0, 100)]
    public double BandwidthAllocation { get; init; }
    
    /// <summary>
    /// Gets the traffic matching criteria.
    /// </summary>
    public List<string> MatchingCriteria { get; init; } = new();
}

#endregion