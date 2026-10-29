# LADR-13: Sticky tool selection requires session-stable tool ordering

**Status:** Accepted (2026-10-28)

## Context

- [LADR-09](LADR-09-sticky-per-session.md) records sticky tool **membership** per captured session and states
  that "within a session the forwarded catalog only grows, so the tool prefix stays stable until the harness
  adds or drops a tool".
- That claim holds for the tool **set**, not the tool **order**. The filter prunes in place and keeps the
  caller's order, so a harness that re-sends the same tools in a different array order still produces a
  different forwarded `tools` array — and every provider prefix cache that orders tools first (Anthropic:
  tools → system → messages; OpenAI-compatible prefix caches) loses the conversation prefix, which is the
  exact cost LADR-09 exists to remove.
- Order is remembered only by HLD 001 LADR-008 first-seen tool ordering (`Imposter:ToolOrdering:Enabled`),
  which is off by default and independent of the decision filter.
- HLD 011 is the precedent for the failure mode this avoids: an option that is configurable end-to-end and
  completely inert ships silently. A sticky-only configuration is that shape — it looks correct, starts
  cleanly, and promises a stability it cannot deliver.

## Decision

**Fail startup when sticky selection is requested without tool ordering.**

- **Rule:** when `Imposter:DecisionsTools:Enabled` is `true` **and** `Imposter:DecisionsTools:StickyPerSession`
  is `true` **and** `Imposter:ToolOrdering:Enabled` is `false`, configuration validation fails and the Host
  does not start. The message names both settings in their structured and environment spellings —
  `Imposter:DecisionsTools:StickyPerSession` (`DECISIONS_TOOLS_STICKY_PER_SESSION`) and
  `Imposter:ToolOrdering:Enabled` (`Imposter__ToolOrdering__Enabled`; ordering has no conventional env
  suffix).
- **A disabled filter stays unvalidated.** `DecisionsTools.Enabled = false` makes the sticky flag inert, so
  any ordering value is valid there — the rule keys on operator opt-in flags, never on classifier answers.
- **Ordering stays independently usable.** `ToolOrdering.Enabled = true` on its own keeps its existing
  behaviour and is not coupled to the decision filter in either direction.
- **Rejected — implicit coupling (sticky switches ordering on).** Convenient, but it attaches ordering's
  availability to the decision filter: the filter would own a routing optimization's policy, so turning the
  filter off would disable ordering and turning ordering off would be impossible while sticky is set. That
  contradicts the Application boundary that ordering is a separate routing-local optimization whose
  availability and rank policy must not hang off decision-model concerns.
- **Rejected — leave the configuration valid.** Every operator running that combination would keep the
  documented stability guarantee while getting none of it, and the only symptom would be unmeasured cache
  misses.
- **Enforcement lives in the existing routing options validator**, not in a second startup validator: a
  second one double-reports failures inside an `AggregateException`.

## Consequences

- **Breaking startup change.** An existing deployment or Conductor workspace with `DECISIONS_TOOLS_ENABLED=true`
  and `DECISIONS_TOOLS_STICKY_PER_SESSION=true` and ordering off refuses to start until it sets
  `Imposter__ToolOrdering__Enabled=true` or clears the sticky flag. The release carries the breaking marker.
- **Conductor workspaces need the paste-in lifecycle script replaced and re-run**, because the shipped script
  forwards the sticky flag but not the ordering flag; without the update the new rule stops those workspaces
  booting.
- **Valid configurations see no runtime change.** With both flags set, selection, recording, thresholds,
  fail-open and bounds are exactly LADR-09's; ordering adds only its existing rank memory. With the filter
  disabled, nothing changes at all.
- **Upstream cache hits remain unmeasured and unguaranteed.** The rule removes one known source of prefix
  churn; it does not prove a hit rate, and definition changes, additions and removals still invalidate later
  cache prefixes.
- A client that sends neither `defer_loading` nor a reordered catalog is unaffected either way.
