#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using PgTg.AMP;
using PgTg.Common;
using Timer = System.Timers.Timer;

namespace SampleAmpTuner.MyModel.Internal
{
    /// <summary>
    /// Manages command polling and priority command insertion for the sample combined amplifier+tuner device.
    /// Combines amp methods (SendPriorityCommand, SetOperateMode, ForceReleasesPtt) with
    /// tuner methods (SetTunerInline, SetTuneStart, OnTuningStateChanged).
    /// Fast polling when PTT is active or tuning is in progress.
    /// </summary>
    internal class CommandQueue : IDisposable
    {
        private const string ModuleName = "CommandQueue";

        private readonly ISampleAmpTunerConnection _connection;
        private readonly object _priorityLock = new();

        private Timer? _pollTimer;
        private Timer? _pttWatchdogTimer;
        private Timer? _initTimer;
        private CancellationTokenRegistration _timerRegistration;

        private string _priorityCommands = string.Empty;
        private int _rxPollIndex;
        private int _txPollIndex;
        private bool _isPtt;
        private bool _isTuning;
        private bool _waitingForTxAck;
        private bool _pttInProgress;
        private double _fwVersion;
        private bool _disposed;
        private bool _isInitialized;
        private bool _initializationInProgress;
        private bool _initRetryLogged;
        private TaskCompletionSource<bool>? _initCompletionSource;

        // Configuration
        private int _pollingRxMs = Constants.PollingRxMs;
        private int _pollingTxMs = Constants.PollingTxMs;
        private int _pttWatchdogMs = Constants.PttWatchdogMs;
        private const int InitRetryIntervalMs = 500;

        /// <summary>
        /// Current PTT state.
        /// </summary>
        public bool IsPtt => _isPtt;

        /// <summary>
        /// Whether waiting for TX acknowledgment from device.
        /// </summary>
        public bool WaitingForTxAck => _waitingForTxAck;

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

