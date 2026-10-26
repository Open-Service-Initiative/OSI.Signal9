namespace OSI.Signal9.Contracts.Agents;

/// <summary>
/// The agent ↔ api contract: the paths, token request and version check both sides build from, so neither keeps a copy
/// that could drift. Every agent request carries <see cref="VersionHeader"/>; api refuses an agent whose version is
/// outside the deployment's supported range (<see cref="UnsupportedVersion"/>).
/// </summary>
public static class AgentProtocol
{
    /// <summary>The token endpoint, at api's mTLS alias, where an agent gets its access tokens.</summary>
    public const string TokenEndpoint = "connect/token";

    /// <summary>The only scope an agent asks for, and only agents can obtain.</summary>
    public const string Scope = "agent";

    /// <summary>
    /// The request header carrying the agent's version, a semantic version (SemVer 2.0.0), on every request it sends:
    /// enrollment, token requests, its own endpoints and the hub.
    /// </summary>
    public const string VersionHeader = "Signal9-Agent-Version";

    /// <summary>Route templates, relative to the api host's root. <c>{agentId:guid}</c> is the agent's ID.</summary>
    public static class Routes
    {
        /// <summary>POST: enrollment (with no access token).</summary>
        public const string Enroll = "api/v1/agents";

        public const string Heartbeat = "api/v1/agents/{agentId:guid}/heartbeat";
        public const string Telemetry = "api/v1/agents/{agentId:guid}/telemetry";
        public const string PendingCommands = "api/v1/agents/{agentId:guid}/pending-commands";
        public const string Certificate = "api/v1/agents/{agentId:guid}/certificate";

        /// <summary>A command's result, below the command's canonical URL.</summary>
        public const string CommandResult = "result";

        public static string HeartbeatOf(Guid agentId) => Of(Heartbeat, agentId);
        public static string TelemetryOf(Guid agentId) => Of(Telemetry, agentId);
        public static string PendingCommandsOf(Guid agentId) => Of(PendingCommands, agentId);
        public static string CertificateOf(Guid agentId) => Of(Certificate, agentId);

        /// <summary>Where the result of the command at <paramref name="commandUrl"/>, its canonical URL, is reported.</summary>
        public static string CommandResultOf(string commandUrl) => $"{commandUrl.TrimStart('/')}/{CommandResult}";

        private static string Of(string template, Guid agentId) => template.Replace("{agentId:guid}", agentId.ToString());
    }

    /// <summary>
    /// How api refuses an agent whose version it doesn't support (or that sent none): 400, because the request can't be
    /// served as sent (RFC 9110 §15.5.1), never 426, which RFC 9110 §15.5.22 keeps for protocol upgrades. The agent
    /// stops rather than retrying, until it's updated.
    /// </summary>
    public static class UnsupportedVersion
    {
        /// <summary>
        /// The <c>application/problem+json</c> type (RFC 9457) of the refusal at api's own endpoints. Its
        /// <c>minimumVersion</c> and <c>maximumVersion</c> members give the supported range.
        /// </summary>
        public const string ProblemType =
            "https://github.com/Open-Service-Initiative/OSI.Signal9/blob/main/docs/agent-protocol.md#unsupported-agent-version";

        /// <summary>The OAuth error code (RFC 6749 §5.2, §8.5) of the refusal at the token endpoint.</summary>
        public const string Error = "unsupported_agent_version";
    }
}
