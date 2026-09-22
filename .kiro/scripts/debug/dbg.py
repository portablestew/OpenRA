#!/usr/bin/env python
"""One-shot debug probe for a running OpenRA process.

A single invocation performs the whole arc: attach, set breakpoints, wait for a
hit, capture the stack / locals / expressions, resume, detach. The game keeps
running afterwards.

  # prove the pipeline works against the RA main menu (the shellmap is a live
  # battle, so Mobile's move activity ticks continuously)
  .kiro/scripts/debug/dbg.py probe --at Mobile.cs:682 --locals

  # only stop for a particular actor, and read some expressions
  .kiro/scripts/debug/dbg.py probe --at Mobile.cs:682 \
      --when 'self.Info.Name == "e1"' --eval self.Info.Name --eval self.Location

  # catch the next thrown exception anywhere
  .kiro/scripts/debug/dbg.py probe --on-exception all --timeout 60

Exit codes: 0 a breakpoint was hit, 2 nothing hit before the timeout, 1 error.

Note there is no "leave it halted" mode: holding the game stopped requires a
process to stay attached, and this tool is one-shot by design. netcoredbg also
supports neither hit counts nor logpoints, so --when is the only filter and a
breakpoint in a hot path will re-fire the instant it resumes.
"""

from __future__ import annotations

import argparse
import json
import sys
import time
from datetime import datetime
from pathlib import Path
from typing import Any

sys.path.insert(0, str(Path(__file__).resolve().parent))

from openra_dev import game, paths  # noqa: E402
from openra_dev.dap import DapError  # noqa: E402
from openra_dev.session import ProbeSession  # noqa: E402

EXIT_OK = 0
EXIT_ERROR = 1
EXIT_NO_HIT = 2


def build_parser() -> argparse.ArgumentParser:
    p = argparse.ArgumentParser(
        prog="dbg.py",
        description="One-shot debug probe against the tracked OpenRA process.",
        formatter_class=argparse.RawDescriptionHelpFormatter,
    )
    sub = p.add_subparsers(dest="command", required=True)

    probe = sub.add_parser("probe", help="set breakpoints, wait for a hit, report, resume")
    probe.add_argument(
        "--at",
        action="append",
        default=[],
        metavar="FILE:LINE",
        help="source breakpoint. FILE may be absolute, repo-relative, or a bare "
        "filename if unique (e.g. Mobile.cs:682). Repeatable.",
    )
    probe.add_argument(
        "--at-function",
        action="append",
        default=[],
        metavar="NAME",
        help="function breakpoint, e.g. OpenRA.Mods.Common.Traits.Mobile.Tick. Repeatable.",
    )
    probe.add_argument(
        "--on-exception",
        action="append",
        default=[],
        metavar="FILTER",
        help="break when an exception is thrown (filter names come from the "
        "adapter; 'all' and 'user-unhandled' are typical). Repeatable.",
    )
    probe.add_argument(
        "--when",
        metavar="EXPR",
        help="condition applied to every breakpoint. The only filter netcoredbg "
        "supports - there are no hit counts.",
    )
    probe.add_argument(
        "--timeout", type=float, default=20.0, metavar="SEC",
        help="how long to wait for a hit (default: 20). Keep it short: the whole "
        "game is halted while stopped.",
    )
    probe.add_argument("--frames", type=int, default=20, metavar="N",
                       help="stack frames to capture (default: 20)")
    probe.add_argument("--locals", action="store_true",
                       help="capture local variables of the top frame")
    probe.add_argument("--depth", type=int, default=1, metavar="N",
                       help="how deep to expand locals (default: 1)")
    probe.add_argument("--eval", action="append", default=[], metavar="EXPR",
                       help="expression to evaluate in the top frame. Repeatable.")
    probe.add_argument("--repeat", type=int, default=1, metavar="N",
                       help="capture N consecutive hits (default: 1)")
    probe.add_argument("--then", choices=["continue", "terminate"], default="continue",
                       help="what to do when done (default: continue)")
    probe.add_argument("--json", action="store_true",
                       help="print the JSON report instead of the human summary")

    sub.add_parser("status", help="report session and debugger availability, attach nothing")
    return p


