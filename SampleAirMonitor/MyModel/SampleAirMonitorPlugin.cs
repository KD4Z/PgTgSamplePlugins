#nullable enable

using PgTg.Common;
using PgTg.Plugins;
using PgTg.Plugins.Core;
using SampleAirMonitor.MyModel.Internal;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Timer = System.Timers.Timer;

namespace SampleAirMonitor.MyModel
{
    /// <summary>
    /// Sample GPIO output plugin demonstrating IGpioOutputPlugin implementation.
    /// Uses the MyModel/Internal architecture pattern with separated concerns:
    /// IConnection (TCP or Serial), CommandQueue, ResponseParser, StatusTracker, Constants.
    ///
    /// Unlike amplifier/tuner plugins, this GPIO plugin has no polling — it only
    /// sends tx frequency/mode to a remote transceiver/device when SetFrequencyHz/SetTransmitMode are called by the Bridge.
    /// </summary>
    [PluginInfo("sample.airmonitor", "Air Monitor",
        Version = "1.0.0",
        Manufacturer = "PgTg",
        Capability = PluginCapability.FrequencyModeMonitoring,
        Description = "Sample plugin that receives frequency and mode updates from the PgTgBridge and forwards them to a transceiver using CAT or CI-V protocol",
        // UiSections declares which control groups PluginManagerForm will display
        // for this plugin when it is selected. Combine flags to enable multiple sections.
        //
        // Available sections:
        //   PluginUiSection.Tcp          - TCP radio button, IP address, port number
        //   PluginUiSection.Serial       - Serial radio button, COM port dropdown
        //   PluginUiSection.Reconnect    - Reconnect delay entry (shown with Tcp or Serial)
        //   PluginUiSection.Wol          - Wake-on-LAN checkbox, MAC address, Test button
        //   PluginUiSection.TcpMultiplex - TCP Multiplex Server enable + listen port
        //   PluginUiSection.GpioAction   - GPIO output action mapping grid
        //   PluginUiSection.Protocol     - CAT / CI-V frequency mode protocol selector
        //
        // Example — TCP + Serial + reconnect (most amplifier/tuner plugins):
        //   UiSections = PluginUiSection.Tcp | PluginUiSection.Serial | PluginUiSection.Reconnect
        //
        // Example — GPIO with action mapping:
        //   UiSections = PluginUiSection.Tcp | PluginUiSection.Serial | PluginUiSection.Reconnect | PluginUiSection.GpioAction
        UiSections = PluginUiSection.Tcp | PluginUiSection.Serial | PluginUiSection.Reconnect | PluginUiSection.Protocol)]
    public class SampleAirMonitorPlugin : IGpioOutputPlugin
    {
        public const string PluginId = "sample.airmonitor";
        private const string ModuleName = "AirMonitor";

        private readonly CancellationToken _cancellationToken;

        // Internal components
        private ISampleAirMonitorConnection? _connection;
        private CommandQueue? _commandQueue;
        private ResponseParser? _parser;
        private StatusTracker? _statusTracker;
        private SampleAirMonitorConfiguration? _config;

        private bool _stopped;
        private bool _disposed;
        private bool _clientsConnected;
        private Timer? _resendTimer;

        private const int ResendIntervalMs = 15_000;

        /// <summary>
        /// True when the Bridge has reported one or more SDR clients connected.
        /// Drives the periodic frequency/mode resend timer.
        /// </summary>
        public bool ClientsConnected => _clientsConnected;

        #region IDevicePlugin

        public PluginInfo Info { get; } = new PluginInfo
        {
            Id = PluginId,
            Name = "Air Monitor",
            Version = "1.0.0",
            Manufacturer = "KD4Z",
            Capability = PluginCapability.Gpio,
            Description = "Sample GPIO output plugin for third-party development reference",
            ConfigurationType = typeof(SampleAirMonitorConfiguration),
            UiSections = PluginUiSection.Tcp | PluginUiSection.Serial | PluginUiSection.Reconnect | PluginUiSection.Protocol
        };

        public PluginConnectionState ConnectionState => _connection?.ConnectionState ?? PluginConnectionState.Disconnected;

        public double MeterDisplayMaxPower => Constants.MeterDisplayMaxPower;

        public event EventHandler<PluginConnectionStateChangedEventArgs>? ConnectionStateChanged;
        public event EventHandler<MeterDataEventArgs>? MeterDataAvailable;
        /// <summary>
        /// Raise when data has changed.
        /// Bridge subscribes to push /device WebSocket updates on change instead of polling.
        /// </summary>
        public event EventHandler? DeviceDataChanged;

        #endregion

        public SampleAirMonitorPlugin(CancellationToken cancellationToken)
        {
            _cancellationToken = cancellationToken;
        }

        #region Lifecycle

