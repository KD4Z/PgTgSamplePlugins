# PgTgBridge Plugin Architecture - Programmer's Reference

## Overview

The PgTgBridge plugin architecture allows developers to add support for new amplifiers and tuners without modifying the core Bridge code. Plugins communicate with external devices and translate their protocol into standardized events that the Bridge consumes.

This document covers:
- Core concepts and interfaces
- Plugin lifecycle management
- Creating built-in plugins
- Creating external DLL plugins
- Configuration schema
- Event handling patterns
- Testing guidelines

---

## Architecture Overview

```
┌─────────────────────────────────────────────────────────────────────┐
│                           Bridge.cs                               │
│  (Subscribes to PluginManager events, routes to radio/VITA)         │
└─────────────────────────────────┬───────────────────────────────────┘
                                  │
                    ┌─────────────▼─────────────┐
                    │      PluginManager        │
                    │  (Lifecycle, Aggregation) │
                    └─────────────┬─────────────┘
                                  │
        ┌─────────────────────────┼─────────────────────────┐
        │                         │                         │
┌───────▼───────┐         ┌───────▼───────┐         ┌───────▼───────┐
│ Kpa1500Plugin │         │ Kpa500/Kat500 │         │ External DLL  │
│ (Built-in)    │         │ (Built-in)    │         │ Plugins       │
└───────┬───────┘         └───────┬───────┘         └───────┬───────┘
        │                         │                         │
        │   ┌─────────────────────┘                         │
        │   │   Internal/ (per-plugin)                      │
        ▼   ▼                                               ▼
┌─────────────────┐                              ┌─────────────────────┐
│  IConnection    │                              │   MyModel/          │
│  CommandQueue   │                              │   ├── Plugin.cs     │
│  ResponseParser │                              │   ├── Config.cs     │
│  StatusTracker  │                              │   └── Internal/     │
│  Constants      │                              │       ├─ IConnection │
└─────────────────┘                              │       ├─ TcpConn    │
                                                 │       ├─ SerialConn │
                                                 │       ├─ CmdQueue   │
                                                 │       ├─ Parser     │
                                                 │       ├─ Tracker    │
                                                 │       └─ Constants  │
                                                 └─────────────────────┘
```

### Key Components

| Component | Location | Purpose |
|-----------|----------|---------|
| `PluginManager` | `PgTg/Plugins/PluginManager.cs` | Central orchestrator for plugin lifecycle and event aggregation |
| `PluginFactory` | `PgTg/Plugins/PluginFactory.cs` | Registry and factory for built-in plugins |
| `PluginLoader` | `PgTg/Plugins/PluginLoader.cs` | Discovers and loads external DLL plugins |
| Core Interfaces | `PgTg/Plugins/Core/` | Interface contracts all plugins must implement |

---

## Internal Architecture Pattern

Both built-in plugins and the sample external plugins use a component-based internal architecture. Each plugin's implementation is split into focused classes organized under an `Internal/` subfolder. This prevents leaking implementation details and keeps the main plugin class clean and concise.

### The Five Internal Classes

| Class | Responsibility |
|-------|---------------|
| `IConnection` | Interface abstracting TCP and Serial transports; raises `DataReceived` and `ConnectionStateChanged` |
| `TcpConnection` | Implements `IConnection` over TCP with auto-reconnect loop and `NetworkStream.ReadAsync` |
| `SerialConnection` | Implements `IConnection` over `System.IO.Ports.SerialPort` with event-driven `DataReceived` |
| `CommandQueue` | Manages polling timers, priority TX/RX commands, PTT watchdog, and optional device initialization |
| `ResponseParser` | Stateless parser; translates raw response strings into a `StatusUpdate` value struct with change flags |
| `StatusTracker` | Thread-safe state machine; holds last-known device state and produces `AmplifierStatusData` / `TunerStatusData` |
| `Constants` | Compile-time constants: command strings, timing values, `DeviceInitializationEnabled`, response keys |

### Folder Layout (External / Sample Plugins)

```
YourPlugin/
└── MyModel/
    ├── YourPlugin.cs              # Implements IAmplifierPlugin / ITunerPlugin / IAmplifierTunerPlugin
    ├── YourPluginConfiguration.cs # Implements appropriate IXxxConfiguration interface
    └── Internal/
        ├── IConnection.cs         # Transport abstraction interface
        ├── TcpConnection.cs       # TCP implementation of IConnection
        ├── SerialConnection.cs    # Serial implementation of IConnection
        ├── Constants.cs           # Command strings, timing constants
        ├── CommandQueue.cs        # Polling, priority commands, PTT watchdog
        ├── ResponseParser.cs      # Protocol parsing → StatusUpdate
        └── StatusTracker.cs       # Thread-safe state + status/meter accessors
```

### Multi-Transport: When to Use IConnection vs Direct Classes

Use the `IConnection` interface abstraction (TCP + Serial) when:
- Your device can be reached over either TCP/IP **or** a serial port (e.g., via a serial-to-Ethernet converter)
- You want users to choose the connection type via configuration
- The sample plugins (`SampleAmp`, `SampleTuner`, `SampleAmpTuner`) all demonstrate this pattern

Use a direct `TcpConnection` only (no interface) when:
- Your device is TCP-only and will never support serial
- Example: `Kpa1500TcpConnection` is the only transport in the built-in KPA1500 plugin

The configuration class signals supported transports by setting `SerialSupported` and `TcpSupported` in its `IPluginConfiguration` implementation.

---

## Core Interfaces

### Interface Hierarchy

```
IDevicePlugin (base)
├── IAmplifierPlugin
├── ITunerPlugin
└── IAmplifierTunerPlugin (inherits both)
```

### IDevicePlugin

The base interface that all plugins must implement. Located at `PgTg/Plugins/Core/IDevicePlugin.cs`.

```csharp
public interface IDevicePlugin : IDisposable
{
    // Plugin metadata
    PluginInfo Info { get; }

    // Connection state (Disconnected, Connecting, Connected, Reconnecting, Error)
    PluginConnectionState ConnectionState { get; }

    // Lifecycle methods
    Task InitializeAsync(IPluginConfiguration configuration, CancellationToken cancellationToken);
    Task StartAsync();
    Task StopAsync();

    // Events
    event EventHandler<PluginConnectionStateChangedEventArgs>? ConnectionStateChanged;
    event EventHandler<MeterDataEventArgs>? MeterDataAvailable;
    event EventHandler? DeviceDataChanged;

    // Device data and control (default interface methods — override as needed)
    double MeterDisplayMaxPower { get; }
    Task WakeupDeviceAsync() => Task.CompletedTask;
    Task ShutdownDeviceAsync() => Task.CompletedTask;
    Dictionary<string, object> GetDeviceData() => new();
    bool SendDeviceCommand(string command) => false;
    DeviceControlDefinition? GetDeviceControlDefinition() => null;

    // Connection state UI behaviour (default = true; override to opt out)
    bool DisableControlsOnDisconnect => true;
}
```

### IAmplifierPlugin

For amplifier-only devices. Located at `PgTg/Plugins/Core/IAmplifierPlugin.cs`.

```csharp
public interface IAmplifierPlugin : IDevicePlugin
{
    AmplifierStatusData GetStatus();
    event EventHandler<AmplifierStatusEventArgs>? StatusChanged;

    // CRITICAL: Low-latency TX/RX interlock control
    void SendPriorityCommand(AmpCommand command);

    void SetFrequencyKhz(int frequencyKhz);
    void SetRadioConnected(bool connected);
}
```

### ITunerPlugin

For tuner-only devices. Located at `PgTg/Plugins/Core/ITunerPlugin.cs`.

```csharp
public interface ITunerPlugin : IDevicePlugin
{
    TunerStatusData GetTunerStatus();
    event EventHandler<TunerStatusEventArgs>? TunerStatusChanged;

    void SetInline(bool inline);
    void StartTune();
    void StopTune();
}
```

### IAmplifierTunerPlugin

For combined devices like the KPA1500. Located at `PgTg/Plugins/Core/IAmplifierTunerPlugin.cs`.

```csharp
public interface IAmplifierTunerPlugin : IAmplifierPlugin, ITunerPlugin
{
    // Inherits all methods from both interfaces
    // Use this when one device provides both amplifier and tuner functionality
}
```

---

## Plugin Capability Enum

```csharp
[Flags]
public enum PluginCapability
{
    None = 0,
    Amplifier = 1,
    Tuner = 2,
    AmplifierAndTuner = Amplifier | Tuner  // For combined devices
}
```

---

## Plugin Lifecycle

### 1. Discovery
The `PluginManager` discovers plugins via:
- `PluginFactory.GetAvailablePlugins()` for built-in plugins
- `PluginLoader.DiscoverPlugins(path)` for external DLLs

### 2. Creation
```csharp
// Built-in
IDevicePlugin plugin = PluginFactory.CreatePlugin(pluginId, cancellationToken);

// External
IDevicePlugin plugin = _pluginLoader.CreatePlugin(pluginId, cancellationToken);
```

### 3. Initialization
```csharp
await plugin.InitializeAsync(configuration, cancellationToken);
```
- Called once before `StartAsync()`
- Plugins should create internal components but NOT connect yet
- Configuration is validated and stored

### 4. Start
```csharp
await plugin.StartAsync();
```
- Establishes connection to device
- Starts polling/communication loops
- Raises `ConnectionStateChanged` as state transitions

### 5. Stop
```csharp
await plugin.StopAsync();
```
- Graceful shutdown
- Releases device resources
- Zeros meter values before final update

### 6. Dispose
```csharp
plugin.Dispose();
```
- Final cleanup
- Unsubscribes from events
- Disposes internal components

---

## Configuration System

### Configuration Interfaces

```csharp
// Base configuration - all plugins must support these
public interface IPluginConfiguration
{
    string PluginId { get; set; }
    bool Enabled { get; set; }
    string IpAddress { get; set; }
    int Port { get; set; }
    int ReconnectDelayMs { get; set; }

    // When true (default), the Controller disables Device Control UI elements
    // (LEDs, fan buttons) whenever the plugin is not in the Connected state.
    // The Power LED is always kept enabled regardless of this setting.
    bool DisableControlsOnDisconnect { get; set; }  // default: true
}

// Amplifier-specific settings
public interface IAmplifierConfiguration : IPluginConfiguration
{
    int PollingIntervalRxMs { get; set; }   // Polling rate in RX mode (e.g., 1000ms)
    int PollingIntervalTxMs { get; set; }   // Polling rate in TX mode (e.g., 20ms)
    int PttWatchdogIntervalMs { get; set; } // PTT refresh interval
    bool KeyAmpDuringTuneCarrier { get; set; }
}

// Tuner-specific settings
public interface ITunerConfiguration : IPluginConfiguration
{
    int TuneTimeoutMs { get; set; }  // Max tune cycle duration
}

// Combined device settings
public interface IAmplifierTunerConfiguration : IAmplifierConfiguration, ITunerConfiguration
{
    // Inherits all properties
}
```

### Creating Plugin-Specific Configuration

```csharp
// Example: Kpa1500Configuration.cs
public class Kpa1500Configuration : IAmplifierTunerConfiguration
{
    // IPluginConfiguration
    public string PluginId { get; set; } = "elecraft.kpa1500";
    public bool Enabled { get; set; } = true;
    public string IpAddress { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 1500;
    public int ReconnectDelayMs { get; set; } = 5000;

    // IAmplifierConfiguration
    public int PollingIntervalRxMs { get; set; } = 1000;
    public int PollingIntervalTxMs { get; set; } = 20;
    public int PttWatchdogIntervalMs { get; set; } = 10000;
    public bool KeyAmpDuringTuneCarrier { get; set; } = true;

    // ITunerConfiguration
    public int TuneTimeoutMs { get; set; } = 30000;

    // Plugin-specific properties can be added here
}
```

### User Configuration Storage

User settings are stored in `Settings.PluginConfigurations`:

```csharp
public class PluginConfigurationEntry
{
    public string PluginId { get; set; }
    public bool Enabled { get; set; }
    public string IpAddress { get; set; }
    public int Port { get; set; }
    public int ReconnectDelayMs { get; set; }
    public bool DisableControlsOnDisconnect { get; set; } = true;  // see below
    public Dictionary<string, object>? CustomSettings { get; set; }
}
```

---

## Creating a Built-in Plugin

### Step 1: Create Plugin Folder Structure

```
PgTg/Plugins/YourManufacturer/
├── YourPlugin.cs              # Main plugin class (implements IAmplifierPlugin, etc.)
├── YourPluginConfiguration.cs # Plugin-specific config
└── Internal/
    ├── IConnection.cs         # Transport abstraction (TCP + Serial)
    ├── TcpConnection.cs       # TCP/IP handling with auto-reconnect
    ├── SerialConnection.cs    # Serial port handling (omit if TCP-only device)
    ├── CommandQueue.cs        # Polling, priority commands, PTT watchdog
    ├── ResponseParser.cs      # Protocol parsing → StatusUpdate
    ├── StatusTracker.cs       # Thread-safe state management
    └── Constants.cs           # Timing values, command strings, device ID
```

### Step 2: Implement the Plugin Class

```csharp
using PgTg.Plugins.Core;

namespace PgTg.Plugins.YourManufacturer
{
    [PluginInfo("manufacturer.model", "Your Plugin Name",
        Version = "1.0.0",
        Manufacturer = "Your Manufacturer",
        Capability = PluginCapability.Amplifier,
        Description = "Description of your plugin")]
    public class YourPlugin : IAmplifierPlugin
    {
        public const string PluginId = "manufacturer.model";

        private readonly CancellationToken _cancellationToken;

        public PluginInfo Info { get; } = new PluginInfo
        {
            Id = PluginId,
            Name = "Your Plugin Name",
            Version = "1.0.0",
            Manufacturer = "Your Manufacturer",
            Capability = PluginCapability.Amplifier,
            ConfigurationType = typeof(YourPluginConfiguration)
        };

        public PluginConnectionState ConnectionState =>
            _connection?.ConnectionState ?? PluginConnectionState.Disconnected;

        // Events
        public event EventHandler<PluginConnectionStateChangedEventArgs>? ConnectionStateChanged;
        public event EventHandler<MeterDataEventArgs>? MeterDataAvailable;
        public event EventHandler<AmplifierStatusEventArgs>? StatusChanged;

        public YourPlugin(CancellationToken cancellationToken)
        {
            _cancellationToken = cancellationToken;
        }

        // Implement lifecycle methods...
    }
}
```

