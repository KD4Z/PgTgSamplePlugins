# PgTg Bridge WebSocket API Reference

Available starting with PgTgBridge build #1000

All examples use `127.0.0.1` (loopback) and assume the client is on the same host as PgTgBridge. Replace `127.0.0.1` with the actual IP address of the host running PgTgBridge when connecting from another machine. The service binds to all network interfaces, so remote connections are fully supported.

One important operational note: on Windows, the Websocket HttpListener requires either running as Administrator or a pre-registered URL ACL. If the service is installed as a Windows Service running under a privileged account (LocalSystem/Admin), this is already satisfied. 

If remote clients can't connect, the Windows Firewall inbound rule for port 4990 also needs to allow it.  If you chose to use a non-priviledged account during the PgTgBridge installer, the ACL and firewall rules have already been setup. If you change the port from the default, you will need to change the inbound firewall rule and add the ACL as well.  

The scripts directory under the PgTgBridge deployment has the PowerShell scripts used to Add/Remove the ACL and firewall rules made during PgTgBridge installation.  You may use these as a guide if you need customization.

## Overview

The PgTg Bridge service exposes three WebSocket endpoints for external clients to control the bridge, receive real-time telemetry data, and monitor device status from Elecraft amplifiers and tuners.

| Property | Value |
|----------|-------|
| **Protocol** | WebSocket over HTTP (`ws://`) |
| **Host** | Any interface (bind to all NICs); use the host's IP from remote clients |
| **Default Port** | `4990` (configurable via SettingsConfig.json) |
| **Encoding** | UTF-8 JSON |
| **Serialization** | camelCase property names, null fields omitted |

---

## Endpoints

### `/command` — Bidirectional Command/Response

Used to send control commands (start, stop, restart) and receive responses. The server also pushes unsolicited `statusChange` messages to all connected `/command` clients when the bridge state transitions.

**URL:** `ws://127.0.0.1:4990/command`

### `/data` — Push-Only Telemetry

Used to receive real-time meter readings, frequency updates, and meter configuration. Clients cannot send messages on this endpoint; any messages sent are silently ignored.

Fields pushed: ForwardPower, SWR, Temperature, DrivePower, PACurrent.

**URL:** `ws://127.0.0.1:4990/data`

### `/device` — Bidirectional Device Status & Commands

Used to receive event-driven snapshots of device polled data (fault codes, power state, antenna selection, operate/standby mode, tuner mode and bypass state, etc.) pushed whenever a change is detected from any enabled plugin, and to send raw commands to specific devices.

Completely isolated from the `/data` meter pipeline to avoid impacting real-time meter responsiveness.

**URL:** `ws://127.0.0.1:4990/device`

---

## Message Types

All messages are JSON objects with a `type` field used for routing:

| Type | Direction | Endpoint | Description |
|------|-----------|----------|-------------|
| `command` | Client → Server | `/command` | Send a command to the bridge |
| `response` | Server → Client | `/command` | Immediate reply to a command |
| `statusChange` | Server → Client | `/command` | Unsolicited state transition push |
| `meterConfig` | Server → Client | `/data` | Meter definitions (sent on connect and when config changes) |
| `meterData` | Server → Client | `/data` | Real-time meter readings |
| `txFrequency` | Server → Client | `/data` | TX frequency and band changes |
| `deviceData` | Server → Client | `/device` | Periodic device polled status snapshots |
| `deviceCommand` | Client → Server | `/device` | Send a raw command to a specific device |
| `deviceCommandResponse` | Server → Client | `/device` | Response to a device command |

---

## `/command` Endpoint

### Sending Commands

Send a JSON message with `type: "command"` and the command name in `command`. Some commands accept an optional `data` field for additional parameters:

```json
{
  "type": "command",
  "command": "RequestStart"
}
```

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `type` | string | Yes | Always `"command"` |
| `command` | string | Yes | The command name (case-insensitive) |
| `data` | string | No | Additional data for commands that require it (e.g., `RequestSendToast`) |

### Command Reference

