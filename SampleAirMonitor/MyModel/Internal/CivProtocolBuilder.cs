#nullable enable

using System.Collections.Generic;

namespace SampleAirMonitor.MyModel.Internal
{
    /// <summary>
    /// Builds Icom CI-V binary protocol frames for setting
    /// transceiver frequency and mode.
    /// Frame format: FE FE [to] [from] [cmd] [data...] FD
    /// </summary>
    internal static class CivProtocolBuilder
    {
        // Map mode strings to CI-V mode bytes
        private static readonly Dictionary<string, byte> ModeMap = new()
        {
            { "LSB",   Constants.CivModeLsb },
            { "USB",   Constants.CivModeUsb },
            { "AM",    Constants.CivModeAm },
            { "CW",    Constants.CivModeCw },
            { "RTTY",  Constants.CivModeRtty },
            { "FM",    Constants.CivModeFm },
            { "CW-R",  Constants.CivModeCwR },
            { "RTTY-R",Constants.CivModeRttyR },
            { "DATA",  Constants.CivModeUsb },
            { "DATA-R",Constants.CivModeLsb },
        };

        /// <summary>
        /// Build a CI-V set-frequency frame.
        /// Frequency is provided in kHz and converted to Hz, then BCD-encoded (LSB first).
        /// Example: 14060 kHz = 14060000 Hz → FE FE [to] [from] 05 00 00 60 40 01 FD
        /// </summary>
        public static byte[] BuildSetFrequency(int frequencyKhz, byte transceiverAddress, byte controllerAddress)
        {
            long frequencyHz = (long)frequencyKhz * 1000;
            byte[] bcd = FrequencyToBcd(frequencyHz);

            // Frame: FE FE <to> <from> <cmd> <bcd[0..4]> FD
            byte[] frame = new byte[11];
            frame[0] = Constants.CivPreamble;
            frame[1] = Constants.CivPreamble;
            frame[2] = transceiverAddress;
            frame[3] = controllerAddress;
            frame[4] = Constants.CivCmdSetFreq;
            frame[5] = bcd[0]; // 1 Hz, 10 Hz
            frame[6] = bcd[1]; // 100 Hz, 1 kHz
            frame[7] = bcd[2]; // 10 kHz, 100 kHz
            frame[8] = bcd[3]; // 1 MHz, 10 MHz
            frame[9] = bcd[4]; // 100 MHz, 1 GHz
            frame[10] = Constants.CivEndOfMessage;

            return frame;
        }

        /// <summary>
        /// Build a CI-V set-mode frame.
        /// Example: "USB" → FE FE [to] [from] 06 01 FD
        /// </summary>
        /// <returns>The CI-V frame bytes, or null if the mode is not recognized.</returns>
        public static byte[]? BuildSetMode(string mode, byte transceiverAddress, byte controllerAddress)
        {
            string upper = mode.ToUpperInvariant();
            if (!ModeMap.TryGetValue(upper, out byte modeByte))
                return null;

            byte[] frame = new byte[8];
            frame[0] = Constants.CivPreamble;
            frame[1] = Constants.CivPreamble;
            frame[2] = transceiverAddress;
            frame[3] = controllerAddress;
            frame[4] = Constants.CivCmdSetMode;
            frame[5] = modeByte;
            frame[6] = 0x01; // filter width (default normal)
            frame[7] = Constants.CivEndOfMessage;

            return frame;
        }

        /// <summary>
        /// Convert a frequency in Hz to a 5-byte BCD array (LSB first).
        /// Each byte contains two BCD digits.
        /// Byte 0: (10 Hz digit)(1 Hz digit)
        /// Byte 1: (1 kHz digit)(100 Hz digit)
        /// Byte 2: (100 kHz digit)(10 kHz digit)
        /// Byte 3: (10 MHz digit)(1 MHz digit)
        /// Byte 4: (1 GHz digit)(100 MHz digit)
        /// </summary>
        private static byte[] FrequencyToBcd(long frequencyHz)
        {
            byte[] bcd = new byte[5];
            for (int i = 0; i < 5; i++)
            {
                int lowDigit = (int)(frequencyHz % 10);
                frequencyHz /= 10;
                int highDigit = (int)(frequencyHz % 10);
                frequencyHz /= 10;
                bcd[i] = (byte)((highDigit << 4) | lowDigit);
            }
            return bcd;
        }
    }
}
