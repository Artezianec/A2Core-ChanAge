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

## Not ported (needs a client-version decision)

- 2.0.12 opcode families and layouts (`B01`-`B09` in the audit).
- 2.0.13 greeting/entry.
- Mixed changes: bomb Ready/Start, sprint/dash, warehouse container semantics, soul-bind attune.
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
