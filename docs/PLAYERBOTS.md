# Playerbots

Playerbots are characters the server plays. Other players see them as ordinary players: they appear, run, join the
party and fight what their leader fights with their class's skills. A player summons one of their own other
characters. The official game has no playerbots, so everything here is **OURS**.

Ported from [ckrealm/A2Core](https://github.com/ckrealm/A2Core) (branch `friends-server`, AGPL-3.0, the same upstream
as this repository): `PlayerBots.cs`, `BotBrain.cs`, `BotRotations.cs`, `GameSession.Bots.cs`,
`PlayerBotsService.cs` and `tools/data/bot-rotations.json`. The design follows AzerothCore's mod-playerbots (no code
shared with it). See [What differs from ckrealm](#what-differs-from-ckrealm) for what was adapted.

**Off by default.** Nothing changes for players until `Aion:Bots:Enabled=true`, and even then `//bot` is for staff
unless `Aion:Bots:PlayersMayUse=true`.

## Using them

| Command | What it does |
|---|---|
| `//bot add <character>` | Summons one of your other characters on this server. With development logins (no stored characters) it makes a new unsaved character: `//bot add <name> [class]`. |
| `//bot remove <name>` / `//bot remove all` | Dismisses bots: they leave the party and the world and save. |
| `//bot list` | Your bots with their state, strategy and role. |
| `//bot follow` / `//bot stay` `[name]` | Movement strategy (default follow). |
| `//bot assist` / `//bot passive` `[name]` | Fighting with you on or off (default assist). Passive bots still heal and buff. |
| `//bot role <tank\|dps\|heal> [name]` | Which rotation entries apply and how the bot picks its fight. A new bot takes its class's first role. |

The name defaults to `all`. A bot leaves the world when its leader logs out, goes back to the character screen or
disconnects; it saves first through the leader's still-open store (`NativeGameListener` calls
`PlayerBots.OwnerLeftAsync` before the store is disposed).

## Configuration (`Aion:Bots`)

| Key | Default | Meaning |
|---|---|---|
| `Enabled` | `false` | Registers `PlayerBots` and the `PlayerBotsService` loop (only with a game port). |
| `PlayersMayUse` | `false` | Non-staff players may use `//bot`. |
| `MaxPerAccount` | `3` | Bots one account may have in the world at once. |
| `MaxTotal` | `20` | Bots on this host at once. |
| `JoinParty` | `true` | Bots join their leader's party (up to the party limit). |
| `TeleportBeyond` | `4000` | A bot farther than this (cm) from its leader, or on another map, is moved next to it. At least 1000. |

In an env file: `Aion__Bots__Enabled=true`.

## Design

**A bot is a session without a socket.** `PlayerBots` makes a `GameSession` (`Bot = true`) for the character and feeds
it the client's own request bodies: login `0x3613`, select `0x361f`, load `0x3622`, moves `0x3700`/`0x3701`/`0x3718`,
casts `0x3800` inside the `0x383a`/`0x383c` bracket and loot `0x5620`. Everything others see goes out on the session's
normal broadcast paths, and the session's own handlers decide what is allowed (learned, cooldown, cost, reach, speed).
The loop (`PlayerBotsService`) ticks every bot every 50 ms under its own `WorldTurn`. A failing bot is logged and
removed; the loop goes on.

**The brain** (`BotBrain.Think`), in order each tick: stay out of the way of known boss mechanics (not wired yet, see
below), bring a monster on a non-tank bot to the group's tank bot, support (heals, buffs, resurrection) for the group,
fight, loot its own drops, then follow (a slot 250 cm from the leader, fanning out by ±60°).

