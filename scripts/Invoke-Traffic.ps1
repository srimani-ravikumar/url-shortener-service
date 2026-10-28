<#
.SYNOPSIS
    Sends redirect traffic (GET /{code}) and reports status codes and latency. Local dev only.
.DESCRIPTION
    Uses the links saved by New-DevLinks.ps1. Redirects are not followed. Latency is measured by the client, so it
    includes the proxy and (on Docker Desktop for Windows) the network bridge; it is a sanity check against the
    50 ms target, not a benchmark.
.PARAMETER Requests
    Total number of requests.
.PARAMETER Concurrency
    How many requests are in flight at the same time.
.PARAMETER Distribution
    Uniform: every link is equally likely. Skewed: a few hot links get most of the traffic, like real traffic.
.PARAMETER IncludeMisses
    Percentage (0-100) of requests that ask for a code that does not exist (expect 404).
.EXAMPLE
    ./scripts/Invoke-Traffic.ps1 -Requests 1000 -Concurrency 20 -Distribution Skewed -IncludeMisses 5
#>
[CmdletBinding()]
param(
    [ValidateRange(1, 1000000)]
    [int] $Requests = 1000,

    [ValidateRange(1, 200)]
    [int] $Concurrency = 10,

    [ValidateSet('Uniform', 'Skewed')]
    [string] $Distribution = 'Uniform',

    [ValidateRange(0, 100)]
    [int] $IncludeMisses = 0
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'DevCommon.psm1') -Force
$config = Get-DevConfig

$links = @(Read-JsonFile $config.LinksFile)
if ($links.Count -eq 0) {
    throw "No links found. Run scripts/New-DevLinks.ps1 first (expected file: $($config.LinksFile))."
}
if (-not (Wait-Healthy -TimeoutSeconds 5)) {
    throw "The service at $($config.BaseUrl) is not ready. Start it with scripts/Start-Dev.ps1."
}

# --- Decide up front which code each request asks for ---
$codes = @($links | ForEach-Object { $_.short_code })
$queue = New-Object 'System.Collections.Concurrent.ConcurrentQueue[string]'
for ($i = 0; $i -lt $Requests; $i++) {
    if ((Get-Random -Maximum 100) -lt $IncludeMisses) {
        $queue.Enqueue((Get-RandomBase62))
    } elseif ($Distribution -eq 'Skewed') {
        # A uniform number cubed is heavily bunched near 0, so the first few links get roughly half of all requests.
        $position = [int][math]::Floor($codes.Count * [math]::Pow((Get-Random -Minimum 0.0 -Maximum 1.0), 3))
        $queue.Enqueue($codes[$position])
    } else {
        $queue.Enqueue($codes[(Get-Random -Maximum $codes.Count)])
    }
}

# --- Worker: takes codes off the queue until it is empty and records status and time ---
# Runs in its own runspace, so everything it needs is passed in. (ForEach-Object -Parallel needs PowerShell 7.)
$worker = {
    param($client, $baseUrl, $queue, $results)
    $code = $null
    while ($queue.TryDequeue([ref]$code)) {
        $timer = [System.Diagnostics.Stopwatch]::StartNew()
        $status = 0 # 0 means "no answer" (connection error or timeout)
        try {
            $response = $client.GetAsync("$baseUrl/$code").GetAwaiter().GetResult()
            $status = [int]$response.StatusCode
            $response.Dispose()
        } catch { }
        $timer.Stop()
        $results.Enqueue([pscustomobject]@{ Status = $status; Ms = $timer.Elapsed.TotalMilliseconds })
    }
}

$results = New-Object 'System.Collections.Concurrent.ConcurrentQueue[object]'
$pool = [runspacefactory]::CreateRunspacePool(1, $Concurrency)
$pool.Open()
$jobs = @()
$clock = [System.Diagnostics.Stopwatch]::StartNew()
try {
    for ($w = 0; $w -lt $Concurrency; $w++) {
        $shell = [powershell]::Create()
        $shell.RunspacePool = $pool
        [void]$shell.AddScript($worker).AddArgument((Get-DevHttpClient)).AddArgument($config.BaseUrl).AddArgument($queue).AddArgument($results)
        $jobs += [pscustomobject]@{ Shell = $shell; Handle = $shell.BeginInvoke() }
    }
    foreach ($job in $jobs) { [void]$job.Shell.EndInvoke($job.Handle) }
}
finally {
    foreach ($job in $jobs) { $job.Shell.Dispose() }
    $pool.Close()
    $pool.Dispose()
}
$clock.Stop()

# --- Report ---
$all = @($results.ToArray())
$times = @($all | ForEach-Object { $_.Ms } | Sort-Object)

# Nearest-rank percentile of the sorted list.
function Get-Percentile([double[]] $sorted, [double] $percent) {
    $index = [math]::Ceiling($percent / 100 * $sorted.Count) - 1
    $sorted[[math]::Max(0, [int]$index)]
}

$seconds = [math]::Max($clock.Elapsed.TotalSeconds, 0.001)
Write-Host ''
Write-Host ('{0} requests in {1:N1} s ({2:N0} requests/s), concurrency {3}, {4} distribution, {5} link(s), {6}% misses' -f `
        $all.Count, $seconds, ($all.Count / $seconds), $Concurrency, $Distribution, $codes.Count, $IncludeMisses)

Write-Host ''
Write-Host 'Status codes:'
$all | Group-Object Status | Sort-Object { [int]$_.Name } |
    ForEach-Object { '  {0,-4} {1}' -f $(if ($_.Name -eq '0') { 'none' } else { $_.Name }), $_.Count } | Write-Host

if ($times.Count -gt 0) {
    $p99 = Get-Percentile $times 99
    Write-Host ''
    Write-Host 'Latency (client side, ms):'
    Write-Host ('  min {0:N1}   p50 {1:N1}   p95 {2:N1}   p99 {3:N1}   max {4:N1}' -f `
            $times[0], (Get-Percentile $times 50), (Get-Percentile $times 95), $p99, $times[-1])
    if ($p99 -lt 50) {
        Write-Result -Status PASS -Name 'p99 latency' -Detail ('{0:N1} ms is under the 50 ms target' -f $p99)
    } else {
        # Only a hint: client-side numbers on a laptop (Docker Desktop, PowerShell threads) are noisy, so this is no failure.
        Write-Host ('[NOTE] p99 {0:N1} ms is over the 50 ms target. The first requests are slower (cold caches); run again to compare.' -f $p99) -ForegroundColor Yellow
    }
}
