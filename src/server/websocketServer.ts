import http from 'node:http';
import crypto from 'node:crypto';
import { WebSocketServer, WebSocket } from 'ws';
import { StatusWriter } from '../status/statusWriter';
import type { VPBridgeConfig } from '../config/config';
import { MessageQueue } from '../bridge/messageQueue';
import type { BridgeMessage, MessageDirection } from '../bridge/message';
import { Logger } from '../logging/logger';

const VERSION = '0.7.1';
type Mailbox = 'vp' | 'bc';
type Side = 'VP' | 'BC';
export type GracefulDisconnectReason = 'shutdown' | 'restart' | 'exit';

type VppEnvelope = {
  protocolVersion?: number;
  id?: string;
  type?: string;
  from?: string;
  recipient?: string;
  method?: string;
  args?: unknown;
  expectsResponse?: boolean;
};

export class VPBridgeServer {
  private readonly httpServer: http.Server;
  private readonly wsServer: WebSocketServer;
  private readonly vpToBcQueue: MessageQueue;
  private readonly bcToVpQueue: MessageQueue;
  private readonly statusWriter: StatusWriter;
  private vpClient?: WebSocket;
  private bcClient?: WebSocket;
  private nextMessageId = 1;
  private flushingVpToBc = false;
  private flushingBcToVp = false;
  private stopping = false;

  constructor(private readonly config: VPBridgeConfig, private readonly logger: Logger) {
    this.vpToBcQueue = new MessageQueue(config.queue.maxMessages);
    this.bcToVpQueue = new MessageQueue(config.queue.maxMessages);
    this.statusWriter = new StatusWriter({ serverRunning:false, vpConnected:false, bcConnected:false, host:config.server.host, port:config.server.port });
    this.httpServer = http.createServer();
    this.wsServer = new WebSocketServer({ noServer:true });

    this.httpServer.on('upgrade', (request, socket, head) => {
      const url = new URL(request.url ?? '/', `http://${request.headers.host ?? 'localhost'}`);
      const pathname = url.pathname;
      if (pathname !== config.server.vpPath && pathname !== config.server.bcPath) {
        socket.write('HTTP/1.1 404 Not Found\r\n\r\n'); socket.destroy(); return;
      }
      if (config.server.mode === 'all') {
        const suppliedKey = url.searchParams.get('apiKey') ?? '';
        if (!this.isApiKeyValid(suppliedKey)) {
          this.logger.system(`AUTH REJECTED for ${pathname} from ${request.socket.remoteAddress ?? 'unknown'}`);
          socket.write('HTTP/1.1 401 Unauthorized\r\nConnection: close\r\n\r\n'); socket.destroy(); return;
        }
      }
      this.wsServer.handleUpgrade(request, socket, head, ws => this.wsServer.emit('connection', ws, request, pathname));
    });

    this.wsServer.on('connection', (ws:WebSocket, _request:http.IncomingMessage, pathname:string) => {
      if (pathname === config.server.vpPath) this.attachVp(ws); else this.attachBc(ws);
    });
  }

  start():Promise<void> { return new Promise((resolve,reject)=>{this.httpServer.once('error',reject);this.httpServer.listen(this.config.server.port,this.config.server.host,()=>{this.httpServer.off('error',reject);this.logger.system(`VPBridge v${VERSION} listening on ws://${this.config.server.host}:${this.config.server.port}`);this.logger.system(`Heartbeat interval: ${this.config.heartbeat.intervalMs} ms`);this.statusWriter.update({serverRunning:true,vpConnected:false,bcConnected:false,host:this.config.server.host,port:this.config.server.port});resolve();});}); }

