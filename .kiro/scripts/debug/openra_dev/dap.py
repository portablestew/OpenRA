"""A minimal Debug Adapter Protocol client, speaking to netcoredbg over stdio.

Why stdio and not a TCP server: netcoredbg's --server mode accepts exactly one
client for the lifetime of the process (open_streams() in src/main.cpp calls
listen_socket() once with no re-accept loop), so a socket buys nothing when the
client owns the debugger anyway. Driving it over its stdin/stdout means no port
allocation, no accept race, and the debugger dies with us.

Framing is the same as LSP: "Content-Length: N\\r\\n\\r\\n" followed by N bytes
of UTF-8 JSON.

Responses and events arrive interleaved on one stream, so a reader thread owns
the pipe and demultiplexes: responses land in a table keyed by request_seq,
events append to a journal that callers scan. (A thread rather than selectors
because select() on Windows only works with sockets, not pipes.)
"""

from __future__ import annotations

import json
import subprocess
import threading
import time
from pathlib import Path
from typing import Any, Callable, Iterable


class DapError(Exception):
    """The adapter rejected a request, or the transport failed."""


class DapClient:
    def __init__(self, argv: list[str], cwd: Path | None = None, log_path: Path | None = None):
        self._argv = argv
        self._cwd = cwd
        self._log_path = log_path
        self._log = None

        self._proc: subprocess.Popen[bytes] | None = None
        self._seq = 0
        self._send_lock = threading.Lock()

        self._responses: dict[int, dict[str, Any]] = {}
        self._resp_cv = threading.Condition()

        # Every event received, in order, kept for the final report.
        self.events: list[dict[str, Any]] = []
        self._events_cv = threading.Condition()
        self._ev_read = 0

        self._reader: threading.Thread | None = None
        self._stderr_reader: threading.Thread | None = None
        self.stderr_text: list[str] = []
        self._eof = threading.Event()

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
                stderr=subprocess.PIPE,
                bufsize=0,
            )
        except OSError as ex:
            raise DapError(f"could not start {self._argv[0]}: {ex}") from ex

        self._reader = threading.Thread(target=self._read_loop, name="dap-reader", daemon=True)
        self._reader.start()
        self._stderr_reader = threading.Thread(
            target=self._read_stderr, name="dap-stderr", daemon=True
        )
        self._stderr_reader.start()

    def close(self, kill_timeout: float = 5.0) -> None:
        """Shut the adapter down. Safe to call more than once."""
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

    def __enter__(self) -> "DapClient":
        self.start()
        return self

    def __exit__(self, *_exc: object) -> None:
        self.close()

    # -- transport ---------------------------------------------------------

    def _trace(self, direction: str, payload: dict[str, Any]) -> None:
        if not self._log:
            return
        stamp = time.strftime("%H:%M:%S") + f".{int(time.time() * 1000) % 1000:03d}"
        try:
            self._log.write(f"{stamp} {direction} {json.dumps(payload)}\n")
            self._log.flush()
        except (OSError, ValueError):
            pass

    def _read_stderr(self) -> None:
        proc = self._proc
        if not proc or not proc.stderr:
            return
        try:
            for raw in proc.stderr:
                text = raw.decode("utf-8", "replace").rstrip()
                if text:
                    self.stderr_text.append(text)
                    self._trace("ERR", {"stderr": text})
        except (OSError, ValueError):
            pass

    def _read_loop(self) -> None:
        proc = self._proc
        if not proc or not proc.stdout:
            return
        # bufsize=0 hands us a raw FileIO, which has no read1(); its read(n) is a
        # single syscall returning up to n bytes, which is the behaviour we want.
        # Buffered streams need read1() instead, since their read(n) blocks for
        # the full n and would stall until the next message happened to arrive.
        read = getattr(proc.stdout, "read1", proc.stdout.read)

        buf = bytearray()
        try:
            while True:
                chunk = read(65536)
                if not chunk:
                    break
                buf += chunk
                while True:
                    sep = buf.find(b"\r\n\r\n")
                    if sep < 0:
                        break
                    length = self._content_length(buf[:sep])
                    if length is None:
                        # Unparseable header: drop it and resync rather than
                        # stalling forever on a malformed frame.
                        del buf[: sep + 4]
                        continue
                    end = sep + 4 + length
                    if len(buf) < end:
                        break
                    body = bytes(buf[sep + 4 : end])
                    del buf[:end]
                    self._dispatch(body)
        except (OSError, ValueError):
            pass
        finally:
            self._eof.set()
            # Wake anyone blocked on a response or event that will never come.
            with self._resp_cv:
                self._resp_cv.notify_all()
            with self._events_cv:
                self._events_cv.notify_all()

    @staticmethod
    def _content_length(header: bytes | bytearray) -> int | None:
        for line in bytes(header).split(b"\r\n"):
            name, _, value = line.partition(b":")
            if name.strip().lower() == b"content-length":
                try:
                    return int(value.strip())
                except ValueError:
                    return None
        return None

    def _dispatch(self, body: bytes) -> None:
        try:
            msg = json.loads(body.decode("utf-8", "replace"))
        except json.JSONDecodeError:
            self._trace("<<?", {"raw": body[:500].decode("utf-8", "replace")})
            return
        self._trace("<<", msg)

        kind = msg.get("type")
        if kind == "response":
            with self._resp_cv:
                self._responses[int(msg.get("request_seq", -1))] = msg
                self._resp_cv.notify_all()
        elif kind == "event":
            with self._events_cv:
                self.events.append(msg)
                self._events_cv.notify_all()
        else:
            # Reverse requests ("runInTerminal" and friends). netcoredbg does not
            # issue any, so recording it is enough.
            with self._events_cv:
                self.events.append(msg)
                self._events_cv.notify_all()

    # -- requests ----------------------------------------------------------

    def request(
        self,
        command: str,
        arguments: dict[str, Any] | None = None,
        timeout: float = 15.0,
        optional: bool = False,
    ) -> dict[str, Any]:
        """Send a request and return its body.

        With optional=True a rejection returns {} instead of raising, for requests
        whose support varies by adapter.
        """
        if self.closed:
            raise DapError(f"adapter is gone; cannot send '{command}'")

        with self._send_lock:
            self._seq += 1
            seq = self._seq
            msg: dict[str, Any] = {"seq": seq, "type": "request", "command": command}
            if arguments is not None:
                msg["arguments"] = arguments
            payload = json.dumps(msg).encode("utf-8")
            frame = b"Content-Length: %d\r\n\r\n%s" % (len(payload), payload)
            self._trace(">>", msg)
            try:
                assert self._proc is not None and self._proc.stdin is not None
                self._proc.stdin.write(frame)
                self._proc.stdin.flush()
            except (OSError, ValueError, AssertionError) as ex:
                raise DapError(f"could not send '{command}': {ex}") from ex

        deadline = time.monotonic() + timeout
        with self._resp_cv:
            while seq not in self._responses:
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    raise DapError(f"timed out after {timeout:g}s waiting for '{command}' response")
                if self._eof.is_set():
                    raise DapError(
                        f"adapter exited while waiting for '{command}'"
                        + (f": {self.stderr_text[-1]}" if self.stderr_text else "")
                    )
                self._resp_cv.wait(min(remaining, 0.25))
            response = self._responses.pop(seq)

        if not response.get("success", False):
            detail = response.get("message") or "no reason given"
            if optional:
                return {}
            raise DapError(f"'{command}' was rejected: {detail}")
        return response.get("body") or {}

    # -- events ------------------------------------------------------------

    def wait_event(
        self,
        names: str | Iterable[str],
        timeout: float,
        predicate: Callable[[dict[str, Any]], bool] | None = None,
    ) -> dict[str, Any] | None:
        """Block until one of `names` arrives, or timeout. None on timeout.

        Scans from an internal cursor, so an event that landed while we were busy
        sending other requests is still seen rather than missed.
        """
        wanted = {names} if isinstance(names, str) else set(names)
        deadline = time.monotonic() + timeout

        with self._events_cv:
            while True:
                for i in range(self._ev_read, len(self.events)):
                    ev = self.events[i]
                    if ev.get("event") in wanted and (predicate is None or predicate(ev)):
                        self._ev_read = i + 1
                        return ev
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    return None
                if self._eof.is_set():
                    return None
                self._events_cv.wait(min(remaining, 0.25))

    def output_text(self) -> list[str]:
        """Console output the debuggee produced, from DAP 'output' events."""
        lines: list[str] = []
        for ev in self.events:
            if ev.get("event") == "output":
                text = (ev.get("body") or {}).get("output") or ""
                if text.strip():
                    lines.append(text.rstrip("\n"))
        return lines

    def event_summary(self) -> list[str]:
        """One-line-per-event digest for the report, minus noisy module loads."""
        out: list[str] = []
        modules = 0
        for ev in self.events:
            name = ev.get("event")
            body = ev.get("body") or {}
            if name == "module":
                modules += 1
                continue
            if name == "output":
                continue
            if name == "stopped":
                out.append(
                    f"stopped: reason={body.get('reason')} thread={body.get('threadId')}"
                    f" allThreadsStopped={body.get('allThreadsStopped')}"
                )
            elif name == "thread":
                out.append(f"thread: {body.get('reason')} id={body.get('threadId')}")
            elif name == "exited":
                out.append(f"exited: code={body.get('exitCode')}")
            elif name == "terminated":
                out.append("terminated")
            elif name == "breakpoint":
                bp = body.get("breakpoint") or {}
                out.append(
                    f"breakpoint: {body.get('reason')} id={bp.get('id')}"
                    f" verified={bp.get('verified')} line={bp.get('line')}"
                )
            else:
                out.append(str(name))
        if modules:
            out.append(f"module: {modules} load event(s) (suppressed)")
        return out