        public Task InitializeAsync(IPluginConfiguration configuration, CancellationToken cancellationToken)
        {
            _config = configuration as SampleAirMonitorConfiguration ?? new SampleAirMonitorConfiguration
            {
                IpAddress = configuration.IpAddress,
                Port = configuration.Port,
                Enabled = configuration.Enabled,
                ReconnectDelayMs = configuration.ReconnectDelayMs,
                ConnectionType = configuration.ConnectionType,
                SerialPort = configuration.SerialPort,
                BaudRate = configuration.BaudRate
            };

            // Create connection based on connection type
            if (_config.ConnectionType == PluginConnectionType.Serial)
            {
                var serialConnection = new SerialConnection(_cancellationToken);
                serialConnection.Configure(_config.SerialPort, _config.BaudRate, _config.ReconnectDelayMs);
                _connection = serialConnection;
                Logger.LogInfo(ModuleName, $"Using serial connection: {_config.SerialPort} at {_config.BaudRate} baud");
            }
            else
            {
                var tcpConnection = new TcpConnection(_cancellationToken);
                tcpConnection.Configure(_config.IpAddress, _config.Port, _config.ReconnectDelayMs);
                _connection = tcpConnection;
                Logger.LogInfo(ModuleName, $"Using TCP connection: {_config.IpAddress}:{_config.Port}");
            }

            string protocolName = _config.PluginFreqModeProtocol == Constants.ProtocolCiv ? "CI-V" : "CAT";
            Logger.LogInfo(ModuleName, $"Frequency/mode protocol: {protocolName}, reconnect delay: {_config.ReconnectDelayMs}ms");

            // Create other components
            _commandQueue = new CommandQueue(_connection);
            _parser = new ResponseParser();
            _statusTracker = new StatusTracker();

            // Wire up events
            _connection.DataReceived += OnDataReceived;
            _connection.ConnectionStateChanged += OnConnectionStateChanged;

            return Task.CompletedTask;
        }

        public async Task StartAsync()
        {
            if (_connection == null || _commandQueue == null || _config == null)
                throw new InvalidOperationException("Plugin not initialized");

            if (_cancellationToken.IsCancellationRequested)
            {
                Logger.LogInfo(ModuleName, "Plugin startup cancelled before start");
                return;
            }

            await _connection.StartAsync();

            if (_cancellationToken.IsCancellationRequested)
            {
                _connection.Stop();
                Logger.LogInfo(ModuleName, "Plugin startup cancelled after connection start");
                return;
            }

            _commandQueue.Start();

            if (!_cancellationToken.IsCancellationRequested)
            {
                Logger.LogInfo(ModuleName, "Plugin started");
            }
        }

        public Task StopAsync()
        {
            if (_stopped) return Task.CompletedTask;

            Logger.LogInfo(ModuleName, "Stopping plugin");

            // Stop resend timer (CLAUDE.md teardown order: -= handler, Stop, Dispose, null)
            StopResendTimer();

            // Stop command queue
            _commandQueue?.Stop();

            // Unwire connection events before stopping (CLAUDE.md teardown order)
            if (_connection != null)
            {
                _connection.DataReceived -= OnDataReceived;
                _connection.ConnectionStateChanged -= OnConnectionStateChanged;
                _connection.Stop();
            }

            _stopped = true;
            Logger.LogInfo(ModuleName, "Plugin stopped");

            return Task.CompletedTask;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // Ensure stopped first to unwire events
            if (!_stopped)
            {
                try
                {
                    StopAsync().Wait(TimeSpan.FromSeconds(5));
                }
                catch (Exception ex)
                {
                    Logger.LogError(ModuleName, $"Error in StopAsync during Dispose: {ex.Message}");
                }
            }

            // Ensure resend timer is torn down (guards against Dispose without prior Stop)
            StopResendTimer();

            _commandQueue?.Dispose();

            if (_connection != null)
            {
                _connection.DataReceived -= OnDataReceived;
                _connection.ConnectionStateChanged -= OnConnectionStateChanged;
                _connection.Stop();
                _connection.Dispose();
            }
        }

        #endregion

        #region IGpioOutputPlugin Methods

        /// <summary>
        /// Called by the Bridge when the radio PTT state changes.
        /// Sends the appropriate GPIO command to the remote device.
        /// </summary>
        public void SetAmpPtt(bool ptt)
        {
            // Not implemented in this sample.
        }

        /// <summary>
        /// Called by the Bridge when the amplifier operate/standby mode changes.
        /// Sends the appropriate GPIO command to the remote device.
        /// </summary>
        public void SetAmpOperateMode(bool operate)
        {
            // Not implemented in this sample.
        }

        /// <summary>
        /// Called by the Bridge when the tuner inline/bypass state changes.
        /// Sends the appropriate GPIO command to the remote device.
        /// </summary>
        public void SetTunerInline(bool inline)
        {
            // Not implemented in this sample.
        }

        /// <summary>
        /// Called by the Bridge when a tune cycle starts or stops.
        /// Sends the appropriate GPIO command to the remote device.
        /// </summary>
        public void SetTunerTune(bool tuning)
        {
            // Not implemented in this sample.
        }

