# SwiftDesk

SwiftDesk is a modern, cross-platform remote support and desktop control application built with C# and .NET 10 LTS. It provides owner-consented remote support across Windows, Linux, and macOS with real-time screen sharing, remote mouse and keyboard control, chunked integrity-checked file transfer, attended access approval, and fine-grained session permissions.

---

## Architecture Overview

SwiftDesk separates coordination, transport, platform adapters, and user interface into decoupled projects:

```
[Avalonia Desktop Client]                      [ASP.NET Core Signaling]                        [Host Agent]
(RemoteDesk.Client)                                (RemoteDesk.Signaling)                    (RemoteDesk.Agent)
         |                                                   |                                       |
         |--- 1. Authenticated Session Request ------------->|                                       |
         |                                                   |<-- 2. Attended Consent & Approval ----|
         |<-- 3. Short-Lived Signed Ticket ------------------|--- 3. Short-Lived Signed Ticket ----->|
         |                                                   |                                       |
         |=== 4. Paired WebSocket Control & Relay Channel ===|=======================================|
         |    (SDP Offer / Answer & ICE Candidates Trickle)  |                                       |
         |                                                   |                                       |
         |========================= 5. Direct WebRTC P2P Stream =====================================>|
         |                    (Ultra-low latency data channel / STUN NAT traversal)                  |
```

---

## Current Status & Capabilities

| Capability | Status | Implementation Details |
| :--- | :--- | :--- |
| **Attended Approval & Consent** | **Implemented** | Explicit interactive prompt; device owner approves requested permissions |
| **Fine-Grained Permissions** | **Implemented** | Independent toggles for `ViewScreen`, `ControlInput`, `TransferFiles`, `RebootDevice` |
| **Short-Lived Signed Tickets** | **Implemented** | HMAC-SHA-256 session tickets with constant-time verification & short expiry |
| **Paired WebSocket Relay** | **Implemented** | Low-latency binary/text message relay between paired peers |
| **WebRTC Media Transport** | **Implemented** | P2P ICE negotiation (`stun.l.google.com:19302`) via `SIPSorcery` with relay fallback |
| **Windows Desktop Capture** | **Implemented** | High-performance desktop surface capture with real-time JPEG compression |
| **Windows Remote Input** | **Implemented** | Normalized coordinate translation & input injection via Win32 `SendInput` |
| **Safe File Transfer** | **Implemented** | Chunked transfer (48 KiB limit), path-traversal protection, and SHA-256 validation |
| **Avalonia Desktop UI** | **Implemented** | Cross-platform client with real-time viewport, input capture, and file transfer |
| **Linux Capture / Input** | **Implemented** | X11 surface capture via `libX11` `XGetImage` & synthetic input via `libXtst` XTest |
| **macOS Capture / Input** | **Implemented** | CoreGraphics desktop capture & Quartz Event Services input injection |
| **Database Persistence** | **Implemented** | Dual-mode: Production PostgreSQL (`Npgsql`) with connection pooling or in-memory for dev |
| **Audit Logging** | **Implemented** | Immutable audit trail for device enrollment, approvals, and connections (`/api/audit`) |
| **Privileged Reboot** | Seam Ready | Authorization boundary ready for dedicated privileged service |

---

## Repository Structure

```text
SwiftDesk/
├── src/
│   ├── RemoteDesk.Client/            # Cross-platform Avalonia UI & screen viewport
│   ├── RemoteDesk.Agent/             # Host agent with owner consent & execution loop
│   ├── RemoteDesk.Core/              # Shared models, permission flags, and platform interfaces
│   ├── RemoteDesk.Protocol/          # Wire contracts, WebRTC messages, and validation
│   ├── RemoteDesk.Security/          # High-entropy secret generation & HMAC ticket signing
│   ├── RemoteDesk.Transport/         # WebRTC peer coordinator, WebSocket protocol & file receiver
│   ├── RemoteDesk.Signaling/         # ASP.NET Core coordination, heartbeat & relay service
│   ├── RemoteDesk.Platform.Windows/  # Windows desktop capture & SendInput injection
│   ├── RemoteDesk.Platform.Linux/    # Linux capability adapter boundary
│   └── RemoteDesk.Platform.MacOS/    # macOS capability adapter boundary
├── tests/
│   ├── RemoteDesk.Core.Tests/        # Permission demand and authorization tests
│   ├── RemoteDesk.Protocol.Tests/    # Contract validation & WebRTC message tests
│   └── RemoteDesk.Security.Tests/    # Ticket issuance & tamper-resistance tests
├── docs/                             # Architecture, protocol, and security specifications
└── RemoteDesk.slnx                   # Solution file
```

---

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/10.0) (or the workspace's local runtime `work/.dotnet/dotnet.exe`)
- Windows, Linux, or macOS

---

## Build and Test

Using the installed .NET CLI or workspace runner:

```powershell
# Restore dependencies
dotnet restore RemoteDesk.slnx

# Build the solution
dotnet build RemoteDesk.slnx --no-restore

# Run the test suite
dotnet test --solution RemoteDesk.slnx --no-build --no-restore
```

*(If using the workspace runtime on Windows, prefix with `.\work\.dotnet\dotnet.exe`)*

---

## Quickstart: Running SwiftDesk Locally

### 1. Start the Signaling Server

```powershell
dotnet run --project src/RemoteDesk.Signaling
```
The server will start listening on `http://localhost:5080`.

### 2. Enroll and Start the Agent (Host Machine)

Open a terminal on the host machine and run:

```powershell
dotnet run --project src/RemoteDesk.Agent -- `
  --bootstrap-token local-bootstrap-change-me `
  --download-directory C:\Temp\SwiftDeskDownloads
```

The agent will enroll and output its credentials:
```text
Enrollment succeeded. Store these values in an OS-protected secret store:
REMOTEDESK_DEVICE_ID=dev_...
REMOTEDESK_DEVICE_SECRET=...
RemoteDesk agent dev_... on Windows
Available permissions: ViewScreen, ControlInput, TransferFiles
```

### 3. Start the Desktop Client (Viewer Machine)

In another terminal, launch the Avalonia UI:

```powershell
dotnet run --project src/RemoteDesk.Client
```

### 4. Connect and Control

1. Paste the `REMOTEDESK_DEVICE_ID` into the **Target device ID** field.
2. Select your desired permissions:
   - **View screen**: Streams the remote desktop.
   - **Control input**: Enables mouse clicks, movements, scroll, and keyboard events.
   - **Transfer files**: Allows sending files to the approved destination directory.
3. Click **Request connection**.
4. In the Agent console, confirm the attended access prompt (`y`).
5. The live remote desktop stream will appear in the client viewport with full interactive control!

---

## Security Invariants

- **Consent-First**: The host agent requires explicit owner approval for every session.
- **Least-Privilege Authorization**: Permissions are independently negotiated. The agent rejects ungranted actions even if signaled.
- **Short-Lived Signed Tickets**: Session tickets expire within minutes and are cryptographically verified using HMAC-SHA-256.
- **Path Traversal & Digest Verification**: Transferred files are checked against path traversal, size limits, and SHA-256 checksums before moving to final destinations.
- **No Covert Installation**: SwiftDesk does not silently install, bypass OS boundaries, or execute arbitrary shell commands.

---

## Next Roadmap Milestones

1. **Production Identity**: Integrate OIDC / MFA provider for enterprise operator authentication.
2. **Privileged Remote Reboot**: Implement separately installed, signed, least-privilege reboot service.
