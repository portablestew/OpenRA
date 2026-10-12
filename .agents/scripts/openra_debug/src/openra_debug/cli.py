"""Transport for netcoredbg's CLI interpreter, driven over stdio.

Why the CLI interpreter and not DAP (``--interpreter=vscode``): with the CLI
interpreter, closing stdin (or the owning process dying) makes netcoredbg detach
cleanly and exit 0 — the debuggee keeps running. That was verified empirically
against a live game, including a hard kill of the owning process while the game
was halted at a breakpoint: netcoredbg went away within a second and the game
kept ticking. The DAP path had the opposite failure mode (an undetached death
took the game down with it), so the CLI interpreter is what gives this library
its core safety property.

Two facts about the CLI interpreter shape everything here:

  * It has **no per-command completion marker.** ``continue`` answers
    ``^running``, ``detach`` answers ``^exit``, but ``delete 3`` produces no
    output at all, and asynchronous notifications (``library loaded``,
    ``thread created``, ``stopped, reason: ...``) interleave freely with command
    output. Waiting for the pipe to go quiet is therefore unreliable.

    So after every command we send a **sentinel**: a bogus command
    ``__mark_<n>`` that the interpreter rejects with a unique, deterministic
    line ``Unknown command: '__mark_<n>'``. That echoed line is our end-of-
    response terminator. Everything printed before it (minus async event lines)
    is the command's output.

  * Attaching **halts the process** (``stopped, reason: interrupted``). The
    session layer must issue ``continue`` after attach to let the game run.

A background reader thread owns the pipe: it accumulates raw output, and other
threads (the caller, the watchdog) read snapshots of it. A thread rather than
select() because select() on Windows works only on sockets, not pipes.
"""

from __future__ import annotations

import re
import subprocess
import threading
import time
from pathlib import Path
from typing import Callable

# ANSI colour netcoredbg wraps error lines in (e.g. red "Unknown command").
_ANSI = re.compile(r"\x1b\[[0-9;]*m")
# Stray control chars netcoredbg emits inside enum formatting (backspaces etc.).
_CTRL = re.compile(r"[\x00-\x08\x0b\x0c\x0e-\x1f]")

# Async notification lines that can appear at any time, interleaved with the
# output of whatever command we sent. Stripped from command responses; the raw
# log keeps everything.
_ASYNC_PREFIXES = (
    "library loaded:",
    "library unloaded:",
    "thread created,",
    "thread exited,",
    "managed thread created",
    "managed thread exited",
    "native thread created",
    "native thread attached",
    "native thread exited",
    "no symbols loaded,",
    "symbols loaded,",
)

_STOP_RE = re.compile(r"^stopped, reason: (?P<reason>[^,]+),")
_BREAKPOINT_HIT_RE = re.compile(r"reason: breakpoint (?P<num>\d+) hit")
_THREAD_ID_RE = re.compile(r"thread id: (?P<tid>\d+)")


class TransportError(Exception):
    """The netcoredbg process failed or went away unexpectedly."""


def strip_ansi(text: str) -> str:
    return _CTRL.sub("", _ANSI.sub("", text))