  async stop(reason:GracefulDisconnectReason='shutdown'):Promise<void> {
    if(this.stopping)return;
    this.stopping=true;
    this.logger.system(`Stopping VPBridge (${reason}); announcing graceful disconnect and clearing both RAM queues`);
    await this.announceDisconnecting(reason);
    this.vpToBcQueue.clear();
    this.bcToVpQueue.clear();
    this.vpClient?.close(1001,`VPBridge ${reason}`);
    this.bcClient?.close(1001,`VPBridge ${reason}`);
    this.wsServer.close();
    this.statusWriter.update({serverRunning:false,vpConnected:false,bcConnected:false});
    await new Promise<void>(resolve=>this.httpServer.close(()=>resolve()));
  }

  private async announceDisconnecting(reason:GracefulDisconnectReason):Promise<void> {
    const sends:Promise<void>[]=[];
    if(this.vpClient?.readyState===WebSocket.OPEN) sends.push(this.sendServerEvent('vp','disconnecting',{reason}));
    if(this.bcClient?.readyState===WebSocket.OPEN) sends.push(this.sendServerEvent('bc','disconnecting',{reason}));
    if(sends.length===0)return;
    await Promise.allSettled(sends);
  }

  private async sendServerEvent(mailbox:Mailbox,event:string,args:Record<string,unknown>):Promise<void> {
    const ws=mailbox==='vp'?this.vpClient:this.bcClient;
    if(!ws||ws.readyState!==WebSocket.OPEN)return;
    const payload=JSON.stringify({protocolVersion:1,id:crypto.randomUUID(),type:'event',from:'server',recipient:mailbox,event,args,expectsResponse:false,source:{app:'VoicePrompterBridge',version:VERSION},timestamp:new Date().toISOString()});
    try { await this.send(ws,payload); this.logger.system(`SERVER→${mailbox.toUpperCase()} ${event} SENT`); }
    catch(err){ this.logger.system(`SERVER→${mailbox.toUpperCase()} ${event} SEND ERROR: ${err instanceof Error?err.message:String(err)}`); }
  }

  private attachVp(ws:WebSocket):void {
    if(this.vpClient?.readyState===WebSocket.OPEN){this.logger.system('New VP connection replaced previous VP connection');this.vpClient.close(4000,'Replaced by new VP connection');}
    this.vpClient=ws; this.logger.system('VP CONNECTED'); this.statusWriter.update({vpConnected:true});
    ws.on('message',(data,isBinary)=>{if(isBinary){this.logger.system('VP binary message ignored');return;}this.receiveMessage('vp','VP_TO_BC',data.toString());});
    ws.on('close',()=>{if(this.vpClient!==ws){this.logger.system('VP DISCONNECTED (replaced connection)');return;}this.vpClient=undefined;this.logger.system('VP DISCONNECTED');this.statusWriter.update({vpConnected:false});this.handleDestinationDisconnect('BC_TO_VP');});
    ws.on('error',err=>this.logger.system(`VP ERROR: ${err.message}`)); void this.flushDirection('BC_TO_VP');
  }

  private attachBc(ws:WebSocket):void {
    if(this.bcClient?.readyState===WebSocket.OPEN){this.logger.system('New BC connection replaced previous BC connection');this.bcClient.close(4000,'Replaced by new BC connection');}
    this.bcClient=ws; this.logger.system('BC CONNECTED'); this.statusWriter.update({bcConnected:true});
    ws.on('message',(data,isBinary)=>{if(isBinary){this.logger.system('BC binary message ignored');return;}this.receiveMessage('bc','BC_TO_VP',data.toString());});
    ws.on('close',()=>{if(this.bcClient!==ws){this.logger.system('BC DISCONNECTED (replaced connection)');return;}this.bcClient=undefined;this.logger.system('BC DISCONNECTED');this.statusWriter.update({bcConnected:false});this.handleDestinationDisconnect('VP_TO_BC');});
    ws.on('error',err=>this.logger.system(`BC ERROR: ${err.message}`)); void this.flushDirection('VP_TO_BC');
  }

