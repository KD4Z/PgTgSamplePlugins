#nullable enable

using PgTg.Common;
using PgTg.Plugins.Core;
using SampleAmp.MyModel.Internal;

namespace SampleAmp.MyModel
{
    /// <summary>
    /// Configuration for the sample amplifier plugin.
    /// </summary>
    public class SampleAmpConfiguration : IAmplifierConfiguration
    {
        // IPluginConfiguration
        public string PluginId { get; set; } = SampleAmpPlugin.PluginId;
        public bool Enabled { get; set; } = false;
        public PluginConnectionType ConnectionType { get; set; } = PluginConnectionType.TCP;
        public string IpAddress { get; set; } = "192.168.1.100";
        public int Port { get; set; } = 5000;
        public string SerialPort { get; set; } = "COM1";
        public int BaudRate { get; set; } = 38400;
        public int ReconnectDelayMs { get; set; } = 5000;
        public bool TcpSupported { get; set; } = true;
        public bool SerialSupported { get; set; } = true;
        public bool WolSupported { get; set; } = false;

        // IAmplifierConfiguration
        public int PollingIntervalRxMs { get; set; } = Constants.PollingRxMs;
        public int PollingIntervalTxMs { get; set; } = Constants.PollingTxMs;
        public int PttWatchdogIntervalMs { get; set; } = Constants.PttWatchdogMs;
    }
}
