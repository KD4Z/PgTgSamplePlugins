#nullable enable

using System;
using System.Threading.Tasks;
using PgTg.Plugins.Core;

namespace SampleAmpTuner.MyModel.Internal
{
    /// <summary>
    /// Interface for device connection implementations (TCP or Serial).
    /// </summary>
    internal interface IConnection : IDisposable
    {
        /// <summary>
        /// Raised when data is received from the device.
        /// </summary>
        event Action<string>? DataReceived;

        /// <summary>
        /// Raised when connection state changes.
        /// </summary>
        event Action<PluginConnectionState>? ConnectionStateChanged;

        /// <summary>
        /// Current connection state.
        /// </summary>
        PluginConnectionState ConnectionState { get; }

        /// <summary>
        /// Whether the connection is currently established.
        /// </summary>
        bool IsConnected { get; }

        /// <summary>
        /// Start the connection.
        /// </summary>
        Task StartAsync();

        /// <summary>
        /// Stop the connection and cleanup.
        /// </summary>
        void Stop();

        /// <summary>
        /// Send data to the device.
        /// </summary>
        /// <param name="data">The command string to send.</param>
        /// <returns>True if sent successfully.</returns>
        bool Send(string data);
    }
}
