# LADR-004: `PUT`/`DELETE /routing/{dialect}/override-authorization`, admin-authed

**Status:** Accepted

<!-- Tactical: the HTTP shape of the toggle. -->

## Context

The toggle must be flippable from a bare terminal `curl` with no query string and no body. A `GET` was the
first instinct ("trigger from a curl") but a `GET` is for reading, not changing state. The operation also
changes how the router authenticates to upstreams, so it is a privileged control surface — it cannot share
the unauthenticated routing surface that HLD 001/002 keep key-less.

## Decision

**Expose** the switch as a resource keyed by dialect:

- `PUT /routing/{dialect}/override-authorization` — enable the override (idempotent set-on).
- `DELETE /routing/{dialect}/override-authorization` — disable it (idempotent set-off).
- `GET /routing/{dialect}/override-authorization` — report current on/off state.

`{dialect}` is `anthropic` or `openai`; any other value is rejected (`400`/`404`). No request body or query
parameter is read. All three require the existing **`X-Admin-Api-Key`** admin authorization
(`AdminPolicy`) — the same boundary as `/admin/credentials*` — so a request is still a one-header `curl`.
The path sits under `/routing/` for operator ergonomics (it controls routing behaviour) but is explicitly
authorized, unlike the sibling proxy endpoints.

## Alternatives Considered

- **`GET` toggle** — rejected: violates HTTP verb semantics (a `GET` must not mutate state); breaks caches/proxies that treat `GET` as safe.
- **Place under `/admin/...`** — viable and considered; the operator chose the `/routing/{dialect}/…` path. Auth requirement is identical either way.
- **Leave the endpoint unauthenticated for convenience** — rejected: it changes outbound auth behaviour; an unauthenticated switch is a privilege-escalation surface (see [NFR-002](../nfrs/NFR-002-toggle-endpoint-authorization.md)).

## Consequences

- A new, small authorized endpoint group lives next to the routing endpoints but opts into `AdminPolicy`; the proxy dialect endpoints stay key-less.
- `PUT`/`DELETE` are naturally idempotent, so repeated curls are safe.
- The `GET` read makes the otherwise-invisible in-memory state observable.

## Related

- **HLD 002 NFR-002** — admin authorization pattern reused here.
- **LADR-005** — the `403` returned by `PUT` when there is no active credential to arm.
