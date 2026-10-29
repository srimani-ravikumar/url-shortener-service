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
        shortener["<b>Shortener API (write)</b><br/><br/>POST /api/v1/shorten<br/>GET /api/v1/analytics/{short_code}<br/>GET /api/v1/users/{user_id}/urls<br/><br/>API Key Auth · Rate Limiting"]

        redirector["<b>Redirector API (read), N replicas</b><br/><br/>GET /{short_code}<br/><br/>Returns: 302 / 404 / 410"]

        worker["<b>Analytics Worker</b><br/><br/>Flush Buffered Clicks<br/>Checkpoint ID Counter"]
    end

    subgraph data["Data Stores"]
        redis[("<b>Redis</b><br/><br/>URL Cache<br/>ID Counter (INCR, AOF)<br/>Click Buffer")]

        cassandra[("<b>Cassandra</b><br/><br/>url_mappings <br/>urls_by_user<br/>url_clicks <br/> url_last_access<br/>users <br/> users_by_api_key<br/>id_counter_checkpoint")]
    end

    subgraph obs["Observability Stack"]
        otel["OpenTelemetry Collector"]
        tempo["<b>Tempo</b><br/>Distributed Traces"]
        prom["<b>Prometheus</b><br/>Metrics"]
        loki["<b>Loki</b><br/>Logs"]
        grafana["<b>Grafana</b><br/>Dashboards"]
    end

    client -->|"HTTP"| nginx

    nginx -->|"/api/v1/*"| shortener
    nginx -->|"/{short_code}"| redirector

    shortener -->|"next ID (INCR)"| redis
    shortener -->|"API key lookup, insert IF NOT EXISTS,<br/>read analytics and user links"| cassandra

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
| Load Balancer (Nginx) | Single entry point; path-based routing; load balancing |
| Shortener API (write) | `POST /api/v1/shorten`, `GET /api/v1/analytics/{short_code}`, `GET /api/v1/users/{user_id}/urls` |
| Redirector API (read) | `GET /{short_code}`, records clicks in Redis |
| Analytics Worker | Flushes buffered clicks from Redis to Cassandra; checkpoints the ID counter |

## Where to go next

* Request flows, APIs and deep dives: [design.md](design.md)
* Cassandra tables and Redis keys behind the data stores above: [db-schema.md](db-schema.md)
* Why each technology was chosen: [tech-decisions.md](tech-decisions.md)
