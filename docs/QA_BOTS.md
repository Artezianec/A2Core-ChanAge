# QA quest bots

Server-side bots that play the main quest progression on production hosts and record where they get stuck. **On by default** (`Aion:QaBots:Enabled`).

## Configuration

```json
"Aion:QaBots": {
  "Enabled": true,
  "Visible": true,
  "StepTimeoutMs": 120000,
  "PathTimeoutMs": 60000,
  "ActionRingSize": 20,
  "MaxBots": 5,
  "HardMaxBots": 10,
  "PlayerProximityCm": 2500,
  "AutoRun": {
    "Enabled": true,
    "Count": 5,
    "Classes": ["Gladiator", "Templar", "Ranger", "Cleric", "Sorcerer"],
    "LevelRange": "1-45",
    "RestartDelayMs": 5000
  },
  "CpuGuard": {
    "Enabled": true,
    "MaxWorldBusy": 0.85,
    "SlowTickMs": 200
  },
  "Retention": {
    "SamplesPerBucket": 10,
    "SampleDetailIntervalMinutes": 15,
    "RunRetentionDays": 90,
    "SampleRetentionDays": 30
  }
}
```

| Setting | Meaning |
|---------|---------|
| `Enabled` | Master switch; when false, no services or `//qa` commands. |
| `Visible` | **Default true**: QA bots are normal visible characters in the world. Set false to apply GM invisibility (`SetInvisible`). |
| `StepTimeoutMs` / `PathTimeoutMs` | Stuck detection thresholds. |
| `MaxBots` / `HardMaxBots` | Pool size caps (auto-run and `//qa run`). |
| `PlayerProximityCm` | Do not attack quest mobs when a real player is this close. |
| `AutoRun` | Continuous production pool when the game world is ready. |

Requires a game port and (for persistence) the gameplay database.

## GM commands (staff only)

| Command | Description |
|---------|-------------|
| `//qa run <class> <from>-<to> [count]` | Manual batch (e.g. `//qa run Gladiator 1-15 2`). |
| `//qa status` | Active run counters. |
| `//qa stop` | Stop the current run / pause auto pool. |

## Player visibility and safety

- **Visible by default**: QA bots use normal world presence for other players (movement, appearance, combat in range) unless `Visible` is false.
- **Public `online` count**: active QA bots in the world are included in `GET /api/status` `online` (alongside real players). Playerbots and market bots are excluded. The response also includes `onlineBots` (QA-only subset). QA bots still do not call `WorldPresence.Enter` (staff presence / internal connection metrics stay player-only).
- **No kill stealing**: quest brain skips mobs engaged by real players and avoids attacking when players are within `PlayerProximityCm`.
- **No loot stealing**: QA bots may only pick up ground drops tagged as their own killer loot (no party/force shares).
- **No parties** with real players; **no season ranking** point flush; characters can be flagged `qa_bot` on internal accounts (migration 134).
- Internal QA accounts are excluded from public season leaderboard queries.

## Architecture

- `QaBots` — runner (50 ms tick, CPU throttle), auto-run pool, DB run lifecycle.
- `QaQuestBrain` + `QuestStepDriver` — quest play and stuck detection. Progress for `no_progress` uses quest goals/level only (position changes do not reset the timer). Soft stuck (`no_progress`, `step_timeout`, missing NPC/mob/env, map-event path stalls) keeps the current quest focus and backs off in-world. QA kill positioning uses per-skill `CastReach` from `Skill.dat`, not the melee `KillReach` constant. ClearMapEvent layers without a field entry NPC use dungeon portal / `EnterDungeonById` before waiting on entry. After each combat cast the tick advances through `Skill.dat` hit delay so damage and XP credit (same requirement as integration tests' `CombatCastAsync`).
- `QaPlayerSafety` — proximity and engagement checks.
- Migrations `133`–`136` (runs/reports, buckets/samples, internal account slots, `aion_app` grants + `game.qa_ensure_internal_account`).
- Admin: **Game → QA bots**, `/api/admin/qa/*`.

## Stuck reason codes

`step_timeout`, `path_stuck`, `no_progress`, `npc_missing`, `mob_missing`, `accept_failed`, `reward_failed`, `unsupported_objective`, `no_quest_available`, `env_missing`, plus boot failures `login_failed`, `create_failed`, `select_failed`, `enter_failed`, `load_failed` (reported under quest code `boot`).

Internal accounts and QA runs initialize once when the world is ready (not every tick). Stale `running` rows in `game.qa_runs` are closed on startup and host shutdown. With Postgres, bots use the same persisted character flow as players (`ServiceNativeCharacterStore`), not development wire id `65536`.

Heartbeat `xp=` is **per-level** experience (resets toward 0 after each level-up, same as the live client `LevelUpdate` / `Exp.dat`); it is not lifetime XP. Logs show `current/need` toward the next level (from `Exp.dat` NeedExp). Below level 10, logs append `quest-track` because **open-field kill XP is officially disabled until level 10** (`GameSession.KillExperienceLevel`) — the same for players; level-ups in the starter band come from quest rewards. `xp=0/2589 quest-track` at L4 after a quest ding is normal.

`quests_rewarded` on `game.qa_runs` increments when a bot **claims a quest reward** (`QuestLog.Rewarded`), synced each tick from the brain — not on every step advance.

`onlineBots` counts only `InWorld` sessions. A brief dip (e.g. 5→3) with no error lines usually means slots are in `Pending` during login/map load or `Leaving` during a staggered auto-restart after a stuck leave (default 5 s `RestartDelayMs`), not silent mass disconnects.

## Follow-ups

- Map-event / dungeon automation, duty/craft objectives.
- Shared driver parity with campaign integration tests.