import http from 'node:http';
import crypto from 'node:crypto';
import { WebSocketServer, WebSocket } from 'ws';
import { StatusWriter } from '../status/statusWriter';
import type { VPBridgeConfig } from '../config/config';
import { Logger } from '../logging/logger';
import { MailboxStore, type StoredMessage } from './mailboxStore';

const VERSION = '0.8.0';
const LEGACY_ADMISSION_DELAY_MS = 500;
const NEGOTIATION_TIMEOUT_MS = 30000;

export type GracefulDisconnectReason = 'shutdown' | 'restart' | 'exit';

type VppEnvelope = {
  protocolVersion?: number;
  id?: string;
  correlationId?: string;
  type?: string;
  from?: string;
  recipient?: string;
  to?: string | string[];
  method?: string;
  event?: string;
  args?: unknown;
  result?: unknown;
  error?: unknown;
  expectsResponse?: boolean;
  targetConnectionId?: string;
  source?: { app?: string; version?: string; [key: string]: unknown };
  queue?: { policy?: 'fifo' | 'replace'; key?: string };
};

type MemoryMessage = Omit<StoredMessage, 'rowId'>;

type ConnectionRecord = {
  connectionId: string;
  socketBox: string;
  ws: WebSocket;
  ip: string;
  hostName?: string;
  service?: string;
  connectedAt: string;
  admitted: boolean;
  negotiating: boolean;
  legacy: boolean;
  registrationTimer?: NodeJS.Timeout;
};

type Negotiation = {
  connection: ConnectionRecord;
  expiresAt: string;
  timer: NodeJS.Timeout;
};

type PendingRequest = {
  originConnectionId: string;
  originMailbox: string;
  targetConnectionId: string;
  targetMailbox: string;
};

export class VPBridgeServer {
  private readonly httpServer = http.createServer();
  private readonly wsServer = new WebSocketServer({ noServer: true });
  private readonly store: MailboxStore;
  private readonly activeConnections = new Map<string, Map<string, ConnectionRecord>>();
  private readonly socketConnections = new Map<WebSocket, ConnectionRecord>();
  private readonly negotiations = new Map<string, Negotiation>();
  private readonly pendingRequests = new Map<string, PendingRequest>();
  private readonly memoryQueues = new Map<string, MemoryMessage[]>();
  private readonly statusWriter: StatusWriter;
  private nextMessageId = 1;
  private stopping = false;

  constructor(private readonly config: VPBridgeConfig, private readonly logger: Logger) {
    this.logger.debug('Initializing Socket Universe Bridge server');
    this.store = new MailboxStore(config, logger);
    this.statusWriter = new StatusWriter({
      serverRunning: false,
      vpConnected: false,
      bcConnected: false,
      mailboxes: this.store.list().map(b => b.id),
      connections: this.connectionStates(),
      host: config.server.host,
      port: config.server.port,
    });

    this.httpServer.on('upgrade', (request, socket, head) => {
      try {
        const url = new URL(request.url ?? '/', `http://${request.headers.host ?? 'localhost'}`);
        const ip = this.normalizeIp(request.socket.remoteAddress ?? 'unknown');
        const requestedBox = this.connectionAttemptLabel(url.pathname);
        const attemptId = this.nextMessageId++;
        this.logger.message(attemptId, requestedBox, 'server', 'RECEIVED', `CONNECT ${ip} ${url.pathname}`);
        const mailbox = this.resolveMailbox(url.pathname);
        if (!mailbox) {
          this.logger.message(attemptId, requestedBox, 'server', 'ERROR', `CONNECT REJECTED: Unknown Socket Box path ${url.pathname}; IP ${ip}`);
          this.logger.debug(`Rejected unknown mailbox path ${url.pathname} from ${ip}`);
          socket.write('HTTP/1.1 404 Not Found\r\n\r\n');
          socket.destroy();
          return;
        }
        if (config.server.mode === 'all') {
          const supplied = url.searchParams.get('apiKey') ?? '';
          if (!this.store.validateApiKey(mailbox, supplied)) {
            this.logger.message(attemptId, mailbox, 'server', 'ERROR', `CONNECT REJECTED: Invalid API key; IP ${ip}`);
            this.logger.system(`AUTH REJECTED for ${mailbox} from ${ip}`);
            this.logger.debug(`Authentication rejected for mailbox ${mailbox} from ${ip}`);
            socket.write('HTTP/1.1 401 Unauthorized\r\nConnection: close\r\n\r\n');
            socket.destroy();
            return;
          }
        }
        this.wsServer.handleUpgrade(request, socket, head, ws => this.wsServer.emit('connection', ws, request, mailbox));
      } catch (e) {
        this.logger.debug('WebSocket upgrade failed', e);
        socket.destroy();
      }
    });

    this.wsServer.on('connection', (ws: WebSocket, request: http.IncomingMessage, mailbox: string) => this.attach(mailbox, ws, request));
  }

