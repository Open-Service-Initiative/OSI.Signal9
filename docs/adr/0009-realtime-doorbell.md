# Real-time hubs are a doorbell; agent actions with authority go over mTLS REST

On Azure, both hubs run on Azure SignalR Service so operators get managed scale-out. Self-hosted, SignalR runs inside the API, with Redis only once the API scales out. Azure SignalR ends each client connection itself and authenticates it with its own bearer token, so a certificate binding can't survive on that connection. The agent hub therefore only signals "you have work". Agents fetch commands, report results and send telemetry over REST to the API with mTLS and certificate-bound tokens. Revocation, confidentiality of command content and delivery to offline agents all rely on that fetch, not on the socket.

## Considered Options

- **SignalR inside the API with a Redis backplane everywhere.** Rejected: on Azure it gives up the managed scale-out that MSPs without in-house operations staff need.
- **Commands carried over Azure SignalR, with a custom proof of key possession on each connection.** Rejected: it means writing our own DPoP-style protocol, routes command content through the relay, and still needs the fetch path for offline agents.
- **Proxying Azure SignalR's client path through the API.** Rejected: the API would hold two sockets per client, which removes the offload Azure SignalR exists for.

## Consequences

Interactive real-time features such as a live shell or remote desktop will need their own per-session channels, designed separately. Dashboard events are small change notices; details are fetched through the BFF.
