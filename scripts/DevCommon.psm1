<#
.SYNOPSIS
    Shared helpers for the developer scripts in this folder. Local development only.
.DESCRIPTION
    Import with: Import-Module (Join-Path $PSScriptRoot 'DevCommon.psm1') -Force
    Works in Windows PowerShell 5.1 and PowerShell 7+ (so no ternary operator, no -SkipHttpErrorCheck, ASCII only).

    Settings can be overridden with environment variables:
      DEV_BASE_URL             public entry point (default: http://localhost:<PUBLIC_PORT from deploy/.env, else 8080>)
      DEV_MAIL_URL             MailPit web address (default: http://localhost:<MAILPIT_UI_PORT from deploy/.env, else 8025>)
      DEV_CASSANDRA_CONTAINER  default urlshortener-cassandra
      DEV_REDIS_CONTAINER      default urlshortener-redis
#>

$script:RepoRoot = Split-Path -Parent $PSScriptRoot

function Get-EnvOrDefault([string] $Name, [string] $Default) {
    $value = [Environment]::GetEnvironmentVariable($Name)
    if ([string]::IsNullOrWhiteSpace($value)) { $Default } else { $value }
}

# Follows PUBLIC_PORT from deploy/.env so the scripts keep working when the port is changed there.
function Get-DefaultBaseUrl {
    "http://localhost:$(Get-EnvFilePort 'PUBLIC_PORT' '8080')"
}

function Get-DefaultMailUrl {
    "http://localhost:$(Get-EnvFilePort 'MAILPIT_UI_PORT' '8025')"
}

# Reads a port number from deploy/.env (a missing or empty setting gives the default).
function Get-EnvFilePort([string] $Setting, [string] $Default) {
    $port = $Default
    $envFile = Join-Path $script:RepoRoot 'deploy/.env'
    if (Test-Path $envFile) {
        $line = Get-Content $envFile | Where-Object { $_ -match "^\s*$Setting\s*=\s*\d+\s*$" } | Select-Object -First 1
        if ($line -and $line -match '(\d+)\s*$') { $port = $Matches[1] }
    }
    $port
}

$script:Config = [pscustomobject]@{
    RepoRoot           = $script:RepoRoot
    BaseUrl            = (Get-EnvOrDefault 'DEV_BASE_URL' (Get-DefaultBaseUrl)).TrimEnd('/')
    MailUrl            = (Get-EnvOrDefault 'DEV_MAIL_URL' (Get-DefaultMailUrl)).TrimEnd('/')
    ComposeFile        = Join-Path $script:RepoRoot 'deploy/docker-compose.yml'
    EnvFile            = Join-Path $script:RepoRoot 'deploy/.env'
    EnvExampleFile     = Join-Path $script:RepoRoot 'deploy/.env.example'
    CassandraContainer = Get-EnvOrDefault 'DEV_CASSANDRA_CONTAINER' 'urlshortener-cassandra'
    RedisContainer     = Get-EnvOrDefault 'DEV_REDIS_CONTAINER' 'urlshortener-redis'
    UsersFile          = Join-Path $PSScriptRoot '.dev-users.json'
    LinksFile          = Join-Path $PSScriptRoot '.dev-links.json'
}

# One HttpClient for all calls. Redirects are NOT followed (tests assert on 302/410 themselves), and HTTP error
# statuses are returned instead of thrown (what -SkipHttpErrorCheck does in PowerShell 7).
Add-Type -AssemblyName System.Net.Http
# Windows PowerShell allows only 2 parallel connections per host unless raised; Invoke-Traffic needs more.
[System.Net.ServicePointManager]::DefaultConnectionLimit = 200
$script:HttpHandler = New-Object System.Net.Http.HttpClientHandler
$script:HttpHandler.AllowAutoRedirect = $false
$script:Http = New-Object System.Net.Http.HttpClient($script:HttpHandler)
$script:Http.Timeout = [TimeSpan]::FromSeconds(30)

function Get-DevConfig {
    <# .SYNOPSIS Returns the central settings (base URL, compose file, container names, data files). #>
    $script:Config
}

function Get-DevHttpClient {
    <# .SYNOPSIS The shared HttpClient (safe to use from several threads). #>
    $script:Http
}

function Invoke-Api {
    <#
    .SYNOPSIS
        Calls the service and always returns the response, whatever the status code.
    .OUTPUTS
        Status (int), Headers (lower-case names), Body (text), Json (parsed body or $null), ElapsedMs.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string] $Method,
        [Parameter(Mandatory)] [string] $Path,
        [string] $ApiKey,
        # A string is sent as it is (to test malformed bodies); anything else is sent as JSON.
        $Body,
        [hashtable] $Headers = @{},
        [string] $BaseUrl = $script:Config.BaseUrl
    )

    $uri = if ($Path -match '^https?://') { $Path } else { "$BaseUrl$Path" }
    $request = New-Object System.Net.Http.HttpRequestMessage -ArgumentList ([System.Net.Http.HttpMethod]::new($Method)), $uri
    try {
        if ($ApiKey) { [void]$request.Headers.TryAddWithoutValidation('X-Api-Key', $ApiKey) }
        foreach ($name in $Headers.Keys) { [void]$request.Headers.TryAddWithoutValidation($name, [string]$Headers[$name]) }
        if ($null -ne $Body) {
            $text = if ($Body -is [string]) { $Body } else { ConvertTo-Json -InputObject $Body -Compress -Depth 5 }
            $request.Content = New-Object System.Net.Http.StringContent -ArgumentList $text, ([System.Text.Encoding]::UTF8), 'application/json'
        }

        $timer = [System.Diagnostics.Stopwatch]::StartNew()
        $response = $script:Http.SendAsync($request).GetAwaiter().GetResult()
        try {
            $content = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
            $timer.Stop()

            $headersOut = @{}
            foreach ($header in $response.Headers) { $headersOut[$header.Key.ToLowerInvariant()] = ($header.Value -join ',') }
            foreach ($header in $response.Content.Headers) { $headersOut[$header.Key.ToLowerInvariant()] = ($header.Value -join ',') }

            $json = $null
            if ($content -and $content.TrimStart() -match '^[\{\[]') {
                try { $json = $content | ConvertFrom-Json } catch { $json = $null }
            }

            [pscustomobject]@{
                Status    = [int]$response.StatusCode
                Headers   = $headersOut
                Body      = $content
                Json      = $json
                ElapsedMs = $timer.Elapsed.TotalMilliseconds
            }
        }
        finally { $response.Dispose() }
    }
    finally { $request.Dispose() }
}

