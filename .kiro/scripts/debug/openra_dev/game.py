"""Session state and source resolution shared by the debug CLIs.

The game process is launched and tracked by .kiro/scripts/build/build-and-run.ps1;
this module only reads that state. The debug state file is ours to write.
"""

from __future__ import annotations

import json
import os
import subprocess
import sys
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

from . import paths


class ToolError(Exception):
    """An expected, explainable failure. Printed without a traceback."""


# --------------------------------------------------------------------------
# tracked game process
# --------------------------------------------------------------------------


def read_game_state() -> dict[str, Any]:
    if not paths.GAME_STATE.exists():
        raise ToolError(
            "No tracked game session.\n"
            "  Start one with: .kiro\\scripts\\build\\build-and-run.ps1 Game.Mod=ra"
        )
    try:
        return json.loads(paths.GAME_STATE.read_text(encoding="utf-8-sig"))
    except (OSError, json.JSONDecodeError) as ex:
        raise ToolError(f"Could not read {paths.rel(paths.GAME_STATE)}: {ex}") from ex


def pid_alive(pid: int) -> bool:
    """True if a process with this pid exists. Windows-only, via tasklist."""
    if not pid:
        return False
    try:
        out = subprocess.run(
            ["tasklist", "/FI", f"PID eq {int(pid)}", "/NH", "/FO", "CSV"],
            capture_output=True,
            text=True,
            timeout=15,
        ).stdout
    except (OSError, subprocess.SubprocessError):
        return False
    return f'"{int(pid)}"' in out


def game_pid() -> int:
    """The pid of the running, tracked game. Raises if it is not alive."""
    state = read_game_state()
    pid = int(state.get("GamePid") or 0)
    if not pid:
        raise ToolError(
            f"{paths.rel(paths.GAME_STATE)} has no GamePid. Relaunch the game."
        )
    if not pid_alive(pid):
        raise ToolError(
            f"Tracked game (PID {pid}) is not running - the state file is stale.\n"
            "  Relaunch with: .kiro\\scripts\\build\\build-and-run.ps1 Game.Mod=ra"
        )
    return pid


# --------------------------------------------------------------------------
# debugger exclusivity
# --------------------------------------------------------------------------


def _processes_named(*names: str) -> list[tuple[str, int]]:
    found: list[tuple[str, int]] = []
    for name in names:
        try:
            out = subprocess.run(
                ["tasklist", "/FI", f"IMAGENAME eq {name}", "/NH", "/FO", "CSV"],
                capture_output=True,
                text=True,
                timeout=15,
            ).stdout
        except (OSError, subprocess.SubprocessError):
            continue
        for line in out.splitlines():
            parts = [p.strip('"') for p in line.strip().split('","')]
            if len(parts) >= 2 and parts[0].lower() == name.lower():
                try:
                    found.append((parts[0], int(parts[1])))
                except ValueError:
                    pass
    return found


def check_debugger_free() -> None:
    """Fail with an actionable message if something already holds the game.

    CoreCLR permits exactly one debugger per process, so a probe cannot run while
    VS Code is attached (vsdbg) or another probe is in flight (netcoredbg). The
    attach would otherwise fail deep inside netcoredbg with an opaque HRESULT.
    """
    stale = read_debug_state()
    if stale:
        pid = int(stale.get("DebuggerPid") or 0)
        if pid and pid_alive(pid):
            raise ToolError(
                f"A debug probe is already in flight (netcoredbg PID {pid}).\n"
                f"  started     : {stale.get('StartedAt')}\n"
                f"  breakpoints : {', '.join(stale.get('Breakpoints') or []) or '(none)'}\n"
                "\n"
                "CoreCLR allows only one debugger per process, so this probe cannot attach.\n"
                "The game may be halted at a breakpoint right now.\n"
                "  Wait for it to finish, or force cleanup with:\n"
                "    .kiro\\scripts\\build\\kill-game.ps1 -All"
            )
        # Stale file from a probe that died without cleaning up.
        clear_debug_state()

    others = _processes_named("netcoredbg.exe", "vsdbg.exe")
    if others:
        listed = ", ".join(f"{n} PID {p}" for n, p in others)
        raise ToolError(
            f"A debugger is already running: {listed}\n"
            "\n"
            "CoreCLR allows only one debugger per process, so this probe cannot attach.\n"
            "  If this is VS Code: stop the debug session (Shift+F5), then retry.\n"
            "  If it is a leftover probe: .kiro\\scripts\\build\\kill-game.ps1 -All"
        )


