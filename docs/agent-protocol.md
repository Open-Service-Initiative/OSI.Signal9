# Agent protocol

How an agent talks to `api`, and how the two stay compatible. The constants live in `OSI.Signal9.Contracts` (`AgentProtocol`, `HubRoutes`), and both the agent and `api` build from them.

## Versions

Agent and `api` versions are paired: every project takes its version from `Directory.Build.props`, so an agent reports the version of the `api` it was built with.

Every agent request carries the agent's version, a [semantic version](https://semver.org/spec/v2.0.0.html), in the `Signal9-Agent-Version` header: enrollment, token requests, the agent's own endpoints and the hub. `api` records it on the agent (`AgentDto.agentVersion`).

The deployment supports a range of agent versions, inclusive, set by `AgentVersions:Minimum` and `AgentVersions:Maximum`. By default it's from `api`'s `major.minor.0` up to `api`'s own version: `api` is updated first, and an agent newer than it isn't supported.

## Unsupported agent version

When the header is missing, malformed or outside the supported range, `api` refuses the request with **400 Bad Request** ([RFC 9110 §15.5.1](https://www.rfc-editor.org/rfc/rfc9110#section-15.5.1)):

- At the agent's endpoints and the hub, an `application/problem+json` body ([RFC 9457](https://www.rfc-editor.org/rfc/rfc9457)) whose `type` is this section's URL, `https://github.com/Open-Service-Initiative/OSI.Signal9/blob/main/docs/agent-protocol.md#unsupported-agent-version`. Its `minimumVersion` and `maximumVersion` members give the supported range.

  ```json
  {
    "type": "https://github.com/Open-Service-Initiative/OSI.Signal9/blob/main/docs/agent-protocol.md#unsupported-agent-version",
    "title": "Agent version not supported",
    "status": 400,
    "detail": "Agent version '0.0.1' is not supported; this deployment supports agents from 0.1.0 to 0.1.0. Update the agent.",
    "minimumVersion": "0.1.0",
    "maximumVersion": "0.1.0"
  }
  ```

- At the token endpoint, the OAuth error ([RFC 6749 §5.2](https://www.rfc-editor.org/rfc/rfc6749#section-5.2)) `unsupported_agent_version`, given only to an authenticated agent, after `agent_revoked`.

The agent then stops: it logs a critical error naming its version and the supported range, and sends nothing more until it's updated. Retrying can't help.

426 Upgrade Required isn't used: [RFC 9110 §15.5.22](https://www.rfc-editor.org/rfc/rfc9110#section-15.5.22) keeps it for switching protocols with the `Upgrade` header, not for updating software.

## Compatibility rules

- An agent tolerates enum values newer than itself: an unknown value reads as undefined rather than failing the response, so a command type it doesn't know fails as "not supported by this agent version".
- Agent paths, the token endpoint and the `agent` scope are never written out on either side; both use `AgentProtocol`.
