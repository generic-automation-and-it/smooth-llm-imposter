# NFR-01: Mutation Visibility (Consistency)

**Status:** Accepted

## Requirement

A successful admin CRUD write (create / update / delete / enable / disable) is observed by the **next**
inbound proxy request — there is no additional caching window. Concretely: given a mutation that returns
success at time T, any request whose processing begins after T routes according to the mutated configuration.

## Verification

Integration test (L2): boot the host with a stub upstream, issue a proxied request and assert the
pre-mutation route, perform the mutation via the admin API, then issue the same proxied request and assert
the **new** route — within the same running process, no restart.

## Acceptance Criteria

- An update to a model mapping changes the rewritten target on the immediately following request.
- Disabling a provider (`Enabled=false`) causes the next request that previously matched it to fall through
  (to another match, the default, or a documented 404) on the immediately following request.
- A newly inserted provider is resolvable on the immediately following request; a deleted one is not.

## Applies To

Goals 1–3; the routing-resolution path (catalog/resolver) consuming the current `IProviderRegistry`
([LADR-01](../ladrs/LADR-01-runtime-mutable-registry.md), [LADR-07](../ladrs/LADR-07-snapshot-consumption-lifetime.md)).
