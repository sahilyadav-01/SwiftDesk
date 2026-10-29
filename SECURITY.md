# Security Policy

## Supported Versions

SwiftDesk is actively maintained and patched for security vulnerabilities. We strongly advise running the latest release version.

| Version | Supported          | Security Patches |
| :--- | :--- | :--- |
| **1.2.x** (Current) | :white_check_mark: | Full active support and security updates |
| **1.1.x** | :white_check_mark: | Critical security patches only |
| **< 1.1.0** | :x: | End of life; upgrade immediately |

---

## Reporting a Vulnerability

The SwiftDesk maintainers and community take security vulnerabilities seriously. We appreciate responsible, coordinated disclosure to help protect our users.

### How to Report

If you discover a security vulnerability in SwiftDesk:

1. **Do NOT open a public GitHub issue.**
2. Send an email to the project maintainer at:
   - **Email**: `raosahil8290@gmail.com`
   - **Subject**: `[SECURITY VULNERABILITY] SwiftDesk - <Brief Description>`
3. Or submit a private advisory through GitHub's [Security Advisories](https://github.com/sahilyadav-01/SwiftDesk/security/advisories/new) feature.

### Information to Include

Please provide as much information as possible to help us reproduce and resolve the issue quickly:
- Type of vulnerability (e.g., authentication bypass, privilege escalation, path traversal, denial of service).
- Affected component (`RemoteDesk.Signaling`, `RemoteDesk.Agent`, `RemoteDesk.Client`, `RemoteDesk.PrivilegedHelper`, or transport).
- Step-by-step reproduction instructions or a minimal proof of concept (PoC).
- Potential impact and your assessment of severity.

---

## Response Timeline & SLA

We follow coordinated vulnerability disclosure:

- **Initial Acknowledgment**: Within **24–48 hours** of receiving your report.
- **Triage & Assessment**: Within **72 hours**, confirming whether the issue is reproducible and determining its severity.
- **Remediation & Patch**: We aim to release a patched version within **14 business days** (or sooner for critical severity issues).
- **Public Disclosure**: Coordinated public release and advisory once the patch is available to users.

---

## Security Architecture & Invariants

For an in-depth breakdown of our trust boundaries, cryptographic models, and enterprise mitigations, please review our comprehensive [Security Architecture Documentation](docs/security.md).

### Non-Negotiable Invariants:
- **Consent-First Attended Access**: An operator can never view a screen, control input, or access files without explicit, interactive owner consent.
- **Zero Stealth Mode**: SwiftDesk never operates invisibly or in the background without clear user awareness.
- **Least-Privilege Isolation**: Interactive agents run as standard desktop users; reboots are gated through an HMAC-authenticated local daemon.
- **No Arbitrary Shell Execution**: Protocol contracts explicitly reject shell execution, arbitrary command injection, or script evaluation.
