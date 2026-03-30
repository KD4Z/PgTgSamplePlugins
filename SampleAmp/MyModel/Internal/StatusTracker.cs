#nullable enable

using System;
using System.Collections.Generic;
using PgTg.AMP;
using PgTg.Common;
using PgTg.Plugins.Core;
using PgTg.RADIO;

namespace SampleAmp.MyModel.Internal
{
    /// <summary>
    /// Tracks current status of the sample amplifier device.
    /// Maintains state and detects changes. Thread-safe via lock.
    /// </summary>
    internal class StatusTracker
    {
        private const string ModuleName = "StatusTracker";
        private readonly object _lock = new();

        // Amplifier state
        public AmpOperateState AmpState { get; private set; } = AmpOperateState.Unknown;
        public bool IsPtt { get; private set; }

        /// <summary>
        /// Radio's PTT state from interlock status (TRANSMITTING).
        /// May lead the device's PTT detection, especially with hardware keying.
        /// </summary>
        public bool RadioPtt { get; private set; }

        public double ForwardPower { get; private set; }
        public double SWR { get; private set; } = 1.0;
        public double ReturnLoss { get; private set; } = 99;
        public int Temperature { get; private set; }
        public double Voltage { get; private set; }
        public double Current { get; private set; }
        public int Antenna { get; private set; }       // Current antenna port (1 or 2)
        public int BandNumber { get; private set; }
        public string BandName { get; private set; } = string.Empty;
        public int FaultCode { get; private set; }
        public string SerialNumber { get; private set; } = string.Empty;
        public double FirmwareVersion { get; private set; }
        public bool IsVitaDataPopulated { get; private set; }

        /// <summary>
        /// Apply a status update from the parser.
        /// </summary>
        public void ApplyUpdate(ResponseParser.StatusUpdate update)
        {
            lock (_lock)
            {
                if (update.AmpState.HasValue) AmpState = update.AmpState.Value;
                if (update.IsPtt.HasValue) IsPtt = update.IsPtt.Value;
                if (update.ForwardPower.HasValue) ForwardPower = update.ForwardPower.Value;
                if (update.SWR.HasValue) SWR = update.SWR.Value;
                if (update.ReturnLoss.HasValue) ReturnLoss = update.ReturnLoss.Value;
                if (update.Temperature.HasValue) Temperature = update.Temperature.Value;
                if (update.Voltage.HasValue) Voltage = update.Voltage.Value;
                if (update.Current.HasValue) Current = update.Current.Value;
                if (update.Antenna.HasValue) Antenna = update.Antenna.Value;
                if (update.BandNumber.HasValue) BandNumber = update.BandNumber.Value;
                if (update.BandName != null) BandName = update.BandName;
                if (update.FaultCode.HasValue) FaultCode = update.FaultCode.Value;
                if (update.SerialNumber != null) SerialNumber = update.SerialNumber;
                if (update.FirmwareVersion.HasValue) FirmwareVersion = update.FirmwareVersion.Value;
                if (update.IsVitaDataPopulated) IsVitaDataPopulated = true;
            }
        }

        /// <summary>
        /// Get current amplifier status for events.
        /// </summary>
        public AmplifierStatusData GetAmplifierStatus()
        {
            lock (_lock)
            {
                return new AmplifierStatusData
                {
                    OperateState = AmpState,
                    IsPttActive = IsPtt,
                    BandNumber = BandNumber,
                    BandName = BandName,
                    FaultCode = FaultCode,
                    FirmwareVersion = FirmwareVersion.ToString("F2"),
                    SerialNumber = SerialNumber,
                    ForwardPower = ForwardPower,
                    SWR = SWR,
                    ReturnLoss = ReturnLoss,
                    Temperature = Temperature
                };
            }
        }

        /// <summary>
        /// Get meter readings for VITA-49 sender.
        /// Returns zero values for power/SWR when not transmitting to prevent frozen meter display.
        /// Uses RadioPtt OR IsPtt to determine transmit state.
        /// </summary>
        public Dictionary<MeterType, MeterReading> GetMeterReadings()
        {
            lock (_lock)
            {
                // Use RadioPtt (from radio interlock) OR IsPtt (from device) to determine if transmitting.
                // RadioPtt may be true before device IsPtt is detected (especially with hardware keying).
                bool isTransmitting = RadioPtt || IsPtt;

                // Use current values if transmitting, otherwise force zeros to prevent meter freeze
                double currentFwdPower = isTransmitting ? ForwardPower : 0;
                double currentSwr = isTransmitting ? SWR : 1.0;
                double currentReturnLoss = isTransmitting ? ReturnLoss : 99;

                var readings = new Dictionary<MeterType, MeterReading>
                {
                    [MeterType.ForwardPower] = new MeterReading(MeterType.ForwardPower, currentFwdPower, MeterUnits.Watts),
                    [MeterType.SWR] = new MeterReading(MeterType.SWR, currentSwr, MeterUnits.SWR),
                    [MeterType.ReturnLoss] = new MeterReading(MeterType.ReturnLoss, currentReturnLoss, MeterUnits.Db),
                    [MeterType.Temperature] = new MeterReading(MeterType.Temperature, Temperature, MeterUnits.DegreesC)
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
                    // "ON" — Power LED: 1 = power on (green), 0 = power off (gray)
                    ["ON"] = AmpState != AmpOperateState.Unknown && AmpState != AmpOperateState.Standby ? 1 : 0,

                    // "OS" — Operate/Standby LED: 1 = operate (green), 0 = standby (yellow)
                    ["OS"] = AmpState == AmpOperateState.Operate || AmpState == AmpOperateState.Transmit ? 1 : 0,

                    // "AN" — Antenna port: integer matching the ActiveValue of each Ant LED
                    //         Ant1 LED is green when AN == "1", gray otherwise
                    //         Ant2 LED is green when AN == "2", gray otherwise
                    ["AN"] = Antenna,

                    // "FL" — Fault LED: 1 = fault active (red), 0 = no fault (gray)
                    //         Clicking the fault LED when active sends ClearFaultCmd
                    ["FL"] = FaultCode > 0 ? 1 : 0,

                    ["BN"] = BandNumber
                };
            }
        }

        /// <summary>
        /// Zero meter values (for shutdown).
        /// </summary>
        public void ZeroMeterValues()
        {
            lock (_lock)
            {
                ForwardPower = 0;
                SWR = 1.0;
                ReturnLoss = 99;
                Temperature = 0;
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
