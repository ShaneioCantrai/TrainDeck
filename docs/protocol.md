# TrainDeck UDP Protocol

TrainDeck sends newline-free UTF-8 JSON datagrams to the Windows bridge.

Default port: `47331`.

## Hello

```json
{
  "app": "TrainDeck",
  "version": 1,
  "type": "hello",
  "device": "Galaxy Tab"
}
```

## Button

```json
{
  "app": "TrainDeck",
  "version": 1,
  "type": "button",
  "command": "horn",
  "label": "Horn",
  "state": "down",
  "at": 17823499123
}
```

`state` is `down` or `up`.

Known high-level button commands include normal controls such as `horn`,
combo/toggle controls such as `door_left`, `door_right`, and `afb`, explicit
halves such as `afb_on` and `afb_off`, plus API-backed helper macros such as
`power_change_ctrl` and `power_change_dc` for the Class 395 power changeover
sequence.

## Axis

```json
{
  "app": "TrainDeck",
  "version": 1,
  "type": "axis",
  "control": "throttle",
  "value": 0.72,
  "at": 17823499123
}
```

Axis `value` is normalized:

- `0.0` to `1.0` for ordinary levers.
- `-1.0` to `1.0` for reverser-style controls.

## Pointer

```json
{
  "app": "TrainDeck",
  "version": 1,
  "type": "pointer",
  "action": "move",
  "dx": 12.5,
  "dy": -4.0,
  "at": 17823499123
}
```

Pointer deltas are relative mouse movement from the walk deck touchpad.
For menu navigation, the tablet may also send `action: "scroll"` with `dy` as
a relative touchpad scroll delta. Touchpad taps are sent as normal
`mouse_left` or `mouse_right` button commands.

## Bridge Commands

The Windows bridge may send UTF-8 JSON datagrams back to the tablet source port
seen in the latest tablet packet.

### Reset Axes

```json
{
  "app": "TrainDeck",
  "version": 1,
  "type": "reset_axes",
  "reason": "not connected: Response status code does not indicate success: 403.",
  "at": 17823499123
}
```

The tablet resets local lever positions to their safe defaults. For the combined
throttle, the safe default is the neutral `N` line.

### Capabilities

```json
{
  "app": "TrainDeck",
  "version": 1,
  "type": "capabilities",
  "axes": ["reverser", "throttle"],
  "buttons": ["door_left", "door_right"],
  "at": 17823499123
}
```

The bridge sends this after a tablet hello and whenever the active TSW API
profile is ready again. The tablet uses it to disable cab-specific controls that
are not mapped for the current loco, such as AFB on trains that do not expose
AFB controls.

### Telemetry

```json
{
  "app": "TrainDeck",
  "version": 1,
  "type": "telemetry",
  "speedKmh": 72.5,
  "speedMph": 45.0,
  "speedLimitKmh": 80.0,
  "speedLimitDistanceM": 0.0,
  "nextSpeedLimitKmh": 40.0,
  "nextSpeedLimitDistanceM": 1232.6,
  "speedHoldArmed": true,
  "speedHoldAutoPilot": true,
  "speedHoldTargetKmh": 80.0,
  "speedHoldOutput": 0.58,
  "speedHoldMode": "power",
  "runRecording": true,
  "runRecordingElapsedSeconds": 34.5,
  "at": 17823499123
}
```

Speed telemetry comes from the TSW HTTP API. `speedLimitKmh` describes the
current limit TrainDeck should obey. `nextSpeedLimitKmh` and
`nextSpeedLimitDistanceM` describe the upcoming speed limit ahead and are omitted
when the Driver Aid API does not currently report one.

`speedHold*` fields describe TrainDeck's own experimental speed hold assist.
The tablet can send the following high-level button commands to arm, disarm, and
adjust it:

- `td_speed_hold_toggle`
- `td_speed_hold_auto_pilot`
- `td_speed_hold_set_current`
- `td_speed_hold_set_limit`
- `td_speed_hold_set_next`
- `td_speed_hold_minus_5`
- `td_speed_hold_minus_1`
- `td_speed_hold_plus_1`
- `td_speed_hold_plus_5`
- `td_run_record_toggle`

`td_run_record_toggle` starts or stops a bridge-side driver trace. Recordings
are saved as CSV files under `%APPDATA%\TrainDeck\runs\` and include speed,
derived acceleration, current and next limits, signal/gradient data, cab handle
state, brake/interlock hints, and TD Hold/Auto Pilot state.

Use `.\scripts\analyze-run.ps1` from the repo root to summarize the latest trace
for mapping and training review. Pass `-Path` to analyze a specific CSV and
`-Json` when a downstream tool needs machine-readable output.

TD Hold and TD Auto Pilot use the first TrainDeck driver trace as a conservative
assist profile: strong acceleration when there is room below the governing
target, staged feathering near the limit, and brake-curve targets for upcoming
lower limits or stop signals instead of crawling early.
