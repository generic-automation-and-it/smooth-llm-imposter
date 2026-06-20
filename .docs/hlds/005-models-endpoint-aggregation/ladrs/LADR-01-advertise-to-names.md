# LADR-01: Advertise `to` (upstream target) names, not inbound `From`

**Status:** Draft

<!-- Status lifecycle: Draft → Prototype → Accepted. Also: "Superseded by LADR-MM", "Deprecated". -->

## Context

Every imposter mapping has two model names: `From` (the inbound name a caller sends, which the
resolver matches on) and `To` (the upstream target the request is rewritten to). The discovery
response can advertise either. There is a real tension: a client that reads `/models`, picks an
advertised id, and sends it back inbound will only trigger an imposter route if that id matches a
mapping's `From`. Advertising `To` therefore lists names that, if selected verbatim by the client,
would **not** match any route (they fall to default/404). Issue #20's acceptance criteria
explicitly call for the `to` values.

## Decision

**Advertise** the distinct set of `To` (upstream target) values. The `/models` response describes
the real models the imposter ultimately forwards to, making the discovery list an accurate
reflection of the router's downstream reach — which is the stated intent of issue #20.

We accept the routing asymmetry as a known property of this design, not a defect to paper over:
discovery answers "what can this router reach", while a working imposter request is still keyed on
the inbound `From` name. The two surfaces describe different things on purpose. Reconciling them
(e.g. advertising `From`, or both) is parked as a future option (see Open) rather than silently
overriding the issue.

## Alternatives Considered

- **Advertise `From` (inbound trigger) names** — directly usable for client model-selection
  (pick-and-send works), but contradicts the issue AC and hides the real upstream targets. Rejected
  for this HLD; recorded as the leading future alternative.
- **Advertise the union of `From` and `To`** — superset that satisfies both audiences, but doubles
  the list, mixes two namespaces under one `id` field with no way to tell them apart, and still
  isn't what the issue asked for. Rejected.

## Consequences

- The discovery list truthfully names the downstream models the router can reach.
- A client cannot blindly pick an advertised id and have it route as an imposter — the `From`/`To`
  asymmetry is a documented sharp edge, surfaced in the HLD AGENTS.md.
- If client-side auto-selection becomes a requirement, this decision is the first thing to revisit.

## Open

<!-- Optional — unresolved questions tied to this decision. Each must have an owner/trigger. -->

- Whether to later advertise `From` (or both) for client auto-selection. Trigger: a concrete client
  workflow that selects a model from `/models` and expects it to route as an imposter. Owner:
  @generic-automation-and-it/project.

## Related

- **LADR-02** — depends on; the `to` set is computed during local synthesis.
- **LADR-04** — refines; how each advertised `to` becomes a `Model` object.
