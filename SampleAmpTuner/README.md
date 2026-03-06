# Sample Amplifier+Tuner Plugin

This sample plugin demonstrates implementing `IAmplifierTunerPlugin` for devices that provide both amplifier and tuner functionality in a single unit.

## Key Concepts

### Single Shared Connection
Unlike separate amp and tuner plugins, combined devices typically use a **single TCP connection** for both subsystems. This requires coordinated management of:
- Connection state affects both subsystems
- Connection loss must handle both amp and tuner states
- Polling interval may vary based on PTT state

### Complex Disposal Pattern
Combined devices have MORE resources to manage:
- Multiple timers: polling, PTT watchdog, tune timeout, meter publishing
- Events from BOTH interfaces must be unwired
- Single connection to dispose

### State Coordination
When connection is lost:
1. If PTT active → force to RX (safety)
2. If tune in progress → abort tune
3. Both subsystems transition to error/reconnecting state

### Implementation Notes
- Implements ALL methods from both `IAmplifierPlugin` and `ITunerPlugin`
- No additional methods needed - just the combination
- Must handle interactions (e.g., can't tune while transmitting)
- Combined meter data from both subsystems

## Usage
Copy this project to create your own combined amplifier+tuner plugin. Replace simulated communication with your actual device protocol.

See the full implementation in `SampleAmpTunerPlugin.cs` for detailed comments and patterns.