def read_debug_state() -> dict[str, Any] | None:
    if not paths.DEBUG_STATE.exists():
        return None
    try:
        return json.loads(paths.DEBUG_STATE.read_text(encoding="utf-8-sig"))
    except (OSError, json.JSONDecodeError):
        return None


def write_debug_state(debugger_pid: int, breakpoints: list[str]) -> None:
    paths.ensure_dirs()
    paths.DEBUG_STATE.write_text(
        json.dumps(
            {
                "DebuggerPid": debugger_pid,
                "StartedAt": datetime.now(timezone.utc).astimezone().isoformat(timespec="seconds"),
                "Breakpoints": breakpoints,
                "OwnerPid": os.getpid(),
            },
            indent=2,
        ),
        encoding="utf-8",
    )


def clear_debug_state() -> None:
    try:
        paths.DEBUG_STATE.unlink(missing_ok=True)
    except OSError:
        pass


# --------------------------------------------------------------------------
# source resolution
# --------------------------------------------------------------------------


def resolve_source(spec: str) -> Path:
    """Turn a user-supplied source reference into an absolute path.

    Accepts an absolute path, a repo-relative path, or a bare filename that is
    searched for under the repo. A bare filename matching more than one file is
    an error listing the candidates, because silently picking one would set the
    breakpoint somewhere the caller did not mean.
    """
    raw = spec.strip().strip('"')
    candidate = Path(raw)

    if candidate.is_absolute():
        if not candidate.is_file():
            raise ToolError(f"Source file not found: {candidate}")
        return candidate.resolve()

    direct = (paths.REPO_ROOT / candidate).resolve()
    if direct.is_file():
        return direct

    name = candidate.name
    if name != raw:
        raise ToolError(
            f"Source file not found: {paths.rel(direct)}\n"
            f"  (interpreted '{raw}' as a repo-relative path)"
        )

    matches = _find_by_name(name)
    if not matches:
        raise ToolError(
            f"No file named '{name}' found under the repo.\n"
            "  Pass a repo-relative path instead, e.g. OpenRA.Mods.Common/Traits/Mobile.cs"
        )
    if len(matches) > 1:
        listed = "\n".join(f"    {paths.rel(m)}" for m in sorted(matches)[:15])
        more = "" if len(matches) <= 15 else f"\n    ... and {len(matches) - 15} more"
        raise ToolError(
            f"'{name}' is ambiguous - {len(matches)} files match:\n{listed}{more}\n"
            "  Pass a repo-relative path to disambiguate."
        )
    return matches[0]


def _find_by_name(name: str) -> list[Path]:
    hits: list[Path] = []
    target = name.lower()
    for root, dirs, files in os.walk(paths.REPO_ROOT):
        dirs[:] = [d for d in dirs if d not in paths.SOURCE_SKIP_DIRS]
        for f in files:
            if f.lower() == target:
                hits.append(Path(root, f).resolve())
    return hits


def parse_location(spec: str) -> tuple[Path, int]:
    """Parse 'file:line' into (absolute path, line).

    Splits on the last colon so drive letters in absolute Windows paths survive.
    """
    raw = spec.strip().strip('"')
    if ":" not in raw:
        raise ToolError(
            f"Breakpoint '{raw}' is missing a line number. Expected <file>:<line>, "
            "e.g. Mobile.cs:682"
        )
    head, _, tail = raw.rpartition(":")
    try:
        line = int(tail)
    except ValueError:
        raise ToolError(
            f"Breakpoint '{raw}' does not end in a line number. Expected <file>:<line>, "
            "e.g. Mobile.cs:682"
        ) from None
    if line < 1:
        raise ToolError(f"Breakpoint '{raw}' has a line number below 1.")
    return resolve_source(head), line


def find_netcoredbg() -> str:
    """Absolute path to netcoredbg.exe, or a ToolError explaining how to get it."""
    from shutil import which

    exe = which("netcoredbg")
    if exe:
        return exe
    raise ToolError(
        "netcoredbg was not found on PATH.\n"
        "  Install it with WinGet:  winget install Samsung.NetCoreDbg\n"
        "  Or grab a release:       https://github.com/Samsung/netcoredbg/releases\n"
        "  (then make sure the folder containing netcoredbg.exe is on PATH)"
    )


def fail(message: str) -> None:
    """Print an expected failure to stderr and exit non-zero."""
    print(f"ERROR: {message}", file=sys.stderr)
    raise SystemExit(1)
