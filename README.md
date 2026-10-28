# URL Shortener Service (trysmartly.co)

A URL shortener I built to answer one question: **what does a design look like when the boring parts are allowed to fail?** Not to replace bit.ly.

If you're reviewing this as a hiring manager, architect, or senior engineer: this README is for you. I'll keep it short, and every claim below links to something you can run or read.

> **Start here: [the full system architecture on one page](docs/02-design/architecture.md)** (diagram, services, data stores, observability).

## Why I Built This..

Most shorteners are a hash and a table. The hard questions only show up in production:

* How do two servers hand out short codes at the same time and never produce the same one, without a lock?
* What happens to redirects when Redis dies? When Cassandra restarts? When a replica is killed mid-traffic?
* How do you count a billion clicks without making the redirect itself slower?
* How do you *prove* "p99 under 50 ms" instead of just writing it in a requirements doc?

I wanted measured answers, not diagrams. So I built it, broke it on purpose, and wrote down what happened.

## What I Was Solving For..

| Problem | What I did | Proof you can rerun |
|---|---|---|
| **Unique, unguessable codes** | Redis `INCR` counter, then a keyed Feistel permutation, then Base62. A permutation cannot collide, so uniqueness is by construction, and `IF NOT EXISTS` is only the safety net | 400 links from 5 users gave 400 distinct codes, never longer than 7 characters |
| **A lost counter must not re-issue old codes** | AOF persistence + a checkpoint in Cassandra + re-seed ahead by a safety gap on startup | `CounterReseed` scenario: delete the counter, restart, 10 new unique codes (counter came back at 100,136, not 126) |
| **Fast redirects that survive a dead cache** | Redis cache in front of Cassandra, with fast-fail connection settings | `RedisOutage` scenario: Redis stopped, redirects still `302`, shortening answers `503`, recovery after restart |
| **Clicks without slowing the redirect** | Three cheap Redis writes per click, a worker flushes batches to Cassandra counters and puts them back if the write fails | Cassandra stopped mid-traffic: nothing lost, total came out exact |
| **Durability** | Cassandra with replicated writes, native TTL for expiry plus a grace period | `Durability` scenario: restart Redis and Cassandra, link and click count survive |
| **Scale the 100:1 read side on its own** | Separate read and write services behind Nginx, stateless replicas | `LoadBalancing` scenario: 3 replicas all serve; with one stopped, 300 of 300 redirects still succeeded |
| **A latency target I can measure** | OpenTelemetry traces, metrics and logs into Grafana, with five custom metrics | Server-side p99 of **42.7 ms** (p50 3.4 ms) from Prometheus, with the caveats written next to it |
| **No secrets in logs** | Found a real leak while testing (long URLs with query strings logged at Debug level) and fixed it | Test that fails if the secret ever reaches a log line |

## What's in the Codebase..

* Three services: Shortener (write), Redirector (read), Analytics Worker, plus Nginx as the single entry point
* Cassandra schema modelled query-first (one table per question), Redis for cache, counter and click buffer
* API-key authentication (only a hash is stored) and per-user token-bucket rate limiting
* Health checks, JSON structured logs, OpenTelemetry, a provisioned Grafana dashboard
* 296 automated tests, 15 end-to-end scenarios (58 checks) including three disruptive ones that stop real containers
* One-command developer scripts and a GitHub Actions pipeline

## Try It..

Needs Docker and PowerShell (5.1 or 7+).

```powershell
./scripts/Start-Dev.ps1 -Build -Scale 3          # whole stack behind Nginx, 3 redirector replicas
./scripts/Invoke-Scenario.ps1 -All -IncludeDisruptive   # watch it survive being broken
```

## Where to Read More..

| If you want to know... | Read |
|---|---|
| What the system must do and the capacity maths | [docs/01-requirements/requirements.md](docs/01-requirements/requirements.md) |
| The architecture diagram on one page | [docs/02-design/architecture.md](docs/02-design/architecture.md) |
| Request flows, APIs, schema and deep dives | [docs/02-design/design.md](docs/02-design/design.md) |
| **Why** each technology was chosen, with the cost I accepted | [docs/02-design/tech-decisions.md](docs/02-design/tech-decisions.md) |
| How it was built, phase by phase, with deviations and results | [docs/03-implementation/implementation-plan.md](docs/03-implementation/implementation-plan.md) |
| Every requirement checked against evidence, and the gaps | [Phase 11 of the plan](docs/03-implementation/implementation-plan.md#phase-11--verification-against-requirements) |
| The dev scripts and scenarios | [scripts/](scripts/) |

## What I Chose Not to Build..

Left out on purpose, and listed in the plan: CDN, `301` redirects, user sign-up, distributed rate limiting, multi-node Cassandra and Redis, editing or deleting links, malicious-URL checks, bot filtering.

And what I did **not** prove: 99.99% availability and 35,000 redirects per second describe a multi-node production deployment. v1 runs one node of each, so those numbers are designed for, not demonstrated. The p99 figure comes from one laptop where the load generator shares the CPU, which is why it sits closer to the limit than I'd like. The plan says so too.

## "Did AI write this?"

Yes, a large part of it, and I'd rather say so than have you guess. I used GitHub Copilot as a pair programmer: the requirements, the design and the technology choices are mine, and Copilot wrote most of the code, tests and scripts one phase at a time.

What I can offer instead of a claim is evidence you can check: every phase in the plan records what was built, what deviated from the plan and why, and how it was verified. The scenarios run against real containers, and [tech-decisions.md](docs/02-design/tech-decisions.md) states the cost of each choice.

> The skill that mattered most was deciding **what to measure, what to break, and what not to build**.

<p align="center"><strong>© 2026 Srimani. All rights reserved.</strong></p>
