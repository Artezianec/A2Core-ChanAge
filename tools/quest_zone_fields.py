"""Merge Quest.dat zone fields (subzone_group, exploration_type) into tools/data/quests.json without a full rebuild.

    python tools/quest_zone_fields.py research/local/quest-tables-<date>/table-000.json

The argument is the WorldProbe export of AION2/Content/Data/Table/Quest.dat. `python tools/quests.py build` writes the same
fields; this script only patches an existing quests.json when the other inputs (rewards, NPCs, localization) are not at hand.
"""
import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from quests import _zone_fields  # noqa: E402

DATA = Path(__file__).resolve().parent / 'data' / 'quests.json'


def main(quest_table: str) -> None:
    rows = json.loads(Path(quest_table).read_text(encoding='utf-8'))['Properties']['Data']
    by_id = {r['ID']['Value']: r for r in rows}
    data = json.loads(DATA.read_text(encoding='utf-8'))
    counts = {'subzone_group': 0, 'exploration_type': 0, 'part': 0}
    for code, quest in data['quests'].items():
        row = by_id.get(quest['id'])
        if row is None:
            continue
        quest.pop('subzone_group', None)
        quest.pop('exploration_type', None)
        fields = _zone_fields(row)
        # Keep key order stable: zone fields right after 'part'.
        rebuilt = {}
        for key, value in quest.items():
            rebuilt[key] = value
            if key == 'part':
                rebuilt.update(fields)
        data['quests'][code] = rebuilt
        for key in counts:
            if rebuilt.get(key):
                counts[key] += 1
    DATA.write_text(json.dumps(data, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    print(counts)


if __name__ == '__main__':
    main(sys.argv[1])
