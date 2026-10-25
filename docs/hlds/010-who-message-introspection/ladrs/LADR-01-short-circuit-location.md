# LADR-01: Short-circuit inside the proxy, not a sidecar endpoint

**Status:** Accepted

## Context

The routing probe needs to see the same `RouteDecision` the forward path sees — inbound
model, resolved target, auth scheme, credential override — so the reply is byte-consistent
with what would actually have been forwarded. A separate endpoint (e.g. `GET /who`) would
have to re-derive the decision from a supplied model and headers, and would risk drifting
from the forward path.

## Decision

**Place the short-circuit inside the existing forward path**, as a seam between
`router.PlanAsync` and `forwarder.SendAsync` in the Host's `RoutingEndpoints.HandleAsync`.
The seam reads the already-computed `RoutePlan`, delegates to an Application-layer
`IWhoMessageResponder` (string-in / string-out), and writes the response and returns —
the forwarder is never called for a match.

The responder reuses `ImposterRouter.DescribeAuth` (made `internal static`) so the auth
scheme string in the reply is the same value the forwarder would have written to the
upstream auth header and the router would have logged.

## Alternatives Considered

- **Separate `GET /who` endpoint** — rejected: would need to duplicate the resolver +
  auth-scheme precedence, and the reply could drift from the actual forward path.
- **Sidecar microservice** — rejected: the feature is a few dozen lines; the operational
  overhead of a second deployable exceeds the value.
- **Admin-API introspection** — rejected: the admin API already exists but is
  out-of-band; the probe's value is in-band, from the same client that sent the chat.

## Consequences

- Positive: one source of truth for the routing decision; the probe's reply cannot
  disagree with the forwarder's behavior.
- Positive: no new HTTP surface area (no new endpoint to secure, document, or test).
- Negative: the forward-path handler grows one more branch; readability depends on the
  short-circuit being clearly named and commented.
- Neutral: the feature is gated on a boolean config so it can be disabled without a
  code change.

## Related

- **LADR-02** — trigger shape (depends on: LADR-01 places the seam; LADR-02 decides what activates it).
- **LADR-03** — response envelope (depends on: LADR-01 provides the decision inputs; LADR-03 decides the output shape).