| Command | Description | Response State | Notes |
|---------|-------------|----------------|-------|
| `RequestStart` | Start the bridge (connect to radio) | `Starting` | Async; watch for `statusChange` → `Running` |
| `RequestStop` | Stop the bridge gracefully | `Stopping` | Async; watch for `statusChange` → `ReadyToStart` |
| `RequestRestart` | Restart the bridge | `Restarting` | Fire-and-forget; ~15 second cycle |
| `RequestStatus` | Poll current bridge state | *(current state)* | No side effects |
| `RequestRadioList` | Get discovered radios | `RequestRadioList` | Radio list JSON in `data` field |
| `RequestSendToast` | Display a toast message on the radio | *(current state)* | Message text in `data` field |

Commands are **case-insensitive** (e.g., `"requeststart"` works the same as `"RequestStart"`).

### Command Response

The server replies immediately with a `response` message:

```json
{
  "type": "response",
  "command": "RequestStart",
  "state": "Starting",
  "data": null
}
```

| Field | Type | Description |
|-------|------|-------------|
| `command` | string | Echo of the command that was sent |
| `state` | string | Resulting bridge state (see Bridge States below) |
| `data` | object/null | Additional data (used by `RequestRadioList`, `RequestSendToast`) |

### Status Change Push

When the bridge transitions between states, all connected `/command` clients receive an unsolicited push:

```json
{
  "type": "statusChange",
  "state": "Running"
}
```

### Bridge States

| State | Description |
|-------|-------------|
| `Initializing` | Service is starting up |
| `WaitingForDebugger` | DEBUG builds only: waiting for debugger attach |
| `ReadyToStart` | Bridge stopped, ready to accept `RequestStart` |
| `Starting` | Bridge connecting to radio |
| `Running` | Bridge connected and operational |
| `Stopping` | Bridge shutting down |
| `Restarting` | Bridge performing restart (~15 second cycle) |
| `Error` | Fatal error occurred |
| `TrialExpired` | Trial license expired |
| `NotDetected` | Controller cannot reach service (client-side only) |
| `UnknownCommand` | Server received unrecognized command |
| `EmptyCommand` | Server received empty command |

### Typical State Transitions

```
ReadyToStart ──RequestStart──► Starting ──► Running
                                              │
                              ┌───────────────┤
                              ▼               ▼
                         RequestStop    RequestRestart
                              │               │
                              ▼               ▼
                          Stopping       Restarting
                              │               │ (~15s)
                              ▼               ▼
                        ReadyToStart      Starting ──► Running
```

On radio disconnect, the bridge automatically transitions to `Restarting` and attempts reconnection.

---

## `/data` Endpoint

### Connection Lifecycle

1. Client connects to `ws://127.0.0.1:4990/data`
2. Server immediately sends a `meterConfig` message describing available meters
3. Server pushes `meterData` and `txFrequency` messages while the bridge is running
4. If the meter configuration changes (e.g., plugin reload), a new `meterConfig` is sent

### Meter Configuration

Sent once on connect and again whenever the meter list changes:

```json
{
  "type": "meterConfig",
  "meters": [
    {
      "name": "AMP_FWD",
      "units": "Watts",
      "min": 0,
      "max": 1500
    },
    {
      "name": "AMP_RL",
      "units": "SWR",
      "min": 1.0,
      "max": 3.0
    },
    {
      "name": "AMP_TEMP",
      "units": "C",
      "min": 0,
      "max": 60
    },
    {
      "name": "TUNER_FWD",
      "units": "Watts",
      "min": 0,
      "max": 600
    },
    {
      "name": "TUNER_RL",
      "units": "SWR",
      "min": 1.0,
      "max": 3.0
    }
  ]
}
```

**Available meters depend on the active plugin:**

| Plugin | Capability | Meters |
|--------|-----------|--------|
| KPA1500 | AmplifierAndTuner | AMP_FWD, AMP_RL, AMP_TEMP, TUNER_FWD, TUNER_RL |
| KPA500 | Amplifier | AMP_FWD, AMP_RL, AMP_TEMP |
| KAT500 | Tuner | TUNER_FWD, TUNER_RL |

**Meter details:**

