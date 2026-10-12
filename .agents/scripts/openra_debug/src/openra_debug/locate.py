"""Find the tracked OpenRA game and confirm it is really *our* game.

The public API never takes a PID. The only source of the attach target is
``.pyddock/tmp/run/game.json``, written by ``build-and-run.ps1``. Before anyone
attaches a debugger we prove three things about that PID, because attaching a
debugger to the wrong process (or a PID that Windows has since recycled) is
exactly the kind of mistake that is expensive to undo:

  1. the process is alive,
  2. it was started when build-and-run.ps1 says it was — the recorded StartTime
     matches the OS process creation time, which defeats PID reuse, and
  3. its image is a ``dotnet`` host running *this repo's* ``bin/OpenRA.dll`` —
     so we never attach to some unrelated dotnet process that happens to have
     inherited the recycled PID.

All of that is done without spawning anything: inside a ``run_python`` snippet a
``subprocess.Popen`` is validated against the shell policy, so ``tasklist`` would
be a policy-checked spawn on every liveness check. ``psutil`` uses native APIs
and avoids it. ``pywin32`` is a fallback for the create-time check if psutil is
somehow unavailable.
"""

from __future__ import annotations

import ctypes
import json
from datetime import datetime, timezone
from pathlib import Path
from shutil import which

from . import paths

try:
    import psutil
except ImportError:  # pragma: no cover - psutil is a declared dependency
    psutil = None  # type: ignore[assignment]


class LocateError(Exception):
    """An expected, explainable failure. Printed without a traceback."""


# How much slack to allow between the StartTime build-and-run.ps1 recorded and
# the OS-reported process creation time. They are captured a fraction of a second
# apart and by different clocks, so an exact match is too strict; a few seconds is
# plenty to still catch a recycled PID (a different process created much later).
_START_TIME_TOLERANCE_S = 5.0


# --------------------------------------------------------------------------
# tracked game process
# --------------------------------------------------------------------------


def read_game_state() -> dict:
    if not paths.GAME_STATE.exists():
        raise LocateError(
            "No tracked game session.\n"
            "  Start one with: .kiro\\scripts\\build\\build-and-run.ps1 Game.Mod=ra"
        )
    try:
        return json.loads(paths.GAME_STATE.read_text(encoding="utf-8-sig"))
    except (OSError, json.JSONDecodeError) as ex:
        raise LocateError(f"Could not read {paths.rel(paths.GAME_STATE)}: {ex}") from ex


def _parse_iso(value: str | None) -> datetime | None:
    if not value:
        return None
    try:
        dt = datetime.fromisoformat(value)
    except ValueError:
        return None
    if dt.tzinfo is None:
        dt = dt.replace(tzinfo=timezone.utc)
    return dt


def _process(pid: int):
    """Return a psutil.Process for a live pid, or None. Never raises."""
    if psutil is None or not pid:
        return None
    try:
        return psutil.Process(pid)
    except (psutil.NoSuchProcess, psutil.AccessDenied, ValueError):
        return None


# --------------------------------------------------------------------------
# ctypes-based Win32 fallback (no dependency; used when psutil is unavailable,
# e.g. before the pyddock sync installs it). psutil, when present, is preferred
# because it also yields cmdline() for the full image-identity check.
#
# PROCESS_QUERY_LIMITED_INFORMATION (0x1000) is the least-privileged right that
# still allows GetProcessTimes / QueryFullProcessImageName. Unlike VM_READ it is
# not refused by a hardened target such as the .NET runtime, so it works against
# the game process.
# --------------------------------------------------------------------------
_PROCESS_QUERY_LIMITED_INFORMATION = 0x1000


class _FILETIME(ctypes.Structure):
    _fields_ = [("low", ctypes.c_uint32), ("high", ctypes.c_uint32)]


def _open_limited(pid: int):
    """A PROCESS_QUERY_LIMITED_INFORMATION handle (int), or 0. Close with CloseHandle."""
    try:
        k32 = ctypes.windll.kernel32  # type: ignore[attr-defined]
    except (AttributeError, OSError):
        return 0
    return k32.OpenProcess(_PROCESS_QUERY_LIMITED_INFORMATION, False, int(pid))


