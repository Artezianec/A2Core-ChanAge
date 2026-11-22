"""Minimal Postgres fixture so QA bots can create a Gladiator on a local cluster (integration tests)."""
import json
from pathlib import Path

try:
    from postgres_dev import sql, literal, ROOT
except ImportError:
    from tools.postgres_dev import sql, literal, ROOT

BUILD = 'qa-integration'
PROFILE = 'dev-v1-1:1'
STARTER = {
    "worldId": 1204,
    "mapId": 1000,
    "position": {"x": 7259.9, "y": -18223.5, "z": 22404.5},
    "resources": {"hp": 1500, "hpMax": 1500, "mp": 200, "mpMax": 200, "sp": 100000, "spMax": 100000},
    "skills": [301, 1008, 8000],
    "slots": [[0, 301]],
    "items": [{"templateId": 110160001, "quantity": 1, "equipmentSlot": 1}],
    "characterClass": "Gladiator",
}


def main():
    profile = json.loads((ROOT / 'tools/data/starter-skills.json').read_text())['1:1']
    skills = profile['skills'][:12]
    starter = STARTER | {"skills": skills, "slots": profile['slots'][:3]}
    sql(f"""
        BEGIN;
        SET LOCAL ROLE aion_owner;
        INSERT INTO catalog.builds VALUES({literal(BUILD)}, '{{}}', now())
          ON CONFLICT (build_id) DO NOTHING;
        INSERT INTO catalog.items VALUES({literal(BUILD)}, 110160001, 'QA Sword', 'weapon', '{{}}')
          ON CONFLICT DO NOTHING;
        INSERT INTO catalog.skills (build_id, template_id, name, category, definition)
        SELECT {literal(BUILD)}, s, 'skill-'||s::text, 'skill', '{{}}'
        FROM unnest(ARRAY{skills}::int[]) AS s
        ON CONFLICT DO NOTHING;
        INSERT INTO game.equipment_rules VALUES({literal(BUILD)}, 110160001, 1, 1, ARRAY[1], 'qa integration')
          ON CONFLICT (build_id, template_id, slot) DO NOTHING;
        INSERT INTO game.starter_profiles (profile_id, build_id, creation_id, race, gender, enabled, definition)
        VALUES({literal(PROFILE)}, {literal(BUILD)}, 1, 1, 1, true, {literal(json.dumps(starter))})
        ON CONFLICT (profile_id) DO UPDATE SET enabled = true, definition = EXCLUDED.definition;
        COMMIT;
    """)
    print('QA integration starter seed applied')


if __name__ == '__main__':
    main()