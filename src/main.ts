import { loadConfig } from './config/config';
import { Logger } from './logging/logger';
import { VPBridgeServer, type GracefulDisconnectReason } from './server/websocketServer';

async function main(): Promise<void> {
  const config = loadConfig();
  const logger = new Logger(config.logging);
  const server = new VPBridgeServer(config, logger);
  let shuttingDown = false;

  const shutdown = async (source: string, reason: GracefulDisconnectReason) => {
    if (shuttingDown) return;
    shuttingDown = true;
    logger.system(`${source} received; graceful ${reason}`);
    try { await server.stop(reason); }
    finally { process.exit(0); }
  };

  process.on('SIGINT', () => void shutdown('SIGINT', 'shutdown'));
  process.on('SIGTERM', () => void shutdown('SIGTERM', 'shutdown'));

  process.stdin.setEncoding('utf8');
  process.stdin.on('data', chunk => {
    for (const rawLine of String(chunk).split(/\r?\n/)) {
      const line = rawLine.trim();
      if (!line) continue;
      const command = line.toLowerCase();
      if (command === 'shutdown' || command === 'restart' || command === 'exit') {
        void shutdown('TRAY', command);
        continue;
      }
      if (command.startsWith('disconnect ')) {
        const connectionId = line.slice('disconnect '.length).trim();
        if (connectionId && !shuttingDown) void server.disconnectConnection(connectionId);
      }
    }
  });
  process.stdin.resume();

  await server.start();
}
main().catch((err) => { console.error(err); process.exit(1); });
