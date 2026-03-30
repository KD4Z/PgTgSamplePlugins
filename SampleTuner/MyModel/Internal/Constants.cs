#nullable enable

namespace SampleTuner.MyModel.Internal
{
    /// <summary>
    /// Constants for the sample tuner device commands and timing parameters.
    /// Protocol: $ prefix, ; terminator, space-separated values.
    /// Device ID: SAMP-T
    /// </summary>
    internal static class Constants
    {
        // Used by Controller Meter display for scale
        public const double MeterDisplayMaxPower = 600;

        #region CAT Command Strings

        // Device initialization
        public const string WakeUpCmd = "$WKP;";
        public const string ShutdownCmd = "$SDN;";        // Full power-off command
        public const string IdentifyCmd = "$IDN;";
        public const string IdentifyResponse = "SAMP-T";
        public static readonly bool DeviceInitializationEnabled = false;  // Demonstrates alternate path

        // Bypass/Inline commands
        public const string BypassCmd = "$BYPB;";      // Set bypass mode
        public const string InlineCmd = "$BYPN;";      // Set inline (not bypassed)

        // Tune commands
        public const string TuneStartCmd = "$TUN;";    // Start tune cycle
        public const string TuneStopCmd = "$TUS;";     // Stop/cancel tune cycle

        // Query commands
        public const string BypassQueryCmd = "$BYP;";  // Query bypass state
        public const string TunePollCmd = "$TPL;";     // Tune poll (0=idle, 1=tuning)
        public const string SwrQueryCmd = "$SWR;";     // Current SWR
        public const string FwdPwrQueryCmd = "$FPW;";  // Forward power ADC value
        public const string InductorQueryCmd = "$IND;"; // Inductor relay (hex)
        public const string CapacitorQueryCmd = "$CAP;"; // Capacitor relay (hex)
        public const string ModeQueryCmd = "$MDE;";    // Mode: A=auto, M=manual
        public const string BandQueryCmd = "$BND;";    // Band number
        public const string FaultQueryCmd = "$FLT;";   // Fault code
        public const string FwVersionCmd = "$VER;";    // Firmware version
        public const string SerialNumberCmd = "$SER;"; // Serial number

        // Mode set commands
        public const string ModeAutoCmd = "$MDA;";     // Set auto mode
        public const string ModeManualCmd = "$MDM;";   // Set manual mode

        // Antenna selection commands
        public const string Antenna1Cmd = "$ANT 1;";
        public const string Antenna2Cmd = "$ANT 2;";
        public const string Antenna3Cmd = "$ANT 3;";

        // Frequency command prefix (append kHz value, 5 digits, semicolon)
        // Format: $FRQ nnnnn; (e.g., $FRQ 14250; for 14.25 MHz)
        public const string SetFreqKhzCmdPrefix = "$FRQ ";

        #endregion

        #region Polling Command Arrays

        /// <summary>
        /// Commands polled during receive (not tuning).
        /// </summary>
        public static readonly string[] RxPollCommands =
        {
            "$BYP;",    // Bypass/Inline status — populates BYP key → Inline/Bypass LED
            "$TPL;",    // Tune poll (0=idle, 1=tuning)
            "$SWR;",    // Current SWR — populates tuner SWR meter
            "$FPW;",    // Forward power ADC — populates tuner power meter
            "$IND;",    // Inductor relay (hex)
            "$CAP;",    // Capacitor relay (hex)
            "$MDE;",    // Mode (A=auto, M=manual)
            "$ANT;",    // Antenna selection — populates AN key → Ant1/Ant2 LEDs
            "$BND;",    // Band number
            "$FLT;",    // Fault code — populates FLT key → Fault LED
        };

        /// <summary>
        /// Commands polled during transmit/tuning (faster polling).
        /// </summary>
        public static readonly string[] TxPollCommands =
        {
            "$SWR;",    // Current SWR
            "$FPW;",    // Forward power ADC
            "$TPL;",    // Tune poll
        };

        #endregion

        #region Timing Constants

        /// <summary>
        /// Polling interval during receive in milliseconds.
        /// </summary>
        public const int PollingRxMs = 100;

        /// <summary>
        /// Polling interval during transmit/tuning in milliseconds.
        /// </summary>
        public const int PollingTxMs = 10;

        /// <summary>
        /// Default tune timeout in milliseconds.
        /// </summary>
        public const int TuneTimeoutMs = 30000;

        #endregion

        #region CAT Response Keys

        // Response keys parsed from tuner responses (after stripping $ prefix)
        public const string KeyByp = "BYP";     // Bypass status
        public const string KeyTpl = "TPL";     // Tune poll
        public const string KeySwr = "SWR";     // Current SWR
        public const string KeyFpw = "FPW";     // Forward power ADC
        public const string KeyInd = "IND";     // Inductor relay
        public const string KeyCap = "CAP";     // Capacitor relay
        public const string KeyMde = "MDE";     // Mode
        public const string KeyBnd = "BND";     // Band number
        public const string KeyFlt = "FLT";     // Fault code
        public const string KeyVer = "VER";     // Firmware version
        public const string KeySer = "SER";     // Serial number
        public const string KeyIdn = "IDN";     // Device identity
        public const string KeyAnt = "ANT";     // Antenna

        #endregion

        #region Band Mapping

        /// <summary>
        /// Map band number to band name string.
        /// </summary>
        public static string LookupBandName(int bandNumber)
        {
            return bandNumber switch
            {
                0 => "160m",
                1 => "80m",
                2 => "60m",
                3 => "40m",
                4 => "30m",
                5 => "20m",
                6 => "17m",
                7 => "15m",
                8 => "12m",
                9 => "10m",
                10 => "6m",
                _ => "Unknown"
            };
        }

        #endregion
    }
}
