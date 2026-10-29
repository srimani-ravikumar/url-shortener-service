# Architecture

The complete picture of the system on one page. Solid arrows carry requests and data; dotted arrows carry telemetry to the observability stack.

```mermaid
---
title: 𝐭𝐫𝐲𝐬𝐦𝐚𝐫𝐭𝐥𝐲.𝐜𝐨 (𝐃𝐢𝐬𝐭𝐫𝐢𝐛𝐮𝐭𝐞𝐝 𝐔𝐑𝐋 𝐒𝐡𝐨𝐫𝐭𝐞𝐧𝐞𝐫 𝐒𝐲𝐬𝐭𝐞𝐦)
config:
  flowchart:
    htmlLabels: false
    curve: linear
    nodeSpacing: 65
    rankSpacing: 90
    wrappingWidth: 450
---
flowchart TB
    %% Unlinked nodes go on the top row in declaration order, so the author comes first (left)
    author["Designed by <b>Srimani</b>"]:::author
    client(["<b>Client</b><br/>Browser / API Consumer"])

    legend["<b>Side Note</b><br/>───▶  Solid: requests and data<br/>┄┄┄▶  Dotted: telemetry to the observability stack"]:::legend

    subgraph edge["Entry Point"]
        nginx["<b>Nginx Reverse Proxy</b><br/>Single Public Address · Path Routing · Load Balancing"]
    end

    subgraph app["Application Services (Stateless, scale by adding replicas)"]
        web["<b>Web UI (Angular, static files)</b><br/><br/>GET /app/*<br/>Sign in · Sign up · Links · Statistics · Account"]

        shortener["<b>Shortener API (write)</b><br/><br/>POST /api/v1/shorten<br/>GET /api/v1/analytics/{short_code}<br/>GET /api/v1/users/{user_id}/urls<br/>POST /api/v1/signup · /recover · /verify<br/>GET · DELETE /api/v1/users/me · POST .../api-key/rotate<br/><br/>API Key Auth · Rate Limiting"]

        redirector["<b>Redirector API (read), N replicas</b><br/><br/>GET /{short_code}<br/><br/>Returns: 302 / 404 / 410"]

        worker["<b>Analytics Worker (background)</b><br/><br/>Flush Buffered Clicks<br/>Checkpoint ID Counter"]
    end

    subgraph data["Data Stores"]
        redis[("<b>Redis</b><br/><br/>URL Cache<br/>ID Counter (INCR, AOF)<br/>Click Buffer<br/>Mail Cooldowns")]

        cassandra[("<b>Cassandra</b><br/><br/>url_mappings <br/>urls_by_user<br/>url_clicks <br/> url_last_access<br/>users <br/> users_by_api_key <br/> users_by_email <br/> email_tokens<br/>id_counter_checkpoint")]
    end

    smtp["<b>SMTP Server</b><br/>Sign-up and recovery mails<br/>(MailPit in local dev)"]

    subgraph obs["Observability Stack"]
        otel["OpenTelemetry Collector"]
        tempo["<b>Tempo</b><br/>Distributed Traces"]
        prom["<b>Prometheus</b><br/>Metrics"]
        loki["<b>Loki</b><br/>Logs"]
        grafana["<b>Grafana</b><br/>Dashboards"]
    end

    client -->|"HTTP"| nginx

    nginx -->|"/app/*"| web
    nginx -->|"/api/v1/*"| shortener
    nginx -->|"/{short_code}"| redirector

    shortener -->|"next ID (INCR),<br/>mail cooldown (SET NX)"| redis
    shortener -->|"API key lookup, insert IF NOT EXISTS,<br/>accounts and email tokens,<br/>read analytics and user links"| cassandra
    shortener -->|"SMTP: verification links"| smtp

    redirector -->|"cache lookup, record click"| redis
    redirector -->|"read mapping on cache miss"| cassandra

    worker -->|"pop changed codes, take counts (GETDEL)"| redis
    worker -->|"add to click counters,<br/>save ID counter checkpoint"| cassandra

    shortener -.->|"OTLP"| otel
    redirector -.->|"OTLP"| otel
    worker -.->|"OTLP"| otel

    otel -.-> tempo
    otel -.-> prom
    otel -.-> loki

    tempo -.-> grafana
    prom -.-> grafana
    loki -.-> grafana

    classDef legend fill:#fff,stroke:#888,stroke-dasharray:4 3
    classDef author fill:#fff,stroke:#888,color:#333,stroke-dasharray:4 3
```

Table definitions, relationships and Redis keys for the data stores are in [db-schema.md](db-schema.md).

## Services

| Service | Responsibility |
|---|---|
| Load Balancer (Nginx) | Single entry point; path-based routing (`/app/*`, `/api/v1/*`, `/{short_code}`); load balancing |
| Web UI (Angular) | Static single-page app under `/app/`: sign in with an API key, sign up and recover by email, create and list links, view statistics, replace the key, delete the account |
| Shortener API (write) | Links: `POST /api/v1/shorten`, `GET /api/v1/analytics/{short_code}`, `GET /api/v1/users/{user_id}/urls`. Accounts: `POST /api/v1/signup`, `/recover`, `/verify`, `GET` and `DELETE /api/v1/users/me`, `POST /api/v1/users/me/api-key/rotate` |
| Redirector API (read) | `GET /{short_code}`, records clicks in Redis |
| Analytics Worker (background) | Flushes buffered clicks from Redis to Cassandra; checkpoints the ID counter |
| SMTP Server | Delivers the sign-up and key recovery mails (MailPit catches them in local development) |

## Where to go next

* Request flows, APIs and deep dives: [design.md](design.md)
* Cassandra tables and Redis keys behind the data stores above: [db-schema.md](db-schema.md)
* Why each technology was chosen: [tech-decisions.md](tech-decisions.md)