**Who a bot fights:** a tank bot first takes monsters on someone else (healers first); then the leader's target (its
last cast or hit on an NPC within 8 s, `GameSession.AttackTarget`), the bot's own target, then monsters attacking the
bot, the leader or the party (their `NpcAi` brain's victim).

**Threat.** A tank bot's session has `HateMultiplier = tank_hate_multiplier` (3); its hits on NPCs add that much more
hate in `NpcAi.Engage`. Players stay at 1. The damage and every message are unchanged.

**Stigmas** (docs/STIGMAS.md): the rotations list each class's stigma skills after its kit. A bot casts a stigma only
when that character learned it and (with `RequireSlot`) has it in a quick slot, i.e. `GameSession.StigmaCastable`.
Bots do not buy stigmas by themselves.

## Rotation data

`tools/data/bot-rotations.json` (embedded as `bot-rotations.json`) holds per class: weapon, range, position
(`front`/`behind`/`ranged`), roles and an ordered rotation. The brain casts the first entry that holds; the last entry
of every class is an unconditional level-1 attack. Conditions (`when`): `target` (`enemy`, `self`, `leader`, `ally`,
`tank`, `dead_ally`), `in_combat`, `buff_missing`, `self_hp_below`, `self_mp_above`/`below`, `target_hp_below`/`above`,
`target_casting`, `target_stunned`, `target_slowed`, `target_elite`, `target_boss`, `target_on_self`,
`target_effect_missing`, `target_has`, `target_distance_above`, `enemies_at_least`, `after` (+ `after_within_ms`),
`after_critical`, `after_block`, `after_evasion`, `after_dodge`.

The file is ckrealm's, unchanged; its generator needs data files this repository does not have, so it is not ported.
`tests/test_bot_rotations.py` and `BotRotationTests` check it against our `class-skills.json` and `stigmas.json`: every
step is a kit skill at its unlock level or a class stigma, the conditions and roles are known, and every playable class
ends on an unconditional level-1 attack. Fighter's rotation is kept for when the class is released.

## What differs from ckrealm

Our `NpcAi`, skill-effect tables and combat do not have everything ckrealm's do, so these parts are adapted (all in
`BotBrain.cs` unless noted):

- **Boss mechanics are not wired:** no stepping out of a wound-up area special (`NpcAi.WindingArea`) and no
  pre-emptive dodge before a special lands (`NpcAi.SpecialDueAt`). The code paths stay, behind stubs returning null.
- **Interrupts wait:** `target_casting: true` entries never fire (our `NpcAi` does not expose a special being wound
  up); their `target_casting: false` twins fire as usual. `target_slowed` never holds; `target_stunned` reads
  `Target.Held`. Elite = `EliteMonster`/`HeroMonster` sub-type, boss = `HeroMonster`.
- **Procs:** `after_critical` works (`GameSession.LastCriticalAt`, set when a hit on an NPC crits). `after_block` and
  `after_evasion` never fire (no block/evasion tracking). `after_dodge` fires after the bot's own accepted Dodge.
- **Effect durations:** `target_effect_missing` / `target_has` treat a damage skill's effect as lasting 8 s
  (`BotBrain.DebuffUpkeepMs`): our skill-effect tables have no single duration per skill.
- **Heal reach** is a constant 2500 cm (`BotBrain.HealRange`; ckrealm reads skill-effects `heal_range`).
- **Assist target:** set on an accepted cast at an NPC and on every landed hit (`GameSession.NoteNpcHit`).
- **Party:** a dismissed bot leaves the party for good (`GameSession.LeavePartyAsBot`); a player's disconnect keeps its
  place as before.
- **Cast replies:** the brain reads both `0x3801` forms through `GameSession.CastResultCode`.
- **Stigmas:** no automatic stigma choice for bots (ckrealm's `ChooseBotStigmas`); see above.

## Tests

- `tests/Aion.World.Tests/PlayerBotsTests.cs`: off by default; a bot logs in through the client requests, others see
  it, it joins the party, follows without a speed reject, assists the leader with accepted casts spaced by the global
  cooldown; caps, commands, dismissing, the owner leaving; `//bot` gating; a failing leave does not stop the loop; a
  heal Cleric bot heals its hurt leader; a tank bot's hate multiplier.
- `tests/Aion.World.Tests/BotRotationTests.cs` and `tests/test_bot_rotations.py`: the rotation data.

## Not done yet

Boss-mechanic avoidance and interrupts (need `NpcAi` hooks), block/evasion procs, per-skill effect durations,
automatic bot stigmas, random world bots and bot accounts.
