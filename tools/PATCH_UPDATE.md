# Update after a client patch

`tools/patch_update.ps1` runs the repeatable part of a patch: detect the build, check the
archive key, extract the tracked tables, diff them against the previous build, rebuild the
catalogue, promote the exports and pack the server data bundle. It records what it produced in
`research/local/build.json`.

Everything it writes stays under `research/local/` (gitignored) except `tools/data/*.json`,
which only changes when you ask for it.

## Prerequisites (once per machine)

- `research/local/archive-key-validated.json` — derived with `tools/probe_archive_keys.py`
- `research/local/tooling/oodle-*/oodle-data-shared.dll` (or `oo2core_9_win64.dll`)
- `research/local/tooling/mappings/*.usmap` for the client build
- .NET 10 and Python 3.11+; `tools/WorldProbe` is built on demand (`CUE4PARSE_SKIP_NATIVE=true`)

## Commands

```powershell
# what is recorded, what changed, which runtime layers are missing
.\tools\patch_update.ps1 -Status

# after a client update
.\tools\patch_update.ps1 -Extract -Diff -BuildCatalog -Pack

# replace the "last accepted" exports so the next patch diffs against this build
.\tools\patch_update.ps1 -Extract -Diff -Promote -BuildCatalog -Pack

# only inspect a class-skills update (writes nothing) / apply it
.\tools\patch_update.ps1 -DryRunData
.\tools\patch_update.ps1 -WriteData
```

| Switch | Effect |
| --- | --- |
| `-Status` | default; prints the recorded state, tracked-file hashes and runtime layers |
| `-Extract` | WorldProbe the groups in `tools/patch_tables.json` into `research/local/patch-<build>/<group>` |
| `-Diff` | `tools/client_table_diff.py` against `research/local/<group>` (the previous build) |
| `-Promote` | copy `patch-<build>/<group>` over `research/local/<group>` |
| `-BuildCatalog` | `tools/game_catalog.py build --local patch-<build>`; needs `-Force` to replace an existing catalogue (kept as `game-catalog-v2.bak`) |
| `-DryRunData` / `-WriteData` | `tools/client_build_update.py` on `class-skills.json`, with or without `--write` |
| `-Pack` | `tools/pack_server_data.py`; skipped with a warning while the bootstrap fixtures are missing |
| `-NoMaps` | pack without terrain/navmesh |
| `-Force` | replace existing patch exports, catalogue or bundle |
| `-SkipKeyCheck` | skip the one-table mount check |

Useful overrides: `-BuildId`, `-ClientPaks`, `-KeyFile`, `-Oodle`, `-Usmap`, `-AppVersion`,
`-CatalogOut`, `-Bundle`.

## Tracked tables

`tools/patch_tables.json` lists the four export groups the catalogue and the skip lists need.
Add tables there (same selection order as the builders expect) when you want them refreshed
every patch.

## What is not automated

- **Archive key and usmap** are per-build. When the key check fails, the script prints the
  `probe_archive_keys.py` command; the key must be re-derived while the client runs.
- **terrain / navmesh** (`TerrainProbe`, `NavProbe`) are separate, long runs; both skip maps
  that are already extracted.
- **bootstrap fixtures** (`app-key.json`, `local-test-key.json`, `app-policy.json`) and the
  NCGuard fixture are captures, not client data. Use the release bundle or `docs/CAPTURE.md`.
- **Other `tools/data` builders** (`item_data.py`, `world_npcs.py`, `map_events.py`, ...) are
  listed after `-DryRunData`; they have different inputs, so run the ones whose table changed
  and rebuild the host afterwards (`dotnet build server/Aion.Host`).

## build.json

Written under `research/local/`, schema version 1:

```json
{
  "client_build": "26728906",
  "app_version": "2.0.12",
  "updated_utc": "...",
  "client_paks": "...",
  "key_file": { "path": "...", "sha256": "..." },
  "usmap": { "path": "...", "sha256": "..." },
  "oodle": { "path": "...", "sha256": "..." },
  "exports": { "patch_dir": "..." },
  "catalog": { "path": "...", "counts": { "items": 0, "skills": 0 }, "manifest_sha256": "..." },
  "bundle": { "path": "...", "sha256": "..." },
  "steps": ["..."],
  "history": [{ "client_build": "...", "steps": ["..."] }]
}
```

`-Status` compares the recorded hashes with the files on disk and warns when the Steam build
changed.
