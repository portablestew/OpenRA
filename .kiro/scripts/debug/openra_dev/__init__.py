"""Agent-facing debug tooling for OpenRA.

Layered so the expensive part is written once:

  paths.py    where things live under .pyddock/tmp
  game.py     tracked-session state, source resolution, debugger exclusivity
  dap.py      Debug Adapter Protocol client (netcoredbg over stdio)
  session.py  a one-shot probe: attach, breakpoint, capture, resume, detach

dbg.py is a thin CLI over session.py. If a stateful debug session is ever needed,
an MCP server can wrap the same ProbeSession without reimplementing any of this.
"""
