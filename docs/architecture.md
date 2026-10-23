# Architecture

## Runtime topology

```mermaid
flowchart LR
    C[Avalonia client] -->|HTTPS: request/status| S[ASP.NET signaling]
    A[Owner-visible agent] -->|HTTPS: enroll/heartbeat/approve| S
    C <-->|permission-scoped WebSocket| R[Paired relay]
    A <-->|permission-scoped WebSocket| R
    A --> P[OS adapter]
    P --> W[Windows APIs]
    P --> L[PipeWire / X11]
    P --> M[ScreenCaptureKit]
```

The signaling service coordinates identities and consent. A controller and agent receive different signed tickets for one session. The relay accepts exactly one peer in each role and forwards framed WebSocket messages without interpreting their content.

For production screen media, replace the WebSocket media path with WebRTC. Signaling should exchange offers, answers, and ICE candidates; media should go peer-to-peer when possible and through TURN only when necessary.

## Session lifecycle

```mermaid
sequenceDiagram
    participant Client
    participant Server
    participant Agent
    Client->>Server: Create request (operator auth)
    Server-->>Client: Pending session ID
    Agent->>Server: Poll pending requests (device auth)
    Agent->>Agent: Show operator and requested permissions
    Agent->>Server: Approve a subset
    Server-->>Agent: Short-lived agent ticket
    Client->>Server: Poll status
    Server-->>Client: Short-lived controller ticket
    Client->>Server: WebSocket + controller ticket
    Agent->>Server: WebSocket + agent ticket
    Server->>Server: Pair exactly one peer per role
    Client<<->>Agent: Permission-scoped messages
```

Tickets include the session ID, device ID, role, granted permissions, issue/expiry timestamps, and a random token ID. They are signed with HMAC-SHA-256 and checked using constant-time signature comparison.

## Project responsibilities

- `Core` has no network or UI dependency. It defines permission semantics and OS adapter contracts.
- `Protocol` owns bounded wire contracts and input/path validation.
- `Security` owns high-entropy credentials and signed session tickets.
- `Transport` owns framing, the HTTP client, and safe file assembly.
- `Signaling` owns enrollment/session state and relay pairing.
- `Agent` enforces the granted permission again before every sensitive operation.
- Platform projects are the only place native capture, input, and reboot code should live.

## Data-store seam

The current registries are process-local so the first milestone is easy to run. Replace them with PostgreSQL repositories before using more than one signaling instance. Session approval must be a compare-and-swap transaction so a request cannot be approved twice under race.

