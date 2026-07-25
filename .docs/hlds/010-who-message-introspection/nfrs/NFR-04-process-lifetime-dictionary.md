# NFR-04: Process-lifetime dictionary (no eviction)

**Status:** Accepted

## Requirement

The session-id translation dictionary (HLD 010 LADR-06) is **process-lifetime**:
once a `(callerId, syntheticId)` pair is minted by a `--newsession` request, it
remains in the dictionary for the life of the process. There is **no TTL**, **no
eviction**, and **no clear** API. A second `--newsession` request with the same
caller id does not overwrite the entry (the same caller always gets the same
synthetic id within a process). A process restart loses all entries — the caller
must re-mint on restart.

The dictionary's *intended* volume is small (operator/agent tooling, a few hundred
entries per process). High-cardinality callers (millions of unique session ids
per process) are an explicit non-goal of this feature: at that scale, the
dictionary would be a memory leak. Operators who need to support that scale
should disable the feature (`Imposter:WhoMessage:Enabled=false`) until a future
HLD adds a persistent or evictable store.

## Verification

The following L0 and L2 tests cover this NFR (all are runnable in the
current build):

- L0 test (`WhoMessageResponderTests`):
  - `--newsession` request with caller id `A` mints synthetic id `S1`.
  - A second `--newsession` request with the same caller id `A` returns the
    **same** synthetic id `S1` (no overwrite).
  - A second `--newsession` request with a different caller id `B` returns a
    **different** synthetic id `S2`.
- A lookup for caller `A` after the first mint returns `S1`; the dictionary has
  exactly two entries after the two mints.
- A lookup for caller `C` (an unknown caller id) returns `false` — the
  dictionary contains no entry for unrelated callers.
- L2 test (`WhoMessageIntegrationTests`): the in-memory dictionary
  configured via a fresh `Fixture` survives across two requests in the same
  test (the second request sees the entry minted by the first).
- A test asserts the dictionary **does not** expose a `Clear()` / `Remove()` /
  `Evict()` API on its public surface — the process-lifetime contract is
  enforced at the type level, not just by convention.

## Acceptance Criteria

- A test asserts that after two `--newsession` requests with two different
  caller ids, the dictionary has exactly two entries.
- A test asserts that after a second `--newsession` request with the same
  caller id as the first, the dictionary has exactly one entry (no
  overwrite).
- A test asserts that an unrelated caller id does **not** appear in the
  dictionary (the dictionary is keyed on the caller-supplied id, not
  populated as a side effect of any other request shape).
- A test asserts that `Imposter:WhoMessage:Enabled=false` causes the
  translation step on the forward path to be skipped — the dictionary may
  have entries from a previous request, but the resolver does not consult
  them and the outbound request carries the captured/derived value
  unchanged.
- A test asserts that `--newsession` without a caller-supplied id does not
  match — the dictionary is not written, the request forwards.

## Applies To

- Goal 6 (Session-id mint + translation).
- The translation step in [LADR-06](../ladrs/LADR-06-session-translation-dictionary.md).
- The non-match forward path that consults the dictionary.
