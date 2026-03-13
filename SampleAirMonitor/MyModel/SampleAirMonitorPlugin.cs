#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using PgTg.Common;
using PgTg.Plugins;
using PgTg.Plugins.Core;
using SampleAirMonitor.MyModel.Internal;

namespace SampleAirMonitor.MyModel
{
    /// <summary>
    /// Sample GPIO output plugin demonstrating IGpioOutputPlugin implementation.
    /// Uses the MyModel/Internal architecture pattern with separated concerns:
    /// IConnection (TCP or Serial), CommandQueue, ResponseParser, StatusTracker, Constants.
    ///
    /// Unlike amplifier/tuner plugins, this GPIO plugin has no polling — it only
    /// sends output commands when the Bridge calls SetAmpPtt, SetAmpOperateMode, etc.
    /// </summary>
    [PluginInfo("sample.airmonitor", "Air Monitor",
        Version = "1.0.0",
        Manufacturer = "PgTg",
        Capability = PluginCapability.FrequencyModeMonitoring,
        Description = "Sample GPIO output plugin for third-party development reference",
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
        private const string ModuleName = "SampleAirMonitorPlugin";

        private readonly CancellationToken _cancellationToken;

        // Internal components
        private ISampleAirMonitorConnection? _connection;
        private CommandQueue? _commandQueue;
        private ResponseParser? _parser;
        private StatusTracker? _statusTracker;
        private SampleAirMonitorConfiguration? _config;

        private bool _stopped;
        private bool _disposed;

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
            if (_statusTracker == null || _commandQueue == null) return;

            _statusTracker.SetAmpPtt(ptt);
            _commandQueue.SendCommand(ptt ? Constants.AmpPttOnCmd : Constants.AmpPttOffCmd);
            Logger.LogVerbose(ModuleName, $"SetAmpPtt({ptt})");
        }

        /// <summary>
        /// Called by the Bridge when the amplifier operate/standby mode changes.
        /// Sends the appropriate GPIO command to the remote device.
        /// </summary>
        public void SetAmpOperateMode(bool operate)
        {
            if (_statusTracker == null || _commandQueue == null) return;

            _statusTracker.SetAmpOperate(operate);
            _commandQueue.SendCommand(operate ? Constants.AmpOperateCmd : Constants.AmpStandbyCmd);
            Logger.LogVerbose(ModuleName, $"SetAmpOperateMode({operate})");
        }

        /// <summary>
        /// Called by the Bridge when the tuner inline/bypass state changes.
        /// Sends the appropriate GPIO command to the remote device.
        /// </summary>
        public void SetTunerInline(bool inline)
        {
            if (_statusTracker == null || _commandQueue == null) return;

            _statusTracker.SetTunerInline(inline);
            _commandQueue.SendCommand(inline ? Constants.TunerInlineCmd : Constants.TunerBypassCmd);
            Logger.LogVerbose(ModuleName, $"SetTunerInline({inline})");
        }

        /// <summary>
        /// Called by the Bridge when a tune cycle starts or stops.
        /// Sends the appropriate GPIO command to the remote device.
        /// </summary>
        public void SetTunerTune(bool tuning)
        {
            if (_statusTracker == null || _commandQueue == null) return;

            _statusTracker.SetTunerTuning(tuning);
            _commandQueue.SendCommand(tuning ? Constants.TunerTuneStartCmd : Constants.TunerTuneStopCmd);
            Logger.LogVerbose(ModuleName, $"SetTunerTune({tuning})");
        }

        public void SetFrequencyKhz(int frequencyKhz)
        {
            if (_statusTracker == null || _commandQueue == null || _config == null) return;

            // Only send if frequency actually changed
            if (!_statusTracker.SetFrequencyKhz(frequencyKhz)) return;

            SendFrequencyCommand(frequencyKhz);
            Logger.LogVerbose(ModuleName, $"SetFrequencyKhz({frequencyKhz})");
        }

        public void SetTransmitMode(string mode)
        {
            if (_statusTracker == null || _commandQueue == null || _config == null) return;
            if (string.IsNullOrEmpty(mode)) return;

            // Only send if mode actually changed
            if (!_statusTracker.SetTransmitMode(mode)) return;

            SendModeCommand(mode);
            Logger.LogVerbose(ModuleName, $"SetTransmitMode({mode})");
        }

        #endregion

        #region Frequency/Mode Senders

        private void SendFrequencyCommand(int frequencyKhz)
        {
            if (_commandQueue == null || _config == null) return;

            if (_config.PluginFreqModeProtocol == Constants.ProtocolCiv)
            {
                byte[] frame = CivProtocolBuilder.BuildSetFrequency(
                    frequencyKhz, _config.CivTransceiverAddress, _config.CivControllerAddress);
                _commandQueue.SendCommand(frame);
            }
            else
            {
                string cmd = CatProtocolBuilder.BuildSetFrequency(frequencyKhz);
                _commandQueue.SendCommand(cmd);
            }
        }

        private void SendModeCommand(string mode)
        {
            if (_commandQueue == null || _config == null) return;

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
            Logger.LogVerbose(ModuleName, isAck ? $"ACK received: {data}" : $"Unexpected response: {data}");
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
                    if (_statusTracker.FrequencyKhz > 0)
                        SendFrequencyCommand(_statusTracker.FrequencyKhz);
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