### Step 3: Register in PluginFactory

Edit `PgTg/Plugins/PluginFactory.cs`:

```csharp
private static readonly Dictionary<string, Func<CancellationToken, IDevicePlugin>> _builtInPlugins = new()
{
    [Kpa1500Plugin.PluginId] = (ct) => new Kpa1500Plugin(ct),
    [YourPlugin.PluginId] = (ct) => new YourPlugin(ct),  // Add your plugin
};

private static readonly Dictionary<string, PluginInfo> _pluginInfos = new()
{
    [Kpa1500Plugin.PluginId] = new PluginInfo { /* ... */ },
    [YourPlugin.PluginId] = new PluginInfo       // Add metadata
    {
        Id = YourPlugin.PluginId,
        Name = "Your Plugin Name",
        Version = "1.0.0",
        Manufacturer = "Your Manufacturer",
        Capability = PluginCapability.Amplifier,
        Description = "Your description",
        ConfigurationType = typeof(YourPluginConfiguration)
    },
};
```

---

## Creating an External DLL Plugin

External plugins are compiled as separate DLL assemblies and loaded at runtime.

### Step 1: Create a Class Library Project

Use `MyModel/` as the top-level folder within your project, mirroring the sample plugin structure:

```
YourPlugin/
├── YourPlugin.csproj
└── MyModel/
    ├── YourPlugin.cs
    ├── YourPluginConfiguration.cs
    └── Internal/
        ├── IConnection.cs
        ├── TcpConnection.cs
        ├── SerialConnection.cs
        ├── Constants.cs
        ├── CommandQueue.cs
        ├── ResponseParser.cs
        └── StatusTracker.cs
```

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>disable</ImplicitUsings>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="System.IO.Ports" Version="10.0.1" />
  </ItemGroup>

  <ItemGroup>
    <!-- These three assemblies are installed by PgTgBridge.
         PgTgBridge must be installed to C:\Program Files\PgTgBridge\ on the
         development machine before these HintPaths will resolve. -->
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
</Project>
```

### Step 2: Implement the Plugin

Use the `[PluginInfo]` attribute for efficient discovery:

```csharp
using PgTg.Plugins.Core;

namespace YourCompany.PgTgPlugin
{
    [PluginInfo("yourcompany.amplifier", "Your Amplifier",
        Version = "1.0.0",
        Manufacturer = "Your Company",
        Capability = PluginCapability.Amplifier,
        Description = "Support for Your Company amplifier")]
    public class YourAmplifierPlugin : IAmplifierPlugin
    {
        // Implementation...
    }
}
```

### Step 3: Deploy

1. Build the plugin DLL
2. Place it in the `plugins/` folder (relative to PgTg executable)
3. The `PluginLoader` will discover it automatically

### Important: Constructor Requirements

External plugins must have one of these constructors:

```csharp
// Preferred - receives cancellation token
public YourPlugin(CancellationToken cancellationToken) { }

// Alternative - parameterless
public YourPlugin() { }
```

---

## Event Handling Patterns

### Raising Status Events

```csharp
// Amplifier status change
private void OnStatusChanged()
{
    var status = new AmplifierStatusData
    {
        OperateState = _currentState,
        IsPttActive = _isPtt,
        WhatChanged = AmplifierStatusChanged.OperateStateChanged
    };

    StatusChanged?.Invoke(this, new AmplifierStatusEventArgs(status, PluginId));
}
```

### Raising Meter Data Events

```csharp
private void RaiseMeterDataEvent()
{
    var readings = new Dictionary<MeterType, MeterReading>
    {
        [MeterType.ForwardPower] = new MeterReading(MeterType.ForwardPower, _forwardPower, MeterUnits.Watts),
        [MeterType.Temperature] = new MeterReading(MeterType.Temperature, _temperature, MeterUnits.Celsius),
        [MeterType.SWR] = new MeterReading(MeterType.SWR, _swr, MeterUnits.Ratio)
    };

    var args = new MeterDataEventArgs(readings, _isPtt, PluginId);
    MeterDataAvailable?.Invoke(this, args);
}
```

### Connection State Changes

```csharp
private void OnConnectionStateChanged(PluginConnectionState newState)
{
    var previous = ConnectionState;
    ConnectionStateChanged?.Invoke(this,
        new PluginConnectionStateChangedEventArgs(previous, newState));
}
```

### Connection State and Device Control UI (Auto Enable/Disable)

When a plugin raises `ConnectionStateChanged`, the Bridge immediately broadcasts a `WsDeviceConnectionState` WebSocket message to the Controller. The Controller responds by enabling or disabling the Device Control panel's interactive elements based on the connection state:

- **Connected** → all LEDs and fan speed buttons are enabled.
- **Any other state** (Disconnected, Connecting, Reconnecting, Error) → all LEDs and buttons are disabled **except the Power LED**, which always remains enabled so the device can be powered on remotely.

#### Configuring the behaviour

This auto-disable behaviour is controlled per plugin via `DisableControlsOnDisconnect` (default: `true`).

**In the plugin class** — read the setting from config in `InitializeAsync` and expose it via the `IDevicePlugin` property:

```csharp
private bool _disableControlsOnDisconnect = true;

// In InitializeAsync:
_disableControlsOnDisconnect = configuration.DisableControlsOnDisconnect;

// IDevicePlugin property:
public bool DisableControlsOnDisconnect => _disableControlsOnDisconnect;
```

**In the plugin settings JSON** — set the value to `false` to opt out:

```json
{
  "PluginId": "mycompany.mydevice",
  "IpAddress": "192.168.1.100",
  "Port": 4000,
  "DisableControlsOnDisconnect": false
}
```

Set `DisableControlsOnDisconnect` to `false` when your plugin manages all UI state independently through `GetDeviceData()` values — for example, if a disconnected state is already reflected by graying out the Power LED via its `ActiveValue`.

---

## Device Control Panel Integration

External plugins can define their own LED indicators in the Device Control dashboard by implementing `GetDeviceControlDefinition()`. The Controller dynamically renders these definitions at runtime — no hard-coded UI changes needed.

### Overview

1. Plugin implements `GetDeviceControlDefinition()` returning a `DeviceControlDefinition`
2. Plugin implements `GetDeviceData()` returning a dictionary whose keys match the definition's `ResponseKey` values
3. Plugin implements `SendDeviceCommand(string)` to handle commands sent from clicked LEDs
4. Plugin fires `DeviceDataChanged` when any value returned by `GetDeviceData()` changes

On WebSocket connect, the service sends the definitions to the Controller, which renders LED indicators dynamically.

### DeviceControlElement Properties

| Property | Type | Description |
|----------|------|-------------|
| `ActiveColor` | `string` | LED color when active: `"green"`, `"yellow"`, `"red"`, `"gray"` |
| `InactiveColor` | `string` | LED color when inactive |
| `ActiveText` | `string` | Label text when active (e.g., `"Operate"`) |
| `InactiveText` | `string` | Label text when inactive (e.g., `"Standby"`) |
| `ActiveCommand` | `string?` | Command sent when clicked while active (`null` = no-op) |
| `InactiveCommand` | `string?` | Command sent when clicked while inactive (`null` = no-op) |
| `ResponseKey` | `string` | Key in `GetDeviceData()` dict that drives this element's state |
| `ActiveValue` | `string` | Value (case-insensitive string compare) that means "active" |
| `IsClickable` | `bool` | Whether the LED responds to clicks |

### DeviceControlDefinition

A wrapper containing two optional sections:

| Property | Type | Description |
|----------|------|-------------|
| `Elements` | `List<DeviceControlElement>` | LED indicator elements rendered left-to-right |
| `FanControl` | `FanControlDefinition?` | Optional fan speed row (▼ label ▲). When non-null the panel adds a dedicated fan speed control row identical to the built-in KPA500/KPA1500 UI. Default: `null` (no fan row) |

### FanControlDefinition

Adds a dedicated fan speed row to the Device Control panel. The row displays the current speed and two buttons (▼ down / ▲ up) that send `SetCommandPrefix + speed + ";"` directly via `SendDeviceCommand`.

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `ResponseKey` | `string` | `"FN"` | Key in `GetDeviceData()` that carries the current fan speed integer |
| `MaxSpeed` | `int` | `5` | Maximum allowed speed (inclusive). Up button disabled at this value |
| `SetCommandPrefix` | `string` | `"FC"` | Command prefix. Combined with speed and `";"` — e.g. `"$FC"` → `"$FC3;"` |
| `PowerResponseKey` | `string?` | `null` | Optional key in `GetDeviceData()` that gates button enable/disable. When `null` buttons are always enabled |
| `PowerActiveValue` | `string` | `"1"` | Value of `PowerResponseKey` that means "powered on." Ignored when `PowerResponseKey` is `null` |

**How the fan row works:**
- `GetDeviceData()` must return `["FN"] = <current speed int>` on every update.
- The panel reads this value and displays it between the two buttons.
- When the user clicks ▲ (up), the panel sends `SetCommandPrefix + (current + 1) + ";"` via `SendDeviceCommand`.
- When the user clicks ▼ (down), it sends `SetCommandPrefix + (current − 1) + ";"`.
- Buttons are disabled when: at min/max speed, power is off (if `PowerResponseKey` is configured), or the speed value has not yet been received.
- Fire `DeviceDataChanged` whenever `FN` changes so the display updates promptly.

### Data Flow

```
Plugin defines UI          Controller renders dynamically
       │                              │
GetDeviceControlDefinition()   ──────►  Build LED controls from definition
       │                              │
GetDeviceData()  ──────────────────►  Compare ResponseKey values to ActiveValue
       │                              │  → set LED color + label text
DeviceDataChanged event  ──────────►  Push updated data via WebSocket
       │                              │
                          ◄──────────  User clicks LED
SendDeviceCommand(cmd)                  → send ActiveCommand or InactiveCommand
```

### Color Reference

| Color String | Description |
|-------------|-------------|
| `"green"` | Active/on state (RGB: 0, 192, 0) |
| `"yellow"` | Warning/standby state (RGB: 255, 255, 0) |
| `"red"` | Fault/alarm state (RGB: 255, 0, 0) |
| `"gray"` | Inactive/off state (RGB: 128, 128, 128) |

### Complete Example

```csharp
public DeviceControlDefinition? GetDeviceControlDefinition()
{
    return new DeviceControlDefinition
    {
        Elements = new List<DeviceControlElement>
        {
            new DeviceControlElement
            {
                ActiveColor = "green", InactiveColor = "gray",
                ActiveText = "Power On", InactiveText = "Power Off",
                ActiveCommand = "$ON0;", InactiveCommand = "$ON1;",
                ResponseKey = "ON", ActiveValue = "1",
                IsClickable = true
            },
            new DeviceControlElement
            {
                ActiveColor = "green", InactiveColor = "yellow",
                ActiveText = "Operate", InactiveText = "Standby",
                ActiveCommand = "$OS0;", InactiveCommand = "$OS1;",
                ResponseKey = "OS", ActiveValue = "1",
                IsClickable = true
            },
            new DeviceControlElement
            {
                ActiveColor = "red", InactiveColor = "gray",
                ActiveText = "FAULT", InactiveText = "No Fault",
                ActiveCommand = "$FLC;", InactiveCommand = null,
                ResponseKey = "FL", ActiveValue = "1",
                IsClickable = true
            }
        },

        // Optional fan speed row — omit entirely if your device has no fan control.
        // Renders ▼ Fan ▲ buttons identical to the built-in KPA500/KPA1500 UI.
        FanControl = new FanControlDefinition
        {
            ResponseKey      = "FN",   // GetDeviceData()["FN"] = current speed int
            MaxSpeed         = 5,       // 0 = off, 5 = full
            SetCommandPrefix = "$FC",   // sends "$FC3;" to select speed 3
            PowerResponseKey = "ON",    // disable buttons when power is off
            PowerActiveValue = "1"
        }
    };
}
```

And the corresponding `GetDeviceData()` implementation:

```csharp
public Dictionary<string, object> GetDeviceData()
{
    lock (_lock)
    {
        return new Dictionary<string, object>
        {
            ["ON"] = AmpState != AmpOperateState.Standby ? 1 : 0,
            ["OS"] = AmpState == AmpOperateState.Operate ? 1 : 0,
            // FL must remain boolean (0/1) to match ActiveValue = "1" in the definition
            ["FL"] = FaultCode > 0 ? 1 : 0,
            // FaultDesc: reserved key — UI shows this string as the Fault LED hover tooltip
            ["FaultDesc"] = GetFaultDescription(FaultCode),
            ["BN"] = BandNumber,
            // FN: current fan speed integer (0–MaxSpeed); drives the fan control row
            ["FN"] = FanSpeed
        };
    }
}
```

### Reserved Device Data Keys

The following keys in `GetDeviceData()` have special meaning to the Controller UI beyond LED activation:

| Key | Type | Purpose |
|-----|------|---------|
| `"FL"` | `int` (0 or 1) | Fault LED activation — must equal `"1"` when `ActiveValue = "1"` |
| `"FLT"` | `int` (0 or 1) | Fault LED activation for tuner-only devices |
| `"FaultDesc"` | `string` | Hover tooltip text shown on the Fault LED when it is active (red). Empty string or omitted = no tooltip. |
| `"BN"` | `int` | Band number — displayed in the band label using the standard band name map |
| `"FN"` | `int` | Current fan speed. Required when `FanControlDefinition.ResponseKey = "FN"` (the default). Value is displayed between the ▼ and ▲ buttons. |

### Fault Description Pattern

Provide a `GetFaultDescription(int faultCode)` static method in your `StatusTracker` and populate `"FaultDesc"` from it. Return `string.Empty` for code 0 (no fault) so the tooltip disappears when the fault clears.

```csharp
public static string GetFaultDescription(int faultCode) => faultCode switch
{
    0 => string.Empty,
    1 => "RF overload",
    2 => "Temperature fault",
    3 => "Power supply fault",
    _ => $"Fault (code {faultCode})"
};
```

Every `ResponseKey` used in the definition **must** also appear in the dictionary returned by `GetDeviceData()`, and the plugin **must** fire `DeviceDataChanged` when those values change.

---

## Critical: PTT/TX Interlock Handling

The `SendPriorityCommand()` method is **safety-critical**. It controls the transmit interlock between the radio and amplifier.

### Requirements

1. **Minimal Latency**: Commands must be sent immediately, bypassing any polling queue
2. **Watchdog Refresh**: PTT must be refreshed before amplifier timeout (typically 15 seconds)
3. **Safe Fallback**: If radio disconnects during TX, immediately release PTT

### Implementation Pattern

```csharp
public void SendPriorityCommand(AmpCommand command)
{
    switch (command)
    {
        case AmpCommand.TX:
            // Insert ^TX15; command IMMEDIATELY - ahead of all other commands
            _commandQueue.InsertPriority("^TX15;");
            _pttWatchdogTimer.Start();  // Refresh every 10s (before 15s timeout)
            break;

        case AmpCommand.RX:
            // Insert ^RX; immediately
            _commandQueue.InsertPriority("^RX;");
            _pttWatchdogTimer.Stop();
            break;
    }
}
```

### Safety: Radio Disconnect

```csharp
public void SetRadioConnected(bool connected)
{
    if (!connected)
    {
        // CRITICAL: Force amplifier to RX if radio disconnects during TX
        _commandQueue.InsertPriority("^RX;");
        Logger.LogWarning(ModuleName, "Radio disconnected - forcing amplifier to RX");
    }
}
```

---

## Status Data Types

### AmplifierStatusData

```csharp
public class AmplifierStatusData
{
    public AmpOperateState OperateState { get; set; }  // Unknown, Standby, Operate, Fault
    public bool IsPttActive { get; set; }
    public int Antenna { get; set; }
    public int BandNumber { get; set; }
    public string BandName { get; set; }
    public int FaultCode { get; set; }
    public string FaultInfo { get; set; }
    public bool IsOverloaded { get; set; }
    public string FirmwareVersion { get; set; }
    public string SerialNumber { get; set; }
    public AmplifierStatusChanged WhatChanged { get; set; }

