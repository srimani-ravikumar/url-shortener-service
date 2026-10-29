<#
.SYNOPSIS
    Runs end-to-end checks ("scenarios") against the running stack. Exits with 1 if any check fails, so CI can reuse it.
.DESCRIPTION
    Each scenario creates its own users and links, so scenarios are independent and can be repeated. The data they
    create is deleted afterwards (use -KeepData to keep it).
    RedisOutage, CounterReseed and Durability disturb the running stack, so they run only when named, or with -All -IncludeDisruptive.
    LoadBalancing needs several redirector replicas (Start-Dev.ps1 -Scale 3) and is skipped otherwise.
    Compatible with Windows PowerShell 5.1 and PowerShell 7+.
    Durability restarts Redis and Cassandra, so it takes a minute or two.
.PARAMETER Name
    One or more scenarios to run.
.PARAMETER All
    Run every scenario except the disruptive ones.
.PARAMETER IncludeDisruptive
    With -All: also run RedisOutage and CounterReseed.
.PARAMETER FlushIntervalSeconds
    The analytics worker's flush interval (default 10 s); used to decide how long to wait for click counts.
.EXAMPLE
    ./scripts/Invoke-Scenario.ps1 -All
    ./scripts/Invoke-Scenario.ps1 -Name HappyPath, Auth
#>
[CmdletBinding(PositionalBinding = $false)]
param(
    # Names are checked below (not with ValidateSet) so "-Name A,B" also works when started with "powershell -File".
    [string[]] $Name,

    [switch] $All,
    [switch] $IncludeDisruptive,
    [switch] $KeepData,

    [ValidateRange(1, 300)]
    [int] $FlushIntervalSeconds = 10
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'DevCommon.psm1') -Force
$config = Get-DevConfig

$allScenarios = @('HappyPath', 'CustomAlias', 'Validation', 'DefaultTtl', 'Expiration', 'Auth', 'NotFound', 'RateLimit',
    'ClickCounting', 'CacheConsistency', 'UserLinks', 'LoadBalancing', 'Signup', 'SignupEnumeration', 'Rotate', 'Recover',
    'DeleteAccount')
$disruptiveScenarios = @('RedisOutage', 'CounterReseed', 'Durability')

if (-not $Name -and -not $All) {
    throw "Choose scenarios with -Name <name>[,<name>...] or run them all with -All. Available: $(($allScenarios + $disruptiveScenarios) -join ', ')."
}

