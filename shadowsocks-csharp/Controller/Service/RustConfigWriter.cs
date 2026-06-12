using System.IO;
using Newtonsoft.Json;
using NLog;
using Shadowsocks.Model;

namespace Shadowsocks.Controller.Service
{
    public static class RustConfigWriter
    {
        private static readonly Logger logger = LogManager.GetCurrentClassLogger();

        public const int DefaultSocksPort = 1080;
        public const int DefaultHttpPort = 1081;

        public static string WriteConfig(Server server, string configPath, string localAddress, int socksPort, int httpPort)
        {
            var rustConfig = RustLocalConfig.FromServer(server, localAddress, socksPort, httpPort);
            var json = JsonConvert.SerializeObject(rustConfig, Formatting.Indented);

            var dir = Path.GetDirectoryName(configPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            File.WriteAllText(configPath, json);
            logger.Info($"Rust core config written to {configPath}");
            return configPath;
        }

        public static string GetConfigPath()
        {
            string appData = System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData);
            string dir = Path.Combine(appData, "shadowsocks-windows-rust");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "config.json");
        }

        public static string GetCorePath()
        {
            string baseDir = System.AppDomain.CurrentDomain.BaseDirectory;
            return Path.Combine(baseDir, "core", "sslocal.exe");
        }
    }
}
