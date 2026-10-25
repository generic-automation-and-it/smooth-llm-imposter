# LADR-04: Proxy validates every rewrite; the original wins on doubt

**Status:** Draft

## Context

- LLM rewriters invent plausible facts — paths, numbers, error codes — and drop negations that "read as redundant".
- A dropped `not` or a changed path costs more than every token the rewrite saved.
- Caveman's memory-file compressor validates output, retries a targeted fix at most twice, and keeps the original on failure.

## Decision

**Accept a rewrite only if a deterministic check passes against its original.** Every identifier, path, URL and link, number, version, code span, environment variable, negation, slash or dollar command (`/skill`, `$skill`), `@` mention and name from the request's own tool catalog (including MCP tools) in the original appears unchanged in the rewrite; the rewrite introduces no identifier, path or number absent from the original; markdown structure (headings, list nesting, fences) is preserved; and the rewrite is shorter than the original. On failure the proxy makes at most one targeted retry naming the failed elements, then forwards the original.

The optimizer's instructions state that its only job is the message's prose and that tools, skills, hooks, plugins, rules and external links are kept exactly; names of hooks, plugins and rules written in plain prose cannot be detected reliably, so for those the instructions are the only guard. They also list the shortcuts it must not take — "this negation is redundant", "this code block can be summarized", "this path can be shortened" — but the check, not the instructions, is the guarantee. Output-side brevity rules (word caps, no tables) are never applied to user input.

## Consequences

- Fidelity does not depend on the optimizer model's obedience (NFR-02).
- Some useful rewrites are discarded, e.g. one that rephrases a negation correctly.
- A retry adds a second generation call on the failing path only.
- A discarded rewrite is remembered as "original" (LADR-05), so the turn is not retried on later requests.
