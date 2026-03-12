#nullable enable

using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PgTg.Common;
using PgTg.Plugins.Core;

namespace SampleTuner.MyModel.Internal
{
    /// <summary>
    /// Manages TCP connection to the sample tuner device.
    /// Handles connect, reconnect, disconnect, and async data receive.
    /// </summary>
    internal class TcpConnection : ISampleTunerConnection
    {
        private const string ModuleName = "TcpConnection";

        private readonly CancellationToken _cancellationToken;
        private readonly object _lock = new();

        private TcpClient? _tcpClient;
        private NetworkStream? _networkStream;
        private string _ipAddress = string.Empty;
        private int _port;
        private bool _isRunning;
        private bool _disposed;

        /// <summary>
        /// Raised when data is received from the device.
        /// </summary>
        public event Action<string>? DataReceived;

        /// <summary>
        /// Raised when connection state changes.
        /// </summary>
        public event Action<PluginConnectionState>? ConnectionStateChanged;

        /// <summary>
        /// Current connection state.
        /// </summary>
        public PluginConnectionState ConnectionState { get; private set; } = PluginConnectionState.Disconnected;

        /// <summary>
        /// Whether the connection is currently established.
        /// </summary>
        public bool IsConnected
        {
            get
            {
                lock (_lock)
                {
                    return _tcpClient?.Connected == true && _networkStream != null;
                }
            }
        }

        public TcpConnection(CancellationToken cancellationToken)
        {
            _cancellationToken = cancellationToken;
        }

        /// <summary>
        /// Configure the TCP connection settings.
        /// </summary>
        public void Configure(string ipAddress, int port)
        {
            _ipAddress = ipAddress;
            _port = port;
        }

        /// <summary>
        /// Start the connection loop. Will continuously attempt to connect and reconnect.
        /// </summary>
        public Task StartAsync()
        {
            if (_isRunning) return Task.CompletedTask;

            _isRunning = true;
            _ = ConnectAndListenAsync();
            return Task.CompletedTask;
        }

        /// <summary>
        /// Stop the connection and cleanup.
        /// </summary>
        public void Stop()
        {
            _isRunning = false;
            Disconnect();
        }

        /// <summary>
        /// Send data to the device.
        /// Commands are sent with $ prefix per the sample tuner protocol.
        /// </summary>
        /// <param name="data">The command string to send.</param>
        /// <returns>True if sent successfully.</returns>
        public bool Send(string data)
        {
            if (!IsConnected || _networkStream == null)
            {
                return false;
            }

            try
            {
                if (!data.StartsWith("$"))
                    data = "$" + data;

                byte[] bytes = Encoding.ASCII.GetBytes(data);
                _networkStream.WriteAsync(bytes, 0, bytes.Length);
                return true;
            }
            catch (IOException ex)
            {
                if (ex.InnerException is SocketException socketEx)
                {
                    Logger.LogError(ModuleName, $"Send Socket Error: {socketEx.Message}");
                }
                else
                {
                    Logger.LogError(ModuleName, $"Send IO Error: {ex.Message}");
                }
                return false;
            }
            catch (ObjectDisposedException)
            {
                Logger.LogError(ModuleName, "Cannot send data: NetworkStream has been disposed.");
                return false;
            }
            catch (Exception ex)
            {
                Logger.LogVerbose(ModuleName, $"Error sending message to device: {ex.Message}");
                return false;
            }
        }

        private async Task ConnectAndListenAsync()
        {
            while (_isRunning && !_cancellationToken.IsCancellationRequested)
            {
                try
                {
                    SetConnectionState(PluginConnectionState.Connecting);

                    lock (_lock)
                    {
                        _tcpClient = new TcpClient();
                    }

                    Logger.LogInfo(ModuleName, $"Attempting to connect to device on {_ipAddress}:{_port}");
                    await _tcpClient.ConnectAsync(_ipAddress, _port, _cancellationToken);

                    if (_tcpClient.Connected)
                    {
                        SetConnectionState(PluginConnectionState.Connected);
                        Logger.LogInfo(ModuleName, $"Successfully connected to {_ipAddress}:{_port}");
                        _networkStream = _tcpClient.GetStream();

                        // Start receiving data
                        await ReceiveDataAsync();
                    }
                    else
                    {
                        Logger.LogError(ModuleName, $"Failed to connect to {_ipAddress}:{_port}");
                        SetConnectionState(PluginConnectionState.Disconnected);
                        CleanupConnection();
                    }
                }
                catch (ObjectDisposedException) { break; }
                catch (OperationCanceledException) { break; }
                catch (SocketException)
                {
                    Logger.LogVerbose(ModuleName, "Unable to establish connection to device.");
                    SetConnectionState(PluginConnectionState.Reconnecting);
                    CleanupConnection();
                }
                catch (Exception ex)
                {
                    Logger.LogError(ModuleName, $"Connection error: {ex.Message}");
                    SetConnectionState(PluginConnectionState.Reconnecting);
                    CleanupConnection();
                }

                if (_isRunning && !_cancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(50, _cancellationToken);
                }
            }
        }

        private async Task ReceiveDataAsync()
        {
            byte[] buffer = new byte[2048];
            StringBuilder receivedMessage = new();

            while (!_cancellationToken.IsCancellationRequested && _networkStream != null && _isRunning)
            {
                try
                {
                    int bytesRead = await _networkStream.ReadAsync(buffer, 0, buffer.Length, _cancellationToken);

                    if (bytesRead > 0)
                    {
                        string chunk = Encoding.ASCII.GetString(buffer, 0, bytesRead);
                        receivedMessage.Append(chunk);

                        string fullMessage = receivedMessage.ToString();
                        int semicolonIndex = fullMessage.LastIndexOf(';');

                        while (semicolonIndex >= 0)
                        {
                            string completeLine = fullMessage.Substring(0, semicolonIndex + 1);
                            DataReceived?.Invoke(completeLine.Trim());

                            receivedMessage.Remove(0, semicolonIndex + 1);
                            fullMessage = receivedMessage.ToString();
                            semicolonIndex = fullMessage.LastIndexOf(';');
                        }
                    }
                    else
                    {
                        // Connection closed by remote host
                        Logger.LogInfo(ModuleName, "Connection closed by remote host.");
                        break;
                    }
                }
                catch (IOException)
                {
                    Disconnect();
                    break;
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (SocketException ex)
                {
                    Logger.LogError(ModuleName, $"Receive Socket Error: {ex.Message}");
                    Disconnect();
                    break;
                }
                catch (Exception ex)
                {
                    Logger.LogError(ModuleName, $"Receive Error: {ex.Message}");
                    Disconnect();
                    break;
                }
            }

            Disconnect();
        }

        private void Disconnect()
        {
            SetConnectionState(PluginConnectionState.Disconnected);
            CleanupConnection();
        }

        private void CleanupConnection()
        {
            lock (_lock)
            {
                try { _networkStream?.Dispose(); } catch { }
                try { _tcpClient?.Close(); } catch { }
                try { _tcpClient?.Dispose(); } catch { }
                _networkStream = null;
                _tcpClient = null;
            }
        }

        private void SetConnectionState(PluginConnectionState state)
        {
            if (ConnectionState != state)
            {
                ConnectionState = state;
                ConnectionStateChanged?.Invoke(state);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _isRunning = false;
            CleanupConnection();

            // Clear event delegates to break reference cycles and prevent memory leaks
            DataReceived = null;
            ConnectionStateChanged = null;
        }
    }
}
