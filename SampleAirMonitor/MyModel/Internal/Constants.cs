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
    }
}
