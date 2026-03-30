#nullable enable

namespace SampleAmp.MyModel.Internal
{
    /// <summary>
    /// Constants for the sample amplifier device commands and timing parameters.
    /// Protocol: $ prefix, ; terminator, space-separated values.
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
        public const string IdentifyResponse = "SAMP500";
        public static readonly bool DeviceInitializationEnabled = true;

        // Operate/Standby commands
        public const string OperateCmd = "$OPR1;";
        public const string StandbyCmd = "$OPR0;";

        // PTT commands
        public const string PttOnCmd = "$TX15;";     // Key PTT with 15 sec timeout
        public const string PttOffCmd = "$RX;";      // Release PTT

        // Query commands
        public const string PowerSwrCmd = "$PWR;";
        public const string TemperatureCmd = "$TMP;";
        public const string VoltageCurrentCmd = "$VLT;";
        public const string OperateStatusCmd = "$OPR;";
        public const string BandCmd = "$BND;";
        public const string FaultCmd = "$FLT;";
        public const string FwVersionCmd = "$VER;";
        public const string SerialNumberCmd = "$SER;";

        // Fault commands
        public const string ClearFaultCmd = "$FLC;";
        public const string FaultQueryCmd = "$FLT;";    // Query current fault code

        // Antenna selection commands — sent when user clicks an antenna LED
        public const string AntennaQueryCmd = "$ANT;";  // Query current antenna (device replies $ANT n;)
        public const string Antenna1Cmd = "$AN1;";      // Select antenna port 1
        public const string Antenna2Cmd = "$AN2;";      // Select antenna port 2

        // Frequency command prefix (append kHz value, 5 digits, semicolon)
        public const string SetFreqKhzCmdPrefix = "$FRQ";

        #endregion

        #region Polling Command Arrays

        /// <summary>
        /// Commands polled during receive (not transmitting).
        /// </summary>
        public static readonly string[] RxPollCommands =
        {
            "$PWR;",    // Power/SWR — populates ForwardPower meter
            "$TMP;",    // Temperature — populates Temperature meter
            "$VLT;",    // Voltage/Current
            "$OPR;",    // Operate/Standby — populates OS LED in Device Control
            "$BND;",    // Band number
            "$ANT;",    // Antenna selection — populates AN LED in Device Control
            "$FLT;",    // Fault code — populates FL LED in Device Control
        };

        /// <summary>
        /// Commands polled during transmit (fast polling for metering).
        /// </summary>
        public static readonly string[] TxPollCommands =
        {
            "$PWR;",    // Power/SWR (primary meter)
            "$TMP;",    // Temperature
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

        // Response keys parsed from device responses
        public const string KeyTx = "TX";
        public const string KeyRx = "RX";
        public const string KeyPwr = "PWR";     // Power/SWR
        public const string KeyOpr = "OPR";     // Operate/standby
        public const string KeyTmp = "TMP";     // Temperature
        public const string KeyFlt = "FLT";     // Fault code
        public const string KeyBnd = "BND";     // Band number
        public const string KeyVlt = "VLT";     // Voltage/Current
        public const string KeyAnt = "ANT";     // Antenna port (1 or 2)
        public const string KeyVer = "VER";     // Firmware version
        public const string KeySer = "SER";     // Serial number
        public const string KeyIdn = "IDN";     // Device identity

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
