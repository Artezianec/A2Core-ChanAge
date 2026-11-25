#!/usr/bin/env python3
"""Build launcher/supported-build.json from the installed Steam copy (no Steam login).

The official build tool signs in to Steam to read the depot manifest.  This local
fallback reads the installed app manifest (build id + depot manifest id) and hashes the
installed files, so the launcher can verify/repair and name the build without a Steam
Guard QR.  Quick files get SHA-256 (the launcher's fingerprint and every launch check);
the remaining files get SHA-1 for the full verify/repair path.

Usage:
    python tools/local_supported_build.py
    python tools/local_supported_build.py --sha1 none   # faster, sizes only for non-quick

The game folder is derived from the appmanifest (installdir under steamapps/common).
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DEFAULT_ACF = Path(r"C:\Program Files (x86)\Steam\steamapps\appmanifest_3393110.acf")
EXECUTABLE = "Aion2/Binaries/Win64/AION2.exe"
APP_ID = 3393110
DEPOT_ID = 3393111
CHUNK = 4 << 20

# Files the installer/runtime writes into the install folder; they are not in the depot.
EXCLUDED_PREFIXES = (
    "Aion2/Saved/",
    "Aion2/Binaries/Win64/logs/",
)
EXCLUDED_SUFFIXES = (".log", ".dmp")
EXCLUDED_EXACT = ("Aion2/InstallLog.txt",)


def parse_acf(path: Path):
    text = path.read_text(encoding="utf-8", errors="ignore")
    build = re.search(r'"buildid"\s+"(\d+)"', text)
    depot = re.search(r'"%d"\s*\{\s*"manifest"\s+"(\d+)"' % DEPOT_ID, text, re.S)
    installdir = re.search(r'"installdir"\s+"([^"]+)"', text)
    if not (build and depot and installdir):
        raise SystemExit(f"Could not read buildid/manifest/installdir from {path}")
    return build.group(1), depot.group(1), installdir.group(1)


def included(rel: str) -> bool:
    low = rel.lower()
    if low in (p.lower() for p in EXCLUDED_EXACT):
        return False
    if any(low.startswith(p.lower()) for p in EXCLUDED_PREFIXES):
        return False
    return not low.endswith(EXCLUDED_SUFFIXES)


def is_quick(rel: str) -> bool:
    low = rel.replace("\\", "/").lower()
    if low == EXECUTABLE.replace("\\", "/").lower():
        return True
    return low.startswith("aion2/content/paks/") and (low.endswith(".utoc") or low.endswith(".sig"))


def hash_file(path: Path, want_sha1: bool, want_sha256: bool) -> tuple[str | None, str | None]:
    sha1 = hashlib.sha1() if want_sha1 else None
    sha256 = hashlib.sha256() if want_sha256 else None
    with path.open("rb") as handle:
        while True:
            chunk = handle.read(CHUNK)
            if not chunk:
                break
            if sha1:
                sha1.update(chunk)
            if sha256:
                sha256.update(chunk)
    return (sha1.hexdigest() if sha1 else None, sha256.hexdigest() if sha256 else None)


def fingerprint(files) -> str:
    digest = hashlib.sha256()
    for entry in sorted((f for f in files if f["quick"]), key=lambda f: f["path"].lower()):
        line = f"{entry['path'].replace(chr(92), '/').lower()}\0{entry['size']}\0{entry['sha256'].lower()}\n"
        digest.update(line.encode("utf-8"))
    return digest.hexdigest()


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--acf", type=Path, default=DEFAULT_ACF)
    parser.add_argument("--game-root", type=Path, help="Override steamapps/common/<installdir>")
    parser.add_argument("--out", type=Path, default=ROOT / "launcher/supported-build.json")
    parser.add_argument("--sha1", choices=("all", "none"), default="all",
                        help="Hash non-quick files for the full verify path (all, default) or only size them")
    parser.add_argument("--dry-run", action="store_true")
    args = parser.parse_args()

    if not args.acf.is_file():
        raise SystemExit(f"App manifest not found: {args.acf}")
    build_id, manifest_id, installdir = parse_acf(args.acf)
    game_root = args.game_root or (args.acf.parent / "common" / installdir)
    if not (game_root / EXECUTABLE).is_file():
        raise SystemExit(f"Game executable not found under {game_root}")

    print(f"build {build_id}, depot manifest {manifest_id}, root {game_root}", flush=True)
    paths = []
    for dirpath, _dirs, names in os.walk(game_root):
        for name in names:
            full = Path(dirpath) / name
            rel = full.relative_to(game_root).as_posix()
            if included(rel):
                paths.append((rel, full))
    paths.sort(key=lambda item: item[0].lower())

    files = []
    total = 0
    done_bytes = 0
    total_bytes = sum(full.stat().st_size for _, full in paths)
    for index, (rel, full) in enumerate(paths, 1):
        size = full.stat().st_size
        total += size
        quick = is_quick(rel)
        sha1 = sha256 = None
        if quick or args.sha1 == "all":
            sha1, sha256 = hash_file(full, want_sha1=not quick and args.sha1 == "all", want_sha256=quick)
        entry = {"path": rel, "size": size, "quick": quick}
        if sha256:
            entry["sha256"] = sha256
        elif sha1:
            entry["sha1"] = sha1
        files.append(entry)
        done_bytes += size
        if index % 50 == 0 or index == len(paths):
            print(f"  [{index}/{len(paths)}] {done_bytes / (1 << 30):.1f}/{total_bytes / (1 << 30):.1f} GiB", flush=True)

    doc = {
        "appId": APP_ID,
        "depotId": DEPOT_ID,
        "manifestId": manifest_id,
        "executable": EXECUTABLE,
        "fingerprint": fingerprint(files),
        "fileCount": len(files),
        "totalBytes": total,
        "files": files,
        "steamBuildId": build_id,
    }
    text = json.dumps(doc, indent=2, ensure_ascii=False) + "\n"
    if args.dry_run:
        print(text[:400])
        print(f"dry-run: {len(files)} files, {total} bytes, fingerprint {doc['fingerprint'][:16]}")
        return 0
    args.out.parent.mkdir(parents=True, exist_ok=True)
    if args.out.exists():
        backup = args.out.with_suffix(args.out.suffix + ".bak")
        backup.write_bytes(args.out.read_bytes())
        print(f"backup: {backup}")
    args.out.write_text(text, encoding="utf-8", newline="\n")
    print(f"wrote {args.out}: {len(files)} files, {total} bytes, fingerprint {doc['fingerprint'][:16]}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
