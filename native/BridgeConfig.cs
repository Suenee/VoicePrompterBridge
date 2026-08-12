using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace VPBridgeTray
{
    internal sealed class BridgeConfig
    {
        public ServerConfig server { get; set; }
        public SecurityConfig security { get; set; }
        public HeartbeatConfig heartbeat { get; set; }
        public QueueConfig queue { get; set; }
        public LoggingConfig logging { get; set; }

        public static BridgeConfig Load(string file)
        {
            BridgeConfig cfg = null;
            try
            {
                if (File.Exists(file))
                {
                    JavaScriptSerializer js = new JavaScriptSerializer();
                    cfg = js.Deserialize<BridgeConfig>(File.ReadAllText(file, Encoding.UTF8));
                }
            }
            catch { }

            if (cfg == null) cfg = new BridgeConfig();
            if (cfg.server == null) cfg.server = new ServerConfig();
            if (cfg.security == null) cfg.security = new SecurityConfig();
            if (cfg.heartbeat == null) cfg.heartbeat = new HeartbeatConfig();
            if (cfg.queue == null) cfg.queue = new QueueConfig();
            if (cfg.logging == null) cfg.logging = new LoggingConfig();

            if (cfg.server.mode != "all") cfg.server.mode = "local";
            cfg.server.host = cfg.server.mode == "all" ? "0.0.0.0" : "127.0.0.1";
            if (cfg.server.port < 1 || cfg.server.port > 65535) cfg.server.port = 8170;
            if (String.IsNullOrWhiteSpace(cfg.server.vpPath)) cfg.server.vpPath = "/vp";
            if (String.IsNullOrWhiteSpace(cfg.server.bcPath)) cfg.server.bcPath = "/bc";
            if (cfg.heartbeat.intervalMs < 5000 || cfg.heartbeat.intervalMs > 3600000) cfg.heartbeat.intervalMs = 30000;
            if (cfg.queue.maxMessages < 1) cfg.queue.maxMessages = 1000;
            if (cfg.queue.offlineBufferSize < 0) cfg.queue.offlineBufferSize = 0;
            if (cfg.queue.offlineBufferMaxAgeMs < 0) cfg.queue.offlineBufferMaxAgeMs = 1000;
            if (String.IsNullOrWhiteSpace(cfg.logging.directory)) cfg.logging.directory = "./logs";
            if (cfg.logging.retentionMinutes < 1) cfg.logging.retentionMinutes = 60;
            return cfg;
        }

        public void Save(string file)
        {
            server.host = server.mode == "all" ? "0.0.0.0" : "127.0.0.1";
            string dir = Path.GetDirectoryName(file);
            if (!String.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            string json = "{\r\n" +
                "  \"server\": {\r\n" +
                "    \"mode\": \"" + JsonEscape(server.mode) + "\",\r\n" +
                "    \"host\": \"" + JsonEscape(server.host) + "\",\r\n" +
                "    \"port\": " + server.port + ",\r\n" +
                "    \"vpPath\": \"" + JsonEscape(server.vpPath) + "\",\r\n" +
                "    \"bcPath\": \"" + JsonEscape(server.bcPath) + "\"\r\n" +
                "  },\r\n" +
                "  \"security\": {\r\n" +
                "    \"apiKey\": \"" + JsonEscape(security.apiKey ?? "") + "\"\r\n" +
                "  },\r\n" +
                "  \"heartbeat\": {\r\n" +
                "    \"intervalMs\": " + heartbeat.intervalMs + "\r\n" +
                "  },\r\n" +
                "  \"queue\": {\r\n" +
                "    \"maxMessages\": " + queue.maxMessages + ",\r\n" +
                "    \"offlineBufferSize\": " + queue.offlineBufferSize + ",\r\n" +
                "    \"offlineBufferMaxAgeMs\": " + queue.offlineBufferMaxAgeMs + "\r\n" +
                "  },\r\n" +
                "  \"logging\": {\r\n" +
                "    \"enabled\": " + (logging.enabled ? "true" : "false") + ",\r\n" +
                "    \"directory\": \"" + JsonEscape(logging.directory) + "\",\r\n" +
                "    \"retentionMinutes\": " + logging.retentionMinutes + "\r\n" +
                "  }\r\n" +
                "}\r\n";

            File.WriteAllText(file, json, new UTF8Encoding(false));
        }

        public static string GenerateApiKey()
        {
            byte[] bytes = new byte[32];
            using (RandomNumberGenerator rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            StringBuilder sb = new StringBuilder(64);
            foreach (byte b in bytes) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        public static bool IsValidApiKey(string key)
        {
            if (String.IsNullOrEmpty(key) || key.Length != 64) return false;
            for (int i = 0; i < key.Length; i++)
            {
                char c = key[i];
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'))) return false;
            }
            return true;
        }

        private static string JsonEscape(string value)
        {
            return (value ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n");
        }
    }

    internal sealed class ServerConfig { public string mode { get; set; } public string host { get; set; } public int port { get; set; } public string vpPath { get; set; } public string bcPath { get; set; } }
    internal sealed class SecurityConfig { public string apiKey { get; set; } }
    internal sealed class HeartbeatConfig { public int intervalMs { get; set; } }
    internal sealed class QueueConfig { public int maxMessages { get; set; } public int offlineBufferSize { get; set; } public int offlineBufferMaxAgeMs { get; set; } }
    internal sealed class LoggingConfig { public bool enabled { get; set; } public string directory { get; set; } public int retentionMinutes { get; set; } }
}
