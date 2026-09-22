"""A one-shot debug probe against an already-running OpenRA process.

The lifecycle is deliberately scoped to a context manager. A probe that dies
while the debuggee is halted would leave the game frozen with nothing attached to
resume it, so cleanup (remove breakpoints -> continue -> disconnect -> reap) runs
on every exit path including exceptions and Ctrl-C.

Breakpoints are removed *before* the final continue on purpose: a breakpoint in a
hot path such as Mobile.Tick re-fires within microseconds of resuming, and we do
not want to be racing that while trying to detach.
"""

from __future__ import annotations

from datetime import datetime, timezone
from pathlib import Path
from typing import Any

from . import game, paths
from .dap import DapClient, DapError

# Guard rails on variable expansion. An OpenRA Actor graph is effectively
# unbounded; without these a --depth 3 probe would dump the whole world.
MAX_CHILDREN_PER_NODE = 40
MAX_VARIABLES_TOTAL = 400


class ProbeSession:
    def __init__(self, game_pid: int, stamp: str, then: str = "continue"):
        self.game_pid = game_pid
        self.then = then
        self.stamp = stamp

        paths.ensure_dirs()
        self.transcript = paths.LOG_DIR / f"probe.{stamp}.dap.log"

        self._client: DapClient | None = None
        self._sources: list[Path] = []
        self._had_function_bps = False
        self._had_exception_bps = False
        self._stopped_thread: int | None = None
        self.capabilities: dict[str, Any] = {}
        self.cleanup_notes: list[str] = []

    # -- lifecycle ---------------------------------------------------------

    @property
    def client(self) -> DapClient:
        if self._client is None:
            raise DapError("session is not started")
        return self._client

    @property
    def debugger_pid(self) -> int:
        return self._client.pid if self._client else 0

    def __enter__(self) -> "ProbeSession":
        exe = game.find_netcoredbg()
        # No --server: we own the debugger, so DAP goes over its stdin/stdout.
        argv = [exe, "--interpreter=vscode", "--attach", str(self.game_pid)]
        self._client = DapClient(argv, cwd=paths.REPO_ROOT, log_path=self.transcript)
        self._client.start()
        game.write_debug_state(self._client.pid, [])
        return self

    def __exit__(self, *_exc: object) -> None:
        self.cleanup()

    def cleanup(self) -> None:
        """Best-effort resume and detach. Never raises."""
        client = self._client
        if client is None:
            game.clear_debug_state()
            return

        if not client.closed:
            # 1. Stop the breakpoints firing before we try to resume past them.
            try:
                self.clear_breakpoints()
            except (DapError, OSError) as ex:
                self.cleanup_notes.append(f"could not clear breakpoints: {ex}")

            # 2. Let the game run again (or kill it, if asked).
            if self.then == "terminate":
                try:
                    client.request("terminate", {}, timeout=10.0, optional=True)
                    self.cleanup_notes.append("sent terminate; game was asked to exit")
                except (DapError, OSError) as ex:
                    self.cleanup_notes.append(f"terminate failed: {ex}")
            elif self._stopped_thread is not None:
                try:
                    client.request(
                        "continue", {"threadId": self._stopped_thread}, timeout=10.0
                    )
                except (DapError, OSError) as ex:
                    self.cleanup_notes.append(
                        f"continue failed: {ex} - the game may still be halted; "
                        "run .kiro\\scripts\\build\\kill-game.ps1 -All"
                    )

            # 3. Detach without taking the game down with us.
            try:
                client.request(
                    "disconnect",
                    {"restart": False, "terminateDebuggee": self.then == "terminate"},
                    timeout=10.0,
                    optional=True,
                )
            except (DapError, OSError) as ex:
                self.cleanup_notes.append(f"disconnect failed: {ex}")

        try:
            client.close()
        except OSError as ex:
            self.cleanup_notes.append(f"could not reap netcoredbg: {ex}")

        game.clear_debug_state()

    # -- setup -------------------------------------------------------------

    def initialize(self) -> dict[str, Any]:
        """initialize, plus the attach handshake netcoredbg expects.

        netcoredbg already attached from its --attach argument (main.cpp runs
        Initialize/Attach/ConfigurationDone before the command loop), so the
        'attach' request here is redundant protocol bookkeeping. It is sent with
        processId because a bare attach{} is a known netcoredbg failure, and it is
        optional because the command-line attach already did the real work.
        """
        self.capabilities = self.client.request(
            "initialize",
            {
                "clientID": "openra-dbg",
                "clientName": "OpenRA agent probe",
                "adapterID": "coreclr",
                "pathFormat": "path",
                "linesStartAt1": True,
                "columnsStartAt1": True,
                "supportsVariableType": True,
                "supportsVariablePaging": False,
                "supportsRunInTerminalRequest": False,
            },
            timeout=30.0,
        )
        # Not all adapters emit this; absence is not an error.
        self.client.wait_event("initialized", timeout=5.0)
        self.client.request(
            "attach", {"processId": self.game_pid}, timeout=30.0, optional=True
        )
        return self.capabilities

    def set_breakpoints(
        self, locations: list[tuple[Path, int]], condition: str | None = None
    ) -> list[dict[str, Any]]:
        """Set source breakpoints, grouped by file.

        DAP replaces every breakpoint for a source on each call, so all lines in
        one file must go in a single request.
        """
        by_file: dict[Path, list[int]] = {}
        for path, line in locations:
            by_file.setdefault(path, []).append(line)

        results: list[dict[str, Any]] = []
        for path, lines in by_file.items():
            spec: list[dict[str, Any]] = []
            for line in lines:
                entry: dict[str, Any] = {"line": line}
                if condition:
                    entry["condition"] = condition
                spec.append(entry)

            body = self.client.request(
                "setBreakpoints",
                {
                    "source": {"path": str(path), "name": path.name},
                    "breakpoints": spec,
                    "lines": lines,
                },
                timeout=30.0,
            )
            self._sources.append(path)
            verified = body.get("breakpoints") or []
            for line, got in zip(lines, verified + [{}] * len(lines)):
                results.append(
                    {
                        "requested": f"{paths.rel(path)}:{line}",
                        "verified": bool(got.get("verified")),
                        "line": got.get("line", line),
                        "id": got.get("id"),
                        "message": got.get("message"),
                    }
                )
        return results

    def set_function_breakpoints(
        self, names: list[str], condition: str | None = None
    ) -> list[dict[str, Any]]:
        spec = [
            {"name": n, **({"condition": condition} if condition else {})} for n in names
        ]
        body = self.client.request(
            "setFunctionBreakpoints", {"breakpoints": spec}, timeout=30.0
        )
        self._had_function_bps = True
        verified = body.get("breakpoints") or []
        return [
            {
                "requested": name,
                "verified": bool(got.get("verified")),
                "line": got.get("line"),
                "id": got.get("id"),
                "message": got.get("message"),
            }
            for name, got in zip(names, verified + [{}] * len(names))
        ]

    def set_exception_breakpoints(self, filters: list[str]) -> None:
        try:
            self.client.request(
                "setExceptionBreakpoints", {"filters": filters}, timeout=30.0
            )
        except DapError as ex:
            available = [
                f.get("filter")
                for f in (self.capabilities.get("exceptionBreakpointFilters") or [])
            ]
            hint = f" Adapter accepts: {', '.join(x for x in available if x)}" if available else ""
            raise DapError(f"{ex}.{hint}") from ex
        self._had_exception_bps = True

    def clear_breakpoints(self) -> None:
        for path in self._sources:
            self.client.request(
                "setBreakpoints",
                {"source": {"path": str(path), "name": path.name}, "breakpoints": [], "lines": []},
                timeout=10.0,
                optional=True,
            )
        self._sources.clear()
        if self._had_function_bps:
            self.client.request(
                "setFunctionBreakpoints", {"breakpoints": []}, timeout=10.0, optional=True
            )
            self._had_function_bps = False
        if self._had_exception_bps:
            self.client.request(
                "setExceptionBreakpoints", {"filters": []}, timeout=10.0, optional=True
            )
            self._had_exception_bps = False

    def configuration_done(self) -> None:
        self.client.request("configurationDone", {}, timeout=30.0, optional=True)

    # -- running -----------------------------------------------------------

    def wait_for_stop(self, timeout: float) -> dict[str, Any] | None:
        ev = self.client.wait_event("stopped", timeout=timeout)
        if ev is None:
            if self.client.closed:
                tail = "; ".join(self.client.stderr_text[-3:])
                raise DapError(
                    "netcoredbg exited while waiting for the breakpoint"
                    + (f": {tail}" if tail else "")
                )
            return None
        body = ev.get("body") or {}
        self._stopped_thread = body.get("threadId")
        return body

    def resume(self) -> None:
        if self._stopped_thread is None:
            return
        self.client.request("continue", {"threadId": self._stopped_thread}, timeout=15.0)
        self._stopped_thread = None

    # -- inspection --------------------------------------------------------

    def capture(
        self,
        stopped: dict[str, Any],
        frames: int,
        want_locals: bool,
        depth: int,
        evals: list[str],
    ) -> dict[str, Any]:
        thread_id = stopped.get("threadId")
        hit = {
            "reason": stopped.get("reason"),
            "description": stopped.get("description") or stopped.get("text"),
            "threadId": thread_id,
            "allThreadsStopped": stopped.get("allThreadsStopped"),
            "hitBreakpointIds": stopped.get("hitBreakpointIds"),
            "at": datetime.now(timezone.utc).astimezone().isoformat(timespec="seconds"),
            "stack": [],
            "scopes": [],
            "evals": [],
        }

        if stopped.get("reason") == "exception":
            info = self.client.request(
                "exceptionInfo", {"threadId": thread_id}, timeout=15.0, optional=True
            )
            if info:
                hit["exception"] = {
                    "id": info.get("exceptionId"),
                    "description": info.get("description"),
                    "breakMode": info.get("breakMode"),
                }

        body = self.client.request(
            "stackTrace",
            {"threadId": thread_id, "startFrame": 0, "levels": frames},
            timeout=20.0,
        )
        stack = body.get("stackFrames") or []
        hit["totalFrames"] = body.get("totalFrames", len(stack))
        for f in stack:
            source = f.get("source") or {}
            hit["stack"].append(
                {
                    "name": f.get("name"),
                    "source": paths.rel(source["path"]) if source.get("path") else None,
                    "line": f.get("line"),
                    "column": f.get("column"),
                }
            )

        if not stack:
            return hit
        frame_id = stack[0].get("id")

        if want_locals and frame_id is not None:
            budget = [MAX_VARIABLES_TOTAL]
            for scope in self.client.request(
                "scopes", {"frameId": frame_id}, timeout=20.0
            ).get("scopes") or []:
                ref = scope.get("variablesReference") or 0
                hit["scopes"].append(
                    {
                        "name": scope.get("name"),
                        "variables": self._expand(ref, depth, budget) if ref else [],
                    }
                )
            if budget[0] <= 0:
                hit["truncated"] = (
                    f"variable expansion stopped at {MAX_VARIABLES_TOTAL} entries"
                )

        for expr in evals:
            try:
                res = self.client.request(
                    "evaluate",
                    {"expression": expr, "frameId": frame_id, "context": "watch"},
                    timeout=20.0,
                )
                hit["evals"].append(
                    {
                        "expression": expr,
                        "result": res.get("result"),
                        "type": res.get("type"),
                    }
                )
            except DapError as ex:
                hit["evals"].append({"expression": expr, "error": str(ex)})

        return hit

    def _expand(self, ref: int, depth: int, budget: list[int]) -> list[dict[str, Any]]:
        if ref <= 0 or depth < 0 or budget[0] <= 0:
            return []
        try:
            body = self.client.request(
                "variables", {"variablesReference": ref}, timeout=20.0
            )
        except DapError as ex:
            return [{"name": "<error>", "value": str(ex)}]

        out: list[dict[str, Any]] = []
        children = (body.get("variables") or [])[:MAX_CHILDREN_PER_NODE]
        for v in children:
            if budget[0] <= 0:
                break
            budget[0] -= 1
            node: dict[str, Any] = {
                "name": v.get("name"),
                "type": v.get("type"),
                "value": v.get("value"),
            }
            child_ref = v.get("variablesReference") or 0
            if child_ref and depth > 0:
                kids = self._expand(child_ref, depth - 1, budget)
                if kids:
                    node["children"] = kids
            elif child_ref:
                node["expandable"] = True
            out.append(node)
        return out
