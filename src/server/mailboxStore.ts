import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import { DatabaseSync } from 'node:sqlite';
import type { VPBridgeConfig } from '../config/config';
import type { Logger } from '../logging/logger';

export type QueueMode = 'OFF' | 'MEMORY' | 'PERSISTENT';

export interface MailboxRecord {
  id: string;
  friendlyName: string;
  note: string;
  apiKey: string;
  queueMode: QueueMode;
  ttlSeconds: number;
  heartbeatSeconds: number;
  maxConnections: number;
  allowedRecipients: string[];
}

export interface StoredMessage {
  rowId: number;
  messageId: string;
  source: string;
  target: string;
  payload: string;
  receivedAt: number;
  expiresAt: number | null;
  queuePolicy: 'fifo' | 'replace';
  queueKey: string | null;
  expectsResponse: boolean;
  originConnectionId: string | null;
  targetConnectionId: string | null;
}

export class MailboxStore {
  private readonly db: DatabaseSync;

  constructor(private readonly config: VPBridgeConfig, private readonly logger: Logger) {
    const d = path.resolve(process.cwd(), 'data');
    fs.mkdirSync(d, { recursive: true });
    const dbPath = path.join(d, 'sub.db');
    this.logger.debug(`Opening mailbox database ${dbPath}`);
    try {
      this.db = new DatabaseSync(dbPath);
      this.db.exec(`
        PRAGMA journal_mode=WAL;
        PRAGMA foreign_keys=ON;
        CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY,value TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS mailboxes(
          id TEXT PRIMARY KEY COLLATE NOCASE,
          friendly_name TEXT NOT NULL,
          note TEXT NOT NULL DEFAULT '',
          api_key TEXT NOT NULL DEFAULT '',
          queue_mode TEXT NOT NULL DEFAULT 'OFF',
          ttl_seconds INTEGER NOT NULL DEFAULT 0,
          heartbeat_seconds INTEGER NOT NULL DEFAULT 30,
          max_connections INTEGER NOT NULL DEFAULT 1
        );
        CREATE UNIQUE INDEX IF NOT EXISTS ux_mailbox_api_key ON mailboxes(api_key) WHERE api_key<>'';
        CREATE TABLE IF NOT EXISTS allowed_recipients(
          source_id TEXT NOT NULL COLLATE NOCASE,
          target_id TEXT NOT NULL COLLATE NOCASE,
          PRIMARY KEY(source_id,target_id),
          FOREIGN KEY(source_id) REFERENCES mailboxes(id) ON DELETE CASCADE ON UPDATE CASCADE,
          FOREIGN KEY(target_id) REFERENCES mailboxes(id) ON DELETE CASCADE ON UPDATE CASCADE,
          CHECK(lower(source_id)<>lower(target_id))
        );
        CREATE TABLE IF NOT EXISTS persistent_queue(
          row_id INTEGER PRIMARY KEY AUTOINCREMENT,
          message_id TEXT NOT NULL,
          source_id TEXT NOT NULL,
          target_id TEXT NOT NULL,
          payload TEXT NOT NULL,
          received_at INTEGER NOT NULL,
          expires_at INTEGER,
          queue_policy TEXT NOT NULL DEFAULT 'fifo',
          queue_key TEXT,
          expects_response INTEGER NOT NULL DEFAULT 0,
          origin_connection_id TEXT,
          target_connection_id TEXT
        );
        CREATE INDEX IF NOT EXISTS ix_queue_target ON persistent_queue(target_id,row_id);
        CREATE INDEX IF NOT EXISTS ix_queue_replace ON persistent_queue(source_id,target_id,queue_key) WHERE queue_policy='replace';
      `);
      this.ensureColumn('mailboxes', 'max_connections', 'ALTER TABLE mailboxes ADD COLUMN max_connections INTEGER NOT NULL DEFAULT 1');
      this.ensureColumn('persistent_queue', 'origin_connection_id', 'ALTER TABLE persistent_queue ADD COLUMN origin_connection_id TEXT');
      this.ensureColumn('persistent_queue', 'target_connection_id', 'ALTER TABLE persistent_queue ADD COLUMN target_connection_id TEXT');
      this.db.exec('UPDATE mailboxes SET max_connections=1 WHERE max_connections IS NULL OR max_connections<1');
      this.migrateLegacy();
      this.purgeExpired();
      this.logger.debug('Mailbox database ready');
    } catch (e) {
      this.logger.debug('Mailbox database initialization failed', e);
      throw e;
    }
  }

