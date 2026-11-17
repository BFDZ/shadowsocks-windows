using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using NLog;
using Shadowsocks.Model;
using Shadowsocks.Util.ProcessManagement;

namespace Shadowsocks.Controller.Service
{
    // https://github.com/shadowsocks/shadowsocks-org/wiki/Plugin
    public sealed class Sip003Plugin : IDisposable
    {
        private static readonly Logger logger = LogManager.GetCurrentClassLogger();

        public IPEndPoint LocalEndPoint { get; private set; }
        public int ProcessId => _started ? _pluginProcess.Id : 0;

        private readonly object _startProcessLock = new object();
        private readonly Job _pluginJob;
        private readonly Process _pluginProcess;
        private bool _started;
        private bool _disposed;

        public static Sip003Plugin CreateIfConfigured(Server server, bool showPluginOutput)
        {
            if (server == null)
            {
                throw new ArgumentNullException(nameof(server));
            }

            if (string.IsNullOrWhiteSpace(server.plugin))
            {
                return null;
            }

            return new Sip003Plugin(
                server.plugin,
                server.plugin_opts,
                server.plugin_args,
                server.server,
                server.server_port,
                showPluginOutput);
        }

        private Sip003Plugin(string plugin, string pluginOpts, string pluginArgs, string serverAddress, int serverPort, bool showPluginOutput)
        {
            if (plugin == null) throw new ArgumentNullException(nameof(plugin));
            if (string.IsNullOrWhiteSpace(serverAddress))
            {
                throw new ArgumentException("Value cannot be null or whitespace.", nameof(serverAddress));
            }
            if (serverPort <= 0 || serverPort > 65535)
            {
                throw new ArgumentOutOfRangeException("serverPort");
            }

            _pluginProcess = new Process
            {
                StartInfo = CreateStartInfo(plugin, pluginArgs, showPluginOutput,
                    Program.WorkingDirectory ?? Environment.CurrentDirectory,
                    serverAddress, serverPort, pluginOpts)
            };

            _pluginJob = new Job();
        }

        /// <summary>
        /// Builds the start info of the plugin process, with the SIP003 environment set.
        /// </summary>
        private static ProcessStartInfo CreateStartInfo(string plugin, string pluginArgs, bool showPluginOutput,
            string workingDirectory, string serverAddress, int serverPort, string pluginOpts)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = plugin,
                Arguments = pluginArgs,
                UseShellExecute = false,
                CreateNoWindow = !showPluginOutput,
                ErrorDialog = false,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = workingDirectory
            };

            try
            {
                SetPluginEnvironment(startInfo, serverAddress, serverPort, pluginOpts);
            }
            catch (ArgumentException)
            {
                // ProcessStartInfo copies the environment block of the current process into a
                // dictionary that compares names case-insensitively. When the block contains the
                // same name twice with different letter cases ("HTTPS_PROXY" and "https_proxy" for
                // example, which bash/msys based launchers do produce), that copy throws and the
                // plugin can never be started. Remove the duplicates and try once more.
                if (!TryRemoveDuplicateEnvironmentVariables())
                    throw;

                SetPluginEnvironment(startInfo, serverAddress, serverPort, pluginOpts);
            }

            return startInfo;
        }

        private static void SetPluginEnvironment(ProcessStartInfo startInfo, string serverAddress, int serverPort, string pluginOpts)
        {
            // Reading Environment here also inherits the environment of the current process,
            // which is what the plugin process should see.
            var environment = startInfo.Environment;
            environment["SS_REMOTE_HOST"] = serverAddress;
            environment["SS_REMOTE_PORT"] = serverPort.ToString();
            environment["SS_PLUGIN_OPTIONS"] = pluginOpts;
        }

        /// <summary>
        /// Removes environment variables whose names only differ in letter case.
        /// </summary>
        /// <returns><c>true</c> if the environment is free of duplicates afterwards.</returns>
        private static bool TryRemoveDuplicateEnvironmentVariables()
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var duplicates = new List<string>();

            foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
            {
                var name = (string)entry.Key;
                if (!names.Add(name))
                {
                    duplicates.Add(name);
                }
            }

            foreach (var name in duplicates)
            {
                // a name is matched case-insensitively, so this drops every entry with that name
                Environment.SetEnvironmentVariable(name, null);
                logger.Warn($"Removed duplicated environment variable \"{name}\", " +
                            "it prevented the SIP003 plugin from being started");
            }

            // verify: a partial cleanup would just make the retry fail again
            names.Clear();
            foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
            {
                if (!names.Add((string)entry.Key))
                {
                    return false;
                }
            }

            return true;
        }

        public bool StartIfNeeded()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(GetType().FullName);
            }

            lock (_startProcessLock)
            {
                if (_started && !_pluginProcess.HasExited)
                {
                    return false;
                }

                var localPort = GetNextFreeTcpPort();
                LocalEndPoint = new IPEndPoint(IPAddress.Loopback, localPort);

                _pluginProcess.StartInfo.Environment["SS_LOCAL_HOST"] = LocalEndPoint.Address.ToString();
                _pluginProcess.StartInfo.Environment["SS_LOCAL_PORT"] = LocalEndPoint.Port.ToString();
                _pluginProcess.StartInfo.Arguments = ExpandEnvironmentVariables(_pluginProcess.StartInfo.Arguments, _pluginProcess.StartInfo.EnvironmentVariables);
                try
                {
                    _pluginProcess.Start();
                }
                catch (System.ComponentModel.Win32Exception ex)
                {
                    // do not use File.Exists(...), it can not handle the scenarios when the plugin file is in system environment path.
                    // https://docs.microsoft.com/en-us/windows/win32/seccrypto/common-hresult-values
                    //if ((uint)ex.ErrorCode == 0x80004005)
                    //  https://docs.microsoft.com/en-us/openspecs/windows_protocols/ms-erref/18d8fbe8-a967-4f1c-ae50-99ca8e491d2d
                    if (ex.NativeErrorCode == 0x00000002)
                    {
                        throw new FileNotFoundException(I18N.GetString("Cannot find the plugin program file"), _pluginProcess.StartInfo.FileName, ex);
                    }
                    throw new ApplicationException(I18N.GetString("Plugin Program"), ex);
                }
                _pluginJob.AddProcess(_pluginProcess.Handle);
                _started = true;
            }

            return true;
        }

        public string ExpandEnvironmentVariables(string name, StringDictionary environmentVariables = null)
        {
            // Expand the environment variables from the new process itself
            if (environmentVariables != null)
            {
                foreach(string key in environmentVariables.Keys)
                {
                    name = name.Replace($"%{key}%", environmentVariables[key]);
                }
            }
            // Also expand the environment variables from current main process (system)
            name = Environment.ExpandEnvironmentVariables(name);
            return name;
        }

        static int GetNextFreeTcpPort()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            int port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                if (!_pluginProcess.HasExited)
                {
                    _pluginProcess.Kill();
                    _pluginProcess.WaitForExit();
                }
            }
            catch (Exception) { }
            finally
            {
                try
                {
                    _pluginProcess.Dispose();
                    _pluginJob.Dispose();
                }
                catch (Exception) { }

                _disposed = true;
            }
        }
    }
}