        public void SetFrequencyHz(int frequencyHz)
        {
            if (_statusTracker == null || _commandQueue == null || _config == null) return;

            // Only send if frequency actually changed
            if (!_statusTracker.SetFrequencyHz(frequencyHz)) return;

            SendFrequencyCommand(frequencyHz);
            //Logger.LogVerbose(ModuleName, $"SetFrequencyHz({frequencyHz})");
        }

        public void SetTransmitMode(string mode)
        {
            if (_statusTracker == null || _commandQueue == null || _config == null) return;

            // Only send if mode actually changed
            if (!_statusTracker.SetTransmitMode(mode)) return;

            SendModeCommand(mode);
        }

        /// <summary>
        /// Called by the Bridge when the SDR connected-clients count transitions between zero and non-zero.
        /// Starts the periodic frequency/mode resend timer when clients are present; stops it when none remain.
        /// </summary>
        public void SetClientsConnected(bool connected)
        {
            _clientsConnected = connected;

            if (connected)
                StartResendTimer();
            else
                StopResendTimer();
        }

        public void SetAmpWakeup(bool active)
        {
            // Not implemented in this sample.
        }

        #endregion

        #region Resend Timer

        private void StartResendTimer()
        {
            if (_resendTimer != null) return; // already running

            _resendTimer = new Timer(ResendIntervalMs) { AutoReset = true };
            _resendTimer.Elapsed += OnResendTimerElapsed;
            _resendTimer.Start();
            Logger.LogVerbose(ModuleName, $"Resend timer started ({ResendIntervalMs / 1000}s interval)");
        }

        private void StopResendTimer()
        {
            if (_resendTimer == null) return;

            // CLAUDE.md teardown order: -= handler, Stop, Dispose, null
            _resendTimer.Elapsed -= OnResendTimerElapsed;
            _resendTimer.Stop();
            _resendTimer.Dispose();
            _resendTimer = null;
            Logger.LogVerbose(ModuleName, "Resend timer stopped");
        }

        private void OnResendTimerElapsed(object? sender, System.Timers.ElapsedEventArgs e)
        {
            if (_statusTracker == null || !_clientsConnected) return;

            if (_statusTracker.FrequencyHz > 0)
                SendFrequencyCommand(_statusTracker.FrequencyHz);

            if (!string.IsNullOrEmpty(_statusTracker.TransmitMode))
                SendModeCommand(_statusTracker.TransmitMode);
        }

        #endregion

        #region Frequency/Mode Senders

        private void SendFrequencyCommand(int frequencyHz)
        {
            if (_commandQueue == null || _config == null) return;
            //Debug.WriteLine(ModuleName + $" SendFrequencyCommand({frequencyHz} Hz)");
            if (_config.PluginFreqModeProtocol == Constants.ProtocolCiv)
            {
                byte[] frame = CivProtocolBuilder.BuildSetFrequency(
                    frequencyHz, _config.CivTransceiverAddress, _config.CivControllerAddress);
                _commandQueue.SendCommand(frame);
            }
            else
            {
                string cmd = CatProtocolBuilder.BuildSetFrequency(frequencyHz);
                _commandQueue.SendCommand(cmd);
            }
        }

        private void SendModeCommand(string mode)
        {
            if (_commandQueue == null || _config == null) return;
            //Debug.WriteLine(ModuleName + $" SendModeCommand({mode})");
            if (_config.PluginFreqModeProtocol == Constants.ProtocolCiv)
            {
                byte[]? frame = CivProtocolBuilder.BuildSetMode(
                    mode, _config.CivTransceiverAddress, _config.CivControllerAddress);
                if (frame != null)
                    _commandQueue.SendCommand(frame);
                else
                    Logger.LogVerbose(ModuleName, $"Unrecognized CI-V mode: {mode}");
            }
            else
            {
                string? cmd = CatProtocolBuilder.BuildSetMode(mode);
                if (cmd != null)
                    _commandQueue.SendCommand(cmd);
                else
                    Logger.LogVerbose(ModuleName, $"Unrecognized CAT mode: {mode}");
            }
        }

        #endregion

        #region Event Handlers

        private void OnDataReceived(string data)
        {
            if (_parser == null) return;

            bool isAck = _parser.Parse(data);
            //Logger.LogVerbose(ModuleName, isAck ? $"ACK received: {data}" : $"Unexpected response: {data}");
        }

        private void OnConnectionStateChanged(PluginConnectionState state)
        {
            var previous = ConnectionState;
            ConnectionStateChanged?.Invoke(this, new PluginConnectionStateChangedEventArgs(previous, state));

            if (state == PluginConnectionState.Connected)
            {
                Logger.LogInfo(ModuleName, "Connected to device");

                // Resend last known frequency and mode on reconnect
                if (_statusTracker != null)
                {
                    if (_statusTracker.FrequencyHz > 0)
                        SendFrequencyCommand(_statusTracker.FrequencyHz);
                    if (!string.IsNullOrEmpty(_statusTracker.TransmitMode))
                        SendModeCommand(_statusTracker.TransmitMode);
                }
            }
            else if (state == PluginConnectionState.Disconnected)
            {
                Logger.LogInfo(ModuleName, "Disconnected from device");
            }
        }

        #endregion
    }
}
