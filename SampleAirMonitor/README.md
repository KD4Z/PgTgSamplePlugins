# Sample Air Monitor Plugin

A PgTgBridge GPIO output plugin that sends radio frequency and mode commands to a transceiver using either **CAT** (Kenwood/Elecraft) or **CI-V** (Icom) protocol over TCP or serial connections.

## Overview

**Plugin ID:** `sample.airmonitor`
**Interface:** `IGpioOutputPlugin`
**Capability:** `PluginCapability.FrequencyModeMonitoring`

This plugin receives frequency and mode updates from the PgTgBridge and forwards them to a transceiver using CAT or CI-V protocol. 

## Features

- **Dual protocol support** — CAT (ASCII text) or CI-V (binary) selectable via configuration
- **TCP and serial connections** with configurable auto-reconnect delay
- **Change detection** — only sends commands when frequency or mode actually changes
- **Reconnect resend** — automatically resends last known frequency and mode when connection is restored


## Configuration

| Property | Default | Description |
|---|---|---|
| `ConnectionType` | `TCP` | `TCP` or `Serial` |
| `IpAddress` | `192.168.1.100` | TCP host address |
| `Port` | `5000` | TCP port number |
| `SerialPort` | `COM1` | Serial COM port |
| `BaudRate` | `38400` | Serial baud rate |
| `ReconnectDelayMs` | `5000` | Delay (ms) before reconnect attempts |
| `PluginFreqModeProtocol` | `1` | Protocol: `1` = CAT, `2` = CI-V |
| `CivControllerAddress` | `0xE0` | CI-V controller address (PC side) |
| `CivTransceiverAddress` | `0x94` | CI-V transceiver address (radio side) |

## Protocols

### CAT (PluginFreqModeProtocol = 1)

Kenwood/Elecraft-style ASCII commands terminated with `;`.

**Set frequency** — `FA` followed by 11-digit frequency in Hz:
```
FA00014060000;     ← 14060 kHz (14.060 MHz)
FA00007203000;     ← 7203 kHz (7.203 MHz)
```

**Set mode** — `MD` followed by a mode digit:
```
MD1;    ← LSB
MD2;    ← USB
MD3;    ← CW
MD4;    ← FM
MD5;    ← AM
MD6;    ← RTTY
MD7;    ← CW-R
MD9;    ← RTTY-R
```

### CI-V (PluginFreqModeProtocol = 2)

Icom CI-V binary protocol. All frames begin with `FE FE` preamble and end with `FD`.

**Set frequency** — Command `05`, 5-byte BCD frequency (LSB first):
```
FE FE 94 E0 05 00 00 60 40 01 FD     ← 14060 kHz
│  │  │  │  │  └──────────────┘ │
│  │  │  │  │   BCD freq data   │
│  │  │  │  └── cmd: set freq   │
│  │  │  └── from: controller   │
│  │  └── to: transceiver       │
│  └── preamble                 │
└── preamble              end ──┘
```

**Set mode** — Command `06`, mode byte + filter:
```
FE FE 94 E0 06 01 01 FD     ← USB, normal filter
```

| Mode | Byte |
|------|------|
| LSB | `0x00` |
| USB | `0x01` |
| AM | `0x02` |
| CW | `0x03` |
| RTTY | `0x04` |
| FM | `0x05` |
| CW-R | `0x07` |
| RTTY-R | `0x08` |


## Project Structure

```
MyModel/
    SampleAirMonitor.cs              # Configuration (IPluginConfiguration)
    SampleAirMonitorPlugin.cs        # Plugin class (IGpioOutputPlugin)
    Internal/
        IConnection.cs               # Transport abstraction (TCP + Serial)
        TcpConnection.cs             # TCP client with auto-reconnect
        SerialConnection.cs          # Serial port client with auto-reconnect
        CommandQueue.cs              # Immediate command dispatch (no polling)
        CatProtocolBuilder.cs        # CAT frequency/mode command builder
        CivProtocolBuilder.cs        # CI-V frequency/mode frame builder
        ResponseParser.cs            # Parses $ACK; acknowledgments
        StatusTracker.cs             # Thread-safe state + change detection
        Constants.cs                 # Protocol bytes, command strings
```

## Building

Requires PgTgBridge installed on the development workstation to resolve assembly references.

```bash
dotnet build SampleAirMonitor.csproj
```

Copy the output DLL to the PgTg plugins directory and configure it in PgTgController Settings - Plugin Manager.
Typical path is: `C:\Program Files\PgTgBridge\plugins`

## Creating Your Own Plugin

1. Copy this project and rename the namespace, class names, and `PluginId`.
2. Set `PluginFreqModeProtocol` to match your target device (CAT or CI-V).
3. For CI-V devices, set the correct controller and transceiver addresses.
4. Update `Constants.cs` if your device uses different command strings.
5. Update `ResponseParser.cs` to decode your device's acknowledgment format.
6. Update the `[PluginInfo(...)]` attribute with your plugin's ID, name, and manufacturer.
