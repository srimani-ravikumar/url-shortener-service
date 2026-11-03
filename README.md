# URL Shortener Service (trysmartly.co)

A URL shortener I built to answer one question: **how do you build a URL shortener that keeps redirecting even when its cache, database, or servers fail?** Not to replace bit.ly.

If you're reviewing this as a hiring manager, architect, or senior engineer: this README is for you.

> **Start here: [the full system architecture on one page](docs/02-design/architecture.md)** (diagram, services, data stores, observability).

[![System architecture](docs/02-design/architecture-v1.png)](docs/02-design/architecture.md)

## Why I Built This..

Most shorteners are a hash and a table. The hard questions only show up in production:

* How do two servers hand out short codes at the same time and never produce the same one, without a lock?
* What happens to redirects when Redis dies? When Cassandra restarts? When a replica is killed mid-traffic?
* How do you count a billion clicks without making the redirect itself slower?
* How do you *prove* "p99 under 50 ms" instead of just writing it in a requirements doc?

I wanted measured answers, not diagrams. So I built it, broke it on purpose, and wrote down what happened.

## What I Was Solving For..

| Problem | Approach | Result |
|---|---|---|
| **Unique, unguessable codes** | Redis counter + keyed permutation + Base62 | No collisions by construction; 400 links gave 400 distinct codes |
| **Counter lost or reset** | AOF persistence + Cassandra checkpoint + re-seed ahead on startup | Old codes are never re-issued after a reset |
| **Redis goes down** | Cache in front of Cassandra, fast-fail connections | Redirects keep working (`302`); only shortening returns `503` |
| **Clicks slowing redirects** | Cheap Redis writes, a worker batches them to Cassandra | Cassandra stopped mid-traffic, zero clicks lost |
| **A replica dies** | Stateless redirectors behind Nginx | One of 3 stopped, 300 of 300 redirects still succeeded |
| **Proving the latency target** | OpenTelemetry + Grafana | p99 **42.7 ms**, p50 3.4 ms |

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

Yes, mostly. I used Claude via Github Copilot as a pair programmer. The ***requirements, design and technology choices are mine***; Copilot wrote most of the code, tests and scripts.

You don't have to trust me on it: every phase in the plan records what was built, what changed and how it was verified.

> The skill that mattered most was deciding **knowing what not to build**.

<p align="center"><strong>© 2026 Srimani. All rights reserved.</strong></p>
