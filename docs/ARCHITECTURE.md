# Architecture

VPBridge is the transport boundary between VoicePrompter and Companion. Two logical endpoints/mailboxes exist: `vp` and `bc`. Each direction has an independent FIFO queue. Queues exist only in RAM and are never persisted; Restart/Stop/Exit discards queued commands. Invalid JSON is rejected and payload transport is logged. Application-level VPP semantics remain outside VPBridge unless a protocol method explicitly targets the server.

`VPBridge.exe` is the native Windows tray controller and owns the server runtime. Process management is specific; generic Node.js processes are never killed.

Local-only mode needs no authentication. All Interfaces requires an API key. WSS is outside the current trusted-LAN beta scope.