| Name | Units | Description | Max Scale |
|------|-------|-------------|-----------|
| `AMP_FWD` | Watts | Amplifier forward power | Plugin-defined (KPA1500: 1500W, KPA500: 600W) |
| `AMP_RL` | SWR | Amplifier SWR | From meter list |
| `AMP_TEMP` | C | Amplifier temperature (Celsius) | From meter list |
| `TUNER_FWD` | Watts | Tuner forward power | Plugin-defined (KAT500: 600W) |
| `TUNER_RL` | SWR | Tuner SWR | From meter list |

### Meter Data

Pushed periodically while the bridge is running:

```json
{
  "type": "meterData",
  "timestamp": "2026-02-07T14:23:45.1234567Z",
  "isTxMode": true,
  "readings": {
    "AMP_FWD": {
      "value": 750.5,
      "units": "Watts",
      "min": 0,
      "max": 1500
    },
    "AMP_RL": {
      "value": 1.3,
      "units": "SWR",
      "min": 1.0,
      "max": 3.0
    },
    "AMP_TEMP": {
      "value": 42.3,
      "units": "C",
      "min": 0,
      "max": 60
    }
  }
}
```

| Field | Type | Description |
|-------|------|-------------|
| `timestamp` | ISO 8601 UTC | When the reading was taken |
| `isTxMode` | boolean | `true` if transmitting, `false` if receiving |
| `readings` | object | Map of meter name → reading |

**Push rate:**
- **TX mode:** Up to 10 updates/second (100ms throttle)
- **RX mode:** Up to 1 update/second (1000ms throttle)

### TX Frequency

Pushed when the radio's transmit frequency changes:

```json
{
  "type": "txFrequency",
  "frequencyKhz": 14200,
  "band": "20m"
}
```

| Field | Type | Description |
|-------|------|-------------|
| `frequencyKhz` | integer | Frequency in kilohertz |
| `band` | string | Band name (160m, 80m, 60m, 40m, 30m, 20m, 17m, 15m, 12m, 10m, 6m) |

---

## `/device` Endpoint

### Connection Lifecycle

1. Client connects to `ws://127.0.0.1:4990/device`
2. Server immediately sends a `deviceData` snapshot with current values from all enabled plugins
3. Server pushes updated `deviceData` messages whenever any polled field changes (event-driven, not on a fixed timer)
4. Only plugins that are enabled and loaded appear in the `devices` array

### Device Data

Pushed immediately when any polled field changes:

```json
{
  "type": "deviceData",
  "devices": [
    {
      "deviceId": "elecraft.kpa1500",
      "deviceName": "Elecraft KPA1500",
      "data": {
        "AN": 1,
        "ON": 1,
        "LQ": {
          "tx": false,
          "operate": true,
          "atuBypass": false,
          "atuInline": true,
          "overload": false,
          "fault": false
        },
        "BN": 5,
        "AI": 1,
        "FL": 0,
        "FS": 2,
        "OS": 1
      }
    }
  ]
}
```

| Field | Type | Description |
|-------|------|-------------|
| `devices` | array | One entry per enabled plugin with data. Empty array if no plugins loaded. |
| `devices[].deviceId` | string | Plugin identifier (e.g., `"elecraft.kpa1500"`) |
| `devices[].deviceName` | string | Human-readable device name (e.g., `"Elecraft KPA1500"`) |
| `devices[].data` | object | Map of command ID to last-polled value |

### Command IDs by Device

#### KAT500 (Tuner)

| Command ID | Type | Description | Values |
|------------|------|-------------|--------|
| `MD` | string | Tuner mode | `"A"` = Auto, `"M"` = Manual, `"B"` = Bypass |
| `BYP` | string | Bypass relay state (polled independently) | `"B"` = bypassed, `"N"` = inline |
| `AN` | integer | Selected antenna port | 0-3 |
| `FLT` | integer | Fault code | 0 = no fault |
| `PS` | integer | Power status | 0 = off, 1 = on |

> **Note:** `BYP` and `MD` both reflect bypass state but are polled separately. `BYP` (`BYPB;`/`BYPN;`) is the hardware relay state; `MD` reports the mode setting. Use `BYP` as the authoritative source for the bypass indicator — `MD="B"` may lag slightly behind `BYP="B"` during transitions.

#### KPA1500 (Amplifier + Tuner)

