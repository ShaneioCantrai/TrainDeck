# TrainDeck

TrainDeck turns an Android tablet into a touchscreen cab controller and button deck for Train Sim World.

It has two parts:

- `android/`: native Android tablet app with cab levers, a configurable button deck, throttle readouts, and an emergency-brake gate.
- `windows/TrainDeck.BridgeApp/`: Windows companion app that receives tablet input over UDP, enables the TSW HTTP API launch option, and sends cab controls to Train Sim World.

TrainDeck is an unofficial community project. It is not affiliated with or endorsed by Dovetail Games.

## Current Status

TrainDeck is currently tested against Train Sim World 6. It should be adaptable to Train Sim World 5 where the same HTTP API is available.

The preferred control path is:

```text
Android tablet -> UDP 47331 -> TrainDeck Bridge -> TSW HTTP API -> cab controls
```

Keyboard output remains as a fallback for mapped buttons and for sessions where the TSW API is unavailable.

## Features

- Android tablet cab deck with six levers and 24 buttons.
- Direct TSW HTTP API control for throttle, brakes, and reverser.
- Combined throttle/brake readout: `Emergency`, `Brake %`, `N`, and `Power %`.
- Hold-to-enter emergency brake gate on the combined throttle.
- TSW API launch-option helper for Steam installs.
- Cab snapshot tool for discovering loaded train controls.
- Editable button labels and command IDs on the tablet.
- JSON keyboard profile on Windows.

## Quick Start

1. Install and start TrainDeck Bridge on Windows.
2. Press **Set API Launch Opt** in the bridge.
3. Restart Train Sim World from Steam.
4. Install and open TrainDeck on the Android tablet.
5. Tap the bridge address in the tablet header and set it to the Windows bridge address shown in the bridge app.
6. Load into a drivable cab in TSW.
7. Confirm the bridge says `TSW API: connected`.

## Build Android

Install the Android SDK and ensure `ANDROID_HOME` is set, or use the default SDK location under `%LOCALAPPDATA%\Android\Sdk`.

From `android/`:

```powershell
gradle :app:assembleDebug
```

Install to a connected tablet:

```powershell
..\scripts\install-android.ps1
```

Wireless ADB helper:

```powershell
..\scripts\enable-wireless-adb.ps1
```

## Build Windows Bridge

Install the .NET SDK that supports the target framework in `windows/TrainDeck.BridgeApp/TrainDeck.BridgeApp.csproj`.

From the repo root:

```powershell
dotnet build .\windows\TrainDeck.BridgeApp\TrainDeck.BridgeApp.csproj
dotnet run --project .\windows\TrainDeck.BridgeApp\TrainDeck.BridgeApp.csproj
```

Publish a Windows build:

```powershell
dotnet publish .\windows\TrainDeck.BridgeApp\TrainDeck.BridgeApp.csproj -c Release -r win-x64 --self-contained false -o .\dist\TrainDeckBridge
```

## TSW HTTP API

TrainDeck Bridge can add `-HTTPAPI` to the Steam launch options for Train Sim World 6. TSW must be restarted after this option is set.

The bridge creates or reads `CommAPIKey.txt` under the user's `Documents\My Games\TrainSimWorld*` config folder and sends the `DTGCommKey` header required by the TSW API.

## Profiles

The Windows keyboard fallback profile is stored under the user's roaming app data:

```text
%APPDATA%\TrainDeck\profiles\default.keyboard.json
```

Tablet button labels and command IDs are stored in Android app preferences. Long-press a tablet button to edit it.

## License

Proprietary. Copyright (c) 2026 Maple Vibe Inc. All rights reserved.

The source code is published for visibility and project coordination only. See
`LICENSE` and `CONTRIBUTING.md` before copying, redistributing, modifying, or
submitting code.
