# LADR-06: In-memory translation dictionary for caller-supplied session ids

**Status:** Accepted

## Context

A caller that wants stable session identity across requests today must send a
session id (header or body field per the existing HLD 009 resolution order) and
trust that the upstream remembers it. Some upstreams (opencode-go diag groups,
for example) want to group by a session id the proxy itself owns — so the proxy
can map a short-lived caller id to a stable synthetic id without forcing the
caller to coordinate a long-lived secret.

The HLD 010 `--newsession` switch mints a synthetic session id and persists it
in an in-memory dictionary keyed on the caller-supplied id. On every subsequent
non-match forward request whose resolver produces a session id equal to a
dictionary key, the proxy translates the session id to the stored synthetic id
before stamping it on the outbound request. The dictionary is the override
source for the existing HLD 009 session-identity stamping path.

The user explicitly chose **process-lifetime** semantics (no TTL, no eviction,
no clear). The expected volume is small (operator/agent tooling, not
high-cardinality user traffic). This is a deliberate trade: the dictionary
grows for the life of the process, in exchange for zero management overhead.

## Decision

**Adopt** a process-lifetime in-memory dictionary
(`ConcurrentDictionary<string, string>`) keyed on the caller-supplied session id
(value = the synthetic id minted by `--newsession`). The dictionary is:

- **Process-lifetime** — no TTL, no eviction, no clear. The dictionary grows
  for the life of the process; volumes are expected to be small enough that this
  is a non-issue.
- **Single instance** — registered as a DI **singleton** (`ISessionTranslationDictionary`)
  so every resolver, transformer, and forwarder sees the same map for the
  process lifetime. (DI lifetime: `Singleton`, not `Scoped` — the dictionary is
  process-lifetime, so per-scope instances would split the map and silently break
  translation.)
- **Read-mostly** — written by the `--newsession` short-circuit, read on every
  non-match forward request that resolves a session id. ConcurrentDictionary
  handles concurrency without locks.
- **Keyed on the caller-supplied id** (the HLD 009 resolution order, falling back
  to `null` if none is present). A `--newsession` request with no caller-supplied
  id does **not** match (the responder returns no match; the request forwards
  normally).
- **Looked up by the resolver** — when the resolver produces a session id equal
  to a dictionary key, the resolved `SessionIdentity.Value` (the live record
  property; the planned rename to `StableId` is part of the follow-up commit
  — both names refer to the same string) is rewritten to the stored synthetic
  id before the transformer stamps it on the outbound request. The
  translation fires only when `Imposter:WhoMessage:Enabled=true`; with the
  toggle off, the dictionary is bypassed and the captured/derived value passes
  through unchanged.

The dictionary lives on the same seam as the customized-switches short-circuit
(between `router.PlanAsync` and `forwarder.SendAsync`) but on a different code
path: the short-circuit runs before the forwarder; the translation runs after
`PlanAsync` (which produces the resolved session id) and before the forwarder
(which stamps the id on the outbound request).

## Alternatives Considered

- **Per-request translation table** — rejected: the user explicitly wants
  process-lifetime semantics. A per-request table would require the caller to
  re-send the synthetic id (defeating the purpose) or the proxy to re-mint on
  every request (defeating the user-facing translation property).
- **TTL with eviction** — rejected: the user explicitly opted out. Adding a
  TTL would require an eviction policy (LRU, time-based, count-based), a
  background sweep task, and tests for the eviction race. Process-lifetime is
  the simpler contract for the expected volume.
- **Persisted to disk / DB** — rejected: the rest of SmoothLlmImposter is
  stateless and key-less (no DB in the default install — see
  `.docs/hlds/008-runtime-config-crud/`). Persistence would require a new
  storage surface, contradicting the project's defaults.
- **Header-based translation** (e.g. translate `x-opencode-session: caller-id`
  to `x-opencode-session: synthetic-id` only) — rejected: the resolved session
  id is consumed by both the body-stamping path (OpenAI `session_id`,
  `prompt_cache_key`) and the header-stamping path (Anthropic
  `x-opencode-session`); translating only the header would leave the body
  untouched and defeat the user-facing property. Translating at the
  `SessionIdentity` level — before the transformer stamps — covers both paths.
- **Configuration-driven translation table** — rejected: the user wants
  in-memory semantics specifically because the table is dynamic (minted at
  request time). A config table would be static and require restart to add a
  row, which defeats the `--newsession` use case.

## Consequences

- Positive: zero storage surface; the dictionary is just a process-memory
  dictionary. No DB, no config, no eviction, no file.
- Positive: the translation step is a single dictionary lookup on the hot
  path — no I/O, no serialization, no async. A `ConcurrentDictionary.TryGetValue`
  is `O(1)` and lock-free.
- Positive: the dictionary can be replaced with a persistent store in a future
  HLD by swapping the singleton; the call sites do not change.
- Negative: the dictionary grows for the life of the process. At the expected
  volume (a few hundred entries per process for operator/agent tooling), this is
  negligible. At higher volumes (e.g. a high-cardinality production caller
  with millions of unique session ids), the dictionary would be a memory leak —
  this is an explicit non-goal of the feature, documented in NFR-04.
- Negative: a process restart loses all minted session ids. Callers using
  `--newsession` must re-mint on restart; the proxy does not persist the
  table. This is an explicit non-goal of the feature, documented in NFR-04.
- Negative: the resolver must learn a new optional dependency (the dictionary);
  without it, the resolver still works because the singleton dictionary
  instance is empty until the first `--newsession` mint — there is no separate
  "empty" code path. Adding the constructor parameter keeps the resolver
  testable in isolation (tests can pass an empty dictionary and exercise the
  no-match branch).
- Neutral: the dictionary is in-process and not clustered. A horizontally
  scaled SmoothLlmImposter would have N independent dictionaries; the caller
  must route to the same instance to use the same synthetic id. This is
  acceptable for the operator/agent-tooling use case and is an explicit
  non-goal of the feature.

## Related

- **LADR-01** — the seam is where the translation step is invoked.
- **LADR-02** — the dictionary is populated by `--newsession` (a customized
  switch); both switches share the same trigger predicate.
- **LADR-04** — the translation fires only when `Imposter:WhoMessage:Enabled=true`.
- **LADR-05** — the translation does not affect streaming behavior; the
  dictionary is consulted on the same hot path regardless of `stream`.
- **LADR-03** — the synthetic id appears in the `--who?` envelope's content
  text (`session:<id>`), so the same id flows from `--newsession` (mint) →
  `--who?` (display) → forward path (stamp).
- **NFR-04** — the process-lifetime growth contract; the dictionary does not
  evict.
- **HLD 009** — the resolver, header-stamping, and body-stamping surfaces the
  translation step layers on top of.
