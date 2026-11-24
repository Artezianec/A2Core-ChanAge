# ExceedEnchant (Amplify ◆) export

Server data: `tools/data/exceed_enchant.json` (embedded in `Aion.World`).

## Client tables

| Table | Role |
|-------|------|
| `Item.dat` `ExceedEnchantGroup` / `EnchantEffectGroup` | Item → exceed + stat group |
| `ExceedEnchant.dat` | Per-group ◆ steps: success / pity / kina / stone costs |
| `AdditionalStat.dat` | Per-group ◆ stat bonuses |

WorldProbe selection (local only, not committed): `AION2/Content/Data/Table/ExceedEnchant.dat`, `AdditionalStat.dat` → `research/local/exceed-enchant-<date>/tables/`.

## Checked-in snapshot (no local dat)

When `research/local/exceed-enchant-*/tables/` is absent:

- **678** enchantable items mapped from `research/client-export/2026-11-22/Item.json.gz`.
- **121** exceed groups; step ladders are **CANDIDATE** copies of the pinned Global ◆0..◆5 ladder (same odds as item `110120003` before this work).
- Stat rows are **CANDIDATE** scaled from `effect_reference` (prior server behaviour).

Replace CANDIDATE sections by exporting dat tables and running:

```bash
python tools/exceed_enchant_data.py
dotnet test tests/Aion.World.Tests --filter EnchantAmplify
```

## Server

`ExceedEnchantTables` resolves `items` → `groups` / `group_caps` / `effects`. Legacy top-level `steps` and `max_surpass_by_item_level` remain as fallbacks for missing mappings.

Wire: `ItemExceedEnchant_RQ` `0x5624` / `RS` `0x5625`.