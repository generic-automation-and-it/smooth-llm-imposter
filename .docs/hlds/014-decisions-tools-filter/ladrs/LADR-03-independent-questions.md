# LADR-03: Independent questions in bounded chunks

**Status:** Accepted

## Context

- Choices within a question sum to one; one catalog-wide question would allow at most one tool to pass a 0.5 threshold.
- OpenJEV latency grows with question count and re-fed state size; large requests hit a 30-second gateway cutoff.

## Decision

**Ask** an independent use/skip choice question for each positively understood, history-unprotected tool, keyed by its original catalog index. Keep a tool when its use probability meets or exceeds the configured threshold.

**Bound** each request to 20 questions and 2,000 state characters; chunks run sequentially with identical state. State is `User request:` (newest user text) then `Earlier conversation (newest first):`; assistant text after the request and system instructions are excluded (system only as a fallback), and harness `<system-reminder>` blocks are removed from the classifier's copy before budgeting. Namespace wrappers are single scored units. Each question asks whether the assistant needs the named tool for the user's request, appends its description, and is capped at 300 characters; criteria are balanced (`use`: the request needs what the tool does or names it; `skip`: the request can be done fully without it).

## Consequences

- Several tools can independently pass the threshold.
- Large catalogs require several requests and incur linear latency.
- Original indices remain stable even when protected tools are not asked about.
- A small classifier is only as good as its state: harness boilerplate in the budget makes every tool look irrelevant, which is why reminders are stripped and an all-skip pass keeps the catalog (2026-10-23).
- Anything in the state that changes between agent steps (post-request assistant text, token-budget system messages) changes the kept set and breaks the upstream prompt cache; the state must stay fixed for a user turn.
- `noul` questions were measured uncalibrated on `kev-4b` (0.4–0.6 for every tool); keep `choice`.
