# Changelog

## 0.7.1 (devel)
- Added VPP `disconnecting` event for graceful VPBridge shutdown, restart, and exit.
- VPBridge now notifies both connected mailboxes before closing their WebSocket connections.
- Tray Stop sends reason `shutdown`; Restart sends `restart`; application Exit sends `exit`.
- Tray-to-server shutdown now uses redirected stdin and waits briefly for graceful completion before falling back to forced process termination.
- Aligned tray, server, and package version to 0.7.1.

## 0.7.0 (devel)
- Implemented VPP v1 mailbox routing with explicit `from` and `recipient`.
- Added server-local `ping` method (`recipient: server`).
- Ping response reports `vp` / `bc` mailbox connection state.
- Added VPBridge-owned heartbeat interval, default 30000 ms.
- Added Heartbeat setting to the tray Settings window; clients receive the value through ping.
- Invalid routing is rejected instead of being forwarded.
- `devel` updater now follows the `devel` branch.

## 0.6.6
- Finalized GitHub-first source layout.
- `upgrade.cmd` can convert an existing non-Git installation into a clean GitHub working copy.
- Build outputs and runtime data are excluded from Git; the tray icon is generated from source code, so no binary icon asset is required.
- `Build-VPBridge.cmd` is non-interactive and safe to call from `upgrade.cmd`.
- Clean checkout is intended to be fully reproducible from source.

## 0.6.5
- `RECEIVED` payload is logged on the same physical line as its status.
- Corrected typed logger implementation from 0.6.4.

## 0.6.3
- Fixed duplicate lines in the live log viewer with bounded file-tail reads.

## 0.6.x
- Added JSON-only transport validation.
- Continued cleanup of obsolete beta files and upgrade automation.

## 0.5.x
- Added Settings and View log windows.
- Added Local only / All Interfaces modes and API-key management.
- Added live endpoint status diagnostics.
- Added automated `upgrade.cmd` workflow.

## 0.4.x
- Added fully bidirectional VP↔BC transport.
- Added native tray icon/menu and application identity.
- Added safe PID/process-specific management.
