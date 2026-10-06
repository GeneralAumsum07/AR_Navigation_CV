<#
.SYNOPSIS
  Download the pinned depth models and verify their SHA-256 against tools/models.lock.json.
.DESCRIPTION
  First run: pass -Record to write the hashes into the lock file (then commit it).
  Every later run verifies them and fails on any mismatch, so a silently changed upstream
  file can never reach a build.
#>
param(
    [switch]$Record
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSCommandPath
$lockPath = Join-Path $root 'models.lock.json'
$cache = Join-Path $root '.cache'
New-Item -ItemType Directory -Force $cache | Out-Null
$lock = Get-Content $lockPath -Raw | ConvertFrom-Json
$changed = $false

foreach ($m in $lock.models) {
    $zip = Join-Path $cache ($m.name + '.zip')
    if (-not (Test-Path $zip)) {
        Write-Host "Downloading $($m.url)"
        # TLS 1.2 is not the default in Windows PowerShell 5.1.
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest -Uri $m.url -OutFile $zip -UseBasicParsing
    }
    $zipHash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    if ([string]::IsNullOrEmpty($m.zipSha256)) {
        if (-not $Record) { throw "$($m.name): zip hash not recorded. Run once with -Record, review, and commit the lock file." }
        $m.zipSha256 = $zipHash; $changed = $true
    } elseif ($m.zipSha256 -ne $zipHash) {
        throw "$($m.name): zip SHA-256 mismatch (lock $($m.zipSha256), got $zipHash)"
    }

    $dir = Join-Path $cache $m.name
    if (-not (Test-Path $dir)) { Expand-Archive -Path $zip -DestinationPath $dir }
    # The archive layout is not documented, so find the DLC rather than assume a path.
    $dlcs = @(Get-ChildItem -Path $dir -Recurse -Filter '*.dlc')
    if ($dlcs.Count -ne 1) {
        Get-ChildItem -Path $dir -Recurse | ForEach-Object { Write-Host $_.FullName }
        throw "$($m.name): expected exactly one .dlc in the archive, found $($dlcs.Count) (listing above)"
    }
    $dlcHash = (Get-FileHash $dlcs[0].FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    if ([string]::IsNullOrEmpty($m.dlcSha256)) {
        if (-not $Record) { throw "$($m.name): DLC hash not recorded. Run once with -Record." }
        $m.dlcSha256 = $dlcHash; $changed = $true
    } elseif ($m.dlcSha256 -ne $dlcHash) {
        throw "$($m.name): DLC SHA-256 mismatch (lock $($m.dlcSha256), got $dlcHash)"
    }
    Write-Host "$($m.name): OK -> $($dlcs[0].FullName)"
}

if ($changed) {
    $lock | ConvertTo-Json -Depth 5 | Set-Content -Path $lockPath -Encoding UTF8
    Write-Host "Recorded hashes in $lockPath - review and commit it."
}
