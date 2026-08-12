import { loadConfig } from './config/config';
import { Logger } from './logging/logger';
import { VPBridgeServer } from './server/websocketServer';

async function main(): Promise<void> {
  const config = loadConfig();
  const logger = new Logger(config.logging);
  const server = new VPBridgeServer(config, logger);
  const shutdown = async (signal: string) => { logger.system(`${signal} received`); await server.stop(); process.exit(0); };
  process.on('SIGINT', () => void shutdown('SIGINT'));
  process.on('SIGTERM', () => void shutdown('SIGTERM'));
  await server.start();
}
main().catch((err) => { console.error(err); process.exit(1); });
