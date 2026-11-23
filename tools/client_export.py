"""Load a gzip-compressed client export table by name.

Default export: research/client-export/2026-11-22/
Override with CLIENT_EXPORT_DIR or an explicit path argument.
"""
from __future__ import annotations

import argparse
import gzip
import json
import os
import sys
from pathlib import Path
from typing import Any

ROOT = Path(__file__).resolve().parents[1]
DEFAULT_EXPORT = ROOT / "research" / "client-export" / "2026-11-22"


def export_dir(path: str | Path | None = None) -> Path:
    if path is not None:
        return Path(path)
    env = os.environ.get("CLIENT_EXPORT_DIR")
    return Path(env) if env else DEFAULT_EXPORT


def _table_path(name: str, directory: Path) -> Path:
    stem = name[:-5] if name.endswith(".json") else name
    if stem.endswith(".json.gz"):
        stem = stem[: -len(".json.gz")]
    elif stem.endswith(".gz"):
        stem = Path(stem).stem
    direct = directory / f"{stem}.json.gz"
    if direct.is_file():
        return direct
    raise FileNotFoundError(f"client export table not found: {stem} (looked for {direct})")


def load_table(name: str, directory: str | Path | None = None) -> dict[str, Any]:
    """Return the parsed JSON document for a table (Version, Ids, Properties)."""
    path = _table_path(name, export_dir(directory))
    with gzip.open(path, "rt", encoding="utf-8") as fh:
        data = json.load(fh)
    if not isinstance(data, dict):
        raise TypeError(f"{path.name} is not a JSON object")
    return data


def load_rows(name: str, directory: str | Path | None = None) -> Any:
    """Return Properties.Data when present, otherwise the full document."""
    doc = load_table(name, directory)
    props = doc.get("Properties")
    if isinstance(props, dict) and "Data" in props:
        return props["Data"]
    return doc


def list_tables(directory: str | Path | None = None) -> list[dict[str, Any]]:
    """Return manifest table entries for the export directory."""
    manifest_path = export_dir(directory) / "manifest.json"
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    return list(manifest.get("tables", []))


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Load a client export table by name.")
    parser.add_argument("table", nargs="?", help="Table name, for example Skill or Item")
    parser.add_argument("--list", action="store_true", help="List tables in the export")
    parser.add_argument("--dir", dest="directory", help="Export directory (defaults to the 2026-11-22 export)")
    args = parser.parse_args(argv)
    directory = args.directory
    if args.list or not args.table:
        tables = list_tables(directory)
        for entry in tables:
            rows = entry.get("rows", "?")
            print(f"{entry.get('table')}\t{rows}\t{entry.get('file')}")
        if not args.table:
            return 0
    doc = load_table(args.table, directory)
    ids = doc.get("Ids")
    props = doc.get("Properties")
    data = props.get("Data") if isinstance(props, dict) else None
    n_ids = len(ids) if isinstance(ids, list) else "?"
    n_rows = len(data) if isinstance(data, list) else (len(data) if isinstance(data, dict) else "?")
    print(f"table={args.table} version={doc.get('Version')} ids={n_ids} data={n_rows} keys={list(doc)}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
