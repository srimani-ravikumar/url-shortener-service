<#
.SYNOPSIS
    Starts the local stack with Docker Compose and waits until it is healthy. Local dev only.
.PARAMETER InfraOnly
    Start only Cassandra (with its schema) and Redis, e.g. to run the services from your IDE or run the integration tests.
.PARAMETER Build
    Rebuild the service images first (needed after code changes).
.PARAMETER Observability
    Also start the Grafana stack and make the services send telemetry to it.
.PARAMETER Scale
    Number of redirector replicas (they share the load behind Nginx).
.EXAMPLE
    ./scripts/Start-Dev.ps1 -Build -Scale 3
#>
[CmdletBinding()]
param(
    [switch] $InfraOnly,
    [switch] $Build,
    [switch] $Observability,
    [ValidateRange(1, 10)]
    [int] $Scale = 1
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'DevCommon.psm1') -Force
$config = Get-DevConfig

# 1. Docker must be running.
try { [void](Invoke-Docker -Arguments @('info')) }
catch { throw 'Docker is not running (or not installed). Start Docker Desktop and try again.' }

# 2. The shortener refuses to start without a secret, so create deploy/.env on first use.
if (-not $InfraOnly) {
    if (-not (Test-Path $config.EnvFile)) {
        Copy-Item $config.EnvExampleFile $config.EnvFile
        Write-Host "Created $($config.EnvFile) from the example."
    }
    $lines = @(Get-Content $config.EnvFile)
    if (-not ($lines | Where-Object { $_ -match '^FEISTEL_SECRET=.+' })) {
        $bytes = New-Object byte[] 32
        $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
        try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
        $secret = [Convert]::ToBase64String($bytes)
        $lines = $lines | ForEach-Object { if ($_ -match '^FEISTEL_SECRET=\s*$') { "FEISTEL_SECRET=$secret" } else { $_ } }
        if (-not ($lines | Where-Object { $_ -match '^FEISTEL_SECRET=' })) { $lines += "FEISTEL_SECRET=$secret" }
        [System.IO.File]::WriteAllLines($config.EnvFile, [string[]]$lines)
        Write-Host 'Generated a FEISTEL_SECRET in deploy/.env. Keep it: changing it later can make new codes collide with old ones.'
    }
}

# 3. Build the "docker compose up" command.
$infraServices = @('cassandra', 'cassandra-init', 'redis')
$observabilityServices = @('otel-collector', 'tempo', 'loki', 'prometheus', 'grafana')

$composeArgs = @()
if ($Observability) {
    $composeArgs += @('--profile', 'observability')
    # A process environment variable wins over deploy/.env, so telemetry is on for this run only.
    $env:OTEL_EXPORTER_OTLP_ENDPOINT = 'http://otel-collector:4317'
}
$composeArgs += @('up', '-d')
if ($Build) { $composeArgs += '--build' }
if (-not $InfraOnly) { $composeArgs += @('--scale', "redirector-api=$Scale") }
if ($InfraOnly) {
    $composeArgs += $infraServices
    if ($Observability) { $composeArgs += $observabilityServices }
}

Write-Host "Starting: docker compose $($composeArgs -join ' ')"
Invoke-Compose -Arguments $composeArgs

# 4. Wait for everything to be healthy.
Write-Host 'Waiting for the services to become healthy...'
foreach ($container in @($config.CassandraContainer, $config.RedisContainer)) {
    if (-not (Wait-ContainerHealthy -Container $container)) { throw "$container did not become healthy in time." }
}

# The schema job runs once and exits; the services cannot work before it has finished.
$deadline = (Get-Date).AddSeconds(120)
do {
    $state = (Invoke-Docker -Arguments @('inspect', '--format', '{{.State.Status}} {{.State.ExitCode}}', 'urlshortener-cassandra-init')) -join ''
    if ($state -eq 'exited 0') { break }
    Start-Sleep -Seconds 2
} while ((Get-Date) -lt $deadline)
if ($state -ne 'exited 0') { throw "The Cassandra schema job did not finish successfully (state: $state)." }

if (-not $InfraOnly) {
    foreach ($service in @('shortener-api', 'redirector-api', 'analytics-worker', 'web')) {
        foreach ($id in (Get-ServiceContainer -Service $service)) {
            if (-not (Wait-ContainerHealthy -Container $id -TimeoutSeconds 120)) { throw "$service ($id) did not become healthy in time." }
        }
    }
    if (-not (Wait-ContainerHealthy -Container 'urlshortener-mailpit' -TimeoutSeconds 60)) { throw 'The MailPit container did not become healthy in time.' }
    if (-not (Wait-Healthy -TimeoutSeconds 60)) { throw "$($config.BaseUrl)/health/ready did not answer 200 in time." }
}

# 5. Where things are.
Write-Host ''
Write-Host 'Ready.' -ForegroundColor Green
if ($InfraOnly) {
    Write-Host '  Cassandra  localhost:9042'
    Write-Host '  Redis      localhost:6379'
} else {
    Write-Host "  Entry point   $($config.BaseUrl)   (POST /api/v1/shorten, GET /{code}, GET /health/ready)"
    Write-Host "  Web app       $($config.BaseUrl)/app/"
    Write-Host '  Mail inbox    http://localhost:8025   (MailPit: sign-up and recovery mails arrive here)'
    Write-Host "  Redirectors   $Scale replica(s)"
}
if ($Observability) {
    Write-Host '  Grafana       http://localhost:3000   (admin / admin)'
    Write-Host '  Prometheus    http://localhost:9090'
}
