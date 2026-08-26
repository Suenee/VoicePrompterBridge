using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VPBridgeTray
{
 internal sealed class BridgeConfig
 {
  public ServerConfig server {get;set;}=new(); public SecurityConfig security {get;set;}=new(); public HeartbeatConfig heartbeat {get;set;}=new(); public QueueConfig queue {get;set;}=new(); public LoggingConfig logging {get;set;}=new();
  public static BridgeConfig Load(string file){BridgeConfig? cfg=null;try{if(File.Exists(file))cfg=JsonSerializer.Deserialize<BridgeConfig>(File.ReadAllText(file,Encoding.UTF8),new JsonSerializerOptions{PropertyNameCaseInsensitive=true});}catch{}cfg??=new();cfg.server??=new();cfg.security??=new();cfg.heartbeat??=new();cfg.queue??=new();cfg.logging??=new();if(cfg.server.mode!="all")cfg.server.mode="local";cfg.server.host=cfg.server.mode=="all"?"0.0.0.0":"127.0.0.1";if(cfg.server.port<1||cfg.server.port>65535)cfg.server.port=8170;if(String.IsNullOrWhiteSpace(cfg.server.vpPath))cfg.server.vpPath="/vp";if(String.IsNullOrWhiteSpace(cfg.server.bcPath))cfg.server.bcPath="/bc";if(cfg.heartbeat.intervalMs<5000||cfg.heartbeat.intervalMs>3600000)cfg.heartbeat.intervalMs=30000;if(cfg.queue.maxMessages<1)cfg.queue.maxMessages=1000;if(cfg.queue.offlineBufferSize<0)cfg.queue.offlineBufferSize=0;if(cfg.queue.offlineBufferMaxAgeMs<0)cfg.queue.offlineBufferMaxAgeMs=1000;if(String.IsNullOrWhiteSpace(cfg.logging.directory))cfg.logging.directory="./logs";if(cfg.logging.retentionMinutes<1)cfg.logging.retentionMinutes=60;if(cfg.logging.debugMode!="SINGLE"&&cfg.logging.debugMode!="ALL")cfg.logging.debugMode="OFF";return cfg;}
  public void Save(string file){server.host=server.mode=="all"?"0.0.0.0":"127.0.0.1";var dir=Path.GetDirectoryName(file);if(!String.IsNullOrEmpty(dir))Directory.CreateDirectory(dir);File.WriteAllText(file,JsonSerializer.Serialize(this,new JsonSerializerOptions{WriteIndented=true}),new UTF8Encoding(false));}
  public static string GenerateApiKey(){return Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();}
  public static bool IsValidApiKey(string key){return !String.IsNullOrEmpty(key)&&key.Length==64&&key.All(Uri.IsHexDigit);}
 }
 internal sealed class ServerConfig {public string mode{get;set;}="local";public string host{get;set;}="127.0.0.1";public int port{get;set;}=8170;public string vpPath{get;set;}="/vp";public string bcPath{get;set;}="/bc";}
 internal sealed class SecurityConfig {public string apiKey{get;set;}="";}
 internal sealed class HeartbeatConfig {public int intervalMs{get;set;}=30000;}
 internal sealed class QueueConfig {public int maxMessages{get;set;}=1000;public int offlineBufferSize{get;set;}=0;public int offlineBufferMaxAgeMs{get;set;}=1000;}
 internal sealed class LoggingConfig {public bool enabled{get;set;}=true;public string directory{get;set;}="./logs";public int retentionMinutes{get;set;}=60;public string debugMode{get;set;}="OFF";}
}
