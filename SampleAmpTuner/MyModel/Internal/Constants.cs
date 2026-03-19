#nullable enable

namespace SampleAmpTuner.MyModel.Internal
{
    /// <summary>
    /// Constants for the sample combined amplifier+tuner device commands and timing parameters.
    /// Protocol: $ prefix, ; terminator, space-separated values.
    /// Device ID: SAMP1500
    /// </summary>
    internal static class Constants
    {
        // Used by Controller Meter display for scale
        public const double MeterDisplayMaxPower = 1500;

        #region CAT Command Strings

        // Device initialization commands
        public const string WakeUpCmd = "$WKP;";
        public const string ShutdownCmd = "$SDN;";        // Full power-off command
        public const string IdentifyCmd = "$IDN;";
        public const string IdentifyResponse = "SAMP1500";
        public static readonly bool DeviceInitializationEnabled = true;

        // Operate/Standby commands (amp)
        public const string OperateCmd = "$OPR1;";
        public const string StandbyCmd = "$OPR0;";

        // PTT commands (amp)
        public const string PttOnCmd = "$TX15;";     // Key PTT with 15 sec timeout
        public const string PttOffCmd = "$RX;";      // Release PTT

        // Fault commands (amp)
        public const string ClearFaultCmd = "$FLC;";

        // Frequency command prefix (append kHz value, 5 digits, semicolon)
        public const string SetFreqKhzCmdPrefix = "$FRQ";

        // Tuner bypass/inline commands
        public const string BypassCmd = "$BYPB;";      // Set bypass mode
        public const string InlineCmd = "$BYPN;";      // Set inline (not bypassed)

        // Tuner tune commands
        public const string TuneStartCmd = "$TUN;";    // Start tune cycle
        public const string TuneStopCmd = "$TUS;";     // Stop/cancel tune cycle

        // Antenna selection commands
        public const string Antenna1Cmd = "$ANT 1;";
        public const string Antenna2Cmd = "$ANT 2;";
        public const string Antenna3Cmd = "$ANT 3;";

        #endregion

        #region Polling Command Arrays

        /// <summary>
        /// Commands polled during receive (not transmitting or tuning).
        /// Combined amp + tuner queries.
        /// </summary>
        public static readonly string[] RxPollCommands =
        {
            "$PWR;",    // Power/SWR (amp)
            "$TMP;",    // Temperature (amp)
            "$OPR;",    // Operate/Standby (amp)
            "$BND;",    // Band number
            "$VLT;",    // Voltage/Current (amp)
            "$BYP;",    // Bypass status (tuner)
            "$TPL;",    // Tune poll (tuner)
            "$SWR;",    // Current SWR (tuner)
            "$FPW;",    // Forward power ADC (tuner)
            "$IND;",    // Inductor relay (tuner)
            "$CAP;",    // Capacitor relay (tuner)
            "$FLT;",    // Fault code
        };

        /// <summary>
        /// Commands polled during transmit or tuning (fast polling for metering).
        /// </summary>
        public static readonly string[] TxPollCommands =
        {
            "$PWR;",    // Power/SWR (amp - primary meter)
            "$TMP;",    // Temperature (amp)
            "$SWR;",    // Current SWR (tuner)
            "$FPW;",    // Forward power ADC (tuner)
            "$TPL;",    // Tune poll (detects tune completion)
        };

        #endregion

        #region Timing Constants

        /// <summary>
        /// PTT watchdog refresh interval in milliseconds.
        /// Must be less than the PTT timeout (15 seconds).
        /// </summary>
        public const int PttWatchdogMs = 10000;

        /// <summary>
        /// Polling interval during receive in milliseconds.
        /// </summary>
        public const int PollingRxMs = 150;

        /// <summary>
        /// Polling interval during transmit in milliseconds.
        /// Fast polling for responsive metering.
        /// </summary>
        public const int PollingTxMs = 15;

        #endregion

        #region CAT Response Keys

        // Amplifier response keys
        public const string KeyTx = "TX";
        public const string KeyRx = "RX";
        public const string KeyPwr = "PWR";     // Power/SWR
        public const string KeyOpr = "OPR";     // Operate/standby
        public const string KeyTmp = "TMP";     // Temperature
        public const string KeyVlt = "VLT";     // Voltage/Current
        public const string KeyBnd = "BND";     // Band number
        public const string KeyFlt = "FLT";     // Fault code
        public const string KeyVer = "VER";     // Firmware version
        public const string KeySer = "SER";     // Serial number
        public const string KeyIdn = "IDN";     // Device identity

        // Tuner response keys
        public const string KeyByp = "BYP";     // Bypass status
        public const string KeyTpl = "TPL";     // Tune poll
        public const string KeySwr = "SWR";     // Current SWR (tuner)
        public const string KeyFpw = "FPW";     // Forward power ADC (tuner)
        public const string KeyInd = "IND";     // Inductor relay
        public const string KeyCap = "CAP";     // Capacitor relay
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
