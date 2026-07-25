# LADR-03: Dialect-shaped chat envelope, not bare text

**Status:** Accepted

## Context

The probe must be consumable by existing SDK clients (OpenAI's `ChatCompletion`, Anthropic's
`Message`) without special-casing. A bare-text 200 reply would force callers to detect
the probe response out-of-band and branch their parsing, which defeats the "in-band"
property.

## Decision

**Wrap the probe's content text in the standard chat envelope for the inbound dialect.**

- OpenAI: `chat.completion` with one choice, `message.role:"assistant"`, the probe text
  as `message.content`, `finish_reason:"stop"`, zero `usage`.
- Anthropic: `type:"message"`, one `text` content block carrying the probe text,
  `stop_reason:"end_turn"`, zero `usage`.

The `model` field in both envelopes echoes the inbound model (not the resolved target),
matching what the caller asked for — the resolved target is named inside the content
text. `id` is a fresh synthetic value; `created` is the request timestamp.

## Alternatives Considered

- **Bare 200 text body** — rejected: unparseable by SDK clients; requires a custom
  out-of-band detection path.
- **Dialect-shaped *error* envelope** (e.g. 4xx with an `error.type:"who_probe"`) —
  rejected: the probe is a success-path affordance; surfacing it as an error trains
  clients to treat it as a failure.
- **Include provider `BaseUrl` and provider key in the body** — rejected: leaks
  internal config topology to callers, violating the fail-transparent principle and
  expanding the secret-leakage surface.

## Consequences

- Positive: SDK clients consume the probe as a normal chat reply; no client change
  required.
- Positive: the reply is trivially greppable in a transcript (`Imposter:` / `Passthrough:`
  prefixes).
- Negative: the probe's envelope is slightly larger than a bare string (~300 bytes for
  OpenAI, ~250 bytes for Anthropic); acceptable given the probe is a single request,
  not a high-QPS endpoint.
- Neutral: `usage` is zero in both dialects, which is honest (no upstream tokens) and
  also signals "this reply is synthetic" to any tooling that tracks cost.

### Addendum: OpenAI `/v1/responses` callers receive a `chat.completion` envelope

The probe is selected by **dialect**, not by inbound upstream path. A request to
`/v1/responses` (OpenAI's newer API surface) whose last user message is `who?` still
receives a `chat.completion` object — not a `response` object. Clients using the
official `openai` Responses SDK and routing to `/v1/responses` will see a parse
error on the synthetic reply.

This is an intentional, documented departure from `RoutingEndpoints.ShouldTranslateChatToResponses`,
which only distinguishes the two shapes on the real forward path. The probe is a
diagnostic affordance, not a production API: callers are expected to use it via the
chat-completion surface (the well-trodden `chat.completions.create(...)` entry point),
and paying for a third envelope shape + a third test surface for a probe that should
not be on a hot path is a poor trade. The asymmetry is locked here so a future
contributor does not read the silence as an oversight.

## Related

- **LADR-01** — depends on (seam provides `plan.InboundModel`, `plan.Decision`, dialect).
- **LADR-02** — independent; trigger shape does not affect response shape.
- **NFR-03** — the envelope's content text is audited against the no-secret-leakage rule.