| Command ID | Type | Description | Values |
|------------|------|-------------|--------|
| `AN` | integer | Selected antenna port | 1-4 |
| `ON` | integer | Power on/off | 0 = off, 1 = on |
| `LQ` | object | LED panel query (see below) | Nested boolean fields |
| `BN` | integer | Band number | 0-10 |
| `AI` | integer | ATU inline/bypass | 0 = bypass, 1 = inline |
| `FL` | integer | Fault code | 0 = no fault |
| `FS` | integer | Fan speed | 0-6 |
| `OS` | integer | Operate/standby | 0 = standby, 1 = operate |

**LQ (LED Query) nested object:**

| Field | Type | Description |
|-------|------|-------------|
| `tx` | boolean | PTT active (transmitting) |
| `operate` | boolean | Amplifier in operate mode |
| `atuBypass` | boolean | ATU is bypassed |
| `atuInline` | boolean | ATU is inline |
| `overload` | boolean | Overload condition |
| `fault` | boolean | Fault condition active |

#### KPA500 (Amplifier)

| Command ID | Type | Description | Values |
|------------|------|-------------|--------|
| `FL` | integer | Fault code | 0 = no fault |
| `BN` | integer | Band number | 0-10 |
| `OS` | integer | Operate/standby | 0 = standby, 1 = operate |
| `ON` | integer | Power on/off | 0 = off, 1 = on |

**Push rate:** Immediately on any change to a polled field (event-driven). A full snapshot is also sent when a client first connects.

### Sending Device Commands

Clients can send raw commands to a specific device plugin by sending a `deviceCommand` message on the `/device` endpoint. The server validates the command and responds with a `deviceCommandResponse`.

**Send:**
```json
{
  "type": "deviceCommand",
  "deviceId": "elecraft.kpa1500",
  "command": "^ON1;"
}
```

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `type` | string | Yes | Always `"deviceCommand"` |
| `deviceId` | string | Yes | Plugin identifier (e.g., `"elecraft.kpa1500"`) |
| `command` | string | Yes | Raw command string to send to the device |

**Response:**
```json
{
  "type": "deviceCommandResponse",
  "deviceId": "elecraft.kpa1500",
  "command": "^ON1;",
  "success": true
}
```

| Field | Type | Description |
|-------|------|-------------|
| `deviceId` | string | Echo of the target device ID |
| `command` | string | Echo of the command that was sent |
| `success` | boolean | `true` if the command was sent to the device |
| `error` | string/null | Error message if `success` is `false` |

### Writable vs Read-Only Commands

Read-only command IDs are rejected to prevent clients from interfering with the polling pipeline. `FLC` (fault clear) is accepted even though it is not a polled value.

The sender is responsible for the correct command prefix — KPA500 and KPA1500 commands require a `^` prefix, while KAT500 commands do not.

| Device | Writable Commands | Read-Only (blocked) | Write-Only (not polled) |
|--------|------------------|---------------------|------------------------|
| KAT500 | MD, BYP, AN, PS | FLT | FLC |
| KPA1500 | AN, ON, BN, AI, FS, OS | LQ, FL | FLC |
| KPA500 | BN, OS, ON | FL | FLC |

**KAT500 bypass toggle:** Send `BYPB;` to engage bypass, `BYPN;` to disengage. The `BYP` field in `deviceData` reflects the current relay state polled from the device.

---

## Examples

### Example 1: Start the Bridge

**Send:**
```json
{"type":"command","command":"RequestStart"}
```

**Immediate response:**
```json
{"type":"response","command":"RequestStart","state":"Starting"}
```

**Subsequent push (when connected to radio):**
```json
{"type":"statusChange","state":"Running"}
```

### Example 2: Stop the Bridge

**Send:**
```json
{"type":"command","command":"RequestStop"}
```

**Immediate response:**
```json
{"type":"response","command":"RequestStop","state":"Stopping"}
```

**Subsequent push (when fully stopped):**
```json
{"type":"statusChange","state":"ReadyToStart"}
```

### Example 3: Restart the Bridge

**Send:**
```json
{"type":"command","command":"RequestRestart"}
```

**Immediate response:**
```json
{"type":"response","command":"RequestRestart","state":"Restarting"}
```

