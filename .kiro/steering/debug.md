---
inclusion: auto
name: openra-debug
description: How to debug a running OpenRA game from Python — set breakpoints, read stacks, inspect variables — with the openra_debug library, plus crash-dump analysis and the safety rules that keep the game alive.
---

# Debugging a running OpenRA game

`openra_debug` is a Python library you `import` inside a `run_python` snippet. It
attaches a debugger (netcoredbg) to the game launched by `build-and-run.ps1`,
lets you set breakpoints, read call stacks, and evaluate expressions against live
objects, then detaches — leaving the game running.

There is **no CLI and no PID argument.** The target is always the tracked game
(`.pyddock/tmp/run/game.json`), whose identity is verified before attaching.

## The one rule that matters

**While the game is stopped at a breakpoint, the whole game is frozen.** Inspect
what you need and resume promptly. The `with session()` block bounds this for you:
if a single halt lasts longer than `hold_timeout` (default 20s, under the 30s
`run_python` budget), the game is auto-resumed and the stale `Hit` starts raising
`StaleHitError`. On any block exit — normal, exception, timeout — the game is
resumed and the debugger detached.

## Live call stack — "what is it doing right now"

```python
from openra_debug import session

with session() as dbg:
    print(dbg.callstack().text)
```

`callstack()` interrupts, captures every managed thread's stack, and resumes
immediately (no lingering halt). Pass `all_threads=False` for just the thread that
happened to be scheduled. This is the first thing to reach for on a hang or a
"where is time going" question.

## Breakpoint, inspect, branch on live state

The reason this is a library and not a fixed command: you can decide what to look
at *after* seeing the stop.

```python
from openra_debug import session

with session() as dbg:
    hit = dbg.stop_at("Mobile.cs:324", when='self.Info.Name == "e1"', timeout=25)
    if hit is None:
        print("breakpoint not hit within the timeout")
    else:
        print(hit.stack_text)
        if hit.eval("this.IsMovingBetweenCells").value == "true":
            print("moving to", hit.eval("this.ToCell").value)
        dbg.resume()
```

- `stop_at(location, when=None, timeout=20)` → a `Hit`, or `None` on timeout.
  `location` is `file:line` (absolute, repo-relative, or a bare filename if
  unique). `when` is a C# boolean condition.
- `Hit.eval(expr)` → an `EvalResult` with `.value` (string), `.text` (raw), and
  `.error`. `Hit.stack(frames=20, all_threads=False)`, `Hit.frame(i)`.
- `dbg.resume()` lets the game run again (also happens automatically on block
  exit).
- Inspecting a `Hit` after the game has resumed raises `StaleHitError`.

Expression scope is the stopped frame. At `Mobile.Tick`, `self` is the `Actor`
and `this` is the `Mobile` trait — so `this.IsMovingBetweenCells`,
`this.MoveResult`, `this.ToCell` resolve, while actor fields are on `self`
(`self.Info.Name`, `self.ActorID`, `self.Location`).

## A scripted sequence

For a canned sequence, `run()` takes netcoredbg's own GDB-like CLI, one command
per line (`break file:line [if cond]`, `backtrace`, `backtrace all`,
`print expr`, `continue`, `frame N`, `next`, `step`, `delete N`):

```python
with session() as dbg:
    print(dbg.run("""
        break Mobile.cs:324 if self.Info.Name == "e1"
        continue
        backtrace
        print self.Info.Name
    """).text)
```

Prefer the typed methods for anything conditional — real Python beats a command
mini-language. Note `continue` in a script is fire-and-forget; use `stop_at` when
you need to *wait* for the breakpoint.

## Crash dumps and post-mortems (dump.py)

For a process that has already crashed — which no live debugger can attach to —
analyse its dump:

```
run_shell(command=".kiro/scripts/openra_debug/dump.py", args=["analyze"])
```
Newest dump by default: prints path/size/timestamp, pending exception, managed
threads, and the current stack. `--path`, `--command SOS`, `--lines`, `--thread N`.
Full transcript always goes to a log. `dump.py collect` snapshots a running game
(brief pause, no attach) as a zero-risk fallback, but for a live game the
`session().callstack()` path above is usually better.

## netcoredbg constraints worth knowing

- **One debugger per process** (CoreCLR limit): a session and VS Code cannot both
  attach. The second fails with an explicit message; `status-game.ps1` shows who
  holds the slot.
- **One game at a time.** `build-and-run.ps1` refuses to launch a second.
- **No hit counts, no logpoints.** A `when` condition is the only filter, and
  every stop halts the whole game. A breakpoint in a hot path like `Mobile.Tick`
  re-fires the instant you resume, and fires for every matching actor each tick —
  a `when` condition is how you pin it to one.
- **`print` gives one level of object expansion**, not a full locals tree. Reach
  into fields explicitly (`this.ToCell.X`). An out-of-scope or misspelled name
  comes back as an `EvalResult` with `.error` set, not an exception.
- **Crash dumps are suppressed while attached**, so a crash inside a session
  window produces no dump — the stop is reported instead.
- **Recovery:** if a session ever leaves the game halted (e.g. netcoredbg itself
  hung), `.kiro\scripts\build\kill-game.ps1 -All` clears it.

## Why it is safe to kill your snippet

netcoredbg is driven over its CLI interpreter, which treats a closed stdin as a
**clean detach**. If your snippet raises, times out, or is hard-killed while the
game is halted, netcoredbg detaches and the game keeps running — verified against
a live game, including a hard kill mid-halt. The `with` block is still the right
way to use it (it resumes and detaches deterministically), but a crash in your own
code will not take the game down with it.
