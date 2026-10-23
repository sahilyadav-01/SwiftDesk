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

## Enterprise Security & Architecture Additions

### Enterprise Production Identity (OIDC & MFA)
- **Token Verification**: Evaluates OpenID Connect (OIDC) JWT tokens using asymmetric RSA public keys (`RS256`) or HMAC keys (`HS256`).
- **MFA Enforcement**: Evaluates `amr` (Authentication Methods Reference) and `acr` claims. Requests without verified multi-factor credentials (e.g., FIDO2, WebAuthn, OTP, hardware key) receive `403 Forbidden` (`mfa_required`).
- **Role-Based Access Control (RBAC)**: Validates enterprise operator roles (`SupportOperator`, `Administrator`).
- **Identity Attribution**: Binds operator identity to validated claims (`sub`, `name`, `email`) rather than client-supplied strings, guaranteeing an unforgeable PostgreSQL audit trail.

### Privileged Remote Reboot Service
- **Separation of Privilege**: The interactive user agent never runs with root/SYSTEM privileges. Screen capture and input injection remain strictly confined to the user session.
- **Dedicated Daemon**: A lightweight, standalone service (`RemoteDesk.PrivilegedHelper`) runs as a system service.
- **Audited IPC Protocol**: Communicates over local-only Named Pipes (Windows) or Unix Domain Sockets (Linux/macOS) using length-prefixed framing.
- **Cryptographic Guarantees**:
  - Request HMAC-SHA256 signature using a machine-local root secret (`%ProgramData%\SwiftDesk\reboot.key` or `/etc/swiftdesk/reboot.key`).
  - Replay protection with tracked cryptographic nonces.
  - Strict ±30-second timestamp freshness window.
  - Input sanitization against shell and command injection.
  - Enforced 60-second rate-limiting cooldown to prevent reboot loops.

### Wayland Portal Integration (PipeWire & xdg-desktop-portal)
- **ScreenCast via PipeWire**: Coordinates with `org.freedesktop.portal.ScreenCast` over session D-Bus and probes `libpipewire-0.3.so.0` for Wayland compositors (GNOME Mutter, KDE KWin, Sway).
- **Remote Desktop & Input**: Dispatches input events through `org.freedesktop.portal.RemoteDesktop` and `libei`.
- **Hybrid Probing**: Detects session environment (`$WAYLAND_DISPLAY`, `$XDG_SESSION_TYPE`, `$DISPLAY`) and falls back cleanly to X11 when running under XWayland or traditional X11.

### Clustered Relay Fallback (Redis Pub/Sub Backplane)
- **Zero Sticky Sessions**: When peers in a session connect to different signaling nodes (e.g. Controller on Node 1, Agent on Node 2), traffic is bridged across nodes through `RedisRelayBackplane`.
- **Pub/Sub Channels**: Channels partitioned as `swiftdesk:relay:{sessionId}:{role}`.
- **Local Short-Circuit**: When both peers land on the same instance, messages are pumped in-memory with zero overhead.

### Cross-Platform Packaging & Installers
- **Windows MSI**: WiX manifest ([`SwiftDesk.wxs`](file:///c:/Users/sahil%20yadav/Desktop/SwiftDesk/packaging/windows/SwiftDesk.wxs)) and PowerShell build script installing the application binaries, configuring the `SwiftDeskPrivilegedHelper` Windows Service, and setting up `%ProgramData%\SwiftDesk` with restricted ACLs.
- **Linux Packages**: Debian ([`.deb`](file:///c:/Users/sahil%20yadav/Desktop/SwiftDesk/packaging/linux/debian/control)) control/postinst/prerm, RPM ([`.spec`](file:///c:/Users/sahil%20yadav/Desktop/SwiftDesk/packaging/linux/rpm/swiftdesk.spec)), and hardened [systemd unit](file:///c:/Users/sahil%20yadav/Desktop/SwiftDesk/packaging/linux/systemd/swiftdesk-privileged-reboot.service) (`ProtectSystem=strict`, `NoNewPrivileges=yes`).
- **macOS Installer**: Packaging script ([`build-macos-pkg.sh`](file:///c:/Users/sahil%20yadav/Desktop/SwiftDesk/packaging/macos/build-macos-pkg.sh)), launchd daemon plist ([`com.swiftdesk.privilegedhelper.plist`](file:///c:/Users/sahil%20yadav/Desktop/SwiftDesk/packaging/macos/LaunchDaemons/com.swiftdesk.privilegedhelper.plist)), and postinstall script for `/Library/LaunchDaemons/`.

---

## Production Deployment Checklist

1. **Signaling & Clustered Relays**: Deploy via `docker compose up -d` with PostgreSQL and Redis.
2. **Enterprise IdP**: Configure OIDC issuer (`OidcIssuer`) and audience (`OidcAudience`) with `RequireMfa: true`.
3. **Privileged Helper Daemons**: Deploy and start the signed privileged helper service on managed endpoints.
4. **Desktop Clients**: Distribute the signed MSI, PKG, or DEB installers to operators and end users.
