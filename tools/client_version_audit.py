#!/usr/bin/env python3
"""Compare the server's hard-coded packet opcodes with a client dispatch table.

The server's constants were derived from the client usmap registration order
(dev-only packet table), which no longer matches the live dispatch opcodes of
AION 2 2.0.12.  This tool resolves every `const ushort|int Name = 0x....`
expression in server/**/*.cs (including `Base + N` forms), matches the constant
name against the dispatch table, and reports the ones that moved.

Usage:
    python tools/client_version_audit.py --opcodes <codec_spec/opcodes.ts>
    python tools/client_version_audit.py --opcodes ... --format csv

The dispatch table is external reference data (projectlumiel:
`codec_spec/opcodes.ts`).  It is never copied
into the repository; pass its path at run time.
"""
from __future__ import annotations

import argparse
import csv
import glob
import io
import os
import re
import sys
from collections import defaultdict

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SUFFIX_RE = re.compile(r"_(RQ|RS|NT)$")
CAMEL_SUFFIX_RE = re.compile(r"(Rq|Rs|Nt)$")


def base_and_suffix(name: str):
    """(normalized base, wire suffix) for a C# constant or dispatch entry."""
    name = re.sub(r"_v\d+$", "", name)
    name = name.replace("GameServer_", "").replace("LoginServer_", "")
    suffix = ""
    match = SUFFIX_RE.search(name)
    if match:
        suffix = match.group(1)
        name = name[: match.start()]
    else:
        match = CAMEL_SUFFIX_RE.search(name)
        if match:
            suffix = {"Rq": "RQ", "Rs": "RS", "Nt": "NT"}[match.group(1)]
            name = name[: match.start()]
    return re.sub(r"[^a-z0-9]", "", name.lower()), suffix


def load_opcodes(path: str):
    text = open(path, encoding="utf-8").read()
    by_key = {}
    by_base = defaultdict(set)
    by_value = defaultdict(list)
    for match in re.finditer(r"^\s*(\w+):\s*(0x[0-9A-Fa-f]{4})\s*,", text, re.M):
        name, value = match.group(1), int(match.group(2), 16)
        base, suffix = base_and_suffix(name)
        by_key[(base, suffix)] = value
        by_base[base].add(value)
        by_value[value].append(name)
    return by_key, by_base, by_value


def load_constants(server: str):
    exprs, where = {}, {}
    for path in glob.glob(os.path.join(server, "**", "*.cs"), recursive=True):
        source = open(path, encoding="utf-8", errors="ignore").read()
        for block in re.finditer(r"const\s+(?:ushort|int|short)\s+([^;]+);", source):
            for name, expr in re.findall(r"(\w+)\s*=\s*([^,;]+)", block.group(1)):
                exprs[name] = expr.strip()
                where[name] = os.path.relpath(path, server)
    values: dict[str, int] = {}

    def resolve(name: str, depth: int = 0):
        if name in values:
            return values[name]
        if depth > 8 or name not in exprs:
            return None
        expr = exprs[name]
        if re.fullmatch(r"0x[0-9A-Fa-f]+|\d+", expr):
            value = int(expr, 16) if expr.startswith("0x") else int(expr)
        else:
            match = re.fullmatch(r"(\w+)\s*([+-])\s*(0x[0-9A-Fa-f]+|\d+)", expr)
            if not match:
                return None
            base = resolve(match.group(1), depth + 1)
            if base is None:
                return None
            delta = int(match.group(3), 16) if match.group(3).startswith("0x") else int(match.group(3))
            value = base + delta if match.group(2) == "+" else base - delta
        values[name] = value
        return value

    for name in exprs:
        resolve(name)
    return values, where


def inbound_cases(server: str, values: dict[str, int]):
    cases = []
    files = ["Aion.World/GameSession.cs", "Aion.World/GameSession.PartyLoot.cs",
             "Aion.Host/Native/NativeListeners.cs"]
    for rel in files:
        path = os.path.join(server, rel)
        if not os.path.isfile(path):
            continue
        source = open(path, encoding="utf-8", errors="ignore").read()
        for match in re.finditer(r"case\s+([A-Za-z_][\w.]*|0x[0-9A-Fa-f]{4})\s*(?:when|:)", source):
            token = match.group(1)
            if token.startswith("0x"):
                cases.append((int(token, 16), token, rel))
            else:
                name = token.split(".")[-1]
                if name in values:
                    cases.append((values[name], name, rel))
    return cases


def reference_for(by_key, by_base, name):
    base, suffix = base_and_suffix(name)
    if (base, suffix) in by_key:
        return by_key[(base, suffix)]
    return by_key.get((base, ""))


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--opcodes", required=True, help="client dispatch table (opcodes.ts)")
    parser.add_argument("--server", default=os.path.join(ROOT, "server"), help="server source root")
    parser.add_argument("--format", choices=("md", "csv"), default="md")
    args = parser.parse_args()

    by_key, by_base, by_value = load_opcodes(args.opcodes)
    values, where = load_constants(args.server)

    moved = []
    for name, value in values.items():
        if not 0x3600 <= value <= 0xFFFF:
            continue
        reference = reference_for(by_key, by_base, name)
        if reference is None or reference == value:
            continue
        moved.append((value, reference, name, where.get(name, "")))
    moved.sort()

    stale_values = {old for old, _, _, _ in moved}
    inbound = sorted({(value, name, rel) for value, name, rel in inbound_cases(args.server, values)
                      if 0x3600 <= value <= 0xFFFF and value not in by_value})

    if args.format == "csv":
        out = io.StringIO()
        writer = csv.writer(out, lineterminator="\n")
        writer.writerow(["old_opcode", "new_opcode", "name", "file"])
        for old, new, name, path in moved:
            writer.writerow([f"0x{old:04X}", f"0x{new:04X}", name, path])
        sys.stdout.write(out.getvalue())
        return 0

    print("# Client opcode audit (server constants vs dispatch table)\n")
    print(f"- dispatch table: `{args.opcodes}`")
    print(f"- server root: `{args.server}`")
    print(f"- resolved constants: {len(values)}")
    print(f"- constants that name a packet at a different opcode: **{len(moved)}**")
    print(f"- inbound cases whose old opcode is absent from the dispatch table: **{len(inbound)}**\n")
    print("| old | 2.0.12 | constant | file |")
    print("|---|---|---|---|")
    for old, new, name, path in moved:
        print(f"| 0x{old:04X} | 0x{new:04X} | `{name}` | `{path}` |")
    if inbound:
        print("\n## Inbound cases not present in the dispatch table\n")
        print("| opcode | constant | file |")
        print("|---|---|---|")
        for value, name, rel in inbound:
            print(f"| 0x{value:04X} | `{name}` | `{rel}` |")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
