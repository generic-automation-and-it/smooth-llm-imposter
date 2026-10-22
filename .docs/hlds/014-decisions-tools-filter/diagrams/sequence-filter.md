# Classification and forwarding

```mermaid
sequenceDiagram
    participant Agent as Agent client
    participant Host as Router HTTP edge
    participant Filter as Tool selection
    participant Client as Gated decision transport
    participant Decision as OpenJEV service
    participant Upstream as Inference provider
    Agent->>Host: Inference request with tools
    Host->>Host: Plan and transform; who-probe short circuit if matched
    Host->>Host: Apply session translation
    Host->>Filter: Effective path and post-transform body
    alt Disabled, excluded or incomplete history
        Filter-->>Host: Original body
    else Eligible catalog
        loop Sequential chunks, stop on exhausted chunk
            Filter->>Client: Independent use/skip questions
            Client->>Decision: Acquire shared gate; POST bounded chunk
            Decision-->>Client: Answers or bounded failure
            Client-->>Filter: Answers or exception; release gate
        end
        Filter-->>Host: Keep protected/unresolved tools; clean dependent choices
    end
    Note over Host,Client: Caller cancellation aborts before inference forward
    Host->>Upstream: Forward final body
    Upstream-->>Host: Inference reply
    Host-->>Agent: Relay reply
```
