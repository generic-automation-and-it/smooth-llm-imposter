# LADR-03: Rewrite human spans of each unremembered human message, in series

**Status:** Draft

## Context

- Claude Code puts harness text (`<system-reminder>` blocks, hook output, local-command echo) inside the same user turn as the human request.
- In an agent loop the last user turn usually carries a tool result; the human request sits in an earlier turn and reappears in every continuation.
- HLD 014's decision prompt already recognizes these harness spans for its classifier copy.

## Decision

**Walk the message array in order, one message at a time, and treat every human-authored message the memory does not yet hold as a candidate** (LADR-05); a remembered message is replayed and skipped. A message whose trimmed text equals a configured imposter switch (HLD 010) is never a candidate and is forwarded verbatim, so switch text never reaches either model. Within a candidate only text outside harness spans is rewritten. A user turn carrying a tool result is not human-authored. Closed harness spans are excluded from the optimizer's input and kept in place: the tag set HLD 014 strips, with HLD 014's pairing rule applied to command blocks — a `<command-name>`/`<command-message>`/`<command-args>` block is excluded only when `<local-command-stdout>`/`<local-command-stderr>` directly follows it (a local command such as `/model`); a skill invocation's command and arguments are the request and are never excluded, even when Claude Code merges both into one user message. The proxy reassembles the turn with every excluded span, non-text block (images, documents, attachments) and separator at its original position, byte for byte.

Candidates are handled sequentially and synchronously — the next message waits for the previous one — so a local optimizer model never serves two rewrites at once (NFR-04). Across requests the same bound holds: every optimizer call takes the process-wide admission gate for its configured endpoint and model (one call in flight per pair), so two sessions or Claude Code subagents take turns instead of calling the local model in parallel (#164 FR-16). A call waits at most `LLM_MESSAGES_TIMEOUT` for admission; past that the message is forwarded and remembered as written. History that was forwarded before the feature was enabled, or before a restart, is rewritten on its first handling; that conversation misses the provider cache once (Open item 3), which is accepted to keep the rule simple.

## Consequences

- Harness context, including large CLAUDE.md reminders, never reaches the optimizer (NFR-04).
- A turn that is mostly harness text yields a short candidate, which the length check may skip.
- The first request after a restart or enablement pays one gate call and one rewrite per unremembered message, in series (Open item 7).
- Concurrent requests queue for the optimizer; under sustained contention a message waits up to `LLM_MESSAGES_TIMEOUT` and is then forwarded and remembered unoptimized, so it is never rewritten later (LADR-05's first value is final). Admission is a separate registry from the decision service's gates (HLD 015 LADR-05), so the gate and the optimizer never take turns with each other.
- Recognition must track harness tag changes in the same place HLD 014 does, or the two stages disagree.

## Open

- An unclosed harness opener is ordinary text for HLD 014; confirm it is safe to rewrite before Prototype.