  private receiveMessage(sender:Mailbox,direction:MessageDirection,payload:string):void {
    let parsed:VppEnvelope;
    try { parsed=JSON.parse(payload) as VppEnvelope; } catch { const id=this.nextMessageId++;this.logger.message(id,direction,'DROPPED',payload);this.logger.system(`#${id} ${this.directionLabel(direction)} dropped: INVALID JSON`);return; }
    const logId=this.nextMessageId++;
    this.logger.message(logId,direction,'RECEIVED',payload);

    if(!parsed || typeof parsed!=='object' || Array.isArray(parsed)) { this.dropRouting(logId,direction,'INVALID_MESSAGE'); return; }
    if(parsed.protocolVersion!==1 || typeof parsed.id!=='string' || !parsed.id || typeof parsed.type!=='string' || typeof parsed.from!=='string' || typeof parsed.recipient!=='string') { this.serverError(sender,parsed,'INVALID_MESSAGE','Missing or invalid VPP envelope fields');this.dropRouting(logId,direction,'INVALID VPP ENVELOPE');return; }
    if(parsed.from!==sender) { this.serverError(sender,parsed,'INVALID_ROUTING',`from must be ${sender} on this connection`);this.dropRouting(logId,direction,'INVALID FROM');return; }

    if(parsed.recipient==='server') { this.handleServerMessage(sender,parsed);this.logger.message(logId,direction,'SENT');return; }
    const expectedRecipient:Mailbox=sender==='vp'?'bc':'vp';
    if(parsed.recipient!==expectedRecipient) { this.serverError(sender,parsed,'INVALID_ROUTING',`recipient must be ${expectedRecipient} or server`);this.dropRouting(logId,direction,'INVALID RECIPIENT');return; }

    const message:BridgeMessage={id:logId,receivedAt:new Date(),direction,payload};
    const destinationOnline=this.isDestinationOnline(direction); const queue=this.getQueue(direction);
    if(!destinationOnline){
      if(this.config.queue.offlineBufferSize===0){this.logger.message(logId,direction,'DROPPED');this.logger.system(`#${logId} ${this.directionLabel(direction)} dropped: ${this.destinationName(direction)} not connected and offline buffer disabled`);return;}
      message.bufferedAt=new Date();this.purgeExpiredOfflineMessages(direction);
      while(queue.length>=this.config.queue.offlineBufferSize){const dropped=queue.dequeue();if(!dropped)break;this.logger.message(dropped.id,dropped.direction,'DROPPED');}
    }
    if(!queue.enqueue(message)){this.logger.message(logId,direction,'DROPPED');this.logger.system(`${this.directionLabel(direction)} queue full (${queue.length}/${this.config.queue.maxMessages})`);return;}
    this.logger.message(logId,direction,'QUEUED');void this.flushDirection(direction);
  }

  private handleServerMessage(sender:Mailbox,m:VppEnvelope):void {
    if(m.type!=='call' || m.method!=='ping' || !m.args || typeof m.args!=='object' || Array.isArray(m.args) || Object.keys(m.args as object).length!==0){this.serverError(sender,m,'UNKNOWN_METHOD','Unsupported server request');return;}
    const response={protocolVersion:1,id:crypto.randomUUID(),correlationId:m.id,type:'response',from:'server',recipient:sender,result:{mailboxes:{vp:{connected:this.vpClient?.readyState===WebSocket.OPEN},bc:{connected:this.bcClient?.readyState===WebSocket.OPEN}},heartbeat:{intervalMs:this.config.heartbeat.intervalMs}},source:{app:'VoicePrompterBridge',version:VERSION},timestamp:new Date().toISOString()};
    this.sendToMailbox(sender,JSON.stringify(response));
  }

  private serverError(sender:Mailbox,m:VppEnvelope,code:string,message:string):void {
    if(!m?.id || m.expectsResponse===false) return;
    const error={protocolVersion:1,id:crypto.randomUUID(),correlationId:m.id,type:'error',from:'server',recipient:sender,error:{code,message},source:{app:'VoicePrompterBridge',version:VERSION},timestamp:new Date().toISOString()};
    this.sendToMailbox(sender,JSON.stringify(error));
  }