def _create_time(pid: int) -> datetime | None:
    """OS process creation time as an aware UTC datetime, or None."""
    proc = _process(pid)
    if proc is not None:
        try:
            return datetime.fromtimestamp(proc.create_time(), tz=timezone.utc)
        except (psutil.NoSuchProcess, psutil.AccessDenied, OSError):
            return None

    handle = _open_limited(pid)
    if not handle:
        return None
    try:
        k32 = ctypes.windll.kernel32  # type: ignore[attr-defined]
        created, exited, kern, user = _FILETIME(), _FILETIME(), _FILETIME(), _FILETIME()
        if not k32.GetProcessTimes(handle, ctypes.byref(created), ctypes.byref(exited),
                                   ctypes.byref(kern), ctypes.byref(user)):
            return None
        # FILETIME: 100-ns ticks since 1601-01-01 UTC.
        ticks = (created.high << 32) | created.low
        seconds = ticks / 10_000_000 - 11_644_473_600  # to Unix epoch
        return datetime.fromtimestamp(seconds, tz=timezone.utc)
    except (OSError, ValueError, OverflowError):
        return None
    finally:
        _close_handle(handle)


def _close_handle(handle: int) -> None:
    try:
        ctypes.windll.kernel32.CloseHandle(handle)  # type: ignore[attr-defined]
    except (AttributeError, OSError):
        pass


def _exe_path(pid: int) -> str | None:
    proc = _process(pid)
    if proc is not None:
        try:
            return proc.exe()
        except (psutil.NoSuchProcess, psutil.AccessDenied, OSError):
            return None
    # Fallback: QueryFullProcessImageNameW (does not need VM_READ).
    handle = _open_limited(pid)
    if not handle:
        return None
    try:
        k32 = ctypes.windll.kernel32  # type: ignore[attr-defined]
        buf = ctypes.create_unicode_buffer(1024)
        size = ctypes.c_uint32(len(buf))
        if k32.QueryFullProcessImageNameW(handle, 0, buf, ctypes.byref(size)):
            return buf.value
        return None
    except (OSError, ValueError):
        return None
    finally:
        _close_handle(handle)


def _cmdline(pid: int) -> list[str]:
    proc = _process(pid)
    if proc is None:
        return []
    try:
        return list(proc.cmdline())
    except (psutil.NoSuchProcess, psutil.AccessDenied, OSError):
        return []


def pid_alive(pid: int) -> bool:
    if not pid:
        return False
    if psutil is not None:
        return _process(pid) is not None
    # ctypes fallback: opening a limited-info handle succeeds iff the process
    # exists. Not refused by the runtime's hardening (unlike VM_READ).
    handle = _open_limited(pid)
    if not handle:
        return False
    _close_handle(handle)
    return True


def resolve_game_pid() -> int:
    """The pid of the running, tracked, identity-checked game.

    Raises LocateError with an actionable message on any failure: no state, dead
    process, PID reuse, or an image that is not this repo's OpenRA.
    """
    state = read_game_state()
    pid = int(state.get("GamePid") or 0)
    if not pid:
        raise LocateError(
            f"{paths.rel(paths.GAME_STATE)} has no GamePid. Relaunch the game."
        )

    if not pid_alive(pid):
        raise LocateError(
            f"Tracked game (PID {pid}) is not running - the state file is stale.\n"
            "  Relaunch with: .kiro\\scripts\\build\\build-and-run.ps1 Game.Mod=ra"
        )

    # (2) start-time match: defeats PID reuse.
    recorded = _parse_iso(state.get("StartTime") or state.get("StartedAt"))
    actual = _create_time(pid)
    if recorded is not None and actual is not None:
        drift = abs((actual - recorded).total_seconds())
        if drift > _START_TIME_TOLERANCE_S:
            raise LocateError(
                f"PID {pid} is alive but was created {drift:.0f}s from the tracked "
                "launch time.\n"
                "  Windows has almost certainly recycled the PID onto a different "
                "process.\n"
                f"    tracked StartTime : {recorded.isoformat()}\n"
                f"    actual created    : {actual.isoformat()}\n"
                "  Relaunch the game to refresh the tracked session:\n"
                "    .kiro\\scripts\\build\\build-and-run.ps1 Game.Mod=ra"
            )

    # (3) image identity. Two levels, depending on what we can see:
    #   - exe path is always available (ctypes). It must be a dotnet host; a PID
    #     recycled onto, say, explorer.exe is caught here.
    #   - cmdline is only available with psutil. When we have it, we additionally
    #     require it to reference *this repo's* OpenRA.dll, which pins identity to
    #     this workspace rather than any dotnet process.
    exe = _exe_path(pid)
    cmdline = _cmdline(pid)

    if exe is not None:
        exe_lower = exe.lower()
        if not (exe_lower.endswith("dotnet.exe") or exe_lower.endswith("openra.dll")
                or "openra" in exe_lower):
            raise LocateError(
                f"PID {pid} is alive but its image is not a dotnet/OpenRA host.\n"
                f"    exe : {exe}\n"
                "  The PID was almost certainly recycled onto an unrelated process. "
                "Relaunch:\n"
                "    .kiro\\scripts\\build\\build-and-run.ps1 Game.Mod=ra"
            )

    if cmdline:
        our_dll = str(paths.GAME_DLL).lower()
        runs_our_game = any(our_dll in a.lower() for a in cmdline) or any(
            a.lower().endswith("openra.dll") for a in cmdline
        )
        if not runs_our_game:
            raise LocateError(
                f"PID {pid} is a dotnet process, but not this repo's OpenRA.\n"
                f"    cmdline : {' '.join(cmdline[:6])}\n"
                f"    expected a reference to {paths.rel(paths.GAME_DLL)}\n"
                "  The tracked session is stale or the PID was reused. Relaunch:\n"
                "    .kiro\\scripts\\build\\build-and-run.ps1 Game.Mod=ra"
            )

    return pid