  start(): Promise<void> {
    return new Promise((resolve, reject) => {
      this.httpServer.once('error', e => { this.logger.debug('HTTP server start failed', e); reject(e); });
      this.httpServer.listen(this.config.server.port, this.config.server.host, () => {
        this.httpServer.removeAllListeners('error');
        this.logger.system(`Socket Universe Bridge v${VERSION} listening on ws://${this.config.server.host}:${this.config.server.port}`);
        this.logger.debug(`Server listening on ${this.config.server.host}:${this.config.server.port}`);
        this.updateStatus(true);
        resolve();
      });
    });
  }

  async stop(reason: GracefulDisconnectReason = 'shutdown') {
    if (this.stopping) return;
    this.stopping = true;
    this.logger.system(`Stopping SUB (${reason}); announcing graceful disconnect`);
    this.logger.debug(`Stop requested: ${reason}`);
    const all = [...this.socketConnections.values()].filter(c => c.ws.readyState === WebSocket.OPEN);
    await Promise.allSettled(all.map(c => this.sendServerEventConnection(c, 'disconnecting', { reason })));
    for (const c of all) c.ws.close(1001, `SUB ${reason}`);
    for (const n of this.negotiations.values()) clearTimeout(n.timer);
    this.negotiations.clear();
    this.activeConnections.clear();
    this.socketConnections.clear();
    this.pendingRequests.clear();
    this.memoryQueues.clear();
    this.wsServer.close();
    this.updateStatus(false);
    this.store.close();
    await new Promise<void>(r => this.httpServer.close(() => r()));
  }

  private resolveMailbox(pathname: string) {
    if (pathname === this.config.server.vpPath) return 'vp';
    if (pathname === this.config.server.bcPath) return 'bc';
    const m = /^\/mailbox\/([a-z0-9_-]+)$/i.exec(pathname);
    if (!m) return undefined;
    return this.store.get(m[1])?.id;
  }

  private connectionAttemptLabel(pathname: string) {
    const dynamic = /^\/mailbox\/([a-z0-9_-]+)$/i.exec(pathname);
    if (dynamic) return dynamic[1];
    const plain = pathname.replace(/^\/+|\/+$/g, '');
    return /^[a-z0-9_-]+$/i.test(plain) ? plain : 'unknown';
  }

  private attach(mailbox: string, ws: WebSocket, request: http.IncomingMessage) {
    const connection: ConnectionRecord = {
      connectionId: crypto.randomUUID(),
      socketBox: mailbox,
      ws,
      ip: this.normalizeIp(request.socket.remoteAddress ?? 'unknown'),
      connectedAt: new Date().toISOString(),
      admitted: false,
      negotiating: false,
      legacy: false,
    };
    this.socketConnections.set(ws, connection);
    this.logger.debug(`Socket opened for ${mailbox} from ${connection.ip}; awaiting registration or legacy admission`);

    connection.registrationTimer = setTimeout(() => {
      if (ws.readyState === WebSocket.OPEN && !connection.admitted && !connection.negotiating) this.admitLegacy(connection);
    }, LEGACY_ADMISSION_DELAY_MS);

    ws.on('message', (d, b) => { if (!b) this.receive(connection, d.toString()); });
    ws.on('close', () => this.detach(connection));
    ws.on('error', e => {
      this.logger.system(`${mailbox.toUpperCase()} ERROR: ${e.message}`);
      this.logger.debug(`Mailbox socket error: ${mailbox} ${connection.connectionId}`, e);
    });
  }

  private normalizeIp(value: string) { return value.startsWith('::ffff:') ? value.slice(7) : value; }