  private ensureColumn(table: string, column: string, ddl: string) {
    const rows = this.db.prepare(`PRAGMA table_info(${table})`).all() as any[];
    if (!rows.some(r => String(r.name).toLowerCase() === column.toLowerCase())) this.db.exec(ddl);
  }

  close() {
    try {
      this.db.close();
      this.logger.debug('Mailbox database closed');
    } catch (e) {
      this.logger.debug('Mailbox database close failed', e);
    }
  }

  private migrateLegacy() {
    if (this.db.prepare(`SELECT value FROM meta WHERE key='legacy_migrated'`).get()) return;
    this.logger.debug('Migrating legacy VP/BC mailboxes into SQLite');
    const ttl = Math.max(0, Math.floor(this.config.queue.offlineBufferMaxAgeMs / 1000));
    const mode: QueueMode = this.config.queue.offlineBufferSize > 0 ? 'MEMORY' : 'OFF';
    const hb = Math.max(0, Math.floor(this.config.heartbeat.intervalMs / 1000));
    const ins = this.db.prepare('INSERT OR IGNORE INTO mailboxes(id,friendly_name,note,api_key,queue_mode,ttl_seconds,heartbeat_seconds,max_connections) VALUES(?,?,?,?,?,?,?,1)');
    ins.run('vp', 'VoicePrompter', 'Migrated legacy mailbox', '', mode, ttl, hb);
    ins.run('bc', 'Bitfocus Companion', 'Migrated legacy mailbox', '', mode, ttl, hb);
    this.db.prepare('INSERT OR IGNORE INTO allowed_recipients(source_id,target_id) VALUES(?,?)').run('vp', 'bc');
    this.db.prepare('INSERT OR IGNORE INTO allowed_recipients(source_id,target_id) VALUES(?,?)').run('bc', 'vp');
    this.db.prepare(`INSERT INTO meta(key,value) VALUES('legacy_migrated',?)`).run(new Date().toISOString());
    this.backupLegacyConfig();
    this.logger.debug('Legacy mailbox migration completed');
  }

  private backupLegacyConfig() {
    const source = path.resolve(process.cwd(), 'config', 'vpbridge.json');
    if (!fs.existsSync(source)) return;
    const dir = path.resolve(process.cwd(), 'config', 'migration-backup');
    fs.mkdirSync(dir, { recursive: true });
    const stamp = new Date().toISOString().replace(/[:.]/g, '-');
    const dest = path.join(dir, `vpbridge-${stamp}.json`);
    if (!fs.existsSync(dest)) fs.copyFileSync(source, dest);
    const cutoff = Date.now() - 7 * 86400000;
    for (const n of fs.readdirSync(dir)) {
      const p = path.join(dir, n);
      try { if (fs.statSync(p).mtimeMs < cutoff) fs.unlinkSync(p); } catch { }
    }
  }

  list(): MailboxRecord[] {
    try {
      const rows = this.db.prepare('SELECT * FROM mailboxes ORDER BY id COLLATE NOCASE').all() as any[];
      const a = this.db.prepare('SELECT target_id FROM allowed_recipients WHERE source_id=? ORDER BY target_id COLLATE NOCASE');
      return rows.map(r => ({
        id: r.id,
        friendlyName: r.friendly_name,
        note: r.note,
        apiKey: r.api_key,
        queueMode: r.queue_mode,
        ttlSeconds: r.ttl_seconds,
        heartbeatSeconds: r.heartbeat_seconds,
        maxConnections: Math.max(1, Number(r.max_connections) || 1),
        allowedRecipients: (a.all(r.id) as any[]).map(x => x.target_id),
      }));
    } catch (e) {
      this.logger.debug('Mailbox list query failed', e);
      throw e;
    }
  }

