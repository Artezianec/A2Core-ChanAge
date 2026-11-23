"""NpcBehaviorCase builder smoke tests."""
import json
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def test_npc_behavior_cases_build():
    subprocess.run([sys.executable, str(ROOT / "tools/npc_behavior_cases.py")], check=True, cwd=ROOT)
    data = json.loads((ROOT / "tools/data/npc-behavior-cases.json").read_text(encoding="utf-8"))
    assert data["stats"]["behavior_rows"] == 2519
    assert data["stats"]["npc_ids_with_cases"] > 100


def test_boss_coverage_report():
    subprocess.run([sys.executable, str(ROOT / "tools/boss_coverage.py")], check=True, cwd=ROOT)


def test_bosschallenge_export_verify():
    subprocess.run([sys.executable, str(ROOT / "tools/bosschallenge_export_verify.py")], check=True, cwd=ROOT)