# AGENTS.md - Who-Message Introspection

AI Context: HLD for Who-Message Introspection (HLD 010). Updated: 2026-07-25

## TL;DR

Design for an in-band routing probe: sending `who?` as the last user message returns a
dialect-shaped synthetic reply describing the resolved route and auth scheme. Intent in
[README.md](./README.md), decisions in [ladrs/](./ladrs/), quality spec in [nfrs/](./nfrs/).

## Non-Negotiables

- **Do not make routing decisions based on `messages` content anywhere except the who-responder.**
  (Body-shape transformation by HLD 004 / HLD 006 transformers is fine; routing-shape inspection
  of `messages` is the who-responder's exclusive job.) The proxy's transparency property
  (HLD 001) depends on routing decisions staying opaque to message content; this HLD carves
  out exactly one sanctioned inspection point, and a second one added silently elsewhere
  would break the transparency invariant.
- **Do not synthesize SSE.** Streaming requests forward unchanged even when the last
  user message is `who?` (LADR-05). Adding streaming synthesis duplicates logic that
  already lives in the real transformers and drifts with every upstream format change.
- **Do not expose secrets, credentials, base URLs, or provider registry keys** in the
  reply. The content text carries only: inbound model, resolved target (or
  `passthrough`), and auth scheme name (NFR-03, LADR-03).
- **Reuse `ImposterRouter.DescribeAuth`** (promoted to `internal static`) for the auth
  string. Re-deriving the scheme precedence locally will drift from the forwarder's
  actual header.
- **Trigger is exact-match `who?` after trim, case-sensitive, last user message only.**
  Do not add regex, case-insensitive, or "any message in history" variants (LADR-02).
- **Feature is gated; default ON.** Do not hardcode enable or disable. The
  `Imposter:WhoMessage:Enabled` boolean (env `IMPOSTER_WHO_MESSAGE_ENABLED`) must be
  readable at request time, and `false` must skip the responder entirely — not just
  skip the reply.

## Architecture Decisions

| LADR | Decision | Why it matters |
|------|----------|----------------|
| [LADR-01](./ladrs/LADR-01-short-circuit-location.md) | Short-circuit inside the proxy, seam between `PlanAsync` and `SendAsync` | A separate endpoint would duplicate the resolver + auth logic and drift from the real forward path. |
| [LADR-02](./ladrs/LADR-02-trigger-shape.md) | Exact `who?` match on last user message | Regex / header / configurable triggers all raise false-positive or adoption-cost problems. |
| [LADR-03](./ladrs/LADR-03-response-envelope.md) | Dialect-shaped chat envelope | Bare text forces out-of-band client branching; SDK clients parse the reply unchanged. |
| [LADR-04](./ladrs/LADR-04-default-on-config.md) | Default-ON, env-overridable | Adoption cliff for a zero-cost feature if opt-in; toggle still needed for byte-identical proof. |
| [LADR-05](./ladrs/LADR-05-no-stream-synthesis.md) | Streaming requests pass through | SSE synthesis is high-drift, low-value; streaming callers re-issue as non-streaming. |

## Key Behaviors

- **Seam location.** The short-circuit sits *after* `router.PlanAsync` (so `RoutePlan`
  is available) and *before* `forwarder.SendAsync` (so no outbound call fires on match).
  Putting it before the plan loses the resolved target; putting it after the forwarder
  is too late.
- **Trigger is body-only.** `stream:true` in the body disables the short-circuit
  regardless of the message content. Header-only signals (`Accept: text/event-stream`)
  are not consulted — the forwarder keys off the body too.
- **Non-text last user content → no match.** A last user message built from image or
  tool parts does not fire the probe; content concatenation is text-parts-only.
- **`DescribeAuth` return value is the auth-scheme vocabulary.** The same tokens the
  log emits (`Bearer` / `ApiKey` / `caller-passthrough` / `none`) appear in the reply;
  do not invent a parallel vocabulary.

## Quality Constraints

See [nfrs/](./nfrs/) for measurable targets. The two that change how code is written:

- **NFR-01 (Transparency):** non-match + feature-disabled paths must be byte-identical
  to pre-HLD. The responder is not invoked when disabled.
- **NFR-03 (No secret leakage):** the responder's only credential dependency is the
  scheme-name string from `DescribeAuth`; it must not read `Secret` or
  `CredentialOverride.Secret`. L0 tests assert no substring of any configured secret
  appears in the reply.

## Changelog

| Date | Change | Ref |
| :---- | :---- | :---- |
| 2026-07-25 | Initial HLD AGENTS.md — 5 LADRs, 3 NFRs, 3 diagrams. | — |
