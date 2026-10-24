# LADR-02: Shared ownership without operator configuration churn

**Status:** Draft

## Context

- At drafting (2026-10-22), existing deployments used seven DECISIONS_TOOLS variables and the corresponding structured options; HLD 014 has since added more.
- Tool enablement and probability threshold are consumer policy; destination, default model and transport budgets are not.
- Renaming settings during extraction would add migration risk without a second production consumer.
- The shipped resilience profile falls back to fixed attempt and timeout values whenever tool filtering is disabled, so the transport's availability is still switched by tool enablement.
- The shipped client resolves its destination, path and timeout from the routing feature's options type rather than a consumer-neutral one.
- Startup validation of decision settings is gated on tool filtering's enable flag; no any-consumer predicate exists.

## Decision

**Separate** logical ownership while preserving the existing operator surface. The integration owns the resolved destination, path, default model, maximum attempts and timeout. HLD 014 owns Enabled and MinProbability. Existing flat and structured names, defaults and conventional-over-structured precedence retain their documented effects on tool filtering; no second naming surface is introduced by this extraction.

The shared client must not use tool enablement as its availability switch. Configuration is validated when an opted-in consumer requires the integration. With all consumers disabled, invalid unused decision settings remain ignored; activating a consumer requires an absolute HTTP(S) destination and valid transport budgets. A consumer cannot mutate the shared service settings for other callers.

Validation is gated by an opt-in predicate any consumer can satisfy, never by one consumer's enable flag. The transport profile resolves independently of the tool filter's enable flag. Transport settings resolve outside the routing options type.

One startup-scoped service profile is sufficient. Multiple destinations, hot reload and per-consumer transport overrides are outside this design. Consumer-specific model selection is request data, not a second transport profile.

## Consequences

- Existing deployments need no settings migration to preserve tool-filter behavior.
- Legacy tool-prefixed names remain temporarily awkward for a shared capability.
- Disabling tool filtering does not disable another independently opted-in consumer.

## Open

- Maintainers revisit neutral operator names and explicit alias precedence when a second production consumer is specified; that change requires a separate compatibility decision.