  private detach(connection: ConnectionRecord) {
    if (connection.registrationTimer) clearTimeout(connection.registrationTimer);
    const negotiation = this.negotiations.get(connection.socketBox.toLowerCase());
    if (negotiation?.connection === connection) {
      clearTimeout(negotiation.timer);
      this.negotiations.delete(connection.socketBox.toLowerCase());
    }
    this.removeActive(connection);
    this.socketConnections.delete(connection.ws);
    for (const [key, p] of this.pendingRequests) {
      if (p.originConnectionId === connection.connectionId || p.targetConnectionId === connection.connectionId) this.pendingRequests.delete(key);
    }
    this.logger.debug(`Socket closed: ${connection.socketBox} ${connection.connectionId}`);
    this.updateStatus(true);
  }

  private admitLegacy(connection: ConnectionRecord): boolean {
    if (connection.admitted) return true;
    const box = this.store.get(connection.socketBox);
    if (!box) return false;
    if (this.activeFor(connection.socketBox).length >= box.maxConnections) {
      this.logger.message(this.nextMessageId++, connection.socketBox, 'server', 'ERROR', `CONNECT REJECTED: Connection limit reached (${box.maxConnections}); IP ${connection.ip}`);
      this.logger.system(`${connection.socketBox.toUpperCase()} CONNECTION REJECTED: limit ${box.maxConnections}`);
      this.logger.debug(`Legacy admission rejected for ${connection.socketBox}; maxConnections reached`);
      connection.ws.close(4003, 'Socket Box connection limit reached');
      return false;
    }
    connection.legacy = true;
    this.admit(connection);
    return true;
  }

  private admit(connection: ConnectionRecord, flush = true) {
    if (connection.registrationTimer) { clearTimeout(connection.registrationTimer); connection.registrationTimer = undefined; }
    const key = connection.socketBox.toLowerCase();
    let bucket = this.activeConnections.get(key);
    if (!bucket) { bucket = new Map(); this.activeConnections.set(key, bucket); }
    connection.admitted = true;
    connection.negotiating = false;
    connection.connectedAt = new Date().toISOString();
    bucket.set(connection.connectionId, connection);
    this.logger.system(`${connection.socketBox.toUpperCase()} CONNECTED (${bucket.size}/${this.store.get(connection.socketBox)?.maxConnections ?? 1})`);
    this.logger.debug(`Mailbox admitted: ${connection.socketBox} ${connection.connectionId}${connection.legacy ? ' legacy' : ''}`);
    this.updateStatus(true);
    if (flush) void this.flush(connection.socketBox);
  }

  private removeActive(connection: ConnectionRecord) {
    if (!connection.admitted) return;
    const key = connection.socketBox.toLowerCase();
    const bucket = this.activeConnections.get(key);
    bucket?.delete(connection.connectionId);
    if (bucket && bucket.size === 0) this.activeConnections.delete(key);
    connection.admitted = false;
    this.logger.system(`${connection.socketBox.toUpperCase()} DISCONNECTED`);
  }

  private activeFor(mailbox: string) {
    const bucket = this.activeConnections.get(mailbox.toLowerCase());
    if (!bucket) return [] as ConnectionRecord[];
    const list = [...bucket.values()].filter(c => c.admitted && c.ws.readyState === WebSocket.OPEN);
    for (const [id, c] of bucket) if (!list.includes(c)) bucket.delete(id);
    if (bucket.size === 0) this.activeConnections.delete(mailbox.toLowerCase());
    return list;
  }

  private findActive(mailbox: string, connectionId: string) {
    return this.activeFor(mailbox).find(c => c.connectionId === connectionId);
  }

  private findActiveById(connectionId: string) {
    for (const box of this.store.list()) {
      const found = this.findActive(box.id, connectionId);
      if (found) return found;
    }
    return undefined;
  }

