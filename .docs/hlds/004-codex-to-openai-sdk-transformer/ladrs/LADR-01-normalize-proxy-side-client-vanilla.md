# LADR-01: Normalize on the proxy side so the client stays vanilla

**Status:** Accepted

<!-- Status lifecycle: Draft → Prototype → Accepted. Also: "Superseded by LADR-MM", "Deprecated". -->

> **Supersedes HLD 001 LADR-006** ("No in-proxy tool-name sanitization; preserve the transparent
> proxy"). That decision resolved upstream-compatibility conflicts client-side. This HLD reverses
> that direction: the router takes ownership of upstream compatibility so clients stay unmodified.

## Context

Agent clients emit OpenAI-dialect requests shaped for OpenAI's own backend. Strict
OpenAI-compatible imposter upstreams validate more tightly and reject some of those shapes, failing
the request before any work happens. The two ways to reconcile this are: reconfigure each client
per-upstream (LADR-006's choice), or have the router reshape the request centrally. The user
requires clients to remain vanilla, so the reconciliation moves into the router.

## Decision

**Adopt** a proxy-side request-normalization seam on matched OpenAI imposter routes. This
introduces a **third sanctioned request-rewrite class** in the forwarder's contract, alongside the
existing two (managed auth, caching injection): a normalization that reshapes the inbound request
body so a strict upstream accepts it.

The seam is scoped to the OpenAI dialect and to matched imposter routes only, and is governed by
the request-only boundary (LADR-02) and a per-provider opt-in (LADR-03). It does not apply to
passthrough/default routes, which remain byte-transparent. What the seam *does* to the request —
the individual normalizations — is intentionally not decided here; each is its own later increment.

## Alternatives Considered

- **Keep LADR-006 (client-side only)** — rejected: requires per-upstream client reconfiguration,
  which contradicts the vanilla-client requirement.
- **Per-client bespoke handling outside the router** — rejected: pushes upstream knowledge into
  every client and is not centrally enforceable.

## Consequences

- Clients stay vanilla; upstream-compatibility knowledge is centralized in one place.
- The transparent-proxy non-negotiable in `ROUTING_AGENTS.md` gains a third documented exception;
  it must be updated, and the boundary (LADR-02) is what keeps "transparent" meaningful.
- HLD 001 LADR-006 is superseded and must carry a supersession note.

## Related

- **LADR-02** — constrains this seam to request-only edits.
- **LADR-03** — gates this seam behind per-provider opt-in.
- **HLD 001 LADR-003** — the streaming/no-response-rewrite stance this design preserves.
- **HLD 001 LADR-007 (Draft)** — the rename+response-remap approach this design deliberately avoids.
