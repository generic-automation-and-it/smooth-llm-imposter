# LADR-05: The `/responses`→Chat downgrade is bidirectional — translate the response stream back

**Status:** Accepted

<!-- Status lifecycle: Draft → Prototype → Accepted. Also: "Superseded by LADR-MM", "Deprecated". -->

> **Amends LADR-02** (request-only; prefer removal over remap) and **narrows HLD 001 LADR-003**
> (no response rewrite) for one scoped path. It does **not** touch transparent/passthrough routes.

## Context

HLD 004 lets a provider set `OpenAiUpstreamApi: chat_completions`, which downgrades an inbound
OpenAI **Responses** request (`/responses`, with `input`/`instructions`) to a **Chat Completions**
request (`/v1/chat/completions`, with `messages`). That edit is request-only by LADR-02.

But the client that sent `/responses` is a **Responses-dialect client** (e.g. Codex): it expects a
Responses **event stream** back (`response.created`, `response.output_text.delta`,
`response.completed`, …). A strict Chat upstream returns **Chat Completions** SSE
(`chat.completion.chunk` with `choices[].delta`). The proxy relays those bytes verbatim, so the
client receives a `200` stream it cannot parse — the turn silently produces no output. Empirically
confirmed against `opencode-go`/kimi: request accepted (200), response unreadable by the client.

The request-side downgrade is therefore a **one-directional half-bridge**. LADR-02's "prefer removal
over remap" cannot fix this: there is no element to *remove* — the response **wire shape** itself
must be translated, or the downgrade path is unusable for its only intended client. A Responses-mode
client cannot switch `wire_api` per-routed-model; its normal/default flow pins a single wire API.

## Decision

**Make the downgrade bidirectional.** Whenever the router downgrades a `/responses` request to Chat
Completions (matched imposter **and** `OpenAiUpstreamApi: chat_completions` **and** the inbound path
was `/responses`), it **must also translate the upstream Chat Completions response back into
Responses-API events**. The bridge is bidirectional or it is nothing — a downgraded request without
a re-upgraded response is never shipped.

Hard constraints that keep this compatible with the streaming non-negotiable:

- **Incremental, never buffered.** The translator is a line-by-line transform over SSE `data:`
  frames: each upstream chunk is mapped to its Responses event(s) and flushed immediately. It never
  reads the response to completion before emitting. First-byte latency, unbuffered relay, the
  infinite-timeout client, and caller-cancellation all survive.
- **Bounded, forward-only state.** Per-stream state is O(active items + open tool-call args) — the
  current output item / content part, per-index tool-call argument accumulation, ids, and usage. No
  whole-stream accumulation, no replay, no look-back.
- **Scoped.** Applies only on the path above. Passthrough/default routes, `responses` upstreams, the
  Anthropic dialect, and a client that called `/chat/completions` directly are **byte-for-byte**
  relayed, exactly as before. The non-streaming (`stream:false`) case maps the Chat Completion object
  to a Responses object analogously.

This **amends LADR-02**: the response path may now *remap* on this one path. Removal-over-remap still
governs the request-side **tool** handling (LADR-02 stands there). It **narrows HLD 001 LADR-003**
from "never read or rewrite the response" to "never **buffer** or **replay** the response, and never
touch it on transparent/passthrough routes" — a bounded, forward-only, incremental transform on an
explicitly-downgraded route is permitted.

## Alternatives Considered

- **Per-model client `wire_api` (configure the client to speak Chat)** — rejected: a Responses-mode
  client pins one wire API for its normal/default flow; it cannot select Chat for only the
  imposter-routed model. This is the actual operational constraint that forces the proxy to translate.
- **Keep request-only; document that Responses clients can't use Chat upstreams** — rejected: it
  defeats HLD 004's purpose (vanilla client, proxy owns upstream compatibility) and ships a path that
  returns 200 but never works.
- **Buffer the full response, translate, then emit** — rejected: reintroduces the buffering /
  long-generation-timeout failure mode that HLD 001 LADR-003 exists to prevent. The translation must
  be incremental.

## Consequences

- The router gains its **first response-side logic** — a bounded, forward-only, per-stream state
  machine. It must stay streaming; any buffering reintroduces the LADR-003 hazard.
- HLD 001 LADR-003 is **narrowed, not discarded**, and `ROUTING_AGENTS.md` must record the narrowing
  as a third boundary (alongside the request-rewrite classes).
- HLD 001 **LADR-007** (rename + response remap, rejected for streaming cost) is partially vindicated
  for **wire-shape** remap: a disciplined incremental remap is accepted here, while tool-name handling
  stays "removal, not rename" (LADR-02).
- The translator is the response-side mirror of the request-side `ToChatCompletions`; both are gated
  by the same route conditions, so the two cannot drift.

## Related

- **LADR-02** — amended here (response may remap on this path; removal still governs request tools).
- **LADR-03** — the per-provider `chat_completions` opt-in that triggers the downgrade this completes.
- **HLD 001 LADR-003** — the streaming non-negotiable, narrowed (no buffer/replay) rather than broken.
- **HLD 001 LADR-007** — the rename+remap approach, partially adopted for wire-shape only.
- **NFR-05** — the streaming-integrity quality bar this decision must meet.
