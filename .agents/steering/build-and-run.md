---
inclusion: auto
name: build-and-run
description: How to build, run, check status, and stop the OpenRA game; where its logs, crash dumps, and other runtime artifacts live.
---

# Build and run OpenRA

Everything under `.kiro/scripts/` is allow-listed for `run_shell`. `command` is the
script path; `args` are the script's own args. `.ps1` and `.py` are both invoked
directly (pyddock maps the interpreter).

## Build only (compile check, don't run anything)
```
run_shell(command=".kiro/scripts/build/build-and-run.ps1", args=["-Build"])
```
Exit 0 means it compiled.

## Build and run the game
```
run_shell(command=".kiro/scripts/build/build-and-run.ps1", args=["Game.Mod=ra"])
```

## Build and run unit tests (not the game)
```
run_shell(command=".kiro/scripts/build/build-and-run.ps1", args=["-Tests"])
```
Exit 0 means every test passed. Test-only flags (require `-Tests`):
- `-Filter <expr>` — run a subset. Bare name = substring match; else NUnit syntax, e.g. `FullyQualifiedName~Clone|TestCategory=Fast`.
- `-PerTest` — print each test's name and result, not just the summary.
```
run_shell(command=".kiro/scripts/build/build-and-run.ps1",
          args=["-Tests", "-Filter", "ActivityCloneTest", "-PerTest"])
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
path if a debug session ever leaves the game halted.

# Debugging

To inspect a running game — breakpoints, stacks, variable values — use the
`openra_debug` library from a `run_python` snippet. See the **openra-debug**
steering doc for the API and the safety rules; `dump.py` (post-mortem crash-dump
analysis) is covered there too.

# Where things are

- Main game logs (start here for game behaviour/errors): `%APPDATA%\OpenRA\Logs` — `exception.log` for crashes, `debug.log`, `perf.log`.
- Crash dumps: `.pyddock\tmp\dumps\openra.<pid>.dmp`.
- Debug session transcripts: `.pyddock\tmp\logs\session.<stamp>.ncdbg.log`.
- Dump analysis transcripts: `.pyddock\tmp\logs\dump-analysis.<stamp>.log`.
- Session state: `.pyddock\tmp\run\game.json` (the game), `debug.json` (a debug session in flight).
- `status-game.ps1` surfaces all of the above in one view.
