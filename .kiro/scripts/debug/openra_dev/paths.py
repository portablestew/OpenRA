"""Well-known paths for the OpenRA agent dev tooling.

Runtime artifacts all live under .pyddock/tmp, which is already git-ignored, so
nothing extra needs excluding from source control.
"""

from __future__ import annotations

from pathlib import Path

# .kiro/scripts/debug/openra_dev/paths.py -> repo root is five levels up.
REPO_ROOT = Path(__file__).resolve().parents[4]

TMP_ROOT = REPO_ROOT / ".pyddock" / "tmp"
RUN_DIR = TMP_ROOT / "run"
LOG_DIR = TMP_ROOT / "logs"
DUMP_DIR = TMP_ROOT / "dumps"

# Written by .kiro/scripts/build/build-and-run.ps1.
GAME_STATE = RUN_DIR / "game.json"
# Written by dbg.py while a probe holds the debugger; read by status-game.ps1.
DEBUG_STATE = RUN_DIR / "debug.json"

GAME_DLL = REPO_ROOT / "bin" / "OpenRA.dll"

# Directories that never contain source we would set a breakpoint in. Skipped
# when resolving a bare filename like "Mobile.cs".
SOURCE_SKIP_DIRS = {
    ".git",
    ".kiro",
    ".pyddock",
    ".vscode",
    "bin",
    "obj",
    "thirdparty",
    "node_modules",
}


def ensure_dirs() -> None:
    for d in (RUN_DIR, LOG_DIR, DUMP_DIR):
        d.mkdir(parents=True, exist_ok=True)


def rel(path: Path | str) -> str:
    """Repo-relative display form, falling back to the absolute path."""
    try:
        return str(Path(path).resolve().relative_to(REPO_ROOT))
    except (ValueError, OSError):
        return str(path)
