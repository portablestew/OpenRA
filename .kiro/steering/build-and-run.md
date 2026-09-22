---
inclusion: auto
name: build-and-run
description: How to build, run, debug, check status, and stop the OpenRA game; where its logs, crash dumps, and other runtime artifacts live.
---

# Build and run OpenRA

Everything under `.kiro/scripts/` is allow-listed for `run_shell`. `command` is the
script path; `args` are the script's own args. `.ps1` and `.py` are both invoked
directly (pyddock maps the interpreter).

Note that a `.py` script here runs as a real subprocess with the **full** standard
library — the import allowlist that applies to inline `run_python` snippets does
not apply to it.

## Build and run the game
```
run_shell(command=".kiro/scripts/build/build-and-run.ps1", args=["Game.Mod=ra"])
```

## Build and run unit tests (not the game)
```
run_shell(command=".kiro/scripts/build/build-and-run.ps1", args=["-Tests"])
```

## Check status (game process, debugger availability, recent logs, crash dumps)
```
run_shell(command=".kiro/scripts/build/status-game.ps1", args=[])
```

## Stop the game
```
run_shell(command=".kiro/scripts/build/kill-game.ps1", args=[])
```
Add `-All` to also sweep stray game/debugger processes. This is also the recovery
path if a probe ever leaves the game halted.

# Debugging (agent)

One call attaches, breakpoints, waits for a hit, captures stack/locals/expressions,
resumes, and detaches. The game keeps running afterwards.

```
run_shell(command=".kiro/scripts/debug/dbg.py",
          args=["probe", "--at", "Mobile.cs:324", "--locals"])
```

`--at FILE:LINE` — FILE may be absolute, repo-relative, or a bare filename if
unique. Also: `--when EXPR` (condition), `--eval EXPR` (top frame, repeatable),
`--at-function Namespace.Type.Method`, `--on-exception all|user-unhandled` (the
only two filters netcoredbg offers), `--timeout SEC` (20), `--frames N`,
`--depth N`, `--repeat N`, `--then terminate`, `--json`.

Exit codes: `0` hit, `2` nothing hit before the timeout, `1` error.

`dbg.py status` reports game and debugger-slot availability without attaching.

## Crash dumps and live snapshots

```
run_shell(command=".kiro/scripts/debug/dump.py", args=["analyze"])
```
Newest dump by default: prints its path/size/timestamp, then pending exception,
managed threads, and the current stack. `--path`, `--command SOS`, `--lines`,
`--thread N`. Full transcript always goes to a log.

```
run_shell(command=".kiro/scripts/debug/dump.py", args=["collect"])
```
Snapshots the running game. Prefer this over a breakpoint for "what is it doing
right now" / "why is it hung": brief pause, no debugger attached, cannot leave the
game halted.

# Debugging (human, in VS Code)

Run and Debug → **Attach (OpenRA)**, then pick the `dotnet` process running
`bin/OpenRA.dll`. Attaches vsdbg by PID; netcoredbg is not involved.

# Where things are

- Main game logs (start here for game behaviour/errors): `%APPDATA%\OpenRA\Logs` — `exception.log` for crashes, `debug.log`, `perf.log`.
- Crash dumps: `.pyddock\tmp\dumps\openra.<pid>.dmp`.
- Probe reports and DAP transcripts: `.pyddock\tmp\logs\probe.<stamp>.json` / `.dap.log`.
- Dump analysis transcripts: `.pyddock\tmp\logs\dump-analysis.<stamp>.log`.
- Session state: `.pyddock\tmp\run\game.json` (the game), `debug.json` (a probe in flight).
- `status-game.ps1` surfaces all of the above in one view.

# Constraints worth knowing

- **One debugger per process** (CoreCLR limit), so a probe and VS Code cannot both
  attach. Both fail with an explicit message; `dbg.py status` shows who holds it.
- **One game instance at a time.** `build-and-run.ps1` refuses to launch a second.
- **netcoredbg has no hit counts and no logpoints.** `--when` is the only filter,
  and every observation halts the process. A breakpoint in a hot path like
  `Mobile.Tick` re-fires the instant the game resumes.
- **Stopping halts the whole game.** Keep `--timeout` short.
- **Crash dumps are suppressed while a debugger is attached**, so a crash inside a
  probe window produces no dump — the probe reports the stop instead.
- **If a probe is killed mid-flight the game dies with it** (ICorDebug takes the
  debuggee down when the debugger vanishes without detaching). Normal exits,
  errors, and timeouts all detach cleanly; only a hard kill of dbg.py is unsafe.
- **A rejected SOS command aborts the rest of a `dump.py analyze` batch**; the
  script warns when that happens.
