#nullable enable

using PgTg.Common;

namespace SampleAirMonitor.MyModel.Internal
{
    /// <summary>
    /// Parses acknowledgment responses from the GPIO controller device.
    /// The GPIO controller may send $ACK; in response to commands.
    /// </summary>
    internal class ResponseParser
    {
        private const string ModuleName = "ResponseParser";

        /// <summary>
        /// Parse a response string from the GPIO controller.
        /// </summary>
        /// <param name="response">The response received from the device.</param>
        /// <returns>True if the response was a recognized acknowledgment.</returns>
        public bool Parse(string response)
        {
            if (string.IsNullOrEmpty(response)) return false;

            string trimmed = response.Trim();
            if (!trimmed.StartsWith("$")) return false;

            string content = trimmed.TrimStart('$').TrimEnd(';').Trim();

            if (content == Constants.KeyAck)
            {
                Logger.LogVerbose(ModuleName, "GPIO controller acknowledged command");
                return true;
            }

            Logger.LogVerbose(ModuleName, $"Unknown GPIO controller response: {response}");
            return false;
        }
    }
}
