"""Stop-signal file used to shut down an elevated NCGuard host.

A non-elevated shell cannot kill that process, so stop-all touches a file the
host polls. The file must not survive into the next start, or the new host
exits immediately and port 443 never opens.
"""
from __future__ import annotations

import time
from pathlib import Path


def clear_stop_file(path: Path | None) -> None:
    """Remove a stop file before a new host starts. Missing is fine."""
    if path is None:
        return
    try:
        path.unlink(missing_ok=True)
    except OSError:
        pass


def _mtime(path: Path) -> float | None:
    try:
        return path.stat().st_mtime
    except OSError:
        return None


def is_stale_stop_file(path: Path, started_at: float) -> bool:
    """True when the file was written before the process started (a leftover)."""
    mtime = _mtime(path)
    return mtime is not None and mtime < started_at


def clear_stale_stop_file(path: Path | None, started_at: float | None = None) -> bool:
    """Delete a leftover stop file written before *started_at* (default: now).

    A file written at or after that instant is left in place as a live stop.
    Returns True when a stale file was removed.
    """
    if path is None:
        return False
    started = time.time() if started_at is None else started_at
    try:
        if not path.exists() or not is_stale_stop_file(path, started):
            return False
        path.unlink()
        return True
    except OSError:
        return False


def is_stop_requested(path: Path | None, started_at: float) -> bool:
    """True when a live (not leftover) stop file is present."""
    if path is None:
        return False
    try:
        return path.exists() and not is_stale_stop_file(path, started_at)
    except OSError:
        return False
