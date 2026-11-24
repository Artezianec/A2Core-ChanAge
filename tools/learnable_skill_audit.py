"""Regenerate docs/skills-audit.md from the C# LearnableSkillAudit harness.

    python tools/learnable_skill_audit.py
    python tools/learnable_skill_audit.py --check   # exits 1 when report is stale
"""
from __future__ import annotations

import argparse
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
REPORT = ROOT / 'docs' / 'skills-audit.md'
TEST_PROJECT = ROOT / 'tests' / 'Aion.World.Tests' / 'Aion.World.Tests.csproj'
FILTER = 'FullyQualifiedName~LearnableSkillAuditTests.Write_report'


def run_test() -> int:
    return subprocess.call(
        ['dotnet', 'test', str(TEST_PROJECT), '--filter', FILTER, '-v', 'q', '--nologo'],
        cwd=ROOT,
    )


def main(argv: list[str] | None = None) -> int:
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('--check', action='store_true', help='fail when committed report differs after audit')
    args = p.parse_args(argv)
    before = REPORT.read_text(encoding='utf-8') if REPORT.exists() and args.check else None
    code = run_test()
    if code != 0:
        return code
    if args.check and before is not None and REPORT.read_text(encoding='utf-8') != before:
        print('docs/skills-audit.md is out of date (run python tools/learnable_skill_audit.py)', file=sys.stderr)
        return 1
    return 0


if __name__ == '__main__':
    raise SystemExit(main())