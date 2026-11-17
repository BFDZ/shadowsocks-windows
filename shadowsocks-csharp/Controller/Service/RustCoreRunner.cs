using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using NLog;
using Shadowsocks.Model;
using Shadowsocks.Util.ProcessManagement;

namespace Shadowsocks.Controller.Service
{
    public class RustCoreRunner
    {
        private static readonly Logger logger = LogManager.GetCurrentClassLogger();

        private Process _process;
        private static Job _rustJob;
        private readonly string _corePath;
        private readonly string _configPath;
        private readonly string _statAddr;

        /// <summary>
        /// Cache of the result of <see cref="IsStatAddrSupported"/>, keyed by core path
        /// </summary>
        private static readonly ConcurrentDictionary<string, bool> StatAddrSupportCache =
            new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        public event EventHandler<string> LogReceived;
        public event EventHandler ProcessExited;

        public bool IsRunning => _process != null && !_process.HasExited;

        static RustCoreRunner()
        {
            _rustJob = new Job();
        }

        public RustCoreRunner(string corePath, string configPath)
            : this(corePath, configPath, null)
        {
        }

        /// <param name="statAddr">
        /// TCP address (IP:PORT) of the local flow statistic listener, passed to the core with
        /// <c>--stat-addr</c>. Pass null to disable the report.
        /// </param>
        public RustCoreRunner(string corePath, string configPath, string statAddr)
        {
            _corePath = corePath;
            _configPath = configPath;
            _statAddr = statAddr;
        }

        public void Start()
        {
            if (_process != null && !_process.HasExited)
                return;

            if (!File.Exists(_corePath))
            {
                logger.Error($"sslocal.exe not found at: {_corePath}");
                throw new FileNotFoundException($"sslocal.exe not found at: {_corePath}");
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = _corePath,
                Arguments = string.IsNullOrEmpty(_statAddr)
                    ? $"-c \"{_configPath}\""
                    : $"-c \"{_configPath}\" --stat-addr {_statAddr}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            _process = new Process
            {
                StartInfo = startInfo,
                EnableRaisingEvents = true
            };

            _process.OutputDataReceived += (s, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    logger.Info("[sslocal] " + e.Data);
                    LogReceived?.Invoke(this, e.Data);
                }
            };

            _process.ErrorDataReceived += (s, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    logger.Info("[sslocal] " + e.Data);
                    LogReceived?.Invoke(this, e.Data);
                }
            };

            _process.Exited += (s, e) =>
            {
                logger.Info("sslocal exited.");
                ProcessExited?.Invoke(this, EventArgs.Empty);
            };

            _process.Start();
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();

            try
            {
                _rustJob.AddProcess(_process.Handle);
            }
            catch (Exception ex)
            {
                logger.LogUsefulException(ex);
            }

            logger.Info($"sslocal started - PID: {_process.Id}, config: {_configPath}"
                + (string.IsNullOrEmpty(_statAddr) ? "" : $", stat-addr: {_statAddr}"));
        }

        public void Stop()
        {
            if (_process == null)
                return;

            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill();
                    _process.WaitForExit(3000);
                }
            }
            catch (Exception ex)
            {
                logger.LogUsefulException(ex);
            }
            finally
            {
                _process.Dispose();
                _process = null;
            }
        }

        public void Restart()
        {
            Stop();
            Start();
        }

        public static bool IsPortAvailable(int port)
        {
            try
            {
                TcpListener listener = new TcpListener(IPAddress.Loopback, port);
                listener.Start();
                listener.Stop();
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static int GetFreePort()
        {
            try
            {
                TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                listener.Stop();
                return port;
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>
        /// Check whether the core binary was built with the <c>local-flow-stat</c> feature
        /// (i.e. it understands <c>--stat-addr</c>).
        ///
        /// The Rust core only reports local flow statistics when built with that feature,
        /// and it is disabled in the upstream default build, so the argument must not be
        /// passed blindly - an unknown argument makes clap terminate the process.
        /// </summary>
        public static bool IsStatAddrSupported(string corePath)
        {
            if (string.IsNullOrEmpty(corePath))
                return false;

            try
            {
                if (!File.Exists(corePath))
                    return false;

                // include the last write time, so replacing the core binary invalidates the cache
                string key = corePath + "|" + File.GetLastWriteTimeUtc(corePath).Ticks;
                return StatAddrSupportCache.GetOrAdd(key, _ => ProbeStatAddrSupport(corePath));
            }
            catch (Exception ex)
            {
                logger.LogUsefulException(ex);
                return false;
            }
        }

        private static bool ProbeStatAddrSupport(string corePath)
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = corePath,
                    Arguments = "--help",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                };

                using (var process = new Process { StartInfo = startInfo })
                {
                    var output = new StringBuilder();
                    process.OutputDataReceived += (s, e) =>
                    {
                        if (e.Data != null) output.AppendLine(e.Data);
                    };
                    process.ErrorDataReceived += (s, e) =>
                    {
                        if (e.Data != null) output.AppendLine(e.Data);
                    };
                    process.Start();
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                    if (!process.WaitForExit(5000))
                    {
                        try { process.Kill(); } catch { /* ignore */ }
                        logger.Warn("probing sslocal --help timed out, flow statistic disabled");
                        return false;
                    }
                    // ensure the asynchronous output callbacks are flushed
                    process.WaitForExit();

                    bool supported = output.ToString().Contains("--stat-addr");
                    if (!supported)
                        logger.Warn("sslocal was built without the `local-flow-stat` feature, "
                            + "GUI traffic statistic is not available");
                    return supported;
                }
            }
            catch (Exception ex)
            {
                logger.LogUsefulException(ex);
                return false;
            }
        }
    }
}
