# Middle Abyss (map 21) — data and exporter notes

**Status (emulator):** Map 21 is registered in `map-points.json` (geometry base map **20**). Kill objectives for duty scrolls **UQ1309037–UQ2309089** use NpcData **2600101–2600148** from `research/client-export/2026-11-22`. **Spawn positions on map 21 remain CANDIDATE** until MapData for `MapId` 21 is imported.

## Regenerate NPC templates and supplement spawns

```bash
python3 tools/quest_supplement.py build
python3 tools/quest_supplement.py apply
python3 tools/audit_quests.py
```

NpcData templates come from the client export (`tools/abyss_middle_data.py`); only ids missing from export still use donor clones in `quest_supplement.py`.

## Import real Middle Abyss spawns (when MapData is available)

1. Run WorldProbe on the client paks with selection for Reshanta Middle `MapData.dat` (MapId **21**). Store output under `research/local/` (not committed).
2. Merge spawns:

```bash
python3 tools/world_npcs.py import-map --map 21 research/local/<probe-dir>/table-000.json
```

3. Re-run `tools/audit_quests.py` and Middle Abyss parity tests.

## Still deferred / CANDIDATE after this PR

| Item | Reason |
| --- | --- |
| Map 21 spawn coordinates | No `MapData` map 21 in repo; supplement grid near Reshanta WSA teleports (CANDIDATE) |
| Festival DQ1202072* / DQ2202072* | Client QuestString: event no longer valid; emulator forces field spawns for dev testing (CANDIDATE positions) |
| Duty scroll **live accept** via item use | Harness uses `QuestAdd`; production path per game-systems gap audit P5 |
| HQ1102070 / 2970018 field talk | Instance map-event spawn only (`map-events.json`) |

See `docs/quests/AUDIT_DEFERRED.md` and `tests/Aion.World.Tests/QuestDeferredParityTests.cs` (66 quest codes).