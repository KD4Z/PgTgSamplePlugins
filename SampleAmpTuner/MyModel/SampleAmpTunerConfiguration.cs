#nullable enable

using PgTg.Common;
using PgTg.Plugins.Core;
using SampleAmpTuner.MyModel.Internal;

namespace SampleAmpTuner.MyModel
{
    /// <summary>
    /// Configuration for the sample combined amplifier+tuner plugin.
    /// Implements IAmplifierTunerConfiguration which combines all amplifier and tuner settings.
    /// </summary>
    public class SampleAmpTunerConfiguration : IAmplifierTunerConfiguration
    {
        // IPluginConfiguration
        public string PluginId { get; set; } = SampleAmpTunerPlugin.PluginId;
        public bool Enabled { get; set; } = false;
        public PluginConnectionType ConnectionType { get; set; } = PluginConnectionType.TCP;
        public string IpAddress { get; set; } = "192.168.1.102";
        public int Port { get; set; } = 5002;
        public string SerialPort { get; set; } = "COM1";
        public int BaudRate { get; set; } = 38400;
        public int ReconnectDelayMs { get; set; } = 5000;
        public bool TcpSupported { get; set; } = true;
        public bool SerialSupported { get; set; } = true;
        public bool WolSupported { get; set; } = false;
        public bool SkipDeviceWakeup { get; set; } = false;
        public bool DisableControlsOnDisconnect { get; set; } = true;

        // IAmplifierConfiguration
        public int PollingIntervalRxMs { get; set; } = Constants.PollingRxMs;
        public int PollingIntervalTxMs { get; set; } = Constants.PollingTxMs;
        public int PttWatchdogIntervalMs { get; set; } = Constants.PttWatchdogMs;

        // ITunerConfiguration
        public int TuneTimeoutMs { get; set; } = 30000;
    }
}
