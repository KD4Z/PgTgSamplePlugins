#nullable enable

using System;
using System.Globalization;
using PgTg.AMP;
using PgTg.Common;
using PgTg.Plugins.Core;

namespace SampleTuner.MyModel.Internal
{
    /// <summary>
    /// Parses $-prefixed CAT responses from the sample tuner device.
    /// Protocol: $KEY value; (e.g., $BYPB; $TPL 1; $SWR 1.23; $FPW 1400;)
    /// </summary>
    internal class ResponseParser
    {
        private const string ModuleName = "ResponseParser";

        /// <summary>
        /// Aggregated status data from parsing one or more responses.
        /// </summary>
        public class StatusUpdate
        {
            // Tuner status
            public TunerOperateState? TunerState { get; set; }
            public TunerTuningState? TuningState { get; set; }
            public int? InductorValue { get; set; }
            public int? CapacitorValue { get; set; }
            public int? Antenna { get; set; }
            public int? BandNumber { get; set; }
            public string? BandName { get; set; }
            public double? SWR { get; set; }
            public int? VFWD { get; set; }     // Forward power ADC value
            public int? FaultCode { get; set; }
            public string? SerialNumber { get; set; }
            public double? FirmwareVersion { get; set; }

            // Change flags
            public bool TunerStateChanged { get; set; }
            public bool TuningStateChanged { get; set; }
            public bool TunerRelaysChanged { get; set; }
            public bool IsVitaDataPopulated { get; set; }
        }

        /// <summary>
        /// Parse a complete response string from the device.
        /// May contain multiple semicolon-delimited responses.
        /// </summary>
        public StatusUpdate Parse(string response, StatusTracker tracker)
        {
            var update = new StatusUpdate();

            if (!response.EndsWith(";"))
                response += ";";

            string[] parts = response.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (string part in parts)
            {
                if (part.Length < 2) continue;

                string trimmed = part.Trim();
                if (!trimmed.StartsWith("$")) continue;

                // Strip $ prefix then split on first space to get key and value
                string content = trimmed.Substring(1);
                int spaceIndex = content.IndexOf(' ');
                string key;
                string value;

                if (spaceIndex >= 0)
                {
                    key = content.Substring(0, spaceIndex).Trim();
                    value = content.Substring(spaceIndex + 1).Trim();
                }
                else
                {
                    key = content.Trim();
                    value = string.Empty;
                }

                if (key.Length == 0) continue;

                ProcessParsedResponse(key, value, update, tracker);
            }

            return update;
        }

        private void ProcessParsedResponse(string key, string value, StatusUpdate update, StatusTracker tracker)
        {
            switch (key)
            {
                case Constants.KeyByp:
                    // BYPB = bypass, BYPN = not bypassed (inline)
                    var tunerState = value == "B" ? TunerOperateState.Bypass : TunerOperateState.Inline;
                    if (tracker.TunerState != tunerState)
                    {
                        update.TunerState = tunerState;
                        update.TunerStateChanged = true;
                    }
                    break;

                case Constants.KeyTpl:
                    // TPL 1 = tuning in progress, TPL 0 = idle
                    var tuningState = value == "1" ? TunerTuningState.TuningInProgress : TunerTuningState.NotTuning;
                    if (tracker.TuningState != tuningState)
                    {
                        update.TuningState = tuningState;
                        update.TuningStateChanged = true;
                    }
                    break;

                case Constants.KeySwr:
                    // Format: "n.nn"
                    if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double swr))
                    {
                        if (swr < 1.0) swr = 1.0;
                        update.SWR = swr;
                        update.IsVitaDataPopulated = true;
                    }
                    break;

                case Constants.KeyFpw:
                    // Forward power ADC value (integer)
                    if (int.TryParse(value, out int vfwd))
                    {
                        update.VFWD = vfwd;
                        update.IsVitaDataPopulated = true;
                    }
                    break;

                case Constants.KeyInd:
                    // Inductor relay value (hex)
                    if (int.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int inductors))
                    {
                        if (tracker.InductorValue != inductors)
                        {
                            update.InductorValue = inductors;
                            update.TunerRelaysChanged = true;
                        }
                    }
                    break;

                case Constants.KeyCap:
                    // Capacitor relay value (hex)
                    if (int.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int capacitors))
                    {
                        if (tracker.CapacitorValue != capacitors)
                        {
                            update.CapacitorValue = capacitors;
                            update.TunerRelaysChanged = true;
                        }
                    }
                    break;

                case Constants.KeyMde:
                    // Mode: A=auto, M=manual
                    // Auto mode maps to Inline (active matching), Manual maps to Bypass (pass-through control)
                    // We track this as TunerState for display purposes only when BYP has not updated it
                    break;

                case Constants.KeyBnd:
                    if (int.TryParse(value, out int band))
                    {
                        update.BandNumber = band;
                        update.BandName = Constants.LookupBandName(band);
                    }
                    break;

                case Constants.KeyAnt:
                    if (int.TryParse(value, out int ant))
                        update.Antenna = ant;
                    break;

                case Constants.KeyFlt:
                    if (int.TryParse(value, out int fault))
                        update.FaultCode = fault;
                    break;

                case Constants.KeyVer:
                    if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double ver))
                        update.FirmwareVersion = ver;
                    break;

                case Constants.KeySer:
                    update.SerialNumber = value.Trim();
                    break;

                case Constants.KeyIdn:
                    // Identity response (e.g., "SAMP-T"), not used after initialization
                    break;

                default:
                    Logger.LogVerbose(ModuleName, $"Unknown response key: {key}");
                    break;
            }
        }
    }
}
