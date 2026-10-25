# LADR-008 — Opt-in first-seen tool ordering and caller-owned cache layouts

**Date:** 2026-10-23
**Status:** Accepted

## Context

Harness reorderings can unnecessarily change an upstream prompt prefix. Sorting by name is stateless,
but a newly added name can insert before existing tools. Retaining removed definitions would advertise
tools the harness cannot execute. Neither sorting nor retention can make changed tool definitions match
an old provider prefix. Anthropic orders cache content as tools → system → messages.

## Decision

Keep ordering **default-off**, enabled with `Imposter:ToolOrdering:Enabled`. For supported completion
paths and explicit captured session identities, remember first-seen ranks in process memory, scoped by
dialect, inbound model, session and caller authentication. Preserve the first catalog's order, append
new identities, and reorder only the current harness-supplied definitions. Never store tool bodies or
restore removed definitions. Apply before routing transforms so injected Anthropic breakpoints mark the
final catalog tail. Classification/normalization still owns its existing independent pruning policy.

Bound memory to 256 session scopes and 1,024 tool identities per scope; expire inactive scopes after
30 minutes and evict least-recently-used scopes at capacity. Serialize rank updates. Store SHA-256 keys,
not raw session/authentication values. Skip absent/derived identities, ambiguous/unknown/duplicate tools,
oversized catalogs/identifiers, deferred loading, Responses-managed conversation state and caller-managed
cache layouts. No persistence, distributed cache or affinity requirement is introduced.
Responses text-block `prompt_cache_breakpoint` controls count as caller-managed caching even without
`prompt_cache_options`. At the tool-rank limit, eligible requests retain their supplied order but still
refresh the scope's activity timestamp; the rank limit does not make active scopes expire as idle.

For caching-enabled Anthropic requests with **no** caller cache controls, mark the last tool, system
block and message content block. If explicit or top-level automatic controls already exist, preserve
the caller's layout entirely; do not add breakpoints or replace TTLs. This avoids exceeding four explicit
breakpoints or placing a five-minute write before an existing one-hour write.

## Consequences

- Default routing stays stateless and preserves supplied order. The opt-in memory is an NFR-001
  exception, not an upstream cache or a tool availability registry.
- New tools append in rank order; removed tools disappear. Reintroduced tools recover their old rank.
  Definition changes, additions and removals can still invalidate later cache prefixes.
- Expiry, eviction, restart and different replicas can reset rank order. Correctness never depends on
  this memory; consistent instance routing can help the optimization but is not required.
- Caller-managed/deferred catalogs retain their supplied order and caching semantics.
- L0/L2 and NativeAOT capture-upstream checks prove request behavior, not real provider cache hits.

## Verification outcome

The Release L0/L1/L2 suite passed; final targeted checks cover order, appends/removals, caller caching,
LRU/idle expiry, rank limits, scope isolation and concurrency. macOS ARM64 and native Linux musl ARM64
publishes were exercised against a capture upstream with ordering enabled, including Anthropic tail
breakpoints, caller one-hour TTL preservation and Responses configuration binding. The Linux container
ran as non-root UID 1654. No trim/AOT warning was suppressed to make this feature pass; macOS linking
reported external clang debug-module-cache warnings only. Local checks disabled the repository's known
NuGet audit baseline issue; the production Dockerfile was not changed.

Linux AMD64 native-runner execution remains outstanding. Real upstream cache-hit usage was not measured;
the capture upstream proves request semantics only.