class CliClient:
    """A line-oriented conversation with ``netcoredbg --interpreter=cli``."""

    def __init__(self, argv: list[str], cwd: Path | None = None,
                 log_path: Path | None = None):
        self._argv = argv
        self._cwd = cwd
        self._log_path = log_path
        self._log = None

        self._proc: subprocess.Popen[bytes] | None = None
        self._mark = 0
        self._send_lock = threading.Lock()

        # Everything read from the pipe, decoded, appended in order. The reader
        # thread is the only writer; readers take the lock and copy.
        self._buf = ""
        self._buf_lock = threading.Condition()
        self._eof = threading.Event()

        self._reader: threading.Thread | None = None

    # -- lifecycle ---------------------------------------------------------

    @property
    def pid(self) -> int:
        return self._proc.pid if self._proc else 0

    @property
    def closed(self) -> bool:
        return self._eof.is_set() or self._proc is None or self._proc.poll() is not None

    def start(self) -> None:
        if self._log_path:
            self._log_path.parent.mkdir(parents=True, exist_ok=True)
            self._log = self._log_path.open("w", encoding="utf-8")

        try:
            self._proc = subprocess.Popen(
                self._argv,
                cwd=str(self._cwd) if self._cwd else None,
                stdin=subprocess.PIPE,
                stdout=subprocess.PIPE,
                stderr=subprocess.STDOUT,
                bufsize=0,
            )
        except OSError as ex:
            raise TransportError(f"could not start {self._argv[0]}: {ex}") from ex

        self._reader = threading.Thread(target=self._read_loop, name="ncdbg-reader",
                                        daemon=True)
        self._reader.start()

    def close(self, kill_timeout: float = 5.0) -> None:
        """Detach and reap. Safe to call more than once.

        Closing stdin is what makes the CLI interpreter detach cleanly, so this
        is the last-ditch safe shutdown even if higher-level cleanup failed.
        """
        proc = self._proc
        if proc is not None:
            try:
                if proc.stdin:
                    proc.stdin.close()
            except OSError:
                pass
            try:
                proc.wait(timeout=kill_timeout)
            except subprocess.TimeoutExpired:
                proc.kill()
                try:
                    proc.wait(timeout=kill_timeout)
                except subprocess.TimeoutExpired:
                    pass
        if self._log:
            self._log.close()
            self._log = None

    # -- reader ------------------------------------------------------------

    def _read_loop(self) -> None:
        proc = self._proc
        if not proc or not proc.stdout:
            return
        # bufsize=0 hands us a raw FileIO whose read(n) is a single syscall
        # returning up to n bytes; buffered streams would need read1() to avoid
        # blocking for the full n.
        read = getattr(proc.stdout, "read1", proc.stdout.read)
        try:
            while True:
                chunk = read(65536)
                if not chunk:
                    break
                text = chunk.decode("utf-8", "replace")
                with self._buf_lock:
                    self._buf += text
                    self._buf_lock.notify_all()
                if self._log:
                    try:
                        self._log.write(text)
                        self._log.flush()
                    except (OSError, ValueError):
                        pass
        except (OSError, ValueError):
            pass
        finally:
            self._eof.set()
            with self._buf_lock:
                self._buf_lock.notify_all()

    def _snapshot(self) -> str:
        with self._buf_lock:
            return self._buf

    # -- sending -----------------------------------------------------------

    def _write_line(self, line: str) -> None:
        assert self._proc is not None and self._proc.stdin is not None
        try:
            self._proc.stdin.write((line + "\n").encode("utf-8"))
            self._proc.stdin.flush()
        except (OSError, ValueError) as ex:
            raise TransportError(f"could not send '{line}': {ex}") from ex

    def command(self, cmd: str, timeout: float = 20.0) -> str:
        """Send one command, return its output (async event lines removed).

        Uses a sentinel to delimit the response: we mark our position in the
        output buffer, send the command followed by ``__mark_<n>``, then wait for
        the interpreter to echo ``Unknown command: '__mark_<n>'`` and return
        everything printed in between.
        """
        if self.closed:
            raise TransportError(f"netcoredbg is gone; cannot send '{cmd}'")

        with self._send_lock:
            self._mark += 1
            token = f"__mark_{self._mark}"
            # The unique line netcoredbg prints for a bogus command. Colour codes
            # are stripped before matching.
            terminator = f"Unknown command: '{token}'"

            with self._buf_lock:
                start = len(self._buf)

            self._write_line(cmd)
            self._write_line(token)

            deadline = time.monotonic() + timeout
            with self._buf_lock:
                while True:
                    tail = self._buf[start:]
                    if terminator in strip_ansi(tail):
                        break
                    if self._eof.is_set():
                        raise TransportError(
                            f"netcoredbg exited while running '{cmd}'"
                        )
                    remaining = deadline - time.monotonic()
                    if remaining <= 0:
                        raise TransportError(
                            f"timed out after {timeout:g}s waiting for '{cmd}'"
                        )
                    self._buf_lock.wait(min(remaining, 0.25))
                response = self._buf[start:]

        return self._clean(response, terminator)

    def send_raw(self, cmd: str) -> None:
        """Send a command without waiting for a response.

        Used for shutdown verbs where waiting is pointless or unsafe: ``continue``
        (the game runs, no prompt comes back until the next stop) and ``detach``
        (netcoredbg answers ``^exit`` and dies).
        """
        with self._send_lock:
            self._write_line(cmd)

    @staticmethod
    def _clean(response: str, terminator: str) -> str:
        out: list[str] = []
        for raw_line in response.splitlines():
            line = strip_ansi(raw_line).rstrip()
            stripped = line.strip()
            if not stripped:
                continue
            if terminator in stripped:
                break
            if any(stripped.startswith(p) for p in _ASYNC_PREFIXES):
                continue
            # netcoredbg status markers: ^running, ^stopped, ^exit, ^done. These
            # are protocol acknowledgements, not command output.
            if stripped.startswith("^"):
                continue
            # The async 'stopped, reason: ...' notification also lands here when a
            # command races a stop; it is captured separately by wait_for_stop.
            if _STOP_RE.match(stripped):
                continue
            out.append(line)
        return "\n".join(out)

    # -- waiting for async stops -------------------------------------------

    def wait_for_stop(self, timeout: float,
                     progress: Callable[[], None] | None = None) -> dict | None:
        """Block until a ``stopped, reason: ...`` line appears, or timeout.

        Returns a dict describing the stop, or None on timeout. Scans from the
        current end of the buffer forward, so a stop that lands while we set up is
        still seen.
        """
        with self._buf_lock:
            start = len(self._buf)

        deadline = time.monotonic() + timeout
        with self._buf_lock:
            while True:
                tail = strip_ansi(self._buf[start:])
                stop = self._find_stop(tail)
                if stop is not None:
                    return stop
                if self._eof.is_set():
                    raise TransportError(
                        "netcoredbg exited while waiting for a breakpoint"
                    )
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    return None
                self._buf_lock.wait(min(remaining, 0.25))

    @staticmethod
    def _find_stop(text: str) -> dict | None:
        for line in text.splitlines():
            line = line.strip()
            m = _STOP_RE.match(line)
            if not m:
                continue
            reason = m.group("reason").strip()
            info: dict = {"reason": reason, "line": line}
            hit = _BREAKPOINT_HIT_RE.search(line)
            if hit:
                info["breakpoint"] = int(hit.group("num"))
            tid = _THREAD_ID_RE.search(line)
            if tid:
                info["thread_id"] = int(tid.group("tid"))
            return info
        return None

    def transcript(self) -> str:
        """The full raw conversation, for a report or post-mortem."""
        return self._snapshot()