  private sendToMailbox(mailbox:Mailbox,payload:string):void { const ws=mailbox==='vp'?this.vpClient:this.bcClient;if(!ws||ws.readyState!==WebSocket.OPEN)return;ws.send(payload,err=>{if(err)this.logger.system(`SERVER→${mailbox.toUpperCase()} SEND ERROR: ${err.message}`);}); }
  private dropRouting(id:number,direction:MessageDirection,reason:string):void { this.logger.message(id,direction,'DROPPED');this.logger.system(`#${id} ${this.directionLabel(direction)} dropped: ${reason}`); }

  private async flushDirection(direction:MessageDirection):Promise<void>{if(direction==='VP_TO_BC'){if(this.flushingVpToBc)return;this.flushingVpToBc=true;}else{if(this.flushingBcToVp)return;this.flushingBcToVp=true;}try{const destination=this.getDestinationClient(direction);if(!destination||destination.readyState!==WebSocket.OPEN)return;this.purgeExpiredOfflineMessages(direction);const queue=this.getQueue(direction);while(true){const active=this.getDestinationClient(direction);if(!active||active.readyState!==WebSocket.OPEN)break;const message=queue.peek();if(!message)break;try{await this.send(active,message.payload);queue.dequeue();this.logger.message(message.id,message.direction,'SENT');}catch(err){this.logger.message(message.id,message.direction,'ERROR');this.logger.system(`${this.directionLabel(direction)} SEND ERROR: ${err instanceof Error?err.message:String(err)}`);break;}}}finally{if(direction==='VP_TO_BC')this.flushingVpToBc=false;else this.flushingBcToVp=false;}}

  private handleDestinationDisconnect(direction:MessageDirection):void{const queue=this.getQueue(direction);if(queue.length===0)return;if(this.config.queue.offlineBufferSize===0){while(true){const m=queue.dequeue();if(!m)break;this.logger.message(m.id,m.direction,'DROPPED');}return;}const now=new Date();const retained:BridgeMessage[]=[];while(true){const m=queue.dequeue();if(!m)break;m.bufferedAt??=now;retained.push(m);}const keepFrom=Math.max(0,retained.length-this.config.queue.offlineBufferSize);for(let i=0;i<retained.length;i++){if(i<keepFrom)this.logger.message(retained[i].id,retained[i].direction,'DROPPED');else queue.enqueue(retained[i]);}}
  private purgeExpiredOfflineMessages(direction:MessageDirection):void{const maxAge=this.config.queue.offlineBufferMaxAgeMs;if(maxAge<=0)return;const queue=this.getQueue(direction),now=Date.now();while(true){const m=queue.peek();if(!m||!m.bufferedAt||now-m.bufferedAt.getTime()<=maxAge)break;queue.dequeue();this.logger.message(m.id,m.direction,'DROPPED');}}
  private getQueue(direction:MessageDirection):MessageQueue{return direction==='VP_TO_BC'?this.vpToBcQueue:this.bcToVpQueue;}
  private getDestinationClient(direction:MessageDirection):WebSocket|undefined{return direction==='VP_TO_BC'?this.bcClient:this.vpClient;}
  private isDestinationOnline(direction:MessageDirection):boolean{const c=this.getDestinationClient(direction);return !!c&&c.readyState===WebSocket.OPEN;}
  private destinationName(direction:MessageDirection):Side{return direction==='VP_TO_BC'?'BC':'VP';}
  private directionLabel(direction:MessageDirection):string{return direction==='VP_TO_BC'?'VP→BC':'BC→VP';}
  private isApiKeyValid(key:string):boolean{const expected=this.config.security.apiKey;if(!/^[a-fA-F0-9]{64}$/.test(expected)||key.length!==expected.length)return false;try{return crypto.timingSafeEqual(Buffer.from(key),Buffer.from(expected));}catch{return false;}}
  private send(ws:WebSocket,payload:string):Promise<void>{return new Promise((resolve,reject)=>ws.send(payload,err=>err?reject(err):resolve()));}
}
