# Protocol notes

## HTTP authentication

- Enrollment uses `X-Bootstrap-Token` only for the bootstrap operation.
- Agent operations use the high-entropy `X-Device-Secret` and device ID.
- Controller operations use `X-Operator-Key` in the development server.
- Session WebSockets use `Authorization: Bearer <short-lived-ticket>`.

Deploy only behind TLS. Header names describe the development protocol; production operator authentication should use standard OIDC access tokens.

## Control messages

`ControlEnvelope` includes a message kind, session ID, monotonically increasing sequence number, and JSON payload. The agent rejects messages for a different session and replayed/out-of-order sequence numbers.

Current control-message limit: 64 KiB. File bytes are base64 encoded in chunks no larger than 48 KiB; the client currently sends 32 KiB chunks. Video should not use this JSON control path.

## File transfer invariants

- The owner selects an approved destination directory when starting the agent.
- Offers contain a plain file name, declared byte length, and SHA-256 digest.
- Rooted paths, traversal, subdirectories, invalid names, oversized files, duplicate transfer IDs, empty chunks, gaps, overlaps, and excess bytes are rejected.
- Data is written to a random `.part` file.
- The final file is moved into place only after length and digest verification.
- Existing destination files are not overwritten.
- Cancellation or disconnect removes the partial file.

## Adding media

Define a binary media header containing codec, frame sequence, timestamps, dimensions, key-frame flag, and payload length. Enforce strict maximum dimensions/bitrate. Prefer an established WebRTC implementation rather than inventing congestion control, encryption, NAT traversal, or retransmission.

