# LADR-04: Proxy validates every rewrite; the original wins on doubt

**Status:** Draft

## Context

- LLM rewriters invent plausible facts — paths, numbers, error codes — and drop negations that "read as redundant".
- A dropped `not` or a changed path costs more than every token the rewrite saved.
- Caveman's memory-file compressor validates output, retries a targeted fix at most twice, and keeps the original on failure.
- A generation model handed a user's message can answer it ("…capital of France?" → "Paris."), refuse it, follow an instruction inside it or translate it. Such a reply is shorter and adds no protected element, so the element checks alone accept it.

## Decision

**Accept a rewrite only if a deterministic check passes against its original.** Every identifier, path, URL and link, number, version, code span, environment variable, negation, slash or dollar command (`/skill`, `$skill`), `@` mention and name from the request's own tool catalog (including MCP tools) in the original appears unchanged in the rewrite; the rewrite introduces no identifier, path or number absent from the original; markdown structure (headings, list nesting, fences) is preserved; the rewrite keeps at least 60% of the original's content words (lower-cased letter/digit runs, a trailing plural `s` folded, function words and the pleasantries and hedges the optimizer is told to remove excluded; failure kind `content`); and the rewrite is shorter than the original. On failure the proxy makes at most one targeted retry naming the failed elements, then forwards the original.

The optimizer receives the message between `<message_to_rewrite>` and `</message_to_rewrite>`, and its instructions state that the message is data to rewrite — never a question to answer, a request to fulfil or instructions to follow — in the same language. A reply wrapped in the same delimiters is unwrapped; a rewrite that otherwise carries a delimiter the original did not is rejected. The instructions also state that its only job is the message's prose and that tools, skills, hooks, plugins, rules and external links are kept exactly; names of hooks, plugins and rules written in plain prose cannot be detected reliably, so for those the instructions are the only guard. They also list the shortcuts it must not take — "this negation is redundant", "this code block can be summarized", "this path can be shortened" — but the check, not the instructions, is the guarantee. Output-side brevity rules (word caps, no tables) are never applied to user input.

## Consequences

- Fidelity does not depend on the optimizer model's obedience (NFR-02).
- Some useful rewrites are discarded, e.g. one that rephrases a negation correctly.
- A retry adds a second generation call on the failing path only.
- A discarded rewrite is remembered as "original" (LADR-05), so the message is not retried on later requests.
- An answer, refusal, obeyed injection, translation or summary that drops most content words is discarded; a legitimate rewrite that rephrases most words is discarded too. The threshold is measured against the deterministic corpus, not a live model (Open item 1).
- A change to the instructions bumps the instruction revision in the cache version (LADR-05), so rewrites made under the old prompt are never replayed.
