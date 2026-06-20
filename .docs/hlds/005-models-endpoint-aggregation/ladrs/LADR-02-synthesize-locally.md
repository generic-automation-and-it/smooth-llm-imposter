# LADR-02: Synthesize the list from the catalogue; replace passthrough, no live upstream call

**Status:** Draft

<!-- Status lifecycle: Draft → Prototype → Accepted. Also: "Superseded by LADR-MM", "Deprecated". -->

## Context

Today `GET /openai/v1/models` is body-less, so it follows the passthrough path to the dialect's
default provider and returns that one upstream's live model list. "Handle passthrough gracefully"
in issue #20 admits two readings: (a) **replace** the passthrough with a list synthesized from
configuration, or (b) **merge** the default upstream's live `/models` with the configured `to`
models. This router is stateless and key-less by default; an upstream model fetch needs network
reachability and possibly a credential, neither of which is guaranteed for a discovery probe.

## Decision

**Replace** the passthrough for this path with a response synthesized **entirely from the route
catalogue**. The router does not forward the request upstream and does not consult the credential
store to serve `/models`; it enumerates the OpenAI-dialect providers, collects their `to` values,
de-duplicates, and shapes the result.

This keeps discovery a pure function of configuration: it works with no upstream reachable, no key
configured, and no database — consistent with the project's stateless/key-less default. The result
is deterministic and cheap (no I/O), at the cost of not reflecting any model an upstream serves
that the router has no mapping for.

## Alternatives Considered

- **Merge live default-upstream `/models` with configured `to`** — richer list, but reintroduces a
  network dependency (and possibly auth) into a path that should always answer, makes the response
  non-deterministic and upstream-availability-coupled, and blurs the "answer what you own" thesis.
  Rejected.
- **Keep passthrough, add aggregation only when no default exists** — two code paths with subtly
  different responses for the same URL depending on config; confusing and harder to test. Rejected.

## Consequences

- Discovery always answers, with no upstream/DB dependency, deterministically (supports NFR-01,
  NFR-03).
- The list never includes models an upstream serves but the router has no `to` mapping for — by
  design; discovery describes the router's configured reach, not an upstream's catalogue.
- The default/passthrough provider's live `/models` is no longer surfaced via this path.

## Open

- Whether an operator will ever want the live default-upstream list back (e.g. a debug query
  parameter). Trigger: an operator request. Owner: @generic-automation-and-it/project.

## Related

- **LADR-01** — depends on; defines which value (`to`) is collected.
- **LADR-03** — bounds the scope of this replacement to one dialect/path/method.