  private receive(connection: ConnectionRecord, payload: string) {
    let m: VppEnvelope;
    const logId = this.nextMessageId++;
    try { m = JSON.parse(payload); }
    catch (e) {
      this.logger.message(logId, connection.socketBox, 'server', 'DROPPED', payload);
      this.logger.debug(`Invalid JSON from ${connection.socketBox}`, e);
      return;
    }

    const preliminaryTarget = m.recipient ?? (typeof m.to === 'string' ? m.to : Array.isArray(m.to) ? m.to.join(',') : 'server');
    this.logger.message(logId, connection.socketBox, preliminaryTarget || 'server', 'RECEIVED', payload);

    if (m.protocolVersion !== 1 || !m.id || !m.type || m.from !== connection.socketBox) {
      this.serverError(connection, m, 'INVALID_MESSAGE', 'Missing/invalid VPP envelope or from mismatch');
      return;
    }

    if (!connection.admitted && !connection.negotiating) {
      if (m.recipient === 'server' && m.type === 'call' && m.method === 'registerConnection') {
        this.handleRegister(connection, m);
        return;
      }
      if (!this.admitLegacy(connection)) return;
    }

    if (connection.negotiating) {
      if (m.recipient !== 'server' || m.type !== 'call' || !['ping', 'replaceConnection', 'cancelConnectionNegotiation'].includes(m.method ?? '')) {
        this.serverError(connection, m, 'INVALID_ROUTING', 'Normal Socket Box traffic is not allowed during replacement negotiation');
        return;
      }
      this.handleServer(connection, m);
      return;
    }

    if (m.recipient === 'server') {
      this.handleServer(connection, m);
      return;
    }

    if (m.correlationId && ['progress', 'response', 'error'].includes(m.type)) {
      if (this.routeCorrelatedReply(connection, m, logId)) return;
    }

    const targets = this.targets(m);
    if (targets.length === 0) {
      this.serverError(connection, m, 'INVALID_ROUTING', 'No valid recipient');
      return;
    }
    if (targets.length > 1 && m.expectsResponse === true) {
      this.serverError(connection, m, 'INVALID_ROUTING', 'A response-requesting message must have one recipient');
      return;
    }

    for (const target of targets) {
      if (target.toLowerCase() === connection.socketBox.toLowerCase() || !this.store.get(target) || !this.store.isAllowed(connection.socketBox, target)) {
        this.serverError(connection, m, 'INVALID_ROUTING', `recipient ${target} is not allowed`);
        this.logger.debug(`Routing rejected ${connection.socketBox} -> ${target}`);
        continue;
      }
      const copy = { ...m, recipient: target };
      delete copy.to;
      this.route(connection, target, copy, logId);
    }
  }

  private targets(m: VppEnvelope) {
    if (typeof m.recipient === 'string' && m.recipient) return [m.recipient];
    if (typeof m.to === 'string') return [m.to];
    if (Array.isArray(m.to)) return [...new Set(m.to.filter(x => typeof x === 'string' && x))];
    return [];
  }

  private route(origin: ConnectionRecord, target: string, m: VppEnvelope, logId: number) {
    const payload = JSON.stringify(m);
    const active = this.activeFor(target);

    if (m.targetConnectionId) {
      const exact = active.find(c => c.connectionId === m.targetConnectionId);
      if (!exact) {
        this.serverError(origin, m, 'CONNECTION_NOT_FOUND', `Connection ${m.targetConnectionId} is not active for ${target}`);
        this.logger.message(logId, origin.socketBox, target, 'DROPPED');
        return;
      }
      exact.ws.send(payload);
      this.trackRequest(origin, exact, m);
      this.logger.message(logId, origin.socketBox, target, 'SENT');
      return;
    }

    if (active.length === 1) {
      active[0].ws.send(payload);
      this.trackRequest(origin, active[0], m);
      this.logger.message(logId, origin.socketBox, target, 'SENT');
      return;
    }

    if (active.length > 1) {
      if (m.expectsResponse === true) {
        this.serverError(origin, m, 'AMBIGUOUS_RECIPIENT', `Socket Box ${target} has ${active.length} active connections`);
        this.logger.message(logId, origin.socketBox, target, 'DROPPED');
        return;
      }
      for (const c of active) c.ws.send(payload);
      this.logger.message(logId, origin.socketBox, target, 'SENT', `fan-out ${active.length}`);
      return;
    }

    this.queue(origin, target, m, payload, logId);
  }

