# Implementation Plan

Step-by-step, phase-wise procedure to implement the URL Shortener as per [requirements.md](../01-requirements/requirements.md), [design.md](../02-design/design.md) and [tech-stack.md](../02-design/tech-stack.md).

## Decisions (Clarified)

| Topic | Decision |
|---|---|
| Authentication | API key in `X-Api-Key` header; `user_id` is derived from the key, never trusted from the request body |
| Click count storage | Separate Cassandra counter table `url_clicks`; `last_accessed` in its own table `url_last_access` (counters can't mix with normal columns) |
| Expiration | Cassandra native TTL on insert + passive `expires_at` check on every redirect |
| Default TTL | 1 year when `expires_at` is omitted (configurable) |
| `expires_at` bounds | Must be in the future; max ~20 years ahead (Cassandra TTL limit minus grace period) |
| Duplicate long URLs | Always create a new code |
| Custom aliases | Case-sensitive |
| API versioning | Management APIs under `/api/v1`; redirect stays at `/{short_code}` |
| Load balancer | Nginx reverse proxy in Docker Compose: single entry point, routes `/api/v1/*` to the shortener and the rest to the redirector |
| ID generation | Redis `INCR` → Feistel obfuscation → Base62 |
| Counter durability | Redis AOF persistence **and** counter checkpoint in Cassandra (re-seed on startup) **and** `IF NOT EXISTS` on every insert with retry |
| List user links | `GET /api/v1/users/{user_id}/urls` backed by a `urls_by_user` table |
| Redirect code | Always `302` in v1 |
| Click counting | Every successful `302` counts (no bot filtering or de-duplication in v1) |
| Rate limiting | Token bucket, 100 requests/min per API key on `POST /api/v1/shorten` |
| CDN | Out of scope; services emit correct `Cache-Control` headers |
| Extras | Health checks, structured logging, rate limiting, OpenTelemetry + Grafana stack, GitHub Actions CI |
| Dev tooling | PowerShell 7+ (`pwsh`) scripts to start/stop the stack, seed users, create links, generate traffic and run scenario checks |
| Accounts (Phases 12-16) | Self-service sign-up with email verification replaces the "no sign-up in v1" decision; one account per email, one active API key per user |
| Email delivery | SMTP via MailKit (`Smtp:*` settings, password from the environment); MailPit as the local catch-all in compose |
| Account deletion | Deletes the account **and all of its links** (redirects then return `404`) |
| Web UI | Angular app with plain SCSS (no UI library) in `web/`, served by its own nginx container under `/app/` on the same origin (no CORS); API key kept in `sessionStorage` |
| Code conventions | Readability first: XML doc comments on public types/members, short comments explaining *why* (not *what*) for non-obvious logic, small focused classes; the same applies to tests and scripts |

## Target Solution Layout

```
URL_Shortener_Service/
├── docs/
│   ├── 01-requirements/                # What the system must do (functional, non-functional, capacity)
│   ├── 02-design/                      # How: APIs, data model, deep dives, tech stack, sequence diagrams
│   ├── 03-implementation/              # This plan, with per-phase status, deviations and verification results
│   └── notes/                          # Early scratch notes
├── src/
│   ├── UrlShortener.Core/              # Domain: Base62, Feistel, validation, TTL math (no I/O)
│   ├── UrlShortener.Infrastructure/    # Cassandra + Redis access, shared OTel/health wiring
│   ├── UrlShortener.Shortener.Api/     # Write service: POST /api/v1/shorten, analytics, user links
│   ├── UrlShortener.Redirector.Api/    # Read service: GET /{short_code}
│   └── UrlShortener.AnalyticsWorker/   # Flushes buffered clicks Redis → Cassandra
├── tests/
│   ├── UrlShortener.Core.Tests/        # NUnit
│   ├── UrlShortener.Infrastructure.Tests/  # integration tests against the Docker stack
│   ├── UrlShortener.TestSupport/       # shared in-memory fakes (no tests)
│   ├── UrlShortener.AnalyticsWorker.Tests/
│   ├── UrlShortener.Shortener.Api.Tests/
│   └── UrlShortener.Redirector.Api.Tests/
├── deploy/
│   ├── cassandra/schema.cql
│   ├── nginx/default.conf
│   ├── otel/ (collector, tempo, prometheus, loki, grafana configs)
│   ├── .env.example                    # Copy to .env (git-ignored) and set FEISTEL_SECRET
│   └── docker-compose.yml
├── scripts/
│   ├── DevCommon.psm1                  # Shared config + HTTP/cqlsh helpers
│   ├── Start-Dev.ps1
│   ├── Stop-Dev.ps1
│   ├── New-DevUser.ps1
│   ├── New-DevLinks.ps1
│   ├── Invoke-Traffic.ps1
│   └── Invoke-Scenario.ps1
├── .github/workflows/ci.yml
├── .dockerignore                       # Each service has its own Dockerfile next to its project
├── .gitignore
└── UrlShortener.slnx
```

---

## Phase 0 — Foundations

**Goal:** Empty-but-buildable solution and local infrastructure.

1. `git init` with a `.gitignore` covering `bin/`, `obj/`, `.vs/`, `.env`, `scripts/.dev-*.json` and test results (pushing to GitHub is done manually).
2. Create solution and the 5 `src` projects + 3 NUnit test projects targeting `net10.0`.
3. Add `Directory.Build.props` (nullable enabled, warnings as errors, implicit usings) and `.editorconfig`, plus a repo-level `nuget.config` that clears machine-wide sources and uses only nuget.org (avoids failures from private/corporate feeds).
4. Add `deploy/docker-compose.yml` with pinned images (no `:latest`):
   - `cassandra:5.0` (single node, healthcheck via `cqlsh -e "describe keyspaces"`)
   - `redis:7.4` with `--appendonly yes --appendfsync everysec`
5. Add `deploy/cassandra/schema.cql` and a one-shot `cassandra-init` container that applies it after Cassandra is healthy.

**Exit criteria:** `dotnet build` passes; `docker compose up` brings Cassandra + Redis healthy with schema applied.

## Phase 1 — Data Model (Cassandra Schema)

**Goal:** Query-first tables for every access pattern.

| Table | Partition key | Purpose |
|---|---|---|
| `url_mappings` | `short_code` | Redirect lookup; columns: `long_url`, `user_id`, `created_at`, `expires_at`, `is_custom` |
| `urls_by_user` | `user_id` (clustering `created_at DESC`, `short_code`) | List a user's links |
| `url_clicks` | `short_code` | `click_count counter` |
| `url_last_access` | `short_code` | `last_accessed timestamp` |
| `users` | `user_id` | `email`, `created_at` |
| `users_by_api_key` | `api_key_hash` | Auth lookup → `user_id` |
| `id_counter_checkpoint` | `name` | Last known Redis counter high-water mark |

Notes:
- Keyspace: `SimpleStrategy RF=1` locally; `NetworkTopologyStrategy` documented for production.
- Store only a **SHA-256 hash** of API keys, never the raw key.
- Rows are always written `USING TTL` (default 1 year when `expires_at` is omitted) = seconds until `expires_at` **+ grace period (e.g. 30 days)**, so the redirector can still return `410 Gone` during the grace window, then `404` after the row is purged.
- Counter tables can't use TTL; orphaned `url_clicks` rows are accepted for v1.
- Seed script creates one dev user + API key for local testing (no sign-up API in v1).
- **Cassandra 5.0 expiry limit:** the default `storage_compatibility_mode: CASSANDRA_4` rejects any TTL that expires after **2038-01-19**, which would break long expiries (the 20-year maximum). The compose file therefore starts Cassandra with `storage_compatibility_mode: NONE` (extends the limit to 2106; Cassandra's own TTL cap is 630,720,000 s = 20 years). Only use `NONE` on a fresh data directory in production.
- **LWT syntax:** the valid order is `INSERT ... IF NOT EXISTS USING TTL <n>` (`USING TTL` after `IF NOT EXISTS`).
- **Seed script (done):** `scripts/New-DevUser.ps1 [-Count n] [-EmailPrefix x]` creates users via `docker exec cqlsh`, stores only the lowercase-hex SHA-256 of each API key (UTF-8) and writes raw keys to the git-ignored `scripts/.dev-users.json`. Works on Windows PowerShell 5.1 and PowerShell 7+.

**Verified:** idempotent schema re-apply; conditional insert returns `[applied] False` on duplicates; logged batch across `url_mappings` + `urls_by_user`; `urls_by_user` returns newest first; counter increments accumulate; 20-year TTL accepted, 20 years + 1 s rejected.

**Exit criteria:** Schema applies cleanly and is idempotent (`IF NOT EXISTS`).

## Phase 2 — Core Domain Library

**Goal:** Pure, fully unit-tested logic in `UrlShortener.Core`.

1. **Base62 encoder/decoder** (`0-9a-zA-Z`).
2. **Feistel obfuscator** — reversible permutation over a fixed 40-bit domain (≈1.1T IDs, always fits in ≤7 Base62 chars; at 3.65B/year that's ~300 years). Key/rounds from configuration.
3. **Short code generator** = `Base62(Feistel(counterValue))`.
4. **Long URL validator** — absolute `http`/`https` only, max length (e.g. 2048), reject links pointing at our own short domain (redirect loops).
5. **Custom alias validator** — regex `^[a-zA-Z0-9_-]+$`, length 3–50, case-sensitive, reserved-word blocklist checked case-insensitively (`api`, `admin`, `login`, `help`, `contact`, `shorten`, `analytics`, `users`, `health`, …).
6. **Expiry validator** — omitted → `now + DefaultTtl` (1 year, configurable); provided → must be in the future and ≤ `now + 20 years − grace period` (Cassandra's max TTL is 630,720,000 s).
7. **TTL calculator** — row TTL (expiry + grace) and cache TTL = `min(remaining_lifetime, default_cache_ttl)`.

**Exit criteria:** NUnit tests cover round-trips (Base62, Feistel), collision-free permutation on sample ranges, and all validation edge cases.

**Status: done.** Code in `src/UrlShortener.Core` (`ShortCodes/`, `Validation/`, `Expiry/`), 90 NUnit tests in `tests/UrlShortener.Core.Tests`, all passing with warnings-as-errors.

Implementation notes for later phases:
- Base62 alphabet is `0-9a-zA-Z`. `FeistelObfuscator.FromSecret(secret)` derives the key from a configuration secret (SHA-256); the secret must stay constant for the lifetime of the data, since changing it changes all future codes (existing links are unaffected because codes are stored, not recomputed).
- `LongUrlValidator` takes the `ShortUrlBase` URI and rejects any URL with the same host (ignoring port, case and trailing dot), so dev URLs on `localhost` are rejected when the short domain is `localhost`.
- `ExpiryPolicy` takes a `TimeProvider`; `GetRowTtlSeconds` never returns 0 (TTL 0 means "no expiry" in Cassandra); `GetCacheTtl` returns `TimeSpan.Zero` for expired links, which callers must treat as "don't cache".
- Custom aliases and generated codes share one key space, so `IF NOT EXISTS` plus retry (Phase 4) is the guard against collisions between them.

## Phase 3 — Infrastructure Layer

**Goal:** Reusable Cassandra and Redis access in `UrlShortener.Infrastructure`.

1. Cassandra session setup (`CassandraCSharpDriver`), prepared statements, `LOCAL_QUORUM` for reads/writes, `SERIAL` for LWTs.
2. Repositories:
   - `UrlMappingRepository.TryInsertAsync` — `INSERT ... IF NOT EXISTS [USING TTL]`, returns `wasApplied`; also writes `urls_by_user`.
   - `UrlMappingRepository.GetAsync(shortCode)`
   - `UserRepository.GetByApiKeyHashAsync`
   - `ClickRepository` — counter `UPDATE`, `last_accessed` upsert, read for analytics.
3. Redis (`StackExchange.Redis`):
   - `IdCounter.NextAsync()` — `INCR id:counter`.
   - **Counter durability:** on startup, if `id:counter` is missing or lower than the Cassandra checkpoint, set it to `checkpoint + safety gap` (e.g. +100,000) via a Lua script; checkpoint the current value to Cassandra periodically.
   - `UrlCache` — get/set `url:{code}` with computed TTL.
   - `ClickBuffer` — `INCR clicks:{code}`, `SADD clicks:dirty {code}`, `SET last:{code}`.

**Exit criteria:** Repositories compile and are exercised by the services in later phases.

**Status: done.** Interfaces and models are in `src/UrlShortener.Core/Storage` (so the APIs can be tested with fakes); implementations are in `src/UrlShortener.Infrastructure` (`Cassandra/`, `Redis/`, `IdCounterDurability`, `AddUrlShortenerInfrastructure(configuration)`). 48 integration tests in `tests/UrlShortener.Infrastructure.Tests` run against the real Docker stack and pass.

Implementation notes for later phases:
- **Wiring:** call `services.AddUrlShortenerInfrastructure(builder.Configuration)`; settings come from the `Cassandra`, `Redis` and `IdCounter` sections. Nothing connects until first use. The Redis connection starts even if Redis is down (`AbortOnConnectFail=false`).
- **Renamed from the plan:** `UserRepository.GetUserIdByApiKeyHashAsync` (returns the user id, or null); `ClickBuffer.PopChangedCodesAsync/TakeAsync/RestoreAsync` are what the worker uses to flush.
- **Link creation is two writes:** the conditional insert into `url_mappings` is what decides uniqueness; the `urls_by_user` write follows it (a conditional write cannot span partitions). If only the second fails, the link still works and an error is logged.
- **Fail-soft vs fail-loud:** `IUrlCache` and `IClickBuffer.RecordAsync` swallow Redis errors (cache miss / lost click) so redirects keep working. `IIdCounter` and the rest of the buffer throw, because a guessed id could collide.
- **Counter durability:** `IdCounterDurability.RestoreIfLostAsync()` at startup, `SaveCheckpointAsync()` periodically (Phase 6 worker). The checkpoint never moves backwards, so a lost counter can't overwrite it. `IdCounter:SafetyGap` (default 100,000) must exceed the ids issued between two checkpoints.
- **Click flush order:** `ClickRepository.AddClicksAsync` writes the (idempotent) last-access time first and the (non-idempotent) counter last, so a failed flush can be retried without double counting.
- **Paging:** `ListByUserAsync` returns an opaque Base64Url token; a token the service did not issue throws `InvalidPageTokenException` (map to `400`).
- **Tests need the stack:** `docker compose -f deploy/docker-compose.yml up -d`. Without it the integration tests are skipped; set `REQUIRE_INTEGRATION_STACK=true` (CI) to make them fail instead. Each fixture uses its own Redis key prefix and short codes and cleans up after itself.
- **Not done (optional in the plan):** negative caching of unknown codes in Redis.

## Phase 4 — Shortener Service (Write Path)

**Goal:** Controllers-based API in `UrlShortener.Shortener.Api`.

1. **API key auth handler** — hash `X-Api-Key`, look up `users_by_api_key`, set `user_id` claim; missing/invalid → `401`.
2. **`POST /api/v1/shorten`**
   - Validate input (Core validators) → `400` on failure.
   - With `custom_alias`: `TryInsert`; not applied → `409 Conflict`.
   - Without alias: `NextAsync` → encode → `TryInsert`; not applied → retry with a new ID (bounded, e.g. 3 attempts).
   - No de-duplication: the same long URL always gets a new code.
   - Return `201 Created` with `short_url`, `long_url`, `expires_at`, `created_at`.
3. **`GET /api/v1/analytics/{short_code}`** — owner only (`403` otherwise); returns `total_clicks`, `created_at`, `last_accessed`.
4. **`GET /api/v1/users/{user_id}/urls`** — caller must match `user_id` (`403` otherwise); paged via Cassandra paging state.
5. Global error handling with RFC 7807 `ProblemDetails`; OpenAPI document.
6. **Rate limiting** — ASP.NET Core token bucket limiter on `POST /api/v1/shorten`, 100 requests/min per API key (configurable; per-instance limits are acceptable for v1).
7. `.http` file for manual testing.

**Exit criteria:** NUnit tests (WebApplicationFactory with faked repositories) cover 201/400/401/403/409 paths.

**Status: done.** Code in `src/UrlShortener.Shortener.Api` (`Controllers/`, `Services/`, `Authentication/`, `Middleware/`, `Configuration/`, `Extensions/`); 54 in-memory tests in `tests/UrlShortener.Shortener.Api.Tests` plus 5 new unit tests for the key hasher. A live smoke test against the real Docker stack also passed (201/409/400/401/403/404, correct row TTLs in Cassandra).

Implementation notes for later phases:
- **Run it:** `dotnet run --project src/UrlShortener.Shortener.Api --launch-profile http` (port 5221); requests are in `UrlShortener.Shortener.Api.http`. In Development the Feistel secret comes from `appsettings.Development.json`; in any other environment `Shortener:FeistelSecret` (at least 16 characters) must be supplied, e.g. `Shortener__FeistelSecret` as an environment variable, or the service refuses to start.
- **Settings:** `Shortener:ShortUrlBase` (default `http://localhost:8080`, the future Nginx address), `Shortener:MaxGenerateAttempts` (3), `RateLimit:TokenLimit/TokensPerPeriod/ReplenishmentPeriodSeconds` (100 / 10 / 6 s = 100 per minute), and an optional `Expiry` section.
- **Authentication is the fallback policy:** every endpoint needs a valid key unless marked `[AllowAnonymous]`. Unknown paths also answer 401 without a key (routes cannot be probed anonymously). Add `/health/*` (Phase 7) as `AllowAnonymous`.
- **Rate limit is per user** (a user with several keys shares one bucket); requests without a valid key share a bucket per client address. Limits are per instance (the plan's accepted v1 trade-off). Behind Nginx (Phase 8) the service needs forwarded-headers handling so the client address is the real one.
- **Error shape:** all errors are `application/problem+json`; field errors use snake_case keys (`errors.long_url`, `errors.custom_alias`, `errors.expires_at`, `errors.page_token`). `long_url` is therefore validated by the service rather than a `[Required]` attribute. Storage outages (Redis/Cassandra down or timing out) give `503` with `Retry-After: 5`; other unexpected errors give a generic `500`.
- **Startup:** `CounterRecoveryService` restores a lost ID counter before the service accepts requests (retries for about a minute while storage starts).
- **Limits:** request bodies are capped at 16 KB (Kestrel). API keys longer than 256 characters are rejected without a lookup.
- **Not done:** the OpenAPI document has no security scheme for `X-Api-Key` yet; no caching of key lookups (one Cassandra read per request).

## Phase 5 — Redirector Service (Read Path)

**Goal:** Lowest-latency path in `UrlShortener.Redirector.Api`.

1. **`GET /{short_code}`** waterfall:
   1. Redis `url:{code}` → hit: use it.
   2. Miss → Cassandra `url_mappings` → write to Redis with `min(remaining_lifetime, 24h)` TTL.
   3. Not found → `404`. Optionally cache a short-lived negative entry to absorb repeated misses.
2. **Passive expiry check** on every request: `expires_at <= now` → `410 Gone`.
3. Success → `302 Found` with `Location` and `Cache-Control: public, max-age=<min(remaining_lifetime, short default)>` (kept short so click tracking stays reasonably accurate behind a future CDN).
4. **Click recording** — only for successful `302`s (never `404`/`410`); fire-and-forget `ClickBuffer` call after the redirect is decided; failures are logged, never block the redirect.
5. Keep this service minimal: no auth, no OpenAPI UI in production.

**Exit criteria:** Tests cover cache hit, cache miss, 404, 410; Redis outage still serves redirects from Cassandra.

**Status: done.** Code in `src/UrlShortener.Redirector.Api` (`Controllers/RedirectController`, `Services/RedirectService`, `Configuration/`, `Extensions/`); 27 in-memory tests in `tests/UrlShortener.Redirector.Api.Tests`. Verified live with both APIs against the real Docker stack: first request ~1.2 s (cold start), warm requests 3-11 ms, correct `302`/`404`/`410`, Redis holds `url:{code}` (TTL 24 h) and `clicks:{code}`, and with Redis stopped redirects kept working at 9-18 ms.

Implementation notes for later phases:
- **Run it:** `dotnet run --project src/UrlShortener.Redirector.Api --launch-profile http` (port 5136); requests in `UrlShortener.Redirector.Api.http`. Settings: `Redirector:ClientCacheMaxAgeSeconds` (60; 0 = `Cache-Control: no-store`), `Redis:OperationTimeoutMs` (100 for this service), optional `Expiry` section.
- **Lookup order:** format check → cache → Cassandra. A code that cannot be a short code (`ShortCodeFormat`: 1-50 of `A-Za-z0-9_-`, e.g. `favicon.ico`) is answered `404` with no lookup at all. The cache write and the click record run in the background (never delay the response); failures are logged and ignored.
- **A click is a successful `302` only** (cache hit or database), never `404`/`410`. Browsers or a CDN honouring `max-age` will not come back during that window, so those repeat views are not counted (that is why the default is only 60 s).
- **Redis outage:** Redis is configured to fail fast while disconnected (`BacklogPolicy.FailFast`, in `CreateRedisConfiguration`). Without it each request waited the whole timeout (a test measured about 1 s per request). Redis failures are logged at most once per 30 s (`ThrottledLogger`) with a count of the suppressed ones; otherwise an outage logs three stack traces per redirect.
- **Location safety:** destinations that are not `http(s)` are never redirected to (`404` plus an error log); spaces, control and non-ASCII characters are percent-encoded before going into the header (a raw non-ASCII `Location` makes Kestrel fail the request).
- **Storage outage:** Cassandra unavailable on a cache miss gives `503` + `Retry-After: 5`, through the handler now shared in `UrlShortener.Infrastructure.Web` (`AddTransientFailureHandling`).
- **Shared test fakes** moved to `tests/UrlShortener.TestSupport` (fake repositories, cache, click buffer, counter, `TestTimeProvider`); the worker tests in Phase 6 should reuse them.
- **Not done:** negative caching of unknown codes (the format check already blocks junk paths); protection against many simultaneous misses on one cold code (each reads Cassandra once); `HEAD` requests (answered `405`; link-preview bots may use them).

## Phase 6 — Analytics Worker

**Goal:** `BackgroundService` in `UrlShortener.AnalyticsWorker`.

1. Every N seconds (configurable, e.g. 10s):
   - `SPOP clicks:dirty <batch>` to get changed codes.
   - For each code, atomically `GETDEL clicks:{code}` and `GETDEL last:{code}`.
   - `UPDATE url_clicks SET click_count = click_count + ?` and upsert `url_last_access`.
2. On Cassandra failure, re-add the count to Redis (`INCRBY`) and re-`SADD` the code so clicks aren't lost.
3. Document the trade-off: counter updates are not idempotent, so a crash between `GETDEL` and the Cassandra write can lose or (on retry) double-count a small number of clicks.
4. Also hosts the periodic **ID counter checkpoint** job (Phase 3).

**Exit criteria:** Clicks generated via the redirector appear in `GET /api/v1/analytics/{code}` after one flush interval.

**Status: done.** Code in `src/UrlShortener.AnalyticsWorker` (`Services/ClickFlusher`, `ClickFlushService`, `CounterCheckpointService`, `PeriodicBackgroundService`; `Configuration/WorkerOptions`; `Extensions/`); 22 tests in `tests/UrlShortener.AnalyticsWorker.Tests` (flusher logic, timed services, and the real service registration). Verified live with all three services against the Docker stack, using 2 s intervals: 5 clicks appeared in the analytics API after the first flush; the counter checkpoint matched Redis; then with Cassandra stopped while 6 more clicks arrived (served from the Redis cache), the worker kept all 6 in Redis and logged the outage once per 30 s, and after Cassandra restarted the total came out as exactly 11 (nothing lost, nothing doubled), 31 s after the restart.

Implementation notes for later phases:
- **Run it:** `dotnet run --project src/UrlShortener.AnalyticsWorker --no-build`. Settings (`Worker` section): `FlushInterval` (10 s), `FlushBatchSize` (500), `FlushConcurrency` (8), `CheckpointInterval` (30 s). Override with environment variables such as `Worker__FlushInterval=00:00:02`. Invalid values stop the worker at startup. The checkpoint interval times the peak ids per second (350/s in the capacity plan) must stay well under `IdCounter:SafetyGap` (100,000 by default; 30 s x 350 = about 10,500).
- **Flush cycle:** pop up to a batch of changed codes, take each link's buffered clicks (atomic read-and-clear), write them (last-access time first, then the counter), put them back if the write fails. A cycle lasts at most one flush interval (popular links keep receiving clicks, so "until empty" could never end), and ends early when a whole batch fails (Cassandra is most likely down). A failed link goes back into the changed set and may be tried again within the same cycle.
- **Shutdown:** a batch that was taken from Redis is always finished or put back; stopping is only honoured between batches, so a clean stop loses nothing. The checkpoint service saves one last checkpoint on shutdown.
- **Delivery guarantee:** "at least once, almost exactly once". An ordinary Cassandra outage loses nothing. Rare losses or double counts remain, because a Cassandra counter update cannot be safely repeated: a process kill between removing clicks from Redis and writing them loses them; a write that succeeds but reports a timeout is counted twice on retry. Redis losing its data loses the buffered clicks (at most one flush interval's worth, plus whatever accumulated during a Cassandra outage).
- **Errors:** storage outages are logged (throttled) and retried on the next tick; any other exception stops the worker, so the container restarts instead of silently doing nothing.
- **Not done yet:** a Dockerfile and compose service (Phase 8). (The worker health endpoints and the `clicks_flushed_total` metric were added in Phase 7.)

## Phase 7 — Observability & Health

**Goal:** Every service is observable and health-checked.

1. **Health checks** — `/health/live` (process) and `/health/ready` (Cassandra + Redis) for both APIs; worker exposes readiness too.
2. **Structured logging** — JSON console logs via `ILogger` with scopes (`short_code`, `user_id`, trace ID); never log API keys or full long URLs with query strings.
3. **OpenTelemetry** — traces (ASP.NET Core, HttpClient, Redis), metrics (runtime, ASP.NET Core, custom: `redirects_total`, `cache_hits_total`, `shorten_total`, `clicks_flushed_total`), logs → OTLP.
4. Add to compose (pinned versions): `otel-collector`, `tempo` (pin e.g. `2.6.1`), `prometheus`, `loki`, `grafana` with provisioned data sources and a dashboard for p99 redirect latency, cache hit ratio and error rates.

**Exit criteria:** A redirect shows a full trace in Grafana/Tempo and its metrics in Prometheus.

**Status: done.** Code in `src/UrlShortener.Infrastructure` (`Telemetry/`, `Health/`), wired into all three services; compose `observability` profile and configs in `deploy/otel/`. The full solution has 294 passing tests. Verified live with all three services plus the observability stack: a redirect trace in Tempo (`GET {shortCode}` > `cache.get` > `cassandra.execute` on a cache miss, plus `clicks.record`), redirect, cache and flush metrics in Prometheus, logs from all three services in Loki (and none containing the long-URL secret that was sent), and the provisioned Grafana dashboard with its three data sources.

Implementation notes for later phases:
- **Health:** `/health/live` (process only) and `/health/ready` (Cassandra + Redis) on both APIs and on the worker, which now hosts a tiny web server on port 5137. The worker's live check also fails if its loops stop ticking. On the Redirector a dead Redis is `Degraded` (still answers 200) because redirects work without it; on the Shortener and the worker it is `Unhealthy`. The endpoints are `AllowAnonymous` (the Shortener API requires authentication by default).
- **Telemetry is switched by environment:** traces, metrics and logs are exported over OTLP only when `OTEL_EXPORTER_OTLP_ENDPOINT` is set (for example `http://localhost:4317`); otherwise nothing is exported. Console logs are readable text in Development and JSON otherwise (override with `Logging__Json=true|false`).
- **Custom metrics** (meter `UrlShortener`): `urlshortener.redirects{result}`, `urlshortener.cache.lookups{result=hit|miss}`, `urlshortener.shorten.requests{outcome}`, `urlshortener.clicks.flushed`, `urlshortener.clicks.flush.failures`. `cache.lookups{result}` replaces the planned `cache_hits_total` (the hit ratio is hits divided by all lookups). Prometheus names end in `_total`, for example `urlshortener_redirects_total`.
- **Traces:** ASP.NET Core spans come from the standard instrumentation; Cassandra and Redis calls are spans created by hand (`cassandra.execute`, `cache.get`, `clicks.record`, ...) because the Redis instrumentation package only exists as a beta.
- **Log safety:** a leak was found and fixed: at Debug level MVC logs action arguments, which exposed the long URL; `CreateShortUrlRequest.ToString()` is now redacted. Log scopes carry `user_id`; API keys are never logged.
- **Compose:** `docker compose -f deploy/docker-compose.yml --profile observability up -d` starts `otel-collector` (4317/4318), `tempo` 2.6.1 (3200), `loki` 3.2.1 (3100), `prometheus` v2.55.0 (9090) and `grafana` 11.3.0 (3000; login `admin`/`admin`, anonymous viewing enabled, dev only). The dashboard is "URL Shortener" (uid `urlshortener-overview`).
- **Not done:** alert rules; no log-based correlation links beyond the Tempo/Loki data sources.

## Phase 8 — Containerization & Full Stack Compose

**Goal:** One command runs the whole system.

1. Multi-stage `Dockerfile` per service (SDK build → `aspnet`/`runtime` chiseled image, non-root user).
2. Compose services: `nginx`, `cassandra`, `cassandra-init`, `redis`, `shortener-api`, `redirector-api`, `analytics-worker`, observability stack.
3. `depends_on` with `condition: service_healthy`; configuration via environment variables; secrets (Feistel key, seed API key) via `.env` file excluded from git.
4. **Nginx reverse proxy** (pinned image, e.g. `nginx:1.27-alpine`) is the only public entry point (e.g. `http://localhost:8080`):
   - `/api/v1/*` → `shortener-api`
   - `/health/*` on the proxy itself; service health endpoints stay internal
   - everything else (`/{short_code}`) → `redirector-api`, load-balanced across scaled replicas via Docker DNS (`resolver 127.0.0.11` + variable upstream so new replicas are picked up)
   - Forwards `X-Forwarded-For`/`X-Forwarded-Proto`; services enable forwarded-headers middleware so rate limiting and logs see the real client.
5. App services aren't exposed on host ports (except optionally in a dev override file); `ShortUrlBase` config points at the Nginx entry point.

**Exit criteria:** `docker compose up` → create a link → follow it → see analytics, all end-to-end.

**Status: done.** Files: `src/UrlShortener.{Shortener.Api,Redirector.Api,AnalyticsWorker}/Dockerfile`, `.dockerignore`, `deploy/nginx/default.conf`, `deploy/.env.example`, and the new services in `deploy/docker-compose.yml`. Verified with `docker compose up -d --build`: all services healthy; through `http://localhost:8080` a link was created (201, `short_url` on port 8080), followed five times (302), an unknown code gave 404, the click total reached 5 in the analytics API after one flush, and a call without a key got 401 from the shortener. With `--scale redirector-api=3`, 120 redirects were spread over the three replicas (47/33/40). All three containers run as the non-root user (uid 1654) and no log line contained the long-URL secret.

**Deviations from the plan above:**

| Plan | What was built | Why |
|---|---|---|
| Item 1: `aspnet`/`runtime` **chiseled** image | `aspnet:10.0-alpine` (still non-root, uid 1654) | Chiseled images have no shell or `wget`, so the compose health checks that `depends_on: service_healthy` relies on cannot run inside the container. |
| Item 4: services **enable forwarded-headers middleware** (in code) | Set `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true` in compose; no code change | The built-in switch adds the same middleware; it trusts any sender, which is acceptable only because no host port reaches the services. The unit tests are unaffected. |
| Item 3: secrets include a **seed API key** in `.env` | `.env` holds `FEISTEL_SECRET`, `PUBLIC_PORT` and the telemetry endpoint only | Keys are generated per user by `scripts/New-DevUser.ps1` and stored hashed; a fixed key in `.env` would be a second, unhashed source. |
| Item 3: `.env` required | Missing `FEISTEL_SECRET` is not a compose error; the shortener refuses to start with a clear message instead | A compose-level `:?` check would also break starting only Cassandra and Redis (for tests and CI) without an `.env` file. |
| Item 2: observability stack part of the stack | Kept behind `--profile observability`; the app services export telemetry only when `OTEL_EXPORTER_OTLP_ENDPOINT` is set in `.env` | Keeps the default stack small; one extra setting turns it on. |
| Item 5: optional dev override file | Not created | Nginx on `PUBLIC_PORT` is enough for the Phase 9 scripts. |

Implementation notes for later phases:
- **Run it:** copy `deploy/.env.example` to `deploy/.env` (git-ignored) and set `FEISTEL_SECRET` (16+ characters; without it the shortener refuses to start and says so). Then `docker compose -f deploy/docker-compose.yml up -d --build`. `PUBLIC_PORT` (default 8080) is the only published application port.
- **Base images:** `mcr.microsoft.com/dotnet/sdk:10.0` to build and `aspnet:10.0-alpine` to run, not the chiseled image the plan suggested: chiseled has no shell or `wget`, which the compose health checks need. Pin the exact .NET patch version in CI if you need reproducible images. Build context is the repository root; the restore step is its own layer, so it is reused until a package changes.
- **Start order:** the app services wait for `cassandra-init` to finish (schema) and for Redis to be healthy; nginx waits for all three to be healthy. Container health uses each service's `/health/ready`.
- **Nginx:** `nginx:1.27-alpine`. `/health/live` is answered by nginx itself; `/health/ready` is forwarded to the redirector; any other `/health/*` is 404; `/api/*` goes to the shortener; everything else to the redirector. Upstream names are variables with Docker's DNS resolver, so scaled replicas are found within about 5 s. The access log shows which replica answered (`upstream=`). Body limit 16 KB, like Kestrel.
- **Client address:** nginx sets `X-Forwarded-For` to the connecting address only (never the caller's own header, which could be faked), and the shortener and redirector trust it through `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true` (no code change; safe because only nginx can reach them). On Docker Desktop for Windows the address nginx sees is the Docker gateway, not the browser's.
- **Worker:** `appsettings.json` binds its health port to `localhost`, so compose overrides it with `Urls=http://+:8080`.
- **Telemetry:** `OTEL_EXPORTER_OTLP_ENDPOINT` comes from `deploy/.env` and is empty (export off) by default. To use the Phase 7 stack, add `--profile observability` and set it to `http://otel-collector:4317`, then recreate the services.
- **Not done:** the optional dev override file with direct host ports for the services (nginx is enough for Phase 9); `.env` carries no seed API key, because `scripts/New-DevUser.ps1` generates users and keys; TLS.

## Phase 9 — Developer PowerShell Scripts

**Goal:** One-command local workflows for starting the system, seeding data and exercising realistic scenarios. All scripts live in `scripts/`, target PowerShell 7+ (`pwsh`, for `ForEach-Object -Parallel` and `-SkipHttpErrorCheck`) and are for **local dev only**. (Built to run on Windows PowerShell 5.1 as well; see the deviations under the status block below.)

### 9.1 `DevCommon.psm1` (shared module)

- Central config: single base URL (the Nginx entry point), compose file path, Cassandra/Redis container names (overridable via env vars).
- `Invoke-Api` — wraps `Invoke-WebRequest` with `-MaximumRedirection 0` and `-SkipHttpErrorCheck` so tests can assert on `302`/`4xx` status codes and headers.
- `Invoke-Cql` — runs CQL inside the Cassandra container via `docker exec ... cqlsh`.
- `Wait-Healthy` — polls `/health/ready` with timeout.
- `Write-Result` — consistent PASS/FAIL output.

### 9.2 `Start-Dev.ps1` / `Stop-Dev.ps1`

| Script | Parameters | Behaviour |
|---|---|---|
| `Start-Dev.ps1` | `-InfraOnly`, `-Build`, `-Observability`, `-Scale <n>` | Checks Docker is running → `docker compose up -d` (optionally `--build`, only Cassandra/Redis with `-InfraOnly`, add the Grafana stack with `-Observability`, scale the redirector with `-Scale`) → waits for every health endpoint → prints service URLs (APIs, Grafana) |
| `Stop-Dev.ps1` | `-Purge` | `docker compose down`; `-Purge` also removes volumes after an explicit confirmation prompt |

### 9.3 `New-DevUser.ps1` — simulate user creation (implemented in Phase 1)

There's no sign-up API in v1, so this script seeds users directly in Cassandra.

- Parameters: `-Count <n>` (default 1), `-EmailPrefix` (default `devuser`).
- For each user: generate `user_id` (GUID) and a random API key (`RandomNumberGenerator`, 32 bytes, Base64Url), compute the SHA-256 hash, insert into `users` and `users_by_api_key` via `Invoke-Cql`.
- Validate the email format before building CQL (no raw user input interpolated).
- Save `user_id`, email and **raw API key** to `scripts/.dev-users.json` for the other scripts to use. This file is **git-ignored**.

### 9.4 `New-DevLinks.ps1` — simulate link creation

- Parameters: `-Count <n>`, `-User <index|email>` (default: all users from `.dev-users.json`, round-robin), `-CustomAliasPrefix`, `-ExpiresInSeconds`, `-Mix` (random blend of default-TTL / short-expiry / custom alias links).
- Calls `POST /api/v1/shorten` with generated long URLs (e.g. `https://example.com/product/<n>?ref=dev`).
- Prints a summary table (created / conflicts / errors) and saves created codes to `scripts/.dev-links.json` (git-ignored).

### 9.5 `Invoke-Traffic.ps1` — simulate redirect traffic

- Parameters: `-Requests <n>`, `-Concurrency <n>`, `-Distribution Uniform|Skewed` (skewed = a few hot links get most clicks, like real traffic), `-IncludeMisses <percent>` (requests for unknown codes).
- Sends `GET /{short_code}` without following redirects; reports status-code counts and p50/p95/p99 latency (client-side) to sanity-check the < 50ms target locally.

### 9.6 `Invoke-Scenario.ps1` — scenario-based checks

- Parameters: `-Name <scenario>` or `-All`; exits non-zero if any check fails, so CI can reuse it.
- Each scenario creates its own users/links, so scenarios are independent and repeatable.

| Scenario | Steps | Expected |
|---|---|---|
| `HappyPath` | Create user → shorten → redirect → wait one flush interval → analytics | `201`, `302` with correct `Location`, `total_clicks = 1` |
| `CustomAlias` | Create alias; repeat same alias; same alias with different case; reserved word (`admin`, `Admin`); invalid chars; too short | `201`, `409`, `201`, `400`, `400`, `400` |
| `Validation` | Non-http URL; relative URL; URL > 2048 chars; URL pointing at short domain; `expires_at` in the past; `expires_at` > 20 years ahead | All `400` |
| `DefaultTtl` | Shorten without `expires_at`; shorten the same long URL again | `expires_at ≈ now + 1 year`; two different codes |
| `Expiration` | Shorten with `expires_at = now + 5s` → redirect → wait 6s → redirect | `302` then `410` |
| `Auth` | No key; invalid key; user B reads user A's analytics; user B lists user A's links | `401`, `401`, `403`, `403` |
| `NotFound` | Redirect to an unknown code | `404` |
| `RateLimit` | Burst > 100 `POST /api/v1/shorten` requests within a minute with one key | At least one `429`; a second key is unaffected |
| `ClickCounting` | Send N redirects plus some `404`s/`410`s → wait one flush interval → analytics | `total_clicks = N` (failures not counted), `last_accessed` set |
| `CacheConsistency` | Redirect twice; check the `url:{code}` key exists in Redis with TTL ≤ remaining lifetime | Key present, TTL correct |
| `RedisOutage` | Create link → `docker stop` Redis → redirect → shorten → `docker start` Redis | Redirect still `302` (Cassandra fallback); shorten fails gracefully (`503`); recovers after restart |
| `CounterReseed` | Delete `id:counter` in Redis → restart shortener → create links | New codes created, no `500`s, no duplicates (re-seeded from checkpoint) |
| `UserLinks` | Create several links for one user → list with small `page_size` | Newest first, paging token works, only own links |
| `LoadBalancing` | Run with `Start-Dev.ps1 -Scale 3` → send redirects → inspect `X-Served-By` response header (instance ID, dev only) | Requests spread across all 3 redirector replicas |
| `Durability` (added in Phase 11) | Create link → redirect once → wait for the click to be stored → restart the Redis and Cassandra containers → delete the cached copy → redirect and read analytics | Still `302` to the same URL; analytics `200` with the click count kept |

`RedisOutage`, `CounterReseed` and `Durability` are disruptive; they only run when named explicitly or with `-All -IncludeDisruptive`.

**Exit criteria:** On a fresh machine, `./scripts/Start-Dev.ps1 -Build` → `./scripts/New-DevUser.ps1 -Count 3` → `./scripts/New-DevLinks.ps1 -Count 50 -Mix` → `./scripts/Invoke-Traffic.ps1 -Requests 1000` → `./scripts/Invoke-Scenario.ps1 -All` runs green.

**Status: done.** Scripts in `scripts/`: `DevCommon.psm1`, `Start-Dev.ps1`, `Stop-Dev.ps1`, `New-DevUser.ps1` (now uses the module), `New-DevLinks.ps1`, `Invoke-Traffic.ps1`, `Invoke-Scenario.ps1`. Verified on Windows PowerShell 5.1 against the full stack with 3 redirector replicas: `Start-Dev.ps1 -Build -Scale 3` → `New-DevUser.ps1 -Count 3` → `New-DevLinks.ps1 -Count 50 -Mix` (50 created) → `Invoke-Traffic.ps1 -Requests 1000` (1000 x 302; also a skewed run with 10% misses) → `Invoke-Scenario.ps1 -All` (46 checks passed) and `-Name RedisOutage,CounterReseed` (6 checks passed). `Stop-Dev.ps1`, `Stop-Dev.ps1 -Purge` (cancel path only; the delete path was not run) and `Start-Dev.ps1 -InfraOnly` were also exercised. The full solution has 296 passing tests (2 new, for `X-Served-By`). **Not tested:** PowerShell 7 end to end (`pwsh` is not installed here). Later, under PowerShell 7.4 in a container, all scripts parsed and the module imported and read and wrote JSON correctly; the scripts avoid 7-only features and use ASCII-only text, so they should run there unchanged.

**Deviations from the plan above:**

| Plan | What was built | Why |
|---|---|---|
| Target PowerShell 7+ (`ForEach-Object -Parallel`, `-SkipHttpErrorCheck`) | Runs on Windows PowerShell 5.1 **and** 7+ | Only 5.1 is installed on this machine. `Invoke-Api` uses a shared `HttpClient` (which returns error statuses and does not follow redirects), and `Invoke-Traffic` uses a runspace pool instead of `-Parallel`. Scripts must stay ASCII (5.1 reads BOM-less files as ANSI) and avoid `?:`, `??` and `&&`. |
| `LoadBalancing`: inspect an `X-Served-By` header ("dev only") | New setting `Redirector:ExposeInstanceId` (default false) makes the redirector add `X-Served-By: <container id>`; compose turns it on | The header did not exist. It is a small code change with two tests, off unless enabled. |
| `Start-Dev.ps1` checks Docker, runs compose, waits | Also creates `deploy/.env` with a generated `FEISTEL_SECRET` when it is missing or empty | Makes the "fresh machine" exit criteria work with one command. |
| `Invoke-Scenario.ps1 -Name <scenario>` | `-Name` takes one or more names (`-Name A,B` works with `powershell -File` too) and is checked in the script instead of by `ValidateSet`; new switches `-KeepData` and `-FlushIntervalSeconds` | `ValidateSet` rejects `A,B` when the script is started with `-File`. Scenarios delete the users, links and Redis keys they created unless `-KeepData` is given. |
| `New-DevUser.ps1 -CassandraContainer` parameter | Removed; set `DEV_CASSANDRA_CONTAINER` instead | The container name now lives in the shared module's configuration. |
| `Invoke-Traffic.ps1` "sanity-check the < 50 ms target" | Prints PASS when p99 is under 50 ms, otherwise only a yellow note | Client-side numbers on Docker Desktop are noisy (first run: p99 108 ms, p50 14 ms), so a miss is not treated as a failure. |

Implementation notes for later phases:
- **Module:** `DevCommon.psm1` exports `Get-DevConfig`, `Invoke-Api` (returns `Status`, `Headers` with lower-case names, `Body`, `Json`, `ElapsedMs`), `Invoke-Cql`, `Invoke-Redis`, `Invoke-Docker`, `Invoke-Compose`, `Wait-Healthy`, `Wait-ContainerHealthy`, `Write-Result`, `New-DevUserRecord`, JSON file helpers and a few more. Settings come from environment variables `DEV_BASE_URL` (default `http://localhost:<PUBLIC_PORT from deploy/.env, else 8080>`), `DEV_CASSANDRA_CONTAINER` and `DEV_REDIS_CONTAINER`.
- **Start/Stop:** `Start-Dev.ps1 [-InfraOnly] [-Build] [-Observability] [-Scale n]`; `-Observability` turns telemetry on for that run (sets the OTLP endpoint for the compose process). It waits for Cassandra, Redis, the schema job and every service container to be healthy, then for the proxy's `/health/ready`. `Stop-Dev.ps1 [-Purge]` runs `docker compose down` (including the observability profile); `-Purge` asks you to type `PURGE`, then also removes the volumes and the saved users and links files.
- **Windows PowerShell 5.1 traps found:** `ConvertFrom-Json` returns a JSON array as one object (and `ConvertTo-Json` of that writes `{"value":[...],"Count":n}`), so `Read-JsonFile` enumerates it explicitly; native commands' stderr becomes error records, so `Invoke-Docker` and `Invoke-Compose` judge by exit code; `-File` does not split `-Name A, B`; double quotes inside native-command arguments get mangled (CQL uses single quotes only).
- **Scenario facts:** `RateLimit` sends 130 quick requests (the bucket holds 100); `Expiration` and `ClickCounting` use lifetimes of a few seconds, so they depend on the host and container clocks agreeing; `ClickCounting` waits one extra flush interval to prove 404s and 410s are not counted; `CounterReseed` first waits (up to 60 s) for the worker to save a checkpoint, then deletes `id:counter`, restarts the shortener and creates 10 links (the counter came back about 100,000 ahead, the safety gap); `LoadBalancing` is skipped unless several redirector replicas run.
- **Not done:** an automated check of the scripts under PowerShell 7; a `-Parallel` fast path for `Invoke-Traffic.ps1`.

## Phase 10 — CI Pipeline (GitHub Actions)

**Goal:** Every push/PR is validated.

1. `ci.yml`: checkout → setup .NET 10 → `dotnet restore` → `dotnet build -warnaserror` → `dotnet test` (NUnit, TRX + coverage artifacts).
2. Build all Docker images (no push in v1).
3. Smoke job: `Start-Dev.ps1 -Build` → `Invoke-Scenario.ps1 -All` (non-disruptive scenarios) → `Stop-Dev.ps1`.

**Exit criteria:** Green pipeline on main; failing tests block merges.

**Status: written and checked locally; not yet run on GitHub** (there is no remote yet; pushing is done manually). File: `.github/workflows/ci.yml`, with two jobs that run in parallel on `ubuntu-latest`:
- **`build-and-test`:** checkout → .NET 10 → NuGet cache → `Start-Dev.ps1 -InfraOnly` (Cassandra, schema, Redis for the integration tests) → `dotnet restore` → `dotnet build -c Release -warnaserror` → `dotnet test` with TRX results and Cobertura coverage (`coverlet.collector` is already in the test projects) → upload `TestResults/**` as the `test-results` artifact (even on failure) → container logs on failure. `REQUIRE_INTEGRATION_STACK=true` makes the integration tests fail instead of skipping when the stack is unreachable.
- **`smoke`:** `Start-Dev.ps1 -Build -Scale 3` → `Invoke-Scenario.ps1 -All` (disruptive scenarios excluded) → container logs on failure → `Stop-Dev.ps1` (always).

Checked locally: `actionlint` reports no problems; the restore, build (`-c Release -warnaserror`, 0 warnings) and test commands from the workflow ran with the same options (296 passed, `.trx` and `coverage.cobertura.xml` produced); the scripts parse and import under real PowerShell 7.4 (run in the `mcr.microsoft.com/powershell` container). The smoke job itself has not run on Linux or under PowerShell 7 end to end, so the first GitHub run is the real test.

**Deviations from the plan above:**

| Plan | What was built | Why |
|---|---|---|
| Item 2: a step that builds all Docker images | No separate job: the smoke job's `Start-Dev.ps1 -Build` builds all three images | A separate build would build everything twice. A broken Dockerfile still fails the pipeline. |
| Item 3: smoke runs after the tests | The jobs run in parallel | Faster feedback; both must be green to merge. Mark both as required checks in the branch protection rules (a GitHub setting, not part of the file). |
| Smoke job: `Start-Dev.ps1 -Build` | `Start-Dev.ps1 -Build -Scale 3` | Three redirector replicas let the `LoadBalancing` scenario run instead of being skipped. |
| (not in the plan) | `concurrency` (a newer push cancels the older run), read-only `permissions`, a 30-minute timeout per job, a NuGet cache | Saves minutes and limits what the workflow can do. Actions are pinned to major versions (`@v4`), not commit hashes. |

Implementation notes for later phases:
- **Branch protection (manual):** in GitHub, require the `build-and-test` and `smoke` checks on `main`; that is what makes "failing tests block merges" true.
- **Image tags:** the `.NET` base images use the floating `10.0` tag; pin a patch version if reproducible CI images matter.
- **If `smoke` fails:** the log step prints the last 200 lines of every container; reproduce locally with `./scripts/Start-Dev.ps1 -Build -Scale 3` and `./scripts/Invoke-Scenario.ps1 -All`.
- **Not done:** publishing images, code-coverage thresholds or reports (the raw files are uploaded), and running the disruptive scenarios in CI.

## Phase 11 — Verification Against Requirements

Most rows below are automated by `Invoke-Scenario.ps1` (Phase 9).

| Requirement | How it's verified |
|---|---|
| Shorten URL | `POST /api/v1/shorten` returns unique 7-char-max code |
| Redirect | `GET /{code}` → `302` to the long URL |
| Custom aliases | Alias accepted; duplicate → `409`; reserved word → `400` |
| Expiration | Default 1-year TTL applied; expired link → `410`; TTL purges rows after grace period |
| Analytics | Click count and `last_accessed` returned after flush |
| Low latency (p99 < 50ms) | Prometheus histogram on redirector dashboard |
| Uniqueness | LWT `IF NOT EXISTS` on every insert + counter re-seed test (flush Redis, keep creating links) |
| Durability | Restart Redis/Cassandra containers; existing links still resolve |
| Availability / Scalability | Stateless services; `Start-Dev.ps1 -Scale 3` runs 3 redirector replicas behind Nginx (`LoadBalancing` scenario) |

**Status: done (verified on 2026-10-28 on one laptop with Docker Desktop).** Everything below ran against the full stack started with `Start-Dev.ps1 -Observability -Scale 3`, with the dashboard's metrics exported to Prometheus.

| Requirement | Result | Evidence |
|---|---|---|
| Shorten URL | Met | 400 links created by 5 users: 400 distinct codes, all Base62, 376 of 7 and 24 of 6 characters (never longer); scenarios `HappyPath`, `Validation`, `DefaultTtl` pass |
| Redirect | Met | `HappyPath`: `302` with the exact `Location`; `NotFound`: `404`; 6000 redirects in a row returned only `302` (and the 2% deliberate misses `404`) |
| Custom aliases | Met | `CustomAlias`: `201`, repeat `409`, other case `201`, `admin`/`Admin` `400`, bad characters `400`, too short `400` |
| Expiration | Met | `DefaultTtl`: `expires_at` within a minute of now + 365 days; `Expiration`: `302`, then `410` after the lifetime; `CacheConsistency`: cache TTL never longer than the remaining lifetime. Rows carry a database TTL of lifetime + 30 days (60 s link: 2,592,058 s; default link: 34,127,998 s = 395 days), so Cassandra removes them itself. The actual purge was not waited for (30 days) |
| Analytics | Met | `HappyPath`/`ClickCounting`: count and `last_accessed` appear after one flush interval; 404s and 410s are not counted |
| Low latency (p99 < 50 ms) | Met, with little margin | Server-side redirect time from Prometheus over about 4,300 requests across 3 replicas (6000 skewed requests, 97% cache hits): p50 3.4 ms, p95 21.1 ms, **p99 42.7 ms**, 99.6% under 50 ms. The load generator's own numbers are far worse (client p50 20 ms, p99 115 ms) because PowerShell, Docker Desktop networking and the generator share one machine, so they cannot judge the target. A real load test on separate hardware has not been done |
| Uniqueness | Met | Inserts use `IF NOT EXISTS` (Phase 3 integration tests); 400 codes distinct; `CounterReseed`: after deleting the Redis counter the service restarted from the saved checkpoint plus the safety gap (126 → 100,136) and all new codes were distinct |
| Durability | Met | `Durability`: after restarting the Redis and Cassandra containers (with the cached copy deleted) the link still redirected, the user could still read analytics and the stored click count was kept. Also `RedisOutage`: with Redis stopped redirects still work (`302`) and shortening answers `503`, recovering after Redis returns. Not tested: loss of the Docker volumes, or a multi-node cluster (out of scope for v1) |
| Availability / Scalability | Met for a single machine | `LoadBalancing`: requests reached all 3 redirector replicas; with one replica stopped, 300 of 300 redirects still returned `302` (Nginx retried another replica); the stack is stateless apart from Cassandra and Redis. 99.99% availability and 35,000 peak redirects per second were **not** measured: they need a multi-node deployment and a proper load test (see below) |

Totals for the final run: 58 scenario checks passed (all 15 scenarios, including the disruptive ones), 296 automated tests passed.

**Gaps and risks to know about:**
- **Latency margin:** p99 of 42.7 ms is under the target but not by much, on a laptop with the load generator competing for the same CPU. The 404 path (a Cassandra read for an unknown code, no negative caching yet) and the first requests after a start are the slowest; negative caching is the first thing to add if real traffic shows a problem.
- **Capacity:** the 11,500 (steady) and 35,000 (peak) redirects per second, the 5.5 TB storage estimate and the 99.99% availability goal describe a multi-node production deployment (several Cassandra nodes, Redis with replicas, several proxies). v1 runs one node of each by design (see Out of Scope), so these numbers are designed for but not demonstrated.
- **CI:** the GitHub Actions workflow has not run yet (Phase 10).
- **Smaller items:** key lookups are not cached (one Cassandra read per authenticated request); no `HEAD` support on redirects; the OpenAPI document has no security scheme for `X-Api-Key`.

## Phase 12 — Accounts Backend (Shortener API)

**Goal:** Users create an account, recover or rotate their key, and delete the account through the API. **Status: done** (details after the exit criteria).

**Data model (added to `deploy/cassandra/schema.cql`):**

| Table / change | Key | Purpose |
|---|---|---|
| `users_by_email` | `email` (lower-cased, trimmed) | Enforces one account per email; written with `IF NOT EXISTS`; holds `user_id` |
| `email_tokens` | `token_hash` | Pending verification: `purpose` (`signup` or `recover`), `email`, `created_at`; row TTL 24 h so it expires on its own |
| `users.api_key_hash` (new column) | - | Lets rotate and delete find and remove the user's current `users_by_api_key` row |

Tokens are 32 random bytes (Base64Url); only the SHA-256 is stored. A token works once (deleted on use).

**Endpoints:**

| Endpoint | Auth | Behaviour |
|---|---|---|
| `POST /api/v1/signup` `{email}` | none | Validates the email (max 254 chars) and always answers `202`, so addresses cannot be probed. Existing email: sends an "you already have an account, use recover" mail instead of a token |
| `POST /api/v1/recover` `{email}` | none | Always `202`; sends a recovery link only if the account exists |
| `POST /api/v1/verify` `{token}` | none | `signup`: creates the user (if the email is still free) and a key. `recover`: replaces the user's key. Returns `{user_id, api_key}` once. Unknown, expired or used token: `400` |
| `GET /api/v1/users/me` | key | Returns `user_id`, `email`, `created_at`; lets the UI validate a pasted key |
| `POST /api/v1/users/me/api-key/rotate` | key | Issues a new key and revokes the old one; returns the new key once |
| `DELETE /api/v1/users/me` | key | Deletes the account and all its links; `204` |

**Account deletion order** (retryable because the key stays valid until the end): page through `urls_by_user` and delete each link's `url_mappings`, `url_clicks`, `url_last_access` rows and its Redis `url:{code}` / `clicks:{code}` keys → delete the `urls_by_user` partition → delete the `users_by_api_key`, `users_by_email` and `users` rows. It runs synchronously in v1; very large accounts are a known risk (move to a background job later).

**Email:** `IEmailSender` with an SMTP implementation (MailKit), configured by `Smtp:Host/Port/UseStartTls/Username/Password/From` (password only from the environment). Links look like `{Web:PublicBaseUrl}/app/verify#token=...`; the token is in the URL **fragment** so it never reaches server logs or `Referer` headers. Sending failures give `503` for the caller to retry; they are never reported as "email unknown".

**Abuse limits:** token-bucket limit per client address on `signup`, `recover` and `verify` (default 5 per hour for the first two), plus a 60-second cooldown per email (Redis key) between mails. `custom_alias` reserves `app`, `signup`, `recover`, `verify`.

**Exit criteria:** NUnit tests (fake email sender and repositories) cover the happy paths, expired/reused tokens, duplicate email, enumeration-safe responses, throttling, rotate, and delete (links gone, redirect `404`, cache cleared); integration tests cover the new Cassandra tables.

**Status: done.** Code: `src/UrlShortener.Core` (`Storage/IUserRepository`, `IEmailTokenStore`, `ICooldownStore`; `Validation/EmailAddressValidator`; `Security/SecretGenerator`), `src/UrlShortener.Infrastructure` (`Cassandra/UserRepository`, `EmailTokenStore`, new statements; `Redis/RedisCooldownStore`; delete operations on the link, click and cache stores), `src/UrlShortener.Shortener.Api` (`Controllers/RegistrationController`, `MeController`; `Services/AccountService`, `AccountMessages`; `Email/SmtpEmailSender`; `Configuration/AccountOptions`). 366 automated tests pass (Core 134, Shortener.Api 88, Infrastructure 80 against the live stack, Redirector 34, Worker 30); the build has 0 warnings with `-warnaserror`. Verified live against real Cassandra, Redis and a MailPit container: sign-up mail arrives with the link, verify returns the key once (`Cache-Control: no-store`), a second use of the token gives `400`, `/users/me` works, two links were created, rotate revokes the old key (`401`), delete returns `204` and leaves no rows in `users`, `users_by_email` or `urls_by_user`. Re-running `schema.cql` twice on the running Cassandra succeeds (the `ALTER TABLE ... ADD IF NOT EXISTS` is idempotent).

**Deviations from the plan above:**

| Plan | What was built | Why |
|---|---|---|
| Settings named `Web:PublicBaseUrl` | `Account:WebBaseUrl` (section `Account`, with `TokenLifetime`, `MailCooldown`, `MailRequestsPerHour`, `VerifyRequestsPerHour`) | Keeps all account settings in one section. |
| Per-address limit "5 per hour" for `signup`, `recover` and `verify` | `signup` and `recover` share one bucket of 5 per hour; `verify` has its own, looser bucket of 30 per hour | A token cannot be guessed, so the verify limit only has to stop floods; a stricter one could lock out a user who retries. |
| (not in the plan) | The shared 429 text is now generic ("You are sending requests too quickly") | The old text said "creating links", which is wrong for account endpoints. |
| `ReplaceApiKey`/`Delete` use the recorded key hash | They also revoke the hash of the key used for the request (a claim added by the authentication handler) | Accounts created before `users.api_key_hash` existed have no recorded hash, so the presented key is the only way to find the row to remove. |
| Dev seed script unchanged | `New-DevUser.ps1` and the scenarios now also write `users.api_key_hash` and `users_by_email`, and clean them up | Dev users then look like real sign-ups and can use recover, rotate and delete. Dev users seeded earlier lack those rows; seed new ones. |

Implementation notes for later phases:
- **Run it:** the API needs `Smtp:Host/Port/From` (defaults in `appsettings.json` point at `localhost:1025` with no TLS, the MailPit port) and `Account:WebBaseUrl` (default `http://localhost:8080`). In production set `Smtp__Host`, `Smtp__Port`, `Smtp__UseStartTls=true`, `Smtp__Username`, `Smtp__Password`, `Smtp__From` and `Account__WebBaseUrl` as environment variables. Phase 15 adds them to compose.
- **Manual testing:** requests are in `UrlShortener.Shortener.Api.http`. For a mail catcher, `docker run -d -p 1025:1025 -p 8025:8025 axllent/mailpit:v1.32.0` and open `http://localhost:8025`.
- **Answers that must not differ:** `signup` and `recover` answer `202` for known, unknown and cooling-down addresses alike; only the validation error (`400`) and a mail-server failure (`503`) differ. A failed send still starts the 60-second cooldown for that address.
- **Token lifetime:** a token is removed when it is taken, before the account work happens. If storage fails right after, the user must ask for a new mail.
- **Deleting an account** removes each link's database row first and its cache entry right after, then its click statistics; failures (for example Redis down) give `503` and the key still works, so the caller retries. Orphans left by earlier failures (a link missing from `urls_by_user`) and a link being created during the deletion are not cleaned up. A redirect that read the row just before it was deleted can still re-cache it for up to 24 hours; this narrow race is accepted for v1.
- **Not done:** an automated test for `SmtpEmailSender` (verified by hand against MailPit instead); alert on failed mails; making deletion a background job for accounts with a very large number of links.

## Phase 13 — Angular App: Scaffold and Core

**Goal:** A buildable app in `web/` with the shared plumbing. **Status: done** (details after the exit criteria).

1. Angular CLI project at the current stable version (pinned in `package.json`; Node version pinned in the Dockerfile and CI), standalone components, signals, strict TypeScript, **plain SCSS** (a small design-token file for colours, spacing and typography; no UI library).
2. `<base href="/app/">`; dev server proxies `/api` to `http://localhost:8080` so the dev setup is also same-origin.
3. Core services: `ApiClient` (typed wrappers for each endpoint), `AuthService` (key in `sessionStorage`, `signedIn` signal, sign-out), HTTP interceptor adding `X-Api-Key` and signing out on `401`, error mapper for RFC 7807 problem details (field errors keyed in snake_case), route guard for signed-in pages.
4. Layout: header (brand, account menu), toast area for errors, loading states.

**Exit criteria:** `npm ci`, lint, unit tests and `ng build` pass; the app shell loads under `/app/`.

**Status: done.** Angular 22.2 (CLI 22.2.5), standalone components and signals, Vitest through `ng test`, ESLint (`angular-eslint`), Node 24 (`engines` in `package.json`; Dockerfile and CI pins come in Phases 15-16). `npm run lint`, `npm run test:ci` (58 tests in 10 files) and `npm run build` (245 kB initial, 68 kB transferred) pass. Verified with the dev server: `/app/`, a deep link such as `/app/some/deep/link` and the production `index.html` all carry `<base href="/app/">`.

Layout of `web/src`:
- `app/core/`: `api-client.ts` (one typed method per endpoint, relative `/api/v1/...` URLs), `api.models.ts` (snake_case DTOs), `api-error.ts` (`ApiError` and `toApiError` for RFC 7807 problems), `auth.service.ts` (key in `sessionStorage`; `signIn`, `acceptCredentials`, `ensureAccount`, `signOut`), `interceptors.ts` (API key header, 401 handling, error conversion, loading counter), `auth.guard.ts`, `toast.service.ts`, `loading.service.ts`, `http-context.ts`.
- `app/layout/`: `header` (brand, email and sign-out for a signed-in user) and `toasts`; `app.html` adds the progress bar and `router-outlet`.
- `app/pages/home.ts`: a placeholder start page, replaced by the sign-in page in Phase 14. `app.routes.ts` had only `''` and a redirect for unknown paths.
- `styles/_tokens.scss` (colours, spacing, type, shape as CSS custom properties), `_base.scss` (element defaults, `.container`, `.btn`), `styles.scss` pulls both in. System fonts only; no third-party requests.
- `testing/fake-storage.ts`: an in-memory `Storage` for tests.

**Deviations from the plan above:**

| Plan | What was built | Why |
|---|---|---|
| `<base href="/app/">` | Set as `baseHref` in `angular.json` (source `index.html` keeps `/`) | The builder writes the tag for every build and `ng serve` serves under `/app/` too. |
| Header with an "account menu" | Brand, email and a sign-out button only | The account and links pages do not exist yet; Phase 14 adds their links. |
| Route guard "for signed-in pages" | `authGuard` is written and tested but not yet used by a route | No page needs it until Phase 14. |

Implementation notes for later phases:
- **Run it:** `cd web; npm ci; npm start` serves `http://localhost:4200/app/` and forwards `/api` to `http://localhost:8080` (`proxy.conf.json`), so the stack must be running (`Start-Dev.ps1`) for API calls. Other scripts: `npm run lint`, `npm run test:ci`, `npm run build` (output in `web/dist/web/browser`).
- **Interceptor order matters** (`app.config.ts`): loading, auth, error. The error interceptor is innermost, so the auth interceptor sees `ApiError`s and signs the user out on a 401, except for requests marked with `SILENT_AUTH_ERRORS` (the check of a typed key, `ApiClient.getMe(true)`).
- **Showing errors:** pages should catch `ApiError`, show `error.fieldError('long_url')` next to a field and `error.userMessage` in a toast; `error.retryAfterSeconds` is set for 429 and 503.
- **Keys:** `AuthService.acceptCredentials` stores a key returned by verify or rotate. After deleting the account or on any sign-out the key is removed from `sessionStorage`.
- **Not done:** Playwright end-to-end tests (out of scope), and the Dockerfile that serves the build (Phase 15).

## Phase 14 — Angular App: Pages

**Goal:** The user journeys, all through the public API. **Status: done** (details after the exit criteria).

| Route | Page |
|---|---|
| `/app` | Landing: "Sign in with API key" and "Get a key" |
| `/app/signup` | Email form, then a "check your inbox" message (same text whether or not the email exists) |
| `/app/recover` | Lost-key form (same "check your inbox" message) |
| `/app/verify` | Reads the token from the URL fragment, calls `verify`, shows the key **once** with a copy button and a "store it now" warning, then goes to the links page |
| `/app/links` | Shorten form (URL, optional alias, optional expiry) with field errors; paginated "My links" list (newest first, page token) with copy buttons |
| `/app/links/:code` | Total clicks, created at, last accessed |
| `/app/account` | Shows the email; rotate key (confirm, then show the new key once); delete account (type the email to confirm) |

Security rules in the UI: never log or put the key in a URL; clear it on sign-out, on `401` and after rotate/delete; no third-party scripts or fonts.

**Exit criteria:** component and service unit tests for every page; a manual walk-through of sign-up (via MailPit), shorten, list, analytics, rotate, recover and delete.

**Status: done.** `npm run lint`, `npm run test:ci` (113 tests in 19 files) and `npm run build` (285 kB initial, 78 kB transferred, pages lazy-loaded, no budget warnings) pass.

Browser walk-through (Chrome-based integrated browser; dev server on 4300 with `/api` forwarded to a locally run Shortener API, real Cassandra and Redis, MailPit as the mail server): sign-up form → "check your inbox" → the mailed link opens `/app/verify`, the token disappears from the address bar, the key is shown once with a copy button and the header shows the user → "continue" → shortening a URL shows the new link at once and lists it → the statistics page loads → account page: typing the email enables "Delete my account" → deleting signs out, returns to the sign-in page and shows a confirmation; the API log shows the user and its 1 link deleted. **Not walked through by hand:** key rotation and recovery (covered by unit tests of the pages and by the backend smoke test in Phase 12), and a narrow-screen layout check.

Pages (all in `web/src/app/pages/`, loaded on demand): `sign-in` (`/`, guarded by `guestGuard`), `request-mail` (`/signup` and `/recover`, one component driven by route data `mode`), `verify`, `links` (with `shorten-form`), `link-detail` (`/links/:code`), `account`. Shared: `shared/copy-button`, `shared/key-reveal`. Core additions: `guestGuard`, `describeError`, `short-links.ts`.

Deviations from the plan above:

| Plan | What was built | Why |
|---|---|---|
| `/app` shows "Sign in with API key" and "Get a key" | The start page is the sign-in form with links to get or recover a key; signed-in users are sent on to `/links` | One clear action per page. |
| `/app/verify` goes to the links page after showing the key | It waits for the user to press "I have saved my key, continue" | Leaving at once would hide the only copy of the key. |
| Account page "shows the email" | Shows email and user id | The user id is what the API calls the account. |

Implementation notes for later phases:
- **Short URLs in the list** are built from the page's origin (`shortUrlFor`), which is right behind nginx (same origin serves the redirector). Under `ng serve` the origin is `localhost:4300`, so those links do not redirect there. The link shown right after creating comes from the API (`Shortener:ShortUrlBase`).
- **Verification links** use `Account:WebBaseUrl`; for a dev server on another port set `Account__WebBaseUrl=http://localhost:4300`.
- **Same-origin check:** the sign-up mail link must open the same origin that serves `/api`, since the key is kept per origin in `sessionStorage`.
- **Not done:** end-to-end browser tests (out of scope), per-page `<meta>` descriptions, narrow-screen and keyboard-only passes beyond the labels, focus management and `aria` attributes already in the templates.

## Phase 15 — Containerization and Wiring

**Goal:** The web app and mail catcher run in the compose stack. **Status: done** (details after the exit criteria).

1. `web/Dockerfile`: Node build stage → `nginx:1.27-alpine` serving the static files with a fallback to `index.html`, non-root, with security headers (`Content-Security-Policy` allowing only same-origin, `X-Content-Type-Options`, `Referrer-Policy: no-referrer`).
2. Compose: `web` service (no host port), `mailpit` (pinned image; SMTP 1025, UI 8025) for dev, SMTP settings and `Web__PublicBaseUrl` passed to the Shortener API.
3. `deploy/nginx/default.conf`: `location /app/` → `web`; `/app` redirects to `/app/`; `/api/` and `/{code}` unchanged.
4. `Start-Dev.ps1`: waits for `web` and MailPit, prints their URLs.
5. `docs/02-design/architecture.md` gets a **Web UI (Angular)** node between Client and Nginx and the new endpoints; `design.md` and `db-schema.md` get the account flows and tables.

**Exit criteria:** `Start-Dev.ps1 -Build` then browsing `http://localhost:8080/app/` completes the whole sign-up-to-redirect journey, with the verification mail visible in MailPit.

**Status: done.** Verified with `Start-Dev.ps1 -Build` (all images built, including `web`, which runs `npm ci` and the production build inside Docker; every container healthy) and the integrated browser at `http://localhost:8080/app/`: sign-up form → mail in MailPit (link built from `Account__WebBaseUrl`) → verify page shows the key once → shorten a URL → the short link answers `302` through nginx (3 clicks) → the statistics page shows 3 clicks after the worker's flush → replace the key (new key shown) → delete the account → the deleted link now answers `404`. `Invoke-Scenario.ps1 -All` also passes (45 checks; `LoadBalancing` skipped with one redirector replica). Responses carry the security headers; `index.html` is `no-cache`, hashed files are cached for a year, and a missing file is a real `404`.

**Deviations from the plan above:**

| Plan | What was built | Why |
|---|---|---|
| Final stage `nginx:1.27-alpine`, non-root | `nginxinc/nginx-unprivileged:1.27-alpine` on port 8080 | The plain nginx image needs root to start; the unprivileged variant runs as a normal user. |
| Settings named `Web__PublicBaseUrl` | `Account__WebBaseUrl`, plus `SMTP_*` and `MAILPIT_UI_PORT` in `deploy/.env` (empty means the MailPit defaults) | Matches the options added in Phase 12. |
| `angular.json` unchanged | Production build sets `inlineCritical: false` | Critical-CSS inlining adds an inline `onload` handler, which a strict `script-src 'self'` policy blocks. |
| (not in the plan) | `absolute_redirect off` in both nginx configs | nginx only knows its container port, so `/app` would redirect to `http://localhost/app/` without the published port. |
| (not in the plan) | `web/` added to the root `.dockerignore` | The .NET images use the repository root as build context; `node_modules` must not be sent. |

Implementation notes for later phases:
- **Run it:** `./scripts/Start-Dev.ps1 -Build`, then open `http://localhost:8080/app/`; mails arrive at `http://localhost:8025`. The web container and MailPit have no other published ports.
- **Real mail:** set `SMTP_HOST`, `SMTP_PORT`, `SMTP_USE_STARTTLS=true`, `SMTP_USERNAME`, `SMTP_PASSWORD` and `SMTP_FROM` in `deploy/.env`. The `shortener-api` service still lists `mailpit` in `depends_on`; remove both for a deployment without the catcher.
- **Public address:** `Account__WebBaseUrl` and `Shortener__ShortUrlBase` both use `http://localhost:${PUBLIC_PORT}`; behind a real domain set them to the public HTTPS address.
- **Content-Security-Policy:** `web/security-headers.conf` allows only the app's own origin; `style-src` has `'unsafe-inline'` because Angular writes component styles into `<style>` tags at run time. The policy was not exercised with a browser-console check; the app works under it (styles and API calls load).
- **nginx config changes need a restart** (`docker compose -f deploy/docker-compose.yml restart nginx`): the file is mounted, and `up -d` does not notice a changed mount.
- **Not done:** the exact browser CSP-violation check, TLS, and running `web` and MailPit in CI (Phase 16).

## Phase 16 — CI, Scenarios and Verification

**Goal:** The new pieces are tested automatically. **Status: done locally; the workflow has not run on GitHub yet** (details after the exit criteria).

1. CI: a `web` job (`npm ci`, lint, unit tests, production build); the smoke job also starts `web` and MailPit.
2. New scenarios in `Invoke-Scenario.ps1` (verification token read from the MailPit API): `Signup` (mail → verify → key works), `SignupEnumeration` (known and unknown emails give the same response), `TokenReuse`, `Rotate` (old key `401`, new key works), `Recover`, `DeleteAccount` (links redirect `404`, key `401`, analytics gone).
3. Update requirements.md with the account and web UI requirements, and extend Phase 11-style verification with the results.

**Exit criteria:** The full pipeline is green, including the new scenarios.

**Status:** every step of the pipeline was run locally with the same commands; the workflow itself has not run on GitHub (no remote), so the first GitHub run is the real test.

| Pipeline step | Local result |
|---|---|
| `build-and-test`: `dotnet build -c Release -warnaserror` and all NUnit projects against the live stack | 0 warnings; 366 tests pass (Core 134, Shortener.Api 88, Infrastructure 80, Redirector.Api 34, AnalyticsWorker 30) |
| `web`: `npm run lint`, `npm run test:ci`, `npm run build` | lint clean; 113 tests in 19 files pass; build 285 kB initial, no budget warnings |
| `smoke`: `Start-Dev.ps1 -Build` then `Invoke-Scenario.ps1 -All` | all images build and every container becomes healthy; 86 checks pass, 1 skipped (`LoadBalancing` with one redirector replica; CI runs three) |
| `actionlint` on `ci.yml` | no findings |

What was added:
- **CI:** a `web` job (Node 24, `npm ci`, lint, unit tests, production build, npm cache). The `smoke` job needed no new steps: `Start-Dev.ps1 -Build` already builds and waits for `web` and MailPit.
- **Scenarios** (`scripts/Invoke-Scenario.ps1`, 41 checks, all in `-All`): `Signup` (mail arrives, link opens the web app with the token in the fragment, verify returns the key with `no-store`, the key works, a second use of the token is `400`, the new account can create links), `SignupEnumeration` (known and unknown addresses get the same `202` and body; the known one is told by mail, with no token; an invalid address is `400`), `Rotate` (no key `401`; the old key `401`, the new key works for the same account), `Recover` (mail, verify, same account, the lost key `401`; an unknown address gets `202` and no mail), `DeleteAccount` (links redirect, then the account is deleted: `204`, key `401`, both links `404`, the Redis cache entry gone, and the address can sign up again as a different account).
- **Helpers** in `scripts/DevCommon.psm1`: `Wait-ForMail` reads MailPit's search API (address from `DEV_MAIL_URL`, or `MAILPIT_UI_PORT` in `deploy/.env`, default 8025) and returns the subject, text and the token from the link.
- **Requirements:** `requirements.md` has the account, web UI and security requirements.

Deviations from the plan above:

| Plan | What was built | Why |
|---|---|---|
| A separate `TokenReuse` scenario | Folded into `Signup` | It needs the same mail and token; a separate scenario would repeat the setup. |
| (not in the plan) | The local compose stack sets `Account__MailRequestsPerHour` to 100 and `Account__VerifyRequestsPerHour` to 300 (override with `ACCOUNT_*` in `deploy/.env`) | The service defaults (5 and 30 per hour per address) would stop repeated scenario runs; production keeps the defaults. |

Gaps and risks:
- **No live check of the mail rate limit (`429`) or the one-mail-per-minute cooldown:** both are covered by API tests with fakes and the cooldown by an integration test, but the local stack's raised limits make a scenario impractical.
- **Not run on GitHub:** the `web` job, the `ubuntu` + PowerShell 7 smoke run with three redirector replicas, and the new scenarios under PowerShell 7. The scripts avoid 7-only features and parse cleanly under 5.1; the `LoadBalancing` scenario is the one that is skipped locally.
- **Branch protection (manual):** add the `web` check to the required checks next to `build-and-test` and `smoke`.
- **MailPit in CI** is the same pinned image as locally (`axllent/mailpit:v1.32.0`); a registry outage would fail the smoke job.

## Out of Scope for v1

- CDN / edge caching (headers only)
- 301 / per-link redirect type
- Multiple API keys per user, passwords, OAuth / social login
- Disposable-email blocking, CAPTCHA
- Browser end-to-end tests (Playwright) and server-side rendering for the web UI
- Distributed (cluster-wide) rate limiting
- Multi-node Cassandra / Redis Sentinel or Cluster
- Updating or deleting links
- Malicious/phishing URL checks (blocklists, Safe Browsing)
- Bot filtering and click de-duplication