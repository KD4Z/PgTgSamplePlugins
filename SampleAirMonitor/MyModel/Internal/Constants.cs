#nullable enable

namespace SampleAirMonitor.MyModel.Internal
{
    /// <summary>
    /// Constants for the SampleAirMonitor GPIO output plugin.
    /// Commands are sent to a remote GPIO controller device when the Bridge
    /// notifies this plugin of amplifier/tuner state changes.
    /// Protocol: $ prefix, ; terminator, space-separated values.
    /// </summary>
    internal static class Constants
    {
        // GPIO plugins do not report meters; set to 0 to indicate no metering.
        public const double MeterDisplayMaxPower = 0;

        #region GPIO Output Commands

        // Amplifier PTT mirror commands
        public const string AmpPttOnCmd  = "$GPIO AMP_PTT_ON;";
        public const string AmpPttOffCmd = "$GPIO AMP_PTT_OFF;";

        // Amplifier operate/standby mirror commands
        public const string AmpOperateCmd = "$GPIO AMP_OPR;";
        public const string AmpStandbyCmd = "$GPIO AMP_STB;";

        // Tuner inline/bypass mirror commands
        public const string TunerInlineCmd = "$GPIO TUN_INLINE;";
        public const string TunerBypassCmd = "$GPIO TUN_BYPASS;";

        // Tuner tune cycle mirror commands
        public const string TunerTuneStartCmd = "$GPIO TUN_START;";
        public const string TunerTuneStopCmd  = "$GPIO TUN_STOP;";

        #endregion

        #region Response Keys

        // Acknowledgment key sent by the GPIO controller
        public const string KeyAck = "ACK";

        #endregion

        #region Frequency/Mode Protocol

        // Protocol selection values (matches PluginFreqModeProtocol config)
        public const int ProtocolCat = 1;
        public const int ProtocolCiv = 2;

        // CAT command format (Kenwood/Elecraft style)
        // FA followed by 11-digit frequency in Hz, terminated with ;
        public const string CatSetFreqPrefix = "FA";
        public const int CatFreqDigits = 11;

        // CAT mode command: MD followed by mode digit, terminated with ;
        public const string CatSetModePrefix = "MD";

        // CI-V frame bytes (Icom protocol)
        public const byte CivPreamble = 0xFE;
        public const byte CivEndOfMessage = 0xFD;
        public const byte CivCmdSetFreq = 0x05;
        public const byte CivCmdSetMode = 0x06;

        // CI-V mode byte values
        public const byte CivModeLsb = 0x00;
        public const byte CivModeUsb = 0x01;
        public const byte CivModeAm = 0x02;
        public const byte CivModeCw = 0x03;
        public const byte CivModeRtty = 0x04;
        public const byte CivModeFm = 0x05;
        public const byte CivModeCwR = 0x07;
        public const byte CivModeRttyR = 0x08;

        // CAT mode digit values (Kenwood/Elecraft)
        public const char CatModeLsb = '1';
        public const char CatModeUsb = '2';
        public const char CatModeCw = '3';
        public const char CatModeFm = '4';
        public const char CatModeAm = '5';
        public const char CatModeCwR = '7';
        public const char CatModeRtty = '6';
        public const char CatModeRttyR = '9';

        #endregion
    }
}