function Invoke-Docker {
    <# .SYNOPSIS Runs docker with the given arguments; returns the output lines and throws if it fails. #>
    param([Parameter(Mandatory)] [string[]] $Arguments)

    # Windows PowerShell turns native stderr text into error records; keep going and judge by the exit code.
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { $output = @(& docker @Arguments 2>&1 | ForEach-Object { "$_" }) }
    finally { $ErrorActionPreference = $previous }

    if ($LASTEXITCODE -ne 0) {
        throw "docker $($Arguments -join ' ') failed (exit $LASTEXITCODE): $($output -join [Environment]::NewLine)"
    }
    $output
}

function Invoke-Cql {
    <# .SYNOPSIS Runs CQL inside the Cassandra container (keyspace urlshortener) and returns the output lines. #>
    param(
        [Parameter(Mandatory)] [string] $Query,
        [string] $Container = $script:Config.CassandraContainer
    )
    # Single quotes only inside the query: Windows PowerShell mangles double quotes in native arguments.
    Invoke-Docker -Arguments @('exec', $Container, 'cqlsh', '-k', 'urlshortener', '-e', $Query)
}

function Invoke-Redis {
    <# .SYNOPSIS Runs redis-cli inside the Redis container, e.g. Invoke-Redis ttl url:abc #>
    param(
        [Parameter(Mandatory, ValueFromRemainingArguments)] [string[]] $Arguments
    )
    Invoke-Docker -Arguments (@('exec', $script:Config.RedisContainer, 'redis-cli') + $Arguments)
}

