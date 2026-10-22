# LADR-05: Classify the effective body at the forward seam

**Status:** Accepted

## Context

- Who-probes synthesize local replies and must not incur decision-service cost.
- Normalization and Responses-to-Chat conversion can change catalog/body shape before forwarding.
- Host owns caller cancellation; selection rules belong in Application.

## Decision

**Invoke** the filter after the who-probe short circuit and session translation, immediately before inference forwarding, inside the existing caller-abort guard. Supply the post-transform body and the effective upstream path; a Responses-to-Chat downgrade is classified as Chat.

Host invokes the filter unconditionally. The filter owns the sole enable gate and path allowlist, preventing endpoint-specific behavior drift. It returns a body string without taking HTTP context, forwarding headers or selecting a provider.

## Consequences

- Prefixed and legacy routes share one selection seam.
- Decisions describe the catalog actually sent upstream.
- The who-probe still has zero external-call cost.
