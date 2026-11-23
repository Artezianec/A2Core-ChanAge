# Client table export (2026-11-22)

Selected client data tables extracted from the pinned **2.0.9.0** client (Paks dated 2026-11-12).

## Source and caveat

- Client build: 2.0.9.0
- Mapping file: `AION2-5.3.2.0-2.0.6.0.usmap` (usmap **2.0.6.0**)
- Extractor: `tools/WorldProbe`

The mappings are from usmap 2.0.6.0 applied to a 2.0.9.0 client. Fields added after 2.0.6.0 may be mis-decoded. Treat newly introduced columns and unexpected values with care, and confirm against a newer usmap when one is available.

Each table is stored as gzip-compressed JSON (`<Table>.json.gz`) and kept under GitHub's 100 MB file size limit. The original export document shape is preserved: `Version`, `Ids`, and `Properties` (row data under `Properties.Data`).

## Tables

| Table | Rows | File | Source bytes | Gzip bytes |
| --- | ---: | --- | ---: | ---: |
| AbyssRiftPhase | 8 | `AbyssRiftPhase.json.gz` | 5,416 | 606 |
| AbyssRiftPhaseSpawnerGroup | 64 | `AbyssRiftPhaseSpawnerGroup.json.gz` | 9,540 | 647 |
| BMShopCategory | 93 | `BMShopCategory.json.gz` | 75,000 | 2,373 |
| BMShopGoods | 236 | `BMShopGoods.json.gz` | 354,921 | 12,002 |
| BossChallengeCategory | 7 | `BossChallengeCategory.json.gz` | 1,892 | 341 |
| BossChallengeContents | 260 | `BossChallengeContents.json.gz` | 134,605 | 5,772 |
| BossChallengeGrade | 12 | `BossChallengeGrade.json.gz` | 1,780 | 265 |
| BossChallengeGroup | 26 | `BossChallengeGroup.json.gz` | 20,713 | 1,735 |
| BossChallengeGroupSet | 68 | `BossChallengeGroupSet.json.gz` | 19,800 | 1,069 |
| BossChallengeReward | 1,742 | `BossChallengeReward.json.gz` | 707,451 | 15,359 |
| GameShop | 61 | `GameShop.json.gz` | 35,630 | 1,929 |
| GameShopGoods | 1,011 | `GameShopGoods.json.gz` | 998,337 | 27,109 |
| GameShopGoodsList | 120 | `GameShopGoodsList.json.gz` | 83,417 | 4,370 |
| GameShopUnitButton | 4 | `GameShopUnitButton.json.gz` | 810 | 250 |
| GimmickGauge | 3 | `GimmickGauge.json.gz` | 1,205 | 329 |
| Item | 9,379 | `Item.json.gz` | 46,507,409 | 783,292 |
| ItemSet | 10 | `ItemSet.json.gz` | 4,814 | 484 |
| ItemSetEffect | 4 | `ItemSetEffect.json.gz` | 900 | 267 |
| MonsterPartsData | 4 | `MonsterPartsData.json.gz` | 1,787 | 358 |
| NpcAggroData | 56 | `NpcAggroData.json.gz` | 39,862 | 1,458 |
| NpcBehaviorCase | 2,519 | `NpcBehaviorCase.json.gz` | 816,942 | 30,219 |
| NpcData | 13,518 | `NpcData.json.gz` | 46,080,407 | 834,836 |
| PackageList | 17 | `PackageList.json.gz` | 10,459 | 859 |
| RandomShop | 6 | `RandomShop.json.gz` | 2,889 | 484 |
| RandomShopGoods | 4,404 | `RandomShopGoods.json.gz` | 1,607,665 | 33,007 |
| RandomShopRefreshCost | 10 | `RandomShopRefreshCost.json.gz` | 1,562 | 285 |
| RewardPackage | 60,683 | `RewardPackage.json.gz` | 33,257,710 | 390,369 |
| RewardPackageItem | 44,826 | `RewardPackageItem.json.gz` | 17,322,116 | 425,908 |
| Skill | 16,282 | `Skill.json.gz` | 103,342,577 | 1,165,176 |
| SkillAbnormal | 6,707 | `SkillAbnormal.json.gz` | 10,413,151 | 150,814 |
| SkillAbnormalEffect | 12,789 | `SkillAbnormalEffect.json.gz` | 5,757,288 | 149,188 |
| SkillAbnormalEffectLv | 16,751 | `SkillAbnormalEffectLv.json.gz` | 6,521,269 | 170,469 |
| SkillAbnormalEffectType | 61 | `SkillAbnormalEffectType.json.gz` | 44,700 | 1,761 |
| SkillAbnormalProperty | 3 | `SkillAbnormalProperty.json.gz` | 1,260 | 342 |
| SkillAcquireData | 4,383 | `SkillAcquireData.json.gz` | 2,751,117 | 37,103 |
| SkillAutoUse | 23 | `SkillAutoUse.json.gz` | 10,614 | 645 |
| SkillCharge | 211 | `SkillCharge.json.gz` | 181,396 | 3,730 |
| SkillCond | 110 | `SkillCond.json.gz` | 189,416 | 6,772 |
| SkillControlSetting | 10 | `SkillControlSetting.json.gz` | 2,156 | 363 |
| SkillEffect | 31,630 | `SkillEffect.json.gz` | 38,254,273 | 636,947 |
| SkillEffectFilter | 10,339 | `SkillEffectFilter.json.gz` | 13,232,560 | 151,668 |
| SkillEffectFilterLocation | 328 | `SkillEffectFilterLocation.json.gz` | 131,474 | 3,679 |
| SkillEffectLv | 78,564 | `SkillEffectLv.json.gz` | 50,126,170 | 1,246,865 |
| SkillLink | 131 | `SkillLink.json.gz` | 131,413 | 2,165 |
| SkillList | 77 | `SkillList.json.gz` | 24,141 | 1,674 |
| SkillLv | 1,204 | `SkillLv.json.gz` | 641,488 | 12,706 |
| SkillProjectile | 2,468 | `SkillProjectile.json.gz` | 4,137,811 | 66,210 |
| SkillTransform | 278 | `SkillTransform.json.gz` | 84,321 | 2,785 |
| Skin | 1,545 | `Skin.json.gz` | 1,821,450 | 60,846 |
| SkinSet | 125 | `SkinSet.json.gz` | 142,127 | 6,163 |
| VehicleLevel | 3 | `VehicleLevel.json.gz` | 442 | 209 |
| VehicleList | 211 | `VehicleList.json.gz` | 357,455 | 9,789 |
| VehicleStat | 15 | `VehicleStat.json.gz` | 4,975 | 501 |
| WaveData | 152 | `WaveData.json.gz` | 35,124 | 1,520 |
| WaveStepData | 490 | `WaveStepData.json.gz` | 122,807 | 5,726 |

Total: 55 tables, 386,568,014 bytes uncompressed, 6,471,798 bytes gzip-compressed.

## Loading a table

From the repository root:

```python
from tools.client_export import load_table, load_rows

doc = load_table("Skill")          # full document: Version, Ids, Properties
rows = load_rows("Skill")          # Properties.Data rows
```

Or:

```bash
python tools/client_export.py Skill
python tools/client_export.py --list
```