function Invoke-Compose {
    <# .SYNOPSIS Runs "docker compose -f deploy/docker-compose.yml ..." and shows its output. #>
    param([Parameter(Mandatory)] [string[]] $Arguments)

    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        & docker compose -f $script:Config.ComposeFile @Arguments 2>&1 | ForEach-Object { Write-Host "$_" }
    }
    finally { $ErrorActionPreference = $previous }
    if ($LASTEXITCODE -ne 0) { throw "docker compose $($Arguments -join ' ') failed (exit $LASTEXITCODE)." }
}

function Get-ServiceContainer {
    <# .SYNOPSIS Ids of the running containers of a compose service (several when it is scaled). #>
    param([Parameter(Mandatory)] [string] $Service)
    @(Invoke-Docker -Arguments @('compose', '-f', $script:Config.ComposeFile, '--profile', 'observability', 'ps', '-q', $Service) |
        Where-Object { $_ })
}

function Wait-Healthy {
    <# .SYNOPSIS Polls a health URL (default: the proxy's /health/ready) until it answers 200. Returns $true or $false. #>
    param(
        [string] $Url = "$($script:Config.BaseUrl)/health/ready",
        [int] $TimeoutSeconds = 120
    )
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        try { if ((Invoke-Api -Method GET -Path $Url).Status -eq 200) { return $true } } catch { }
        Start-Sleep -Seconds 2
    } while ((Get-Date) -lt $deadline)
    $false
}

function Wait-ContainerHealthy {
    <# .SYNOPSIS Waits until a container's Docker health check reports healthy. Returns $true or $false. #>
    param(
        [Parameter(Mandatory)] [string] $Container,
        [int] $TimeoutSeconds = 180
    )
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        try {
            $state = Invoke-Docker -Arguments @('inspect', '--format', '{{.State.Health.Status}}', $Container)
            if ($state -contains 'healthy') { return $true }
        } catch { }
        Start-Sleep -Seconds 2
    } while ((Get-Date) -lt $deadline)
    $false
}

function Write-Result {
    <# .SYNOPSIS One consistently formatted PASS / FAIL / SKIP line. #>
    param(
        [Parameter(Mandatory)] [ValidateSet('PASS', 'FAIL', 'SKIP')] [string] $Status,
        [Parameter(Mandatory)] [string] $Name,
        [string] $Detail
    )
    $colors = @{ PASS = 'Green'; FAIL = 'Red'; SKIP = 'Yellow' }
    $line = "[$Status] $Name"
    if ($Detail) { $line += " - $Detail" }
    Write-Host $line -ForegroundColor $colors[$Status]
}

function Get-RandomBase62 {
    <# .SYNOPSIS A random 7-character string of letters and digits: a valid short code that almost surely does not exist. #>
    param([int] $Length = 7)
    $alphabet = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789'
    -join (1..$Length | ForEach-Object { $alphabet[(Get-Random -Maximum $alphabet.Length)] })
}

function Read-JsonFile {
    <# .SYNOPSIS Reads a JSON array file into an array (empty when the file does not exist). #>
    param([Parameter(Mandatory)] [string] $Path)
    if (-not (Test-Path $Path)) { return @() }
    $parsed = Get-Content $Path -Raw | ConvertFrom-Json
    # Windows PowerShell returns a JSON array as ONE object (and writes it back as {"value":[...],"Count":n}),
    # so enumerate it explicitly to get plain items in both PowerShell versions.
    $items = @()
    foreach ($item in $parsed) { $items += $item }
    $items
}

function Write-JsonFile {
    <# .SYNOPSIS Writes an array as JSON, UTF-8 without a byte order mark. #>
    param(
        [Parameter(Mandatory)] [string] $Path,
        [Parameter(Mandatory)] [AllowEmptyCollection()] [object[]] $Items
    )
    $json = ConvertTo-Json -InputObject @($Items) -Depth 5
    [System.IO.File]::WriteAllText($Path, $json, (New-Object System.Text.UTF8Encoding($false)))
}

