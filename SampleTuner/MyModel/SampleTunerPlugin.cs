#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using PgTg.AMP;
using PgTg.Common;
using PgTg.Plugins;
using PgTg.Plugins.Core;
using SampleTuner.MyModel.Internal;

namespace SampleTuner.MyModel
{
    /// <summary>
    /// Sample tuner plugin demonstrating ITunerPlugin implementation.
    /// Uses the MyModel/Internal architecture pattern with separated concerns:
    /// IConnection (TCP or Serial), CommandQueue, ResponseParser, StatusTracker, Constants.
    /// </summary>
    [PluginInfo("sample.tuner", "Sample Tuner",
        Version = "1.0.0",
        Manufacturer = "Sample Manufacturer",
        Capability = PluginCapability.Tuner,
        Description = "Sample tuner plugin for third-party development reference",
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
    public class SampleTunerPlugin : ITunerPlugin
    {
        public const string PluginId = "sample.tuner";
        private const string ModuleName = "SampleTunerPlugin";

        private readonly CancellationToken _cancellationToken;

        // Internal components
        private ISampleTunerConnection? _connection;
        private CommandQueue? _commandQueue;
        private ResponseParser? _parser;
        private StatusTracker? _statusTracker;
        private SampleTunerConfiguration? _config;

        private bool _stopped;
        private bool _disposed;
        private bool _disableControlsOnDisconnect = true;

        #region IDevicePlugin

        public PluginInfo Info { get; } = new PluginInfo
        {
            Id = PluginId,
            Name = "Sample Tuner",
            Version = "1.0.0",
            Manufacturer = "Sample Manufacturer",
            Capability = PluginCapability.Tuner,
            Description = "Sample tuner plugin for third-party development reference",
            ConfigurationType = typeof(SampleTunerConfiguration),
            UiSections = PluginUiSection.Tcp | PluginUiSection.Serial | PluginUiSection.Reconnect
        };

        public PluginConnectionState ConnectionState => _connection?.ConnectionState ?? PluginConnectionState.Disconnected;

        public double MeterDisplayMaxPower => Constants.MeterDisplayMaxPower;

        /// <summary>
        /// Controls whether the Device Control panel automatically disables LEDs and buttons
        /// when the connection state is not Connected. Set via plugin settings JSON:
        ///   "DisableControlsOnDisconnect": false
        /// Defaults to true (controls are disabled when disconnected, except the Power LED).
        /// Set to false if your plugin manages UI state independently via GetDeviceData() values.
        /// </summary>
        public bool DisableControlsOnDisconnect => _disableControlsOnDisconnect;

        public event EventHandler<PluginConnectionStateChangedEventArgs>? ConnectionStateChanged;
        public event EventHandler<MeterDataEventArgs>? MeterDataAvailable;

        /// <summary>
        /// Raise when data has changed.
        /// Bridge subscribes to push /device WebSocket updates on change instead of polling.
        /// </summary>
        public event EventHandler? DeviceDataChanged;

        #endregion

        #region ITunerPlugin

        public event EventHandler<TunerStatusEventArgs>? TunerStatusChanged;

        #endregion

        public SampleTunerPlugin(CancellationToken cancellationToken)
        {
            _cancellationToken = cancellationToken;
        }

        #region Lifecycle

        public Task InitializeAsync(IPluginConfiguration configuration, CancellationToken cancellationToken)
        {
            _config = configuration as SampleTunerConfiguration ?? new SampleTunerConfiguration
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
                Constants.PollingRxMs,
                Constants.PollingTxMs);
            _commandQueue.SkipDeviceWakeup = _config.SkipDeviceWakeup;

            // Read the connection-state UI behaviour from settings.
            // Set "DisableControlsOnDisconnect": false in the plugin's settings JSON to opt out.
            _disableControlsOnDisconnect = _config.DisableControlsOnDisconnect;

            // Wire up events
            _connection.DataReceived += OnDataReceived;
            _connection.ConnectionStateChanged += OnConnectionStateChanged;

            return Task.CompletedTask;
        }

        public async Task StartAsync()
        {
            if (_connection == null || _commandQueue == null || _config == null)
                throw new InvalidOperationException("Plugin not initialized");

            // Check cancellation before starting to prevent startup during shutdown
            if (_cancellationToken.IsCancellationRequested)
            {
                Logger.LogInfo(ModuleName, "Plugin startup cancelled before start");
                return;
            }

            // Start connection first (must be connected before sending init commands)
            await _connection.StartAsync();

            // Check cancellation after connection start
            if (_cancellationToken.IsCancellationRequested)
            {
                _connection.Stop();
                Logger.LogInfo(ModuleName, "Plugin startup cancelled after connection start");
                return;
            }

            // Start command queue with device initialization (waits for device to respond)
            await _commandQueue.StartAsync();

            // Only log success if not cancelled
            if (!_cancellationToken.IsCancellationRequested)
            {
                Logger.LogInfo(ModuleName, "Plugin started");
            }
        }

        public Task StopAsync()
        {
            if (_stopped) return Task.CompletedTask;

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

        public void SetFrequencyKhz(int frequencyKhz)
        {
            _commandQueue?.SetFrequencyKhz(frequencyKhz);
        }

        public void SetRadioPtt(bool isPtt)
        {
            if (_statusTracker != null && _statusTracker.SetRadioPtt(isPtt))
            {
                // RadioPtt changed - update command queue PTT state
                _commandQueue?.OnPttStateChanged(isPtt);

                // Zero meter values when transitioning from TX to RX
                if (!isPtt)
                {
                    _statusTracker.ZeroMeterValues();
                    RaiseMeterDataEvent();
                }
            }
        }

        public void SetTransmitMode(string mode)
        {
            // Notify the plugin of the radio's current transmit mode (e.g. "USB", "CW", "AM").
        }

        #endregion

        #region Event Handlers

        private void OnDataReceived(string data)
        {
            if (_parser == null || _statusTracker == null || _commandQueue == null) return;

            // Parse the response
            var update = _parser.Parse(data, _statusTracker);

            // Handle firmware version
            if (update.FirmwareVersion.HasValue)
            {
                _commandQueue.SetFirmwareVersion(update.FirmwareVersion.Value);
            }

            // Apply to status tracker
            bool hadTunerChange = update.TunerStateChanged || update.TuningStateChanged || update.TunerRelaysChanged || update.FaultChanged;
            bool hadDeviceDataChange = update.TunerStateChanged || update.FaultCode.HasValue
                || update.BandNumber.HasValue || update.Antenna.HasValue || update.FanSpeed.HasValue;

            _statusTracker.ApplyUpdate(update);

            // Update command queue tuning state
            if (update.TuningState.HasValue)
            {
                Debug.WriteLine("Tuning State change detected--raising OnTuningStateChanged");
                _commandQueue.OnTuningStateChanged(update.TuningState.Value == TunerTuningState.TuningInProgress);

                // Zero meter values when tune cycle completes
                if (update.TuningState.Value == TunerTuningState.NotTuning)
                {
                    _statusTracker.ZeroMeterValues();
                }
            }

            // Raise events
            if (hadTunerChange)
            {
                var tunerStatus = _statusTracker.GetTunerStatus();
                tunerStatus.WhatChanged = DetermineTunerChange(update);
                Debug.WriteLine("Tuner Status change detected--raising TunerStatusChanged");
                TunerStatusChanged?.Invoke(this, new TunerStatusEventArgs(tunerStatus, PluginId));
            }

            // Raise device data changed event for Device Control panel updates
            if (hadDeviceDataChange)
                DeviceDataChanged?.Invoke(this, EventArgs.Empty);

            // Raise meter data event on every data received
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
            bool isTuning = _statusTracker.TuningState == TunerTuningState.TuningInProgress;
            var args = new MeterDataEventArgs(readings, isTuning, PluginId);
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
        /// Returns the LED layout shown in the Device Control window for this plugin.
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
        ///   $BYP;  → response "$BYP N;" or "$BYP B;" → StatusTracker["BYP"]
        ///   $ANT;  → response "$ANT 1;"  or "$ANT 2;" → StatusTracker["AN"]
        ///   $FLT;  → response "$FLT n;"               → StatusTracker["FLT"]
        /// </summary>
        public DeviceControlDefinition? GetDeviceControlDefinition()
        {
            return new DeviceControlDefinition
            {
                Elements = new List<DeviceControlElement>
                {
                    // ---------------------------------------------------------------
                    // POWER LED  *** SPECIAL CASE — IsPowerIndicator = true ***
                    //   ResponseKey "PS" populated by deriving from TunerState in
                    //   StatusTracker.GetDeviceData(): 1 when tuner is responding, 0 otherwise.
                    //   Active (green)   = tuner is powered on and communicating
                    //   Inactive (gray)  = tuner is off or not yet connected
                    //   Click while ON   → sends $PS0; (power off)
                    //   Click while OFF  → sends $PS1; (power on)
                    //
                    //   IsPowerIndicator = true tells the Device Control panel that this
                    //   element represents the device power state.  Two behaviours follow:
                    //     1. This LED remains ENABLED even when the device is off, so the
                    //        user can always click it to power the device back on.
                    //     2. All other LEDs (and the fan buttons, if present) are disabled
                    //        automatically while this element is inactive (device off).
                    //   Set IsPowerIndicator on at most ONE element per definition.
                    // ---------------------------------------------------------------
                    new DeviceControlElement
                    {
                        ActiveColor      = "green",
                        InactiveColor    = "gray",
                        ActiveText       = "Power",
                        InactiveText     = "Power",
                        ActiveCommand    = "$PS0;",    // Send to power off
                        InactiveCommand  = "$PS1;",    // Send to power on
                        ResponseKey      = "PS",       // Matches GetDeviceData()["PS"]
                        ActiveValue      = "1",        // 1 = powered on
                        IsClickable      = true,
                        IsPowerIndicator = true        // Keeps this LED clickable when device is off
                    },

                    // ---------------------------------------------------------------
                    // INLINE / BYPASS LED
                    //   ResponseKey "BYP" populated by $BYP; poll.
                    //   Device response: "$BYP N;" = inline (not bypassed)
                    //                   "$BYP B;" = bypassed
                    //   Active (green)   = tuner is inline (actively matching)
                    //   Inactive (yellow)= tuner is bypassed (pass-through)
                    //   Click while INLINE  → sends $BYPB; (go to bypass)
                    //   Click while BYPASS  → sends $BYPN; (go to inline / not-bypassed)
                    // ---------------------------------------------------------------
                    new DeviceControlElement
                    {
                        ActiveColor    = "green",
                        InactiveColor  = "yellow",
                        ActiveText     = "Inline",
                        InactiveText   = "Bypass",
                        ActiveCommand  = "$BYPB;",   // Currently inline → go to bypass
                        InactiveCommand = "$BYPN;",  // Currently bypassed → go inline
                        ResponseKey    = "BYP",      // Matches GetDeviceData()["BYP"]
                        ActiveValue    = "1",        // 1 = inline
                        IsClickable    = true
                    },

                    // ---------------------------------------------------------------
                    // ANTENNA 1 LED
                    //   ResponseKey "AN" populated by $ANT; poll.
                    //   Device response: "$ANT 1;" sets AN = 1, "$ANT 2;" sets AN = 2.
                    //   Active (green)   = Antenna 1 is currently selected
                    //   Inactive (gray)  = another antenna is selected
                    //   Click (any state)→ sends $ANT 1; to select antenna 1
                    //   NOTE: Ant1 and Ant2 share the same ResponseKey "AN" but each has
                    //         a different ActiveValue — only one can be active at a time.
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
                    // ANTENNA 2 LED
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
                    // FAULT LED
                    //   ResponseKey "FLT" populated by $FLT; poll.
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
                        ResponseKey    = "FLT",      // Matches GetDeviceData()["FLT"]
                        ActiveValue    = "1",        // Active when FaultCode > 0
                        IsClickable    = true
                    }
                },

                // ---------------------------------------------------------------
                // FAN SPEED ROW
                //   ResponseKey "FN" is populated by $FAN; poll → StatusTracker["FN"]
                //   MaxSpeed 3 reflects a lighter-duty tuner fan (0 = off, 3 = full).
                //   SetCommandPrefix "$FC" → button sends "$FC2;" to set speed 2.
                //
                //   Fan button enable/disable is automatic: because the Power element
                //   above has IsPowerIndicator = true, the panel disables the fan
                //   buttons whenever the device is off — no extra configuration needed.
                // ---------------------------------------------------------------
                FanControl = new FanControlDefinition
                {
                    ResponseKey      = "FN",    // Matches GetDeviceData()["FN"]
                    MaxSpeed         = 3,        // 0 = off, 3 = full speed
                    SetCommandPrefix = "$FC",    // e.g. "$FC2;" to set speed 2
                }
            };
        }

        #endregion

        #region Helpers

        private TunerStatusChange DetermineTunerChange(ResponseParser.StatusUpdate update)
        {
            if (update.FaultChanged) return TunerStatusChange.FaultOccurred;
            if (update.TuningStateChanged) return TunerStatusChange.TuningStateChanged;
            if (update.TunerStateChanged) return TunerStatusChange.OperateStateChanged;
            if (update.TunerRelaysChanged) return TunerStatusChange.RelayValuesChanged;
            return TunerStatusChange.General;
        }

        #endregion
    }
}
