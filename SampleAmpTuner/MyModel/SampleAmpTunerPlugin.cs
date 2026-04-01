#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PgTg.AMP;
using PgTg.Common;
using PgTg.Plugins;
using PgTg.Plugins.Core;
using SampleAmpTuner.MyModel.Internal;

namespace SampleAmpTuner.MyModel
{
    /// <summary>
    /// Sample combined amplifier+tuner plugin demonstrating IAmplifierTunerPlugin implementation.
    /// Uses the MyModel/Internal architecture pattern with separated concerns:
    /// IConnection (TCP or Serial), CommandQueue, ResponseParser, StatusTracker, Constants.
    /// </summary>
    [PluginInfo("sample.amplifier-tuner", "Sample Amplifier+Tuner",
        Version = "1.0.0",
        Manufacturer = "Sample Manufacturer",
        Capability = PluginCapability.AmplifierAndTuner,
        Description = "Sample combined amplifier+tuner plugin for third-party development reference",
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
        // Example — TCP only, with Wake-on-LAN:
        //   UiSections = PluginUiSection.Tcp | PluginUiSection.Reconnect | PluginUiSection.Wol
        UiSections = PluginUiSection.Tcp | PluginUiSection.Serial | PluginUiSection.Reconnect)]
    public class SampleAmpTunerPlugin : IAmplifierTunerPlugin
    {
        public const string PluginId = "sample.amplifier-tuner";
        private const string ModuleName = "SampleAmpTunerPlugin";

        private readonly CancellationToken _cancellationToken;

        // Internal components
        private ISampleAmpTunerConnection? _connection;
        private CommandQueue? _commandQueue;
        private ResponseParser? _parser;
        private StatusTracker? _statusTracker;
        private SampleAmpTunerConfiguration? _config;

        private bool _radioConnected;
        private bool _stopped;
        private bool _disposed;

        #region IDevicePlugin

        public PluginInfo Info { get; } = new PluginInfo
        {
            Id = PluginId,
            Name = "Sample Amplifier+Tuner",
            Version = "1.0.0",
            Manufacturer = "Sample Manufacturer",
            Capability = PluginCapability.AmplifierAndTuner,
            Description = "Sample combined amplifier+tuner plugin for third-party development reference",
            ConfigurationType = typeof(SampleAmpTunerConfiguration),
            UiSections = PluginUiSection.Tcp | PluginUiSection.Serial | PluginUiSection.Reconnect
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

        #region IAmplifierPlugin

        public event EventHandler<AmplifierStatusEventArgs>? StatusChanged;

        #endregion

        #region ITunerPlugin

        public event EventHandler<TunerStatusEventArgs>? TunerStatusChanged;

        #endregion

        public SampleAmpTunerPlugin(CancellationToken cancellationToken)
        {
            _cancellationToken = cancellationToken;
        }

        #region Lifecycle

        public Task InitializeAsync(IPluginConfiguration configuration, CancellationToken cancellationToken)
        {
            _config = configuration as SampleAmpTunerConfiguration ?? new SampleAmpTunerConfiguration
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
                serialConnection.Configure(_config.SerialPort, _config.BaudRate);
                _connection = serialConnection;
                Logger.LogInfo(ModuleName, $"Using serial connection: {_config.SerialPort} at {_config.BaudRate} baud");
            }
            else
            {
                var tcpConnection = new TcpConnection(_cancellationToken);
                tcpConnection.Configure(_config.IpAddress, _config.Port);
                _connection = tcpConnection;
                Logger.LogInfo(ModuleName, $"Using TCP connection: {_config.IpAddress}:{_config.Port}");
            }

            // Create other components
            _commandQueue = new CommandQueue(_connection, _cancellationToken);
            _parser = new ResponseParser();
            _statusTracker = new StatusTracker();

            // Configure command queue
            _commandQueue.Configure(
                _config.PollingIntervalRxMs,
                _config.PollingIntervalTxMs,
                _config.PttWatchdogIntervalMs);
            _commandQueue.SkipDeviceWakeup = _config.SkipDeviceWakeup;

            // Wire up events
            _connection.DataReceived += OnDataReceived;
            _connection.ConnectionStateChanged += OnConnectionStateChanged;

            return Task.CompletedTask;
        }

