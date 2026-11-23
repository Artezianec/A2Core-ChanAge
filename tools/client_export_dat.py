"""Shared helpers for building tools/data from research/client-export/."""
from __future__ import annotations

from pathlib import Path
from typing import Any

try:
    from .client_export import DEFAULT_EXPORT, load_rows
except ImportError:
    from client_export import DEFAULT_EXPORT, load_rows


def short(value: Any) -> str:
    return str(value).split("::")[-1] if value is not None else ""


def cell_id(value: Any) -> int:
    if isinstance(value, dict) and "Value" in value:
        return int(value["Value"])
    return int(value)


def item_name_to_id(directory: str | Path | None = None) -> dict[str, int]:
    return {row["Name"]: cell_id(row["ID"]) for row in load_rows("Item", directory)}


def export_version(table: str, directory: str | Path | None = None) -> str | int | None:
    try:
        from .client_export import load_table
    except ImportError:
        from client_export import load_table

    return load_table(table, directory).get("Version")


def default_export_dir() -> Path:
    return DEFAULT_EXPORT


def export_available(directory: str | Path | None = None) -> bool:
    d = Path(directory) if directory is not None else DEFAULT_EXPORT
    return (d / "Item.json.gz").is_file()