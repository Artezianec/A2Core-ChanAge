# Reference porting status

Branch: `port/shared-gameplay-fixes` (from `main` at `a8c19e6`).

This branch ports the Reference work that is independent of the client wire, plus the
client-format/tooling pieces that are safe to carry on every build. Protocol changes
and the mixed wire/behavior changes are intentionally not on this branch yet.

## Ported

| Area | Change | Main files |
|---|---|---|
| Combat | Fall damage no longer enters combat | `GameSession.Fall.cs`, `GameSession.cs` |
| Loot | Ground drops expire after 120 s | `GameSession.Loot.cs` |
| Skills | `Teleport_Caster` rows drive the destination (Leaping Slam target-side, Shadowstrike target-back) | `SkillMoves.cs`, `GameSession.SkillMoves.cs` |
| Skills | Slow from negative `StatChange MoveSpeed`; `KnockdownInstant` displacement | `AbnormalSpecs.cs` |
| Skills | `Dispel_True [DeBuff]` breaks control; scope falls back to the skill target | `SkillEffects.cs` |
| Map events | Standing volume re-check, exact script NPCs, QA required-before-optional | `MapEvents.cs`, `GameSession.MapEvents.cs`, `QaMapEventRunner.cs` |
| Login | Zero/non-finite saved positions fall back to the map start and are never checkpointed | `GameSession.cs` |
| Rides | Default ride is persisted after insertion and announced with `0x900c`; login announces it too | `GameSession.Rides.cs`, `GameSession.cs` |
| Attendance | Stored and new attendance rows carry a complete time | `GameSession.Achievements.cs` |
| Bomb | Plant range is the skill's 30 m range and every remaining tower is a candidate | `GameSession.EnvObjBomb.cs` |
| Shop | Duplicate personal/server and per-store products are folded (257 -> 201 goods) | `tools/shop_served_catalog.py`, generated catalog |
| Client format | CUE4Parse reads 2.0.11+ versioned key manifests and header permutations | `third_party/CUE4Parse/.../Aion2*` |
| Platform | `GET warehouse/v2.0/me/items` returns an empty paged 200 | `PlatformApiGateResponses.cs` |
| Host | Launch pairing is shared through `platform-pairing.json`; launcher launches beat the fixture token | `PlatformBootstrapEndpoints.cs`, `Program.cs` |
| Launcher | APC-to-remote-thread injection fallback, NCGuard hosts-block coexistence, Steam `-applaunch` | `DnsInject.cs`, `SystemFiles.cs`, `Workflow.cs` |
| Tooling | 2.0.12 opcode audit, local supported-build generator, patch pipeline, NCGuard CA helper | `tools/` |
| Diagnostics | Opt-in native flow journal (`AION_FLOW_LOG=1`) | `FlowLog.cs`, `NativeListeners.cs` |

## Protocol (2.0.9 / 2.0.12 / 2.0.13)

The server keeps its 2.0.9 handlers and selects a wire version per native connection from the
game greeting:

- **2.0.9** (`0x3610`) keeps the original path.
- **2.0.12** (`0x5E58`, reply `0xCB02`) uses the 2.0.12 greeting, world login (`0xD456`),
  select (`0x3C4F` / `0xA8F9`), load (`0xB24E` / `0x4EF8`), lobby customizing
  (`0x569D`..`0x56AA`, extended slot `0x910A`), create result `u16`, warp-leave flags,
  appearance mask/point block, respawn/quest bodies, dodge flags, item-store widths and
  ride success results.
- **2.0.13** (`0x16FE`, reply `0x83A9`) negotiates, logs in with the 2.0.12 opcode map and
  has an **experimental** entry path: `0x83A9`/`0xC4F6` select, compressed `0x2053`,
  `0x6AF4` load complete, `0xd79e` enter-map position, the `0x2AE9815F` `MyCharAppear`
  body with the 17 entry abnormals and no `0x3623`. Still pending a live 2.0.13 run.

Renumbered 2.0.12 families are translated at the connection edges
(`ProtocolTranslation`), so the 2.0.9 handlers stay unchanged: party loot, trade, dungeon
rooms, arena, duel, airspace, godstone, monolith, force, abyss, achievements and the
single moves. `0x382A`/`0x382C` stay untouched because they are the abnormal add/remove
packets.

## Modern-only behavior (2.0.12 / 2.0.13)

- Bomb `Ready` only enters the ground aim (`0x384c`); `Start` plants and plays the throw.
  2.0.9 keeps the single Ready = plant path.
- Sprint defaults to variant 11: one reply batch and one re-assert of the client's opening
  roll. 2.0.9 keeps variant 0.
- Warehouses use the 2.0.12 container ids: 15 is the per-character warehouse and 2 the account
  one (`ContainerToLocation` / `ContainerOf`); the list (`0x5611`), move (`0x563a`), sort and
  store/take-out (`0x5642` / `0x5644`) share the mapping. 2.0.9 keeps 2/3.
- Soul-bind attune (`0x5631` -> `0x5632`) replaces the selected stat with a different pool stat
  and consumes the cost item; the reply layout is the client's own reader (no capture yet).

## Not ported

- The 2.0.13 deferred entry bursts are emitted and staged (`0x61A0`, `0xE25A`, the list and
  spawn queues) but still need a live 2.0.13 run to confirm the frames.
- Launcher 2.0.12 build pin: the Target still pins 2.0.9 and offers 2.0.13 only as an
  experimental profile. Add 2.0.12 as a profile instead of replacing the 2.0.9 manifest.

## Known pre-existing failures (also on `main`)

- `RideMountTests.Skill_cast_dismounts` (cast refused with `0x3801` result 1).
- `ServerFileLoggerTests.UniquePath_skips_a_file_the_launcher_tee_already_holds`.
- `ChannelFileLogWriterTests` (two Windows file-lock cases).
- `test_served_goods_have_positive_price_unless_official_free` (zero-price rows already
  present in the committed 2.0.9 catalog).

## Verification

```powershell
dotnet build server/Aion.Host -c Release
dotnet test tests/Aion.Host.Tests/Aion.Host.Tests.csproj -c Debug
dotnet test tests/Aion.Launcher.Core.Tests/Aion.Launcher.Core.Tests.csproj -c Debug
dotnet test tests/Aion.World.Tests/Aion.World.Tests.csproj -c Debug `
  --filter "FullyQualifiedName~FallDamageTests|FullyQualifiedName~AbnormalSpecsFixesTests|FullyQualifiedName~SkillEffectControlTests|FullyQualifiedName~SkillMoveTests|FullyQualifiedName~DropKeyTests"
```
