# Who-Message Introspection — High-Level Design

| | |
|---|---|
| **Status** | In Design — base `who?` probe Completed (LADRs 01/05, NFRs 01/02/03 Accepted); `--who?` / `--newsession` switch family + translation dictionary Draft (LADR-02/03/04 `Draft (revised)`, LADR-06 + NFR-04 `Draft`) |
| **Owner** | SmoothLlmImposter maintainers |
| **Tracker** | _None — completed without a tracker issue (NO-TICKET)_ |
| **Last updated** | 2026-07-25 |

> Design HLD. This document delivers **intent + spec** — what we are
> building and why, the decisions behind it, and the quality bar it must meet. It does
> **not** contain an implementation plan; execution (phasing, sub-issues, sequencing) is
> tracked in the issue tracker (worktask list).

## TL;DR

Three switches — `who?`, `--who?`, `--newsession` — short-circuit the forward path when the last user message matches exactly (trimmed, case-sensitive, non-streaming). `who?` returns routing + auth info; `--who?` adds session identity; `--newsession` mints a synthetic session id and stores the caller→synthetic mapping in a process-lifetime dictionary that translates session ids on subsequent forwards. Diagnostic logging (Debug level) in `WhoMessageResponder` reports each non-match reason; `RoutingEndpoints` logs when the feature is disabled or a translation is applied.

## Intent

SmoothLlmImposter is a transparent, stateless, key-less router. Callers — agent clients
in particular — currently have no in-band way to confirm which upstream model their
request resolved to, or which auth scheme is being used, without tailing server logs or
reproducing the config locally. This HLD introduces an in-band **customized switches**
feature: a chat message whose last user content is exactly one of the configured
switches (currently `--who?` for the routing probe and `--newsession` for a
session-id mint + translate feature) short-circuits the upstream and returns a
dialect-shaped synthetic reply. The whole feature family is config-gated
(`Imposter:WhoMessage:Enabled`, default `true`) so operators can opt out.

## Key Goals

### 1. In-band routing probe

A request whose last user message is the exact string `who?` (trimmed) is intercepted
between the router's plan step and the upstream forwarder. The proxy returns a 200
chat reply whose content text names the inbound model, the resolved upstream target
(or `passthrough`), and the resolved auth scheme. No upstream HTTP call is made for
that request — the probe costs zero upstream tokens and round-trips in the same
latency class as a local `/v1/models` response. *(A `--who?` / `--newsession`
switch-family and a `session:<id>` envelope field are part of a proposed
extension — see the "Implementation status" note above; they are not the live
contract today.)*

**Acceptance criteria / DoD**

- POST to `/openai/v1/chat/completions` or `/anthropic/v1/messages` with last user
  content `who?` returns HTTP 200 with a synthetic body.
- The upstream stub in integration tests is never invoked for a matched `who?` request.
- The reply content text matches the format
  `Imposter: <inbound> → <target> (auth: <scheme>)` for imposter routes, or
  `Passthrough: <inbound> (auth: <scheme>)` for passthrough.

### 2. Dialect fidelity

The synthetic reply uses the same wire envelope as a real completion for that dialect,
so existing clients parse it unchanged. OpenAI-dialect requests receive a
`chat.completion` object with one choice and `finish_reason:"stop"`. Anthropic-dialect
requests receive a `type:"message"` object with one `text` content block and
`stop_reason:"end_turn"`. Usage fields are zero — no tokens were consumed.

**Acceptance criteria / DoD**

- An OpenAI SDK client can deserialize the synthetic reply as a normal `ChatCompletion`.
- An Anthropic SDK client can deserialize the synthetic reply as a normal `Message`.
- `usage` fields in both dialects report zero values (no prompt/completion tokens).

### 3. Streaming exclusion

Requests with `stream: true` are **not** intercepted, even when the last user message
is a configured switch (today `who?`; future `--who?` / `--newsession`). SSE synthesis
would require fabricating the chunked delta protocol per dialect for negligible
benefit; streaming callers who want a probe can simply send it as a non-streaming
request.

**Acceptance criteria / DoD**

- `stream: true` + `who?` (or `--who?` / `--newsession` once implemented) reaches the
  upstream stub in integration tests.
- No SSE synthesis code exists in the short-circuit path.

### 4. Config-gated, default-ON

`Imposter:WhoMessage:Enabled` is a boolean under the existing `Imposter` options root,
defaulting to `true`. The conventional env override is `IMPOSTER_WHO_MESSAGE_ENABLED`
(parsed as bool via the root-level `ApplyRootBooleanOverride` helper in
`ImposterOptionsPostConfigure.cs`; invalid values log a Warning and leave the bound
value unchanged, mirroring the bool.TryParse semantics of the per-provider
`_IS_DEFAULT` / `_ENABLED` overrides). Setting it to `false` disables the
short-circuit; the request is forwarded verbatim.