    // Meter values
    public double ForwardPower { get; set; }
    public double SWR { get; set; }
    public double ReturnLoss { get; set; }
    public int Temperature { get; set; }
    public int PACurrent { get; set; }
    public double DrivePower { get; set; }
}
```

### TunerStatusData

```csharp
public class TunerStatusData
{
    public TunerOperateState OperateState { get; set; }  // Inline, Bypass
    public TunerTuningState TuningState { get; set; }    // NotTuning, TuningInProgress
    public int InductorValue { get; set; }
    public int Capacitor1Value { get; set; }
    public int Capacitor2Value { get; set; }
    public double LastSwr { get; set; }
    public TunerStatusChanged WhatChanged { get; set; }
    public string FirmwareVersion { get; set; }
    public string SerialNumber { get; set; }
}
```

---

## Meter Types

```csharp
public enum MeterType
{
    ForwardPower = 0,       // Watts
    ReturnLoss = 1,         // dB
    Temperature = 2,        // Celsius
    PACurrent = 3,          // Amps
    DrivePower = 4,         // Watts
    SWR = 5,                // Ratio
    TunerForwardPower = 10, // Watts
    TunerReturnLoss = 11,   // dB
    Efficiency = 20         // Percentage
}
```

---

## Best Practices

### 1. Use Component-Based Architecture

Split complex plugins into focused components:
- **Connection**: TCP/IP socket handling, reconnection logic
- **Command Queue**: Priority handling, polling management
- **Response Parser**: Protocol parsing, data extraction
- **Status Tracker**: State machine, change detection

### 2. Thread Safety

- Use locks for shared state
- Events may be raised from background threads
- Use `ConcurrentDictionary` for thread-safe collections

### 3. Logging

Use the standard `Logger` class:

```csharp
Logger.LogInfo(ModuleName, "Plugin started");
Logger.LogWarning(ModuleName, "Connection lost, reconnecting...");
Logger.LogError(ModuleName, $"Error: {ex.Message}");
Logger.LogVerbose(ModuleName, "Verbose debug info");
```

### 4. Graceful Error Handling

```csharp
public async Task StartAsync()
{
    try
    {
        await _connection.StartAsync(_config.IpAddress, _config.Port);
    }
    catch (Exception ex)
    {
        Logger.LogError(ModuleName, $"Failed to start: {ex.Message}");
        // Don't throw - allow retry via ConnectionStateChanged event
    }
}
```

### 5. Resource Cleanup

Always implement `IDisposable` properly:

```csharp
public void Dispose()
{
    if (_disposed) return;
    _disposed = true;

    _timer?.Dispose();
    _connection?.Dispose();
    // Unsubscribe from events to prevent memory leaks
}
```

---

## Testing Your Plugin

### Unit Tests

Test components in isolation:

```csharp
[Fact]
public void ResponseParser_ParsesStatusCorrectly()
{
    var parser = new YourResponseParser();
    var result = parser.Parse("^OS0;");  // Operating state = Standby

    Assert.Equal(AmpOperateState.Standby, result.OperateState);
}
```

### Integration Tests

Test the full plugin against a mock device:

```csharp
[Fact]
public async Task Plugin_ConnectsAndReceivesStatus()
{
    using var mockDevice = new MockAmplifierServer(port: 1500);
    using var plugin = new YourPlugin(CancellationToken.None);

    await plugin.InitializeAsync(new YourPluginConfiguration { Port = 1500 }, CancellationToken.None);
    await plugin.StartAsync();

    // Wait for connection
    await Task.Delay(1000);

    Assert.Equal(PluginConnectionState.Connected, plugin.ConnectionState);
}
```

---

## Getting Started - Sample Plugin Projects

To help third-party developers get started quickly, the solution includes three standalone sample plugin projects that demonstrate all three plugin types. These projects use the component-based `MyModel/Internal/` architecture and are designed to be copied and used as starting templates.

### Sample Projects

| Project | Type | Location | Maps To |
|---------|------|----------|---------|
| **SampleAmp** | `IAmplifierPlugin` | `PgTgSamplePlugins/SampleAmp/` | KPA500 pattern — amplifier-only with PTT watchdog, meter publishing, TCP+Serial |
| **SampleTuner** | `ITunerPlugin` | `PgTgSamplePlugins/SampleTuner/` | KAT500 pattern — tuner-only, inline/bypass, tune cycle, TCP+Serial |
| **SampleAmpTuner** | `IAmplifierTunerPlugin` | `PgTgSamplePlugins/SampleAmpTuner/` | KPA1500 pattern — combined amp+tuner, single connection, TCP+Serial |

### Sample Project File Layout

Each sample project shares the same `MyModel/Internal/` structure:

```
PgTgSamplePlugins/SampleAmp/
├── SampleAmp.csproj
└── MyModel/
    ├── SampleAmpPlugin.cs              # Implements IAmplifierPlugin
    ├── SampleAmpConfiguration.cs       # Implements IAmplifierConfiguration
    └── Internal/
        ├── IConnection.cs              # TCP + Serial transport abstraction
        ├── TcpConnection.cs            # TCP/IP with auto-reconnect loop
        ├── SerialConnection.cs         # SerialPort with event-driven receive
        ├── Constants.cs                # Command strings, timing, DeviceInitializationEnabled
        ├── CommandQueue.cs             # Polling timers, PTT watchdog, init sequence
        ├── ResponseParser.cs           # $-prefix protocol parser → StatusUpdate
        └── StatusTracker.cs            # Thread-safe state, GetAmplifierStatus(), meters
```

`SampleTuner` and `SampleAmpTuner` follow the identical layout with their own namespaces (`SampleTuner.MyModel[.Internal]`, `SampleAmpTuner.MyModel[.Internal]`).

### Fictitious Sample Protocol (`$`-prefix)

The sample projects implement a fictitious device protocol to illustrate parsing patterns without tying the examples to real Elecraft CAT syntax.

**Format:** `$COMMAND[ARGS];` request, `$COMMAND value;` response

#### Amplifier Commands (SampleAmp, SampleAmpTuner)

| Command | Response | Description |
|---------|----------|-------------|
| `$PWR;` | `$PWR ppp sss;` | Forward power (W), SWR×10 |
| `$TMP;` | `$TMP ttt;` | Temperature °C |
| `$VLT;` | `$VLT vvv iii;` | Voltage×10, Current×10 |
| `$OPR;` | `$OPR n;` | 0=standby, 1=operate |
| `$OPR1;` / `$OPR0;` | — | Set operate / standby |
| `$BND;` | `$BND nn;` | Band number |
| `$FRQ nnnnn;` | — | Set frequency kHz |
| `$TX15;` | `$TX;` | Key PTT (15 s watchdog) |
| `$RX;` | `$RX;` | Release PTT |
| `$FLT;` | `$FLT nn;` | Fault code |
| `$FLC;` | — | Clear fault |
| `$VER;` | `$VER n.nn;` | Firmware version |
| `$SER;` | `$SER xxxxx;` | Serial number |
| `$IDN;` | `$IDN SAMP500;` | Device identity (`SAMP1500` for AmpTuner) |
| `$WKP;` | — | Wake from sleep |

#### Tuner Commands (SampleTuner, SampleAmpTuner)

| Command | Response | Description |
|---------|----------|-------------|
| `$BYP;` | `$BYPB;` or `$BYPN;` | Query bypass state |
| `$BYPB;` / `$BYPN;` | — | Set bypass / inline |
| `$TUN;` | — | Start tune |
| `$TUS;` | — | Stop tune |
| `$TPL;` | `$TPL n;` | Tune poll (0=idle, 1=tuning) |
| `$SWR;` | `$SWR n.nn;` | Current SWR |
| `$FPW;` | `$FPW nnnn;` | Forward power ADC |
| `$IND;` | `$IND xx;` | Inductor relay (hex) |
| `$CAP;` | `$CAP xx;` | Capacitor relay (hex) |
| `$MDE;` | `$MDE x;` | Mode: A=auto, M=manual |
| `$ANT n;` | — | Set antenna 1–3 |

**Parsing convention:** `ResponseParser` strips the leading `$`, then splits on the first space to get the key and value. E.g. `$PWR 250 15;` → key=`PWR`, value=`250 15`.

### Device Initialization Flag

Each `Constants.cs` declares:

```csharp
public const bool DeviceInitializationEnabled = true;  // or false
```

- **`true`** (SampleAmp, SampleAmpTuner): On connect, `CommandQueue` sends `$WKP;$IDN;` and waits for the identity response before normal polling begins. Retries every 500 ms via `_initTimer`.
- **`false`** (SampleTuner): Initialization is skipped; the poll timer starts immediately. Use this for devices that do not require a wake-up sequence.

See the `CommandQueue.StartDeviceInitializationAsync()` method in each sample for the implementation.

### Key Features of Sample Projects

- **Multi-transport**: `IConnection` interface lets the same plugin run over TCP or Serial — chosen via `config.ConnectionType` in `InitializeAsync`
- **Rigorous lifecycle**: Proper Initialize → Start → Stop → Dispose with no missed events
- **Memory leak prevention**: All event handlers unwired before Stop/Dispose; timer teardown follows CLAUDE.md order (unsubscribe → stop → dispose → null)
- **PTT safety**: Watchdog timer refreshes PTT every 10 s for amplifiers; released immediately on connection loss
- **Tune timeout**: `_tuneTimeoutTimer` aborts stuck tune cycles after `TuneTimeoutMs`
- **Thread-safe state**: `StatusTracker` uses `lock(_lock)` for all state reads/writes

### Using the Sample Projects

1. **Copy the appropriate sample project** as a starting point:
   ```bash
   cp -r PgTgSamplePlugins/SampleAmp MyAmpPlugin
   ```

2. **Rename files and namespaces** to match your device:
   - Update `.csproj` filename and `<RootNamespace>` / `<AssemblyName>`
   - Replace `SampleAmp.MyModel` namespace with `YourCompany.YourModel`
   - Update `PluginId` constant and `[PluginInfo]` attribute
   - Update device ID in `Constants.DeviceId`

3. **Implement device-specific protocol**:
   - Replace `$`-prefix commands in `Constants.cs` with your device's actual commands
   - Update `ResponseParser` to parse your device's response format
   - Update `CommandQueue` poll commands and PTT commands
   - Update `StatusTracker` meter calculations for your device's ADC/units

4. **Configure references for your plugin project**:
   - Install PgTgBridge to `C:\Program Files\PgTgBridge\` on your development machine
   - Reference `PgTg.dll`, `PgTg.Common.dll`, and `PgTg.Helpers.dll` from the install directory using `HintPath` (see the `.csproj` template above)
   - Copy your compiled DLL to the Bridge's `plugins/` directory
   - Add a configuration entry to `appSettings.json`

### Development Environment Prerequisite

> **PgTgBridge must be installed before the sample plugin projects (or any plugin project modeled after them) can be built.**
>
> The three assembly references in each sample `.csproj` use `HintPath` entries pointing to `C:\Program Files\PgTgBridge\bin\`. If PgTgBridge is not installed at that path, MSBuild will not be able to resolve `PgTg.dll`, `PgTg.Common.dll`, or `PgTg.Helpers.dll` and the build will fail with missing assembly errors.
>
> Install PgTgBridge using the provided MSI installer, then open or restore the sample plugin projects.

### Building Sample Projects

The sample projects reference the installed PgTgBridge assemblies directly and can be built independently of the main solution once PgTgBridge is installed:

```bash
dotnet build PgTgSamplePlugins/SampleAmp/SampleAmp.csproj
dotnet build PgTgSamplePlugins/SampleTuner/SampleTuner.csproj
dotnet build PgTgSamplePlugins/SampleAmpTuner/SampleAmpTuner.csproj
```

The sample projects are also included in the main solution (`PgTg.sln`) for compile-time validation. When plugin interfaces change in `PgTg.dll`, rebuilding the samples against the updated installed DLL will surface any required implementation updates.

---

## Plugin Implementation Guide

This section provides detailed guidance on implementing enterprise-grade plugins based on patterns demonstrated in the sample projects.

### Lifecycle Management

Plugins follow a strict lifecycle: **Initialize → Start → Stop → Dispose**

#### InitializeAsync

Called once to configure the plugin. **Do NOT connect or subscribe to events here.**

```csharp
public Task InitializeAsync(IPluginConfiguration configuration, CancellationToken cancellationToken)
{
    _config = configuration as MyPluginConfiguration
        ?? throw new ArgumentException("Invalid configuration type");

    // Store cancellation token
    _cancellationToken = cancellationToken;

    // Create timers (but don't start them)
    _pollingTimer = new Timer(_config.PollingIntervalRxMs);
    _pollingTimer.AutoReset = true;

    // DO NOT:
    // - Connect to device
    // - Subscribe to events
    // - Start timers

    return Task.CompletedTask;
}
```

**Why:** InitializeAsync may be called during configuration validation. If initialization fails, the plugin should be disposable without side effects.

#### StartAsync

Connect to the device and begin operations.

```csharp
public async Task StartAsync()
{
    // STEP 1: Subscribe to events BEFORE connecting
    // This prevents missing early events
    _pollingTimer.Elapsed += OnPollingTimerElapsed;

    // STEP 2: Connect to device
    await ConnectAsync();

    // STEP 3: Start timers
    _pollingTimer.Start();

    Logger.LogInfo(ModuleName, "Plugin started");
}
```

**Event Subscription Order Matters:** Subscribe to events *before* triggering actions that might raise them.

#### Device Initialization (Optional)

Some devices require a wake-up sequence before they respond to commands. The Elecraft KPA500, KPA1500, and KAT500 implement this pattern in their CommandQueue classes.

**Pattern Overview:**

1. Send wake-up command (`;;;P` for amplifiers, `;;;` for tuners)
2. Send null command (`;`) and wait for device to echo it back
3. Retry every 500ms until device responds
4. Only after initialization completes does normal polling begin

**Implementation:**

```csharp
// In CommandQueue class
private bool _isInitialized;
private bool _initializationInProgress;
private TaskCompletionSource<bool>? _initCompletionSource;
private Timer? _initTimer;
private const int InitRetryIntervalMs = 500;

