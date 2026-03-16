#nullable enable

namespace SampleAirMonitor.MyModel.Internal
{
    /// <summary>
    /// Tracks the last known output state sent to the GPIO controller device.
    /// Used for logging and optional state query support.
    /// </summary>
    internal class StatusTracker
    {
        private readonly object _lock = new();

        /// <summary>Last amplifier PTT state sent to GPIO controller.</summary>
        public bool AmpPtt { get; private set; }

        /// <summary>Last amplifier operate state sent to GPIO controller.</summary>
        public bool AmpOperate { get; private set; }

        /// <summary>Last tuner inline state sent to GPIO controller.</summary>
        public bool TunerInline { get; private set; }

        /// <summary>Last tuner tuning state sent to GPIO controller.</summary>
        public bool TunerTuning { get; private set; }

        /// <summary>Last frequency in Hz sent to transceiver.</summary>
        public int FrequencyHz { get; private set; }

        /// <summary>Last transmit mode sent to transceiver.</summary>
        public string TransmitMode { get; private set; } = string.Empty;

        /// <summary>Set the amp PTT state.</summary>
        public void SetAmpPtt(bool ptt)
        {
            lock (_lock) { AmpPtt = ptt; }
        }

        /// <summary>Set the amp operate/standby state.</summary>
        public void SetAmpOperate(bool operate)
        {
            lock (_lock) { AmpOperate = operate; }
        }

        /// <summary>Set the tuner inline/bypass state.</summary>
        public void SetTunerInline(bool inline)
        {
            lock (_lock) { TunerInline = inline; }
        }

        /// <summary>Set the tuner tuning state.</summary>
        public void SetTunerTuning(bool tuning)
        {
            lock (_lock) { TunerTuning = tuning; }
        }

        /// <summary>
        /// Set the frequency. Returns true if the value changed.
        /// </summary>
        public bool SetFrequencyHz(int frequencyHz)
        {
            lock (_lock)
            {
                if (FrequencyHz == frequencyHz) return false;
                FrequencyHz = frequencyHz;
                return true;
            }
        }

        /// <summary>
        /// Set the transmit mode. Returns true if the value changed.
        /// </summary>
        public bool SetTransmitMode(string mode)
        {
            lock (_lock)
            {
                if (TransmitMode == mode) return false;
                TransmitMode = mode;
                return true;
            }
        }
    }
}
