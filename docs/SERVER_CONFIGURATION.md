# Server configuration (Aion.Host)

The .NET host reads settings from **`server/Aion.Host/appsettings.json`**, **`appsettings.Development.json`** (when `ASPNETCORE_ENVIRONMENT=Development`), and environment variables. ASP.NET Core maps `Aion__Section__Key=value` to `Aion:Section:Key` (double underscore → colon).

Production Linux installs use `tools/server_env.py`, which writes an **EnvironmentFile** for systemd (same keys, paths as absolute filesystem paths).

## Dev vs production

| Aspect | Development (local) | Production (hosted) |
|--------|---------------------|---------------------|
| Typical bind | `127.0.0.1` (`Aion:Network:BindAddress`) | `0.0.0.0` |
| Public address | `127.0.0.1` | Player-facing IPv4 (`Aion:Network:PublicAddress`) |
| Databases | Often omitted | `ConnectionStrings__Aion` and `ConnectionStrings__Auth` required |
| Native logins | `Aion:Native:Logins=development` (any client, no save) or `accounts` | `accounts` (tickets + Postgres) |
| HTTPS | Optional on loopback | `Aion:Https:CertificateFile` + `KeyFile`, or `Aion:Https:Proxy=true` |
| Registration | Open by default | Usually `Aion:Accounts:Registration=false` (admins create accounts) |
| NCGuard capture | May enable research capture | `Aion:NcGuard:Capture=false` |
| Wardrobe static files | Open on loopback unless `Aion:Wardrobe:StaffOnly` | Staff cookie when not loopback |

Set `ASPNETCORE_ENVIRONMENT=Development` to load `appsettings.Development.json`.

## Connection strings

| Env variable | Config key | Purpose |
|--------------|------------|---------|
| `ConnectionStrings__Aion` | `ConnectionStrings:Aion` | Gameplay DB (characters, world state) |
| `ConnectionStrings__Auth` | `ConnectionStrings:Auth` | Account auth DB |

When both are set, account APIs, native **accounts** logins, staff tools, and chat account verification are enabled.

## `Aion:Network`

| Key | Default | Description |
|-----|---------|-------------|
| `BindAddress` | `127.0.0.1` | Kestrel and native TCP listen address |
| `PublicAddress` | `127.0.0.1` | IPv4 advertised to clients (login redirect, chat directory) |

## Account site (Kestrel)

| Key | Default | Description |
|-----|---------|-------------|
| `Aion:LocalPort` | `8780` | HTTPS (or HTTP behind proxy) account + API port |
| `Aion:Https:CertificateFile` | — | PEM certificate for account port |
| `Aion:Https:KeyFile` | — | PEM private key |
| `Aion:Https:Proxy` | `false` | Account port listens on loopback only; trust `X-Forwarded-*` from local proxy |
| `Aion:BridgeSecret` | — | ≥64 chars: internal handoff API (`/internal/*`) |
| `Aion:Accounts:Registration` | `true` | Public sign-up on account page |
| `Aion:Worlds:Enabled` | `false` | Multi-world (opt-in, [MULTI_WORLD.md](MULTI_WORLD.md)): the login port lists the world registry and starts managed world servers |
| `Aion:CustomNpcs:AssumePatchedClients` | `false` | Custom NPCs ([CUSTOM_NPCS.md](CUSTOM_NPCS.md)): send custom template ids, which only a client that loaded the patch pak would know. Keep off: the official client does not load unsigned paks |

## `Aion:Web`

| Key | Description |
|-----|-------------|
| `StaffPath` | Private staff dashboard path (e.g. `/k3x9…`); hides `/admin.html` |
| `Downloads` | Directory served at `/api/downloads/*` (launcher updates) |

## `Aion:AbyssRiftZone`, `Aion:FieldEvents`

`Aion:AbyssRiftZone` (`Enabled`, `ServerUtcOffsetHours`, `RiftId`, `MapId`, `MinLevel`, `MinItemLevel`, `TimeUnitSeconds`, `ArenaRadius`,
`ArenaCentre`, `GroupSpacing`, `LateStartMinutes`): the scheduled Abyss Rift Zone, default on. See [ABYSS-RIFT-ZONE.md](ABYSS-RIFT-ZONE.md).
`Aion:FieldEvents:Invasions` (default `true`): Dimensional Invasion (Beritra Air Raid and its variants) in the event list and
entrance. `Aion:FieldBosses:GuideTimers` / `Schedules` / `ServerUtcOffsetHours`: see [FIELD-BOSSES.md](FIELD-BOSSES.md).