  private queue(origin: ConnectionRecord, target: string, m: VppEnvelope, payload: string, logId: number) {
    const box = this.store.get(target)!;
    if (box.queueMode === 'OFF') {
      this.logger.message(logId, origin.socketBox, target, 'DROPPED');
      return;
    }
    const now = Date.now();
    const expires = box.ttlSeconds === 0 ? null : now + box.ttlSeconds * 1000;
    const policy = m.queue?.policy === 'replace' ? 'replace' : 'fifo';
    const key = policy === 'replace' && typeof m.queue?.key === 'string' && m.queue.key ? m.queue.key : null;
    const q: MemoryMessage = {
      messageId: m.id!,
      source: origin.socketBox,
      target,
      payload,
      receivedAt: now,
      expiresAt: expires,
      queuePolicy: policy,
      queueKey: key,
      expectsResponse: m.expectsResponse === true,
      originConnectionId: origin.connectionId,
      targetConnectionId: m.targetConnectionId ?? null,
    };

    if (box.queueMode === 'PERSISTENT') {
      const old = this.store.storePersistent(q);
      for (const replaced of old) this.notifySuperseded(replaced);
    } else {
      const arr = this.memoryQueues.get(target.toLowerCase()) ?? [];
      if (policy === 'replace' && key) {
        for (let i = arr.length - 1; i >= 0; i--) {
          const old = arr[i];
          if (old.source.toLowerCase() === origin.socketBox.toLowerCase() && old.queuePolicy === 'replace' && old.queueKey === key && old.originConnectionId === origin.connectionId && old.targetConnectionId === (m.targetConnectionId ?? null)) {
            arr.splice(i, 1);
            this.notifySuperseded(old);
          }
        }
      }
      arr.push(q);
      this.memoryQueues.set(target.toLowerCase(), arr);
    }
    this.logger.message(logId, origin.socketBox, target, 'QUEUED', box.queueMode);
  }

  private notifySuperseded(m: Omit<StoredMessage, 'rowId'> | StoredMessage) {
    if (!m.expectsResponse || !m.originConnectionId) return;
    this.sendTransportErrorToConnectionId(m.originConnectionId, m.source, m.messageId, 'SUPERSEDED', 'Queued message was replaced by a newer value');
  }

  private trackRequest(origin: ConnectionRecord, target: ConnectionRecord, m: VppEnvelope) {
    if (m.expectsResponse !== true || !m.id) return;
    this.pendingRequests.set(this.requestKey(target.socketBox, m.id), {
      originConnectionId: origin.connectionId,
      originMailbox: origin.socketBox,
      targetConnectionId: target.connectionId,
      targetMailbox: target.socketBox,
    });
  }

  private routeCorrelatedReply(sender: ConnectionRecord, m: VppEnvelope, logId: number) {
    const pending = this.pendingRequests.get(this.requestKey(sender.socketBox, m.correlationId!));
    if (!pending) return false;
    if ((m.recipient ?? '').toLowerCase() !== pending.originMailbox.toLowerCase()) {
      this.serverError(sender, m, 'INVALID_ROUTING', 'Correlated reply recipient does not match the originating Socket Box');
      return true;
    }
    if (sender.connectionId !== pending.targetConnectionId) {
      this.serverError(sender, m, 'INVALID_ROUTING', 'Correlated reply came from a different destination connection');
      return true;
    }
    const origin = this.findActiveById(pending.originConnectionId);
    if (origin && origin.ws.readyState === WebSocket.OPEN) {
      origin.ws.send(JSON.stringify(m));
      this.logger.message(logId, sender.socketBox, pending.originMailbox, 'SENT');
    } else {
      this.logger.message(logId, sender.socketBox, pending.originMailbox, 'DROPPED', 'origin connection gone');
    }
    if (m.type === 'response' || m.type === 'error') this.pendingRequests.delete(this.requestKey(sender.socketBox, m.correlationId!));
    return true;
  }

  private requestKey(targetMailbox: string, requestId: string) { return `${targetMailbox.toLowerCase()}\u0000${requestId}`; }

  private async flush(target: string) {
    if (this.activeFor(target).length === 0) return;
    const key = target.toLowerCase();
    const now = Date.now();
    const mem = this.memoryQueues.get(key) ?? [];
    const keep: MemoryMessage[] = [];
    for (const m of mem) {
      if (m.expiresAt !== null && m.expiresAt <= now) continue;
      const handled = await this.deliverStored(m);
      if (!handled) keep.push(m);
    }
    this.memoryQueues.set(key, keep);

    for (const m of this.store.persistentFor(target)) {
      if (m.expiresAt !== null && m.expiresAt <= Date.now()) { this.store.deletePersistent(m.rowId); continue; }
      try {
        const handled = await this.deliverStored(m);
        if (handled) this.store.deletePersistent(m.rowId);
      } catch (e) {
        this.logger.debug(`Persistent queue flush failed for ${target}`, e);
        break;
      }
    }
  }

