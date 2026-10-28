<#
.SYNOPSIS
    Stops the local stack. Local dev only.
.PARAMETER Purge
    Also deletes the Cassandra and Redis data (volumes), after you confirm. The saved dev users and links
    are deleted too, because their database rows are gone.
.EXAMPLE
    ./scripts/Stop-Dev.ps1 -Purge
#>
[CmdletBinding()]
param(
    [switch] $Purge
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'DevCommon.psm1') -Force
$config = Get-DevConfig

# Deleting data is hard to undo, so ask before doing it.
if ($Purge) {
    $answer = Read-Host 'This deletes ALL data in Cassandra and Redis. Type PURGE to continue'
    if ($answer -cne 'PURGE') {
        Write-Host 'Cancelled. Nothing was changed.'
        return
    }
}

# The observability profile is included so its containers are stopped as well.
$composeArgs = @('--profile', 'observability', 'down')
if ($Purge) { $composeArgs += '--volumes' }
Invoke-Compose -Arguments $composeArgs

if ($Purge) {
    foreach ($file in @($config.UsersFile, $config.LinksFile)) {
        if (Test-Path $file) { Remove-Item $file -Force; Write-Host "Deleted $file" }
    }
    Write-Host 'All data deleted.' -ForegroundColor Yellow
} else {
    Write-Host 'Stopped. Data is kept in Docker volumes.'
}
