# LADR-03: Scope to OpenAI dialect + `GET /openai/v1/models` only

**Status:** Draft

<!-- Status lifecycle: Draft → Prototype → Accepted. Also: "Superseded by LADR-MM", "Deprecated". -->

## Context

The router serves two dialects behind prefixes (`/openai`, `/anthropic`) and forwards any HTTP
method on the catch-all routes. The Anthropic dialect also exposes a `/v1/models` discovery path,
with a different response shape (`ListResponse` of `ModelInfo`). Issue #20 is written entirely
against the OpenAI dialect and the `GET /openai/v1/models` route. The aggregation behaviour is a
new, narrow exception to the transparent-proxy contract, so its blast radius must be explicit.

## Decision

**Scope** the aggregation to exactly one case: an inbound **`GET`** whose dialect is **OpenAI** and
whose post-prefix path is **`/v1/models`**. Every other case is unchanged transparent passthrough —
the Anthropic dialect, any non-GET method on `/openai/v1/models`, and any other OpenAI path
(`/v1/responses`, `/v1/chat/completions`, …). The legacy unprefixed `/v1/models` route remains
intentionally unmapped (dialect-ambiguous), so this exception is reachable only via the `/openai`
prefix.

Recognition keys off all three facets (method + dialect + path); a mismatch on any one falls
straight through to the existing planning path.

## Alternatives Considered

- **Apply to both dialects now** — symmetric, but doubles the surface (a second response shape, a
  second set of tests) for a problem the issue scopes to OpenAI. Deferred to a follow-up if needed.
- **Match any method on `/v1/models`** — a `POST` to `/v1/models` is not OpenAI discovery semantics;
  matching it would shadow a legitimate passthrough. Rejected; GET-only.

## Consequences

- The new behaviour is auditable and narrow: one dialect, one path, one method.
- `GET /anthropic/v1/models` continues to passthrough — an intentional asymmetry until a follow-up
  HLD extends aggregation to Anthropic.
- A client calling the OpenAI discovery path with a non-GET method still reaches the upstream as
  before.

## Open

- Whether to extend aggregation to `GET /anthropic/v1/models` (Anthropic list shape). Trigger: a
  consumer that needs Anthropic discovery to reflect configured routes. Owner:
  @generic-automation-and-it/project.

## Related

- **LADR-02** — this LADR bounds the replacement scope LADR-02 introduces.