  private async deliverStored(m: Omit<StoredMessage, 'rowId'> | StoredMessage): Promise<boolean> {
    const active = this.activeFor(m.target);
    if (active.length === 0) return false;
    try { JSON.parse(m.payload); } catch { return true; }

    if (m.targetConnectionId) {
      const exact = active.find(c => c.connectionId === m.targetConnectionId);
      if (!exact) {
        if (m.expectsResponse && m.originConnectionId) this.sendTransportErrorToConnectionId(m.originConnectionId, m.source, m.messageId, 'CONNECTION_NOT_FOUND', 'Queued target connection is no longer active');
        return true;
      }
      await this.send(exact.ws, m.payload);
      this.trackStoredRequest(m, exact);
      return true;
    }

    if (active.length === 1) {
      await this.send(active[0].ws, m.payload);
      this.trackStoredRequest(m, active[0]);
      return true;
    }

    if (m.expectsResponse) {
      if (m.originConnectionId) this.sendTransportErrorToConnectionId(m.originConnectionId, m.source, m.messageId, 'AMBIGUOUS_RECIPIENT', `Socket Box ${m.target} has ${active.length} active connections`);
      return true;
    }

    await Promise.all(active.map(c => this.send(c.ws, m.payload)));
    return true;
  }

  private trackStoredRequest(m: Omit<StoredMessage, 'rowId'> | StoredMessage, target: ConnectionRecord) {
    if (!m.expectsResponse || !m.originConnectionId) return;
    this.pendingRequests.set(this.requestKey(target.socketBox, m.messageId), {
      originConnectionId: m.originConnectionId,
      originMailbox: m.source,
      targetConnectionId: target.connectionId,
      targetMailbox: target.socketBox,
    });
  }

  private handleServer(connection: ConnectionRecord, m: VppEnvelope) {
    if (m.type !== 'call') {
      this.serverError(connection, m, 'UNKNOWN_METHOD', 'Unsupported server request');
      return;
    }
    switch (m.method) {
      case 'ping': this.handlePing(connection, m); return;
      case 'registerConnection': this.handleRegister(connection, m); return;
      case 'replaceConnection': void this.handleReplace(connection, m); return;
      case 'cancelConnectionNegotiation': void this.handleCancelNegotiation(connection, m); return;
      default: this.serverError(connection, m, 'UNKNOWN_METHOD', 'Unsupported server request');
    }
  }

  private handlePing(connection: ConnectionRecord, m: VppEnvelope) {
    if (!this.isEmptyObject(m.args)) {
      this.serverError(connection, m, 'UNKNOWN_METHOD', 'Unsupported server request');
      return;
    }
    const boxes: Record<string, { connected: boolean }> = {};
    for (const b of this.store.list()) boxes[b.id] = { connected: this.activeFor(b.id).length > 0 };
    const hb = (this.store.get(connection.socketBox)?.heartbeatSeconds ?? 30) * 1000;
    this.sendResponse(connection, m, { mailboxes: boxes, heartbeat: { intervalMs: hb } });
  }

  private handleRegister(connection: ConnectionRecord, m: VppEnvelope) {
    const args = this.objectArgs(m.args);
    if (!args || Object.keys(args).some(k => k !== 'hostName') || ('hostName' in args && (typeof args.hostName !== 'string' || args.hostName.trim().length === 0)) || m.expectsResponse !== true) {
      this.serverError(connection, m, 'INVALID_ARGUMENT', 'registerConnection args may contain only a non-empty hostName');
      return;
    }
    if (connection.registrationTimer) { clearTimeout(connection.registrationTimer); connection.registrationTimer = undefined; }
    connection.hostName = typeof args.hostName === 'string' ? args.hostName.trim() : undefined;
    connection.service = typeof m.source?.app === 'string' && m.source.app.trim() ? m.source.app.trim() : connection.service;

    if (connection.admitted) {
      connection.legacy = false;
      this.sendResponse(connection, m, this.admittedResult(connection));
      return;
    }
    if (connection.negotiating) {
      this.serverError(connection, m, 'CONNECTION_NEGOTIATION_IN_PROGRESS', 'This connection is already negotiating replacement');
      return;
    }

    const box = this.store.get(connection.socketBox);
    if (!box) {
      this.serverError(connection, m, 'INVALID_ROUTING', 'Unknown Socket Box');
      return;
    }
    if (this.activeFor(connection.socketBox).length < box.maxConnections) {
      this.admit(connection, false);
      this.sendResponse(connection, m, this.admittedResult(connection));
      void this.flush(connection.socketBox);
      return;
    }

    const key = connection.socketBox.toLowerCase();
    if (this.negotiations.has(key)) {
      this.serverError(connection, m, 'CONNECTION_NEGOTIATION_IN_PROGRESS', 'Another connection is already negotiating replacement');
      void this.closeAfterSend(connection, 4004, 'Connection negotiation already in progress');
      return;
    }

    connection.negotiating = true;
    const expiresAt = new Date(Date.now() + NEGOTIATION_TIMEOUT_MS).toISOString();
    const timer = setTimeout(() => void this.expireNegotiation(connection), NEGOTIATION_TIMEOUT_MS);
    this.negotiations.set(key, { connection, expiresAt, timer });
    this.sendResponse(connection, m, this.negotiationResult(connection.socketBox, expiresAt));
    this.logger.debug(`Replacement negotiation started for ${connection.socketBox} ${connection.connectionId}`);
  }

