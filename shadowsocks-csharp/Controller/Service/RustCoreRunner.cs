using System;
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

        public event EventHandler<string> LogReceived;
        public event EventHandler ProcessExited;

        public bool IsRunning => _process != null && !_process.HasExited;

        static RustCoreRunner()
        {
            _rustJob = new Job();
        }

        public RustCoreRunner(string corePath, string configPath)
        {
            _corePath = corePath;
            _configPath = configPath;
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
                Arguments = $"-c \"{_configPath}\"",
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

            logger.Info($"sslocal started - PID: {_process.Id}, config: {_configPath}");
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
    }
}
