#nullable enable

using System;
using System.Collections.Generic;
using PgTg.AMP;
using PgTg.Common;
using PgTg.Plugins.Core;
using PgTg.RADIO;
using MeterUnits = PgTg.RADIO.MeterUnits;

namespace SampleTuner.MyModel.Internal
{
    /// <summary>
    /// Tracks current status of the sample tuner device.
    /// Maintains state and detects changes. Thread-safe via lock.
    /// </summary>
    internal class StatusTracker
    {
        private const string ModuleName = "StatusTracker";
        private readonly object _lock = new();

        // Tuner state
        public TunerOperateState TunerState { get; private set; } = TunerOperateState.Unknown;
        public TunerTuningState TuningState { get; private set; } = TunerTuningState.Unknown;
        public int InductorValue { get; private set; }
        public int CapacitorValue { get; private set; }
        public int Antenna { get; private set; }
        public int FanSpeed { get; private set; }      // Fan speed 0–3 (from $FAN; poll)
        public int BandNumber { get; private set; }
        public string BandName { get; private set; } = string.Empty;
        public double SWR { get; private set; } = 1.0;
        public double VFWD { get; set; }
        public int FaultCode { get; private set; }
        public bool RadioPtt { get; private set; }

        public static string GetFaultDescription(int faultCode) => faultCode switch
        {
            0 => string.Empty,
            1 => "RF overload",
            2 => "Temperature fault",
            3 => "Power supply fault",
            _ => $"Fault (code {faultCode})"
        };
        public string SerialNumber { get; private set; } = string.Empty;
        public double FirmwareVersion { get; private set; }
        public bool IsVitaDataPopulated { get; private set; }

        /// <summary>
        /// Convert raw VFWD ADC value to Watts using power-law calibration.
        /// P = 0.000721 * VFWD^1.803
        /// </summary>
        private double ForwardPowerWatts => VFWD > 0 ? Math.Pow(VFWD, 1.803) * 0.000721 : 0.0;

        /// <summary>
        /// Apply a status update from the parser.
        /// </summary>
        public void ApplyUpdate(ResponseParser.StatusUpdate update)
        {
            lock (_lock)
            {
                if (update.TunerState.HasValue) TunerState = update.TunerState.Value;
                if (update.TuningState.HasValue) TuningState = update.TuningState.Value;
                if (update.InductorValue.HasValue) InductorValue = update.InductorValue.Value;
                if (update.CapacitorValue.HasValue) CapacitorValue = update.CapacitorValue.Value;
                if (update.Antenna.HasValue) Antenna = update.Antenna.Value;
                if (update.FanSpeed.HasValue) FanSpeed = update.FanSpeed.Value;
                if (update.BandNumber.HasValue) BandNumber = update.BandNumber.Value;
                if (update.BandName != null) BandName = update.BandName;
                if (update.SWR.HasValue) SWR = update.SWR.Value;
                if (update.VFWD.HasValue) VFWD = update.VFWD.Value;
                if (update.FaultCode.HasValue) FaultCode = update.FaultCode.Value;
                if (update.SerialNumber != null) SerialNumber = update.SerialNumber;
                if (update.FirmwareVersion.HasValue) FirmwareVersion = update.FirmwareVersion.Value;
                if (update.IsVitaDataPopulated) IsVitaDataPopulated = true;
            }
        }

        /// <summary>
        /// Get current tuner status for events.
        /// </summary>
        public TunerStatusData GetTunerStatus()
        {
            lock (_lock)
            {
                return new TunerStatusData
                {
                    OperateState = TunerState,
                    TuningState = TuningState,
                    InductorValue = InductorValue,
                    Capacitor1Value = CapacitorValue,
                    Capacitor2Value = 0,
                    LastSwr = SWR,
                    FirmwareVersion = FirmwareVersion.ToString("F2"),
                    SerialNumber = SerialNumber,
                    ForwardPower = ForwardPowerWatts
                };
            }
        }

        /// <summary>
        /// Get meter readings for VITA-49 sender.
        /// Returns zero values when not transmitting to prevent bouncing meter display in receive.
        /// </summary>
        public Dictionary<MeterType, MeterReading> GetMeterReadings()
        {
            lock (_lock)
            {
                double currentSwr = RadioPtt ? SWR : 1.0;
                double currentFwdPower = RadioPtt ? ForwardPowerWatts : 0;

                var readings = new Dictionary<MeterType, MeterReading>
                {
                    [MeterType.TunerSWR] = new MeterReading(MeterType.TunerSWR, currentSwr, MeterUnits.SWR),
                    [MeterType.TunerForwardPower] = new MeterReading(MeterType.TunerForwardPower, currentFwdPower, MeterUnits.Dbm)
                };

                return readings;
            }
        }

        /// <summary>
        /// Get device data for the /device WebSocket endpoint and Device Control panel.
        /// </summary>
        /// <summary>
        /// Get device data for the /device WebSocket endpoint and Device Control panel.
        /// Every key returned here MUST match a ResponseKey in GetDeviceControlDefinition().
        /// The Controller compares each value (as string, case-insensitive) to the
        /// ActiveValue of the matching LED to decide whether to show ActiveColor or InactiveColor.
        /// </summary>
        public Dictionary<string, object> GetDeviceData()
        {
            lock (_lock)
            {
                return new Dictionary<string, object>
                {
                    // "PS" — Power LED: 1 = tuner is on and responding (green), 0 = off (gray)
                    //   We infer power-on from TunerState: if the tuner is responding with any
                    //   known state, it is considered powered on.
                    ["PS"] = TunerState != TunerOperateState.Unknown ? 1 : 0,

                    // "BYP" — Inline/Bypass LED: 1 = inline (green), 0 = bypass (yellow)
                    //   Populated by $BYP; poll. BYP response: "N" = not-bypassed (inline),
                    //   "B" = bypassed.  See ResponseParser.KeyByp case.
                    ["BYP"] = TunerState == TunerOperateState.Inline ? 1 : 0,

                    // "AN" — Antenna port: integer matching the ActiveValue of each Ant LED
                    //   Populated by $ANT; poll.  Ant1 LED active when AN == "1", etc.
                    ["AN"] = Antenna,

                    // "FLT" — Fault LED: 1 = fault active (red), 0 = no fault (gray)
                    //   Populated by $FLT; poll.
                    ["FLT"] = FaultCode > 0 ? 1 : 0,
                    ["FaultDesc"] = GetFaultDescription(FaultCode),

                    ["BN"] = BandNumber,

                    // "FN" — Fan speed: integer 0–3 fed to the fan control row
                    //         Up/down buttons send $FC0;–$FC3; (FanControlDefinition.SetCommandPrefix = "$FC")
                    ["FN"] = FanSpeed
                };
            }
        }

        /// <summary>
        /// Zero meter values (for shutdown or tune cycle completion).
        /// </summary>
        public void ZeroMeterValues()
        {
            lock (_lock)
            {
                Logger.LogVerbose(ModuleName, "Zeroing tuner meters");
                SWR = 1.0;
                VFWD = 0;
            }
        }

        /// <summary>
        /// Set the radio's PTT state from interlock status.
        /// Called when the radio transitions to/from TRANSMITTING state.
        /// </summary>
        /// <returns>True if the RadioPtt value changed.</returns>
        public bool SetRadioPtt(bool isPtt)
        {
            lock (_lock)
            {
                if (RadioPtt != isPtt)
                {
                    Logger.LogVerbose(ModuleName, $"RadioPtt changed: {RadioPtt} -> {isPtt}");
                    RadioPtt = isPtt;
                    return true;
                }
            }
            return false;
        }
    }
}
