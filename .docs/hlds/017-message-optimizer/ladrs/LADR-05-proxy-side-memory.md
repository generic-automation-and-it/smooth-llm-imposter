# LADR-05: Proxy-side memory of what was forwarded

**Status:** Draft

## Context

- Claude Code and Codex keep their own history and resend the original messages every turn; they never see the proxy's rewritten request.
- An in-text "optimized" marker would therefore never return to the proxy, would cost ~15 tokens per optimized message on every later turn, could be echoed by the end LLM and could be forged.
- Once a turn has gone upstream in one form, forwarding it in another form later breaks the cache prefix.

## Decision

**Remember, per session, the exact text forwarded for each human message the stage handled, and replay it.** The key is a hash of the original span text plus the optimizer version (optimizer model and instruction revision). The recorded outcome is what was actually forwarded: the accepted rewrite, or the original — after a negative gate answer, a discarded rewrite, or a failed or open optimizer. A remembered message makes no gate or optimizer call. The first recorded outcome for a key is final: concurrent requests carrying the same new candidate reuse it instead of calling the optimizer again. A message below the length minimum is not recorded, because the check is deterministic.

The memory is in-process and bounded; it evicts whole sessions, least recently used first, so an active session never loses part of its history and an evicted session behaves like a restart (Open item 3). Any human message the memory does not hold is a candidate again (LADR-03). No marker is written into forwarded text, and audit comes from logs (hash, outcome, sizes — NFR-04).

## Consequences

- No message is compressed twice, and repeat requests in a user turn cost no model calls.
- The provider cache prefix stays stable while the session is retained (NFR-01).
- A restart loses the memory: the conversation's history is rewritten again on the next request, possibly with different bytes, so it misses the cache once (Open item 3).
- Storing the forwarded text keeps user content in process memory for the entry's lifetime.

## Open

- Scope for requests without a captured session id — Open item 8, before Prototype.
