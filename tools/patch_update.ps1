<#
.SYNOPSIS
    One command per client patch: detect the build, check the archive key, extract the tracked
    game tables, diff them against the previous build, rebuild the catalogue, pack the server
    data bundle and record what was produced in research/local/build.json.

.DESCRIPTION
    Pipeline (each step is opt-in except the read-only status check):

      -Status        Show the recorded state (research/local/build.json) and whether the client
                     build or the tracked files changed. This is the default.
      -Extract       Extract the groups listed in tools/patch_tables.json with WorldProbe into
                     research/local/patch-<build>/<group> (selections written to
                     research/local/selections/<group>.json).
      -Diff          Compare the new exports with the previous exports in research/local/ (only
                     the groups that exist) using tools/client_table_diff.py.
      -BuildCatalog  Rebuild research/local/game-catalog-v2 from the patch exports
                     (tools/game_catalog.py build --local <patch>).
      -Promote       Copy the patch exports over research/local/<group> (the "last accepted
                     build", used by the other builders and as the base for the next diff).
                     Run it after -Diff.
      -DryRunData    Print what tools/client_build_update.py would change in
                     tools/data/class-skills.json (no write).
      -WriteData     Apply that class-skills update (adds --write).
      -Pack          Run tools/pack_server_data.py (skipped with a warning when the bootstrap
                     fixtures are missing).

    The archive key and the usmap are per-build and are NOT updated by this script: when the key
    no longer mounts, the tool prints the probe command to re-derive it.

.EXAMPLE
    .\tools\patch_update.ps1 -Status

.EXAMPLE
    .\tools\patch_update.ps1 -Extract -Diff -BuildCatalog -Pack

.EXAMPLE
    .\tools\patch_update.ps1 -Extract -Force -Diff -BuildCatalog -Pack -Bundle aion-server-data-26728906.tar.gz

.NOTES
    Requires: .NET 10 (WorldProbe built or buildable), Python 3.11+, the archive key at
    research/local/archive-key-validated.json and an Oodle DLL under research/local/tooling/.
#>
[CmdletBinding()]
param(
    [string]$BuildId,
    [string]$ClientPaks,
    [string]$KeyFile,
    [string]$Oodle,
    [string]$Usmap,
    [string]$AppVersion,
    [string]$CatalogOut,
    [string]$Bundle,

    [switch]$Status,
    [switch]$Extract,
    [switch]$Diff,
    [switch]$BuildCatalog,
    [switch]$Promote,
    [switch]$DryRunData,
    [switch]$WriteData,
    [switch]$Pack,
    [switch]$NoMaps,
    [switch]$SkipKeyCheck,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$Repo = Split-Path -Parent $PSScriptRoot
Set-Location $Repo
$Local = Join-Path $Repo 'research\local'
$StatePath = Join-Path $Local 'build.json'

function Write-Info([string]$text) { Write-Host $text }
function Write-Step([string]$text) { Write-Host ''; Write-Host "=== $text ===" -ForegroundColor Cyan }
function Write-Warn([string]$text) { Write-Host "WARN: $text" -ForegroundColor Yellow }
function Fail([string]$text) { Write-Host "ERROR: $text" -ForegroundColor Red; exit 1 }

function Write-JsonText([string]$path, [string]$json) {
    $dir = Split-Path -Parent $path
    if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Force $dir | Out-Null }
    [IO.File]::WriteAllText($path, $json, (New-Object Text.UTF8Encoding($false)))
}

function Write-Selection([string]$path, [string[]]$tables) {
    $quoted = $tables | ForEach-Object { '"' + ($_ -replace '\\', '\\') + '"' }
    Write-JsonText $path ('[' + ($quoted -join ',') + ']')
}

function Get-Sha256([string]$path) {
    if ($path -and (Test-Path $path) -and -not (Test-Path $path -PathType Container)) {
        return (Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash.ToLowerInvariant()
    }
    return $null
}

function Read-State {
    if (-not (Test-Path $StatePath)) { return $null }
    try { return (Get-Content $StatePath -Raw | ConvertFrom-Json) } catch { return $null }
}

function Save-State($state) {
    Write-JsonText $StatePath ($state | ConvertTo-Json -Depth 10)
}

function Get-SteamBuildId {
    $libraries = New-Object System.Collections.Generic.List[string]
    $steam = 'C:\Program Files (x86)\Steam'
    $vdf = Join-Path $steam 'steamapps\libraryfolders.vdf'
    if (Test-Path $vdf) {
        $text = Get-Content $vdf -Raw
        foreach ($match in [regex]::Matches($text, '"path"\s+"([^"]+)"')) {
            $libraries.Add(($match.Groups[1].Value -replace '\\\\', '\'))
        }
    }
    $libraries.Add($steam)
    foreach ($library in ($libraries | Select-Object -Unique)) {
        $acf = Join-Path $library 'steamapps\appmanifest_3393110.acf'
        if (-not (Test-Path $acf)) { continue }
        $match = [regex]::Match((Get-Content $acf -Raw), '"buildid"\s+"(\d+)"')
        if ($match.Success) { return $match.Groups[1].Value }
    }
    return $null
}

function Resolve-ClientPaks {
    if ($ClientPaks) {
        if (-not (Test-Path $ClientPaks)) { Fail "Client paks folder not found: $ClientPaks" }
        return (Resolve-Path $ClientPaks).Path
    }
    $steam = 'C:\Program Files (x86)\Steam\steamapps\common\AION2\Aion2\Content\Paks'
    if (Test-Path $steam) { return (Resolve-Path $steam).Path }
    if ($env:AION2_PAKS -and (Test-Path $env:AION2_PAKS)) { return (Resolve-Path $env:AION2_PAKS).Path }
    Fail 'Client paks folder not found. Pass -ClientPaks "<install>\Aion2\Content\Paks" or set AION2_PAKS.'
}

function Resolve-KeyFile {
    $path = $KeyFile
    if (-not $path) { $path = Join-Path $Local 'archive-key-validated.json' }
    if (-not (Test-Path $path)) {
        Fail "Archive key not found at $path. Derive it first: python tools/probe_archive_keys.py --install <install> --pid <AION2 pid> --deep --output research/local/archive-key-report.json"
    }
    return (Resolve-Path $path).Path
}

function Resolve-Oodle {
    if ($Oodle) {
        if (-not (Test-Path $Oodle)) { Fail "Oodle DLL not found: $Oodle" }
        return (Resolve-Path $Oodle).Path
    }
    $tooling = Join-Path $Local 'tooling'
    if (Test-Path $tooling) {
        $candidates = Get-ChildItem $tooling -Directory -Filter 'oodle-*' -ErrorAction SilentlyContinue |
            ForEach-Object { Join-Path $_.FullName 'oodle-data-shared.dll' }
        foreach ($candidate in $candidates) {
            if (Test-Path $candidate) { return (Resolve-Path $candidate).Path }
        }
    }
    Fail 'Oodle DLL not found under research/local/tooling/oodle-*/. Place oodle-data-shared.dll there or pass -Oodle.'
}

function Resolve-Usmap {
    if ($Usmap) {
        if (-not (Test-Path $Usmap)) { Fail "Usmap not found: $Usmap" }
        return (Resolve-Path $Usmap).Path
    }
    $dir = Join-Path $Local 'tooling\mappings'
    $list = Get-ChildItem $dir -File -Filter '*.usmap' -ErrorAction SilentlyContinue
    if (-not $list) { Fail "No .usmap under $dir. Add the mapping for the client build or pass -Usmap." }
    if ($BuildId) {
        $exact = $list | Where-Object { $_.Name -match ('build' + [regex]::Escape($BuildId)) } | Select-Object -First 1
        if ($exact) { return $exact.FullName }
    }
    return ($list | Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName
}

function Resolve-WorldProbe {
    $dll = Join-Path $Repo 'tools\WorldProbe\bin\Release\net10.0\WorldProbe.dll'
    if (Test-Path $dll) { return $dll }
    Write-Warn 'WorldProbe not built; building tools/WorldProbe (Release).'
    $env:CUE4PARSE_SKIP_NATIVE = 'true'
    & dotnet build (Join-Path $Repo 'tools\WorldProbe\WorldProbe.csproj') -c Release --nologo | Out-Host
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path $dll)) { Fail 'Could not build tools/WorldProbe.' }
    return $dll
}

function Get-AppVersionFromUsmap([string]$path) {
    if ($AppVersion) { return $AppVersion }
    $name = Split-Path -Leaf $path
    $match = [regex]::Match($name, '(\d+)\.(\d+)\.(\d+)')
    if ($match.Success) { return ($match.Groups[1].Value + '.' + $match.Groups[2].Value + '.' + $match.Groups[3].Value) }
    return $null
}

function Invoke-WorldProbe([string]$worldProbe, [string]$paks, [string]$key, [string]$oodleDll,
    [string]$mapping, [string]$selection, [string]$outDir, [string]$version, [string]$log) {
    $arguments = @($worldProbe, $paks, $key, $oodleDll, $mapping, $selection, $outDir)
    if ($version) { $arguments += @('--app-version', $version) }
    $output = & dotnet @arguments 2>&1
    $output | Tee-Object -FilePath $log -Append | Out-Host
    return ($LASTEXITCODE -eq 0)
}

function Test-ArchiveKey([string]$worldProbe, [string]$paks, [string]$key, [string]$oodleDll,
    [string]$mapping, [string]$version) {
    $checkDir = Join-Path $Local '.patch-keycheck'
    $selection = Join-Path $Local 'selections\keycheck.json'
    Remove-Item -Recurse -Force $checkDir -ErrorAction SilentlyContinue
    Write-Selection $selection @('Aion2/Content/Data/Table/EnumString.dat')
    $log = Join-Path $Local 'patch-keycheck.log'
    $ok = Invoke-WorldProbe $worldProbe $paks $key $oodleDll $mapping $selection $checkDir $version $log
    Remove-Item -Recurse -Force $checkDir -ErrorAction SilentlyContinue
    return $ok
}

function Load-PatchGroups {
    $config = Join-Path $Repo 'tools\patch_tables.json'
    if (-not (Test-Path $config)) { Fail 'tools/patch_tables.json is missing.' }
    $doc = Get-Content $config -Raw | ConvertFrom-Json
    $groups = [ordered]@{}
    foreach ($property in $doc.groups.PSObject.Properties) {
        $groups[$property.Name] = @($property.Value)
    }
    return $groups
}

function Show-Status {
    $state = Read-State
    $detected = if ($BuildId) { $BuildId } else { Get-SteamBuildId }

    Write-Step 'Patch status'
    if (-not $state) {
        Write-Info 'No research/local/build.json yet: nothing has been recorded on this machine.'
    } else {
        Write-Info ("Recorded client build : {0}" -f $state.client_build)
        Write-Info ("Recorded app version  : {0}" -f $state.app_version)
        Write-Info ("Last update (UTC)     : {0}" -f $state.updated_utc)
        if ($state.catalog -and $state.catalog.path) {
            Write-Info ("Catalogue             : {0} (items {1}, skills {2})" -f $state.catalog.path, $state.catalog.counts.items, $state.catalog.counts.skills)
        }
        if ($state.bundle -and $state.bundle.path) {
            Write-Info ("Bundle                : {0}" -f $state.bundle.path)
        }
    }
    Write-Info ("Detected Steam build  : {0}" -f ($(if ($detected) { $detected } else { '(not found)' })))

    if ($state -and $detected -and $state.client_build -ne $detected) {
        Write-Warn ("Client build changed: {0} -> {1}. Run: .\tools\patch_update.ps1 -Extract -Diff -BuildCatalog -Pack" -f $state.client_build, $detected)
    }

    Write-Info ''
    Write-Info 'Tracked inputs:'
    foreach ($label in @('key_file', 'usmap', 'oodle')) {
        $entry = $state.$label
        if (-not $entry) { Write-Info ("  {0,-9} (not recorded)" -f $label); continue }
        $exists = Test-Path $entry.path
        $hashOk = $exists -and (Get-Sha256 $entry.path) -eq $entry.sha256
        $mark = if ($hashOk) { 'ok' } elseif ($exists) { 'CHANGED' } else { 'MISSING' }
        Write-Info ("  {0,-9} {1} ({2})" -f $label, $mark, $entry.path)
    }

    $catalogPresent = (Test-Path (Join-Path $Local 'game-catalog-v2\manifest.json')) -and (Test-Path (Join-Path $Local 'game-catalog-v2\entries.jsonl'))
    $terrain = Get-ChildItem (Join-Path $Local 'terrain') -Directory -ErrorAction SilentlyContinue | Measure-Object | Select-Object -ExpandProperty Count
    $navmesh = Get-ChildItem (Join-Path $Local 'navmesh') -Directory -ErrorAction SilentlyContinue | Measure-Object | Select-Object -ExpandProperty Count
    $keys = Test-Path (Join-Path $Local 'bootstrap-fixtures\rsa-candidate-20261009\app-key.json')
    $policy = Test-Path (Join-Path $Local 'bootstrap-fixtures\policy-lime-20261014\app-policy.json')
    Write-Info ''
    Write-Info 'Runtime layers:'
    Write-Info ("  game-catalog-v2 : {0}" -f ($(if ($catalogPresent) { 'present' } else { 'MISSING (run -BuildCatalog)' })))
    Write-Info ("  terrain maps    : {0}" -f $terrain)
    Write-Info ("  navmesh maps    : {0}" -f $navmesh)
    Write-Info ("  bootstrap keys  : {0}" -f ($(if ($keys) { 'present' } else { 'MISSING (release bundle or docs/CAPTURE.md)' })))
    Write-Info ("  bootstrap policy: {0}" -f ($(if ($policy) { 'present' } else { 'MISSING (release bundle or docs/CAPTURE.md)' })))
}

function Invoke-Extract([string]$worldProbe, [string]$paks, [string]$key, [string]$oodleDll,
    [string]$mapping, [string]$version, [string]$patchDir, $groups) {
    $done = @()
    foreach ($name in $groups.Keys) {
        $out = Join-Path $patchDir $name
        if (Test-Path $out) {
            if (-not $Force) { Write-Warn "Skipping $name (exists: $out; use -Force to replace)"; continue }
            Remove-Item -Recurse -Force $out
        }
        $selection = Join-Path $Local ('selections\' + $name + '.json')
        Write-Selection $selection $groups[$name]
        Write-Step "Extract $name"
        $log = Join-Path $patchDir 'extract.log'
        $ok = Invoke-WorldProbe $worldProbe $paks $key $oodleDll $mapping $selection $out $version $log
        if (-not $ok) {
            Write-Warn "WorldProbe failed for $name; see $log"
            continue
        }
        $done += $name
    }
    return $done
}

function Invoke-Diff([string]$patchDir, $groups) {
    $diffRoot = Join-Path $patchDir 'diff'
    foreach ($name in $groups.Keys) {
        $old = Join-Path $Local $name
        $new = Join-Path $patchDir $name
        $selection = Join-Path $Local ('selections\' + $name + '.json')
        if (-not (Test-Path $old)) { Write-Warn "No previous export for $name (skipping diff)"; continue }
        if (-not (Test-Path $new)) { Write-Warn "No new export for $name (skipping diff)"; continue }
        if (-not (Test-Path $selection)) { Write-Warn "No selection for $name (skipping diff)"; continue }
        if ((Test-Path (Join-Path $new 'localization-000.json')) -and -not (Test-Path (Join-Path $new 'table-000.json'))) {
            Write-Info "Skipping diff for $name (localization output, not a table-NNN export)"
            continue
        }
        Write-Step "Diff $name"
        $out = Join-Path $diffRoot $name
        & python (Join-Path $Repo 'tools\client_table_diff.py') --tables $old $new $selection $out 2>&1 | Out-Host
        if ($LASTEXITCODE -ne 0) { Write-Warn "diff failed for $name" }
    }
}

function Invoke-Promote([string]$patchDir, $groups) {
    Write-Step 'Promote patch exports'
    Write-Info 'The exports in research/local/<group> are replaced; run -Diff before promoting if you need the comparison.'
    foreach ($name in $groups.Keys) {
        $src = Join-Path $patchDir $name
        if (-not (Test-Path $src)) { Write-Warn "Skipping $name (no new export)"; continue }
        $dst = Join-Path $Local $name
        if (Test-Path $dst) { Remove-Item -Recurse -Force $dst }
        Copy-Item -Recurse -Force $src $dst
        Write-Info ("{0} -> {1}" -f $name, $dst)
    }
}

function Invoke-Catalog([string]$patchDir, [string]$out) {
    Write-Step 'Build game-catalog-v2'
    $manifest = Join-Path $patchDir 'reuse-table-probe\table-010.json'
    if (-not (Test-Path $manifest)) { Fail "Cannot build the catalogue: $manifest is missing (run -Extract first)." }
    if (Test-Path $out) {
        if (-not $Force) {
            Fail "Catalogue already exists: $out. Pass -Force to replace it (the previous one is kept as game-catalog-v2.bak)."
        }
        $backup = $out.TrimEnd('\') + '.bak'
        if (Test-Path $backup) { Remove-Item -Recurse -Force $backup }
        Rename-Item -LiteralPath $out -NewName (Split-Path -Leaf $backup)
        Write-Info "Previous catalogue kept as $backup"
    }
    & python (Join-Path $Repo 'tools\game_catalog.py') build --local $patchDir --output $out 2>&1 | Out-Host
    if ($LASTEXITCODE -ne 0) { Fail 'game_catalog.py build failed.' }
    $manifestPath = Join-Path $out 'manifest.json'
    if (Test-Path $manifestPath) {
        $doc = Get-Content $manifestPath -Raw | ConvertFrom-Json
        Write-Info ("Catalogue: items {0}, skills {1} -> {2}" -f $doc.counts.items, $doc.counts.skills, $out)
    }
    return $manifestPath
}

function Invoke-Data([string]$patchDir, [string]$selection, [string]$build, [bool]$write) {
    Write-Step 'Update tools/data (class-skills)'
    $arguments = @((Join-Path $Repo 'tools\client_build_update.py'),
        (Join-Path $patchDir 'reuse-table-probe'), $selection, '--build', $build)
    if ($write) { $arguments += '--write' }
    & python @arguments 2>&1 | Out-Host
    if ($LASTEXITCODE -ne 0) { Write-Warn 'client_build_update.py failed.' }
    Write-Info ''
    Write-Info 'Other builders (run only the ones whose source table changed; inputs differ per builder):'
    Write-Info '  python tools/item_data.py            (items.json)'
    Write-Info '  python tools/world_npcs.py           (world-npcs.json)'
    Write-Info '  python tools/map_events.py           (map-events.json)'
    Write-Info '  python tools/quests.py               (quests.json)'
    Write-Info '  python tools/loot_data.py            (loot.json)'
    Write-Info '  python tools/shop_data.py            (shops.json)'
    Write-Info 'After changing tools/data, rebuild the host: dotnet build server/Aion.Host'
}

function Invoke-Pack([string]$catalog, [string]$output, [bool]$noMaps) {
    Write-Step 'Pack server data bundle'
    $keys = Join-Path $Local 'bootstrap-fixtures\rsa-candidate-20261009\app-key.json'
    $policy = Join-Path $Local 'bootstrap-fixtures\policy-lime-20261014\app-policy.json'
    $ncguard = Join-Path $Local 'ncguard-payload-fetch-20261014-072114\fixture.json'
    $missing = @()
    if (-not (Test-Path $keys)) { $missing += 'bootstrap/keys/app-key.json' }
    if (-not (Test-Path $policy)) { $missing += 'bootstrap/policy/app-policy.json' }
    if (-not (Test-Path $ncguard)) { $missing += 'ncguard/fixture.json' }
    if (-not (Test-Path (Join-Path $catalog 'manifest.json'))) { $missing += 'game-catalog-v2/manifest.json' }
    if ($missing.Count -gt 0) {
        Write-Warn ('Pack skipped; missing: ' + ($missing -join ', '))
        Write-Info 'Use the release bundle (aion-server-data.tar.gz) or capture the bootstrap fixtures (docs/CAPTURE.md).'
        return $null
    }

    if (Test-Path $output) {
        if (-not $Force) { Fail "Bundle already exists: $output (pass -Force to replace)." }
        Remove-Item -Force $output
    }
    if (-not $noMaps) {
        $terrain = Join-Path $Local 'terrain'
        $navmesh = Join-Path $Local 'navmesh'
        if (-not (Test-Path $terrain) -or -not (Test-Path $navmesh)) {
            Write-Warn 'terrain/ or navmesh/ missing; packing without maps (-NoMaps behaviour).'
            $noMaps = $true
        }
    }

    $arguments = @((Join-Path $Repo 'tools\pack_server_data.py'), $output, '--catalog', $catalog)
    if ($noMaps) { $arguments += '--no-maps' }
    & python @arguments 2>&1 | Out-Host
    if ($LASTEXITCODE -ne 0) { Fail 'pack_server_data.py failed.' }
    return (Resolve-Path $output).Path
}

# ---------------------------------------------------------------- main

$statusOnly = -not ($Extract -or $Diff -or $BuildCatalog -or $Promote -or $DryRunData -or $WriteData -or $Pack)
if ($Status -or $statusOnly) {
    Show-Status
    if ($statusOnly) {
        Write-Info ''
        Write-Info 'Next: .\tools\patch_update.ps1 -Extract -Diff -BuildCatalog -Pack'
    }
    exit 0
}

if (-not $BuildId) { $BuildId = Get-SteamBuildId }
if (-not $BuildId) { Fail 'Could not detect the Steam build id; pass -BuildId <id>.' }

$paks = Resolve-ClientPaks
$key = Resolve-KeyFile
$oodleDll = Resolve-Oodle
$worldProbe = Resolve-WorldProbe
$mapping = Resolve-Usmap
$appVersion = Get-AppVersionFromUsmap $mapping
$patchDir = Join-Path $Local ('patch-' + $BuildId)
if (-not (Test-Path $patchDir)) { New-Item -ItemType Directory -Force $patchDir | Out-Null }
if (-not $CatalogOut) { $CatalogOut = Join-Path $Local 'game-catalog-v2' }
if (-not $Bundle) { $Bundle = Join-Path $Repo ('aion-server-data-' + $BuildId + '.tar.gz') }
$groups = Load-PatchGroups

Write-Step 'Environment'
Write-Info ("Repo        : {0}" -f $Repo)
Write-Info ("Client build: {0}" -f $BuildId)
Write-Info ("App version : {0}" -f ($(if ($appVersion) { $appVersion } else { '(auto-detect)' })))
Write-Info ("Paks        : {0}" -f $paks)
Write-Info ("Key         : {0}" -f $key)
Write-Info ("Oodle       : {0}" -f $oodleDll)
Write-Info ("Usmap       : {0}" -f $mapping)
Write-Info ("Patch dir   : {0}" -f $patchDir)

$steps = New-Object System.Collections.Generic.List[string]

if (-not $SkipKeyCheck) {
    Write-Step 'Check archive key'
    if (Test-ArchiveKey $worldProbe $paks $key $oodleDll $mapping $appVersion) {
        Write-Info 'Key mounts and decodes a data table.'
        $steps.Add('key-check')
    } else {
        Write-Warn 'The archive key did not decode a table. The key or the usmap is probably from another build.'
        Write-Info 'Re-derive it: python tools/probe_archive_keys.py --install "<install>" --pid <AION2 pid> --deep --output research/local/archive-key-report.json'
        Write-Info 'Or re-run with -SkipKeyCheck to continue anyway.'
        exit 2
    }
}

if ($Extract) {
    $done = Invoke-Extract $worldProbe $paks $key $oodleDll $mapping $appVersion $patchDir $groups
    $steps.Add('extract: ' + ($done -join ', '))
}

if ($Diff) {
    Invoke-Diff $patchDir $groups
    $steps.Add('diff')
}

if ($Promote) {
    Invoke-Promote $patchDir $groups
    $steps.Add('promote')
}

$catalogManifest = $null
if ($BuildCatalog) {
    $catalogManifest = Invoke-Catalog $patchDir $CatalogOut
    $steps.Add('catalog')
}

if ($DryRunData -or $WriteData) {
    $selection = Join-Path $Local 'selections\reuse-table-probe.json'
    if (-not (Test-Path $selection)) { Fail "Selection missing: $selection (run -Extract first)." }
    Invoke-Data $patchDir $selection $BuildId ([bool]$WriteData)
    if ($WriteData) { $steps.Add('tools-data: class-skills') } else { $steps.Add('tools-data: dry-run') }
}

$bundlePath = $null
if ($Pack) {
    $bundlePath = Invoke-Pack $CatalogOut $Bundle ([bool]$NoMaps)
    if ($bundlePath) { $steps.Add('pack: ' + $bundlePath) }
}

# ---------------------------------------------------------------- state

$previous = Read-State
$state = [ordered]@{
    schema_version = 1
    client_build    = $BuildId
    app_version     = $appVersion
    updated_utc     = (Get-Date).ToUniversalTime().ToString('o')
    client_paks     = $paks
    key_file        = [ordered]@{ path = $key; sha256 = (Get-Sha256 $key) }
    usmap           = [ordered]@{ path = $mapping; sha256 = (Get-Sha256 $mapping) }
    oodle           = [ordered]@{ path = $oodleDll; sha256 = (Get-Sha256 $oodleDll) }
    exports         = [ordered]@{ patch_dir = $patchDir }
    catalog         = $null
    bundle          = $null
    steps           = @($steps)
    history         = @()
}

if ($catalogManifest -and (Test-Path $catalogManifest)) {
    $doc = Get-Content $catalogManifest -Raw | ConvertFrom-Json
    $state.catalog = [ordered]@{
        path          = $CatalogOut
        manifest      = $catalogManifest
        manifest_sha256 = (Get-Sha256 $catalogManifest)
        counts        = [ordered]@{ items = $doc.counts.items; skills = $doc.counts.skills }
    }
} elseif ($previous -and $previous.catalog) {
    $state.catalog = $previous.catalog
}
if ($bundlePath) {
    $state.bundle = [ordered]@{ path = $bundlePath; sha256 = (Get-Sha256 $bundlePath) }
} elseif ($previous -and $previous.bundle) {
    $state.bundle = $previous.bundle
}
if ($previous) {
    $entry = [ordered]@{
        client_build = $previous.client_build
        app_version  = $previous.app_version
        updated_utc  = $previous.updated_utc
        steps        = @($previous.steps)
    }
    $history = @()
    if ($previous.history) { $history = @($previous.history) }
    $history = @($entry) + $history
    $state.history = @($history | Select-Object -First 10)
}

Save-State $state
Write-Step 'Recorded'
Write-Info ("research/local/build.json updated (build {0}, steps: {1})" -f $BuildId, ($(if ($steps.Count) { $steps -join ', ' } else { 'none' })))
