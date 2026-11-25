<#
.SYNOPSIS
    Trust (or remove) the NCGuard sink's loopback CA in the current user's Root store.

.DESCRIPTION
    The AION2 client validates the TLS certificate of the Settings Server
    (mobileappconfig.g.nc.com) strictly, unlike the NCGuard/STUN hosts. The C# NCGuard sink
    generates its own CA on first start (research/.start-all runtime, ncguard-state) but does
    not install it, so the client aborts the handshake with alert unknown_ca and the game shows
    "Failed to retrieve Settings Server value".

    This script adds that CA to Cert:\CurrentUser\Root (no elevation, reversible). Run it once
    per machine after the NCGuard sink has started at least once; the sink reuses its CA
    (ca-root.pfx), so the thumbprint stays the same across starts.

.EXAMPLE
    .\tools\trust_ncguard_ca.ps1 -Status
    .\tools\trust_ncguard_ca.ps1
    .\tools\trust_ncguard_ca.ps1 -Remove
#>
[CmdletBinding()]
param(
    [switch]$Remove,
    [switch]$Status
)

$ErrorActionPreference = 'Stop'
$Repo = Split-Path -Parent $PSScriptRoot
$stateRoot = Join-Path $Repo '.start-all\runtime\ncguard-state'

if (-not (Test-Path $stateRoot)) {
    throw "No NCGuard state under $stateRoot. Start the stack once (start-all.cmd) so the sink creates its CA."
}

$latest = Get-ChildItem $stateRoot -Directory |
    Where-Object { Test-Path (Join-Path $_.FullName 'ca.pem') } |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1
if (-not $latest) { throw "No ca.pem found under $stateRoot." }
$ca = Join-Path $latest.FullName 'ca.pem'

# The CA is stable; read its SHA-1 thumbprint from the PEM with certutil.
$dump = & certutil -dump $ca 2>&1 | Out-String
$match = [regex]::Match($dump, 'Cert Hash\(sha1\):\s*([0-9a-fA-F]+)')
if (-not $match.Success) { throw "Could not read the CA thumbprint from $ca." }
$thumbprint = $match.Groups[1].Value.ToUpperInvariant()

function Test-Trusted {
    return [bool](Get-ChildItem Cert:\CurrentUser\Root |
        Where-Object { $_.Thumbprint -eq $thumbprint } | Select-Object -First 1)
}

if ($Status) {
    $trusted = Test-Trusted
    Write-Host ("CA       : {0}" -f $ca)
    Write-Host ("Thumbprint: {0}" -f $thumbprint)
    Write-Host ("Trusted  : {0}" -f ($(if ($trusted) { 'yes (CurrentUser Root)' } else { 'NO' })))
    if (-not $trusted) {
        Write-Host ''
        Write-Host 'Install it: .\tools\trust_ncguard_ca.ps1'
    }
    exit 0
}

if ($Remove) {
    if (Test-Trusted) {
        & certutil -user -delstore Root $thumbprint | Out-Host
        Write-Host "Removed $thumbprint from Cert:\CurrentUser\Root."
    } else {
        Write-Host "$thumbprint is not in Cert:\CurrentUser\Root; nothing to remove."
    }
    exit 0
}

if (Test-Trusted) {
    Write-Host "$thumbprint is already trusted (Cert:\CurrentUser\Root)."
    exit 0
}

& certutil -user -addstore -f Root $ca | Out-Host
if ($LASTEXITCODE -ne 0 -or -not (Test-Trusted)) {
    throw "certutil could not add the CA to Cert:\CurrentUser\Root."
}
Write-Host "Trusted $thumbprint (CN=AionResearch loopback CA) in Cert:\CurrentUser\Root."
Write-Host 'Start or restart the client (launcher Play or Steam -applaunch) so it revalidates the Settings Server.'
