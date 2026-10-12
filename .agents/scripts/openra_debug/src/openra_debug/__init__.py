"""Agent-facing live debugger for a running OpenRA game.

Attaches netcoredbg to the game process tracked by
``.kiro/scripts/build/build-and-run.ps1`` and drives it over its CLI interpreter,
so an agent can set breakpoints, inspect state, and read stacks from inside a
``run_python`` snippet:

    from openra_debug import session

    with session() as dbg:
        print(dbg.callstack().text)          # what is the game doing right now

    with session() as dbg:
        hit = dbg.stop_at("Mobile.cs:324", when='self.Info.Name == "e1"')
        print(hit.stack_text)
        if hit.eval("self.IsMovingBetweenCells").value == "true":
            print(hit.eval("self.ToCell").value)
        dbg.resume()

Design notes that matter for safe use live in the public API below and in
``.kiro/steering/debug.md``. The short version: the game is *halted* whenever it
is stopped at a breakpoint, and a session has a bounded lifetime — do the
inspection and let the ``with`` block exit promptly.

Layering (each file is one concern):

  paths.py     where runtime artifacts live under .pyddock/tmp
  locate.py    find + identity-check the tracked game; find netcoredbg
  cli.py       netcoredbg CLI-over-stdio transport, sentinel-delimited responses
  session.py   attach/continue, breakpoints, stops, inspection, guaranteed detach
  report.py    bounded text rendering of results
  __init__.py  the public surface (this file)
"""

from __future__ import annotations

# The real public API is wired up in a later build step; importing the names
# here keeps `from openra_debug import session` working once session.py lands.
from .locate import LocateError
from .session import (
    DebugError,
    Hit,
    Result,
    Session,
    StaleHitError,
    session,
)

__all__ = [
    "session",
    "Session",
    "Hit",
    "Result",
    "DebugError",
    "LocateError",
    "StaleHitError",
]
