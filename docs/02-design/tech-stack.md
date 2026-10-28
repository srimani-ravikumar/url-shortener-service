## Tech Stack

| Component | Technology |
|---|---|
| API | .NET 10 |
| Persistence mechanism | Apache Cassandra |
| Caching | Redis |
| Deployment Target | Docker |
| API Style | ASP.NET Core Controllers |
| ID Generation (Counter) | Redis `INCR` (same instance as cache) |
| Services | Shortener (write), Redirector (read), Analytics Worker (Redis → Cassandra click flush) |
| Orchestration | Docker Compose |
| Testing | NUnit |