public async Task StartAsync()
{
    // Create timers but don't start polling yet
    _pollTimer = new Timer { Interval = _pollingRxMs };
    _pollTimer.Elapsed += OnPollTimerElapsed;

    // Wait for device to wake up before polling
    await StartDeviceInitializationAsync();
}

private async Task StartDeviceInitializationAsync()
{
    // Optional: Check if initialization is disabled
    if (!Constants.DeviceInitializationEnabled)
    {
        _isInitialized = true;
        _pollTimer?.Start();
        return;
    }

    _initializationInProgress = true;
    _initCompletionSource = new TaskCompletionSource<bool>();

    // Send wake-up + null command
    _connection.Send(WakeUpCmd + NullCmd);

    // Retry every 500ms until response
    _initTimer = new Timer { Interval = InitRetryIntervalMs };
    _initTimer.Elapsed += (s, e) => _connection.Send(NullCmd);
    _initTimer.Start();

    // Wait for device to respond
    await _initCompletionSource.Task;
}

// Called when data received during initialization
public bool OnInitializationResponse(string response)
{
    if (!_initializationInProgress) return false;

    if (response.Contains(NullCmd))
    {
        _initTimer?.Stop();
        _initializationInProgress = false;
        _isInitialized = true;
        _pollTimer?.Start();
        _initCompletionSource?.TrySetResult(true);
        return true;
    }
    return false;
}
```

**Disable Flag:**

Each device has a `DeviceInitializationEnabled` constant in its Constants class. Set to `false` to skip the wake-up sequence (useful for devices that don't require it or for testing).

```csharp
// In Kpa1500Constants.cs
public const bool DeviceInitializationEnabled = false;  // Set true to enable
```

**Reference Implementations:**
- `Kpa500CommandQueue` - Full implementation with async initialization
- `Kpa1500CommandQueue` - Same pattern, currently disabled
- `Kat500CommandQueue` - Same pattern, currently disabled

#### StopAsync

Gracefully disconnect and clean up.

```csharp
public Task StopAsync()
{
    // STEP 1: Zero out meters (UI cleanup)
    _forwardPower = 0;
    _swr = 1.0;
    RaiseMeterDataEvent();  // Final update with zeros

    // STEP 2: Unsubscribe from events
    if (_pollingTimer != null)
        _pollingTimer.Elapsed -= OnPollingTimerElapsed;

    // STEP 3: Stop timers
    _pollingTimer?.Stop();

    // STEP 4: Disconnect
    Disconnect();

    Logger.LogInfo(ModuleName, "Plugin stopped");
    return Task.CompletedTask;
}
```

**Critical:** Always unsubscribe from events in StopAsync to prevent callbacks after stop.

#### Dispose

Final cleanup to prevent memory leaks.

```csharp
private bool _disposed = false;

public void Dispose()
{
    if (_disposed) return;  // Guard against double disposal
    _disposed = true;

    // STEP 1: Unsubscribe event handlers FIRST (a thread-pool callback may be
    //         queued between Stop() and Dispose() and will still fire unless
    //         the handler is detached first)
    if (_pollingTimer != null)
        _pollingTimer.Elapsed -= OnPollingTimerElapsed;
    if (_pttWatchdogTimer != null)
        _pttWatchdogTimer.Elapsed -= OnPttWatchdogElapsed;

    // STEP 2: Stop timers
    _pollingTimer?.Stop();
    _pttWatchdogTimer?.Stop();

    // STEP 3: Dispose IDisposable members
    _pollingTimer?.Dispose();
    _pttWatchdogTimer?.Dispose();
    _pollingTimer = null;
    _pttWatchdogTimer = null;

    // STEP 4: Close connections and release network resources
    _tcpClient?.Dispose();
    ConnectionStateChanged = null;
    MeterDataAvailable = null;
    StatusChanged = null;
}
```

**Required Teardown Order (per CLAUDE.md):**
1. `-= HandlerMethod` — detach all subscribed handlers first; prevents a queued thread-pool callback from firing after disposal
2. `.Stop()` — halt the timer
3. `.Dispose()` — release the underlying OS timer resource
4. `= null` — help GC and prevent accidental reuse

> **Warning:** Stopping a timer does not guarantee no further callbacks. Between `Stop()` and `Dispose()` a callback may already be queued on the thread pool. Unsubscribing first is the only safe approach.

### Memory Leak Prevention

**Event subscriptions are the #1 cause of memory leaks in long-running plugins.**

#### The Problem

```csharp
// BAD - Creates memory leak
_timer.Elapsed += OnTimerElapsed;  // Plugin subscribes to timer
// Later... plugin is "disposed" but timer still holds reference to plugin
// Plugin can never be garbage collected!
```

#### The Solution

```csharp
// GOOD - Always unsubscribe
public void Dispose()
{
    if (_timer != null)
    {
        _timer.Stop();
        _timer.Elapsed -= OnTimerElapsed;  // Break the reference
        _timer.Dispose();
    }
}
```

#### Event Subscription Checklist

For every event subscription (`+=`), you **must** have a corresponding unsubscription (`-=`) in both:
- `StopAsync()` - for operational cleanup
- `Dispose()` - for memory leak prevention

### TCP Connection Resilience

Plugins must handle connection failures gracefully and reconnect automatically.

#### Robust Connection Pattern

```csharp
private async Task ConnectAsync()
{
    if (_config == null || _disposed) return;

    SetConnectionState(PluginConnectionState.Connecting);
    Logger.LogInfo(ModuleName, $"Connecting to {_config.IpAddress}:{_config.Port}");

    try
    {
        // CRITICAL: Dispose old connection first (prevents memory leak)
        if (_tcpClient != null)
        {
            _tcpClient.Close();
            _tcpClient.Dispose();
            _tcpClient = null;
        }

        // Create new connection
        _tcpClient = new TcpClient();
        _tcpClient.SendTimeout = 5000;
        _tcpClient.ReceiveTimeout = 5000;

        // Connect with ConfigureAwait(false) to avoid deadlocks
        await _tcpClient.ConnectAsync(_config.IpAddress, _config.Port)
            .ConfigureAwait(false);

        _stream = _tcpClient.GetStream();

        SetConnectionState(PluginConnectionState.Connected);
        Logger.LogInfo(ModuleName, "Connected");

        // Start polling
        _pollingTimer?.Start();

        // Start background read loop
        _ = Task.Run(ReadResponsesAsync, _cancellationToken);
    }
    catch (Exception ex)
    {
        // NEVER crash on connection failure - handle gracefully
        Logger.LogError(ModuleName, $"Connection failed: {ex.Message}");
        SetConnectionState(PluginConnectionState.Error);

        // Schedule automatic reconnection
        ScheduleReconnect();
    }
}
```

#### Reconnection Strategy

```csharp
private void ScheduleReconnect()
{
    if (_config == null || _disposed) return;

    Logger.LogWarning(ModuleName,
        $"Scheduling reconnect in {_config.ReconnectDelayMs}ms");

    // Non-blocking delay then reconnect
    Task.Delay(_config.ReconnectDelayMs).ContinueWith(async _ =>
    {
        if (!_disposed)
        {
            SetConnectionState(PluginConnectionState.Reconnecting);
            await ConnectAsync().ConfigureAwait(false);
        }
    }, _cancellationToken);
}
```

#### Connection Loss Handling

```csharp
private void OnConnectionLost()
{
    Logger.LogWarning(ModuleName, "Connection lost - will reconnect");

    // Clean up current connection
    _stream?.Close();
    _tcpClient?.Close();

    // Stop polling
    _pollingTimer?.Stop();

    // Handle in-progress operations
    if (_isPtt)
    {
        _isPtt = false;
        _pttWatchdogTimer?.Stop();
        RaiseStatusChanged(AmplifierStatusChange.PttStateChanged);
        Logger.LogWarning(ModuleName, "PTT aborted due to connection loss");
    }

    // Attempt reconnection
    SetConnectionState(PluginConnectionState.Reconnecting);
    ScheduleReconnect();
}
```

**Key Points:**
- Always dispose old connection before creating new one
- Use `ConfigureAwait(false)` on async operations
- Never crash - transition to Error state and schedule retry
- Abort in-progress operations (PTT, tuning) on connection loss
- Log all connection state changes for debugging

### Thread-Safe Event Raising

Events may have zero subscribers, so always use null-conditional operator.

```csharp
// GOOD - Thread-safe, handles null subscribers
private void RaiseStatusChanged(AmplifierStatusChange whatChanged)
{
    var status = GetStatus();
    status.WhatChanged = whatChanged;

    // Null-conditional operator prevents NullReferenceException
    StatusChanged?.Invoke(this, new AmplifierStatusEventArgs(status, PluginId));
}

// ALTERNATIVE - Local copy pattern (prevents race conditions)
private void RaiseStatusChanged(AmplifierStatusChange whatChanged)
{
    var handler = StatusChanged;  // Get local copy
    if (handler != null)
    {
        var status = GetStatus();
        status.WhatChanged = whatChanged;
        handler(this, new AmplifierStatusEventArgs(status, PluginId));
    }
}
```

### PTT Safety Patterns (Amplifier Plugins)

PTT (Push-to-Talk) control is **safety-critical**. Failure to release PTT can damage equipment.

#### PTT Watchdog

```csharp
// Amplifiers typically timeout PTT after 15 seconds
// Refresh every 10 seconds to prevent timeout during long transmissions
private Timer _pttWatchdogTimer = new Timer(10000);

public void SendPriorityCommand(AmpCommand command)
{
    if (command == AmpCommand.TX)
    {
        _isPtt = true;
        _pttWatchdogTimer?.Start();  // Start watchdog
        RaiseStatusChanged(AmplifierStatusChange.PttStateChanged);

        // TODO: Send PTT command to device
    }
    else if (command == AmpCommand.RX)
    {
        _isPtt = false;
        _pttWatchdogTimer?.Stop();  // Stop watchdog
        RaiseStatusChanged(AmplifierStatusChange.PttStateChanged);

        // TODO: Send PTT release command
    }
}

private void OnPttWatchdogElapsed(object? sender, ElapsedEventArgs e)
{
    if (_isPtt && _stream != null)
    {
        Logger.LogVerbose(ModuleName, "Refreshing PTT watchdog");
        // TODO: Send PTT refresh command
    }
}
```

#### Connection Loss During PTT

```csharp
private void OnConnectionLost()
{
    // CRITICAL: Abort PTT if active
    if (_isPtt)
    {
        _isPtt = false;
        _pttWatchdogTimer?.Stop();
        RaiseStatusChanged(AmplifierStatusChange.PttStateChanged);
        Logger.LogWarning(ModuleName, "PTT aborted due to connection loss");
    }

    // ... reconnection logic
}
```

### Tune Timeout Patterns (Tuner Plugins)

Tune cycles must not hang indefinitely.

```csharp
// Timeout timer prevents stuck tuning states
private Timer _tuneTimeoutTimer;

