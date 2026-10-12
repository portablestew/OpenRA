"""A live debug session against the running OpenRA game.

Scoped to a context manager:

    with session() as dbg:
        hit = dbg.stop_at("Mobile.cs:324", when='self.Info.Name == "e1"')
        print(hit.stack_text)
        dbg.resume()

What the context manager guarantees, and why it matters:

  * **The game is only ever halted for a bounded time.** Whenever the game is
    stopped at a breakpoint, a watchdog thread is armed. If nothing resumes it
    within ``hold_timeout`` seconds (default 20), the watchdog resumes and
    detaches on its own. This is what stops a slow, wedged, or exception-throwing
    snippet from leaving a real-time game frozen. A ``Hit`` whose halt was
    auto-released raises :class:`StaleHitError` if you keep poking at it.

  * **On any exit path the game is left running and netcoredbg is detached.**
    ``__exit__`` runs ``delete <n>`` for every breakpoint, then ``continue``,
    then ``detach``. If any of that fails, closing netcoredbg's stdin is the
    backstop: the CLI interpreter treats stdin EOF as a clean detach (verified
    against a live game), so even a hard failure leaves the game alive.

Breakpoints are deleted *before* the final resume on purpose: a breakpoint in a
hot path such as ``Mobile.Tick`` re-fires within microseconds of resuming, and we
do not want to be racing that while trying to detach.
"""

from __future__ import annotations

import re
import threading
from contextlib import contextmanager
from datetime import datetime, timezone
from typing import Iterator

from . import locate, paths
from .cli import CliClient, TransportError
from .report import render_callstack, render_hit

DEFAULT_HOLD_TIMEOUT = 20.0
DEFAULT_STOP_TIMEOUT = 20.0

_BREAKPOINT_SET_RE = re.compile(r"Breakpoint (?P<num>\d+) at ")


class DebugError(Exception):
    """A debug operation failed. Printed without a traceback by the CLIs."""


class StaleHitError(DebugError):
    """The halt this Hit described has already been released.

    Raised when a Hit is inspected after its session resumed the game — either
    explicitly, or because the hold-timeout watchdog fired. The frame ids and
    thread the Hit referred to no longer describe a stopped process.
    """


class Result:
    """The text output of one raw debugger command, plus a little structure."""

    def __init__(self, command: str, text: str):
        self.command = command
        self.text = text

    def __repr__(self) -> str:
        return f"Result({self.command!r}, {len(self.text)} chars)"


class EvalResult:
    """The result of evaluating one expression at a stopped frame."""

    def __init__(self, expression: str, raw: str):
        self.expression = expression
        self.text = raw
        self.value = self._extract(expression, raw)
        self.error = raw if raw.strip().lower().startswith("error:") else None

    @staticmethod
    def _extract(expression: str, raw: str) -> str | None:
        # netcoredbg prints `expr = value`; return the value side. On an object it
        # is `expr = {Type}: {fields...}` which is still useful as-is.
        line = raw.strip()
        if line.lower().startswith("error:"):
            return None
        prefix = f"{expression} = "
        if line.startswith(prefix):
            return line[len(prefix):]
        # Some expressions echo a normalised form; fall back to the RHS of the
        # first '=' if present.
        if " = " in line:
            return line.split(" = ", 1)[1]
        return line or None

    def __repr__(self) -> str:
        return f"EvalResult({self.expression!r}, value={self.value!r})"


class Hit:
    """A snapshot of the game stopped at a breakpoint.

    Inspection methods (``stack``, ``eval``, ``locals_of``) query the live,
    stopped process, so they only work while the session is still halted at this
    stop. Once the session resumes — explicitly or via the watchdog — they raise
    :class:`StaleHitError`.
    """

    def __init__(self, session: "Session", stop: dict, epoch: int):
        self._session = session
        self._epoch = epoch
        self.reason: str = stop.get("reason", "unknown")
        self.thread_id: int | None = stop.get("thread_id")
        self.breakpoint: int | None = stop.get("breakpoint")
        self.at = datetime.now(timezone.utc).astimezone().isoformat(timespec="seconds")
        self.raw = stop.get("line", "")
        # Captured eagerly at the stop so the Hit stays useful in a report even
        # after resume.
        self.stack_text: str = ""

    # -- staleness ---------------------------------------------------------

    def _check_fresh(self) -> None:
        if self._session._epoch != self._epoch or not self._session._halted:
            raise StaleHitError(
                "This Hit's halt has already been released (the game resumed, or "
                "the hold-timeout watchdog fired). Set the breakpoint again and "
                "catch a fresh stop."
            )

    # -- inspection (live) -------------------------------------------------

    def stack(self, frames: int = 20, all_threads: bool = False) -> Result:
        self._check_fresh()
        return self._session._backtrace(frames=frames, all_threads=all_threads)

    def eval(self, expression: str) -> EvalResult:
        """Evaluate an expression at the current top frame."""
        self._check_fresh()
        return self._session._eval(expression)

    def eval_all(self, *expressions: str) -> list[EvalResult]:
        return [self.eval(e) for e in expressions]

    def frame(self, index: int) -> Result:
        """Select stack frame ``index`` (subsequent eval runs against it)."""
        self._check_fresh()
        return self._session._raw(f"frame {index}")

    @property
    def text(self) -> str:
        return render_hit(self)

    def __repr__(self) -> str:
        return (f"Hit(reason={self.reason!r}, breakpoint={self.breakpoint}, "
                f"thread={self.thread_id})")


