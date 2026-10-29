# LADR-05: Per-section profiles, circuits and admission gates

**Status:** Accepted

## Context

- HLD 017 adds a second decision-service consumer (message gate) alongside the tool filter. The two sections can use different models and endpoints, so one consumer's outage says nothing about the other's (HLD 014 LADR-11).
- LADR-03 defines a single process-wide gate. With two consumers on different endpoints, a single shared budget serializes unrelated traffic and reports contention as a shared wait.
- LADR-02 resolves one transport profile from the operator surface. Two sections need two resolved profiles with shared-default fallback, not two independent naming surfaces.
- HLD 014 LADR-11 places the tool-filter breaker outside the retries and outside the gate. A second consumer needs the same placement with its own breaker instance.

## Decision

1. **Resolve one transport profile per decision section.** Each section resolves `BaseUrl`, `Path`, `Model`, `MaxAttempts`, `TimeoutSeconds`, `MinProbability` (where applicable) and `CircuitBreakSeconds` from its own `DECISIONS_<SECTION>_*` value, falling back to shared `DECISIONS_*`, falling back to the built-in default. Existing `DECISIONS_TOOLS_*` values keep their effective values; a missing section value never inherits another section's value.
2. **Own one circuit per decision section.** Each section holds its own breaker instance following HLD 014 LADR-11 trip rule: one failed logical call after retries opens that section only; caller cancellation, gate-wait timeouts and invalid answers never open it. An open section makes no call and short-circuits before gate acquisition.
3. **One process-wide local-LLM slot (amended 2026-10-29, #247).** Every decision section and the HLD 017 optimizer take one shared slot (`LocalLlmSlot`), whatever endpoint or model each resolves to, so only one local-LLM call is in flight per process. A call keeps the slot through its retries and retry delays. Each caller keeps its own wait budget and timeout type: a decision call waits up to its section's `TimeoutSeconds` (`DecisionGateTimeoutException`), an optimizer call up to `LLM_MESSAGES_TIMEOUT` (`OptimizerAdmissionTimeoutException`); neither opens a circuit. *Supersedes the 2026-10-28 rule — one gate per distinct endpoint plus model, sections on different pairs running in parallel — because these models usually run on the operator's own machine, where parallel calls on different models compete for the same hardware and one call at a time is the fastest order.*
4. **HLD 014 owns the tool section; HLD 017 owns the message section.** This LADR owns only profile resolution, circuit placement and gate keying. Thresholds, questions and fail-open actions stay with the consuming HLDs.

## Consequences

- A classifier outage stops only its own section for a cooldown; the other section proceeds.
- Every decision and optimizer call serializes, including sections on different endpoints; a remote classifier no longer runs in parallel with a local optimizer. The head-of-line wait is bounded by each caller's own wait budget.
- Retry defaults are one retry (`MaxAttempts` 2) after a `RetryDelaySeconds` delay (default 5, 0–60) for both the decision transport and the optimizer, because a held slot blocks every other local-LLM call for the whole retry budget.
- The outbound upstream gate (`IMPOSTER_MAX_CONCURRENT_REQUESTS`) no longer wraps the decision or optimizer clients, so an open upstream stream cannot block them.
- A shared `DECISIONS_*` variable configures both sections unless overridden per section; no `DECISIONS_ENABLED` exists, so one variable cannot enable two features.
- Validation follows effective enablement: transport settings for a section are validated only when that section is effectively on.

## Implementation

- 2026-10-28 (#228): items 1 and 3. `IDecisionModelClient.DecideAsync` takes the section; `DecisionServiceProfiles` resolves a registered section's named `DecisionServiceOptions` (defaults while the section is off; an unregistered section throws, so gate keys stay bounded by the registered sections). `DecisionServiceGate` keys its semaphores by `(BaseUrl without trailing '/' + Path, Model)`. One named HTTP client serves every section, so the section's attempts and timeout travel on the request and the resilience pipeline reads them back. The message gate's circuit log fields report its own profile. A deployment that set only `DECISIONS_TOOLS_*` transport moves the gate to the built-in default; startup logs one Warning for that case.
- 2026-10-29 (#247): item 3 amended. `DecisionServiceGate` and the HLD 017 `OptimizerAdmissionGate` both take the Application singleton `LocalLlmSlot` instead of keyed semaphores; `ConcurrencyLimitingHandler` was removed from the `decision-service` and `llm-messages` clients; `RetryDelaySeconds` layers like the other transport fields (`DECISIONS_TOOLS_`/`DECISIONS_MESSAGES_` → `DECISIONS_` → default) and reaches the resilience pipeline on the request. macOS ARM64 NativeAOT smoke: a gate on `127.0.0.1`/`gate-model` and an optimizer on `localhost`/`opt-model` stayed at one call in flight across three concurrent requests.
- Item 2 (circuit per section) shipped with HLD 017 (`DecisionCircuitBreaker`, `MessageGateCircuitBreaker`).

## Related

- **HLD 014 LADR-11** — breaker trip rule reused per section.
- **HLD 017 LADR-01/LADR-07** — gate and optimizer consumers of this amendment.
- **HLD 017 LADR-05** — session-scoped memory needs per-instance lifetime and uncapped mode; see HLD 014 LADR-09 amendment.
