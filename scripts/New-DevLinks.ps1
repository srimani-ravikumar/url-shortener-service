<#
.SYNOPSIS
    Creates short links through the API (POST /api/v1/shorten) for the dev users. Local dev only.
.DESCRIPTION
    Users come from scripts/.dev-users.json (see New-DevUser.ps1) and are used in turn (round-robin).
    Created codes are appended to scripts/.dev-links.json (git-ignored) for Invoke-Traffic.ps1.
.PARAMETER Count
    How many links to create.
.PARAMETER User
    Use only this user: a 0-based index into .dev-users.json, or an email address. Default: all users in turn.
.PARAMETER CustomAliasPrefix
    Give every link a custom alias "<prefix>-<random>" instead of a generated code.
.PARAMETER ExpiresInSeconds
    Give every link this lifetime. Default: the service default (1 year).
.PARAMETER Mix
    Random blend: about 70% default lifetime, 15% short lifetime (5 to 60 minutes), 15% custom alias.
.EXAMPLE
    ./scripts/New-DevLinks.ps1 -Count 50 -Mix
#>
[CmdletBinding()]
param(
    [ValidateRange(1, 100000)]
    [int] $Count = 10,

    [string] $User,

    [ValidatePattern('^[A-Za-z0-9_-]{1,30}$')]
    [string] $CustomAliasPrefix,

    [ValidateRange(1, 630000000)]
    [int] $ExpiresInSeconds,

    [switch] $Mix
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'DevCommon.psm1') -Force
$config = Get-DevConfig

# --- Pick the users ---
$users = @(Get-DevUsers)
if ($User) {
    if ($User -match '^\d+$') {
        if ([int]$User -ge $users.Count) { throw "User index $User does not exist; there are $($users.Count) user(s) (0-based)." }
        $users = @($users[[int]$User])
    } else {
        $users = @($users | Where-Object { $_.email -eq $User })
        if ($users.Count -eq 0) { throw "No dev user with email '$User'." }
    }
}

if (-not (Wait-Healthy -TimeoutSeconds 5)) {
    throw "The service at $($config.BaseUrl) is not ready. Start it with scripts/Start-Dev.ps1."
}

# A request body for link number $n, using this run's options.
function New-LinkRequest([int] $n) {
    $body = @{ long_url = "https://example.com/product/${n}?ref=dev" }

    $kind = 'default'
    if ($Mix) {
        $roll = Get-Random -Maximum 100
        if ($roll -ge 85) { $kind = 'alias' } elseif ($roll -ge 70) { $kind = 'short' }
    }
    if ($CustomAliasPrefix) { $kind = 'alias' }

    if ($kind -eq 'alias') {
        $prefix = if ($CustomAliasPrefix) { $CustomAliasPrefix } else { 'dev' }
        # A random suffix keeps repeated runs from colliding with earlier aliases.
        $body.custom_alias = "$prefix-$(Get-RandomBase62 -Length 8)"
    }
    if ($ExpiresInSeconds) {
        $body.expires_at = [DateTime]::UtcNow.AddSeconds($ExpiresInSeconds).ToString('o')
    } elseif ($kind -eq 'short') {
        $body.expires_at = [DateTime]::UtcNow.AddSeconds((Get-Random -Minimum 300 -Maximum 3601)).ToString('o')
    }
    $body
}

$created = New-Object System.Collections.Generic.List[object]
$summary = [ordered]@{ created = 0; conflicts = 0; rate_limited = 0; errors = 0 }

for ($i = 0; $i -lt $Count; $i++) {
    # Not named $user: PowerShell variable names are case-insensitive, so that would overwrite the -User parameter.
    $owner = $users[$i % $users.Count]
    $body = New-LinkRequest -n ($i + 1)

    # A user may create 100 links per minute; on 429 wait for the bucket to refill (10 tokens every 6 seconds).
    $response = $null
    for ($attempt = 0; $attempt -lt 15; $attempt++) {
        $response = Invoke-Api -Method POST -Path '/api/v1/shorten' -ApiKey $owner.api_key -Body $body
        if ($response.Status -ne 429) { break }
        if ($attempt -eq 0) { $summary.rate_limited++ }
        Start-Sleep -Seconds 1
    }

    switch ($response.Status) {
        201 {
            $summary.created++
            $shortUrl = $response.Json.short_url
            # Taken from the raw text: ConvertFrom-Json would turn the timestamp into a local DateTime.
            $expiresAt = if ($response.Body -match '"expires_at":"([^"]+)"') { $Matches[1] } else { $null }
            $created.Add([pscustomobject]@{
                    short_code   = ($shortUrl -split '/')[-1]
                    short_url    = $shortUrl
                    long_url     = $response.Json.long_url
                    user_id      = $owner.user_id
                    expires_at   = $expiresAt
                    custom_alias = [bool]$body.custom_alias
                })
        }
        409 { $summary.conflicts++ }
        default {
            $summary.errors++
            Write-Warning "Link $($i + 1) failed with status $($response.Status): $($response.Body)"
        }
    }

    if ((($i + 1) % 25) -eq 0) { Write-Host "  $($i + 1) of $Count requested..." }
}

# Keep earlier links too, so several runs build up one pool for Invoke-Traffic.
if ($created.Count -gt 0) {
    $all = New-Object System.Collections.Generic.List[object]
    foreach ($link in (Read-JsonFile $config.LinksFile)) { $all.Add($link) }
    foreach ($link in $created) { $all.Add($link) }
    Write-JsonFile -Path $config.LinksFile -Items $all.ToArray()
}

Write-Host ''
Write-Host "Summary ($($users.Count) user(s)):"
[pscustomobject]$summary | Format-List
Write-Host "Saved $($created.Count) link(s) to $($config.LinksFile) (git-ignored)."
if ($summary.errors -gt 0) { exit 1 }
