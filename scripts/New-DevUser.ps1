<#
.SYNOPSIS
    Seeds dev users and API keys directly into Cassandra (v1 has no sign-up API). Local dev only.
.DESCRIPTION
    Stores only the lowercase-hex SHA-256 of each API key in users_by_api_key.
    Raw keys are written to scripts/.dev-users.json (git-ignored) for the other dev scripts and manual testing.
    Compatible with Windows PowerShell 5.1 and PowerShell 7+.
    Cassandra container name: DEV_CASSANDRA_CONTAINER (default urlshortener-cassandra).
#>
[CmdletBinding()]
param(
    [ValidateRange(1, 100)]
    [int] $Count = 1,

    [ValidatePattern('^[A-Za-z0-9._-]+$')]
    [string] $EmailPrefix = 'devuser'
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'DevCommon.psm1') -Force

$config = Get-DevConfig
$existing = @(Read-JsonFile $config.UsersFile)

$created = @()
for ($i = 1; $i -le $Count; $i++) {
    $user = New-DevUserRecord -EmailPrefix $EmailPrefix
    # The hash is not needed outside the database; keep only what other scripts use.
    $created += [pscustomobject]@{ user_id = $user.user_id; email = $user.email; api_key = $user.api_key }
}

Write-JsonFile -Path $config.UsersFile -Items (@($existing) + @($created))

Write-Host "Created $($created.Count) user(s). Keys saved to $($config.UsersFile) (git-ignored)."
$created | Format-Table user_id, email, api_key -AutoSize
