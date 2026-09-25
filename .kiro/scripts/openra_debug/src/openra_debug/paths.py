"""Well-known paths for the OpenRA debug tooling.

Runtime artifacts all live under ``.pyddock/tmp`` (already git-ignored), so
nothing extra needs excluding from source control.

The repo root is resolved from this file's location. Under a normal
``pip install -e`` the package lives at
``<repo>/.kiro/scripts/openra_debug/src/openra_debug/paths.py`` (six parents up
to the repo root). We do not rely solely on that arithmetic though: we walk
upward looking for a directory that has both ``.kiro`` and ``bin``, which keeps
working if the package is ever relocated or imported from an installed copy.
"""

from __future__ import annotations

from pathlib import Path


def _find_repo_root() -> Path:
    here = Path(__file__).resolve()
    for parent in here.parents:
        if (parent / ".kiro").is_dir() and (parent / "OpenRA.slnx").is_file():
            return parent
    # Fall back to the layout-based guess: src/openra_debug/paths.py ->
    # openra_debug -> src -> openra_debug(pkg root) -> scripts -> .kiro -> repo.
    return here.parents[5]


REPO_ROOT = _find_repo_root()

TMP_ROOT = REPO_ROOT / ".pyddock" / "tmp"
RUN_DIR = TMP_ROOT / "run"
LOG_DIR = TMP_ROOT / "logs"
DUMP_DIR = TMP_ROOT / "dumps"

# Written by .kiro/scripts/build/build-and-run.ps1.
GAME_STATE = RUN_DIR / "game.json"
# Written by a live debug session while it holds netcoredbg; read by
# status-game.ps1 so a human can see a probe is in flight.
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
