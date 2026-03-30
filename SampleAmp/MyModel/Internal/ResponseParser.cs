#nullable enable

using System;
using System.Globalization;
using PgTg.AMP;
using PgTg.Common;

namespace SampleAmp.MyModel.Internal
{
    /// <summary>
    /// Parses $ -prefixed CAT responses from the sample amplifier device.
    /// Protocol: $KEY value; (e.g., $PWR 500 12; $TMP 45; $OPR 1;)
    /// </summary>
    internal class ResponseParser
    {
        private const string ModuleName = "ResponseParser";

        /// <summary>
        /// Aggregated status data from parsing one or more responses.
        /// </summary>
        public class StatusUpdate
        {
            // Amplifier status
            public AmpOperateState? AmpState { get; set; }
            public bool? IsPtt { get; set; }
            public double? ForwardPower { get; set; }
            public double? SWR { get; set; }
            public double? ReturnLoss { get; set; }
            public int? Temperature { get; set; }
            public double? Voltage { get; set; }
            public double? Current { get; set; }
            public int? BandNumber { get; set; }
            public string? BandName { get; set; }
            public int? FaultCode { get; set; }
            public string? SerialNumber { get; set; }
            public double? FirmwareVersion { get; set; }

            // Device Control state — match the ResponseKey fields in GetDeviceControlDefinition()
            public int? Antenna { get; set; }   // Antenna port number; maps to ResponseKey "AN"

            // Change flags
            public bool AmpStateChanged { get; set; }
            public bool PttStateChanged { get; set; }
            public bool PttReady { get; set; }
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
                case Constants.KeyTx:
                    update.IsPtt = true;
                    update.PttReady = !tracker.IsPtt;
                    if (!tracker.IsPtt)
                        update.PttStateChanged = true;
                    break;

                case Constants.KeyRx:
                    update.IsPtt = false;
                    if (tracker.IsPtt)
                        update.PttStateChanged = true;
                    break;

                case Constants.KeyPwr:
                    // Format: "ppp sss" where ppp=forward power (watts), sss=SWR*10
                    ParsePowerSwr(value, update);
                    break;

                case Constants.KeyOpr:
                    var osState = value == "1" ? AmpOperateState.Operate : AmpOperateState.Standby;
                    if (tracker.AmpState != osState)
                    {
                        update.AmpState = osState;
                        update.AmpStateChanged = true;
                    }
                    break;

                case Constants.KeyTmp:
                    if (int.TryParse(value, out int temp))
                    {
                        update.Temperature = temp;
                        update.IsVitaDataPopulated = true;
                    }
                    break;

                case Constants.KeyVlt:
                    // Format: "vvv iii" where vvv=voltage*10, iii=current*10
                    ParseVoltageCurrent(value, update);
                    break;

                case Constants.KeyBnd:
                    if (int.TryParse(value, out int band))
                    {
                        update.BandNumber = band;
                        update.BandName = Constants.LookupBandName(band);
                    }
                    break;

                case Constants.KeyAnt:
                    // Response: $ANT n; where n = 1 or 2
                    // Drives the Ant1 / Ant2 LED indicators in Device Control.
                    // Only the matching antenna LED lights green; the other shows gray.
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
                    update.SerialNumber = value;
                    break;

                case Constants.KeyIdn:
                    // Identity response (e.g., "SAMP500"), not used after initialization
                    break;

                default:
                    Logger.LogVerbose(ModuleName, $"Unknown response key: {key}");
                    break;
            }
        }

        private void ParsePowerSwr(string value, StatusUpdate update)
        {
            // Format: "ppp sss" where ppp=power in watts, sss=SWR*10
            var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
            {
                if (int.TryParse(parts[0], out int power))
                {
                    update.ForwardPower = power;
                    update.IsVitaDataPopulated = true;
                }

                if (int.TryParse(parts[1], out int swrRaw))
                {
                    double swr = swrRaw / 10.0;
                    if (swr < 1.0) swr = 1.0;
                    update.SWR = swr;
                    update.ReturnLoss = SwrToReturnLoss(swr);
                    update.IsVitaDataPopulated = true;
                }
            }
            else if (parts.Length == 1)
            {
                if (int.TryParse(parts[0], out int power))
                {
                    update.ForwardPower = power;
                    update.IsVitaDataPopulated = true;
                }
            }
        }

        private void ParseVoltageCurrent(string value, StatusUpdate update)
        {
            // Format: "vvv iii" where vvv=voltage*10, iii=current*10
            var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
            {
                if (double.TryParse(parts[0], out double voltage))
                    update.Voltage = voltage / 10.0;

                if (double.TryParse(parts[1], out double current))
                    update.Current = current / 10.0;
            }
        }

        /// <summary>
        /// Convert SWR to return loss in dB.
        /// RL = -20 * log10((SWR-1)/(SWR+1))
        /// </summary>
        private static double SwrToReturnLoss(double swr)
        {
            if (swr <= 1.0) return 99.0;
            double rho = (swr - 1.0) / (swr + 1.0);
            if (rho <= 0) return 99.0;
            return Math.Round(-20.0 * Math.Log10(rho), 1);
        }
    }
}
