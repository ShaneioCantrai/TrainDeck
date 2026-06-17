# TrainDeck

TrainDeck turns an Android tablet into a touchscreen cab controller, button box,
and driving deck for Train Sim World and other train simulators.

It has two parts:

- `android/`: native Android tablet app with cab levers, a configurable button deck, throttle readouts, and an emergency-brake gate.
- `windows/TrainDeck.BridgeApp/`: Windows companion app that receives tablet input over UDP, enables the TSW HTTP API launch option, and sends cab controls to Train Sim World.

TrainDeck is an unofficial community project. It is not affiliated with or endorsed by Dovetail Games.

## Current Status

TrainDeck is currently tested against Train Sim World 6. It should be adaptable to Train Sim World 5 where the same HTTP API is available.

See `docs/architecture-roadmap.md` for the broader plan to keep TrainDeck usable
as a portable train-sim control surface with separate game adapters.

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

Download the current tester release from:

```text
https://github.com/ShaneioCantrai/TrainDeck/releases
```

1. Download `TrainDeckBridgeSetup-*-win-x64.exe` and `TrainDeck-android-*-debug.apk` from the release assets.
2. Install and start TrainDeck Bridge on Windows.
3. Press **Set API Launch Opt** in the bridge.
4. Restart Train Sim World from Steam.
5. Install and open TrainDeck on the Android tablet.
6. Tap the bridge address in the tablet header and set it to the Windows bridge address shown in the bridge app.
7. Load into a drivable cab in TSW.
8. Confirm the bridge says `TSW API: connected`.

## Feedback And Tester Reports

Please use GitHub Issues for bug reports, feature requests, compatibility notes,
and locomotive profile observations:

```text
https://github.com/ShaneioCantrai/TrainDeck/issues
```

Useful tester reports include the Train Sim World version, route, locomotive,
what worked, what did not, and any bridge log or cab snapshot details requested
by Maple Vibe Inc.

For general questions, comments, setup discussion, and tester notes that are not
ready to become an issue, use GitHub Discussions:

```text
https://github.com/ShaneioCantrai/TrainDeck/discussions
```

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

## Build Release Artifacts

TrainDeck release artifacts are built locally into `dist\release\packages`.
The Windows bridge installer uses Inno Setup 6.

```powershell
.\scripts\build-release.ps1 -Version 0.1.0
```

The release package includes:

- `TrainDeckBridgeSetup-*-win-x64.exe`: Windows installer for TrainDeck Bridge.
- `TrainDeckBridge-*-win-x64.zip`: portable Windows bridge folder.
- `TrainDeck-android-*-debug.apk`: Android tablet app for sideload testing.
- `SHA256SUMS.txt`: checksums for the release artifacts.

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