**Subsequent pushes (over ~15 seconds):**
```json
{"type":"statusChange","state":"Restarting"}
{"type":"statusChange","state":"Starting"}
{"type":"statusChange","state":"Running"}
```

### Example 4: Poll Current Status

**Send:**
```json
{"type":"command","command":"RequestStatus"}
```

**Response:**
```json
{"type":"response","command":"RequestStatus","state":"Running"}
```

### Example 5: Get Radio List

**Send:**
```json
{"type":"command","command":"RequestRadioList"}
```

**Response:**
```json
{
  "type": "response",
  "command": "RequestRadioList",
  "state": "RequestRadioList",
  "data": "{ \"radio-1\": { \"name\": \"Flex-6700\", ... } }"
}
```

The `data` field contains a JSON string (not a nested object) of discovered radios.


### Example 6: Using wscat (command line)

```bash
# Connect to command endpoint
npx wscat -c ws://127.0.0.1:4990/command

# Send start command
> {"type":"command","command":"RequestStart"}
< {"type":"response","command":"RequestStart","state":"Starting"}
< {"type":"statusChange","state":"Running"}

# Send stop command
> {"type":"command","command":"RequestStop"}
< {"type":"response","command":"RequestStop","state":"Stopping"}
< {"type":"statusChange","state":"ReadyToStart"}

# Send toast message to radio display
> {"type":"command","command":"RequestSendToast","data":"CQ Contest!"}
< {"type":"response","command":"RequestSendToast","state":"Running"}
```

```bash
# Connect to data endpoint (receive-only, real-time meters)
npx wscat -c ws://127.0.0.1:4990/data

< {"type":"meterConfig","meters":[...]}
< {"type":"meterData","timestamp":"...","isTxMode":false,"readings":{...}}
< {"type":"txFrequency","frequencyKhz":14200,"band":"20m"}
```

```bash
# Connect to device endpoint (bidirectional: receives device status, accepts commands)
npx wscat -c ws://127.0.0.1:4990/device

< {"type":"deviceData","devices":[{"deviceId":"elecraft.kpa1500","deviceName":"Elecraft KPA1500","data":{"AN":1,"ON":1,"LQ":{"tx":false,"operate":true,"atuBypass":false,"atuInline":true,"overload":false,"fault":false},"BN":5,"AI":1,"FL":0,"FS":2,"OS":1}}]}

# Send a device command (power on KPA1500)
> {"type":"deviceCommand","deviceId":"elecraft.kpa1500","command":"^ON1;"}
< {"type":"deviceCommandResponse","deviceId":"elecraft.kpa1500","command":"^ON1;","success":true}

# Read-only command is rejected
> {"type":"deviceCommand","deviceId":"elecraft.kpa1500","command":"^FL;"}
< {"type":"deviceCommandResponse","deviceId":"elecraft.kpa1500","command":"^FL;","success":false,"error":"Command 'FL' is read-only"}
```

---

## Error Handling

| Scenario | Behavior |
|----------|----------|
| Service not running | WebSocket connection refused |
| Port changed | Connect fails; check Settings for actual port |
| Malformed JSON sent | Silently ignored by server |
| Unknown command | Response with `state: "UnknownCommand"` |
| Empty command | Response with `state: "EmptyCommand"` |
| Client disconnects | Server cleans up automatically |
| Bridge error during operation | `statusChange` push with `state: "Error"` |
| Radio connection lost | Auto-restart: `Restarting` → `Starting` → `Running` |
| Device command to unknown device | `deviceCommandResponse` with `success: false`, error: `"Device '...' not found"` |
| Device command is read-only | `deviceCommandResponse` with `success: false`, error: `"Command '...' is read-only"` |
| Device not connected | `deviceCommandResponse` with `success: false`, error: `"Device not connected or send failed"` |
| Bridge not running (device cmd) | `deviceCommandResponse` with `success: false`, error: `"Bridge not running"` |

---

## Port Configuration

The default WebSocket port is **4990**. If the port is already in use (e.g., another instance), the service automatically decrements the port number until an available port is found (minimum 1024). The actual port is saved to Settings.

When connecting from external clients, verify the port by checking the service logs or the PgTg Controller's connection status.
