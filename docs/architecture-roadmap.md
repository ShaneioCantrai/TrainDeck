# TrainDeck Architecture And Roadmap

TrainDeck is currently focused on Train Sim World, but the project should be
kept as a reusable train-sim control surface rather than a one-game helper.

The intended shape is:

```text
Android deck UI -> TrainDeck bridge protocol -> Bridge core -> Game adapter
```

TSW is the first game adapter. The tablet and Fold UI, deck pages, walk deck,
touchpad, button state, status indicator, and options menu should stay as
portable as possible.

## Layers

### Android Deck UI

The Android app should remain game-agnostic where possible. It owns the touch
experience: cab levers, deck pages, Easy mode, Walk mode, touchpad, status, and
local button editing.

The UI should send intent-level commands such as `horn`, `door_left`,
`reverser`, `throttle`, `master_key`, or `power_change_ctrl`. It should avoid
knowing which game API, keyboard binding, macro, or simulator-specific control
ultimately handles the command.

### Bridge Core

The bridge should own device connectivity, deck capabilities, profile loading,
keyboard and mouse fallback output, connected-device state, logging, and common
macro plumbing.

Long term, the bridge should treat connected devices as first-class clients
rather than only tracking the last tablet that sent a packet. Useful roles may
include `Primary Driver`, `Walk`, `Conductor`, and `Observer`.

### Game Adapters

Game adapters translate TrainDeck actions into a simulator-specific execution
path. A game adapter may use an HTTP API, a plugin, memory-safe IPC, keyboard
input, mouse input, configuration files, or a mix of those methods.

Adapters should expose capabilities back to the deck so unsupported controls can
be disabled or marked `N/A` for the current cab.

## Candidate Simulators

### Train Sim World

TSW is the current adapter. It uses the TSW HTTP API where possible and falls
back to keyboard or mouse output where needed.

### Train Simulator Classic

Train Simulator Classic is the next major commercial target to investigate. The
likely first path is keyboard and profile-driven control, unless a better
supported API, plugin path, or cab-control access method is found.

### Open-Source Train Sims

Open Rails and other open-source train simulators are attractive future targets
because their internals are inspectable and may allow cleaner integrations.
Their onboarding and learning curves can be rough, so TrainDeck could also act
as a gentler control and teaching layer: Easy decks, cab-specific startup flows,
guided macros, and clearer control labels.

## Profile Direction

Profiles should move toward game-agnostic actions first, with per-game mappings
underneath.

Examples:

- `throttle_axis`
- `reverser_forward`
- `door_left_toggle`
- `master_key`
- `horn`
- `walk_interact`

Each game adapter can then decide whether that action becomes an API call, a
keyboard shortcut, a mouse action, or a macro.

## Design Goals

- Keep the Android deck usable on tablets, foldables, and smaller secondary
  screens.
- Prefer capabilities from the active cab over hard-coded assumptions.
- Keep Easy mode as a discoverability and onboarding layer, not only a shortcut
  page.
- Avoid baking TSW-only concepts into the deck UI when a more general action
  name will work.
- Preserve keyboard and mouse fallback paths for sims without a rich API.