# --------------------------------------------------------------------------
# netcoredbg discovery
# --------------------------------------------------------------------------


def find_netcoredbg() -> str:
    """Absolute path to netcoredbg.exe, or a LocateError explaining how to get it."""
    exe = which("netcoredbg")
    if exe:
        return exe
    raise LocateError(
        "netcoredbg was not found on PATH.\n"
        "  Install it with WinGet:  winget install Samsung.NetCoreDbg\n"
        "  Or grab a release:       https://github.com/Samsung/netcoredbg/releases\n"
        "  (then make sure the folder containing netcoredbg.exe is on PATH)"
    )


# --------------------------------------------------------------------------
# source resolution
# --------------------------------------------------------------------------


def resolve_source(spec: str) -> Path:
    """Turn a user-supplied source reference into an absolute path.

    Accepts an absolute path, a repo-relative path, or a bare filename searched
    for under the repo. A bare filename matching more than one file is an error
    listing the candidates, because silently picking one would set the breakpoint
    somewhere the caller did not mean.
    """
    raw = spec.strip().strip('"')
    candidate = Path(raw)

    if candidate.is_absolute():
        if not candidate.is_file():
            raise LocateError(f"Source file not found: {candidate}")
        return candidate.resolve()

    direct = (paths.REPO_ROOT / candidate).resolve()
    if direct.is_file():
        return direct

    name = candidate.name
    if name != raw:
        raise LocateError(
            f"Source file not found: {paths.rel(direct)}\n"
            f"  (interpreted '{raw}' as a repo-relative path)"
        )

    matches = _find_by_name(name)
    if not matches:
        raise LocateError(
            f"No file named '{name}' found under the repo.\n"
            "  Pass a repo-relative path instead, e.g. "
            "OpenRA.Mods.Common/Traits/Mobile.cs"
        )
    if len(matches) > 1:
        listed = "\n".join(f"    {paths.rel(m)}" for m in sorted(matches)[:15])
        more = "" if len(matches) <= 15 else f"\n    ... and {len(matches) - 15} more"
        raise LocateError(
            f"'{name}' is ambiguous - {len(matches)} files match:\n{listed}{more}\n"
            "  Pass a repo-relative path to disambiguate."
        )
    return matches[0]


def _find_by_name(name: str) -> list[Path]:
    import os

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
        raise LocateError(
            f"Breakpoint '{raw}' is missing a line number. Expected <file>:<line>, "
            "e.g. Mobile.cs:324"
        )
    head, _, tail = raw.rpartition(":")
    try:
        line = int(tail)
    except ValueError:
        raise LocateError(
            f"Breakpoint '{raw}' does not end in a line number. Expected "
            "<file>:<line>, e.g. Mobile.cs:324"
        ) from None
    if line < 1:
        raise LocateError(f"Breakpoint '{raw}' has a line number below 1.")
    return resolve_source(head), line
