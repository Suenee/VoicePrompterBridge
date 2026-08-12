# VoicePrompter Bridge (VPBridge)

VoicePrompter Bridge is a small Windows tray application that transports JSON messages between **VoicePrompter (VP)** and **Bitfocus Companion (BC)**.

```text
VoicePrompter  ⇄  /vp  ⇄  VPBridge  ⇄  /bc  ⇄  VoicePrompterModule / Companion
```

The project deliberately keeps the bridge small. It owns WebSocket transport, endpoint connection state, RAM-only FIFO buffering, authentication for LAN access, and diagnostics. Application commands belong to VoicePrompter Protocol (VPP), not to the transport layer, except for explicitly defined server/system methods when implemented.

Current application version: **0.6.6**.

## Requirements

- Windows 10 or Windows 11
- Node.js available in `PATH`
- Git for Windows for `upgrade.cmd`
- .NET Framework 4.x C# compiler for the native tray executable (normally available on supported Windows systems)

## Install from GitHub

The GitHub repository is `VoicePrompterBridge`. The application is named **VoicePrompter Bridge**.

```bat
git clone https://github.com/Suenee/VoicePrompterBridge.git VoicePrompterBridge
cd VoicePrompterBridge
npm install
npm run build
Build-VPBridge.cmd
```

Start `VPBridge.exe`.

On first run, if `config\vpbridge.json` does not exist, copy `config\vpbridge.example.json` to `config\vpbridge.json`. The GitHub-aware `upgrade.cmd` does this automatically.

## Tray application

The tray application provides:

- Start
- Stop
- Restart — reloads `config\vpbridge.json` and clears both RAM queues
- Settings
- View log
- Exit

The log window is modeless, resizable, live-updating, and displays transport/endpoint state.

## WebSocket endpoints

Default local endpoints:

```text
ws://127.0.0.1:8170/vp
ws://127.0.0.1:8170/bc
```

Only one active owner is expected for each endpoint. A newly connected client replaces the previous connection on that mailbox/endpoint.

## Network and authentication

Settings support two listen modes:

- **Local only** — binds to `127.0.0.1`; API key is not required.
- **All Interfaces** — binds to `0.0.0.0`; a 256-bit API key represented by 64 hexadecimal characters is required.

The current beta intentionally uses `WS://`, not `WSS://`. All Interfaces is intended only for a trusted local network.

## Configuration

Runtime configuration is deliberately not tracked by Git. The tracked template is:

```text
config\vpbridge.example.json
```

The live file is:

```text
config\vpbridge.json
```

Important defaults:

```json
{
  "server": {
    "mode": "local",
    "host": "127.0.0.1",
    "port": 8170,
    "vpPath": "/vp",
    "bcPath": "/bc"
  },
  "security": {
    "apiKey": ""
  },
  "queue": {
    "maxMessages": 1000,
    "offlineBufferSize": 0,
    "offlineBufferMaxAgeMs": 1000
  },
  "logging": {
    "enabled": true,
    "directory": "./logs",
    "retentionMinutes": 60
  }
}
```

`offlineBufferSize: 0` is the safe default: messages are dropped when the destination is unavailable. Any enabled buffering remains in RAM only and is always discarded on Stop/Restart/Exit.

## Logging

`logs\vpbridge.log` contains transport diagnostics. A typical record is:

```text
12.08.2026 01:11:38.429  #1  VP→BC  RECEIVED  {"protocolVersion":1,...}
12.08.2026 01:11:38.430  #1  VP→BC  QUEUED
12.08.2026 01:11:38.432  #1  VP→BC  SENT
```

Log retention defaults to one hour. Transport queues are never persisted.

## Update

Run:

```bat
upgrade.cmd
```

The updater uses GitHub as the source of truth. It downloads `main`, removes obsolete untracked source files, installs dependencies, rebuilds TypeScript and `VPBridge.exe`, preserves ignored runtime configuration/logs, and restores the previous running state after a successful upgrade.

If tracked source files were modified locally, the updater stops rather than silently overwrite them.

## Development and testing

Visible debug server:

```bat
VPBridge-Debug.cmd
```

Terminal clients:

```bat
npm run test:vp
npm run test:bc
```

Emergency termination:

```bat
Kill-VPBridge.cmd
```

The kill script targets only VPBridge-owned processes and must never terminate generic `node.exe`, because Bitfocus Companion also uses Node.js.

See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) and [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md).

## Related repositories

- VoicePrompter: `Suenee/VoicePrompter`
- Companion module: `Suenee/companion-module-voiceprompter`

The canonical VPP specification is maintained in `PROTOCOL.md` in the `companion-module-voiceprompter` repository.

## Generated artifacts

The tray application icon is generated from C# drawing code at runtime. No binary icon asset is required in the repository. Generated executables, `dist/`, `runtime/`, `node_modules/`, logs and live configuration are deliberately not stored in Git.

## License

A repository license has not been selected here yet. Do not assume permission beyond the rights granted by the repository owner.