$Name = @($Name | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
foreach ($requested in $Name) {
    if (($allScenarios + $disruptiveScenarios) -notcontains $requested) {
        throw "Unknown scenario '$requested'. Available: $(($allScenarios + $disruptiveScenarios) -join ', ')."
    }
}

# ---------------------------------------------------------------------------------------------------------------
# Bookkeeping: results, and everything the scenarios create (so it can be deleted afterwards)
# ---------------------------------------------------------------------------------------------------------------
$script:Results = New-Object System.Collections.Generic.List[object]
$script:CurrentScenario = ''
$script:CreatedUsers = New-Object System.Collections.Generic.List[object]
$script:CreatedCodes = New-Object System.Collections.Generic.List[string]

function Add-Result([string] $Check, [string] $Status, [string] $Detail) {
    $script:Results.Add([pscustomobject]@{ Scenario = $script:CurrentScenario; Check = $Check; Status = $Status; Detail = $Detail })
    Write-Result -Status $Status -Name "$($script:CurrentScenario): $Check" -Detail $Detail
}

function Assert-Equal([string] $Check, $Actual, $Expected) {
    if ("$Actual" -eq "$Expected") { Add-Result $Check 'PASS' "$Actual" }
    else { Add-Result $Check 'FAIL' "expected $Expected, got $Actual" }
}

function Assert-True([string] $Check, [bool] $Condition, [string] $Detail = '') {
    $status = if ($Condition) { 'PASS' } else { 'FAIL' }
    Add-Result $Check $status $Detail
}

function Skip-Scenario([string] $Reason) { Add-Result 'skipped' 'SKIP' $Reason }

# ---------------------------------------------------------------------------------------------------------------
# Helpers shared by the scenarios
# ---------------------------------------------------------------------------------------------------------------
function New-ScenarioUser {
    $user = New-DevUserRecord -EmailPrefix 'scenario'
    $script:CreatedUsers.Add($user)
    $user
}

# Creates a link and remembers its code for cleanup. Returns the response and the code (null unless it was created).
function New-ShortLink($User, [string] $LongUrl = "https://example.com/scenario/$(Get-RandomBase62)", [string] $Alias, [string] $ExpiresAt) {
    $body = @{ long_url = $LongUrl }
    if ($Alias) { $body.custom_alias = $Alias }
    if ($ExpiresAt) { $body.expires_at = $ExpiresAt }

    $response = Invoke-Api -Method POST -Path '/api/v1/shorten' -ApiKey $User.api_key -Body $body
    $code = $null
    if ($response.Status -eq 201) {
        $code = ($response.Json.short_url -split '/')[-1]
        $script:CreatedCodes.Add($code)
    }
    [pscustomobject]@{ Response = $response; Code = $code }
}

# Reads a string field straight from the JSON text (ConvertFrom-Json would turn timestamps into local DateTimes).
function Get-RawJsonString([string] $Body, [string] $Field) {
    if ($Body -match ('"' + $Field + '":"([^"]*)"')) { $Matches[1] }
}

# Click counts are written by the worker every flush interval, so poll until the expected count shows up.
function Wait-ForClicks($User, [string] $Code, [long] $Expected) {
    $deadline = (Get-Date).AddSeconds($FlushIntervalSeconds * 3 + 10)
    do {
        $response = Invoke-Api -Method GET -Path "/api/v1/analytics/$Code" -ApiKey $User.api_key
        if ($response.Status -eq 200 -and [long]$response.Json.total_clicks -ge $Expected) { return $response.Json }
        Start-Sleep -Seconds 2
    } while ((Get-Date) -lt $deadline)
    $response.Json
}

function Get-IsoTime([double] $SecondsFromNow) { [DateTime]::UtcNow.AddSeconds($SecondsFromNow).ToString('o') }

# ---------------------------------------------------------------------------------------------------------------
# Scenarios
# ---------------------------------------------------------------------------------------------------------------
function Test-HappyPath {
    $user = New-ScenarioUser
    $longUrl = 'https://example.com/happy-path?ref=scenario'

    $link = New-ShortLink -User $user -LongUrl $longUrl
    Assert-Equal 'shorten returns 201' $link.Response.Status 201
    if (-not $link.Code) { return }

    $redirect = Invoke-Api -Method GET -Path "/$($link.Code)"
    Assert-Equal 'redirect returns 302' $redirect.Status 302
    Assert-Equal 'Location is the long URL' $redirect.Headers['location'] $longUrl

    $analytics = Wait-ForClicks -User $user -Code $link.Code -Expected 1
    Assert-Equal 'total_clicks after one flush interval' $analytics.total_clicks 1
}

function Test-CustomAlias {
    $user = New-ScenarioUser
    $alias = "alias-$(Get-RandomBase62 -Length 8)"

    Assert-Equal 'new alias returns 201' (New-ShortLink -User $user -Alias $alias).Response.Status 201
    Assert-Equal 'same alias again returns 409' (New-ShortLink -User $user -Alias $alias).Response.Status 409
    # Aliases are case-sensitive, so this is a different code.
    Assert-Equal 'same alias in other case returns 201' (New-ShortLink -User $user -Alias $alias.ToUpperInvariant()).Response.Status 201
    Assert-Equal 'reserved word "admin" returns 400' (New-ShortLink -User $user -Alias 'admin').Response.Status 400
    Assert-Equal 'reserved word "Admin" returns 400' (New-ShortLink -User $user -Alias 'Admin').Response.Status 400
    Assert-Equal 'invalid characters return 400' (New-ShortLink -User $user -Alias 'bad alias!').Response.Status 400
    Assert-Equal 'too short alias returns 400' (New-ShortLink -User $user -Alias 'ab').Response.Status 400
}

function Test-Validation {
    $user = New-ScenarioUser

    # The short domain is learned from a real link, so the check works however the service is configured.
    $probe = New-ShortLink -User $user
    $shortBase = $probe.Response.Json.short_url -replace '/[^/]+$', ''

    $cases = [ordered]@{
        'non-http URL'                  = @{ long_url = 'ftp://example.com/file' }
        'relative URL'                  = @{ long_url = '/just/a/path' }
        'URL longer than 2048'          = @{ long_url = 'https://example.com/' + ('a' * 2100) }
        'URL pointing at the short domain' = @{ long_url = "$shortBase/abc" }
        'expires_at in the past'        = @{ long_url = 'https://example.com/a'; expires_at = (Get-IsoTime -3600) }
        'expires_at more than 20 years ahead' = @{ long_url = 'https://example.com/a'; expires_at = [DateTime]::UtcNow.AddYears(21).ToString('o') }
    }
    foreach ($case in $cases.Keys) {
        $response = Invoke-Api -Method POST -Path '/api/v1/shorten' -ApiKey $user.api_key -Body $cases[$case]
        Assert-Equal "$case returns 400" $response.Status 400
    }
}

function Test-DefaultTtl {
    $user = New-ScenarioUser
    $longUrl = 'https://example.com/default-ttl'

    $first = New-ShortLink -User $user -LongUrl $longUrl
    $second = New-ShortLink -User $user -LongUrl $longUrl
    Assert-Equal 'first shorten returns 201' $first.Response.Status 201
    Assert-Equal 'second shorten returns 201' $second.Response.Status 201

    $expiresAt = [DateTimeOffset]::Parse((Get-RawJsonString $first.Response.Body 'expires_at'), [System.Globalization.CultureInfo]::InvariantCulture)
    $expected = [DateTimeOffset]::UtcNow.AddDays(365)
    $difference = [math]::Abs(($expiresAt - $expected).TotalMinutes)
    Assert-True 'expires_at is about one year from now' ($difference -lt 5) ("expires_at $($expiresAt.ToString('u')), off by {0:N1} min" -f $difference)
    Assert-True 'the same long URL gets two different codes' ($first.Code -and $second.Code -and $first.Code -ne $second.Code) "$($first.Code) / $($second.Code)"
}

function Test-Expiration {
    $user = New-ScenarioUser
    $link = New-ShortLink -User $user -ExpiresAt (Get-IsoTime 5)
    Assert-Equal 'shorten with a 5 s lifetime returns 201' $link.Response.Status 201
    if (-not $link.Code) { return }

    Assert-Equal 'redirect before expiry returns 302' (Invoke-Api -Method GET -Path "/$($link.Code)").Status 302
    Start-Sleep -Seconds 6
    Assert-Equal 'redirect after expiry returns 410' (Invoke-Api -Method GET -Path "/$($link.Code)").Status 410
}

function Test-Auth {
    $userA = New-ScenarioUser
    $userB = New-ScenarioUser
    $link = New-ShortLink -User $userA
    if (-not $link.Code) { Add-Result 'setup: user A creates a link' 'FAIL' "status $($link.Response.Status)"; return }

    Assert-Equal 'no API key returns 401' (Invoke-Api -Method GET -Path "/api/v1/analytics/$($link.Code)").Status 401
    Assert-Equal 'invalid API key returns 401' (Invoke-Api -Method GET -Path "/api/v1/analytics/$($link.Code)" -ApiKey "invalid-$(Get-RandomBase62)").Status 401
    Assert-Equal 'owner reads own analytics (control)' (Invoke-Api -Method GET -Path "/api/v1/analytics/$($link.Code)" -ApiKey $userA.api_key).Status 200
    Assert-Equal "user B reading user A's analytics returns 403" (Invoke-Api -Method GET -Path "/api/v1/analytics/$($link.Code)" -ApiKey $userB.api_key).Status 403
    Assert-Equal "user B listing user A's links returns 403" (Invoke-Api -Method GET -Path "/api/v1/users/$($userA.user_id)/urls" -ApiKey $userB.api_key).Status 403
}

function Test-NotFound {
    Assert-Equal 'unknown code returns 404' (Invoke-Api -Method GET -Path "/$(Get-RandomBase62)").Status 404
}

function Test-RateLimit {
    $userX = New-ScenarioUser
    $userY = New-ScenarioUser

    # The bucket holds 100 tokens and refills slowly, so a quick burst of 130 must run into the limit.
    $counts = @{}
    for ($i = 0; $i -lt 130; $i++) {
        $status = (New-ShortLink -User $userX).Response.Status
        $counts["$status"] = 1 + [int]$counts["$status"]
    }
    $summary = ($counts.Keys | Sort-Object | ForEach-Object { "$_ x$($counts[$_])" }) -join ', '
    Assert-True 'a burst of 130 requests gets at least one 429' ($counts.ContainsKey('429')) $summary
    Assert-Equal 'a second key is unaffected' (New-ShortLink -User $userY).Response.Status 201
}

function Test-ClickCounting {
    $user = New-ScenarioUser
    $counted = New-ShortLink -User $user
    $expiring = New-ShortLink -User $user -ExpiresAt (Get-IsoTime 4)
    if (-not $counted.Code -or -not $expiring.Code) { Add-Result 'setup: create links' 'FAIL' 'could not create the links'; return }

    $statuses = 1..7 | ForEach-Object { (Invoke-Api -Method GET -Path "/$($counted.Code)").Status }
    Assert-True '7 redirects return 302' (@($statuses | Where-Object { $_ -ne 302 }).Count -eq 0) ($statuses -join ',')
    Assert-Equal 'one redirect of the soon-to-expire link returns 302' (Invoke-Api -Method GET -Path "/$($expiring.Code)").Status 302

    $misses = 1..2 | ForEach-Object { (Invoke-Api -Method GET -Path "/$(Get-RandomBase62)").Status }
    Assert-True '2 unknown codes return 404' (@($misses | Where-Object { $_ -ne 404 }).Count -eq 0) ($misses -join ',')

    Start-Sleep -Seconds 5
    $gone = 1..2 | ForEach-Object { (Invoke-Api -Method GET -Path "/$($expiring.Code)").Status }
    Assert-True '2 redirects after expiry return 410' (@($gone | Where-Object { $_ -ne 410 }).Count -eq 0) ($gone -join ',')

    [void](Wait-ForClicks -User $user -Code $counted.Code -Expected 7)
    [void](Wait-ForClicks -User $user -Code $expiring.Code -Expected 1)
    # One more flush interval: if 404s or 410s were counted, they would show up by now.
    Start-Sleep -Seconds ($FlushIntervalSeconds + 3)

    $analytics = (Invoke-Api -Method GET -Path "/api/v1/analytics/$($counted.Code)" -ApiKey $user.api_key).Json
    $expiredAnalytics = (Invoke-Api -Method GET -Path "/api/v1/analytics/$($expiring.Code)" -ApiKey $user.api_key).Json
    Assert-Equal 'total_clicks counts only the 7 successful redirects' $analytics.total_clicks 7
    Assert-True 'last_accessed is set' ($null -ne $analytics.last_accessed) "$($analytics.last_accessed)"
    Assert-Equal '410 responses are not counted' $expiredAnalytics.total_clicks 1
}

function Test-CacheConsistency {
    $user = New-ScenarioUser
    $shortLived = New-ShortLink -User $user -ExpiresAt (Get-IsoTime 3600)
    $default = New-ShortLink -User $user
    if (-not $shortLived.Code -or -not $default.Code) { Add-Result 'setup: create links' 'FAIL' 'could not create the links'; return }

    foreach ($code in @($shortLived.Code, $default.Code)) {
        1..2 | ForEach-Object { [void](Invoke-Api -Method GET -Path "/$code") }
    }

    # The cache is written in the background, so give it a moment.
    $ttls = @{}
    foreach ($code in @($shortLived.Code, $default.Code)) {
        $ttl = -1
        for ($attempt = 0; $attempt -lt 10 -and $ttl -lt 0; $attempt++) {
            $ttl = [int](Invoke-Redis ttl "url:$code" | Select-Object -First 1)
            if ($ttl -lt 0) { Start-Sleep -Milliseconds 500 }
        }
        $ttls[$code] = $ttl
    }

    $short = $ttls[$shortLived.Code]
    $long = $ttls[$default.Code]
    Assert-True 'cached key exists for the 1-hour link, with a TTL of at most 1 hour' ($short -gt 3000 -and $short -le 3600) "ttl $short s"
    Assert-True 'cached key exists for the 1-year link, with a TTL of at most 24 hours' ($long -gt 86000 -and $long -le 86400) "ttl $long s"
}

function Test-UserLinks {
    $userA = New-ScenarioUser
    $userB = New-ScenarioUser

    $codesA = @()
    foreach ($i in 1..5) {
        $codesA += (New-ShortLink -User $userA).Code
        Start-Sleep -Milliseconds 30 # keep the creation times apart so "newest first" is unambiguous
    }
    1..2 | ForEach-Object { [void](New-ShortLink -User $userB) }

    $items = @()
    $pages = 0
    $token = $null
    do {
        $path = "/api/v1/users/$($userA.user_id)/urls?page_size=2"
        if ($token) { $path += "&page_token=$([uri]::EscapeDataString($token))" }
        $response = Invoke-Api -Method GET -Path $path -ApiKey $userA.api_key
        if ($response.Status -ne 200) { Add-Result 'list page returns 200' 'FAIL' "status $($response.Status)"; return }
        $pages++
        $items += @($response.Json.items)
        $token = $response.Json.next_page_token
        if ($pages -eq 1) { Assert-Equal 'first page has 2 items' @($response.Json.items).Count 2 }
    } while ($token -and $pages -lt 10)

    Assert-Equal 'all links are returned over 3 pages' "$($items.Count) in $pages" '5 in 3'
    Assert-True 'only the user''s own links are listed' (@($items | Where-Object { $codesA -notcontains $_.short_code }).Count -eq 0)

    $newestFirst = $true
    for ($i = 1; $i -lt $items.Count; $i++) {
        if ([DateTime]$items[$i].created_at -gt [DateTime]$items[$i - 1].created_at) { $newestFirst = $false }
    }
    Assert-True 'links are newest first' ($newestFirst -and $items[0].short_code -eq $codesA[-1])
}

function Test-LoadBalancing {
    $replicas = @(Get-ServiceContainer -Service 'redirector-api')
    if ($replicas.Count -lt 2) { Skip-Scenario 'only one redirector replica is running; start with Start-Dev.ps1 -Scale 3'; return }

    $user = New-ScenarioUser
    $link = New-ShortLink -User $user
    if (-not $link.Code) { Add-Result 'setup: create link' 'FAIL' "status $($link.Response.Status)"; return }

    # Nginx looks up replicas every few seconds, so allow a few rounds for it to find all of them.
    $seen = @()
    for ($round = 0; $round -lt 4 -and $seen.Count -lt $replicas.Count; $round++) {
        $names = 1..60 | ForEach-Object { (Invoke-Api -Method GET -Path "/$($link.Code)").Headers['x-served-by'] }
        $seen = @($names | Where-Object { $_ } | Sort-Object -Unique)
        if ($seen.Count -eq 0) { break }
        if ($seen.Count -lt $replicas.Count) { Start-Sleep -Seconds 6 }
    }
    if ($seen.Count -eq 0) {
        Add-Result 'X-Served-By header is present' 'FAIL' 'no header; rebuild the redirector image (Start-Dev.ps1 -Build)'
        return
    }
    Assert-Equal "requests reach all $($replicas.Count) replicas" $seen.Count $replicas.Count
}

function Test-RedisOutage {
    $user = New-ScenarioUser
    $link = New-ShortLink -User $user
    if (-not $link.Code) { Add-Result 'setup: create link' 'FAIL' "status $($link.Response.Status)"; return }

    $redis = $config.RedisContainer
    try {
        [void](Invoke-Docker -Arguments @('stop', $redis))
        Start-Sleep -Seconds 1
        Assert-Equal 'redirect still returns 302 without Redis' (Invoke-Api -Method GET -Path "/$($link.Code)").Status 302
        Assert-Equal 'shorten returns 503 without Redis' (New-ShortLink -User $user).Response.Status 503
    }
    finally {
        # Always bring Redis back, even if a check above threw.
        [void](Invoke-Docker -Arguments @('start', $redis))
        [void](Wait-ContainerHealthy -Container $redis -TimeoutSeconds 60)
    }

    # The services reconnect in the background, so allow some time.
    $status = 0
    $deadline = (Get-Date).AddSeconds(60)
    do {
        $status = (New-ShortLink -User $user).Response.Status
        if ($status -ne 201) { Start-Sleep -Seconds 2 }
    } while ($status -ne 201 -and (Get-Date) -lt $deadline)
    Assert-Equal 'shorten works again after Redis is back' $status 201
}

function Get-CheckpointValue {
    $row = Invoke-Cql "SELECT value FROM id_counter_checkpoint WHERE name = 'short_code';" | Where-Object { $_ -match '^\s*\d+\s*$' } | Select-Object -First 1
    if ($row) { [long]$row.Trim() } else { [long]0 }
}

function Test-CounterReseed {
    $user = New-ScenarioUser
    $before = New-ShortLink -User $user
    if (-not $before.Code) { Add-Result 'setup: create link' 'FAIL' "status $($before.Response.Status)"; return }

    # After losing the counter the service restarts from the last saved checkpoint, so wait for the worker to save one.
    $counter = [long](Invoke-Redis get 'id:counter' | Select-Object -First 1)
    $deadline = (Get-Date).AddSeconds(60)
    while ((Get-CheckpointValue) -lt $counter -and (Get-Date) -lt $deadline) { Start-Sleep -Seconds 3 }
    if ((Get-CheckpointValue) -lt $counter) {
        Add-Result 'worker saved a checkpoint' 'FAIL' 'no up-to-date checkpoint after 60 s (is the analytics worker running?)'
        return
    }

    [void](Invoke-Redis del 'id:counter')
    Invoke-Compose -Arguments @('restart', 'shortener-api')
    foreach ($id in (Get-ServiceContainer -Service 'shortener-api')) { [void](Wait-ContainerHealthy -Container $id -TimeoutSeconds 90) }

    $links = 1..10 | ForEach-Object { New-ShortLink -User $user }
    $failed = @($links | Where-Object { $_.Response.Status -ne 201 })
    $codes = @($links | ForEach-Object { $_.Code } | Where-Object { $_ })
    Assert-Equal 'all 10 new links are created (no 500s)' $failed.Count 0
    Assert-True 'codes are unique, and different from the one made before' (($codes + $before.Code | Sort-Object -Unique).Count -eq ($codes.Count + 1)) "$($codes.Count) new codes"

    $after = [long](Invoke-Redis get 'id:counter' | Select-Object -First 1)
    Assert-True 'the counter was re-seeded ahead of its old value' ($after -gt $counter) "before $counter, after $after"
}

function Test-Durability {
    $user = New-ScenarioUser
    $link = New-ShortLink -User $user -LongUrl 'https://example.com/durability'
    if (-not $link.Code) { Add-Result 'setup: create link' 'FAIL' "status $($link.Response.Status)"; return }

    Assert-Equal 'redirect before the restart returns 302' (Invoke-Api -Method GET -Path "/$($link.Code)").Status 302
    $before = Wait-ForClicks -User $user -Code $link.Code -Expected 1
    Assert-Equal 'the click is stored in Cassandra before the restart' $before.total_clicks 1

    foreach ($container in @($config.RedisContainer, $config.CassandraContainer)) {
        [void](Invoke-Docker -Arguments @('restart', $container))
    }
    foreach ($container in @($config.RedisContainer, $config.CassandraContainer)) {
        if (-not (Wait-ContainerHealthy -Container $container -TimeoutSeconds 180)) {
            Add-Result "$container healthy again" 'FAIL' 'not healthy after 180 s'
            return
        }
    }

    # Remove the cached copy so the answer has to come from Cassandra's own data.
    [void](Invoke-Redis del "url:$($link.Code)")

    # The services reconnect in the background, so allow some time.
    $redirect = $null
    $deadline = (Get-Date).AddSeconds(120)
    do {
        try { $redirect = Invoke-Api -Method GET -Path "/$($link.Code)" } catch { $redirect = $null }
        if (-not $redirect -or $redirect.Status -ne 302) { Start-Sleep -Seconds 3 }
    } while ((-not $redirect -or $redirect.Status -ne 302) -and (Get-Date) -lt $deadline)

    Assert-Equal 'the link still redirects after restarting Redis and Cassandra' $redirect.Status 302
    Assert-Equal 'Location is unchanged' $redirect.Headers['location'] 'https://example.com/durability'

    $after = Invoke-Api -Method GET -Path "/api/v1/analytics/$($link.Code)" -ApiKey $user.api_key
    Assert-Equal 'the user and the link are still known (analytics returns 200)' $after.Status 200
    Assert-True 'the stored click count survived' ([long]$after.Json.total_clicks -ge 1) "total_clicks $($after.Json.total_clicks)"
}

# ---------------------------------------------------------------------------------------------------------------
# Accounts: these read the mails from MailPit (http://localhost:8025 in the local stack)
# ---------------------------------------------------------------------------------------------------------------
function New-UniqueEmail([string] $Prefix) { "$Prefix-$((Get-RandomBase62 -Length 10).ToLowerInvariant())@example.com" }

function Request-Mail([string] $Path, [string] $Email) {
    Invoke-Api -Method POST -Path $Path -Body @{ email = $Email }
}

# Spends an emailed token. Returns the response; the credentials are in .Json.
function Use-Token([string] $Token) {
    Invoke-Api -Method POST -Path '/api/v1/verify' -Body @{ token = $Token }
}

# Remembers an account made through the API, so cleanup can delete its rows.
function Add-CreatedUser($Credentials) {
    $user = [pscustomobject]@{
        user_id      = $Credentials.user_id
        email        = $Credentials.email
        api_key      = $Credentials.api_key
        api_key_hash = (Get-Sha256Hex $Credentials.api_key)
    }
    $script:CreatedUsers.Add($user)
    $user
}

# After a key replacement the old hash is gone; keep the record current so cleanup removes the right row.
function Update-UserKey($User, [string] $NewKey) {
    $User.api_key = $NewKey
    $User.api_key_hash = Get-Sha256Hex $NewKey
}

function Test-Signup {
    $email = New-UniqueEmail 'signup'

    Assert-Equal 'signup returns 202' (Request-Mail '/api/v1/signup' $email).Status 202
    $mail = Wait-ForMail -To $email
    Assert-True 'a verification mail arrives' ($null -ne $mail)
    if (-not $mail) { return }
    Assert-True 'the link opens the web app and keeps the token in the fragment' ($mail.Text -match '/app/verify#token=') 

    $verify = Use-Token $mail.Token
    Assert-Equal 'verify returns 200' $verify.Status 200
    if ($verify.Status -ne 200) { return }
    $user = Add-CreatedUser $verify.Json
    Assert-Equal 'the response is marked no-store' $verify.Headers['cache-control'] 'no-store'

    $me = Invoke-Api -Method GET -Path '/api/v1/users/me' -ApiKey $user.api_key
    Assert-Equal 'the new key works (GET /users/me returns 200)' $me.Status 200
    Assert-Equal 'the account has the signed-up email' $me.Json.email $email

    Assert-Equal 'a second use of the token returns 400' (Use-Token $mail.Token).Status 400
    Assert-Equal 'the new account can create links' (New-ShortLink -User $user).Response.Status 201
}

function Test-SignupEnumeration {
    $known = New-ScenarioUser
    $unknownEmail = New-UniqueEmail 'unknown'

    $forKnown = Request-Mail '/api/v1/signup' $known.email
    $forUnknown = Request-Mail '/api/v1/signup' $unknownEmail
    Assert-Equal 'a known address gets 202' $forKnown.Status 202
    Assert-Equal 'an unknown address gets 202' $forUnknown.Status 202
    Assert-Equal 'the two responses have the same body' $forKnown.Body $forUnknown.Body

    $knownMail = Wait-ForMail -To $known.email
    Assert-True 'the known address is told it already has an account, with no token' ($knownMail -and $knownMail.Subject -match 'already have an account' -and -not $knownMail.Token)
    $unknownMail = Wait-ForMail -To $unknownEmail
    Assert-True 'the unknown address gets a verification token' ($unknownMail -and $unknownMail.Token)

    $invalid = Request-Mail '/api/v1/signup' 'not-an-email'
    Assert-Equal 'an invalid address returns 400' $invalid.Status 400
    Assert-True 'the 400 names the email field' ($null -ne $invalid.Json.errors.email)
}

function Test-Rotate {
    $user = New-ScenarioUser
    $oldKey = $user.api_key

    Assert-Equal 'rotating without a key returns 401' (Invoke-Api -Method POST -Path '/api/v1/users/me/api-key/rotate').Status 401
    $rotate = Invoke-Api -Method POST -Path '/api/v1/users/me/api-key/rotate' -ApiKey $oldKey
    Assert-Equal 'rotate returns 200' $rotate.Status 200
    if ($rotate.Status -ne 200) { return }
    Update-UserKey $user $rotate.Json.api_key

    Assert-True 'the new key differs from the old one' ($rotate.Json.api_key -ne $oldKey)
    Assert-Equal 'the old key returns 401' (Invoke-Api -Method GET -Path '/api/v1/users/me' -ApiKey $oldKey).Status 401
    $me = Invoke-Api -Method GET -Path '/api/v1/users/me' -ApiKey $user.api_key
    Assert-Equal 'the new key returns 200' $me.Status 200
    Assert-Equal 'it is the same account' $me.Json.user_id $user.user_id
}

function Test-Recover {
    $user = New-ScenarioUser
    $oldKey = $user.api_key

    Assert-Equal 'recover returns 202' (Request-Mail '/api/v1/recover' $user.email).Status 202
    $mail = Wait-ForMail -To $user.email
    Assert-True 'a recovery mail with a token arrives' ($mail -and $mail.Token)
    if (-not $mail -or -not $mail.Token) { return }

    $verify = Use-Token $mail.Token
    Assert-Equal 'verify returns 200' $verify.Status 200
    if ($verify.Status -ne 200) { return }
    Update-UserKey $user $verify.Json.api_key
    Assert-Equal 'it is the same account' $verify.Json.user_id $user.user_id
    Assert-Equal 'the lost key returns 401' (Invoke-Api -Method GET -Path '/api/v1/users/me' -ApiKey $oldKey).Status 401
    Assert-Equal 'the new key returns 200' (Invoke-Api -Method GET -Path '/api/v1/users/me' -ApiKey $user.api_key).Status 200

    $stranger = New-UniqueEmail 'nobody'
    Assert-Equal 'recover for an unknown address also returns 202' (Request-Mail '/api/v1/recover' $stranger).Status 202
    Assert-True 'but no mail is sent to it' ($null -eq (Wait-ForMail -To $stranger -TimeoutSeconds 4))
}

function Test-DeleteAccount {
    $user = New-ScenarioUser
    $first = New-ShortLink -User $user
    $second = New-ShortLink -User $user
    if (-not $first.Code -or -not $second.Code) { Add-Result 'setup: two links' 'FAIL' "statuses $($first.Response.Status), $($second.Response.Status)"; return }

    # Following the links also puts them into the Redis cache, which deleting the account must clear.
    Assert-Equal 'the first link redirects before deleting' (Invoke-Api -Method GET -Path "/$($first.Code)").Status 302
    Assert-Equal 'the second link redirects before deleting' (Invoke-Api -Method GET -Path "/$($second.Code)").Status 302

    Assert-Equal 'deleting without a key returns 401' (Invoke-Api -Method DELETE -Path '/api/v1/users/me').Status 401
    Assert-Equal 'deleting the account returns 204' (Invoke-Api -Method DELETE -Path '/api/v1/users/me' -ApiKey $user.api_key).Status 204

    Assert-Equal 'the key returns 401 afterwards' (Invoke-Api -Method GET -Path '/api/v1/users/me' -ApiKey $user.api_key).Status 401
    Assert-Equal 'the first link now returns 404' (Invoke-Api -Method GET -Path "/$($first.Code)").Status 404
    Assert-Equal 'the second link now returns 404' (Invoke-Api -Method GET -Path "/$($second.Code)").Status 404
    Assert-Equal 'the cache entry of the first link is gone' ((Invoke-Redis exists "url:$($first.Code)") -join '') 0

    # The address is free again, so the same person can sign up anew and gets a different account.
    Assert-Equal 'the address can sign up again' (Request-Mail '/api/v1/signup' $user.email).Status 202
    $mail = Wait-ForMail -To $user.email
    if (-not $mail -or -not $mail.Token) { Add-Result 'a new verification mail arrives' 'FAIL' 'no mail'; return }
    $verify = Use-Token $mail.Token
    Assert-Equal 'the new account is created' $verify.Status 200
    if ($verify.Status -eq 200) {
        [void](Add-CreatedUser $verify.Json)
        Assert-True 'it is a different account' ($verify.Json.user_id -ne $user.user_id)
    }
}

# ---------------------------------------------------------------------------------------------------------------
# Cleanup: delete everything the scenarios created (in small groups, to keep command lines short)
# ---------------------------------------------------------------------------------------------------------------
function Remove-ScenarioData {
    $codes = @($script:CreatedCodes | Sort-Object -Unique)
    for ($i = 0; $i -lt $codes.Count; $i += 20) {
        $group = $codes[$i..([math]::Min($i + 19, $codes.Count - 1))]
        $statements = $group | ForEach-Object {
            "DELETE FROM url_mappings WHERE short_code = '$_'; DELETE FROM url_clicks WHERE short_code = '$_'; DELETE FROM url_last_access WHERE short_code = '$_';"
        }
        [void](Invoke-Cql ($statements -join ' '))
    }
    for ($i = 0; $i -lt $codes.Count; $i += 30) {
        $group = $codes[$i..([math]::Min($i + 29, $codes.Count - 1))]
        [void](Invoke-Redis (@('del') + ($group | ForEach-Object { "url:$_"; "clicks:$_"; "last:$_" })))
    }
    foreach ($user in $script:CreatedUsers) {
        [void](Invoke-Cql ("DELETE FROM urls_by_user WHERE user_id = '$($user.user_id)'; DELETE FROM users WHERE user_id = '$($user.user_id)'; " +
                "DELETE FROM users_by_api_key WHERE api_key_hash = '$($user.api_key_hash)'; DELETE FROM users_by_email WHERE email = '$($user.email)';"))
    }
    Write-Host "Deleted $($codes.Count) link(s) and $($script:CreatedUsers.Count) user(s) created by the scenarios."
}

# ---------------------------------------------------------------------------------------------------------------
# Run
# ---------------------------------------------------------------------------------------------------------------
if (-not (Wait-Healthy -TimeoutSeconds 5)) {
    throw "The service at $($config.BaseUrl) is not ready. Start it with scripts/Start-Dev.ps1."
}

$selected = @()
if ($Name) { $selected += $Name }
if ($All) {
    $selected += $allScenarios
    if ($IncludeDisruptive) { $selected += $disruptiveScenarios }
}
$selected = @($selected | Select-Object -Unique)

try {
    foreach ($scenario in $selected) {
        Write-Host ''
        Write-Host "=== $scenario" -ForegroundColor Cyan
        $script:CurrentScenario = $scenario
        try { & "Test-$scenario" }
        catch { Add-Result 'ran without an unexpected error' 'FAIL' $_.Exception.Message }
    }
}
finally {
    if (-not $KeepData) {
        try { Remove-ScenarioData } catch { Write-Warning "Cleanup failed: $($_.Exception.Message)" }
    }
}

# ---------------------------------------------------------------------------------------------------------------
# Summary
# ---------------------------------------------------------------------------------------------------------------
$failed = @($script:Results | Where-Object { $_.Status -eq 'FAIL' })
$passed = @($script:Results | Where-Object { $_.Status -eq 'PASS' })
$skipped = @($script:Results | Where-Object { $_.Status -eq 'SKIP' })

Write-Host ''
Write-Host "Checks: $($passed.Count) passed, $($failed.Count) failed, $($skipped.Count) skipped (scenarios: $($selected -join ', '))"
if ($failed.Count -gt 0) {
    Write-Host 'Failed checks:' -ForegroundColor Red
    $failed | ForEach-Object { Write-Host "  $($_.Scenario): $($_.Check) - $($_.Detail)" -ForegroundColor Red }
    exit 1
}
Write-Host 'All checks passed.' -ForegroundColor Green
exit 0
