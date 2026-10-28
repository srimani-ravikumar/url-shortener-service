# Architecture

The complete picture of the system on one page. Solid arrows carry requests and data; dotted arrows carry telemetry to the observability stack.

```mermaid
---
title: trysmartly.co (Distributed URL Shortener System)
---
flowchart TB
    client(["Client<br/>browser or API consumer"])

    subgraph edge["Entry point"]
        nginx["Nginx reverse proxy<br/>single public address<br/>routes by path, spreads load"]
    end

    subgraph app["Application services (stateless, scale by adding replicas)"]
        shortener["Shortener API (write)<br/>POST /api/v1/shorten<br/>GET /api/v1/analytics/{short_code}<br/>GET /api/v1/users/{user_id}/urls<br/>API key auth, rate limiting"]
        redirector["Redirector API (read), N replicas<br/>GET /{short_code}<br/>302 / 404 / 410"]
        worker["Analytics Worker<br/>flushes buffered clicks<br/>checkpoints the ID counter"]
    end

    subgraph data["Data stores"]
        redis[("Redis<br/>URL cache, ID counter (AOF),<br/>click buffer")]
        cassandra[("Cassandra<br/>url_mappings, urls_by_user,<br/>url_clicks, url_last_access,<br/>users, users_by_api_key,<br/>id_counter_checkpoint")]
    end

    subgraph obs["Observability stack"]
        otel["OpenTelemetry Collector"]
        tempo["Tempo<br/>traces"]
        prom["Prometheus<br/>metrics"]
        loki["Loki<br/>logs"]
        grafana["Grafana<br/>dashboards"]
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
