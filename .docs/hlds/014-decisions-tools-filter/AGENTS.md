# AGENTS.md — Decision-Model Tool Pre-Filter

## TL;DR

Read [README.md](README.md) first — decision-tool filtering intent, decisions and quality requirements live there.

## Changelog

| Date | Change | Ref |
|---|---|---|
| 2026-10-23 | Conductor/local-command wrappers stripped from state; fail-open log names status and model | — |
| 2026-10-23 | Memo keyed per tool question; only valid answers cached; text between reminder spans kept (review) | #141 |
| 2026-10-23 | Request-first state (no post-request assistant text); need-question wording; all-skip keeps catalog; deferred-loading skip; chunk memo; byte-preserving output; threshold 0.20 | #138 |
| 2026-10-23 | Classifier success logs use canonical request shape instead of caller-controlled paths | #140 |
| 2026-10-23 | Conductor paste-in installer forwards decision settings; existing UI scripts require replacement/re-run | #138 |
| 2026-10-22 | HLD 014 accepted; HTTP(S)/hosted-choice caveats; HLD 015 proposed, not superseding; AMD64/cache gates pending | #138 |
