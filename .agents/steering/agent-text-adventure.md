---
inclusion: auto
name: agent-text-adventure
description: OpenRA text_adventure patterns.
---

# text_adventure

- Banner: `{"cmd":"help"}`. Then `>` marks your turn.
- Start: `{"cmd":"load_skirmish"}` with tool `timeout_ms=90000, settle_ms=1000`.
- Build: `{"cmd":"build","item":"e1","count":2}`. `eta_ticks` is cumulative; array builds resolve a tick later, so their etas are lower bounds.
- Production: `{"cmd":"production"}` lists options only when changed; full catalog `{"cmd":"production_tree"}`.
- Fresh producer: `options:[]`, `enabled:false` on first read; read again.
- Place: `{"cmd":"place","item":"powr","cell":[40,50]}`. `cell` is where it placed.
- Wait job: `{"cmd":"wait","job":1,"timeout_ticks":250}`. `result:"job"` settled; `status:"pending"` or `"paused"` keeps it open.
- Wait events: `{"cmd":"wait","timeout_ticks":500,"min_ticks":500}`. `result`: `events`, `timeout`, `paused`, `world_changed`. Events are your ordered movement, combat, production and notifications.
- Undo: `{"cmd":"undo","offset":-250}` or `{"cmd":"undo","at_tick":12000}`.
- Map: `{"cmd":"map_state"}` writes `map-state.png`; screenshots go to `screenshots/`.
- Advance under threat: AttackMove.
- Repair: `{"cmd":"order","actor":<unit>,"order":"Repair","target_actor":<fix id>}`.
- Vision comes only from your units; scout.
- `path.eta_ticks` is a floor.
- Sell refunds `RefundPercent` of the cost (default 50), scaled by the building's health: repair before you sell.
- Repair drains cash as it heals: check `cash` in state before you start.
- A structure repair holds only while the building is damaged, so it ends at full health: read its `repairing` flag again before you count on it.
- Under low power, power down a building you do not need, such as a superweapon, to restore the grid. `power_restored` says when.
- Check `powers` before you plan around a super weapon: a charging one is not ready, and `remaining_ticks` says when it will be.
- A `healed` event marks a medic at work. A repair does not raise it.
