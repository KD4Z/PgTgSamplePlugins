#nullable enable

using System;
using System.IO;
using System.IO.Ports;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PgTg.Common;
using PgTg.Plugins.Core;

namespace SampleAirMonitor.MyModel.Internal
{
    /// <summary>
    /// Manages serial port connection to the air monitor GPIO controller device.
    /// Handles connect, reconnect, disconnect, and event-driven data receive.
    /// </summary>
    internal class SerialConnection : ISampleAirMonitorConnection
    {
        private const string ModuleName = "SerialConnection";

        private readonly CancellationToken _cancellationToken;
        private readonly object _lock = new();

        private SerialPort? _serialPort;
        private string _portName = string.Empty;
        private int _baudRate = 38400;
        private int _reconnectDelayMs = 5000;
        private bool _isRunning;
        private bool _disposed;
        private readonly StringBuilder _receivedMessage = new();

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
                    return _serialPort?.IsOpen == true;
                }
            }
        }

        public SerialConnection(CancellationToken cancellationToken)
        {
            _cancellationToken = cancellationToken;
        }

        /// <summary>
        /// Configure the serial port settings.
        /// </summary>
        public void Configure(string portName, int baudRate = 38400, int reconnectDelayMs = 5000)
        {
            _portName = portName;
            _baudRate = baudRate;
            _reconnectDelayMs = reconnectDelayMs;
        }

        /// <summary>
        /// Start the connection loop.
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
        /// </summary>
        /// <param name="data">The command string to send.</param>
        /// <returns>True if sent successfully.</returns>
        public bool Send(string data)
        {
            if (!IsConnected || _serialPort == null)
            {
                return false;
            }

            try
            {
                if (!data.StartsWith("$"))
                    data = "$" + data;

                _serialPort.Write(data);
                return true;
            }
            catch (InvalidOperationException ex)
            {
                Logger.LogError(ModuleName, $"Send Error (port not open): {ex.Message}");
                return false;
            }
            catch (TimeoutException ex)
            {
                Logger.LogError(ModuleName, $"Send Timeout: {ex.Message}");
                return false;
            }
            catch (Exception ex)
            {
                Logger.LogVerbose(ModuleName, $"Error sending message to device: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Send raw bytes to the device (used for CI-V binary protocol).
        /// </summary>
        public bool Send(byte[] data)
        {
            if (!IsConnected || _serialPort == null)
            {
                return false;
            }

            try
            {
                _serialPort.Write(data, 0, data.Length);
                return true;
            }
            catch (InvalidOperationException ex)
            {
                Logger.LogError(ModuleName, $"Send Error (port not open): {ex.Message}");
                return false;
            }
            catch (TimeoutException ex)
            {
                Logger.LogError(ModuleName, $"Send Timeout: {ex.Message}");
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
                        _serialPort = new SerialPort(_portName, _baudRate, Parity.None, 8, StopBits.One)
                        {
                            ReadTimeout = 500,
                            WriteTimeout = 500,
                            Handshake = Handshake.None,
                            DtrEnable = true,
                            RtsEnable = true
                        };
                    }

                    Logger.LogInfo(ModuleName, $"Attempting to open serial port {_portName} at {_baudRate} baud");
                    _serialPort.Open();

                    if (_serialPort.IsOpen)
                    {
                        SetConnectionState(PluginConnectionState.Connected);
                        Logger.LogInfo(ModuleName, $"Successfully opened {_portName}");

                        // Wire up data received event
                        _serialPort.DataReceived += OnSerialDataReceived;

                        // Wait while connected
                        while (_isRunning && !_cancellationToken.IsCancellationRequested && _serialPort?.IsOpen == true)
                        {
                            await Task.Delay(100, _cancellationToken);
                        }

                        // Unwire event before cleanup
                        if (_serialPort != null)
                            _serialPort.DataReceived -= OnSerialDataReceived;
                    }
                    else
                    {
                        Logger.LogError(ModuleName, $"Failed to open {_portName}");
                        SetConnectionState(PluginConnectionState.Disconnected);
                        CleanupConnection();
                    }
                }
                catch (ObjectDisposedException) { break; }
                catch (OperationCanceledException) { break; }
                catch (UnauthorizedAccessException ex)
                {
                    Logger.LogError(ModuleName, $"Port {_portName} access denied: {ex.Message}");
                    SetConnectionState(PluginConnectionState.Reconnecting);
                    CleanupConnection();
                }
                catch (IOException ex)
                {
                    Logger.LogVerbose(ModuleName, $"Unable to open serial port: {ex.Message}");
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
                    await Task.Delay(_reconnectDelayMs, _cancellationToken);
                }
            }
        }

        private void OnSerialDataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            if (_serialPort == null || !_serialPort.IsOpen) return;

            try
            {
                string chunk = _serialPort.ReadExisting();
                if (string.IsNullOrEmpty(chunk)) return;

                _receivedMessage.Append(chunk);

                string fullMessage = _receivedMessage.ToString();
                int semicolonIndex = fullMessage.LastIndexOf(';');

                while (semicolonIndex >= 0)
                {
                    string completeLine = fullMessage.Substring(0, semicolonIndex + 1);
                    DataReceived?.Invoke(completeLine.Trim());

                    _receivedMessage.Remove(0, semicolonIndex + 1);
                    fullMessage = _receivedMessage.ToString();
                    semicolonIndex = fullMessage.LastIndexOf(';');
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ModuleName, $"Error reading serial data: {ex.Message}");
            }
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
                try
                {
                    if (_serialPort != null)
                    {
                        _serialPort.DataReceived -= OnSerialDataReceived;
                        if (_serialPort.IsOpen)
                            _serialPort.Close();
                        _serialPort.Dispose();
                    }
                }
                catch { }
                _serialPort = null;
            }
            _receivedMessage.Clear();
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

            // Break event chains to prevent callbacks on disposed objects
            DataReceived = null;
            ConnectionStateChanged = null;
        }
    }
}
