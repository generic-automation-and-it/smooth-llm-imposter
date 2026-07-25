# LADR-03: Dialect-shaped chat envelope, not bare text

**Status:** Accepted

## Context

The customized-switches feature family (`--who?` and `--newsession`) must be consumable
by existing SDK clients (OpenAI's `ChatCompletion`, Anthropic's `Message`) without
special-casing. A bare-text 200 reply would force callers to detect the response
out-of-band and branch their parsing, which defeats the "in-band" property. Both
switches therefore share the same envelope shape, with the content text being the
only discriminator.

## Decision

**Wrap each switch's content text in the standard chat envelope for the inbound dialect.**

- OpenAI: `chat.completion` with one choice, `message.role:"assistant"`, the switch
  text as `message.content`, `finish_reason:"stop"`, zero `usage`.
- Anthropic: `type:"message"`, one `text` content block carrying the switch text,
  `stop_reason:"end_turn"`, zero `usage`.

The `model` field in both envelopes echoes the inbound model (not the resolved target),
matching what the caller asked for — the resolved target is named inside the content
text. `id` is a fresh synthetic value with a switch-specific prefix so transcripts
can be greppable per switch:
- `--who?` → `chatcmpl-who-{guid:N}` (OpenAI) / `msg_who_{guid:N}` (Anthropic)
- `--newsession` → `chatcmpl-newsession-{guid:N}` (OpenAI) / `msg_newsession_{guid:N}` (Anthropic)

Content text by switch:
- `--who?` (imposter) → `Imposter: <inbound> → <target> (auth: <scheme>) session:<id>`
- `--who?` (passthrough) → `Passthrough: <inbound> (auth: <scheme>) session:null`
- `--newsession` → `Session: <callerId> → <syntheticId>`

The `session:<id>` field in the `--who?` content text is the persisted synthetic id
for the caller (so the caller can reuse the same id on subsequent requests), or
`session:null` for passthrough (which does not persist sessions).

## Alternatives Considered

- **Bare 200 text body** — rejected: unparseable by SDK clients; requires a custom
  out-of-band detection path.
- **Dialect-shaped *error* envelope** (e.g. 4xx with an `error.type:"who_probe"`) —
  rejected: the switches are success-path affordances; surfacing them as errors trains
  clients to treat them as failures.
- **Include provider `BaseUrl` and provider key in the body** — rejected: leaks
  internal config topology to callers, violating the fail-transparent principle and
  expanding the secret-leakage surface.
- **Distinct envelopes per switch (e.g. one for the probe, one for the session)** —
  rejected: doubles the SDK-client parsing surface area for no benefit; the content
  text is the discriminator.
- **Return the synthetic session id in a header instead of the body** — rejected:
  the response is a chat reply; callers parse chat reply bodies, not arbitrary
  response headers. Headers on a synthetic 200 are not part of any SDK contract.

## Consequences

- Positive: SDK clients consume every switch reply as a normal chat reply; no client
  change required.
- Positive: the reply is trivially greppable in a transcript (`Imposter:` /
  `Passthrough:` / `Session:` prefixes).
- Positive: the `chatcmpl-who-` / `msg_who_` / `chatcmpl-newsession-` /
  `msg_newsession_` id prefix split lets a single regex or grep locate a specific
  switch's replies in a stream of completions.
- Negative: each switch's envelope is slightly larger than a bare string (~300 bytes
  for OpenAI, ~250 bytes for Anthropic); acceptable given the switches are one-shot
  affordances, not a high-QPS endpoint.
- Neutral: `usage` is zero in both dialects, which is honest (no upstream tokens) and
  also signals "this reply is synthetic" to any tooling that tracks cost.

### Addendum: OpenAI `/v1/responses` callers receive a `chat.completion` envelope

The switches are selected by **dialect**, not by inbound upstream path. A request to
`/v1/responses` (OpenAI's newer API surface) whose last user message is `--who?` or
`--newsession` still receives a `chat.completion` object — not a `response` object.
Clients using the official `openai` Responses SDK and routing to `/v1/responses` will
see a parse error on the synthetic reply.

This is an intentional, documented departure from `RoutingEndpoints.ShouldTranslateChatToResponses`,
which only distinguishes the two shapes on the real forward path. The switches are
diagnostic affordances, not a production API: callers are expected to use them via the
chat-completion surface (the well-trodden `chat.completions.create(...)` entry point),
and paying for a third envelope shape + a third test surface for switches that should
not be on a hot path is a poor trade. The asymmetry is locked here so a future
contributor does not read the silence as an oversight.

## Related

- **LADR-01** — depends on (seam provides `plan.InboundModel`, `plan.Decision`, dialect).
- **LADR-02** — independent; trigger shape does not affect response shape (both
  switches share this envelope).
- **LADR-06** — the `session:<id>` field in the `--who?` content text is the
  synthetic id minted by an earlier `--newsession`; both switches read the same
  translation dictionary for consistency.
- **NFR-03** — the envelope's content text is audited against the no-secret-leakage rule.