public void StartTune()
{
    if (_tuningState == TunerTuningState.TuningInProgress)
    {
        Logger.LogWarning(ModuleName, "Tune already in progress");
        return;
    }

    Logger.LogInfo(ModuleName, "Starting tune cycle");

    // Start timeout timer (safety mechanism)
    _tuneTimeoutTimer?.Start();

    _tuningState = TunerTuningState.TuningInProgress;
    RaiseTunerStatusChanged(TunerStatusChange.TuningStateChanged);

    // TODO: Send tune command to device
}

private void OnTuneTimeoutElapsed(object? sender, ElapsedEventArgs e)
{
    if (_tuningState == TunerTuningState.TuningInProgress)
    {
        Logger.LogError(ModuleName, "Tune timeout - aborting");

        _tuneTimeoutTimer?.Stop();
        _tuningState = TunerTuningState.NotTuning;
        RaiseTunerStatusChanged(TunerStatusChange.TuningStateChanged);

        // TODO: Send abort command to device
    }
}
```

### Polling Patterns

Different polling rates for RX vs TX.

```csharp
public void SendPriorityCommand(AmpCommand command)
{
    if (command == AmpCommand.TX)
    {
        // Switch to fast polling during TX
        _pollingTimer.Interval = _config.PollingIntervalTxMs;  // ~20ms
    }
    else if (command == AmpCommand.RX)
    {
        // Slow polling during RX
        _pollingTimer.Interval = _config.PollingIntervalRxMs;  // ~1000ms
    }
}
```

**Rationale:** Fast polling during TX provides responsive meter updates. Slow polling during RX reduces network traffic.

### Common Pitfalls

#### ❌ Don't: Connect in InitializeAsync

```csharp
// BAD - Connects too early
public Task InitializeAsync(IPluginConfiguration config, CancellationToken ct)
{
    _config = config;
    ConnectAsync();  // WRONG - initialization may be just for validation
    return Task.CompletedTask;
}
```

#### ✅ Do: Connect in StartAsync

```csharp
// GOOD - Connects when actually starting
public async Task StartAsync()
{
    await ConnectAsync();
}
```

#### ❌ Don't: Forget to Unsubscribe Events

```csharp
// BAD - Memory leak
public void StartAsync()
{
    _timer.Elapsed += OnTimer;
}
// No corresponding -= in Stop/Dispose
```

#### ✅ Do: Always Unsubscribe

```csharp
// GOOD - Prevents memory leak
public void Dispose()
{
    if (_timer != null)
        _timer.Elapsed -= OnTimer;
}
```

#### ❌ Don't: Crash on Connection Errors

```csharp
// BAD - Crashes plugin
await _tcpClient.ConnectAsync(ip, port);  // Throws on failure
```

#### ✅ Do: Handle Gracefully

```csharp
// GOOD - Handles errors and retries
try
{
    await _tcpClient.ConnectAsync(ip, port);
}
catch (Exception ex)
{
    Logger.LogError(ModuleName, $"Connection failed: {ex.Message}");
    SetConnectionState(PluginConnectionState.Error);
    ScheduleReconnect();
}
```

#### ❌ Don't: Dispose Resources Twice

```csharp
// BAD - Can cause exceptions
public void Dispose()
{
    _timer.Dispose();
    _timer.Dispose();  // CRASH - ObjectDisposedException
}
```

#### ✅ Do: Guard Against Double Disposal

```csharp
// GOOD - Safe disposal
private bool _disposed = false;

public void Dispose()
{
    if (_disposed) return;
    _disposed = true;

    _timer?.Dispose();
}
```

### ConfigureAwait Best Practices

Always use `ConfigureAwait(false)` on async operations in plugins to avoid deadlocks.

```csharp
// GOOD
await _tcpClient.ConnectAsync(ip, port).ConfigureAwait(false);

// BAD - Can deadlock on sync context
await _tcpClient.ConnectAsync(ip, port);
```

**Why:** Plugins run in a background service. Capturing the synchronization context can cause deadlocks.

### Logging Guidelines

Use appropriate log levels:

```csharp
Logger.LogError(ModuleName, "Connection failed");        // Errors
Logger.LogWarning(ModuleName, "Reconnecting...");        // Warnings
Logger.LogInfo(ModuleName, "Plugin started");            // Important events
Logger.LogVerbose(ModuleName, "Polling device");         // Verbose debugging
```

---

## Complete Sample Implementations

> **Note — Legacy Monolithic Pattern:** The examples below show the original single-file, monolithic implementation style. They are preserved here as a compact illustration of the plugin interface contracts. For new plugin development, use the **component-based `MyModel/Internal/` architecture** documented in [Sample Plugin Projects](#getting-started---sample-plugin-projects) and [Internal Architecture Pattern](#internal-architecture-pattern) above. The standalone sample projects in `PgTgSamplePlugins/` are the authoritative reference implementations.

The following sections provide complete, compilable examples of all three plugin types embedded in this documentation. For standalone projects that you can copy and modify, see the sample projects described above.

### Sample 1: Amplifier-Only Plugin (Kpa500Plugin)

This example shows an amplifier-only plugin for the Elecraft KPA500.

```csharp
#nullable enable

using System.Net.Sockets;
using System.Text;
using System.Timers;
using PgTg.AMP;
using PgTg.Common;
using PgTg.Plugins.Core;
using PgTg.RADIO;
using Timer = System.Timers.Timer;

namespace PgTg.Plugins.Elecraft
{
    /// <summary>
    /// Configuration for the KPA500 amplifier plugin.
    /// </summary>
    public class Kpa500Configuration : IAmplifierConfiguration
    {
        public string PluginId { get; set; } = Kpa500Plugin.PluginId;
        public bool Enabled { get; set; } = true;
        public string IpAddress { get; set; } = "192.168.1.100";
        public int Port { get; set; } = 1500;
        public int ReconnectDelayMs { get; set; } = 5000;
        public int PollingIntervalRxMs { get; set; } = 1000;
        public int PollingIntervalTxMs { get; set; } = 50;
        public int PttWatchdogIntervalMs { get; set; } = 10000;
        public bool KeyAmpDuringTuneCarrier { get; set; } = true;
    }

    /// <summary>
    /// Plugin for Elecraft KPA500 amplifier (amplifier-only, no built-in ATU).
    /// Implements IAmplifierPlugin interface.
    /// </summary>
    [PluginInfo("elecraft.kpa500", "Elecraft KPA500",
        Version = "1.0.0",
        Manufacturer = "Elecraft",
        Capability = PluginCapability.Amplifier,
        Description = "Elecraft KPA500 500W amplifier")]
    public class Kpa500Plugin : IAmplifierPlugin
    {
        public const string PluginId = "elecraft.kpa500";
        private const string ModuleName = "Kpa500Plugin";

        private readonly CancellationToken _cancellationToken;
        private Kpa500Configuration? _config;
        private TcpClient? _tcpClient;
        private NetworkStream? _stream;
        private Timer? _pollingTimer;
        private Timer? _pttWatchdogTimer;
        private Timer? _meterTimer;
        private bool _disposed;
        private bool _isPtt;
        private AmpOperateState _operateState = AmpOperateState.Unknown;
        private double _forwardPower;
        private double _swr;
        private int _temperature;
        private PluginConnectionState _connectionState = PluginConnectionState.Disconnected;

        #region IDevicePlugin Implementation

        public PluginInfo Info { get; } = new PluginInfo
        {
            Id = PluginId,
            Name = "Elecraft KPA500",
            Version = "1.0.0",
            Manufacturer = "Elecraft",
            Capability = PluginCapability.Amplifier,
            Description = "Elecraft KPA500 500W amplifier",
            ConfigurationType = typeof(Kpa500Configuration)
        };

        public PluginConnectionState ConnectionState => _connectionState;

        public event EventHandler<PluginConnectionStateChangedEventArgs>? ConnectionStateChanged;
        public event EventHandler<MeterDataEventArgs>? MeterDataAvailable;

        #endregion

        #region IAmplifierPlugin Implementation

        public event EventHandler<AmplifierStatusEventArgs>? StatusChanged;

        #endregion

        public Kpa500Plugin(CancellationToken cancellationToken)
        {
            _cancellationToken = cancellationToken;
        }

        #region Lifecycle Methods

        public Task InitializeAsync(IPluginConfiguration configuration, CancellationToken cancellationToken)
        {
            _config = configuration as Kpa500Configuration ?? new Kpa500Configuration
            {
                IpAddress = configuration.IpAddress,
                Port = configuration.Port,
                Enabled = configuration.Enabled,
                ReconnectDelayMs = configuration.ReconnectDelayMs
            };

            Logger.LogInfo(ModuleName, $"Initialized with config: {_config.IpAddress}:{_config.Port}");
            return Task.CompletedTask;
        }

        public async Task StartAsync()
        {
            if (_config == null)
                throw new InvalidOperationException("Plugin not initialized");

            // Start timers
            _pollingTimer = new Timer { Interval = _config.PollingIntervalRxMs };
            _pollingTimer.Elapsed += OnPollingTimerElapsed;

            _pttWatchdogTimer = new Timer { Interval = _config.PttWatchdogIntervalMs };
            _pttWatchdogTimer.Elapsed += OnPttWatchdogElapsed;

            _meterTimer = new Timer { Interval = 1000 };
            _meterTimer.Elapsed += OnMeterTimerElapsed;
            _meterTimer.Start();

            // Connect to amplifier
            await ConnectAsync();

            Logger.LogInfo(ModuleName, "Plugin started");
        }

        public Task StopAsync()
        {
            Logger.LogInfo(ModuleName, "Stopping plugin");

            _pollingTimer?.Stop();
            _pttWatchdogTimer?.Stop();
            _meterTimer?.Stop();

            // Zero meters before stopping
            _forwardPower = 0;
            _swr = 1.0;
            RaiseMeterDataEvent();

            Disconnect();

            Logger.LogInfo(ModuleName, "Plugin stopped");
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _pollingTimer?.Dispose();
            _pttWatchdogTimer?.Dispose();
            _meterTimer?.Dispose();
            Disconnect();
        }

        #endregion

        #region IAmplifierPlugin Methods

        public AmplifierStatusData GetStatus()
        {
            return new AmplifierStatusData
            {
                OperateState = _operateState,
                IsPttActive = _isPtt,
                ForwardPower = _forwardPower,
                SWR = _swr,
                Temperature = _temperature
            };
        }

        public void SendPriorityCommand(AmpCommand command)
        {
            // CRITICAL: Priority commands for TX/RX interlock
            switch (command)
            {
                case AmpCommand.TX:
                    SendCommand("^TX15;");  // TX with 15 second timeout
                    _pttWatchdogTimer?.Start();
                    _pollingTimer!.Interval = _config!.PollingIntervalTxMs;
                    break;

                case AmpCommand.RX:
                    SendCommand("^RX;");
                    _pttWatchdogTimer?.Stop();
                    _pollingTimer!.Interval = _config!.PollingIntervalRxMs;
                    break;

                case AmpCommand.TXforTuneCarrier:
                    if (_config!.KeyAmpDuringTuneCarrier)
                    {
                        SendCommand("^TX15;");
                        _pttWatchdogTimer?.Start();
                    }
                    break;
            }
        }

        public void SetFrequencyKhz(int frequencyKhz)
        {
            // KPA500 auto-detects frequency from RF, but we can send it explicitly
            SendCommand($"^FR{frequencyKhz:D8};");
        }

        public void SetRadioConnected(bool connected)
        {
            if (!connected && _isPtt)
            {
                // SAFETY: Force to RX if radio disconnects during TX
                SendCommand("^RX;");
                _pttWatchdogTimer?.Stop();
                Logger.LogWarning(ModuleName, "Radio disconnected - forcing to RX");
            }
        }

        #endregion

        #region Private Methods

        private async Task ConnectAsync()
        {
            if (_config == null) return;

            SetConnectionState(PluginConnectionState.Connecting);

            try
            {
                _tcpClient = new TcpClient();
                await _tcpClient.ConnectAsync(_config.IpAddress, _config.Port);
                _stream = _tcpClient.GetStream();

                SetConnectionState(PluginConnectionState.Connected);
                _pollingTimer?.Start();

                // Start reading responses
                _ = Task.Run(ReadResponsesAsync);

                Logger.LogInfo(ModuleName, "Connected to KPA500");
            }
            catch (Exception ex)
            {
                Logger.LogError(ModuleName, $"Connection failed: {ex.Message}");
                SetConnectionState(PluginConnectionState.Error);
                ScheduleReconnect();
            }
        }

        private void Disconnect()
        {
            _stream?.Close();
            _tcpClient?.Close();
            _stream = null;
            _tcpClient = null;
            SetConnectionState(PluginConnectionState.Disconnected);
        }

        private void ScheduleReconnect()
        {
            if (_config == null || _disposed) return;

            Task.Delay(_config.ReconnectDelayMs).ContinueWith(_ =>
            {
                if (!_disposed)
                {
                    SetConnectionState(PluginConnectionState.Reconnecting);
                    _ = ConnectAsync();
                }
            });
        }

        private void SendCommand(string command)
        {
            if (_stream == null || !_tcpClient!.Connected) return;

            try
            {
                byte[] data = Encoding.ASCII.GetBytes(command);
                _stream.Write(data, 0, data.Length);
            }
            catch (Exception ex)
            {
                Logger.LogError(ModuleName, $"Send failed: {ex.Message}");
            }
        }

        private async Task ReadResponsesAsync()
        {
            byte[] buffer = new byte[1024];
            StringBuilder responseBuilder = new();

            while (!_disposed && _stream != null)
            {
                try
                {
                    int bytesRead = await _stream.ReadAsync(buffer, 0, buffer.Length, _cancellationToken);
                    if (bytesRead == 0) break;

                    responseBuilder.Append(Encoding.ASCII.GetString(buffer, 0, bytesRead));

                    // Process complete responses (terminated by ;)
                    string response = responseBuilder.ToString();
                    int semicolonIndex;
                    while ((semicolonIndex = response.IndexOf(';')) >= 0)
                    {
                        string completeResponse = response.Substring(0, semicolonIndex + 1);
                        ParseResponse(completeResponse);
                        response = response.Substring(semicolonIndex + 1);
                    }
                    responseBuilder.Clear();
                    responseBuilder.Append(response);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Logger.LogError(ModuleName, $"Read error: {ex.Message}");
                    ScheduleReconnect();
                    break;
                }
            }
        }

