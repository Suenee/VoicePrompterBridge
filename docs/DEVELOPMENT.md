# Development

## Source layout

```text
src/main.ts                 server entry point
src/server/                 WebSocket transport
src/bridge/                 message and FIFO queue model
src/config/                 configuration loading/validation
src/logging/                diagnostic file logger
src/status/                 tray/server status exchange
src/tools/                  terminal test clients
native/                     Windows tray UI/controller
native/UiIcons.cs           generated tray/application icon
config/                     runtime config template
```

## Development server

```bat
npm install
npm run dev
```

## Build

```bat
npm run build
Build-VPBridge.cmd
```

## Transport regression test

Run VPBridge, then use `npm run test:vp` and `npm run test:bc` in separate terminals. Confirm one `RECEIVED → QUEUED → SENT` sequence per message and verify both directions.

Test disconnect behavior with the default buffer disabled: commands must be `DROPPED`, not replayed after reconnect.

## Versioning

Update the package version, server version, native tray/build metadata and CHANGELOG together. Keep generated files (`dist`, runtime, EXE, logs, node_modules) out of Git.