  get(id: string) { return this.list().find(m => m.id.toLowerCase() === id.toLowerCase()); }

  isAllowed(s: string, t: string) {
    try { return !!this.db.prepare('SELECT 1 FROM allowed_recipients WHERE source_id=? AND target_id=?').get(s, t); }
    catch (e) { this.logger.debug(`Mailbox ACL query failed ${s}->${t}`, e); throw e; }
  }

  validateApiKey(mailbox: string, key: string) {
    const m = this.get(mailbox);
    if (!m || !m.apiKey) return false;
    const a = Buffer.from(m.apiKey), b = Buffer.from(key);
    return a.length === b.length && crypto.timingSafeEqual(a, b);
  }

  purgeExpired() {
    try { this.db.prepare('DELETE FROM persistent_queue WHERE expires_at IS NOT NULL AND expires_at<=?').run(Date.now()); }
    catch (e) { this.logger.debug('Persistent queue expiry cleanup failed', e); throw e; }
  }

  persistentFor(t: string): StoredMessage[] {
    this.purgeExpired();
    try {
      return (this.db.prepare('SELECT * FROM persistent_queue WHERE target_id=? ORDER BY row_id').all(t) as any[]).map(r => this.rowToStored(r));
    } catch (e) {
      this.logger.debug(`Persistent queue read failed for ${t}`, e);
      throw e;
    }
  }

  storePersistent(m: Omit<StoredMessage, 'rowId'>): StoredMessage[] {
    try {
      let old: StoredMessage[] = [];
      if (m.queuePolicy === 'replace' && m.queueKey) {
        const rows = this.db.prepare(`
          SELECT * FROM persistent_queue
          WHERE source_id=? AND target_id=? AND queue_policy='replace' AND queue_key=?
            AND COALESCE(origin_connection_id,'')=COALESCE(?, '')
            AND COALESCE(target_connection_id,'')=COALESCE(?, '')
        `).all(m.source, m.target, m.queueKey, m.originConnectionId, m.targetConnectionId) as any[];
        old = rows.map(r => this.rowToStored(r));
        this.db.prepare(`
          DELETE FROM persistent_queue
          WHERE source_id=? AND target_id=? AND queue_policy='replace' AND queue_key=?
            AND COALESCE(origin_connection_id,'')=COALESCE(?, '')
            AND COALESCE(target_connection_id,'')=COALESCE(?, '')
        `).run(m.source, m.target, m.queueKey, m.originConnectionId, m.targetConnectionId);
      }
      this.db.prepare(`
        INSERT INTO persistent_queue(
          message_id,source_id,target_id,payload,received_at,expires_at,queue_policy,queue_key,expects_response,origin_connection_id,target_connection_id
        ) VALUES(?,?,?,?,?,?,?,?,?,?,?)
      `).run(m.messageId, m.source, m.target, m.payload, m.receivedAt, m.expiresAt, m.queuePolicy, m.queueKey, m.expectsResponse ? 1 : 0, m.originConnectionId, m.targetConnectionId);
      return old;
    } catch (e) {
      this.logger.debug(`Persistent queue write failed ${m.source}->${m.target}`, e);
      throw e;
    }
  }

  deletePersistent(id: number) {
    try { this.db.prepare('DELETE FROM persistent_queue WHERE row_id=?').run(id); }
    catch (e) { this.logger.debug(`Persistent queue delete failed row ${id}`, e); throw e; }
  }

  private rowToStored(r: any): StoredMessage {
    return {
      rowId: r.row_id,
      messageId: r.message_id,
      source: r.source_id,
      target: r.target_id,
      payload: r.payload,
      receivedAt: r.received_at,
      expiresAt: r.expires_at,
      queuePolicy: r.queue_policy,
      queueKey: r.queue_key,
      expectsResponse: !!r.expects_response,
      originConnectionId: r.origin_connection_id ?? null,
      targetConnectionId: r.target_connection_id ?? null,
    };
  }
}