        private void ParseResponse(string response)
        {
            // Parse KPA500 CAT responses
            if (response.StartsWith("^OS"))
            {
                // Operating state: ^OS0; = Standby, ^OS1; = Operate
                var newState = response[3] == '1' ? AmpOperateState.Operate : AmpOperateState.Standby;
                if (newState != _operateState)
                {
                    _operateState = newState;
                    RaiseStatusChangedEvent(AmplifierStatusChanged.OperateStateChanged);
                }
            }
            else if (response.StartsWith("^WS"))
            {
                // Forward power: ^WS1234; = 1234 watts
                if (int.TryParse(response.AsSpan(3, response.Length - 4), out int watts))
                {
                    _forwardPower = watts;
                }
            }
            else if (response.StartsWith("^SW"))
            {
                // SWR: ^SW150; = 1.50:1 SWR
                if (int.TryParse(response.AsSpan(3, response.Length - 4), out int swrValue))
                {
                    _swr = swrValue / 100.0;
                }
            }
            else if (response.StartsWith("^TP"))
            {
                // Temperature: ^TP045; = 45 degrees C
                if (int.TryParse(response.AsSpan(3, response.Length - 4), out int temp))
                {
                    _temperature = temp;
                }
            }
            else if (response.StartsWith("^TX"))
            {
                _isPtt = true;
                RaiseStatusChangedEvent(AmplifierStatusChanged.PttStateChanged);
            }
            else if (response.StartsWith("^RX"))
            {
                _isPtt = false;
                RaiseStatusChangedEvent(AmplifierStatusChanged.PttStateChanged);
            }
        }

        private void OnPollingTimerElapsed(object? sender, ElapsedEventArgs e)
        {
            // Poll for status
            SendCommand("^OS;");  // Operating state
            SendCommand("^WS;");  // Forward power
            SendCommand("^SW;");  // SWR
            SendCommand("^TP;");  // Temperature
        }

        private void OnPttWatchdogElapsed(object? sender, ElapsedEventArgs e)
        {
            // Refresh PTT before timeout
            if (_isPtt)
            {
                SendCommand("^TX15;");
            }
        }

        private void OnMeterTimerElapsed(object? sender, ElapsedEventArgs e)
        {
            RaiseMeterDataEvent();
        }

        private void SetConnectionState(PluginConnectionState newState)
        {
            var previous = _connectionState;
            _connectionState = newState;
            ConnectionStateChanged?.Invoke(this, new PluginConnectionStateChangedEventArgs(previous, newState));
        }

        private void RaiseStatusChangedEvent(AmplifierStatusChanged whatChanged)
        {
            var status = GetStatus();
            status.WhatChanged = whatChanged;
            StatusChanged?.Invoke(this, new AmplifierStatusEventArgs(status, PluginId));
        }

        private void RaiseMeterDataEvent()
        {
            var readings = new Dictionary<MeterType, MeterReading>
            {
                [MeterType.ForwardPower] = new MeterReading(MeterType.ForwardPower, _forwardPower, MeterUnits.Watts),
                [MeterType.SWR] = new MeterReading(MeterType.SWR, _swr, MeterUnits.Ratio),
                [MeterType.Temperature] = new MeterReading(MeterType.Temperature, _temperature, MeterUnits.Celsius)
            };

            MeterDataAvailable?.Invoke(this, new MeterDataEventArgs(readings, _isPtt, PluginId));
        }

        #endregion
    }
}
```

---

### Sample 2: Tuner-Only Plugin (Kat500Plugin)

This example shows a tuner-only plugin for the Elecraft KAT500 external antenna tuner.

```csharp
#nullable enable

using System.Net.Sockets;
using System.Text;
using System.Timers;
using PgTg.Common;
using PgTg.Plugins.Core;
using PgTg.RADIO;
using Timer = System.Timers.Timer;

namespace PgTg.Plugins.Elecraft
{
    /// <summary>
    /// Configuration for the KAT500 tuner plugin.
    /// </summary>
    public class Kat500Configuration : ITunerConfiguration
    {
        public string PluginId { get; set; } = Kat500Plugin.PluginId;
        public bool Enabled { get; set; } = true;
        public string IpAddress { get; set; } = "192.168.1.101";
        public int Port { get; set; } = 1501;
        public int ReconnectDelayMs { get; set; } = 5000;
        public int TuneTimeoutMs { get; set; } = 30000;
    }

    /// <summary>
    /// Plugin for Elecraft KAT500 external antenna tuner (tuner-only).
    /// Implements ITunerPlugin interface.
    /// </summary>
    [PluginInfo("elecraft.kat500", "Elecraft KAT500",
        Version = "1.0.0",
        Manufacturer = "Elecraft",
        Capability = PluginCapability.Tuner,
        Description = "Elecraft KAT500 external antenna tuner")]
    public class Kat500Plugin : ITunerPlugin
    {
        public const string PluginId = "elecraft.kat500";
        private const string ModuleName = "Kat500Plugin";

        private readonly CancellationToken _cancellationToken;
        private Kat500Configuration? _config;
        private TcpClient? _tcpClient;
        private NetworkStream? _stream;
        private Timer? _pollingTimer;
        private Timer? _tuneTimeoutTimer;
        private Timer? _meterTimer;
        private bool _disposed;
        private TunerOperateState _operateState = TunerOperateState.Unknown;
        private TunerTuningState _tuningState = TunerTuningState.Unknown;
        private int _inductorValue;
        private int _capacitor1Value;
        private int _capacitor2Value;
        private double _lastSwr = 1.0;
        private PluginConnectionState _connectionState = PluginConnectionState.Disconnected;

        #region IDevicePlugin Implementation

        public PluginInfo Info { get; } = new PluginInfo
        {
            Id = PluginId,
            Name = "Elecraft KAT500",
            Version = "1.0.0",
            Manufacturer = "Elecraft",
            Capability = PluginCapability.Tuner,
            Description = "Elecraft KAT500 external antenna tuner",
            ConfigurationType = typeof(Kat500Configuration)
        };

        public PluginConnectionState ConnectionState => _connectionState;

        public event EventHandler<PluginConnectionStateChangedEventArgs>? ConnectionStateChanged;
        public event EventHandler<MeterDataEventArgs>? MeterDataAvailable;

        #endregion

        #region ITunerPlugin Implementation

        public event EventHandler<TunerStatusEventArgs>? TunerStatusChanged;

        #endregion

        public Kat500Plugin(CancellationToken cancellationToken)
        {
            _cancellationToken = cancellationToken;
        }

        #region Lifecycle Methods

        public Task InitializeAsync(IPluginConfiguration configuration, CancellationToken cancellationToken)
        {
            _config = configuration as Kat500Configuration ?? new Kat500Configuration
            {
                IpAddress = configuration.IpAddress,
                Port = configuration.Port,
                Enabled = configuration.Enabled,
                ReconnectDelayMs = configuration.ReconnectDelayMs
            };

            Logger.LogInfo(ModuleName, $"Initialized with config: {_config.IpAddress}:{_config.Port}");
            return Task.CompletedTask;
        }

        public async Task StartAsync()
        {
            if (_config == null)
                throw new InvalidOperationException("Plugin not initialized");

            // Start polling timer
            _pollingTimer = new Timer { Interval = 1000 };
            _pollingTimer.Elapsed += OnPollingTimerElapsed;

            // Tune timeout timer (doesn't auto-start)
            _tuneTimeoutTimer = new Timer { Interval = _config.TuneTimeoutMs, AutoReset = false };
            _tuneTimeoutTimer.Elapsed += OnTuneTimeoutElapsed;

            // Meter timer
            _meterTimer = new Timer { Interval = 1000 };
            _meterTimer.Elapsed += OnMeterTimerElapsed;
            _meterTimer.Start();

            // Connect to tuner
            await ConnectAsync();

            Logger.LogInfo(ModuleName, "Plugin started");
        }

        public Task StopAsync()
        {
            Logger.LogInfo(ModuleName, "Stopping plugin");

            _pollingTimer?.Stop();
            _tuneTimeoutTimer?.Stop();
            _meterTimer?.Stop();

            Disconnect();

            Logger.LogInfo(ModuleName, "Plugin stopped");
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _pollingTimer?.Dispose();
            _tuneTimeoutTimer?.Dispose();
            _meterTimer?.Dispose();
            Disconnect();
        }

        #endregion

        #region ITunerPlugin Methods

        public TunerStatusData GetTunerStatus()
        {
            return new TunerStatusData
            {
                OperateState = _operateState,
                TuningState = _tuningState,
                InductorValue = _inductorValue,
                Capacitor1Value = _capacitor1Value,
                Capacitor2Value = _capacitor2Value,
                LastSwr = _lastSwr
            };
        }

        public void SetInline(bool inline)
        {
            // KAT500: ATB0 = Bypass, ATB1 = Inline (Auto Tune Bypass)
            SendCommand(inline ? "ATB1;" : "ATB0;");
            Logger.LogInfo(ModuleName, $"Set tuner {(inline ? "inline" : "bypass")}");
        }

        public void StartTune()
        {
            // Start tune cycle
            SendCommand("AT1;");  // Auto-tune start
            _tuningState = TunerTuningState.TuningInProgress;
            _tuneTimeoutTimer?.Start();
            RaiseTunerStatusChangedEvent(Core.TunerStatusChanged.TuningStateChanged);
            Logger.LogInfo(ModuleName, "Tune cycle started");
        }

        public void StopTune()
        {
            // Abort tune cycle
            SendCommand("AT0;");  // Auto-tune abort
            _tuneTimeoutTimer?.Stop();
            _tuningState = TunerTuningState.NotTuning;
            RaiseTunerStatusChangedEvent(Core.TunerStatusChanged.TuningStateChanged);
            Logger.LogInfo(ModuleName, "Tune cycle aborted");
        }

        #endregion

        #region Private Methods

        private async Task ConnectAsync()
        {
            if (_config == null) return;

            SetConnectionState(PluginConnectionState.Connecting);

            try
            {
                _tcpClient = new TcpClient();
                await _tcpClient.ConnectAsync(_config.IpAddress, _config.Port);
                _stream = _tcpClient.GetStream();

                SetConnectionState(PluginConnectionState.Connected);
                _pollingTimer?.Start();

                // Start reading responses
                _ = Task.Run(ReadResponsesAsync);

                Logger.LogInfo(ModuleName, "Connected to KAT500");
            }
            catch (Exception ex)
            {
                Logger.LogError(ModuleName, $"Connection failed: {ex.Message}");
                SetConnectionState(PluginConnectionState.Error);
                ScheduleReconnect();
            }
        }

        private void Disconnect()
        {
            _stream?.Close();
            _tcpClient?.Close();
            _stream = null;
            _tcpClient = null;
            SetConnectionState(PluginConnectionState.Disconnected);
        }

        private void ScheduleReconnect()
        {
            if (_config == null || _disposed) return;

            Task.Delay(_config.ReconnectDelayMs).ContinueWith(_ =>
            {
                if (!_disposed)
                {
                    SetConnectionState(PluginConnectionState.Reconnecting);
                    _ = ConnectAsync();
                }
            });
        }

        private void SendCommand(string command)
        {
            if (_stream == null || !_tcpClient!.Connected) return;

            try
            {
                byte[] data = Encoding.ASCII.GetBytes(command);
                _stream.Write(data, 0, data.Length);
            }
            catch (Exception ex)
            {
                Logger.LogError(ModuleName, $"Send failed: {ex.Message}");
            }
        }

        private async Task ReadResponsesAsync()
        {
            byte[] buffer = new byte[1024];
            StringBuilder responseBuilder = new();

            while (!_disposed && _stream != null)
            {
                try
                {
                    int bytesRead = await _stream.ReadAsync(buffer, 0, buffer.Length, _cancellationToken);
                    if (bytesRead == 0) break;

                    responseBuilder.Append(Encoding.ASCII.GetString(buffer, 0, bytesRead));

                    string response = responseBuilder.ToString();
                    int semicolonIndex;
                    while ((semicolonIndex = response.IndexOf(';')) >= 0)
                    {
                        string completeResponse = response.Substring(0, semicolonIndex + 1);
                        ParseResponse(completeResponse);
                        response = response.Substring(semicolonIndex + 1);
                    }
                    responseBuilder.Clear();
                    responseBuilder.Append(response);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Logger.LogError(ModuleName, $"Read error: {ex.Message}");
                    ScheduleReconnect();
                    break;
                }
            }
        }

        private void ParseResponse(string response)
        {
            // Parse KAT500 responses
            if (response.StartsWith("ATB"))
            {
                // Bypass state: ATB0 = Bypass, ATB1 = Inline
                var newState = response[3] == '1' ? TunerOperateState.Inline : TunerOperateState.Bypass;
                if (newState != _operateState)
                {
                    _operateState = newState;
                    RaiseTunerStatusChangedEvent(Core.TunerStatusChanged.OperateStateChanged);
                }
            }
            else if (response.StartsWith("AT1"))
            {
                // Tune started
                _tuningState = TunerTuningState.TuningInProgress;
                RaiseTunerStatusChangedEvent(Core.TunerStatusChanged.TuningStateChanged);
            }
            else if (response.StartsWith("AT0") || response.StartsWith("ATD"))
            {
                // Tune completed or aborted (ATD = tune done)
                _tuneTimeoutTimer?.Stop();
                _tuningState = TunerTuningState.NotTuning;
                RaiseTunerStatusChangedEvent(Core.TunerStatusChanged.TuningStateChanged);
                Logger.LogInfo(ModuleName, $"Tune completed, SWR: {_lastSwr:F2}:1");
            }
            else if (response.StartsWith("SW"))
            {
                // SWR: SW150; = 1.50:1
                if (int.TryParse(response.AsSpan(2, response.Length - 3), out int swrValue))
                {
                    _lastSwr = swrValue / 100.0;
                }
            }
            else if (response.StartsWith("LC"))
            {
                // L/C values: LC128064032; = L=128, C1=064, C2=032
                if (response.Length >= 12)
                {
                    if (int.TryParse(response.AsSpan(2, 3), out int l)) _inductorValue = l;
                    if (int.TryParse(response.AsSpan(5, 3), out int c1)) _capacitor1Value = c1;
                    if (int.TryParse(response.AsSpan(8, 3), out int c2)) _capacitor2Value = c2;
                    RaiseTunerStatusChangedEvent(Core.TunerStatusChanged.RelayValuesChanged);
                }
            }
        }

