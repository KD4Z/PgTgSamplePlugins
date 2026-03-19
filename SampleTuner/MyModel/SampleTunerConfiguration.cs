#nullable enable

using PgTg.Common;
using PgTg.Plugins.Core;
using SampleTuner.MyModel.Internal;

namespace SampleTuner.MyModel
{
    /// <summary>
    /// Configuration for the Sample Tuner plugin.
    /// Implements ITunerConfiguration to provide all required settings.
    /// </summary>
    public class SampleTunerConfiguration : ITunerConfiguration
    {
        // IPluginConfiguration base properties

        /// <summary>
        /// Unique identifier for this plugin.
        /// </summary>
        public string PluginId { get; set; } = SampleTunerPlugin.PluginId;

        /// <summary>
        /// Whether this plugin instance is enabled.
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Connection type (TCP or Serial).
        /// </summary>
        public PluginConnectionType ConnectionType { get; set; } = PluginConnectionType.TCP;

        /// <summary>
        /// IP address of the tuner when using TCP connection.
        /// </summary>
        public string IpAddress { get; set; } = "192.168.1.101";

        /// <summary>
        /// TCP port number for tuner communication.
        /// </summary>
        public int Port { get; set; } = 5001;

        /// <summary>
        /// Serial port name when using serial connection (e.g., "COM1", "/dev/ttyUSB0").
        /// </summary>
        public string SerialPort { get; set; } = "COM1";

        /// <summary>
        /// Serial baud rate. Default 38400 matches sample device specification.
        /// </summary>
        public int BaudRate { get; set; } = 38400;

        /// <summary>
        /// Delay in milliseconds before attempting to reconnect after connection loss.
        /// </summary>
        public int ReconnectDelayMs { get; set; } = 5000;

        /// <summary>
        /// Indicates TCP connection is supported by this plugin.
        /// </summary>
        public bool TcpSupported { get; set; } = true;

        /// <summary>
        /// Indicates Serial connection is supported by this plugin.
        /// </summary>
        public bool SerialSupported { get; set; } = true;

        /// <summary>
        /// Indicates Wake-on-LAN is not supported by this plugin.
        /// </summary>
        public bool WolSupported { get; set; } = false;

        /// <summary>
        /// When true, skip the device initialization/wake-up sequence (AmpWakeupMode=0).
        /// Not applicable to tuner-only plugins but required by IPluginConfiguration.
        /// </summary>
        public bool SkipDeviceWakeup { get; set; } = false;

        // ITunerConfiguration specific properties

        /// <summary>
        /// Maximum time in milliseconds to wait for a tune cycle to complete.
        /// </summary>
        public int TuneTimeoutMs { get; set; } = Constants.TuneTimeoutMs;
    }
}
