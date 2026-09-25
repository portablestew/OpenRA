"""Bounded text rendering of debug results.

Everything here exists to keep an agent's context from being flooded. A
``backtrace all`` on OpenRA lists ~15 threads, and ``print self`` on an Actor is
one very long line; the raw text always goes to the session log, while these
renderers return a trimmed, readable form for ``.text``.
"""

from __future__ import annotations

# A single netcoredbg `print` on a rich object (an Actor, a Mobile trait) is one
# long line. Keep it, but cap absurd lengths so one eval cannot dominate output.
_MAX_LINE = 2000
_MAX_STACK_FRAMES = 40
_MAX_CALLSTACK_LINES = 400


def _clip_line(line: str, limit: int = _MAX_LINE) -> str:
    if len(line) <= limit:
        return line
    return line[:limit] + f" ... [+{len(line) - limit} chars, see session log]"


def render_hit(hit) -> str:
    """One-screen summary of a breakpoint stop."""
    lines = [
        "== Hit " + "=" * 54,
        f"  reason     : {hit.reason}",
    ]
    if hit.breakpoint is not None:
        lines.append(f"  breakpoint : {hit.breakpoint}")
    if hit.thread_id is not None:
        lines.append(f"  thread     : {hit.thread_id}")
    lines.append(f"  at         : {hit.at}")
    if hit.stack_text:
        lines.append("")
        lines.append("== Stack " + "=" * 52)
        lines.append(_indent(_clip_stack(hit.stack_text)))
    return "\n".join(lines)


def render_callstack(text: str) -> str:
    """Trim a backtrace / backtrace-all transcript for display."""
    return _clip_stack(text, max_lines=_MAX_CALLSTACK_LINES)


def _clip_stack(text: str, max_lines: int = _MAX_STACK_FRAMES) -> str:
    kept: list[str] = []
    frame_lines = 0
    dropped = 0
    for raw in text.splitlines():
        line = _clip_line(raw.rstrip())
        if not line.strip():
            continue
        # Count only actual frame lines (#0, #1, ...) against the cap; keep thread
        # headers and blanks so a multi-thread dump stays readable.
        is_frame = line.lstrip().startswith("#")
        if is_frame and frame_lines >= max_lines:
            dropped += 1
            continue
        if is_frame:
            frame_lines += 1
        kept.append(line)
    if dropped:
        kept.append(f"... {dropped} more frame(s) (see session log)")
    return "\n".join(kept)


def _indent(text: str, prefix: str = "  ") -> str:
    return "\n".join(prefix + line for line in text.splitlines())