class Session:
    """Owns the attached netcoredbg and the halt/resume state machine.

    Not constructed directly; use :func:`session`.
    """

    def __init__(self, game_pid: int, hold_timeout: float = DEFAULT_HOLD_TIMEOUT):
        self.game_pid = game_pid
        self.hold_timeout = hold_timeout

        stamp = datetime.now().strftime("%Y%m%d-%H%M%S")
        paths.ensure_dirs()
        self.transcript_path = paths.LOG_DIR / f"session.{stamp}.ncdbg.log"

        self._client: CliClient | None = None
        self._breakpoints: list[int] = []

        # Halt state. `_epoch` increments on every resume so a Hit can tell it has
        # gone stale. `_halted` is True exactly while the game is stopped.
        self._lock = threading.RLock()
        self._halted = False
        self._epoch = 0

        # Long-halt watchdog: armed while halted, resumes+detaches if the caller
        # sits on a stop past hold_timeout.
        self._watchdog: threading.Timer | None = None
        self._watchdog_fired = False
        self.notes: list[str] = []

    # -- lifecycle ---------------------------------------------------------

    @property
    def client(self) -> CliClient:
        if self._client is None:
            raise DebugError("session is not started")
        return self._client

    @property
    def debugger_pid(self) -> int:
        return self._client.pid if self._client else 0

    def _start(self) -> None:
        # find_netcoredbg() confirms it is installed and gives a good error if
        # not, but we invoke the *bare* name: the pyddock shell policy matches the
        # spawn's command against `^netcoredbg$`, which the absolute WinGet path
        # would not satisfy. CreateProcess resolves it via PATH just as which()
        # did. Keeping the argv exactly `netcoredbg --interpreter=cli --attach
        # <pid>` is also what the policy's arg allow-list is written against.
        locate.find_netcoredbg()
        argv = ["netcoredbg", "--interpreter=cli", "--attach", str(self.game_pid)]
        self._client = CliClient(argv, cwd=paths.REPO_ROOT,
                                log_path=self.transcript_path)
        self._client.start()
        _write_debug_state(self._client.pid, self.game_pid)

        # Attaching halts the process (stopped, reason: interrupted). Drain the
        # attach banner, then let the game run so we are not holding it frozen
        # before the caller has even asked for a breakpoint.
        self._client.wait_for_stop(timeout=30.0)
        self._resume_locked(record_epoch=False)

    def _close(self) -> None:
        """Guaranteed cleanup. Never raises."""
        self._cancel_watchdog()
        client = self._client
        if client is None:
            _clear_debug_state()
            return

        if not client.closed:
            # 1. Stop breakpoints firing before we try to resume past them.
            for num in list(self._breakpoints):
                try:
                    client.command(f"delete {num}", timeout=10.0)
                except TransportError as ex:
                    self.notes.append(f"could not delete breakpoint {num}: {ex}")
            self._breakpoints.clear()

            # 2. Resume (if halted), then detach. Both are fire-and-forget: the
            #    game runs on continue, and detach makes netcoredbg print ^exit
            #    and die.
            try:
                if self._halted:
                    client.send_raw("continue")
                    self._halted = False
                client.send_raw("detach")
            except TransportError as ex:
                self.notes.append(
                    f"clean detach failed ({ex}); falling back to stdin close, "
                    "which the CLI interpreter also treats as a clean detach"
                )

        # 3. Reap. close() shuts stdin, the ultimate safe detach, and waits.
        try:
            client.close()
        except OSError as ex:
            self.notes.append(f"could not reap netcoredbg: {ex}")

        _clear_debug_state()

    # -- watchdog ----------------------------------------------------------

    def _arm_watchdog(self) -> None:
        self._cancel_watchdog()
        self._watchdog = threading.Timer(self.hold_timeout, self._on_hold_timeout)
        self._watchdog.name = "ncdbg-holdwatch"
        self._watchdog.daemon = True
        self._watchdog.start()

    def _cancel_watchdog(self) -> None:
        if self._watchdog is not None:
            self._watchdog.cancel()
            self._watchdog = None

    def _on_hold_timeout(self) -> None:
        """Resume the game if a caller has held the halt too long.

        Runs on the timer thread. Only needs netcoredbg's pipe, so a wedged main
        thread cannot block it.
        """
        with self._lock:
            if not self._halted:
                return
            self._watchdog_fired = True
            self.notes.append(
                f"hold-timeout: game was halted longer than {self.hold_timeout:g}s; "
                "auto-resumed to avoid freezing it"
            )
            try:
                self._resume_locked()
            except TransportError as ex:
                self.notes.append(f"hold-timeout auto-resume failed: {ex}")

    # -- halt/resume -------------------------------------------------------

    def _resume_locked(self, record_epoch: bool = True) -> None:
        client = self.client
        client.send_raw("continue")
        self._halted = False
        if record_epoch:
            self._epoch += 1
        self._cancel_watchdog()

    def resume(self) -> None:
        """Let the game run again. Idempotent."""
        with self._lock:
            if not self._halted:
                return
            self._resume_locked()

    # -- breakpoints + stops ----------------------------------------------

    def _set_breakpoint(self, location: str, when: str | None) -> int:
        path, line = locate.parse_location(location)
        # netcoredbg's `break` parser rejects a drive-letter absolute path (the
        # "C:" colon confuses its file:line split), but accepts a repo-relative
        # path and resolves it against loaded symbols. We resolve the absolute
        # path first purely to validate existence and disambiguate, then hand
        # netcoredbg the relative form it actually parses.
        spec = f"break {locate.paths.rel(path)}:{line}"
        if when:
            spec += f" if {when}"
        out = self.client.command(spec, timeout=30.0)
        m = _BREAKPOINT_SET_RE.search(out)
        if not m:
            raise DebugError(
                f"could not set breakpoint at {locate.paths.rel(path)}:{line}: "
                f"{out.strip() or 'no confirmation from netcoredbg'}"
            )
        num = int(m.group("num"))
        self._breakpoints.append(num)
        return num

    def stop_at(self, location: str, when: str | None = None,
                timeout: float = DEFAULT_STOP_TIMEOUT) -> "Hit | None":
        """Set a breakpoint, run, and wait for it to be hit.

        Returns a :class:`Hit` on success, or None if the timeout elapses with no
        stop. On a hit the game is *halted*; the hold-timeout watchdog is armed,
        and you should inspect and then ``resume()`` (or let the ``with`` block
        exit) promptly.

        ``location`` is ``file:line`` (file may be absolute, repo-relative, or a
        bare filename if unique). ``when`` is a C# boolean condition evaluated in
        the frame, e.g. ``'self.Info.Name == "e1"'``.
        """
        with self._lock:
            self._set_breakpoint(location, when)
            self._resume_locked()  # run until the breakpoint fires

        stop = self.client.wait_for_stop(timeout=timeout)
        if stop is None:
            return None

        with self._lock:
            self._halted = True
            self._epoch += 1
            hit = Hit(self, stop, self._epoch)
            # Capture the stack eagerly so the Hit is still readable in a report
            # after we resume.
            hit.stack_text = self._backtrace(frames=20, all_threads=False).text
            self._arm_watchdog()
            return hit

    def callstack(self, all_threads: bool = True, frames: int = 20) -> Result:
        """Interrupt the game, capture the current stack(s), and resume.

        This is the "what is it doing right now" path. It halts the game only for
        the moment it takes to read the stack, then resumes immediately — no
        breakpoint, no lingering halt.

        Defaults to every managed thread (``backtrace all``): a bare ``interrupt``
        stops whichever thread happened to be scheduled (often a socket poll, not
        the logic loop), so the single-thread stack is rarely the interesting one.
        Pass ``all_threads=False`` for just the interrupted thread.
        """
        with self._lock:
            self.client.send_raw("interrupt")
        stop = self.client.wait_for_stop(timeout=10.0)
        if stop is None:
            raise DebugError("interrupt did not halt the game within 10s")
        with self._lock:
            self._halted = True
            self._epoch += 1
            try:
                result = self._backtrace(frames=frames, all_threads=all_threads)
            finally:
                self._resume_locked()
        return Result(result.command, render_callstack(result.text))

    # -- low-level inspection (assume halted; caller holds correctness) ----

    def _backtrace(self, frames: int, all_threads: bool) -> Result:
        cmd = "backtrace all" if all_threads else "backtrace"
        out = self.client.command(cmd, timeout=25.0)
        return Result(cmd, out)

    def _eval(self, expression: str) -> EvalResult:
        out = self.client.command(f"print {expression}", timeout=20.0)
        return EvalResult(expression, out)

    def _raw(self, command: str) -> Result:
        """Escape hatch: run an arbitrary CLI command against the session.

        For the occasional verb the typed API does not wrap (``list``, ``next``,
        ``step``, ``frame``). The caller owns correctness — e.g. stepping only
        makes sense while halted.
        """
        out = self.client.command(command, timeout=20.0)
        return Result(command, out)

    def run(self, script: str) -> Result:
        """Run a newline-separated sequence of raw CLI commands, one transcript.

        The "well known public syntax" is netcoredbg's own GDB-like CLI:

            dbg.run('''
                break Mobile.cs:324 if self.Info.Name == "e1"
                continue
                backtrace
                print self.ToCell
            ''')

        Lines are sent in order and their outputs concatenated. Blank lines and
        ``#`` comments are ignored. This is a thin convenience over :meth:`_raw`;
        for anything conditional, prefer the typed methods and real Python.

        Note: ``continue`` here does not wait for the next stop (the CLI gives no
        prompt back until one happens). Use :meth:`stop_at` when you need to wait
        for a breakpoint.
        """
        chunks: list[str] = []
        for raw_line in script.splitlines():
            line = raw_line.strip()
            if not line or line.startswith("#"):
                continue
            verb = line.split()[0].lower()
            if verb in ("continue", "c", "detach", "quit", "q", "run", "r"):
                # Fire-and-forget verbs: no response terminator comes back.
                with self._lock:
                    self.client.send_raw(line)
                    if verb in ("continue", "c"):
                        self._halted = False
                        self._epoch += 1
                        self._cancel_watchdog()
                chunks.append(f"> {line}\n(sent)")
                continue
            out = self.client.command(line, timeout=25.0)
            chunks.append(f"> {line}\n{out}")
        return Result("run", "\n\n".join(chunks))


