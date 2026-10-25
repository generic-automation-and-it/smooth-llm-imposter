# Transform-stage ordering

The design fact worth a diagram is **where** the strip sits: after normalization, before
the `chat_completions` downgrade, so both upstream API shapes inherit it.

```mermaid
flowchart TD
    A[Inbound OpenAI-dialect body] --> B[ParseObject]
    B --> C{RequestNormalization?<br/>HLD 004 — IsImposter-gated}
    C -- codex_to_openai_sdk --> D[Normalize root]
    C -- none --> E
    D --> E{StripEncryptedContent == true?<br/>HLD 011 — not imposter-gated}
    E -- no --> G
    E -- yes --> F["Drop input[] reasoning items<br/>with non-null encrypted_content"]
    F --> G{OpenAiUpstreamApi}
    G -- responses --> H[Forward /v1/responses]
    G -- chat_completions --> I[ToChatCompletions<br/>BuildMessages over survivors]
    I --> J[Forward /v1/chat/completions]
```

Two properties the ordering buys:

- The downgrade never needs ZDR awareness — by the time `ToChatCompletions` runs, matched
  items are already gone.
- The strip is reachable on the `responses` path, where `RequestNormalization` is forbidden
  by the validator. That is the whole motivating case (LADR-02).

## Request/response sequence

```mermaid
sequenceDiagram
    participant C as Codex client (ZDR, store:false)
    participant P as SmoothLlmImposter
    participant U as Imposter upstream (e.g. LM Studio)

    C->>P: POST /v1/responses<br/>input[] includes reasoning + encrypted_content
    Note over P: Resolve route → ProviderRoute<br/>StripEncryptedContent == true
    Note over P: Drop matched reasoning items<br/>(no decrypt, no log — NFR-01)
    P->>U: POST /v1/responses (sanitized body)
    U-->>P: 200 (previously 400 "Encrypted content is not supported.")
    P-->>C: 200 streamed/forwarded verbatim
```
