## Functional Requirements

- **Shorten URL:** Given a long URL, generate a unique short URL.
- **Redirect:** When a short URL is accessed, redirect the user to the original long URL.
- **Custom Aliases:** Allow users to create custom short codes (if available).
- **Expiration:** Support link expiration with default and custom TTLs.
- **Analytics:** Track basic click counts for each short URL.

## Non-Functional Requirements

- **High Availability:** The system must be highly available (e.g., 99.99%).
- **Low Latency:** Redirect requests must complete in under 50ms (p99).
- **Uniqueness:** Each short code maps to exactly one long URL, and short codes never collide.
- **Scalability:** Should handle a large number of read and write requests (100:1 read-to-write ratio).
- **Durability:** Once a short URL is created, the mapping must not be lost.

## Capacity Estimation

### Writes (URL Shortening)

- Average write QPS = 10,000,000 / (24 * 3600) ≈ 115 QPS (steady state)
- Peak write load (3x factor) ≈ 350 QPS

### Reads (Redirects)

- Average read QPS = 1,000,000,000 / (24 * 3600) ≈ 11,500 QPS (steady state)
- Peak read load (3x factor) ≈ 35,000 QPS

### Storage (5 Years)

Each URL mapping stores:

| Field | Size |
|---|---|
| Short code | ~7 characters = 7 bytes |
| Long URL | average 200 characters = 200 bytes |
| User ID | UUID = 36 bytes |
| Timestamps, metadata | ~50 bytes |
| **Total per record** | **~300 bytes** |

- URLs per year: 10M/day * 365 days = 3.65 billion URLs/year
- Storage per year: 3.65B * 300 bytes = ~1.1 TB/year
- 5-year storage: ~5.5 TB

### Short Code Length

Using Base62 encoding (a-z, A-Z, 0-9):

- 6 characters: 62^6 = 56.8 billion unique codes
- 7 characters: 62^7 = 3.5 trillion unique codes

With 3.65 billion URLs per year, a 7-character code provides ample headroom for decades of growth.