        public async Task StartAsync()
        {
            Logger.LogVerbose(ModuleName, "StartAsync");
            if (_connection == null || _commandQueue == null || _config == null)
                throw new InvalidOperationException("Plugin not initialized");

            if (_cancellationToken.IsCancellationRequested)
            {
                Logger.LogInfo(ModuleName, "Plugin startup cancelled before start");
                return;
            }

            // Start connection first (must be connected before sending init commands)
            await _connection.StartAsync();

            if (_cancellationToken.IsCancellationRequested)
            {
                _connection.Stop();
                Logger.LogInfo(ModuleName, "Plugin startup cancelled after connection start");
                return;
            }

            // Start command queue with device initialization (waits for device to respond)
            await _commandQueue.StartAsync();

            if (!_cancellationToken.IsCancellationRequested)
            {
                Logger.LogInfo(ModuleName, "Plugin started");
            }
            else
            {
                Logger.LogInfo(ModuleName, "Plugin startup cancelled after commandQueue start");
            }
        }

        public async Task StopAsync()
        {
            if (_stopped) return;

            Logger.LogInfo(ModuleName, "Stopping plugin");

            // Zero meter values and send final update
            _statusTracker?.ZeroMeterValues();
            RaiseMeterDataEvent();

            // Stop command queue
            _commandQueue?.Stop();

            // Unwire connection events before stopping
            if (_connection != null)
            {
                _connection.DataReceived -= OnDataReceived;
                _connection.ConnectionStateChanged -= OnConnectionStateChanged;
                _connection.Stop();
            }

            _stopped = true;
            Logger.LogInfo(ModuleName, "Plugin stopped");

            await Task.CompletedTask;
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

        #region IDevicePlugin Wakeup/Shutdown

        public async Task WakeupDeviceAsync()
        {
            if (_connection?.IsConnected == true)
            {
                Logger.LogInfo(ModuleName, "WakeupDeviceAsync: starting device initialization");
                if (_commandQueue != null)
                    await _commandQueue.InitializeDeviceAsync();
            }
        }

        public Task ShutdownDeviceAsync()
        {
            if (_connection?.IsConnected == true)
            {
                _connection.Send(Constants.ShutdownCmd);
                Logger.LogInfo(ModuleName, "ShutdownDeviceAsync: sent ShutdownCmd");
            }
            return Task.CompletedTask;
        }

        #endregion

        #region IAmplifierPlugin Methods

        public AmplifierStatusData GetStatus()
        {
            return _statusTracker?.GetAmplifierStatus() ?? new AmplifierStatusData();
        }

        public void SendPriorityCommand(AmpCommand command)
        {
            if (_commandQueue == null || _statusTracker == null) return;

            _commandQueue.SendPriorityCommand(command, _statusTracker.AmpState);
        }

        public void SetFrequencyKhz(int frequencyKhz)
        {
            _commandQueue?.SetFrequencyKhz(frequencyKhz);
        }

        public void SetRadioConnected(bool connected)
        {
            _radioConnected = connected;

            if (!connected && _commandQueue != null)
            {
                // Safety: force release PTT if radio disconnects
                _commandQueue.ForceReleasesPtt();
                Logger.LogVerbose(ModuleName, "Radio disconnected, forcing device to RX (Safety Measure)");
            }
        }

        public void SetOperateMode(bool operate)
        {
            if (_commandQueue == null) return;

            _commandQueue.SetOperateMode(operate);
            Logger.LogVerbose(ModuleName, $"Setting amplifier to {(operate ? "OPERATE" : "STANDBY")} mode");
        }

        public void SetRadioPtt(bool isPtt)
        {
            if (_statusTracker != null && _statusTracker.SetRadioPtt(isPtt))
            {
                // RadioPtt changed - update command queue PTT state
                _commandQueue?.OnPttStateChanged(isPtt);
            }
        }

        public void SetTransmitMode(string mode)
        {
            // Notify the plugin of the radio's current transmit mode (e.g. "USB", "CW", "AM").
        }

        #endregion

        #region ITunerPlugin Methods

        public TunerStatusData GetTunerStatus()
        {
            return _statusTracker?.GetTunerStatus() ?? new TunerStatusData();
        }

        public void SetInline(bool inline)
        {
            _commandQueue?.SetTunerInline(inline);
        }

        public void StartTune()
        {
            _commandQueue?.SetTuneStart(true);
        }

        public void StopTune()
        {
            _commandQueue?.SetTuneStart(false);
        }

        #endregion

        #region Event Handlers

        private void OnDataReceived(string data)
        {
            if (_parser == null || _statusTracker == null || _commandQueue == null) return;

            // Parse the response
            var update = _parser.Parse(data, _statusTracker);

            // Handle TX/RX acknowledgments
            if (update.IsPtt.HasValue)
            {
                _commandQueue.OnTxRxResponseReceived(update.IsPtt.Value);
            }

            // Handle firmware version
            if (update.FirmwareVersion.HasValue)
            {
                _commandQueue.SetFirmwareVersion(update.FirmwareVersion.Value);
            }

            // Determine what changed before applying the update
            bool hadAmpChange = update.AmpStateChanged || update.PttStateChanged || update.PttReady;
            bool hadTunerChange = update.TunerStateChanged || update.TuningStateChanged || update.TunerRelaysChanged;
            bool hadDeviceDataChange = update.AmpStateChanged || update.TunerStateChanged
                || update.FaultCode.HasValue || update.BandNumber.HasValue || update.Antenna.HasValue
                || update.FanSpeed.HasValue;

            // Apply to status tracker
            _statusTracker.ApplyUpdate(update);

            // Update command queue PTT state
            if (update.IsPtt.HasValue)
            {
                _commandQueue.OnPttStateChanged(update.IsPtt.Value);
            }

            // Update command queue tuning state
            if (update.TuningState.HasValue)
            {
                _commandQueue.OnTuningStateChanged(update.TuningState.Value == TunerTuningState.TuningInProgress);
            }

            // Raise amplifier events
            if (hadAmpChange)
            {
                var ampStatus = _statusTracker.GetAmplifierStatus();
                ampStatus.WhatChanged = DetermineAmpChange(update);
                StatusChanged?.Invoke(this, new AmplifierStatusEventArgs(ampStatus, PluginId));
            }

            // Raise tuner events
            if (hadTunerChange)
            {
                var tunerStatus = _statusTracker.GetTunerStatus();
                tunerStatus.WhatChanged = DetermineTunerChange(update);
                TunerStatusChanged?.Invoke(this, new TunerStatusEventArgs(tunerStatus, PluginId));
            }

            // Raise device data changed event for Device Control panel updates
            if (hadDeviceDataChange)
                DeviceDataChanged?.Invoke(this, EventArgs.Empty);

            // Raise meter data event on every status update from the device
            RaiseMeterDataEvent();
        }

        private void OnConnectionStateChanged(PluginConnectionState state)
        {
            var previous = ConnectionState;
            ConnectionStateChanged?.Invoke(this, new PluginConnectionStateChangedEventArgs(previous, state));

            if (state == PluginConnectionState.Connected)
            {
                Logger.LogInfo(ModuleName, "Connected to device");
            }
            else if (state == PluginConnectionState.Disconnected)
            {
                Logger.LogInfo(ModuleName, "Disconnected from device");
            }
        }

        private void RaiseMeterDataEvent()
        {
            if (_statusTracker == null) return;

            var readings = _statusTracker.GetMeterReadings();
            bool isTransmitting = _statusTracker.IsPtt || _statusTracker.RadioPtt;
            var args = new MeterDataEventArgs(readings, isTransmitting, PluginId);
            MeterDataAvailable?.Invoke(this, args);
        }

        #endregion

        #region IDevicePlugin Device Control

        public Dictionary<string, object> GetDeviceData()
        {
            return _statusTracker?.GetDeviceData() ?? new Dictionary<string, object>();
        }

        public bool SendDeviceCommand(string command)
        {
            if (_connection == null || !_connection.IsConnected) return false;
            _connection.Send(command);
            return true;
        }

        /// <summary>
        /// Returns the LED layout shown in the Device Control window for this combined
        /// amplifier+tuner plugin.
        ///
        /// HOW IT WORKS — three-part data flow:
        ///   1. The poller in CommandQueue sends the commands in Constants.RxPollCommands on a
        ///      timer (Constants.PollingRxMs).  Each command triggers a device response.
        ///   2. ResponseParser turns each response into a StatusUpdate, and StatusTracker stores
        ///      the resulting state.  StatusTracker.GetDeviceData() exposes those values as a
        ///      Dictionary keyed by the same short strings used in ResponseKey below.
        ///   3. When DeviceDataChanged fires, the Controller re-fetches GetDeviceData() and
        ///      compares each value to the LED's ActiveValue (string, case-insensitive).
        ///      Match → ActiveColor + ActiveText.  No match → InactiveColor + InactiveText.
        ///      When the user clicks a LED, SendDeviceCommand() is called with either
        ///      ActiveCommand (if currently active) or InactiveCommand (if currently inactive).
        ///
        /// POLLER COMMANDS that populate these LEDs (see Constants.RxPollCommands):
        ///   $OPR;  → response "$OPR 1;" or "$OPR 0;" → StatusTracker["ON"] and ["OS"]
        ///   $ANT;  → response "$ANT 1;"  or "$ANT 2;" → StatusTracker["AN"]
        ///   $BYP;  → response "$BYP N;"  or "$BYP B;" → StatusTracker["AI"]
        ///   $FLT;  → response "$FLT n;"               → StatusTracker["FL"]
        /// </summary>
        public DeviceControlDefinition? GetDeviceControlDefinition()
        {
            return new DeviceControlDefinition
            {
                Elements = new List<DeviceControlElement>
                {
                    // ---------------------------------------------------------------
                    // POWER LED  (Amplifier)
                    //   ResponseKey "ON" populated by $OPR; poll.
                    //   StatusTracker["ON"] = 1 when AmpState is not Unknown or Standby.
                    //   Active (green)   = amplifier is powered on
                    //   Inactive (gray)  = amplifier is off or not yet responding
                    //   Click while ON   → sends $ON0; (power off)
                    //   Click while OFF  → sends $ON1; (power on)
                    // ---------------------------------------------------------------
                    new DeviceControlElement
                    {
                        ActiveColor    = "green",
                        InactiveColor  = "gray",
                        ActiveText     = "Power",
                        InactiveText   = "Power",
                        ActiveCommand  = "$ON0;",    // Send to turn device off
                        InactiveCommand = "$ON1;",   // Send to turn device on
                        ResponseKey    = "ON",       // Matches GetDeviceData()["ON"]
                        ActiveValue    = "1",        // 1 = powered on
                        IsClickable    = true
                    },

                    // ---------------------------------------------------------------
                    // OPERATE / STANDBY LED  (Amplifier)
                    //   ResponseKey "OS" populated by $OPR; poll.
                    //   StatusTracker["OS"] = 1 when AmpState is Operate or Transmit.
                    //   Active (green)   = amplifier in Operate mode (ready to TX)
                    //   Inactive (yellow)= amplifier in Standby mode (yellow = caution, not fault)
                    //   Click while OPERATE → sends $OS0; (go to standby)
                    //   Click while STANDBY → sends $OS1; (go to operate)
                    // ---------------------------------------------------------------
                    new DeviceControlElement
                    {
                        ActiveColor    = "green",
                        InactiveColor  = "yellow",
                        ActiveText     = "Operate",
                        InactiveText   = "Standby",
                        ActiveCommand  = "$OS0;",    // Go to standby
                        InactiveCommand = "$OS1;",   // Go to operate
                        ResponseKey    = "OS",       // Matches GetDeviceData()["OS"]
                        ActiveValue    = "1",
                        IsClickable    = true
                    },

                    // ---------------------------------------------------------------
                    // ANTENNA 1 LED  (Shared by Amp + Tuner)
                    //   ResponseKey "AN" populated by $ANT; poll.
                    //   Device response: "$ANT 1;" sets AN = 1.
                    //   Active (green)   = Antenna 1 is currently selected
                    //   Inactive (gray)  = another antenna is selected
                    //   Click (any state)→ sends $ANT 1; to select antenna 1
                    //   NOTE: Ant1 and Ant2 share ResponseKey "AN" but have different
                    //         ActiveValue strings — only one can be active at a time.
                    // ---------------------------------------------------------------
                    new DeviceControlElement
                    {
                        ActiveColor    = "green",
                        InactiveColor  = "gray",
                        ActiveText     = "Ant 1",
                        InactiveText   = "Ant 1",
                        ActiveCommand  = "$ANT 1;",  // Already on Ant1, re-select (harmless)
                        InactiveCommand = "$ANT 1;", // Switch to Ant1
                        ResponseKey    = "AN",       // Matches GetDeviceData()["AN"]
                        ActiveValue    = "1",        // Active when AN == 1
                        IsClickable    = true
                    },

                    // ---------------------------------------------------------------
                    // ANTENNA 2 LED  (Shared by Amp + Tuner)
                    //   Shares ResponseKey "AN" with Ant1, but ActiveValue = "2"
                    //   Active (green)   = Antenna 2 is currently selected
                    //   Inactive (gray)  = another antenna is selected
                    //   Click (any state)→ sends $ANT 2; to select antenna 2
                    // ---------------------------------------------------------------
                    new DeviceControlElement
                    {
                        ActiveColor    = "green",
                        InactiveColor  = "gray",
                        ActiveText     = "Ant 2",
                        InactiveText   = "Ant 2",
                        ActiveCommand  = "$ANT 2;",  // Already on Ant2, re-select (harmless)
                        InactiveCommand = "$ANT 2;", // Switch to Ant2
                        ResponseKey    = "AN",       // Same key as Ant1, different ActiveValue
                        ActiveValue    = "2",        // Active when AN == 2
                        IsClickable    = true
                    },

                    // ---------------------------------------------------------------
                    // ATU INLINE / BYPASS LED  (Tuner)
                    //   ResponseKey "AI" populated by $BYP; poll.
                    //   Device response: "$BYP N;" = inline (not bypassed) → AI = 1
                    //                   "$BYP B;" = bypassed                → AI = 0
                    //   Active (green)   = ATU is inline (active matching)
                    //   Inactive (yellow)= ATU is bypassed (pass-through)
                    //   Click while INLINE  → sends $AI0; (bypass the ATU)
                    //   Click while BYPASS  → sends $AI1; (put ATU inline)
                    // ---------------------------------------------------------------
                    new DeviceControlElement
                    {
                        ActiveColor    = "green",
                        InactiveColor  = "yellow",
                        ActiveText     = "Inline",
                        InactiveText   = "Bypass",
                        ActiveCommand  = "$AI0;",    // Currently inline → go to bypass
                        InactiveCommand = "$AI1;",   // Currently bypassed → go inline
                        ResponseKey    = "AI",       // Matches GetDeviceData()["AI"]
                        ActiveValue    = "1",        // 1 = inline
                        IsClickable    = true
                    },

                    // ---------------------------------------------------------------
                    // FAULT LED  (Amp + Tuner shared)
                    //   ResponseKey "FL" populated by $FLT; poll.
                    //   Active (red)     = a fault condition is present (FaultCode > 0)
                    //   Inactive (gray)  = no fault
                    //   Click while ACTIVE   → sends $FLC; (Clear Fault)
                    //   Click while INACTIVE → no-op (null = nothing sent)
                    // ---------------------------------------------------------------
                    new DeviceControlElement
                    {
                        ActiveColor    = "red",
                        InactiveColor  = "gray",
                        ActiveText     = "FAULT",
                        InactiveText   = "Fault",
                        ActiveCommand  = "$FLC;",    // Clear the fault
                        InactiveCommand = null,      // Nothing to do when no fault
                        ResponseKey    = "FL",       // Matches GetDeviceData()["FL"]
                        ActiveValue    = "1",        // Active when FaultCode > 0
                        IsClickable    = true
                    }
                },

                // ---------------------------------------------------------------
                // FAN SPEED ROW
                //   ResponseKey "FN" is populated by $FAN; poll → StatusTracker["FN"]
                //   MaxSpeed 6 matches the higher-power combined amp+tuner fan range (0–6).
                //   SetCommandPrefix "$FC" → button sends "$FC4;" to set speed 4.
                //   PowerResponseKey "ON" mirrors the Power LED: buttons are only
                //   enabled while the amplifier is powered on (ON == "1").
                // ---------------------------------------------------------------
                FanControl = new FanControlDefinition
                {
                    ResponseKey      = "FN",    // Matches GetDeviceData()["FN"]
                    MaxSpeed         = 6,        // 0 = off, 6 = full speed
                    SetCommandPrefix = "$FC",    // e.g. "$FC4;" to set speed 4
                    PowerResponseKey = "ON",     // Enable buttons only when amplifier is on
                    PowerActiveValue = "1"
                }
            };
        }

        #endregion

        #region Helpers

        private AmplifierStatusChange DetermineAmpChange(ResponseParser.StatusUpdate update)
        {
            if (update.PttReady) return AmplifierStatusChange.PttReady;
            if (update.PttStateChanged) return AmplifierStatusChange.PttStateChanged;
            if (update.AmpStateChanged) return AmplifierStatusChange.OperateStateChanged;
            return AmplifierStatusChange.General;
        }

        private TunerStatusChange DetermineTunerChange(ResponseParser.StatusUpdate update)
        {
            if (update.TuningStateChanged) return TunerStatusChange.TuningStateChanged;
            if (update.TunerStateChanged) return TunerStatusChange.OperateStateChanged;
            if (update.TunerRelaysChanged) return TunerStatusChange.RelayValuesChanged;
            return TunerStatusChange.General;
        }

        #endregion
    }
}
