#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using PgTg.Common;
using Timer = System.Timers.Timer;

namespace SampleTuner.MyModel.Internal
{
    /// <summary>
    /// Manages command polling and priority command insertion for the sample tuner device.
    /// Tuner-specific commands (tune start/stop, bypass) are expedited ahead of regular polling.
    /// Tuning state drives fast polling (no PTT watchdog for tuner-only device).
    /// </summary>
    internal class CommandQueue : IDisposable
    {
        private const string ModuleName = "CommandQueue";

        private readonly ISampleTunerConnection _connection;
        private readonly object _priorityLock = new();

        private Timer? _pollTimer;
        private Timer? _initTimer;
        private CancellationTokenRegistration _timerRegistration;

        private string _priorityCommands = string.Empty;
        private int _rxPollIndex;
        private int _txPollIndex;
        private bool _isTuning;
        private bool _isPtt;
        private double _fwVersion;
        private bool _disposed;
        private bool _isInitialized;
        private bool _initializationInProgress;
        private TaskCompletionSource<bool>? _initCompletionSource;

        // Configuration
        private int _pollingRxMs = Constants.PollingRxMs;
        private int _pollingTxMs = Constants.PollingTxMs;
        private const int InitRetryIntervalMs = 500;

        /// <summary>
        /// Whether currently tuning.
        /// </summary>
        public bool IsTuning => _isTuning;

        /// <summary>
        /// Firmware version detected from device.
        /// </summary>
        public double FirmwareVersion => _fwVersion;

        /// <summary>
        /// Whether device initialization has completed.
        /// </summary>
        public bool IsInitialized => _isInitialized;

        /// <summary>
        /// When true, skip the device initialization/wake-up sequence.
        /// Set by the plugin when AmpWakeupMode == 0.
        /// </summary>
        public bool SkipDeviceWakeup { get; set; } = false;

        public CommandQueue(ISampleTunerConnection connection, CancellationToken cancellationToken)
        {
            _connection = connection;

            // Subscribe to DataReceived for initialization handling
            _connection.DataReceived += OnDataReceived;

            // Register token to stop timers on cancel and unblock any pending initialization
            _timerRegistration = cancellationToken.Register(() =>
            {
                _initTimer?.Stop();
                _pollTimer?.Stop();
                _initCompletionSource?.TrySetCanceled();
            });
        }

        private void OnDataReceived(string data)
        {
            if (_initializationInProgress)
            {
                OnInitializationResponse(data);
            }
        }

        /// <summary>
        /// Configure timing parameters.
        /// </summary>
        public void Configure(int pollingRxMs, int pollingTxMs)
        {
            _pollingRxMs = pollingRxMs;
            _pollingTxMs = pollingTxMs;
        }

        /// <summary>
        /// Start polling timer. Performs device initialization first if enabled.
        /// </summary>
        /// <returns>Task that completes when device initialization is done.</returns>
        public async Task StartAsync()
        {
            _pollTimer = new Timer { Interval = _pollingRxMs };
            _pollTimer.Elapsed += OnPollTimerElapsed;

            // Send wake-up sequence and wait for initialization to complete
            await StartDeviceInitializationAsync();
        }

        /// <summary>
        /// Sends wake-up commands to initialize the device.
        /// Normal polling begins after receiving the expected response.
        /// Can be disabled via Constants.DeviceInitializationEnabled.
        /// </summary>
        /// <returns>Task that completes when device responds.</returns>
        private async Task StartDeviceInitializationAsync()
        {
            if (!Constants.DeviceInitializationEnabled || SkipDeviceWakeup)
            {
                // Unsubscribe from DataReceived since we don't need it
                _connection.DataReceived -= OnDataReceived;
                _isInitialized = true;
                if (SkipDeviceWakeup)
                    Logger.LogVerbose(ModuleName, "Skipping device initialization (AmpWakeupMode=0)");
                else
                    Logger.LogVerbose(ModuleName, "Device initialization disabled, starting normal polling immediately");
                _pollTimer?.Start();
                return;
            }

            _isInitialized = false;
            _initializationInProgress = true;
            _initCompletionSource = new TaskCompletionSource<bool>();

            // Send identify command and wait for response
            _connection.Send(Constants.IdentifyCmd);
            Logger.LogVerbose(ModuleName, "Sent device initialization sequence, waiting for response");

            // Start timer to retry every 500ms until device responds
            _initTimer = new Timer { Interval = InitRetryIntervalMs };
            _initTimer.Elapsed += OnInitTimerElapsed;
            _initTimer.Start();

            // Wait for initialization to complete
            await _initCompletionSource.Task;
        }

