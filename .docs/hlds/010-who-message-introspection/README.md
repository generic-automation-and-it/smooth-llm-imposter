# Who-Message Introspection — High-Level Design

| | |
|---|---|
| **Status** | Completed |
| **Owner** | SmoothLlmImposter maintainers |
| **Tracker** | _None — completed without a tracker issue (NO-TICKET)_ |
| **Last updated** | 2026-07-25 |

> Design HLD. This document delivers **intent + spec** — what we are
> building and why, the decisions behind it, and the quality bar it must meet. It does
> **not** contain an implementation plan; execution (phasing, sub-issues, sequencing) is
> tracked in the issue tracker (worktask list).

## Intent

SmoothLlmImposter is a transparent, stateless, key-less router. Callers — agent clients
in particular — currently have no in-band way to confirm which upstream model their
request resolved to, or which auth scheme is being used, without tailing server logs or
reproducing the config locally. This HLD introduces an in-band probe: a chat message
whose last user content is exactly `who?` short-circuits the upstream and returns a
dialect-shaped synthetic reply naming the resolved route and auth scheme. The feature is
config-gated (`Imposter:WhoMessage:Enabled`, default `true`) so operators can opt out.

## Key Goals

### 1. In-band routing probe

A request whose last user message is the exact string `who?` is intercepted between
the router's plan step and the upstream forwarder. The proxy returns a 200 chat reply
whose content text names the inbound model, the resolved upstream target (or
`passthrough`), and the resolved auth scheme. No upstream HTTP call is made for that
request — the probe costs zero upstream tokens and round-trips in the same latency class
as a local `/v1/models` response.

**Acceptance criteria / DoD**

- POST to `/openai/v1/chat/completions` or `/anthropic/v1/messages` with last user
  content `who?` returns HTTP 200 with a synthetic body.
- The upstream stub in integration tests is never invoked for a matched `who?` request.
- The reply content text matches the format `Imposter: <inbound> → <target> (auth: <scheme>)`
  for imposter routes, or `Passthrough: <inbound> (auth: <scheme>)` for passthrough.

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
is `who?`. SSE synthesis would require fabricating the chunked delta protocol per
dialect for negligible benefit; streaming callers who want the probe can simply send it
as a non-streaming request.

**Acceptance criteria / DoD**

- `stream: true` + `who?` reaches the upstream stub in integration tests.
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

Any request whose last user message is **not** the exact trigger is forwarded
byte-identically to the pre-HLD behavior — no content inspection, no body mutation, no
new headers. The `messages` array is parsed only when the feature is enabled; when
disabled, the responder is not invoked at all.

**Acceptance criteria / DoD**

- Integration tests for the existing imposter/passthrough/SSE paths continue to pass
  unchanged with the feature enabled.
- The forwarder's request body in non-match cases is byte-identical to the inbound body.
- Disabling the feature adds zero new parsing on the hot path.

## Core Separation of Concerns

> The who-message short-circuit is the **one** place the proxy reads `messages` content.
> It lives in Application as a string-in / string-out responder, invoked from a single
> seam in the Host endpoint between `router.PlanAsync` and `forwarder.SendAsync`.

No other component inspects message text; the rest of the pipeline remains an opaque
proxy. The responder reuses `ImposterRouter.DescribeAuth` (made `internal static`) so
the auth-scheme string in the reply cannot drift from the string written to the log and
to the wire — one source of truth for the same resolved value.

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

LADRs 01–03 are strategic (*what* and *why*); 04–05 are tactical (*how*). Each is a
single decision — a horizontal concern spanning this HLD. See [`./ladrs/`](./ladrs/).

| LADR | Decision | Status |
|------|----------|--------|
| [LADR-01](./ladrs/LADR-01-short-circuit-location.md) | Short-circuit inside the proxy, not a sidecar endpoint | Accepted |
| [LADR-02](./ladrs/LADR-02-trigger-shape.md) | Exact-match `"who?"` on the last user message | Accepted |
| [LADR-03](./ladrs/LADR-03-response-envelope.md) | Dialect-shaped chat envelope, not bare text | Accepted |
| [LADR-04](./ladrs/LADR-04-default-on-config.md) | Default-ON opt-out config | Accepted |
| [LADR-05](./ladrs/LADR-05-no-stream-synthesis.md) | No SSE synthesis — streaming requests pass through | Accepted |

## Non-Functional Requirements

Each NFR is a horizontal quality concern spanning the whole design, with a measurable
target, a verification mechanism, and acceptance criteria. See [`./nfrs/`](./nfrs/).

| NFR | Attribute | Target (summary) | Status |
|-----|-----------|------------------|--------|
| [NFR-01](./nfrs/NFR-01-transparency.md) | Transparency | Non-match path byte-identical to pre-HLD | Accepted |
| [NFR-02](./nfrs/NFR-02-no-upstream-cost.md) | Efficiency | Zero upstream HTTP calls on match | Accepted |
| [NFR-03](./nfrs/NFR-03-no-secret-leakage.md) | Security | Response contains no secret, credential, or masked fragment | Accepted |

## Changelog

| Date | Change | Ref |
| :---- | :---- | :---- |
| 2026-07-25 | Initial draft — intent, 5 goals, 5 LADRs, 3 NFRs, 3 diagrams. | — |
| 2026-07-25 | Implemented: `WhoMessageResponder` + endpoint seam + `Imposter:WhoMessage:Enabled` (default `true`) + env override `IMPOSTER_WHO_MESSAGE_ENABLED`. 17 L0 + 5 L2 tests pass. LADRs/NFRs → Accepted; HLD → Completed. | — |
