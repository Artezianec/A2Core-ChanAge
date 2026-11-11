# Multi-world (opt-in)

One login port can list several worlds. Each world is a game port with its own `world_id`. Characters, tickets and
world sessions are already scoped by that id. Ported from [KiskEmu](https://github.com/Doxylamin/KiskEmu) (AGPL-3.0).

**Off by default.** With the default configuration a single host behaves exactly as before:

- the login port shows the built-in two rows (Light 1361, Dark 2361);
- every pick redirects to this host's game port;
- the game keeps the wire server id 1204.

Migration 120 only adds the registry table, one `primary` row that describes the current world (1204, 1361/2361),
and a nullable `race` column on `auth.client_handoffs`. It changes no existing data.

## Pieces

| Piece | Where | What |
| --- | --- | --- |
| World registry | `game.worlds` (migration 120), `IWorldRegistry`, `PostgresWorldRegistry` | `primary` (this host), `managed` (a child process this host starts), `remote` (a world-role host elsewhere on the same database) |
| Login list | `LoginWorldList`, `LoginSession.Directory` | a Light and a Dark row per listed world; each character on its own world's row |
| Routing | `IClientHandoff.RouteLoginAsync` | before the redirect, the login's grant is rebound to the picked world (a game port only accepts grants for its own world) |
| Supervisor | `WorldSupervisor` | starts, stops and restarts managed worlds as `aion-host` in world role (`Aion:Role=world`), with backoff after a crash |
| World role | `WorldProcessOptions`, `/internal/world/status`, `/internal/world/stop` | game port only, with loopback status and stop; no login port, account site, chat or NCGuard |
| Staff page | dashboard → System → **Worlds** (`/api/admin/worlds`) | anyone with `ViewDashboard` can see the list; adding, changing, starting, stopping, restarting and removing need `ManageWorlds` (Administrator, #484); every change is audited |

## Turning it on (migration path from a single database)

1. Back up the database (`pg_dump`), then deploy and run the migrations as usual. Migration 120 is additive.
2. Restart the host. Nothing changes yet: the Worlds page lists the primary world.
3. On the Worlds page, add a **managed** world:
   - a new world id (e.g. 1205);
   - two unused login rows (e.g. 1362/2362);
   - a free game port (e.g. 13329);
   - a free loopback internal port (e.g. 18781).

   It stays stopped (`enabled = false`).
4. Open the new game port in the firewall.
5. Set `Aion:Worlds:Enabled=true` on the login host and restart it. The login port now lists the registry. The
   primary world keeps its rows and port, and game sessions use their world id as the wire server id (still 1204 for
   the primary world).
6. Start the new world on the Worlds page. It appears in the game's server list once its game port answers.

To turn it off again, stop the extra worlds and set `Aion:Worlds:Enabled=false`. Characters created on another world
keep that `world_id` and are not shown until it is enabled again.

All worlds share this one database: accounts, the Quna wallet, staff tools and audit. A managed world reads the same
connection strings as the login host (environment or appsettings).

## Not in this version (KiskEmu has them)

- **Separate auth host** (KiskEmu 091): the login host stops running a world and the primary becomes a managed world.
  That is a data migration and a change for every single host, so it is left out.
- **One database per world** (KiskEmu 092/093, `RealmLink`, account mirror, `server/DATABASES.md`) and the **realm
  WebSocket** (`server/Aion.Host/Realm/`, `REALM_LINK.md`). Both build on the per-world databases.
- **Per-world staff tools** through the auth host (`WorldLinks`: live map, players, maintenance, announcements per
  world), realm-wide chat across processes, and population history per world (KiskEmu 100).
- **Quna pushes** reach only the sessions of the process that made the change. A session on another world shows the
  new balance at its next inventory request or world entry. The balance itself is always right, because there is one
  wallet in one database.

## Local test

```bash
dotnet test tests/Aion.World.Tests --filter "FullyQualifiedName~MultiWorld|FullyQualifiedName~SingleHost"
dotnet test tests/Aion.Host.Tests --filter "FullyQualifiedName~WorldRegistry"
AION_POSTGRES_TESTS=1 python3 -m unittest tests.test_postgres_dev.WorldRegistrySqlTests -v
```

Two worlds on one machine:

1. Run the host with `Aion:Worlds:Enabled=true` and a login port.
2. Add a managed world on the Worlds page and start it.
3. Pick its row in the game's server list. The client connects to its game port.
4. A character created there is listed on that world's row.