The toggle is read via `IOptions<ImposterOptions>` at the endpoint seam, so flipping
the env var **requires a host restart** — consistent with every other `Imposter`
option in the codebase today.

**Acceptance criteria / DoD**

- A default boot (no config override) intercepts `who?`.
- `IMPOSTER_WHO_MESSAGE_ENABLED=false` causes a `who?` request to be forwarded.
- An invalid env value (e.g. `yes_please`) does not crash boot and is logged at Warning.

### 5. Transparency preserved

Any request whose last user message is **not** an exact-match configured switch is
forwarded byte-identically to the pre-HLD behavior — no content inspection, no body
mutation, no new headers. The `messages` array is parsed only when the feature is
enabled; when disabled, the responder is not invoked at all.

**Acceptance criteria / DoD**

- Integration tests for the existing imposter/passthrough/SSE paths continue to pass
  unchanged with the feature enabled.
- The forwarder's request body in non-match cases is byte-identical to the inbound body.
- Disabling the feature adds zero new parsing on the hot path.

### 6. Session-id mint + translation (`--newsession`)

A request whose last user message is the exact string `--newsession` is intercepted.
The proxy:

1. Resolves the **caller-supplied session id** from the request — same resolution order
   as the existing HLD 009 session-identity forwarder (`prompt_cache_key` body →
   `metadata.user_id` → `user` body, then headers `session_id` / `x-opencode-session` /
   `x-session-id` / `conversation_id`; if none is present, the probe is rejected —
   `--newsession` requires a stable caller key to be useful).
2. Generates a **synthetic session id** (UUID).
3. **Persists** the pair `(callerId, syntheticId)` in a process-lifetime in-memory
   dictionary (`ConcurrentDictionary<string, string>`). The dictionary grows for
   process lifetime — no TTL, no eviction, no clear. The user expects volumes to be
   small.
4. Returns a 200 chat reply whose content text is `Session: <callerId> → <syntheticId>`
   (same dialect-shaped envelope as `--who?`).

On **subsequent** requests that are *not* a switch match, when the resolver produces a
session id equal to a key in the dictionary, the proxy **translates it to the stored
synthetic id** before stamping it on the outbound request. This is the same shape as
HLD 009 session-identity forwarding, but the override source is the in-memory
dictionary instead of the captured/derived resolver. The translation only fires when
the feature is enabled; with `Imposter:WhoMessage:Enabled=false` the dictionary is
bypassed and the original captured/derived value passes through unchanged.

**Acceptance criteria / DoD**

- POST with last user content `--newsession` and a caller-supplied `session_id` header
  returns 200 with content `Session: <callerId> → <syntheticId>`.
- A second POST carrying the same caller-supplied id (no probe match) reaches the
  upstream, and the outbound request carries the **synthetic** id, not the caller id.
- Two `--newsession` requests with two different caller ids produce two distinct
  dictionary entries; the dictionary is process-lifetime and the test asserts the
  entries survive across the same process.
- A `--newsession` request with **no** caller-supplied id (no header, no body field)
  does **not** match — the responder returns no match and the request forwards.
- The same gate `Imposter:WhoMessage:Enabled=false` disables both the probe and the
  translation; the dictionary is unused.

### 7. Switch registration (forward-compatible) — future HLD may consider

The two switches are hardcoded constants today (`--who?`, `--newsession`) so the
behavior is fixed and stable for callers. A future HLD may consider moving the
switch table to config (an `Imposter:WhoMessage:Switches` array of
`{trigger, kind, responseShape}` objects) without churning the gate, the seam, or
the translation dictionary. This is **not** a near-term extension point — it is
captured here so a future contributor does not invent the design ad-hoc.

**Acceptance criteria / DoD** (for the future HLD, not this one)

- Today: two switches registered (`--who?`, `--newsession`), both exact-match, both
  non-streaming, both gated by the same `Imposter:WhoMessage:Enabled` toggle.
- A future third switch (e.g. `--key=value`) can be added by editing the responder's
  switch table — no change to `ImposterOptions`, no new env var, no new LADR in
  this HLD.

## Core Separation of Concerns

> The customized-switches short-circuit is the **one** place the proxy reads `messages`
> content. It lives in Application as a string-in / string-out responder, invoked from
> a single seam in the Host endpoint between `router.PlanAsync` and `forwarder.SendAsync`.
> The in-memory session-id translation dictionary sits alongside the existing HLD 009
> session-identity resolver on the same seam — but the dictionary only fires on the
> *non-match* forward path, never on the short-circuit reply.

