#nullable enable

using System;
using PgTg.Common;

namespace SampleAirMonitor.MyModel.Internal
{
    /// <summary>
    /// Sends GPIO output commands to the remote GPIO controller device.
    /// Unlike amplifier/tuner plugins, the GPIO plugin has no polling — it only
    /// sends commands when the Bridge calls SetAmpPtt, SetAmpOperateMode, etc.
    /// Also sends frequency/mode commands via CAT or CI-V protocol.
    /// </summary>
    internal class CommandQueue : IDisposable
    {
        private const string ModuleName = "CommandQueue";

        private readonly ISampleAirMonitorConnection _connection;
        private bool _disposed;

        public CommandQueue(ISampleAirMonitorConnection connection)
        {
            _connection = connection;
        }

        /// <summary>
        /// Start (no-op for GPIO plugin — no polling timers needed).
        /// </summary>
        public void Start()
        {
            //Logger.LogVerbose(ModuleName, "GPIO command queue started");
        }

        /// <summary>
        /// Stop (no-op for GPIO plugin — no polling timers to stop).
        /// </summary>
        public void Stop()
        {
            //Logger.LogVerbose(ModuleName, "GPIO command queue stopped");
        }

        /// <summary>
        /// Send a GPIO output command immediately (ASCII string).
        /// </summary>
        public void SendCommand(string command)
        {
            if (!_connection.IsConnected)
            {
                //Logger.LogVerbose(ModuleName, $"Not connected — dropping command: {command}");
                return;
            }

            _connection.Send(command);
           // Logger.LogVerbose(ModuleName, $"Command sent: {command}");
        }

        /// <summary>
        /// Send raw bytes immediately (used for CI-V binary protocol).
        /// </summary>
        public void SendCommand(byte[] data)
        {
            if (!_connection.IsConnected)
            {
               // Logger.LogVerbose(ModuleName, "Not connected — dropping binary command");
                return;
            }

            _connection.Send(data);
            //Logger.LogVerbose(ModuleName, $"Binary command sent: {data.Length} bytes");
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
        }
    }
}
