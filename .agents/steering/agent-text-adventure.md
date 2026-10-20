---
inclusion: auto
name: agent-text-adventure
description: How to get into an OpenRA game through the text_adventure tool, and what the banner does not say: settle_ms and timeout_ms, production and its eta and fresh-producer quirks, and where map-state.png and screenshots are written.
---

# Playing OpenRA through text_adventure

1. Connect and read the banner:
   `text_adventure(command='{"cmd":"help"}')`
   The default address is `127.0.0.1:3000`. The reply is the banner, not an envelope.
2. Start a skirmish. Its reply can outrun the default, so pass both timing keys:
   `text_adventure(command='{"cmd":"load_skirmish"}', timeout_ms=90000, settle_ms=1000)`
   `settle_ms` collects output for exactly that window; `timeout_ms` caps the whole call.
3. Every other reply is one JSON line, then a lone `>` line. The `>` means it is your turn.
4. Queue a build, then read the queue on the next turn:
   `text_adventure(command='{"cmd":"build","item":"e1","count":2}')`
   `eta_ticks` is cumulative for the queue. Builds in one array resolve a tick later, so etas inside an array are lower bounds.
5. A newly placed producer reports `options: []` and `enabled:false` for a moment. Read production on the next turn:
   `text_adventure(command='{"cmd":"production"}')`
   Options are listed only when they changed since this connection last read them; `changed` says whether they did. The full catalog is:
   `text_adventure(command='{"cmd":"production_tree"}')`
6. Place a building. `cell` is where it placed, which may differ from the requested cell (search moves it up to 10 cells):
   `text_adventure(command='{"cmd":"place","item":"powr","cell":[40,50]}')`
7. Wait on a job, with `timeout_ms` as the `wait` key:
   `text_adventure(command='{"cmd":"wait","job":1,"timeout_ms":5000}')`
8. `map_state` writes `map-state.png` to the workspace, the folder the game was started from. Screenshots go to a `screenshots` folder in the same place. Read the `path` the reply gives:
   `text_adventure(command='{"cmd":"map_state"}')`
- A structure repair holds only while the building is damaged, so it ends at full health: read its `repairing` flag again before you count on it.
- `path.eta_ticks` is a floor.
- Sell refunds `RefundPercent` of the cost (default 50), scaled by the building's health: repair before you sell.
- Repair drains cash as it heals: check `cash` in state before you start.
- Under low power, power down a building you do not need, such as a superweapon, to restore the grid. `power_restored` says when.
- Check `powers` before you plan around a super weapon: a charging one is not ready, and `remaining_ticks` says when it will be.
- A `healed` event marks a medic at work. A repair does not raise it.
