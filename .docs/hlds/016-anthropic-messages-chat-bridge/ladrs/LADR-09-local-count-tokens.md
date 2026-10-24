# LADR-09: `count_tokens` is answered locally with a shared estimator

**Status:** Draft

## Context

- Chat Completions has no token-counting endpoint; the client calls Messages `count_tokens` for context management.
- A 404 or a wrong count degrades compaction decisions.
- The same estimate is needed when an upstream omits usage.

## Decision

**Adopt** answering `count_tokens` on bridged providers locally, with one deterministic estimator defined in the [harness spec](../spec/04-harness-and-errors.md), without calling the upstream. The same estimator supplies the `message_start` input figure and the missing-usage fallback.

## Consequences

- No extra upstream call or cost for counting.
- Counts are approximate and tokenizer-independent; the client's safety margins must absorb the error.
- Replacing the estimator with a real tokenizer for a given upstream is a later, separate decision (`Open`: trigger is measured miscompaction on a named upstream).
