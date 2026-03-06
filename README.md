# PgTgSamplePlugins

Reference implementations for developing third-party plugins for the PgTgBridge amplifier/tuner service. Each project demonstrates a complete, working plugin using the `MyModel/Internal` architecture pattern.

Note: The $commands coded in each example are fictitious.  You will need to translate the polling/parsing command pattern into whatever your actual hardware requires!

## Projects

### SampleAmp — Amplifier-Only Plugin

**Plugin ID:** `sample.amplifier`
**Interface:** `IAmplifierPlugin`
**Capability:** `PluginCapability.Amplifier`
**Device ID:** `SAMP1500`
**Max Meter Power:** 1500 W

Demonstrates how to implement a standalone amplifier plugin. Features:

- TCP and serial connection support via `IConnection` abstraction
- Device initialization sequence (wakeup + identity check enabled)
- PTT keying with 15-second hardware watchdog (`$TX15;` / `$RX;`)
- Fast TX polling (15 ms) / slower RX polling (150 ms)
- Operate/Standby control (`$OPR1;` / `$OPR0;`)
- Metering: forward power, SWR/return loss, temperature, voltage, current
- Band tracking and frequency pass-through (`$FRQ nnnnn;`)
- Safety PTT release on radio disconnect

**Protocol format:** `$KEY value;` — `$` prefix, space-separated key and value, `;` terminator.
Example: `$PWR 500 12;` (500 W forward, SWR 1.2)

---

### SampleTuner — Tuner-Only Plugin

**Plugin ID:** `sample.tuner`
**Interface:** `ITunerPlugin`
**Capability:** `PluginCapability.Tuner`
**Device ID:** `SAMPTUN`
**Max Meter Power:** 600 W

Demonstrates how to implement a standalone antenna tuner plugin. Also demonstrates the **alternate startup path** where device initialization is disabled (`DeviceInitializationEnabled = false`) — the plugin connects and begins polling without waiting for an identity handshake.

Features:

- TCP and serial connection support
- Bypass/Inline control (`$BYPB;` / `$BYPN;`)
- Tune start/stop (`$TUN;` / `$TUS;`)
- Auto and manual tuning modes (`$MDA;` / `$MDM;`)
- Antenna selection (up to 3 antennas)
- Fast TX/tuning polling (10 ms) / RX polling (100 ms)
- 30-second tune timeout
- Metering: forward power (ADC→watts conversion), SWR
- Relay state tracking: inductor (hex), capacitor (hex)
- Zero meters when tune cycle completes or radio drops PTT

**Power conversion:** `Watts = Math.Pow(VFWD, 1.803) * 0.000721` (ADC voltage to watts)

---

### SampleAmpTuner — Combined Amplifier+Tuner Plugin

**Plugin ID:** `sample.amplifier-tuner`
**Interface:** `IAmplifierTunerPlugin`
**Capability:** `PluginCapability.AmplifierAndTuner`
**Device ID:** `SAMPCOMBO`
**Max Meter Power:** 1500 W

Demonstrates a single plugin that implements **both** `IAmplifierPlugin` and `ITunerPlugin` for devices that integrate an amplifier and tuner in one unit. A single shared connection manages both subsystems.

Features:

- All amplifier capabilities from SampleAmp
- All tuner capabilities from SampleTuner
- Single `IConnection` instance shared between both subsystems
- Coordinated state: PTT and tuning states managed together
- Combined meter data events from both subsystems
- Device initialization enabled (`SAMP1500` identity check)
- Safety interlock: PTT forces RX if radio disconnects

---

## Architecture Pattern

All three plugins follow the `MyModel/Internal` layered architecture:

```
MyModel/
    SampleXxxPlugin.cs          # IDevicePlugin implementation, public API
    SampleXxxConfiguration.cs   # IPluginConfiguration, connection & timing settings
    Internal/
        IConnection.cs          # Abstraction for TCP or serial transport
        TcpConnection.cs        # TCP client with auto-reconnect
        SerialConnection.cs     # Serial port client with auto-reconnect
        CommandQueue.cs         # Timed polling, PTT watchdog, priority commands
        ResponseParser.cs       # Parses $KEY value; protocol responses
        StatusTracker.cs        # Thread-safe device state, meter readings
        Constants.cs            # Command strings, response keys, timing values
```

### Key Design Points

| Concern | How it is handled |
|---|---|
| Connection type | Selected at `InitializeAsync` via `PluginConnectionType`; `IConnection` hides the difference |
| Polling rate | Two rates: slow during RX, fast during TX/tuning; switched by `CommandQueue` |
| PTT safety | Hardware watchdog refreshed every ~10 s; forced RX on radio disconnect or plugin stop |
| Event teardown | Handlers unsubscribed before `Stop()` / `Dispose()` to prevent post-dispose callbacks |
| Thread safety | `StatusTracker` uses a lock; `CommandQueue` uses `CancellationToken` for clean shutdown |
| Device init | Optional handshake (`$WKP;` → `$IDN;` → check identity string); can be disabled per plugin |

---

## Building

Each project references three assemblies in the deployment folder for  `PgTgBridge`
You will need PgTgBridge installed on your development workstation in order to resolve these references.

```xml
<ItemGroup>
    <Reference Include="PgTg">
      <HintPath>C:\Program Files\PgTgBridge\bin\PgTg.dll</HintPath>
    </Reference>
    <Reference Include="PgTg.Common">
      <HintPath>C:\Program Files\PgTgBridge\bin\PgTg.Common.dll</HintPath>
    </Reference>
    <Reference Include="PgTg.Helpers">
      <HintPath>C:\Program Files\PgTgBridge\bin\PgTg.Helpers.dll</HintPath>
    </Reference>
  </ItemGroup>
```

Build from the individual project directory:

```bash
dotnet build SampleAmp/SampleAmp.csproj
dotnet build SampleTuner/SampleTuner.csproj
dotnet build SampleAmpTuner/SampleAmpTuner.csproj
```

The output DLL is the plugin assembly. Copy it to the PgTg plugins directory and configure it in the PgTgController - Settings - Plugin Manager.

---

## Creating Your Own Plugin

1. Copy the sample project that matches your device capability (amp-only, tuner-only, or combined).
2. Rename the namespace, class names, and `PluginId` constant.
3. Update `Constants.cs` with your device's command strings, response keys, and timing values.
4. Update `ResponseParser.cs` to decode your device's response format.
5. Update `StatusTracker.cs` if your device exposes different state fields.
6. Update `CommandQueue.cs` poll arrays and any device-specific sequencing.
7. Set `DeviceInitializationEnabled` to `true` or `false` based on whether your device requires an identity handshake at startup.
8. Update `[PluginInfo(...)]` on your plugin class with the correct ID, name, manufacturer, and description.
