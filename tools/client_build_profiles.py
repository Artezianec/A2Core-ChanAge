"""Load tools/data/client-build-profiles.json for WorldProbe and other export tools.

Usage:
  python tools/client_build_profiles.py list
  python tools/client_build_profiles.py key-report 2.0.13
  python tools/client_build_profiles.py dumper-offsets 2.0.13
"""

from __future__ import annotations

import argparse
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
TABLE_PATH = ROOT / "tools/data/client-build-profiles.json"


def load_table() -> dict:
    return json.loads(TABLE_PATH.read_text(encoding="utf-8"))


def normalize_version(version_id: str | None) -> str:
    table = load_table()
    default = table["defaultVersionId"]
    if not version_id:
        return default
    version_id = version_id.strip()
    if version_id in {"2.0.6.0", "steam-latest"}:
        return default
    if version_id in table["profiles"]:
        return version_id
    return default


def profile(version_id: str | None) -> dict:
    table = load_table()
    key = normalize_version(version_id)
    return table["profiles"][key]


def key_report_dict(version_id: str | None) -> dict:
    row = profile(version_id)
    key_hex = row.get("aesPakKeyHex")
    if not key_hex:
        raise SystemExit(
            f"profile {row.get('displayVersion', version_id)} has no aesPakKeyHex; "
            "use research/local/archive-key-validated.json for the pinned build"
        )
    return {
        "client_version": row["displayVersion"],
        "key_hex": key_hex if key_hex.startswith("0x") else f"0x{key_hex}",
        "protocol_profile_id": row.get("protocolProfileId"),
        "protocol_verified": row.get("protocolVerified"),
    }


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="command", required=True)
    sub.add_parser("list")
    key = sub.add_parser("key-report")
    key.add_argument("version", nargs="?", default=None)
    dump = sub.add_parser("dumper-offsets")
    dump.add_argument("version", nargs="?", default=None)
    args = parser.parse_args()

    table = load_table()
    if args.command == "list":
        for pid, row in table["profiles"].items():
            steam = row.get("steamBuildId") or "TODO"
            print(f"{pid}\tsteam_build={steam}\tdefault={table['defaultVersionId'] == pid}")
        return
    if args.command == "key-report":
        print(json.dumps(key_report_dict(args.version), indent=2))
        return
    if args.command == "dumper-offsets":
        row = profile(args.version)
        print(json.dumps(row.get("dumperOffsets") or {}, indent=2))


if __name__ == "__main__":
    main()