        public CommandQueue(ISampleAmpTunerConnection connection, CancellationToken cancellationToken)
        {
            _connection = connection;

            // Subscribe to DataReceived for initialization handling
            _connection.DataReceived += OnDataReceived;

            // Register token to stop timers on cancel and unblock any pending initialization
            _timerRegistration = cancellationToken.Register(() =>
            {
                _initTimer?.Stop();
                _pollTimer?.Stop();
                _pttWatchdogTimer?.Stop();
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
        public void Configure(int pollingRxMs, int pollingTxMs, int pttWatchdogMs)
        {
            _pollingRxMs = pollingRxMs;
            _pollingTxMs = pollingTxMs;
            _pttWatchdogMs = pttWatchdogMs;
        }

        /// <summary>
        /// Start polling timers. Performs device initialization first if enabled.
        /// </summary>
        /// <returns>Task that completes when device initialization is done.</returns>
        public async Task StartAsync()
        {
            _pollTimer = new Timer { Interval = _pollingRxMs };
            _pollTimer.Elapsed += OnPollTimerElapsed;

            _pttWatchdogTimer = new Timer { Interval = _pttWatchdogMs };
            _pttWatchdogTimer.Elapsed += OnPttWatchdogTimerElapsed;
            // Watchdog starts when PTT is activated

            if (Constants.DeviceInitializationEnabled && !SkipDeviceWakeup)
            {
                // Send wake-up sequence and wait for initialization to complete
                await InitializeDeviceAsync();
            }
            else
            {
                if (SkipDeviceWakeup)
                    Logger.LogVerbose(ModuleName, "Skipping device initialization (AmpWakeupMode != 1)");
                else
                    Logger.LogVerbose(ModuleName, "Device initialization disabled, starting normal polling immediately");
                _pollTimer?.Start();
            }
        }

        /// <summary>
        /// Sends wake-up commands to initialize the device.
        /// Normal polling begins after receiving the expected response.
        /// Called from StartAsync (mode 1) or WakeupDeviceAsync (mode 2).
        /// </summary>
        /// <returns>Task that completes when device responds.</returns>
        public async Task InitializeDeviceAsync()
        {
            _isInitialized = false;
            _initializationInProgress = true;
            _initCompletionSource = new TaskCompletionSource<bool>();

            // Send wake-up command followed by identify command
            string initSequence = Constants.WakeUpCmd + Constants.IdentifyCmd;
            _connection.Send(initSequence);
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

            // Resend wake-up command to initialize device
            _connection.Send(Constants.WakeUpCmd);
            if (!_initRetryLogged)
            {
                Logger.LogVerbose(ModuleName, "Resending wake-up command for device initialization");
                _initRetryLogged = true;
            }
        }

        /// <summary>
        /// Called when a response is received during initialization.
        /// Initialization completes when any semicolon-terminated response arrives.
        /// </summary>
        /// <param name="response">The response received from the device.</param>
        /// <returns>True if initialization is complete.</returns>
        public bool OnInitializationResponse(string response)
        {
            if (!_initializationInProgress)
                return _isInitialized;

            if (response.Contains(';'))
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
        /// Stop polling timers.
        /// </summary>
        public void Stop()
        {
            _initTimer?.Stop();
            _pollTimer?.Stop();
            _pttWatchdogTimer?.Stop();
        }

        /// <summary>
        /// Send a priority command immediately (bypasses polling queue).
        /// </summary>
        public void SendPriorityCommand(AmpCommand command, AmpOperateState currentState)
        {
            string sendNowCommand;
            switch (command)
            {
                case AmpCommand.RX:
                    _pttWatchdogTimer?.Stop();
                    _pttInProgress = false;
                    _waitingForTxAck = false;

                    sendNowCommand = Constants.PttOffCmd;

                    // Send RX command immediately for lowest latency
                    _connection.Send(sendNowCommand);
                    Logger.LogVerbose(ModuleName, $"Priority RX command sent: {sendNowCommand}");
                    break;

                case AmpCommand.TX:
                case AmpCommand.TXforTuneCarrier:
                    _waitingForTxAck = true;
                    sendNowCommand = Constants.PttOnCmd;

                    // Immediately switch to fast polling when waiting for TX ack
                    if (_pollTimer != null && _pollTimer.Interval != _pollingTxMs)
                    {
                        _pollTimer.Stop();
                        _pollTimer.Interval = _pollingTxMs;
                        _pollTimer.Start();
                    }

                    // Send TX command immediately for lowest latency
                    _connection.Send(sendNowCommand);
                    _pttWatchdogTimer?.Start();
                    _pttInProgress = true;
                    Logger.LogVerbose(ModuleName, $"Priority TX command sent: {sendNowCommand}");
                    break;
            }
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
            // This ensures fast polling (15ms) for the entire tune cycle
            if (start)
            {
                OnTuningStateChanged(true);
            }
            else
            {
                OnTuningStateChanged(false);
            }
        }

        /// <summary>
        /// Queue operate/standby command.
        /// </summary>
        public void SetOperateMode(bool operate)
        {
            lock (_priorityLock)
            {
                _priorityCommands = operate
                    ? Constants.ClearFaultCmd + Constants.OperateCmd
                    : Constants.StandbyCmd;
            }
            Logger.LogVerbose(ModuleName, $"Queued {(operate ? "OPERATE" : "STANDBY")} command");
        }

        /// <summary>
        /// Send frequency change command directly to device.
        /// </summary>
        public void SetFrequencyKhz(int frequencyKhz)
        {
            string txCommand = $"{Constants.SetFreqKhzCmdPrefix}{frequencyKhz:D5};";
            _connection.Send(txCommand);
        }

        /// <summary>
        /// Called when TX/RX response received from device.
        /// </summary>
        public void OnTxRxResponseReceived(bool isTx)
        {
            if (_disposed) return;

            _waitingForTxAck = false;
            _isPtt = isTx;

            // Adjust polling rate when TX acknowledgment received
            if (_pollTimer != null)
            {
                bool needsFastPolling = _isPtt || _isTuning || _waitingForTxAck;
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
        /// Called when PTT state detected via polling.
        /// </summary>
        public void OnPttStateChanged(bool isPtt)
        {
            if (_disposed) return;

            _isPtt = isPtt;

            // Adjust polling rate based on PTT state
            // Use fast polling when PTT is active OR tuning OR waiting for TX ack
            if (_pollTimer != null)
            {
                bool needsFastPolling = _isPtt || _isTuning || _waitingForTxAck;
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
            // Use fast polling when tuning OR PTT is active OR waiting for TX ack
            if (_pollTimer != null)
            {
                bool needsFastPolling = _isTuning || _isPtt || _waitingForTxAck;
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
        /// Set firmware version (for informational tracking).
        /// </summary>
        public void SetFirmwareVersion(double version)
        {
            _fwVersion = version;
            Logger.LogVerbose(ModuleName, $"Device FW Version: {_fwVersion}");
        }

        /// <summary>
        /// Force release of PTT (safety measure when radio disconnects).
        /// </summary>
        public void ForceReleasesPtt()
        {
            if (_pttInProgress)
            {
                _pttWatchdogTimer?.Stop();
                _pttInProgress = false;
                _waitingForTxAck = false;
                SendPriorityCommand(AmpCommand.RX, AmpOperateState.Unknown);
                Logger.LogVerbose(ModuleName, "Forced device to RX (Safety Measure)");
            }
        }

        private void OnPollTimerElapsed(object? sender, ElapsedEventArgs e)
        {
            if (!_connection.IsConnected) return;

            string cmdsToSend;
            bool needsFastPolling = _isPtt || _isTuning || _waitingForTxAck;

            if (needsFastPolling)
            {
                cmdsToSend = GetNextPollCommand(true);
            }
            else
            {
                cmdsToSend = GetNextPollCommand(false);
                if (_fwVersion == 0.0)
                {
                    cmdsToSend = Constants.IdentifyCmd;
                    Logger.LogVerbose(ModuleName, "Requesting device firmware version");
                }
            }

            SendCommand(cmdsToSend);
        }

        private void OnPttWatchdogTimerElapsed(object? sender, ElapsedEventArgs e)
        {
            if (!_connection.IsConnected)
            {
                _pttWatchdogTimer?.Stop();
                return;
            }

            if (_pttInProgress)
            {
                // Re-send TX command to keep PTT alive (must be < 15s device timeout)
                lock (_priorityLock)
                {
                    _priorityCommands = Constants.PttOnCmd;
                }
            }
        }

        private string GetNextPollCommand(bool isFastPolling)
        {
            if (_waitingForTxAck)
            {
                return string.Empty;
            }

            string command;
            if (isFastPolling)
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
                Logger.LogVerbose(ModuleName, $"Sending priority command: {priorityToSend}");
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

            if (_pttWatchdogTimer != null)
            {
                _pttWatchdogTimer.Elapsed -= OnPttWatchdogTimerElapsed;
                _pttWatchdogTimer.Stop();
                _pttWatchdogTimer.Dispose();
                _pttWatchdogTimer = null;
            }
        }
    }
}