## `Aion:Debug`

Optional research traces written by the game session (not the ASP.NET log).

| Key | Default | Description |
|-----|---------|-------------|
| `ShopFile` | — | Append-only log of shop/NP-shop/random-shop packets and purchase outcomes (`GameSession.ConfigureShopDebug`). Same effect as env `AION_SHOP_DEBUG`. |

Map-event debug uses env `AION_MAP_EVENT_DEBUG` only (see `GameSession.MapEvents.cs`).

## `Aion:Shop`

| Key | Default | Description |
|-----|---------|-------------|
| `PremiumMode` | `Quna` | Optional override for billing SKUs: default spends Quna from the bag (official). `Kina` remaps to Kina for private dev; `Free` or `Disabled` refuse or zero prices. |

BM shop tabs load sale rows from `GET /local-api-gate/goods/v2.0/goods` (see `tools/data/platform_goods.json`). Game purchases still use NP shop `0x37cd` / coin shop `0x8c06` / NPC `0x8c00` as in `docs/parity/MERCHANT_INVENTORY.ru.md`.

GM dev wallet: builder `GrantShopCurrency kina|quna [count]`. **Kinah** and **Quna** both appear as bag currency items on **0x5611** (`930100001`, `535300006`). Platform HTTP **`virtual_currency/v2.0/me/balances`** uses **`currency_group_id` 145** with **`currency_id` 109** (GameCash, shop checks) and **103** (GameCashPending, the top-left Quna counter), and **158** with **110** (Mileage); the host mirrors the account wallet there. The server does **not** send speculative **`RefreshCurrency_NT` (0x3bd1)** at world entry (no official capture of that packet on entry yet). Optional **0x3bd1** may still follow inventory spends. GNB Quna display wiring is documented in `tools/data/currency_hud.json`.

## Subscribe window (membership, Daeva Pass, Pass Shop)

| Key | Default | Description |
|-----|---------|-------------|
| `Aion:World:MembershipByDefault` | `true` | Every account holds the Special Quai Membership (Status tab, `SubscribeList_NT 0xe33b`), renewed for 30 days when it lapses. `false`: only GM grants and Pass Shop purchases. |

