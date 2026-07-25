# LADR-02: Request-only normalization; prefer removal over remap

**Status:** Accepted — **amended by LADR-05**

<!-- Status lifecycle: Draft → Prototype → Accepted. Also: "Superseded by LADR-MM", "Deprecated". -->

> **Amended by LADR-05.** "Request-only" holds for **tool normalization** (this decision's subject) and
> for every transparent/passthrough route. It does **not** hold for the `/responses`→Chat *downgrade*
> path, where LADR-05 requires translating the response stream back to Responses events — a wire-shape
> remap that cannot be expressed as a removal. Removal-over-remap still governs request-side tools.

## Context

The router streams the upstream response back unbuffered and never rewrites it (HLD 001 LADR-003);
a half-streamed response cannot be safely re-read or replayed. Some compatibility fixes are tempting
to implement by altering a request value and then mapping the altered value *back* when the upstream
echoes it in the response — which would require buffering/parsing the SSE stream and holding
per-request state across response deltas. That breaks the streaming non-negotiable.

## Decision

**Constrain** all normalization to the **request only**. The response stream is relayed unchanged
and is never an input to normalization.

When a candidate normalization would otherwise need the upstream-altered value mapped back through
the response, the design instead **removes** the offending request element rather than remapping it.
Removal keeps the transform one-directional: an element that was never sent cannot be echoed back, so
no reverse map and no response rewriting are required. The trade-off — a removed element is a removed
capability for that request — is accepted as the cost of preserving transparent streaming, and is
documented per-normalization where it applies.

## Alternatives Considered

- **Rewrite-and-remap (request + streaming response rewrite)** — rejected: stateful SSE rewriting
  across partial deltas is fragile and breaks LADR-003 (this is the HLD 001 LADR-007 draft approach).
- **Buffer the full response, transform, then emit** — rejected: defeats unbuffered streaming and
  can exceed timeouts on long generations.

## Consequences

- The streaming non-negotiable (LADR-003) is preserved; no response-path code is added.
- No per-request state needs to survive into the response stream.
- Normalizations are limited to what can be expressed as request edits; some fixes are therefore
  "remove" rather than "translate", with a documented capability cost.

## Related

- **LADR-01** — this is the boundary that keeps the proxy-side seam compatible with transparency.
- **HLD 001 LADR-003** — infinite-timeout / no-response-rewrite streaming stance.