  private async handleReplace(connection: ConnectionRecord, m: VppEnvelope) {
    const negotiation = this.negotiations.get(connection.socketBox.toLowerCase());
    const args = this.objectArgs(m.args);
    const selectedId = args && Object.keys(args).length === 1 && typeof args.connectionId === 'string' ? args.connectionId.trim() : '';
    if (!connection.negotiating || negotiation?.connection !== connection || !selectedId || m.expectsResponse !== true) {
      this.serverError(connection, m, 'INVALID_ARGUMENT', 'replaceConnection requires exactly one non-empty connectionId from the active negotiating connection');
      return;
    }

    const selected = this.findActive(connection.socketBox, selectedId);
    const box = this.store.get(connection.socketBox)!;
    if (selected) {
      void this.sendServerEventConnection(selected, 'disconnecting', { reason: 'replaced' });
      this.removeActive(selected);
      selected.ws.close(4000, 'Replaced by authenticated connection');
    }

    if (this.activeFor(connection.socketBox).length < box.maxConnections) {
      clearTimeout(negotiation.timer);
      this.negotiations.delete(connection.socketBox.toLowerCase());
      connection.negotiating = false;
      this.admit(connection, false);
      this.sendResponse(connection, m, this.admittedResult(connection));
      void this.flush(connection.socketBox);
      return;
    }

    this.sendResponse(connection, m, this.negotiationResult(connection.socketBox, negotiation.expiresAt));
  }

  private async handleCancelNegotiation(connection: ConnectionRecord, m: VppEnvelope) {
    const negotiation = this.negotiations.get(connection.socketBox.toLowerCase());
    if (!connection.negotiating || negotiation?.connection !== connection || !this.isEmptyObject(m.args) || m.expectsResponse !== true) {
      this.serverError(connection, m, 'INVALID_ARGUMENT', 'cancelConnectionNegotiation requires args: {} from the active negotiating connection');
      return;
    }
    clearTimeout(negotiation.timer);
    this.negotiations.delete(connection.socketBox.toLowerCase());
    connection.negotiating = false;
    await this.sendResponseAsync(connection, m, { status: 'cancelled' });
    connection.ws.close(1000, 'Connection negotiation cancelled');
  }

  private async expireNegotiation(connection: ConnectionRecord) {
    const key = connection.socketBox.toLowerCase();
    const negotiation = this.negotiations.get(key);
    if (negotiation?.connection !== connection) return;
    this.negotiations.delete(key);
    connection.negotiating = false;
    await this.sendServerEventConnection(connection, 'disconnecting', { reason: 'negotiationTimeout' });
    connection.ws.close(4005, 'Connection negotiation timeout');
    this.logger.debug(`Replacement negotiation timed out for ${connection.socketBox} ${connection.connectionId}`);
  }

  private admittedResult(connection: ConnectionRecord) {
    const box = this.store.get(connection.socketBox)!;
    return {
      status: 'admitted',
      maxConnections: box.maxConnections,
      currentConnections: this.activeFor(connection.socketBox).length,
      connection: this.publicConnection(connection),
    };
  }

  private negotiationResult(mailbox: string, expiresAt: string) {
    const box = this.store.get(mailbox)!;
    const active = this.activeFor(mailbox);
    return {
      status: 'replacementNegotiation',
      maxConnections: box.maxConnections,
      currentConnections: active.length,
      expiresAt,
      connections: active.map(c => this.publicConnection(c)),
    };
  }

