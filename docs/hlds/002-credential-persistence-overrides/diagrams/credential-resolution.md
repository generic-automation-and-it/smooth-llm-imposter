# Diagram — Credential Resolution

Where stored credentials enter the request flow. This extends HLD 001's routing decision at **one seam**:
the no-match / passthrough branch. The matched-imposter branch is identical to HLD 001.

```mermaid
flowchart TD
    A[Inbound request] --> B[Resolve dialect from endpoint]
    B --> C{First imposter mapping matches?}
    C -->|yes| D[HLD 001 path:<br/>rewrite model, inject caching,<br/>apply CONFIG key]
    C -->|no| E{Dialect has a default/passthrough provider?}
    E -->|no| F[404 dialect-shaped error]
    E -->|yes| G{Active stored credential for dialect?}
    G -->|no| H[HLD 001 passthrough:<br/>forward unchanged, config key if any]
    G -->|yes| I[Decrypt secret via IDataProtector<br/>apply AuthScheme ApiKey/Bearer<br/>apply BaseUrlOverride if set]
    D --> Z[Stream SSE back]
    H --> Z
    I --> Z
```

> Decryption happens just-in-time at forward; the plaintext secret is never stored, logged, or returned by
> the admin API. The imposter branch (`yes` at the first decision) never consults PostgreSQL.
