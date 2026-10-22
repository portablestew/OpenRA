# Step 8: the wait header

A `wait` reply used to attach the full state digest, which carries every unit the local player knows. That is most of the bytes
in a reply an agent reads once per turn, and the agent rarely needs the list: it waits for events, then decides. This step makes
`wait` carry a compact header instead, and leaves the full digest one `state` request away.

## Where it goes

The envelope's `state` key carries the header on every `wait` reply (object or job form, and an array whose items are all `wait`
or `state` with `detail:"summary"`). `state` with `detail:"summary"` returns the same object, so the header and the summary are one
shape. `state` with no `detail`, or `detail:"full"`, is the digest as before, and is the default.

Keys of `state` when `detail` is `summary`, in serialization order:

| key | type | why it is here |
|-----|------|----------------|
| `tick` | int | The game clock the header was read at. It equals the envelope's `tick`, which is also sent, so a reader of the header alone can date it. |
| `game_over` | bool | A wait's most useful stop is the end of the game. It is the engine's `IsGameOver`, the same flag the full digest carries. |
| `winner` | string or null | Named only when the game is over: the player whose `WinState` is `Won`. Null while the game runs, and null when the game ends with no winner. An agent needs no digest read to learn who won. |
| `local_player` | string or null | Whose view the header is. Every field below is read from this player's side of the fog, so the name tells the agent the perspective. |
| `funds` | int or null | The local player's spendable total (cash plus harvested resources). An agent deciding whether to build reads this each turn, so it stays in the header. Null without a local player. |
| `capacity` | int or null | The local player's `ResourceFundsCapacity`, the cap on the harvested pool. Read with `funds`, because the two together say how much of the spendable total is already capped. |
| `power` | object or null | The local player's `Provided`, `Drained`, `Excess` and `State`. Power is the first thing a low-power base loses, so it is read each turn with funds. Null without a local player or a `PowerManager`. |
| `known_counts` | object | Per player name, the count of each type the local player knows of that player's units. This is the agent's view of enemy strength by type, so the header carries it instead of the unit list. Null without a local player. |
| `threats` | array | The top 15 known enemies by standoff, with id, cell, hp, heading, bearing and the friendly each is nearest to. The threat list is the agent's answer to "what is coming", so it stays in the header whole. |
| `unit_counts` | object | Per owner name, the number of units the state would list for that owner. It answers "how many of mine are alive" without listing them. Units with no owner are not counted, since a map key cannot be null. |

## What the header leaves out, and where to get it

These are not in a `wait` reply. Each is reachable through `state` with no `detail`, which is the full digest:

- **The units list.** Every unit row and every structure row, by id, type, cell, facing, hp, damage, activity and idle, plus the
  `repairing` and `powered_down` of your buildings. Agents need the ids for orders, so a `wait` reply alone does not name the
  unit to give the next order to. Use `actors` for one owner or type, or `state` for the whole list.
- **Enemy units beyond the top 15 threats.** `threats` is capped at 15 and 3 per type. A target past that cap is in the units list.
- **Per-unit detail for a threat.** The threat row has hp and bearing, but not the full unit row (facing, activity, idle).
- **Player profile data.** `faction`, `color`, `bot`, `handicap` and `dead` (the player's `WinState.Lost`) are per player, and
  sent only in the full digest. The header's `winner` names the one player who won, once the game is over.
- **Other players' economy and power.** The header carries only the local player's funds, capacity and power. Another player's
  economy is null in the full digest too, so nothing is lost there; it is listed for completeness.
- **The map and spawns.** `map` (uid, title, bounds, each player's home cell) is static and only in the full digest.
- **The selection.** `selection` (the ids your orders selected) is only in the full digest.
- **The map image keys** (`map_state_png`, `map_state_tick`, `map_state_render`, `map_px_per_cell`, `map_origin`, `map_size`).
  A `wait` envelope does not render the map. `state` with no `detail` still does, as it always has.

## What did not move

- `events`, and the `wait` result (`result`, `start_tick`, `tick`, `requested_ticks`, `waited_ticks`, plus `job` in the job form).
  Those are unchanged, so an agent that only reads events sees the same reply it saw before, minus the digest.
- Every row keeps its shape. No unit row carries `pos`, as step 6 decided.
- `state` with no `detail` is the digest it was before this step, byte for byte. The test in `AgentWaitStateTest` checks it.

## Bytes

Measured by `AgentWaitStateTest.WaitReplyIsSmallerThanTheFullDigest` on a headless world with one local unit and one enemy unit.
The "before" reply is a `ping`: it carries the full digest and the map keys, and no events, which is what the old `wait`
attached. The "after" reply is a `wait` with `timeout_ticks:0`, which carries the header. The "before" figure is therefore a
close baseline, not a byte-for-byte copy of an old `wait`: the old reply also had its own `results` and `events`, and those
are a few bytes.

- wait bytes before (full digest, ping): 2077
- wait bytes after (header, wait): 453

The header is about a fifth of the size here. The full digest grows with every unit the local player knows. The header grows
with the number of unit types and players, and its `threats` list is capped at 15, so it does not grow with the unit count.
