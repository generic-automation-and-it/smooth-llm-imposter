# LADR-04: Live upstream evals run as a separate, secret-gated PR gate (L3)

**Status:** Draft

<!-- Tactical LADR (how we verify). Status lifecycle: Draft → Prototype → Accepted. -->

## Context

This design's correctness depends on an **external** upstream's tool-validation contract
(characterized in [`../examples/upstream-tool-validation.md`](../examples/upstream-tool-validation.md)).
That contract can drift and can only be confirmed against the live upstream. The repo's existing
tests are deliberately **hermetic**: **L0** (in-process unit, no I/O) and **L2** (in-process Host via
`WebApplicationFactory` + stub transport; a WireMock container in CI) — no external network, no
secrets. Verifying the live contract needs real network plus the org `OPENCODE_API_KEY`, which is
slower, can flake or incur cost, and — per GitHub — is **not available to fork PRs**.

## Decision

**Run** live upstream evals as a **separate CI job (`pr_evals_gate`)**, distinct from the default
`dotnet test` / `pr-gate` and from the hermetic tiers. Classify them as **L3** — end-to-end against a
real external dependency — specifically *upstream-contract evals*: they assert the upstream's
acceptance rules and that a **normalized** request is accepted, at the **status/contract level**, not
model output text.

The gate:
- requires `OPENCODE_API_KEY` (org secret), passed via env and **never logged**;
- is **neutral (not failed)** when the secret is absent, so fork PRs degrade gracefully;
- starts **non-blocking / informational**, promotable to required once stable;
- asserts the negative + positive eval matrix (unsupported type/invalid name → 400; normalized
  toolset → 200).

The repo's tier vocabulary is extended: **L3 = live external/eval tests**, run only in `pr_evals_gate`.

## Alternatives Considered

- **Fold evals into `pr-gate` / L2** — rejected: injects external network, secrets, and flakiness
  into the hermetic gate, contradicting the infra-free test design.
- **Rely only on the static eval doc** — rejected: a point-in-time snapshot; upstream contract drift
  would silently break normalization with no signal.

## Consequences

- Upstream-contract drift and normalization regressions are caught on internal PRs; fork PRs stay green.
- Adds one CI job + an org-secret dependency scoped to that job; the core gate stays hermetic.
- A blocking policy is needed before promotion so upstream outages don't block merges.

## Open

- Promotion criteria from non-blocking → required (owner: maintainers; trigger: N consecutive stable runs).
- Where eval bodies live so the doc and the job can't drift (single source) — resolve at implementation.

## Related

- **NFR-04** — the conformance attribute this gate verifies.
- The eval doc in [`../examples/`](../examples/).