        private void OnPollingTimerElapsed(object? sender, ElapsedEventArgs e)
        {
            SendCommand("ATB;");  // Query bypass state
            SendCommand("SW;");   // Query SWR
            SendCommand("LC;");   // Query L/C values
        }

        private void OnTuneTimeoutElapsed(object? sender, ElapsedEventArgs e)
        {
            // Tune timed out - abort
            Logger.LogWarning(ModuleName, "Tune timeout - aborting");
            StopTune();
        }

        private void OnMeterTimerElapsed(object? sender, ElapsedEventArgs e)
        {
            var readings = new Dictionary<MeterType, MeterReading>
            {
                [MeterType.TunerReturnLoss] = new MeterReading(MeterType.TunerReturnLoss,
                    _lastSwr > 1 ? 20 * Math.Log10((_lastSwr - 1) / (_lastSwr + 1)) : -40, MeterUnits.dB)
            };

            MeterDataAvailable?.Invoke(this, new MeterDataEventArgs(readings, false, PluginId));
        }

        private void SetConnectionState(PluginConnectionState newState)
        {
            var previous = _connectionState;
            _connectionState = newState;
            ConnectionStateChanged?.Invoke(this, new PluginConnectionStateChangedEventArgs(previous, newState));
        }

        private void RaiseTunerStatusChangedEvent(Core.TunerStatusChanged whatChanged)
        {
            var status = GetTunerStatus();
            status.WhatChanged = whatChanged;
            TunerStatusChanged?.Invoke(this, new TunerStatusEventArgs(status, PluginId));
        }

        #endregion
    }
}
```

---

### Sample 3: Combined Amplifier + Tuner Plugin (Kpa1500Plugin)

This example shows a combined plugin that provides both amplifier and tuner functionality in a single device (like the Elecraft KPA1500 with built-in ATU).

```csharp
#nullable enable

using System.Net.Sockets;
using System.Text;
using System.Timers;
using PgTg.AMP;
using PgTg.Common;
using PgTg.Plugins.Core;
using PgTg.RADIO;
using Timer = System.Timers.Timer;

namespace PgTg.Plugins.Elecraft
{
    /// <summary>
    /// Configuration for the KPA1500 combined amplifier/tuner plugin.
    /// </summary>
    public class Kpa1500SampleConfiguration : IAmplifierTunerConfiguration
    {
        public string PluginId { get; set; } = Kpa1500SamplePlugin.PluginId;
        public bool Enabled { get; set; } = true;
        public string IpAddress { get; set; } = "192.168.1.100";
        public int Port { get; set; } = 1500;
        public int ReconnectDelayMs { get; set; } = 5000;

        // Amplifier settings
        public int PollingIntervalRxMs { get; set; } = 1000;
        public int PollingIntervalTxMs { get; set; } = 20;
        public int PttWatchdogIntervalMs { get; set; } = 10000;
        public bool KeyAmpDuringTuneCarrier { get; set; } = true;

        // Tuner settings
        public int TuneTimeoutMs { get; set; } = 30000;
    }

    /// <summary>
    /// Combined plugin for Elecraft KPA1500 amplifier with built-in ATU.
    /// Implements IAmplifierTunerPlugin (both IAmplifierPlugin and ITunerPlugin).
    ///
    /// This demonstrates a single device providing both capabilities through
    /// one TCP connection, sharing status and meter data.
    /// </summary>
    [PluginInfo("elecraft.kpa1500.sample", "Elecraft KPA1500 (Sample)",
        Version = "1.0.0",
        Manufacturer = "Elecraft",
        Capability = PluginCapability.AmplifierAndTuner,
        Description = "Elecraft KPA1500 1500W amplifier with built-in ATU (Sample Implementation)")]
    public class Kpa1500SamplePlugin : IAmplifierTunerPlugin
    {
        public const string PluginId = "elecraft.kpa1500.sample";
        private const string ModuleName = "Kpa1500SamplePlugin";

        private readonly CancellationToken _cancellationToken;
        private Kpa1500SampleConfiguration? _config;
        private TcpClient? _tcpClient;
        private NetworkStream? _stream;
        private Timer? _pollingTimer;
        private Timer? _pttWatchdogTimer;
        private Timer? _tuneTimeoutTimer;
        private Timer? _meterTimer;
        private bool _disposed;

        // Amplifier state
        private bool _isPtt;
        private AmpOperateState _ampOperateState = AmpOperateState.Unknown;
        private double _forwardPower;
        private double _swr;
        private int _temperature;
        private int _paCurrent;
        private double _drivePower;

        // Tuner state
        private TunerOperateState _tunerOperateState = TunerOperateState.Unknown;
        private TunerTuningState _tuningState = TunerTuningState.Unknown;
        private int _inductorValue;
        private int _capacitor1Value;
        private int _capacitor2Value;
        private double _lastSwr = 1.0;

        private PluginConnectionState _connectionState = PluginConnectionState.Disconnected;

        #region IDevicePlugin Implementation

        public PluginInfo Info { get; } = new PluginInfo
        {
            Id = PluginId,
            Name = "Elecraft KPA1500 (Sample)",
            Version = "1.0.0",
            Manufacturer = "Elecraft",
            Capability = PluginCapability.AmplifierAndTuner,
            Description = "Elecraft KPA1500 1500W amplifier with built-in ATU (Sample Implementation)",
            ConfigurationType = typeof(Kpa1500SampleConfiguration)
        };

        public PluginConnectionState ConnectionState => _connectionState;

        public event EventHandler<PluginConnectionStateChangedEventArgs>? ConnectionStateChanged;
        public event EventHandler<MeterDataEventArgs>? MeterDataAvailable;

        #endregion

        #region IAmplifierPlugin Implementation

        public event EventHandler<AmplifierStatusEventArgs>? StatusChanged;

        #endregion

        #region ITunerPlugin Implementation

        public event EventHandler<TunerStatusEventArgs>? TunerStatusChanged;

        #endregion

        public Kpa1500SamplePlugin(CancellationToken cancellationToken)
        {
            _cancellationToken = cancellationToken;
        }

        #region Lifecycle Methods

        public Task InitializeAsync(IPluginConfiguration configuration, CancellationToken cancellationToken)
        {
            _config = configuration as Kpa1500SampleConfiguration ?? new Kpa1500SampleConfiguration
            {
                IpAddress = configuration.IpAddress,
                Port = configuration.Port,
                Enabled = configuration.Enabled,
                ReconnectDelayMs = configuration.ReconnectDelayMs
            };

            Logger.LogInfo(ModuleName, $"Initialized with config: {_config.IpAddress}:{_config.Port}");
            return Task.CompletedTask;
        }

        public async Task StartAsync()
        {
            if (_config == null)
                throw new InvalidOperationException("Plugin not initialized");

            // Polling timer (RX rate initially)
            _pollingTimer = new Timer { Interval = _config.PollingIntervalRxMs };
            _pollingTimer.Elapsed += OnPollingTimerElapsed;

            // PTT watchdog for amplifier
            _pttWatchdogTimer = new Timer { Interval = _config.PttWatchdogIntervalMs };
            _pttWatchdogTimer.Elapsed += OnPttWatchdogElapsed;

            // Tune timeout for tuner
            _tuneTimeoutTimer = new Timer { Interval = _config.TuneTimeoutMs, AutoReset = false };
            _tuneTimeoutTimer.Elapsed += OnTuneTimeoutElapsed;

            // Meter timer
            _meterTimer = new Timer { Interval = 1000 };
            _meterTimer.Elapsed += OnMeterTimerElapsed;
            _meterTimer.Start();

            // Connect to device
            await ConnectAsync();

            Logger.LogInfo(ModuleName, "Plugin started");
        }

        public Task StopAsync()
        {
            Logger.LogInfo(ModuleName, "Stopping plugin");

            _pollingTimer?.Stop();
            _pttWatchdogTimer?.Stop();
            _tuneTimeoutTimer?.Stop();
            _meterTimer?.Stop();

            // Zero meters
            _forwardPower = 0;
            _swr = 1.0;
            RaiseMeterDataEvent();

            Disconnect();

            Logger.LogInfo(ModuleName, "Plugin stopped");
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _pollingTimer?.Dispose();
            _pttWatchdogTimer?.Dispose();
            _tuneTimeoutTimer?.Dispose();
            _meterTimer?.Dispose();
            Disconnect();
        }

        #endregion

        #region IAmplifierPlugin Methods

        public AmplifierStatusData GetStatus()
        {
            return new AmplifierStatusData
            {
                OperateState = _ampOperateState,
                IsPttActive = _isPtt,
                ForwardPower = _forwardPower,
                SWR = _swr,
                Temperature = _temperature,
                PACurrent = _paCurrent,
                DrivePower = _drivePower
            };
        }

        public void SendPriorityCommand(AmpCommand command)
        {
            // CRITICAL: Priority commands bypass all queuing
            switch (command)
            {
                case AmpCommand.TX:
                    SendCommand("^TX15;");  // TX with 15 second timeout
                    _pttWatchdogTimer?.Start();
                    _pollingTimer!.Interval = _config!.PollingIntervalTxMs;
                    _meterTimer!.Interval = 20;  // Fast meter updates during TX
                    break;

                case AmpCommand.RX:
                    SendCommand("^RX;^FE;");  // RX and reset fault
                    _pttWatchdogTimer?.Stop();
                    _pollingTimer!.Interval = _config!.PollingIntervalRxMs;
                    _meterTimer!.Interval = 1000;  // Slow meter updates in RX
                    break;

                case AmpCommand.TXforTuneCarrier:
                    if (_config!.KeyAmpDuringTuneCarrier)
                    {
                        SendCommand("^TX15;");
                        _pttWatchdogTimer?.Start();
                    }
                    break;
            }
        }

        public void SetFrequencyKhz(int frequencyKhz)
        {
            SendCommand($"^FR{frequencyKhz:D8};");
        }

        public void SetRadioConnected(bool connected)
        {
            if (!connected && _isPtt)
            {
                // SAFETY: Force to RX if radio disconnects during TX
                SendCommand("^RX;");
                _pttWatchdogTimer?.Stop();
                Logger.LogWarning(ModuleName, "Radio disconnected - forcing to RX");
            }
        }

        #endregion

        #region ITunerPlugin Methods

        public TunerStatusData GetTunerStatus()
        {
            return new TunerStatusData
            {
                OperateState = _tunerOperateState,
                TuningState = _tuningState,
                InductorValue = _inductorValue,
                Capacitor1Value = _capacitor1Value,
                Capacitor2Value = _capacitor2Value,
                LastSwr = _lastSwr
            };
        }

        public void SetInline(bool inline)
        {
            // KPA1500: ^AMS0; = Bypass, ^AMS1; = Inline (ATU Mode Select)
            SendCommand(inline ? "^AMS1;" : "^AMS0;");
            Logger.LogInfo(ModuleName, $"Set tuner {(inline ? "inline" : "bypass")}");
        }

        public void StartTune()
        {
            SendCommand("^AT;");  // Start auto-tune
            _tuningState = TunerTuningState.TuningInProgress;
            _tuneTimeoutTimer?.Start();
            RaiseTunerStatusChangedEvent(Core.TunerStatusChanged.TuningStateChanged);
            Logger.LogInfo(ModuleName, "Tune cycle started");
        }

        public void StopTune()
        {
            SendCommand("^SWT;");  // Stop tuning
            _tuneTimeoutTimer?.Stop();
            _tuningState = TunerTuningState.NotTuning;
            RaiseTunerStatusChangedEvent(Core.TunerStatusChanged.TuningStateChanged);
            Logger.LogInfo(ModuleName, "Tune cycle stopped");
        }

        #endregion

        #region Connection and Communication

        private async Task ConnectAsync()
        {
            if (_config == null) return;

            SetConnectionState(PluginConnectionState.Connecting);

            try
            {
                _tcpClient = new TcpClient();
                await _tcpClient.ConnectAsync(_config.IpAddress, _config.Port);
                _stream = _tcpClient.GetStream();

                SetConnectionState(PluginConnectionState.Connected);
                _pollingTimer?.Start();

                _ = Task.Run(ReadResponsesAsync);

                Logger.LogInfo(ModuleName, "Connected to KPA1500");
            }
            catch (Exception ex)
            {
                Logger.LogError(ModuleName, $"Connection failed: {ex.Message}");
                SetConnectionState(PluginConnectionState.Error);
                ScheduleReconnect();
            }
        }

        private void Disconnect()
        {
            _stream?.Close();
            _tcpClient?.Close();
            _stream = null;
            _tcpClient = null;
            SetConnectionState(PluginConnectionState.Disconnected);
        }

        private void ScheduleReconnect()
        {
            if (_config == null || _disposed) return;

            Task.Delay(_config.ReconnectDelayMs).ContinueWith(_ =>
            {
                if (!_disposed)
                {
                    SetConnectionState(PluginConnectionState.Reconnecting);
                    _ = ConnectAsync();
                }
            });
        }

        private void SendCommand(string command)
        {
            if (_stream == null || !_tcpClient!.Connected) return;

            try
            {
                byte[] data = Encoding.ASCII.GetBytes(command);
                _stream.Write(data, 0, data.Length);
            }
            catch (Exception ex)
            {
                Logger.LogError(ModuleName, $"Send failed: {ex.Message}");
            }
        }