# --------------------------------------------------------------------------
# public entry point
# --------------------------------------------------------------------------


@contextmanager
def session(hold_timeout: float = DEFAULT_HOLD_TIMEOUT) -> Iterator[Session]:
    """Attach to the tracked OpenRA game for the duration of the ``with`` block.

    Takes no PID: the target is the game tracked by ``build-and-run.ps1``, whose
    identity is verified (alive, right start time, right image) before attaching.

    On exit — normal, exception, or Ctrl-C — the game is resumed and netcoredbg is
    detached. While the block runs, any breakpoint stop halts the game; the
    ``hold_timeout`` (default 20s, comfortably under the 30s run_python budget)
    bounds how long a single halt can last before the game is auto-resumed.

        with session() as dbg:
            print(dbg.callstack().text)
    """
    game_pid = locate.resolve_game_pid()
    sess = Session(game_pid, hold_timeout=hold_timeout)
    sess._start()
    try:
        yield sess
    finally:
        sess._close()


# --------------------------------------------------------------------------
# debug-session state file (read by status-game.ps1)
# --------------------------------------------------------------------------


def _write_debug_state(debugger_pid: int, game_pid: int) -> None:
    import json
    import os

    paths.ensure_dirs()
    try:
        paths.DEBUG_STATE.write_text(
            json.dumps(
                {
                    "DebuggerPid": debugger_pid,
                    "GamePid": game_pid,
                    "StartedAt": datetime.now(timezone.utc).astimezone()
                    .isoformat(timespec="seconds"),
                    "OwnerPid": os.getpid(),
                },
                indent=2,
            ),
            encoding="utf-8",
        )
    except OSError:
        pass


def _clear_debug_state() -> None:
    try:
        paths.DEBUG_STATE.unlink(missing_ok=True)
    except OSError:
        pass
