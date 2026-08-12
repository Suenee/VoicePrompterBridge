# Development

Source layout: `src/main.ts` entry point; `src/server/` WebSocket transport; `src/bridge/` FIFO model; `src/config/` configuration; `src/logging/` logger; `src/status/` status exchange; `src/tools/` test clients; `native/` tray UI; `assets/` icons.

Development: `npm install`, `npm run dev`.

Build: `npm run build`, then `Build-VPBridge.cmd`.

Regression: run VPBridge plus `npm run test:vp` and `npm run test:bc`; verify one RECEIVED → QUEUED → SENT sequence and both directions. With buffering disabled, disconnected-destination messages must be dropped and never replayed.
