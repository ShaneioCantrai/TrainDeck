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

