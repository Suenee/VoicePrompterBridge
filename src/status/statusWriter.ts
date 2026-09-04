import fs from 'node:fs';
import path from 'node:path';

export interface BridgeRuntimeStatus {
  serverRunning: boolean;
  vpConnected: boolean;
  bcConnected: boolean;
  mailboxes: string[];
  connections: Record<string, boolean>;
  host: string;
  port: number;
  updatedAt: string;
}

export class StatusWriter {
  private readonly filePath = path.resolve(process.cwd(), 'runtime', 'status.json');
  private readonly tempPath = `${this.filePath}.tmp`;

  constructor(private status: Omit<BridgeRuntimeStatus, 'updatedAt'>) {
    fs.mkdirSync(path.dirname(this.filePath), { recursive: true });
    this.write();
  }

  update(patch: Partial<Omit<BridgeRuntimeStatus, 'updatedAt'>>): void {
    this.status = { ...this.status, ...patch };
    this.write();
  }

  private write(): void {
    const value: BridgeRuntimeStatus = { ...this.status, updatedAt: new Date().toISOString() };
    try {
      fs.writeFileSync(this.tempPath, JSON.stringify(value, null, 2), 'utf8');
      fs.renameSync(this.tempPath, this.filePath);
    } catch {
      try { fs.writeFileSync(this.filePath, JSON.stringify(value, null, 2), 'utf8'); } catch { }
    }
  }
}
