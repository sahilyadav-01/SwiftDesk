# SwiftDesk Privacy Policy

**Effective Date:** January 1, 2026  
**Last Updated:** November 17, 2026  
**Maintained by:** Sahil Yadav & [SK ONE TECH](https://skonetechsupport.com/)  
**Website:** [https://skonetechsupport.com/](https://skonetechsupport.com/)  
**Contact:** [raosahil8290@gmail.com](mailto:raosahil8290@gmail.com)

---

## 1. Overview & Commitment to Privacy

SwiftDesk ("SwiftDesk", "we", "us", or "our"), developed by **Sahil Yadav** and powered by **SK ONE TECH**, provides enterprise-grade, cross-platform remote support and desktop control across Windows, Linux, and macOS.

We believe that remote support software must operate with absolute transparency, strict owner consent, and verifiable zero-trust security. SwiftDesk was architected from the ground up to guarantee that your device, screen, files, and personal data remain strictly under your control at all times.

---

## 2. Core Privacy Principles

1. **Consent-First Attended Access**: An operator can **never** view your screen, control your mouse or keyboard, or transfer files without your explicit, interactive consent on the host computer. Unattended or covert access is strictly rejected at the core protocol layer.
2. **Zero Surveillance / No Covert Operation**: SwiftDesk never operates invisibly in the background. Active remote sessions are prominently displayed with persistent status indicators on both the host and client computers.
3. **No Keystroke Logging or Background Monitoring**: SwiftDesk does not log keystrokes, track user activities, record webcam feeds, or capture clipboard data outside of active, consented sessions.
4. **Direct Peer-to-Peer Encryption**: Wherever network topology permits, audio/video streaming and input control data flow directly between the two endpoints using WebRTC encrypted peer-to-peer (P2P) connections with STUN/NAT traversal (`stun.l.google.com:19302`), never passing through intermediary servers.
5. **Ephemeral Relay Channels**: When direct P2P connections cannot be established due to restrictive firewalls or symmetric NATs, data is forwarded through an ephemeral WebSocket relay channel in real-time. Relay messages are streamed strictly in-memory and are never stored, inspected, or recorded on disk.

---

## 3. Information We Collect & Process

### A. Device Identity & Enrollment Data
When a host machine initializes or enrolls with the signaling service:
- **SwiftDesk Device ID**: A randomly generated 12-digit public identifier (e.g., `686 171 244 053`) used to address connection requests.
- **Device Secret**: A high-entropy cryptographic secret generated locally on the device and stored in hashed form (`SHA-256`) on the signaling server for authentication.
- **Operating Environment Metadata**: Host platform name (`Windows`, `Linux`, `macOS`), computer host name (for user recognition), and agent version.

### B. Session Authorization Data
To coordinate secure connections:
- **Session Tickets**: Short-lived, cryptographically signed session tickets (HMAC-SHA256) with strict 5–10 minute expiration windows.
- **Operator Attribution**: Operator name and authenticated key/OIDC token claims (`sub`, `name`, `email`) for enterprise auditing.
- **Negotiated Permission Flags**: Specific granular capabilities requested and granted (`ViewScreen`, `ControlInput`, `TransferFiles`, `RebootDevice`).

### C. Local Application Data
The desktop client stores local user preferences exclusively on your local hard drive (in `%LocalAppData%\SwiftDesk` on Windows, `~/.local/share/SwiftDesk` on Linux, or `~/Library/Application Support/SwiftDesk` on macOS):
- `device-identity.json`: Local device credentials.
- `favorites.json`: Bookmarked remote device addresses and nicknames.
- `recent-sessions.json`: Local connection history timestamps and device identifiers.

> [!NOTE]
> Local configuration data never leaves your computer and is never synced to external third-party servers.

---

## 4. What We DO NOT Collect

- We **do NOT** record, save, or store screen captures or video recordings.
- We **do NOT** monitor, record, or retain keystrokes or passwords.
- We **do NOT** collect personal files, documents, or browsing history.
- We **do NOT** sell, rent, monetize, or share your telemetry or device information with third-party advertisers or data brokers.

---

## 5. File Transfers & Data Integrity

When you utilize SwiftDesk's file transfer capability:
- File transfers require explicit permission (`TransferFiles`) negotiated during session initiation.
- Files are transferred in 48 KiB chunked streams with strict path-traversal prevention (`..` sanitization).
- Received files are written solely to the user's pre-configured download directory.
- Complete SHA-256 digests are verified before writing completed files to disk to prevent corrupted or tampered payloads.

---

## 6. Remote Reboot & System Security

- The interactive user agent never runs with elevated administrator or root privileges. Screen capture and input injection are strictly confined to the active user desktop session.
- Privileged operations (such as system reboot) are delegated to a separate, dedicated background daemon (`RemoteDesk.PrivilegedHelper`) over local IPC with root key HMAC verification, nonce replay protection, fresh timestamp windows (±30 seconds), and a 60-second rate-limiting cooldown.

---

## 7. Data Retention & Deletion

- **Signaling Server State**: Active session tickets automatically expire within 5–10 minutes. Session relay rooms are destroyed immediately upon disconnection.
- **Local Data Deletion**: You can permanently delete all local identity and history data at any time by deleting the `%LocalAppData%\SwiftDesk\` directory on your computer.
- **Database Records**: Registered device records in PostgreSQL retain device IDs and hashed secrets for persistent reconnection. Server administrators can delete device entries at any time using standard database commands or via administrative APIs.

---

## 8. Enterprise & Managed IT Support

Enterprise deployments, private cloud hosting, SLAs, and dedicated technical support are provided and managed by **[SK ONE TECH](https://skonetechsupport.com/)**. Organizations deploying custom on-premises signaling clusters retain 100% data sovereignty over their private PostgreSQL databases and Redis relay backplanes.

---

## 9. Changes to this Policy

We may update this Privacy Policy from time to time to reflect product enhancements or evolving legal standards. Any revisions will be reflected with an updated "Last Updated" date at the top of this document.

---

## 10. Contact Us

If you have any questions, concerns, or requests regarding this Privacy Policy or your data, please contact:

- **Entity**: SK ONE TECH / Sahil Yadav
- **Official Website**: [https://skonetechsupport.com/](https://skonetechsupport.com/)
- **Email**: [raosahil8290@gmail.com](mailto:raosahil8290@gmail.com)
- **GitHub**: [https://github.com/sahilyadav-01/SwiftDesk](https://github.com/sahilyadav-01/SwiftDesk)