function Get-DevUsers {
    <# .SYNOPSIS The users saved by New-DevUser.ps1 (user_id, email, api_key). #>
    $users = @(Read-JsonFile $script:Config.UsersFile)
    if ($users.Count -eq 0) {
        throw "No dev users found. Run scripts/New-DevUser.ps1 first (expected file: $($script:Config.UsersFile))."
    }
    $users
}

function New-ApiKey {
    $bytes = New-Object byte[] 32
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
    [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
}

# Lowercase hex SHA-256 of the UTF-8 key: the form the service stores and looks up.
function Get-Sha256Hex([string] $Value) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try { $hash = $sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($Value)) }
    finally { $sha.Dispose() }
    -join ($hash | ForEach-Object { $_.ToString('x2') })
}

function New-DevUserRecord {
    <#
    .SYNOPSIS
        Creates a dev user straight in Cassandra (skipping the email sign-up) and returns user_id, email, api_key, api_key_hash.
        Only the hash of the key is stored in the database.
    #>
    param(
        [ValidatePattern('^[A-Za-z0-9._-]+$')]
        [string] $EmailPrefix = 'devuser'
    )

    $userId = [guid]::NewGuid().ToString()
    $email = "$EmailPrefix-$($userId.Substring(0, 8))@example.com"
    $apiKey = New-ApiKey
    $hash = Get-Sha256Hex $apiKey

    # Every interpolated value is generated or pattern-validated above (GUID, hex, [A-Za-z0-9._-]).
    # Same rows as a real sign-up: the key hash is also kept on the user, and the email is claimed.
    [void](Invoke-Cql ("INSERT INTO users (user_id, email, created_at, api_key_hash) VALUES ('$userId', '$email', toTimestamp(now()), '$hash'); " +
            "INSERT INTO users_by_api_key (api_key_hash, user_id) VALUES ('$hash', '$userId'); " +
            "INSERT INTO users_by_email (email, user_id) VALUES ('$email', '$userId');"))

    [pscustomobject]@{ user_id = $userId; email = $email; api_key = $apiKey; api_key_hash = $hash }
}

function Wait-ForMail {
    <#
    .SYNOPSIS
        Waits for a mail to an address to show up in MailPit (the local mail catcher) and returns the newest one,
        or $null if none arrives in time.
    .OUTPUTS
        Subject, Text, and Token (the value after "#token=" in the mail, or $null).
    #>
    param(
        [Parameter(Mandatory)] [string] $To,
        [int] $TimeoutSeconds = 20
    )

    $query = [uri]::EscapeDataString("to:$To")
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        $found = Invoke-Api -Method GET -Path "$($script:Config.MailUrl)/api/v1/search?query=$query"
        if ($found.Status -eq 200 -and $found.Json -and @($found.Json.messages).Count -gt 0) {
            $message = Invoke-Api -Method GET -Path "$($script:Config.MailUrl)/api/v1/message/$(@($found.Json.messages)[0].ID)"
            $token = $null
            if ($message.Json.Text -match '#token=([A-Za-z0-9_-]+)') { $token = $Matches[1] }
            return [pscustomobject]@{ Subject = $message.Json.Subject; Text = $message.Json.Text; Token = $token }
        }
        Start-Sleep -Seconds 1
    } while ((Get-Date) -lt $deadline)
    $null
}

Export-ModuleMember -Function Get-DevConfig, Get-DevHttpClient, Invoke-Api, Invoke-Docker, Invoke-Cql, Invoke-Redis,
    Invoke-Compose, Get-ServiceContainer, Wait-Healthy, Wait-ContainerHealthy, Write-Result, Get-RandomBase62,
    Read-JsonFile, Write-JsonFile, Get-DevUsers, Get-Sha256Hex, New-DevUserRecord, Wait-ForMail