GM builder commands: `Membership on|off` (per account until the host restarts) and `BattlePassExp <exp>` (Daeva Pass exp to every active pass). The Pass Shop lists the `Subscribe` category goods from `goods/v2.0/goods`; buying one through `NPShopPurchase_RQ` spends Quna from the bag by default and adds 30 days. The paid Daeva Pass unlocks through `BattlePassPurchase_RQ` (1500 Quna unless `PremiumMode` is `Free` or `Disabled`; grants the paid track's immediate step-1 reward on success).

## `Aion:Progression`

| Key | Default | Description |
|-----|---------|-------------|
| `LevelCap` | `45` | Maximum character level (Global live cap on build **25767555**). Exp.dat / PcStatLevel tables and GM validation still load through level **100**; XP and `SetLevel` stop at this cap. Quests with `UnlockLevel` above the cap are marked `unavailable` in `tools/data/quests.json`. |

## `Aion:Rates`

Host multipliers for experience, quest experience, Kinah rewards, and loot-table rolls (defaults **1.0** — unchanged official pacing). Scheduled dashboard events (`SharedWorld.ExpRate` / `DropRate`, each clamped 0.1–10) multiply on top of these base rates. Idea and formulas from the community fork [ckrealm/A2Core](https://github.com/ckrealm/A2Core).

| Key | Default | Description |
|-----|---------|-------------|
| `Profile` | `official` | Preset: `official` (all 1.0) or `accelerated` (experience 5, Kinah 3, drops 3, quest experience 5). Any explicit rate below overrides the preset. |
| `Experience` | from profile | Kill experience multiplier (with event boosts). |
| `QuestExperience` | same as `Experience` | Quest turn-in experience (not boosted by experience events). |
| `Kinah` | from profile | Kinah lines in loot, quests, boxes, achievements, dungeons, etc. |
| `Drop` | from profile | Extra loot-table rolls per kill (capped at 100 rolls). |

Example (`appsettings.json` or env `Aion__Rates__Experience=2`):

```json
"Aion": {
  "Rates": {
    "Profile": "official",
    "Experience": 2,
    "Kinah": 1.5,
    "Drop": 2
  }
}
```

## Client protocol version

| Key / env | Default | Description |
|-----------|---------|-------------|
| `Aion:ClientProtocol` or `Aion:ClientBuild` | `2.0.9` | Which client wire profile the **whole server** uses: handshake, game-port framing, NPLogin parsing, and world-entry opcode layouts. Unset or invalid values fall back to **2.0.9** (build `26262663`). |
| `AION_CLIENT_PROTOCOL` | — | Overrides `Aion:ClientProtocol` / `Aion:ClientBuild` (same tokens). |

| Value | Meaning |
|-------|---------|
| `2.0.9` | Default. Login `0x3900`/`0x3901`, game `0x3610`/`0x3611`, RC4 on all client bodies, world login `0x3613`. |
| `2.0.12` | Game handshake `0x5E58` → `0xCB02`, clear opcode framing after handshake, varint NPLogin on the login port, world login `0xD456`, select/load `0x3C4F`/`0xB24E`. Opcode profile `26728906`. Launcher `/api/launcher/config` advertises build `2.0.12.0`. |
| `2.0.13` | Experimental: modern handshake detection and varint NPLogin; opcode table still reuses pinned 2.0.9 until verified. |
| `auto` | Per connection: detect `0x5E58`/`0x16FE` vs `0x3610` and clear-opcode framing from the first game frames; login parsing accepts both legacy and varint shapes. Default opcode profile stays `26262663` until you pin `2.0.12`. |

Example (`appsettings.json` for a 2.0.12 Steam private copy):

```json
"Aion": {
  "ClientProtocol": "2.0.12"
}
```

Profiles and Steam build ids are in `tools/data/client-build-profiles.json` (embedded in `Aion.Core`).

## `Aion:Native`

Native login/game ports (optional; both off unless ports are set).

| Key | Default | Description |
|-----|---------|-------------|
| `LoginPort` | — | Login server TCP port (dev chain: `3700`) |
| `GamePort` | — | Game server TCP port (`13328`) |
| `Logins` | `accounts` | `accounts` or `development` |
| `WorldId` | `1204` | Born/current server id on wire |
| `IntroCharacter` | `false` | Dev: seed `$` intro character |
| `CharacterLevel` | `1` | Dev intro level |
| `DevelopmentStartMap` | — | Dev: map id at entry |
| `DevelopmentGmNames` | — | Indexed: `DevelopmentGmNames__0`, … — GM chat names |
| `ClientGmPanel` | `true` | When `true`, staff lobby logins set `Login_RS._is_admin` and the client F10 builder panel works (`0x362e`). Env `AION_CLIENT_GM_PANEL` overrides (`0`/`false`/`off` = off). Chat `//` builder commands still work for flagged staff when off. |
| `TerrainDirectory` | auto | Extracted terrain grids |
| `NavMeshDirectory` | auto | Extracted nav meshes |
| `PreloadMaps` | `[1000]` | Indexed: `PreloadMaps__0`, … — warm-up at startup |
| `TraceDirectory` | — | Per-connection client message traces |
| `MaxConnections` | `16` (`64` in `appsettings.Development.json`) | Concurrent native connections per port |
| `IdleTimeout` | `1:00:00` | Close silent clients after duration |

## `Aion:Chat`

| Key | Default | Description |
|-----|---------|-------------|
| `Aion:ChatPort` | — | Lime/STOMP chat HTTP port (`8091` in deploy) |
| `Aion:ChatServerKey` | `1204` | Chat “server key” string |
| `Aion:ChatDevelopmentLogins` | — | Accept dev chat without game ticket |

## `Aion:Platform`

Bootstrap HTTP for the dev client (`GamePlatform.ini` → `ConfigurationServerUrl`).

| Key | Description |
|-----|-------------|
| `Port` | Bootstrap listen port (`8087` in deploy) |
| `FixtureDirectory` | `app-key.json`, session fixtures |
| `PolicyFile` | Encrypted policy blob |
| `PolicyPlaintextFile` | Plain policy encrypted at startup |
| `GeolocationFile` | JSON for `geolocations/v1.0/me` |
| `LaunchAuthorizationFile` | One-time launch token file |
| `RequestLog` | JSONL request log (`local-api-gate` paths, queries, virtual_currency bodies, 503 unhandled) |
| `CustomData:*` | Policy `custom_data` overrides (e.g. `CustomData:SecondaryAuthCheckType`) |

## `Aion:NcGuard`

| Key | Default | Description |
|-----|---------|-------------|
| `Port` | — | TLS sink port (`443` in deploy) |
| `FixtureFile` | — | NCGuard response rules JSON |
| `DataDirectory` | `research/local` | Runtime state / captures |
| `Capture` | `true` | Record requests (off on public hosts) |
| `RewriteHosts` | `false` | Dev: rewrite system hosts file |
| `DurationSeconds` | — | Auto-stop after N seconds |
| `StopFile` | — | Touch to stop sink |
| `Hosts` | built-in list | Hostnames served |

## `Aion:World`

| Key | Default | Description |
|-----|---------|-------------|
| `MapEventAskNpcProximityFallback` | `true` | When true, open map-event **AskNpc** on **Companion** or aggressive combat NpcData can complete without client talk UI: ~5 m for 3 s, or **0x3830** lock-on. Normal quest NPCs still need **0xe240** / step talk. Set `false` to disable. Env: `Aion__World__MapEventAskNpcProximityFallback=false`. |

## `Aion:Bosses`

Dungeon boss phases (slams, adds, enrage, frenzy) and tracked `ClearWave` waves: `Phases`, `Frenzy`, `FrenzySeconds`,
`DamageScale`, `Waves`, `WavePartSeconds`. See [BOSSES.md](BOSSES.md).

## `Aion:FieldBosses`

Open-world field boss respawns (retail-like hours), a personal participant-table chest for every contributor, optional
kill/spawn broadcasts (`AnnouncementScope` Off/Zone/Server, default Off; `ChatBroadcast` default false; legacy
`Announcements`), and participant centre notices. See [FIELD-BOSSES.md](FIELD-BOSSES.md).

## `Aion:Debug`

Playtest debug logging (packets, actions, entity ids). See **[DEBUG_PLAYTEST.md](./DEBUG_PLAYTEST.md)** for enable steps and filters.

| Key | Default | Description |
|-----|---------|-------------|
| `Enabled` | `false` | Master switch |
| `LogDirectory` | `research/local/logs` | Rolling `debug-YYYYMMDD.log` |
| `Packets` / `Actions` / `Entities` / `HttpRequests` | `true` when enabled | Per-category toggles |
| `MaxPacketHexBytes` | `256` | Truncate packet hex dumps |
| `ExcludeMovement` / `ExcludeHeartbeat` | `true` | Filter noisy opcodes |
| `IncludeOpcodes` / `ExcludeOpcodes` | — | Indexed opcode lists |

## `Aion:ServerLog`

Mirrors all host console logging to `research/local/logs/server-yyyyMMdd-HHmmss.log` (UTF-8). Enabled by default; set `Aion:ServerLog:Enabled=false` to disable. See [DEBUG_PLAYTEST.md](./DEBUG_PLAYTEST.md).

| Key | Default | Description |
|-----|---------|-------------|
| `Enabled` | `true` | Create the server log file |
| `LogDirectory` | `research/local/logs` | Output directory |
| `MinLevel` | `Information` | File provider minimum level |

## Optional paths (deploy / dev)

| Key | Set by | Purpose |
|-----|--------|---------|
| `Aion:WardrobePath` | `server_env.py` / dev | Staff 3D preview assets |
| `Aion:Wardrobe:StaffOnly` | dev checks | Require staff cookie on loopback |
| `Aion:MapImagesPath` | deploy | Dashboard map images |
| `Aion:Backups:Directory` | deploy | pg_dump output |
| `Aion:Backups:Tool` | deploy | Path to `postgres_dev.py` |
| `Aion:Backups:PgHome` | deploy | Postgres data dir for backups |

## Python dev chain (`tools/run_game_chain.py`)

Sets many of the same `Aion__Native__*` and `Aion__Platform__*` variables for a local multi-process run. See script and `tools/server_env.py` for authoritative deploy defaults.

## Development wire placeholders

Shared constants for local probes and development logins live in `server/Aion.World/DevelopmentDefaults.cs` and `tools/dev_defaults.py` (account sid `1000001`, world id `1204`, null GUID, opening quest codes).

## Related files

- `server/Aion.Host/Program.cs` — binds sections to services
- `server/Aion.Host/NetworkOptions.cs`, `Native/NativeListeners.cs` (`NativeOptions`), `Platform/PlatformBootstrapEndpoints.cs`, `NcGuard/NcGuardSink.cs`
- `tools/server_env.py` — production EnvironmentFile generator
- `tests/test_server_deploy.py` — asserts on generated deploy env
