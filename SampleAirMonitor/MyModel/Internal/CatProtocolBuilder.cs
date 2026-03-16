#nullable enable

using System.Collections.Generic;

namespace SampleAirMonitor.MyModel.Internal
{
    /// <summary>
    /// Builds CAT (Kenwood/Elecraft) protocol commands for setting
    /// transceiver frequency and mode via ASCII text commands.
    /// Format: FAxxxxxxxxxxx; (11-digit Hz) and MDn; (mode digit).
    /// </summary>
    internal static class CatProtocolBuilder
    {
        // Map mode strings to CAT mode digits
        private static readonly Dictionary<string, char> ModeMap = new()
        {
            { "LSB",   Constants.CatModeLsb },
            { "USB",   Constants.CatModeUsb },
            { "CW",    Constants.CatModeCw },
            { "FM",    Constants.CatModeFm },
            { "AM",    Constants.CatModeAm },
            { "CW-R",  Constants.CatModeCwR },
            { "RTTY",  Constants.CatModeRtty },
            { "RTTY-R",Constants.CatModeRttyR },
            { "DATA",  Constants.CatModeUsb },
            { "DATA-R",Constants.CatModeLsb },
        };

        /// <summary>
        /// Build a CAT set-frequency command string.
        /// Frequency is provided in Hz for the 11-digit format.
        /// Example: 14060000 Hz → "FA00014060000;"
        /// </summary>
        public static string BuildSetFrequency(int frequencyHz)
        {
            return Constants.CatSetFreqPrefix +
                   frequencyHz.ToString().PadLeft(Constants.CatFreqDigits, '0') + ";";
        }

        /// <summary>
        /// Build a CAT set-mode command string.
        /// Example: "USB" → "MD2;"
        /// </summary>
        /// <returns>The command string, or null if the mode is not recognized.</returns>
        public static string? BuildSetMode(string mode)
        {
            string upper = mode.ToUpperInvariant();
            if (ModeMap.TryGetValue(upper, out char digit))
            {
                return Constants.CatSetModePrefix + digit + ";";
            }
            return null;
        }
    }
}
