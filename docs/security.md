# SwiftDesk Security Architecture & Model

SwiftDesk is engineered from the ground up on **consent-first, zero-trust, and least-privilege principles**. This document outlines the security architecture, trust boundaries, cryptographic invariants, and threat mitigations implemented across SwiftDesk.

---

## 1. Trust Boundaries & Principles

```
+-----------------------------------------------------------------------------------+
|                                 Signaling Service                                 |
|          - Authenticates operators (OIDC / Operator Key)                          |
|          - Enrolls device IDs via high-entropy bootstrap tokens                  |
|          - Issues cryptographically signed, short-lived session tickets           |
|          - Zero storage of plain text secrets (SHA-256 hashes only)              |
+-----------------------------------------------------------------------------------+
                                         |
                                         | Paired WSS / WebRTC Signaling
                                         v
+-----------------------------------------------------------------------------------+
|                             Host Agent (Target Device)                            |
|  [CRITICAL AUTHORIZATION BOUNDARY: Runs as logged-in user, NEVER root/SYSTEM]     |
|          - Interactive Attended Consent Prompt (Owner must explicitly approve)    |
|          - Grants only requested subset of permissions (View, Control, File, Boot)|
|          - Enforces granular permission checks on every single message            |
+-----------------------------------------------------------------------------------+
       |                                                               |
       | Local Authenticated IPC (Named Pipe / UDS)                    | Restricted User Context
       v                                                               v
+----------------------------------------------+  +---------------------------------+
|        Privileged Helper Daemon              |  |         Native Adapters         |
|        (RemoteDesk.PrivilegedHelper)         |  | - Desktop Surface Capture       |
| - Runs as LocalSystem / systemd root         |  | - Windows SendInput / libei     |
| - HMAC-SHA256 request signatures             |  | - Safe chunked file receiver    |
| - Replay protection nonces                   |  | - Strictly NO arbitrary shell   |
| - Rate limits & 60s reboot cooldown          |  |   execution or command dispatch |
+----------------------------------------------+  +---------------------------------+
```

### Core Invariants:
1. **Consent-First Attended Access**: An operator can never view a screen, control input, or access files without explicit, interactive owner consent on the target machine.
2. **Least Privilege**: The interactive user agent executes strictly within the unprivileged user's desktop session. Privileged actions (such as system reboots) are isolated into a standalone helper daemon.
3. **Defense in Depth**: Even if a malicious or compromised operator claims a permission, the agent rejects unauthorized action envelopes at the local dispatch layer.
4. **No Backdoors or Covert Access**: SwiftDesk explicitly does not support stealth installations, hidden background recording, keystroke logging outside remote sessions, or arbitrary shell execution.

---

## 2. Implemented Security Controls

### A. Authentication & Cryptographic Identity
- **Device Enrollment**: Agents enroll using a cryptographically random, base64-encoded bootstrap token. Devices receive a permanent 12-digit numeric device ID and a 256-bit cryptographically secure device secret (`DeviceSecret`).
- **Secret Hashing**: Device secrets are hashed using SHA-256 before storage; raw secrets are never persisted on the signaling server.
- **Enterprise OpenID Connect (OIDC)**: Supports enterprise identity providers (Google Workspace, Microsoft Entra ID, Okta, Keycloak). Validates asymmetric RSA signatures (`RS256`) or symmetric HMAC (`HS256`), issuer (`iss`), audience (`aud`), and token expiration (`exp`).
- **Mandatory Multi-Factor Authentication (MFA)**: Evaluates `amr` (Authentication Methods References) and `acr` claims in JWTs. Sessions without hardware key / FIDO2 / OTP multi-factor confirmation are rejected with `403 Forbidden` (`mfa_required`).
- **Role-Based Access Control (RBAC)**: Validates authorized operator roles (`SupportOperator`, `Administrator`). Operator identity in audit records is bound to authenticated OIDC claims (`sub`, `name`, `email`), preventing operator spoofing.

### B. Session Ticket Cryptography
- **HMAC-SHA256 Signed Tickets**: Controller and Agent session tickets are minted using an ephemeral or configured 256-bit signing key.
- **Bounded Lifetime**: Tickets expire in **5 to 10 minutes** (`RequestLifetimeMinutes`, `TicketLifetimeMinutes`), preventing stale ticket misuse.
- **Constant-Time Verification**: All secret and signature verifications utilize `CryptographicOperations.FixedTimeEquals` to prevent side-channel timing attacks.
- **Paired Relay Isolation**: Each session relay room accommodates exactly one controller and one agent socket. Unknown sockets or socket collisions are immediately terminated.

