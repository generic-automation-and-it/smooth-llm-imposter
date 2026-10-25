# LADR-01: Global opt-in includes passthrough

**Status:** Accepted

## Context

- Large agent catalogs occur on both rewritten and real-provider routes.
- Provider-specific selection would make the operator's optimization depend on model routing.
- The router's existing transparency contract excludes content-based selection.

## Decision

**Adopt** one default-off, startup-scoped global decision-tools stage. Enabling it deliberately permits tool-catalog/body-choice rewriting on passthrough as well as imposter routes; no provider flag is required.

This narrows [HLD 001 LADR-003](../../001-llm-imposter-routing/ladrs/LADR-003-infinite-timeout-no-resilience-handler.md)'s inference transport scope: the auxiliary decision service has its own bounded complete-response policy. It does not change upstream auth, response streaming or same-dialect routing. HLD 010's exclusive content-reader claim is amended: the switch responder and this filter are the sanctioned prompt-text inspectors.

## Consequences

- One operator switch covers every supported route.
- Opting in changes passthrough request bodies and introduces classifier egress.
- Disabled behavior remains transparent under [NFR-01](../nfrs/NFR-01-disabled-transparency.md).