        private async Task ReadResponsesAsync()
        {
            byte[] buffer = new byte[1024];
            StringBuilder responseBuilder = new();

            while (!_disposed && _stream != null)
            {
                try
                {
                    int bytesRead = await _stream.ReadAsync(buffer, 0, buffer.Length, _cancellationToken);
                    if (bytesRead == 0) break;

                    responseBuilder.Append(Encoding.ASCII.GetString(buffer, 0, bytesRead));

                    string response = responseBuilder.ToString();
                    int semicolonIndex;
                    while ((semicolonIndex = response.IndexOf(';')) >= 0)
                    {
                        string completeResponse = response.Substring(0, semicolonIndex + 1);
                        ParseResponse(completeResponse);
                        response = response.Substring(semicolonIndex + 1);
                    }
                    responseBuilder.Clear();
                    responseBuilder.Append(response);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Logger.LogError(ModuleName, $"Read error: {ex.Message}");
                    ScheduleReconnect();
                    break;
                }
            }
        }

        private void ParseResponse(string response)
        {
            // AMPLIFIER responses
            if (response.StartsWith("^OS"))
            {
                var newState = response[3] switch
                {
                    '0' => AmpOperateState.Standby,
                    '1' => AmpOperateState.Operate,
                    _ => AmpOperateState.Unknown
                };
                if (newState != _ampOperateState)
                {
                    _ampOperateState = newState;
                    RaiseAmpStatusChangedEvent(AmplifierStatusChanged.OperateStateChanged);
                }
            }
            else if (response.StartsWith("^WS"))
            {
                if (int.TryParse(response.AsSpan(3, response.Length - 4), out int watts))
                    _forwardPower = watts;
            }
            else if (response.StartsWith("^SW"))
            {
                if (int.TryParse(response.AsSpan(3, response.Length - 4), out int swrValue))
                    _swr = swrValue / 100.0;
            }
            else if (response.StartsWith("^TP"))
            {
                if (int.TryParse(response.AsSpan(3, response.Length - 4), out int temp))
                    _temperature = temp;
            }
            else if (response.StartsWith("^PI"))
            {
                if (int.TryParse(response.AsSpan(3, response.Length - 4), out int current))
                    _paCurrent = current;
            }
            else if (response.StartsWith("^PD"))
            {
                if (int.TryParse(response.AsSpan(3, response.Length - 4), out int drive))
                    _drivePower = drive / 10.0;
            }
            else if (response.StartsWith("^TX"))
            {
                _isPtt = true;
                RaiseAmpStatusChangedEvent(AmplifierStatusChanged.PttReady);
            }
            else if (response.StartsWith("^RX"))
            {
                _isPtt = false;
                RaiseAmpStatusChangedEvent(AmplifierStatusChanged.PttStateChanged);
            }

            // TUNER responses
            else if (response.StartsWith("^AMS"))
            {
                var newState = response[4] == '1' ? TunerOperateState.Inline : TunerOperateState.Bypass;
                if (newState != _tunerOperateState)
                {
                    _tunerOperateState = newState;
                    RaiseTunerStatusChangedEvent(Core.TunerStatusChanged.OperateStateChanged);
                }
            }
            else if (response.StartsWith("^TU"))
            {
                // Tuning state: ^TU0; = not tuning, ^TU1; = tuning
                var newTuningState = response[3] == '1'
                    ? TunerTuningState.TuningInProgress
                    : TunerTuningState.NotTuning;

                if (_tuningState == TunerTuningState.TuningInProgress &&
                    newTuningState == TunerTuningState.NotTuning)
                {
                    // Tune cycle completed
                    _tuneTimeoutTimer?.Stop();
                    _lastSwr = _swr;  // Capture final SWR
                    Logger.LogInfo(ModuleName, $"Tune completed, SWR: {_lastSwr:F2}:1");
                }

                if (newTuningState != _tuningState)
                {
                    _tuningState = newTuningState;
                    RaiseTunerStatusChangedEvent(Core.TunerStatusChanged.TuningStateChanged);
                }
            }
            else if (response.StartsWith("^RL"))
            {
                // Relay values: ^RL128064032; = L=128, C1=064, C2=032
                if (response.Length >= 12)
                {
                    if (int.TryParse(response.AsSpan(3, 3), out int l)) _inductorValue = l;
                    if (int.TryParse(response.AsSpan(6, 3), out int c1)) _capacitor1Value = c1;
                    if (int.TryParse(response.AsSpan(9, 3), out int c2)) _capacitor2Value = c2;
                    RaiseTunerStatusChangedEvent(Core.TunerStatusChanged.RelayValuesChanged);
                }
            }
        }

        #endregion

        #region Timer Handlers

        private void OnPollingTimerElapsed(object? sender, ElapsedEventArgs e)
        {
            // Poll amplifier status
            SendCommand("^OS;");  // Operating state
            SendCommand("^WS;");  // Forward power
            SendCommand("^SW;");  // SWR
            SendCommand("^TP;");  // Temperature
            SendCommand("^PI;");  // PA current
            SendCommand("^PD;");  // Drive power

            // Poll tuner status
            SendCommand("^AMS;"); // ATU mode
            SendCommand("^TU;");  // Tuning state
            SendCommand("^RL;");  // Relay values
        }

        private void OnPttWatchdogElapsed(object? sender, ElapsedEventArgs e)
        {
            if (_isPtt)
            {
                SendCommand("^TX15;");  // Refresh PTT before 15s timeout
            }
        }

        private void OnTuneTimeoutElapsed(object? sender, ElapsedEventArgs e)
        {
            Logger.LogWarning(ModuleName, "Tune timeout - aborting");
            StopTune();
        }

        private void OnMeterTimerElapsed(object? sender, ElapsedEventArgs e)
        {
            RaiseMeterDataEvent();
        }

        #endregion

        #region Event Helpers

        private void SetConnectionState(PluginConnectionState newState)
        {
            var previous = _connectionState;
            _connectionState = newState;
            ConnectionStateChanged?.Invoke(this, new PluginConnectionStateChangedEventArgs(previous, newState));
        }

        private void RaiseAmpStatusChangedEvent(AmplifierStatusChanged whatChanged)
        {
            var status = GetStatus();
            status.WhatChanged = whatChanged;
            StatusChanged?.Invoke(this, new AmplifierStatusEventArgs(status, PluginId));
        }

        private void RaiseTunerStatusChangedEvent(Core.TunerStatusChanged whatChanged)
        {
            var status = GetTunerStatus();
            status.WhatChanged = whatChanged;
            TunerStatusChanged?.Invoke(this, new TunerStatusEventArgs(status, PluginId));
        }

        private void RaiseMeterDataEvent()
        {
            // Combined meter data from both amplifier and tuner functions
            var readings = new Dictionary<MeterType, MeterReading>
            {
                // Amplifier meters
                [MeterType.ForwardPower] = new MeterReading(MeterType.ForwardPower, _forwardPower, MeterUnits.Watts),
                [MeterType.SWR] = new MeterReading(MeterType.SWR, _swr, MeterUnits.Ratio),
                [MeterType.Temperature] = new MeterReading(MeterType.Temperature, _temperature, MeterUnits.Celsius),
                [MeterType.PACurrent] = new MeterReading(MeterType.PACurrent, _paCurrent, MeterUnits.Amps),
                [MeterType.DrivePower] = new MeterReading(MeterType.DrivePower, _drivePower, MeterUnits.Watts),

                // Tuner meters (return loss calculated from SWR)
                [MeterType.TunerReturnLoss] = new MeterReading(MeterType.TunerReturnLoss,
                    _lastSwr > 1 ? -20 * Math.Log10((_lastSwr - 1) / (_lastSwr + 1)) : 40, MeterUnits.dB)
            };

            MeterDataAvailable?.Invoke(this, new MeterDataEventArgs(readings, _isPtt, PluginId));
        }

        #endregion
    }
}
```

---

## Key Differences Between Plugin Types

| Aspect | Amplifier-Only | Tuner-Only | Combined |
|--------|---------------|------------|----------|
| Interface | `IAmplifierPlugin` | `ITunerPlugin` | `IAmplifierTunerPlugin` |
| Configuration | `IAmplifierConfiguration` | `ITunerConfiguration` | `IAmplifierTunerConfiguration` |
| Capability | `PluginCapability.Amplifier` | `PluginCapability.Tuner` | `PluginCapability.AmplifierAndTuner` |
| PTT Control | Yes (`SendPriorityCommand`) | No | Yes |
| Tune Control | No | Yes (`StartTune`, `StopTune`) | Yes |
| Events | `StatusChanged` | `TunerStatusChanged` | Both |
| Status Data | `AmplifierStatusData` | `TunerStatusData` | Both |
| Connection | Independent | Independent | Shared single connection |

---

## Reference Implementation: KPA1500 Plugin

The `Kpa1500Plugin` in `PgTg/Plugins/Elecraft/` is the built-in reference for the `IAmplifierTunerPlugin` pattern:

| File | Purpose |
|------|---------|
| `Kpa1500Plugin.cs` | Main plugin class, implements `IAmplifierTunerPlugin` |
| `Kpa1500Configuration.cs` | Plugin-specific configuration |
| `Internal/Kpa1500TcpConnection.cs` | TCP connection with auto-reconnect |
| `Internal/Kpa1500CommandQueue.cs` | Priority commands, polling, PTT watchdog |
| `Internal/Kpa1500ResponseParser.cs` | CAT protocol parsing |
| `Internal/Kpa1500StatusTracker.cs` | State machine, change detection |
| `Internal/Kpa1500Constants.cs` | Timing values, command strings |

### Sample Plugin Reference Implementations

For external plugin development, the sample projects under `PgTgSamplePlugins/` are the primary reference:

| Plugin | Interface | Key Patterns Demonstrated |
|--------|-----------|--------------------------|
| `SampleAmp/MyModel/` | `IAmplifierPlugin` | TCP+Serial via `IConnection`, PTT watchdog, device init enabled, Device Control definitions |
| `SampleTuner/MyModel/` | `ITunerPlugin` | TCP+Serial via `IConnection`, tune timeout, device init disabled, Device Control definitions |
| `SampleAmpTuner/MyModel/` | `IAmplifierTunerPlugin` | Combined amp+tuner state, both amp and tuner change detection, Device Control definitions |

Each sample's `Internal/` folder contains the full set of 7 component files (`IConnection`, `TcpConnection`, `SerialConnection`, `Constants`, `CommandQueue`, `ResponseParser`, `StatusTracker`) with annotated source demonstrating every pattern described in this document.

---

## Summary Checklist

When creating a new plugin:

- [ ] Choose appropriate interface (`IAmplifierPlugin`, `ITunerPlugin`, or `IAmplifierTunerPlugin`)
- [ ] Create configuration class implementing appropriate interface
- [ ] Add `[PluginInfo]` attribute to plugin class
- [ ] Implement all lifecycle methods (`InitializeAsync`, `StartAsync`, `StopAsync`, `Dispose`)
- [ ] Implement device-specific communication
- [ ] Handle reconnection gracefully
- [ ] Implement priority command handling for amplifiers (PTT safety)
- [ ] Raise appropriate events (`StatusChanged`, `MeterDataAvailable`, `ConnectionStateChanged`)
- [ ] Register in `PluginFactory` (for built-in plugins)
- [ ] Add unit and integration tests
- [ ] Test PTT interlock timing thoroughly
- [ ] Implement `GetDeviceControlDefinition()` for Device Control panel UI
- [ ] Add `FanControlDefinition` to the definition if the device has a variable-speed fan

---
## Key Architecture Changes
- Plugin lifecycle: Plugins are initialized in constructor via InitializePluginManager() and started in StartAsync() via _pluginManager.StartAllAsync()
- Event routing: Amplifier commands now flow through _activeAmplifier (IAmplifierPlugin) instead of _kpa1500Client
- Meter data: Plugin raises MeterDataAvailable events → PluginManager aggregates → Bridge receives via MeterDataReceived event → forwards to VitaMeterSender
- Status queries: Plugin provides current status via GetStatus() / GetTunerStatus(), converted to PGXL/TGXL format for radio protocol


_pttLatencyStopwatch - Stopwatch for measuring elapsed time
_pttLatencyMeasurementActive - Flag to track if measurement is in progress
Start timer in AmpClient_SendAmplifierCatCommand (lines 720-726):

When AmpCommand.TX or AmpCommand.TXforTuneCarrier is sent, the stopwatch is started
This happens when the radio sends PTT_REQUESTED
Stop timer and log in PluginManager_AmplifierStatusChanged (lines 295-301):
When AmplifierStatusChanged.PttReady is received (meaning ^TX was parsed from the amplifier)

Logs: "Amplifier PTT latency = <value> ms" at Verbose level

The timing captures the complete round-trip:
Radio sends PTT_REQUESTED → InterlockBase detects it → Bridge sends to plugin → Plugin sends ^TX15; to amplifier → Amplifier responds with ^TX; → Plugin parses it → Raises PttReady event → Bridge logs latency

---
## Version History

| Version | Date | Changes |
|---------|------|---------|
| 1.0 | December 2025 | Initial plugin architecture release |
| 1.1 | March 2026 | Phase 4 doc update: MyModel/Internal architecture pattern, $-prefix protocol reference, multi-transport guidance, timer disposal order fix, sample project file trees, LogLudicrous → LogVerbose |
| 1.2 | March 2026 | Device Control panel integration for external plugins: `GetDeviceControlDefinition()`, dynamic LED rendering, `DeviceControlElement`/`DeviceControlDefinition` types |
| 1.3 | April 2026 | Fan speed control row for external plugins: `FanControlDefinition`, `FanControl` property on `DeviceControlDefinition`, `"FN"` reserved key, updated sample plugins (SampleAmp/SampleTuner/SampleAmpTuner) |

---

*Document generated for PgTgBridge Plugin Architecture v1.0*
