#nullable enable

using PgTg.Common;
using PgTg.Plugins.Core;

namespace SampleAirMonitor.MyModel
{
    /// <summary>
    /// Configuration for the SampleAirMonitor GPIO output plugin.
    /// GPIO plugins use the base IPluginConfiguration (no polling intervals needed).
    /// </summary>
    public class SampleAirMonitorConfiguration : IPluginConfiguration
    {
        // IPluginConfiguration
        public string PluginId { get; set; } = SampleAirMonitorPlugin.PluginId;
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
    }
}