### C. Isolated Privileged Remote Reboot Daemon
- **Architecture**: Remote reboots are executed by `RemoteDesk.PrivilegedHelper`, running as a restricted background service (`LocalSystem` on Windows, hardened `systemd` unit on Linux, or `launchd` daemon on macOS).
- **Authenticated IPC**: Communicates strictly over local IPC (`\\.\pipe\RemoteDesk.PrivilegedReboot` or `/run/swiftdesk/reboot.sock`) protected by restricted operating system DACLs.
- **HMAC-SHA256 Signatures**: Requests must be signed with a root-only machine key (`reboot.key`) using HMAC-SHA256.
- **Freshness & Replay Prevention**:
  - Timestamps are strictly verified against a **±30-second window**.
  - Nonces are tracked in an expiring ring buffer to reject replayed requests.
  - Reason strings are sanitized against command injection and shell metacharacters.
  - Mandatory **60-second cooldown** prevents reboot loops.

### D. File Transfer Sandbox & Defense
- **Path Traversal Protection**: File paths are stripped of directory traversal sequences (`../`, `..\`, absolute roots) and constrained to an operator-approved destination directory.
- **Chunk Bounding**: File transfer payloads are capped at **48 KiB** per chunk with strict index validation.
- **SHA-256 Integrity Verification**: Completed files are hashed and verified against the sender's SHA-256 digest before finalizing on disk. Mismatched or truncated files are immediately deleted.

### E. Media Transport & Stream Security
- **WebRTC Encryption**: Direct peer-to-peer data channels negotiate DTLS-SRTP encryption with end-to-end forward secrecy.
- **SCTP Bounding**: Frames are fragmented into bounded **32 KiB** chunks with header validation (`DataChannelMessageFramer`) preventing SCTP buffer poisoning.
- **Non-blocking Frame Conflation**: Prevents UI message queue starvation and denial of service during high-frequency screen updates.

### F. Persistence & Audit Logging
- **PostgreSQL Relational Storage**: Production configurations utilize parameterized queries and connection pooling via `Npgsql`, avoiding SQL injection.
- **Clustered Relay Backplane**: Redis Pub/Sub backplane partitions messages to strictly scoped session channels (`swiftdesk:relay:{sessionId}:{role}`).
- **Immutable Audit Trail**: All device enrollments, operator session requests, approval grants, and disconnects are written to an auditable event store (`/api/audit`).

---

## 3. Threat Model & Mitigation Matrix

| Threat | Impact | Mitigation |
| :--- | :--- | :--- |
| **Man-in-the-Middle (MitM)** | Eavesdropping / session hijacking | TLS 1.3 edge termination, DTLS-SRTP peer media encryption, signed session tickets |
| **Operator Impersonation** | Unauthorized support request | OIDC JWT verification, RS256 public key signatures, mandatory MFA (`amr`/`acr`) |
| **Unattended Device Takeover** | Remote control without consent | Mandatory interactive attended consent prompt; agent rejects unapproved requests |
| **Timing Attacks** | Secret / ticket extraction | `CryptographicOperations.FixedTimeEquals` on all secret and signature comparisons |
| **Privilege Escalation** | Root / SYSTEM takeover | Agent runs as unprivileged user; helper daemon uses HMAC signatures, nonces, and input sanitization |
| **Path Traversal via File Transfer** | Arbitrary file overwrite | Sanitization of paths, sandbox directory enforcement, SHA-256 integrity validation |
| **Replay Attacks** | Re-executing prior commands | Cryptographic nonces, short ticket expiry (5 min), ±30-second timestamp freshness windows |
| **Denial of Service (DoS)** | Exhaustion of server/client memory | Bounded payload sizes (32 KiB SCTP, 48 KiB file chunks), non-blocking frame conflation |

---

## 4. Explicit Non-Goals

SwiftDesk is intentionally designed to **prevent abusive or surveillance usage**:
- ❌ **No Stealth / Hidden Mode**: SwiftDesk will never provide a stealth mode. Connection indicators are always visible.
- ❌ **No Consent Bypass**: Bypassing owner consent is strictly treated as an exploit and violation of project policy.
- ❌ **No Arbitrary Shell Execution**: The platform adapters do not accept shell commands, command-line arguments, or arbitrary script execution.
- ❌ **No Credential Harvesting**: SwiftDesk never logs or captures Windows credentials, system passwords, or master keys.
