using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using NLog;

namespace Shadowsocks.Controller.Service
{
    public class FlowStatEventArgs : EventArgs
    {
        /// <summary>
        /// Bytes sent to the remote server (upload), cumulative
        /// </summary>
        public long Tx { get; }

        /// <summary>
        /// Bytes received from the remote server (download), cumulative
        /// </summary>
        public long Rx { get; }

        public FlowStatEventArgs(long tx, long rx)
        {
            Tx = tx;
            Rx = rx;
        }
    }

    /// <summary>
    /// Receives local flow statistic reports from the rust core (sslocal).
    ///
    /// With <c>--stat-addr 127.0.0.1:PORT</c>, sslocal opens a TCP connection every
    /// 500 milliseconds and writes 16 bytes of payload:
    /// <c>[0..8)  tx</c> (sent to remote, upload) and <c>[8..16) rx</c> (received from remote,
    /// download), both little endian u64 cumulative counters. The connection is closed
    /// right after the write.
    /// </summary>
    public class LocalStatListener : IDisposable
    {
        private static readonly Logger logger = LogManager.GetCurrentClassLogger();

        private const int PayloadSize = 16; // 2 * u64
        private const int ReceiveTimeoutMs = 3000;

        private TcpListener _listener;
        private Thread _acceptThread;
        private volatile bool _running;

        /// <summary>
        /// The port which sslocal should be told to report to, 0 if not started
        /// </summary>
        public int Port { get; private set; }

        public event EventHandler<FlowStatEventArgs> FlowStatReceived;

        /// <summary>
        /// Start listening on the loopback interface
        /// </summary>
        /// <param name="port">port to listen on, 0 for an ephemeral free port</param>
        /// <returns>the actual port bound</returns>
        public int Start(int port = 0)
        {
            Stop();

            var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();

            _listener = listener;
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            _running = true;

            _acceptThread = new Thread(AcceptLoop)
            {
                IsBackground = true,
                Name = "LocalStatListener"
            };
            _acceptThread.Start();

            return Port;
        }

        public void Stop()
        {
            _running = false;

            var listener = _listener;
            _listener = null;
            if (listener != null)
            {
                try
                {
                    listener.Stop();
                }
                catch (Exception ex)
                {
                    logger.Debug(ex, "stop local stat listener failed");
                }
            }

            var thread = _acceptThread;
            _acceptThread = null;
            if (thread != null && thread != Thread.CurrentThread)
            {
                try
                {
                    // AcceptTcpClient returns immediately after the listener is closed
                    thread.Join(1000);
                }
                catch (Exception ex)
                {
                    logger.Debug(ex, "wait for local stat listener thread failed");
                }
            }

            Port = 0;
        }

        public void Dispose()
        {
            Stop();
        }

        private void AcceptLoop()
        {
            while (_running)
            {
                TcpClient client;
                try
                {
                    var listener = _listener;
                    if (listener == null)
                        break;
                    client = listener.AcceptTcpClient();
                }
                catch (Exception ex)
                {
                    if (_running)
                        logger.Debug(ex, "accept local stat connection failed");
                    break;
                }

                // Handle each report on the thread pool, so a stuck client can not block
                // the accept loop and stop all subsequent statistics.
                ThreadPool.QueueUserWorkItem(_ => HandleClient(client));
            }
        }

        private void HandleClient(TcpClient client)
        {
            try
            {
                using (client)
                {
                    client.ReceiveTimeout = ReceiveTimeoutMs;

                    var buffer = new byte[PayloadSize];
                    int offset = 0;
                    while (offset < PayloadSize)
                    {
                        int n = client.GetStream().Read(buffer, offset, PayloadSize - offset);
                        if (n <= 0)
                            break;
                        offset += n;
                    }

                    if (offset != PayloadSize)
                    {
                        logger.Debug($"incomplete flow statistic payload: {offset}/{PayloadSize}");
                        return;
                    }

                    // Little endian u64 counters
                    long tx = BitConverter.ToInt64(buffer, 0);
                    long rx = BitConverter.ToInt64(buffer, 8);

                    FlowStatReceived?.Invoke(this, new FlowStatEventArgs(tx, rx));
                }
            }
            catch (Exception ex)
            {
                if (_running)
                    logger.Debug(ex, "read local flow statistic failed");
            }
        }
    }
}
