# LADR-03: Independent questions in bounded chunks

**Status:** Accepted

## Context

- Choices within a question sum to one; one catalog-wide question would allow at most one tool to pass a 0.5 threshold.
- OpenJEV latency grows with question count and re-fed state size; large requests hit a 30-second gateway cutoff.

## Decision

**Ask** an independent use/skip choice question for each positively understood, history-unprotected tool, keyed by its original catalog index. Keep a tool when its use probability meets or exceeds the configured threshold.

**Bound** each request to 20 questions and 2,000 state characters; chunks run sequentially with identical state. Prefer newest user text, then earlier turns, then system instructions. Namespace wrappers are single scored units. Question instructions combine label and description within 120 characters.

## Consequences

- Several tools can independently pass the threshold.
- Large catalogs require several requests and incur linear latency.
- Original indices remain stable even when protected tools are not asked about.