No other component inspects message text; the rest of the pipeline remains an opaque
proxy. The responder reuses `ImposterRouter.DescribeAuth` (made `internal static`) so
the auth-scheme string in the reply cannot drift from the string written to the log and
to the wire — one source of truth for the same resolved value. The session-id
dictionary reuses the existing `SessionIdentityResolver` resolution order; the
dictionary is an override source on the same seam, not a parallel resolver.

## Guiding Principle — Fail transparent

> When the trigger does not match, behave exactly as if this HLD did not exist.

- On non-match, the request continues to the forwarder with no body mutation, no header
  mutation, and no log line referencing the probe.
- On match, the reply exposes only what is already logged at Information level: model
  names, target name, auth scheme. Never a secret, credential, masked fragment, base
  URL, or internal provider key.

---

## Diagrams

- [System Context (C1) + sequence + flowchart](./diagrams/c4-context.md)

## Architecture Decisions (LADRs)

LADRs 01, 05, and 06 are strategic (*what* and *why*); LADR-04 is tactical (*how*). LADR-02 and LADR-03 are content-shape decisions that bridge strategy and tactics. Each is a
single decision — a horizontal concern spanning this HLD. See [`./ladrs/`](./ladrs/).

> **Note (2026-07-25):** The LADR taxonomy distinguishes strategic (*what/why*),
> tactical (*how*), and bridge (content-shape) decisions. LADR-01, 05, and 06 are
> strategic; LADR-04 is tactical; LADR-02 and LADR-03 bridge the two because they
> govern content shape (a tactical concern) in service of the strategic decision of
> what content is allowed.

| LADR | Decision | Status |
|------|----------|--------|
| [LADR-01](./ladrs/LADR-01-short-circuit-location.md) | Short-circuit inside the proxy, not a sidecar endpoint | Accepted |
| [LADR-02](./ladrs/LADR-02-trigger-shape.md) | Exact-match `"--who?"` or `"--newsession"` on the last user message (proposed; live is `who?`) | Accepted |
| [LADR-03](./ladrs/LADR-03-response-envelope.md) | Dialect-shaped chat envelope, not bare text (proposed; live omits `session:`) | Accepted |
| [LADR-04](./ladrs/LADR-04-default-on-config.md) | Default-ON opt-out config (proposed; shared by both switches AND the dictionary) | Accepted |
| [LADR-05](./ladrs/LADR-05-no-stream-synthesis.md) | No SSE synthesis — streaming requests pass through | Accepted |
| [LADR-06](./ladrs/LADR-06-session-translation-dictionary.md) | In-memory `ConcurrentDictionary` translates caller-supplied session ids to stored override ids on the forward path | Accepted |

## Non-Functional Requirements

Each NFR is a horizontal quality concern spanning the whole design, with a measurable
target, a verification mechanism, and acceptance criteria. See [`./nfrs/`](./nfrs/).

| NFR | Attribute | Target (summary) | Status |
|-----|-----------|------------------|--------|
| [NFR-01](./nfrs/NFR-01-transparency.md) | Transparency | Non-match path byte-identical to pre-HLD | Accepted |
| [NFR-02](./nfrs/NFR-02-no-upstream-cost.md) | Efficiency | Zero upstream HTTP calls on match | Accepted |
| [NFR-03](./nfrs/NFR-03-no-secret-leakage.md) | Security | Response contains no secret, credential, or masked fragment | Accepted |
| [NFR-04](./nfrs/NFR-04-process-lifetime-dictionary.md) | Process-lifetime dictionary | Translation dictionary does not evict; entries survive process lifetime | Accepted |

## Changelog

| Date | Change | Ref |
| :---- | :---- | :---- |
| 2026-07-25 | Initial draft — intent, 5 goals, 5 LADRs, 3 NFRs, 3 diagrams. | — |
| 2026-07-25 | Implemented: `WhoMessageResponder` + endpoint seam + `Imposter:WhoMessage:Enabled` (default `true`) + env override `IMPOSTER_WHO_MESSAGE_ENABLED`. 17 L0 + 5 L2 tests pass. LADRs/NFRs → Accepted; HLD → Completed. | — |
| 2026-07-25 | Extended design (NOT YET IMPLEMENTED): proposed trigger is `--who?` (live is `who?`); proposed `--newsession` switch for session-id mint + in-memory translation; proposed `session:<id>` envelope field. New LADR-06, new NFR-04, new goal 6 (session-id mint + translation) and goal 7 (switch registration). LADR-02/03/04 marked `Draft (revised)`; LADR-06 and NFR-04 stay `Draft`. **HLD is in design** — implementation lands in a follow-up commit. | — |
| 2026-07-25 | LADR-05 reclassification note — clarifies the strategic / tactical / bridge split; no status change. | — |