# --------------------------------------------------------------------------
# status
# --------------------------------------------------------------------------


def cmd_status() -> int:
    state = game.read_game_state()
    pid = int(state.get("GamePid") or 0)
    alive = game.pid_alive(pid)

    print(f"game      : PID {pid} {'RUNNING' if alive else 'NOT RUNNING (stale state)'}")
    print(f"args      : {' '.join(state.get('GameArgs') or [])}")
    print(f"config    : {state.get('Configuration')}")
    print(f"started   : {state.get('StartedAt')}")

    try:
        game.check_debugger_free()
        print("debugger  : free - a probe or VS Code can attach")
    except game.ToolError as ex:
        print("debugger  : BUSY")
        for line in str(ex).splitlines():
            print(f"  {line}")
        return EXIT_ERROR
    return EXIT_OK if alive else EXIT_ERROR


# --------------------------------------------------------------------------
# probe
# --------------------------------------------------------------------------


def cmd_probe(args: argparse.Namespace) -> int:
    if not (args.at or args.at_function or args.on_exception):
        raise game.ToolError(
            "Nothing to break on. Pass at least one of --at, --at-function, --on-exception.\n"
            "  e.g. --at Mobile.cs:682"
        )
    if args.repeat < 1:
        raise game.ToolError("--repeat must be at least 1.")

    locations = [game.parse_location(spec) for spec in args.at]
    pid = game.game_pid()
    game.check_debugger_free()

    stamp = datetime.now().strftime("%Y%m%d-%H%M%S")
    report: dict[str, Any] = {
        "ok": False,
        "gamePid": pid,
        "requested": {
            "at": [f"{paths.rel(p)}:{line}" for p, line in locations],
            "atFunction": list(args.at_function),
            "onException": list(args.on_exception),
            "when": args.when,
            "timeoutSec": args.timeout,
            "repeat": args.repeat,
            "then": args.then,
        },
        "breakpoints": [],
        "hits": [],
        "timedOut": False,
        "resumed": False,
        "events": [],
        "output": [],
        "notes": [],
    }

    started = time.monotonic()
    with ProbeSession(pid, stamp, then=args.then) as session:
        report["debuggerPid"] = session.debugger_pid
        report["transcript"] = paths.rel(session.transcript)

        session.initialize()

        if locations:
            report["breakpoints"] += session.set_breakpoints(locations, args.when)
        if args.at_function:
            report["breakpoints"] += session.set_function_breakpoints(
                args.at_function, args.when
            )
        if args.on_exception:
            session.set_exception_breakpoints(args.on_exception)
            report["breakpoints"].append(
                {"requested": f"exception:{','.join(args.on_exception)}", "verified": True}
            )

        game.write_debug_state(
            session.debugger_pid, [b["requested"] for b in report["breakpoints"]]
        )

        unverified = [b for b in report["breakpoints"] if not b.get("verified")]
        if unverified and len(unverified) == len(report["breakpoints"]):
            report["notes"].append(
                "No breakpoint was verified. The line may hold no executable code, "
                "or the binary is stale - rebuild with "
                ".kiro\\scripts\\build\\build-and-run.ps1."
            )

        session.configuration_done()

        for n in range(args.repeat):
            stopped = session.wait_for_stop(args.timeout)
            if stopped is None:
                report["timedOut"] = True
                if n == 0:
                    report["notes"].append(
                        f"Nothing hit within {args.timeout:g}s. The code path may not "
                        "be running (a world must be loaded for actor traits to tick), "
                        "or --when never matched."
                    )
                break
            report["hits"].append(
                session.capture(stopped, args.frames, args.locals, args.depth, args.eval)
            )
            if n + 1 < args.repeat:
                session.resume()

        report["events"] = session.client.event_summary()
        report["output"] = session.client.output_text()

    report["notes"] += session.cleanup_notes
    report["resumed"] = args.then == "continue" and not any(
        "continue failed" in note for note in session.cleanup_notes
    )
    report["ok"] = bool(report["hits"])
    report["elapsedSec"] = round(time.monotonic() - started, 2)

    paths.ensure_dirs()
    report_path = paths.LOG_DIR / f"probe.{stamp}.json"
    report_path.write_text(json.dumps(report, indent=2), encoding="utf-8")
    report["report"] = paths.rel(report_path)

    if args.json:
        print(json.dumps(report, indent=2))
    else:
        print_summary(report)

    return EXIT_OK if report["hits"] else EXIT_NO_HIT


