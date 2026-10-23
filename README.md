# RemoteDesk

RemoteDesk is a cross-platform C#/.NET foundation for an owner-consented remote support tool. This repository implements the secure control plane first: device enrollment, heartbeats, attended approval, permission-scoped short-lived tickets, a paired WebSocket relay, an Avalonia desktop client, an agent, and integrity-checked file transfer.

It does **not** silently install, bypass operating-system permissions, or expose arbitrary command execution.

## Current status

| Capability | Status |
| --- | --- |
| Device enrollment and authenticated heartbeat | Implemented (in-memory server store) |
| Attended connection request and approval | Implemented |
| Separate view/input/file/reboot permissions | Implemented |
| Short-lived signed controller/agent tickets | Implemented |
| Paired WebSocket data relay | Implemented |
| Safe file receive with size, path, offset, and SHA-256 checks | Implemented |
| Cross-platform Avalonia connection UI | Implemented |
| Windows desktop capture/input | Adapter boundary only |
| Linux PipeWire/X11 capture/input | Adapter boundary only |
| macOS ScreenCaptureKit/input | Adapter boundary only |
| Video encoding, WebRTC, ICE/STUN/TURN | Not yet implemented |
| Persistent PostgreSQL device/session store | Not yet implemented |
| Privileged remote reboot service | Not yet implemented |

The capability probe reports native features as unavailable until their real OS adapters are linked. The agent therefore cannot approve permissions it cannot fulfill.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)
- Windows, Linux, or macOS for the client/agent

## Build and test

```powershell
dotnet restore RemoteDesk.slnx
dotnet build RemoteDesk.slnx --no-restore
dotnet test --solution RemoteDesk.slnx --no-build --no-restore
```

## Local end-to-end file transfer

1. Start the signaling server:

   ```powershell
   dotnet run --project src/RemoteDesk.Signaling
   ```

2. Enroll and start an agent. Choose the receive directory explicitly:

   ```powershell
   dotnet run --project src/RemoteDesk.Agent -- `
     --bootstrap-token local-bootstrap-change-me `
     --download-directory C:\Temp\RemoteDeskDownloads
   ```

   The agent prints a device ID and secret once. Put them in an OS-protected secret store for later runs. Do not commit them.

3. Start the desktop client:

   ```powershell
   dotnet run --project src/RemoteDesk.Client
   ```

4. Enter the printed device ID, select only **Transfer files**, and request a connection. Approve the request in the agent console, enter an absolute source-file path in the client, and send it.

The development operator key is `local-operator-change-me`. All local defaults are refused when the signaling server runs outside the Development environment.

## Production configuration

Set these through a secret manager or environment variables:

```text
RemoteDesk__BootstrapToken=<high-entropy enrollment token>
RemoteDesk__OperatorKey=<replace with real administrator authentication>
RemoteDesk__SigningKey=<at least 32 random bytes, represented as a secret string>
RemoteDesk__RequestLifetimeMinutes=5
RemoteDesk__TicketLifetimeMinutes=10
```

The single operator key is deliberately a development seam, not a production identity system. Before internet deployment, replace it with an OIDC provider, MFA, named operators, revocation, audit storage, rate limiting, TLS termination, and a persistent database.

## Repository layout

```text
src/
  RemoteDesk.Client/            Avalonia desktop connection UI
  RemoteDesk.Agent/             owner-visible host agent
  RemoteDesk.Core/              permissions, sessions, platform interfaces
  RemoteDesk.Protocol/          API and control-message contracts
  RemoteDesk.Security/          secrets and signed session tickets
  RemoteDesk.Transport/         API/WebSocket helpers and safe file receiver
  RemoteDesk.Signaling/         ASP.NET Core coordination and relay service
  RemoteDesk.Platform.Windows/  Windows capability boundary
  RemoteDesk.Platform.Linux/    Linux capability boundary
  RemoteDesk.Platform.MacOS/    macOS capability boundary
tests/                          xUnit unit tests
docs/                           architecture and security notes
```

See [architecture.md](docs/architecture.md), [protocol.md](docs/protocol.md), and [security.md](docs/security.md) before extending the native adapters.

## Recommended next milestones

1. Add PostgreSQL persistence plus OIDC/MFA and immutable audit events.
2. Implement Windows capture using Windows Graphics Capture or Desktop Duplication in the Windows adapter.
3. Add a video encoder and WebRTC transport; keep control/file messages on separately authorized data channels.
4. Add input only after view-only sessions are stable, with explicit local consent and locked-screen/integrity tests.
5. Add PipeWire portal and ScreenCaptureKit adapters, preserving their native permission prompts.
6. Implement reboot as a separately installed, signed, least-privilege service with confirmation and reconnect auditing.

