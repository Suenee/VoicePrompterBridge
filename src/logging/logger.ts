import fs from 'node:fs';
import path from 'node:path';
import type {VPBridgeConfig} from '../config/config';
export type TransportStatus='RECEIVED'|'SENT'|'QUEUED'|'DROPPED'|'ERROR';
export class Logger {
 private readonly logFile:string;private readonly debugFile:string;private cleanupTimer?:NodeJS.Timeout;
 constructor(private readonly config:VPBridgeConfig['logging']){const dir=path.resolve(process.cwd(),config.directory);fs.mkdirSync(dir,{recursive:true});this.logFile=path.join(dir,'SocketUniverseBridge.log');this.debugFile=path.join(dir,'debug.log');if(config.debugMode==='SINGLE')try{fs.writeFileSync(this.debugFile,'','utf8')}catch{}if(this.config.enabled){this.cleanup();this.cleanupTimer=setInterval(()=>this.cleanup(),60000);this.cleanupTimer.unref()}this.debug(`Logger initialized; traffic=${this.logFile}; debugMode=${config.debugMode}`)}
 system(message:string):void{this.writeTraffic(`${this.timestamp()}  SYSTEM  ${this.singleLine(message)}`)}
 message(id:number,from:string,to:string,status:TransportStatus,payload?:string):void{const suffix=payload!==undefined?`  ${this.singleLine(payload)}`:'';this.writeTraffic(`${this.timestamp()}  #${id}  ${from.toUpperCase()}→${to.toUpperCase()}  ${status}${suffix}`)}
 debug(message:string,error?:unknown):void{if(this.config.debugMode==='OFF')return;let detail=this.singleLine(message);if(error!==undefined)detail+=` | ${error instanceof Error?(error.stack??error.message):String(error)}`;try{fs.appendFileSync(this.debugFile,`${this.timestamp()}  DEBUG  ${detail}\n`,'utf8')}catch{}}
 dispose():void{if(this.cleanupTimer){clearInterval(this.cleanupTimer);this.cleanupTimer=undefined}}
 private singleLine(value:string):string{return value.replace(/\r\n|\r|\n/g,' ')}
 private writeTraffic(line:string):void{console.log(line);if(!this.config.enabled)return;fs.appendFileSync(this.logFile,`${line}\n`,'utf8')}
 private timestamp():string{const d=new Date();const pad=(n:number,width=2)=>String(n).padStart(width,'0');return `${pad(d.getDate())}.${pad(d.getMonth()+1)}.${d.getFullYear()} ${pad(d.getHours())}:${pad(d.getMinutes())}:${pad(d.getSeconds())}.${pad(d.getMilliseconds(),3)}`}
 private cleanup():void{if(!fs.existsSync(this.logFile))return;const cutoff=Date.now()-this.config.retentionMinutes*60000;const lines=fs.readFileSync(this.logFile,'utf8').split(/\r?\n/);const kept:string[]=[];for(const line of lines){if(!line)continue;const m=line.match(/^(\d{2})\.(\d{2})\.(\d{4}) (\d{2}):(\d{2}):(\d{2})\.(\d{3})/);if(!m)continue;const[,dd,mm,yyyy,hh,min,ss,ms]=m;const ts=new Date(Number(yyyy),Number(mm)-1,Number(dd),Number(hh),Number(min),Number(ss),Number(ms)).getTime();if(ts>=cutoff)kept.push(line)}fs.writeFileSync(this.logFile,kept.length?`${kept.join('\n')}\n`:'','utf8')}
}