  private publicConnection(connection: ConnectionRecord) {
    const value: Record<string, unknown> = {
      connectionId: connection.connectionId,
      socketBox: connection.socketBox,
      ip: connection.ip,
      connectedAt: connection.connectedAt,
    };
    if (connection.hostName) value.hostName = connection.hostName;
    if (connection.service) value.service = connection.service;
    return value;
  }

  private objectArgs(value: unknown): Record<string, unknown> | undefined {
    return value !== null && typeof value === 'object' && !Array.isArray(value) ? value as Record<string, unknown> : undefined;
  }

  private isEmptyObject(value: unknown) {
    const args = this.objectArgs(value);
    return !!args && Object.keys(args).length === 0;
  }

  private serverError(connection: ConnectionRecord, m: VppEnvelope, code: string, message: string) {
    this.logger.debug(`Server error for ${connection.socketBox} ${connection.connectionId}: ${code} ${message}`);
    this.logger.message(this.nextMessageId++, connection.socketBox, 'server', 'ERROR', `${code}: ${message}`);
    if (!m?.id || m.expectsResponse === false) return;
    this.sendRaw(connection, JSON.stringify({
      protocolVersion: 1,
      id: crypto.randomUUID(),
      correlationId: m.id,
      type: 'error',
      from: 'server',
      recipient: connection.socketBox,
      error: { code, message },
      source: { app: 'SocketUniverseBridge', version: VERSION },
      timestamp: new Date().toISOString(),
    }));
  }

  private sendTransportErrorToConnectionId(connectionId: string, mailbox: string, correlationId: string, code: string, message: string) {
    const connection = this.findActiveById(connectionId);
    if (!connection || connection.socketBox.toLowerCase() !== mailbox.toLowerCase()) return;
    this.sendRaw(connection, JSON.stringify({
      protocolVersion: 1,
      id: crypto.randomUUID(),
      correlationId,
      type: 'error',
      from: 'server',
      recipient: mailbox,
      error: { code, message },
      source: { app: 'SocketUniverseBridge', version: VERSION },
      timestamp: new Date().toISOString(),
    }));
  }

  private sendResponse(connection: ConnectionRecord, m: VppEnvelope, result: unknown) {
    this.sendRaw(connection, this.responsePayload(connection, m, result));
  }

  private sendResponseAsync(connection: ConnectionRecord, m: VppEnvelope, result: unknown) {
    return this.send(connection.ws, this.responsePayload(connection, m, result));
  }

  private responsePayload(connection: ConnectionRecord, m: VppEnvelope, result: unknown) {
    return JSON.stringify({
      protocolVersion: 1,
      id: crypto.randomUUID(),
      correlationId: m.id,
      type: 'response',
      from: 'server',
      recipient: connection.socketBox,
      result,
      source: { app: 'SocketUniverseBridge', version: VERSION },
      timestamp: new Date().toISOString(),
    });
  }

  private sendServerEventConnection(connection: ConnectionRecord, event: string, args: Record<string, unknown>) {
    if (connection.ws.readyState !== WebSocket.OPEN) return Promise.resolve();
    return this.send(connection.ws, JSON.stringify({
      protocolVersion: 1,
      id: crypto.randomUUID(),
      type: 'event',
      from: 'server',
      recipient: connection.socketBox,
      event,
      args,
      expectsResponse: false,
      source: { app: 'SocketUniverseBridge', version: VERSION },
      timestamp: new Date().toISOString(),
    }));
  }

  private sendRaw(connection: ConnectionRecord, payload: string) {
    if (connection.ws.readyState === WebSocket.OPEN) connection.ws.send(payload);
  }

  private async closeAfterSend(connection: ConnectionRecord, code: number, reason: string) {
    await new Promise(resolve => setTimeout(resolve, 0));
    if (connection.ws.readyState === WebSocket.OPEN) connection.ws.close(code, reason);
  }

  private send(ws: WebSocket, payload: string) {
    return new Promise<void>((resolve, reject) => ws.send(payload, e => e ? reject(e) : resolve()));
  }

  private connectionStates() {
    const states: Record<string, boolean> = {};
    for (const b of this.store.list()) states[b.id] = this.activeFor(b.id).length > 0;
    return states;
  }

  private updateStatus(running: boolean) {
    const states = this.connectionStates();
    this.statusWriter.update({
      serverRunning: running,
      vpConnected: states.vp === true,
      bcConnected: states.bc === true,
      mailboxes: this.store.list().map(b => b.id),
      connections: states,
      host: this.config.server.host,
      port: this.config.server.port,
    });
  }
}
