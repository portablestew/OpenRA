#!/usr/bin/env python
"""Collect and analyse OpenRA crash dumps via dotnet-dump.

  # what happened in the most recent crash
  .kiro/scripts/debug/dump.py analyze

  # snapshot a running game without stopping it for long, then read its stacks
  .kiro/scripts/debug/dump.py collect
  .kiro/scripts/debug/dump.py analyze --command "clrstack -all"

collect is the low-risk way to answer "what is the game doing right now" - it
pauses the process only briefly and needs no debugger attached, so unlike dbg.py
it cannot leave the game halted and does not contend for the single CoreCLR
debugger slot.

Full analysis output always goes to a log file; stdout is bounded, because
commands like `dumpheap -stat` on OpenRA run to thousands of lines.
"""

from __future__ import annotations

import argparse
import subprocess
import sys
import time
from datetime import datetime
from pathlib import Path
from shutil import which

sys.path.insert(0, str(Path(__file__).resolve().parent))

from openra_dev import game, paths  # noqa: E402

# Enough to explain a crash: what threads exist, the pending exception, and where
# the current thread was.
DEFAULT_COMMANDS = ["clrthreads", "pe -lines", "clrstack"]

DEFAULT_LINES = 120


def find_dotnet_dump() -> str:
    exe = which("dotnet-dump")
    if exe:
        return exe
    raise game.ToolError(
        "dotnet-dump was not found on PATH.\n"
        "  Install it with: dotnet tool install -g dotnet-dump\n"
        "  (then make sure %USERPROFILE%\\.dotnet\\tools is on PATH)"
    )


def list_dumps() -> list[Path]:
    if not paths.DUMP_DIR.exists():
        return []
    return sorted(
        paths.DUMP_DIR.glob("*.dmp"), key=lambda p: p.stat().st_mtime, reverse=True
    )


def describe(path: Path, selected_of: int | None = None) -> None:
    st = path.stat()
    age = time.time() - st.st_mtime
    age_text = f"{int(age // 60)}m ago" if age >= 60 else f"{int(age)}s ago"
    print(f"  path     : {paths.rel(path)}")
    print(f"  size     : {st.st_size / (1024 * 1024):,.1f} MB")
    print(
        "  created  : "
        f"{datetime.fromtimestamp(st.st_mtime).strftime('%Y-%m-%d %H:%M:%S')}  ({age_text})"
    )
    if selected_of == 0:
        print("  selected : explicit path")
    elif selected_of == 1:
        print("  selected : newest (the only dump)")
    elif selected_of is not None:
        print(f"  selected : newest of {selected_of} dumps")


def resolve_dump(explicit: str | None) -> tuple[Path, int]:
    if explicit:
        p = Path(explicit)
        if not p.is_absolute():
            p = (paths.REPO_ROOT / p).resolve()
        if not p.is_file():
            raise game.ToolError(f"Dump file not found: {p}")
        return p, 0

    dumps = list_dumps()
    if not dumps:
        raise game.ToolError(
            f"No dumps found in {paths.rel(paths.DUMP_DIR)}.\n"
            "  Crash dumps appear there automatically when the game dies with no "
            "debugger attached.\n"
            "  To snapshot a running game instead: "
            ".kiro\\scripts\\debug\\dump.py collect"
        )
    return dumps[0], len(dumps)


# --------------------------------------------------------------------------
# analyze
# --------------------------------------------------------------------------


def cmd_analyze(args: argparse.Namespace) -> int:
    exe = find_dotnet_dump()
    dump, total = resolve_dump(args.path)

    commands = list(args.commands) if args.commands else list(DEFAULT_COMMANDS)
    if args.thread is not None:
        commands.insert(0, f"setthread {args.thread}")

    print("== Dump " + "=" * 56)
    describe(dump, total)

    paths.ensure_dirs()
    stamp = datetime.now().strftime("%Y%m%d-%H%M%S")
    log_path = paths.LOG_DIR / f"dump-analysis.{stamp}.log"

    argv = [exe, "analyze", str(dump)]
    for c in commands:
        argv += ["-c", c]
    argv += ["-c", "exit"]

    try:
        proc = subprocess.run(argv, capture_output=True, text=True, timeout=args.timeout)
    except subprocess.TimeoutExpired:
        raise game.ToolError(
            f"dotnet-dump did not finish within {args.timeout:g}s. "
            "Large dumps and first-run symbol downloads are slow; retry with "
            "--timeout."
        ) from None
    except OSError as ex:
        raise game.ToolError(f"could not run dotnet-dump: {ex}") from ex

    transcript = (proc.stdout or "") + (proc.stderr or "")
    header = (
        f"dump       : {dump}\n"
        f"commands   : {commands}\n"
        f"exit code  : {proc.returncode}\n"
        + "-" * 70
        + "\n"
    )
    log_path.write_text(header + transcript, encoding="utf-8")

    if proc.returncode != 0 and not transcript.strip():
        raise game.ToolError(
            f"dotnet-dump exited {proc.returncode} with no output. See {paths.rel(log_path)}"
        )

    # dotnet-dump does not delimit or echo the commands it runs in batch mode, so
    # the transcript is one stream. Printing it whole (bounded) beats guessing at
    # section boundaries and mislabelling output.
    print("\n== " + "; ".join(commands) + " " + "=" * 8)
    lines = [line for line in transcript.splitlines() if line.strip()]
    for line in lines[: args.lines]:
        print(f"  {line}")
    if len(lines) > args.lines:
        print(f"  ... {len(lines) - args.lines} more lines (see the log)")

    print("\n" + "=" * 64)
    print(f"  full output: {paths.rel(log_path)}")
    if proc.returncode != 0:
        # A command SOS rejects aborts the batch, so later commands never ran.
        print(
            f"  dotnet-dump exited {proc.returncode} - one command failed and the"
            " rest of the batch was skipped."
        )
    return 0


