# Security model

## Trust boundaries

1. The signaling service is trusted to authenticate devices/operators and authorize session pairing.
2. The relay is trusted for availability and routing. Production media/data should still use WebRTC authenticated encryption end to end.
3. The agent is the final authorization boundary. It checks the granted permission for every input, file, and reboot message.
4. Native adapters are privileged boundaries and must not accept arbitrary command lines, executable paths, or shell text.

## Implemented safeguards

- 256-bit randomly generated device secrets, stored server-side as SHA-256 hashes.
- Constant-time secret and ticket signature checks.
- Short-lived, signed, role-specific session tickets.
- Owner approval can grant only a non-empty subset of the requested permissions.
- One controller and one agent socket per session.
- Bounded HTTP/control fields, relay messages, file sizes, and file chunks.
- File traversal, overwrite, offset, length, and digest defenses.
- No native capture/input/reboot capability is advertised until implemented.
- Non-development startup rejects the checked-in local credentials.

## Required before production

- TLS/HSTS at the edge; trusted proxy configuration; secure WebSocket origin policy.
- OIDC/MFA, named operators, device ownership, key rotation, revocation, and lockout/rate limiting.
- PostgreSQL persistence, encryption at rest, migrations, backups, and immutable audit events.
- CSRF protection if cookie authentication is introduced.
- Ticket replay tracking or proof-of-possession for higher-risk deployments.
- Signed/notarized installers and updates, rollback, SBOM, dependency scanning, and incident response.
- Native permission UX, permanent connection indicator, local disconnect control, and session history.
- Fuzzing of protocol parsing and soak tests for relay backpressure/reconnect behavior.
- Independent security review before exposing the service to the public internet.

## Explicit non-goals

- Hidden installation or operation
- Permission-prompt bypasses
- Arbitrary shell execution
- Credential collection
- Security-control evasion