# --------------------------------------------------------------------------
# human summary
# --------------------------------------------------------------------------


def print_summary(r: dict[str, Any]) -> None:
    def section(title: str) -> None:
        print(f"\n== {title} " + "=" * max(0, 58 - len(title)))

    section("Probe")
    print(f"  game PID   : {r['gamePid']}")
    print(f"  netcoredbg : {r.get('debuggerPid')}")
    print(f"  elapsed    : {r['elapsedSec']}s")
    if r["requested"]["when"]:
        print(f"  condition  : {r['requested']['when']}")

    section("Breakpoints")
    for bp in r["breakpoints"]:
        mark = "ok  " if bp.get("verified") else "UNVERIFIED"
        line = f"  {mark} {bp['requested']}"
        if bp.get("line") and str(bp.get("line")) not in str(bp["requested"]):
            line += f" -> line {bp['line']}"
        if bp.get("message"):
            line += f"  ({bp['message']})"
        print(line)

    if not r["hits"]:
        section("Result")
        print("  No hit." if r["timedOut"] else "  No hit recorded.")
    for i, hit in enumerate(r["hits"], 1):
        section(f"Hit {i}/{len(r['hits'])}")
        print(f"  reason : {hit['reason']}  thread {hit['threadId']}  at {hit['at']}")
        if hit.get("exception"):
            ex = hit["exception"]
            print(f"  EXCEPTION {ex.get('id')}: {ex.get('description')}")

        print(f"  stack ({len(hit['stack'])} of {hit.get('totalFrames', '?')} frames):")
        for f in hit["stack"]:
            loc = f"{f['source']}:{f['line']}" if f.get("source") else "(no source)"
            print(f"    {f['name']}  [{loc}]")

        for scope in hit.get("scopes") or []:
            print(f"  {scope['name']}:")
            print_vars(scope.get("variables") or [], indent=4)
        if hit.get("truncated"):
            print(f"    ({hit['truncated']})")

        for e in hit.get("evals") or []:
            if "error" in e:
                print(f"  eval {e['expression']} -> ERROR {e['error']}")
            else:
                print(f"  eval {e['expression']} -> {e['result']}  ({e.get('type')})")

    if r.get("output"):
        section("Debuggee output")
        for line in r["output"][-20:]:
            print(f"  {line}")

    if r.get("events"):
        section("Events")
        for line in r["events"]:
            print(f"  {line}")

    section("State")
    print(f"  resumed    : {r['resumed']}")
    print(f"  report     : {r.get('report')}")
    print(f"  transcript : {r.get('transcript')}")
    for note in r.get("notes") or []:
        print(f"  NOTE: {note}")


def print_vars(variables: list[dict[str, Any]], indent: int) -> None:
    pad = " " * indent
    for v in variables:
        suffix = " ..." if v.get("expandable") else ""
        type_part = f" : {v['type']}" if v.get("type") else ""
        print(f"{pad}{v.get('name')}{type_part} = {v.get('value')}{suffix}")
        if v.get("children"):
            print_vars(v["children"], indent + 2)


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    try:
        if args.command == "status":
            return cmd_status()
        return cmd_probe(args)
    except game.ToolError as ex:
        print(f"ERROR: {ex}", file=sys.stderr)
        return EXIT_ERROR
    except DapError as ex:
        print(f"ERROR: debug adapter: {ex}", file=sys.stderr)
        print(
            "  If the game is left halted, recover with: "
            ".kiro\\scripts\\build\\kill-game.ps1 -All",
            file=sys.stderr,
        )
        return EXIT_ERROR
    except KeyboardInterrupt:
        print("\nInterrupted; the session was cleaned up.", file=sys.stderr)
        return EXIT_ERROR


if __name__ == "__main__":
    raise SystemExit(main())
