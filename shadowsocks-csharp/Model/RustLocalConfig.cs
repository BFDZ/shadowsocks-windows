using System.Collections.Generic;
using Newtonsoft.Json;

namespace Shadowsocks.Model
{
    public class RustServerConfig
    {
        [JsonProperty("address")]
        public string Address { get; set; }

        [JsonProperty("port")]
        public int Port { get; set; }

        [JsonProperty("method")]
        public string Method { get; set; }

        [JsonProperty("password")]
        public string Password { get; set; }

        [JsonProperty("plugin", NullValueHandling = NullValueHandling.Ignore)]
        public string Plugin { get; set; }

        [JsonProperty("plugin_opts", NullValueHandling = NullValueHandling.Ignore)]
        public string PluginOpts { get; set; }

        [JsonProperty("plugin_args", NullValueHandling = NullValueHandling.Ignore)]
        public string PluginArgs { get; set; }

        [JsonProperty("timeout", NullValueHandling = NullValueHandling.Ignore)]
        public int? Timeout { get; set; }
    }

    public class RustLocalEndpoint
    {
        [JsonProperty("protocol")]
        public string Protocol { get; set; }

        [JsonProperty("local_address")]
        public string LocalAddress { get; set; }

        [JsonProperty("local_port")]
        public int LocalPort { get; set; }
    }

    public class RustLocalConfig
    {
        [JsonProperty("servers")]
        public List<RustServerConfig> Servers { get; set; }

        [JsonProperty("locals")]
        public List<RustLocalEndpoint> Locals { get; set; }

        [JsonProperty("mode", NullValueHandling = NullValueHandling.Ignore)]
        public string Mode { get; set; }

        public RustLocalConfig()
        {
            Servers = new List<RustServerConfig>();
            Locals = new List<RustLocalEndpoint>();
        }

        public static RustLocalConfig FromServer(Server server, string localAddress, int socksPort, int httpPort)
        {
            var config = new RustLocalConfig();

            var serverConfig = new RustServerConfig
            {
                Address = server.server,
                Port = server.server_port,
                Method = server.method,
                Password = server.password,
            };

            if (!string.IsNullOrWhiteSpace(server.plugin))
            {
                serverConfig.Plugin = server.plugin;
                if (!string.IsNullOrWhiteSpace(server.plugin_opts))
                    serverConfig.PluginOpts = server.plugin_opts;
                if (!string.IsNullOrWhiteSpace(server.plugin_args))
                    serverConfig.PluginArgs = server.plugin_args;
            }

            if (server.timeout > 0)
                serverConfig.Timeout = server.timeout;

            config.Servers.Add(serverConfig);

            config.Locals.Add(new RustLocalEndpoint
            {
                Protocol = "socks",
                LocalAddress = localAddress,
                LocalPort = socksPort
            });

            config.Locals.Add(new RustLocalEndpoint
            {
                Protocol = "http",
                LocalAddress = localAddress,
                LocalPort = httpPort
            });

            config.Mode = "tcp_and_udp";

            return config;
        }
    }
}