# --------------------------------------------------------------------------
# collect
# --------------------------------------------------------------------------


def cmd_collect(args: argparse.Namespace) -> int:
    exe = find_dotnet_dump()
    pid = args.pid or game.game_pid()

    paths.ensure_dirs()
    stamp = datetime.now().strftime("%Y%m%d-%H%M%S")
    out = Path(args.output).resolve() if args.output else paths.DUMP_DIR / f"openra.live.{stamp}.dmp"

    print(f"Collecting {args.type} dump of PID {pid}...")
    print("  The process is paused while the dump is written.")
    argv = [exe, "collect", "-p", str(pid), "--type", args.type, "-o", str(out)]
    try:
        proc = subprocess.run(argv, capture_output=True, text=True, timeout=args.timeout)
    except subprocess.TimeoutExpired:
        raise game.ToolError(
            f"dotnet-dump collect did not finish within {args.timeout:g}s."
        ) from None
    except OSError as ex:
        raise game.ToolError(f"could not run dotnet-dump: {ex}") from ex

    if proc.stdout.strip():
        for line in proc.stdout.strip().splitlines():
            print(f"  {line}")
    if proc.returncode != 0:
        detail = (proc.stderr or proc.stdout or "").strip()
        raise game.ToolError(f"dotnet-dump collect exited {proc.returncode}: {detail}")
    if not out.is_file():
        raise game.ToolError(f"dotnet-dump reported success but {out} does not exist.")

    print("\n== Collected " + "=" * 51)
    describe(out, 0)
    print(f"\n  Analyse with: .kiro\\scripts\\debug\\dump.py analyze --path {paths.rel(out)}")
    return 0


def cmd_list() -> int:
    dumps = list_dumps()
    if not dumps:
        print(f"No dumps in {paths.rel(paths.DUMP_DIR)}.")
        return 0
    print(f"{len(dumps)} dump(s) in {paths.rel(paths.DUMP_DIR)}, newest first:\n")
    for d in dumps:
        st = d.stat()
        print(
            f"  {datetime.fromtimestamp(st.st_mtime).strftime('%Y-%m-%d %H:%M:%S')}"
            f"  {st.st_size / (1024 * 1024):>9,.1f} MB  {d.name}"
        )
    return 0


def build_parser() -> argparse.ArgumentParser:
    p = argparse.ArgumentParser(
        prog="dump.py",
        description="Collect and analyse OpenRA crash dumps.",
        formatter_class=argparse.RawDescriptionHelpFormatter,
    )
    sub = p.add_subparsers(dest="command", required=True)

    a = sub.add_parser("analyze", help="run SOS commands against a dump (default: newest)")
    a.add_argument("--path", metavar="DMP", help="dump to analyse (default: newest in .pyddock/tmp/dumps)")
    # dest must not be "command": that is the subparser's own dest, and argparse
    # would overwrite the subcommand name with this option's value.
    a.add_argument("--command", action="append", default=[], metavar="SOS", dest="commands",
                   help=f"SOS command, repeatable. Default: {'; '.join(DEFAULT_COMMANDS)}")
    a.add_argument("--thread", type=int, metavar="N",
                   help="switch to this thread index before running the commands")
    a.add_argument("--lines", type=int, default=DEFAULT_LINES, metavar="N",
                   help=f"max lines printed (default: {DEFAULT_LINES}); the full "
                        "transcript always goes to the log")
    a.add_argument("--timeout", type=float, default=300.0, metavar="SEC",
                   help="give up after this long (default: 300)")

    c = sub.add_parser("collect", help="snapshot the running game into a dump")
    c.add_argument("--pid", type=int, help="process to dump (default: the tracked game)")
    c.add_argument("--type", choices=["Full", "Heap", "Mini", "Triage"], default="Heap",
                   help="dump type (default: Heap - Full is several GB for OpenRA)")
    c.add_argument("--output", metavar="DMP", help="where to write it")
    c.add_argument("--timeout", type=float, default=300.0, metavar="SEC",
                   help="give up after this long (default: 300)")

    sub.add_parser("list", help="list collected dumps")
    return p


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    try:
        if args.command == "analyze":
            return cmd_analyze(args)
        if args.command == "collect":
            return cmd_collect(args)
        return cmd_list()
    except game.ToolError as ex:
        print(f"ERROR: {ex}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
