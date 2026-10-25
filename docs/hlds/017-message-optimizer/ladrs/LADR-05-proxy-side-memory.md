# LADR-05: Session-scoped checksum cache of forwarded messages

**Status:** Draft

## Context

- No Messages or Chat Completions response carries the request's messages, so Claude Code and Codex resend their own original history every turn and never see the proxy's rewrite.
- An in-text "optimized" marker would never return, would cost ~15 tokens per message every turn, and could be echoed or forged.
- A message forwarded in one form and later in another breaks the cache prefix.
- The tool filter already keeps sticky per-session state in memory, scoped by session identity and caller credentials (HLD 014 LADR-09).

## Decision

**Cache, per session scope, the text forwarded for each original human message, keyed by its checksum, and replace every matching message with it.** The checksum is a SHA-256 of the original human text (harness spans excluded, LADR-03) plus the optimizer version (model and instruction revision). The value is what was forwarded: the accepted rewrite, or the original after a negative gate answer, a discarded rewrite or a failed or open optimizer. A hit makes no model call; the first value for a checksum is final, and concurrent requests reuse it. Messages below the length minimum are not cached.

The scope is the tool filter's — captured session identity (HLD 009) plus caller credential headers — without the inbound model, so a side request or `/model` switch does not rewrite history again. Nothing is shared across sessions or callers. A request without a captured session id is not cached: its human messages are optimized on every request.

The cache lives in process memory only — no file or database — with a sliding TTL: a scope unused for `LLM_MESSAGES_CACHE_TTL_HOURS` (optional, default and maximum 120) is dropped, and every hit refreshes it. There is no scope or entry cap. Audit comes from logs (checksum, outcome, sizes — NFR-04), never a marker.

## Consequences

- No message is compressed twice; repeats cost no model calls, and the cache prefix stays stable while the scope lives (NFR-01).
- A restart or a scope idle past the TTL loses the cache: history is rewritten on the next request and misses the provider cache once (Open item 3).
- A sessionless conversation pays a gate call and a rewrite per human message on every request and gets no provider cache benefit.
- User text stays in memory for up to five days, uncapped.
- The shared session-scoped memory needs a per-instance lifetime and an uncapped mode; tools keep 30 minutes and 256 scopes.
