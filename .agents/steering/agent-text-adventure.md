---
inclusion: auto
name: agent-text-adventure
description: OpenRA text_adventure patterns.
---

# text_adventure

- Banner: `{"cmd":"help"}`. Then `>` marks your turn.
- A command is real JSON: an object, or an array of objects. A string holding JSON (`"{\"cmd\":\"ping\"}"`) is refused.
- Size `timeout_ms` past `timeout_ticks` × `ms_per_tick`: `timeout_ticks` is game time, `timeout_ms` caps only the call's wall clock and never the game.
- Start: `{"cmd":"load_skirmish"}` with tool `timeout_ms=90000, settle_ms=1000`.
- Build: `{"cmd":"build","item":"e1","count":2}`. `eta_ticks` is cumulative; array builds resolve a tick later, so their etas are lower bounds.
- Production: `{"cmd":"production"}` lists options only when changed; full catalog `{"cmd":"production_tree"}`.
- Fresh producer: `options:[]`, `enabled:false` on first read; read again.
- Place: `{"cmd":"place","item":"powr","cell":[40,50]}`. `cell` is where it placed. `{"cmd":"place"}` with no args places the done queue-head item; with nothing done it errors.
- Finished: a `ConstructionComplete` notification names a structure with `placement_required:true`, so place it. A `UnitReady` notification names a unit with its `cell`.
- `NewOptions` names in `options` the options that became buildable since the last one (empty when none). An ally's has no `options`.
- Wait events: `{"cmd":"wait","timeout_ticks":500,"min_ticks":500}`. `result`: `events`, `timeout`, `paused`, `world_changed`. Events are your ordered movement, combat, production and notifications.
- A `wait` reply carries a header in `state`, not the full digest: `tick`, `game_over`, `winner`, `local_player`, `funds`, `capacity`, `power`, `known_counts`, `threats`, `unit_counts`, `units` (yours), `queues` (yours), and `powers` when an opponent's power timer is visible to you. It has no map. `state` with `detail:"summary"` returns the same header; `state` with no `detail` is the full digest.
- Map: `{"cmd":"map_state"}` writes `map-state.png`, readable with the file Read tool. `{"cmd":"map_state","format":"text"}` returns the grid inline (a space is unexplored); screenshots go to `screenshots/`.
- Always AttackMove to advance; use Move only when you must not fire (retreat, reposition, scout past contact).
- Discover orders: `{"cmd":"orders","actor":12}` -> `orders`, every name the actor takes. Use it to find `Repair` (vehicle at a fix depot), `Dock` (harvester at a refinery) and the player actor's `RepairBuilding`.
- Repair: `{"cmd":"order","actor":<your player actor>,"order":"RepairBuilding","target_actor":<building id>,"on":true}`. One building per call. It drains `funds` as it heals, so check `funds` first. It stays on until you send `on:false`.
- A structure repair holds only while the building is damaged, so it ends at full health: read its `repairing` flag again before you count on it.
- Vision comes only from your units; scout.
- `path.eta_ticks` is a floor.
- Sell refunds `RefundPercent` of the cost (default 50), scaled by the building's health: repair before you sell.
- Costs compare against `funds`, the spendable total (`cash_funds + resource_funds`). `cash_funds` is only the uncapped pool; `resource_funds` is only the harvested pool, capped by `resource_funds_capacity`. A `SilosNeeded` notification means that cap is full and further harvest is wasted: build a silo or spend.
- `powers` lists each support power you can see, with its charge. Your own are always listed. An opponent's silo countdown is listed when the engine shows you its timer. A charging power is not ready, and `remaining_ticks` says when it will be. An empty list does not mean no opponent power is charging.
- Group orders are partial: a matched actor that is dead, not owned, or refuses the name is skipped with its `error`, and the rest are ordered. `skipped` lists them, and is absent when none were skipped. The group is refused only when no matched actor can take the order.
- `queued:true` appends to one actor's own activity queue; it does not make a group wait for its other members.
- Unit and structure rows have no `pos`. `damage_state` appears only when a unit is below full health.
- A `healed` event marks a medic at work. A repair does not raise it.

## Play

- Avoid `digest:false` unless you need more detail. It drops `state` and the map keys from the reply, header included, so the reply gets thinner, not richer. For more detail, call `state`.
- Prefer long waits over polling: `timeout_ticks` is an integer 0-1000. Call `state` for enemy IDs beyond `threats`, or enemy `known_counts`. Use the wait header, `production` and `look` for routine monitoring.
- A `known_counts` of zero means you have not seen any, not that there are none.
- Fog and shroud: a structure you have seen stays visible there, and units will attack it, but it gets no state updates. Units keep attacking after it is destroyed unless you scout it again.
- Hidden artillery can hit your base from fog.
- Attacked from the shroud: `attacker:null` with `attacker_shrouded:true` is a real but unseen attacker. `attacker_bearing` points from the victim at it (null when the firer died before the shot landed).
- NoBuild with `reason:"exit_blocked"`: set `producer`'s rally point clear of the exit: `{"cmd":"order","actor":<producer>,"order":"SetRallyPoint","cell":[x,y]}`.
- Before a long wait, give an idle unit a cheap order: `{"cmd":"order","actor":<unit>,"order":"Move","cell":[x,y]}` (scouting past contact).