        private void OnInitTimerElapsed(object? sender, ElapsedEventArgs e)
        {
            if (!_initializationInProgress || !_connection.IsConnected)
            {
                _initTimer?.Stop();
                return;
            }

            // Resend identify command
            _connection.Send(Constants.IdentifyCmd);
            Logger.LogVerbose(ModuleName, $"Resending identify command for device initialization");
        }

        /// <summary>
        /// Called when a response is received during initialization.
        /// Initialization completes when the expected identity response arrives.
        /// </summary>
        /// <param name="response">The response received from the device.</param>
        /// <returns>True if initialization is complete.</returns>
        public bool OnInitializationResponse(string response)
        {
            if (!_initializationInProgress)
                return _isInitialized;

            if (response.Contains(Constants.IdentifyResponse, StringComparison.OrdinalIgnoreCase))
            {
                // Stop the init retry timer per CLAUDE.md disposal pattern
                if (_initTimer != null)
                {
                    _initTimer.Elapsed -= OnInitTimerElapsed;
                    _initTimer.Stop();
                    _initTimer.Dispose();
                    _initTimer = null;
                }

                // Unsubscribe from DataReceived - no longer needed after initialization
                _connection.DataReceived -= OnDataReceived;

                _initializationInProgress = false;
                _isInitialized = true;
                Logger.LogVerbose(ModuleName, "Device detected, starting normal polling");

                // Now start the poll timer
                _pollTimer?.Start();

                // Signal that initialization is complete
                _initCompletionSource?.TrySetResult(true);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Stop polling timer.
        /// </summary>
        public void Stop()
        {
            _initTimer?.Stop();
            _pollTimer?.Stop();
        }

        /// <summary>
        /// Queue tuner inline/bypass command.
        /// </summary>
        public void SetTunerInline(bool inline)
        {
            lock (_priorityLock)
            {
                _priorityCommands = inline
                    ? Constants.InlineCmd
                    : Constants.TuneStopCmd + Constants.BypassCmd;
            }
            Logger.LogVerbose(ModuleName, $"Queued {(inline ? "INLINE" : "BYPASS")} command");
        }

        /// <summary>
        /// Queue a tune start/stop command.
        /// </summary>
        public void SetTuneStart(bool start)
        {
            lock (_priorityLock)
            {
                _priorityCommands = start
                    ? Constants.TuneStartCmd
                    : Constants.TuneStopCmd;
            }
            Logger.LogVerbose(ModuleName, $"Queued {(start ? "TUNE START" : "TUNE STOP")} command");

            // Immediately transition to tuning state when tune starts
            // This ensures fast polling (10ms) for the entire tune cycle
            if (start)
            {
                OnTuningStateChanged(true);
            }
        }

        /// <summary>
        /// Queue antenna selection command.
        /// </summary>
        public void SetAntenna(int antenna)
        {
            lock (_priorityLock)
            {
                _priorityCommands = antenna switch
                {
                    1 => Constants.Antenna1Cmd,
                    2 => Constants.Antenna2Cmd,
                    3 => Constants.Antenna3Cmd,
                    _ => Constants.Antenna1Cmd
                };
            }
            Logger.LogVerbose(ModuleName, $"Queued Antenna {antenna} command");
        }

        /// <summary>
        /// Send frequency command directly to device.
        /// </summary>
        public void SetFrequencyKhz(int frequencyKhz)
        {
            string txCommand = $"{Constants.SetFreqKhzCmdPrefix}{frequencyKhz:D5};";
            // Send frequency immediately for lowest latency
            _connection.Send(txCommand);
        }

        /// <summary>
        /// Called when PTT state changes (from radio interlock).
        /// Adjusts polling rate when PTT is active during tune.
        /// </summary>
        public void OnPttStateChanged(bool isPtt)
        {
            if (_disposed) return;

            _isPtt = isPtt;

            // Adjust polling rate based on PTT/tuning state
            if (_pollTimer != null)
            {
                bool needsFastPolling = _isPtt || _isTuning;
                int targetInterval = needsFastPolling ? _pollingTxMs : _pollingRxMs;
                if (_pollTimer.Interval != targetInterval)
                {
                    _pollTimer.Stop();
                    _pollTimer.Interval = targetInterval;
                    _pollTimer.Start();
                }
            }
        }

        /// <summary>
        /// Called when tuning state changes.
        /// </summary>
        public void OnTuningStateChanged(bool isTuning)
        {
            if (_disposed) return;

            _isTuning = isTuning;

            // Adjust polling rate based on tuning state
            // Use fast polling when tuning OR PTT is active
            // Timer interval changes require Stop/Start to take effect immediately
            if (_pollTimer != null)
            {
                bool needsFastPolling = _isTuning || _isPtt;
                int targetInterval = needsFastPolling ? _pollingTxMs : _pollingRxMs;
                if (_pollTimer.Interval != targetInterval)
                {
                    _pollTimer.Stop();
                    _pollTimer.Interval = targetInterval;
                    _pollTimer.Start();
                }
            }
        }

        /// <summary>
        /// Set firmware version.
        /// </summary>
        public void SetFirmwareVersion(double version)
        {
            _fwVersion = version;
            Logger.LogVerbose(ModuleName, $"Device FW Version: {_fwVersion}");
        }

        private void OnPollTimerElapsed(object? sender, ElapsedEventArgs e)
        {
            if (!_connection.IsConnected) return;

            string cmdsToSend;
            // Use TX commands if either PTT is active OR tuning is in progress
            if (_isPtt || _isTuning)
            {
                cmdsToSend = GetNextPollCommand(true);
            }
            else
            {
                cmdsToSend = GetNextPollCommand(false);
                if (_fwVersion == 0.0)
                {
                    cmdsToSend = Constants.FwVersionCmd;
                }
            }

            SendCommand(cmdsToSend);
        }

        private string GetNextPollCommand(bool isTuningOrPtt)
        {
            string command;
            if (isTuningOrPtt)
            {
                command = Constants.TxPollCommands[_txPollIndex];
                _txPollIndex = (_txPollIndex + 1) % Constants.TxPollCommands.Length;
            }
            else
            {
                command = Constants.RxPollCommands[_rxPollIndex];
                _rxPollIndex = (_rxPollIndex + 1) % Constants.RxPollCommands.Length;
            }

            return command;
        }

        private void SendCommand(string message)
        {
            if (string.IsNullOrEmpty(message) && string.IsNullOrEmpty(_priorityCommands))
                return;

            // Check for and send priority commands first
            string? priorityToSend = null;
            lock (_priorityLock)
            {
                if (_priorityCommands.Length > 0)
                {
                    priorityToSend = _priorityCommands;
                    _priorityCommands = string.Empty;
                }
            }

            if (priorityToSend != null)
            {
                _connection.Send(priorityToSend);
            }

            // Send regular polling command
            if (!string.IsNullOrEmpty(message))
            {
                _connection.Send(message);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try { _timerRegistration.Dispose(); } catch { }

            // Cancel any pending initialization
            _initCompletionSource?.TrySetCanceled();

            _connection.DataReceived -= OnDataReceived;

            // Timer disposal per CLAUDE.md: unsubscribe -> stop -> dispose -> null
            if (_initTimer != null)
            {
                _initTimer.Elapsed -= OnInitTimerElapsed;
                _initTimer.Stop();
                _initTimer.Dispose();
                _initTimer = null;
            }

            if (_pollTimer != null)
            {
                _pollTimer.Elapsed -= OnPollTimerElapsed;
                _pollTimer.Stop();
                _pollTimer.Dispose();
                _pollTimer = null;
            }
        }
    }
}
