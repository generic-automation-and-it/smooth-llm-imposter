# AGENTS.md - Responses Chat Tool History Normalization

AI Context: HLD for Responses Chat Tool History Normalization. Updated: 2026-06-20

> AI-coder context for this HLD. Architecture diagrams live in [`./diagrams/`](./diagrams/),
> decisions in [`./ladrs/`](./ladrs/), quality spec in [`./nfrs/`](./nfrs/). This file is
> guardrails, not narrative — the narrative is in [`./README.md`](./README.md).

## TL;DR

Specifies request-side prior-turn tool-history normalization for the OpenAI `/responses`→Chat
downgrade; intent is in README, decisions in `ladrs/`, quality spec in `nfrs/`.

## Non-Negotiables

- **Only the downgrade path is in scope.** Apply this design only when a matched OpenAI imposter route
  downgrades inbound `/responses` to Chat Completions; passthrough/default routes, Anthropic routes,
  OpenAI `responses` upstreams, and direct Chat callers remain transparent.
- **Preserve pairs, drop gaps.** A Chat assistant `tool_calls` message is emitted only when all referenced
  tool calls have matching following tool outputs; otherwise incomplete history is removed.
- **Never synthesize conversation facts.** Do not invent tool outputs, replacement call ids, assistant
  messages, or fallback content to satisfy Chat validation.
- **No response-side repair.** HLD 004 LADR-05 translates successful Chat responses back to Responses
  shape; it must not compensate for invalid request history.
- LADRs are Draft status — flag deviations rather than silently overriding.

## Architecture Decisions

Only decisions whose violation produces wrong code. Full records in [`./ladrs/`](./ladrs/).

| LADR | Decision | Why it matters |
|------|----------|----------------|
| LADR-01 | Normalize prior-turn Responses tool history into Chat adjacency on the downgrade path. | Strict Chat upstreams reject assistant tool calls that are not immediately answered by tool messages. |
| LADR-02 | Remove incomplete history; never synthesize missing tool results or rename tool identities. | Synthetic outputs would change model-visible facts; renames break client tool dispatch assumptions. |

## Key Behaviors

- The normalizer works on prior-turn history, not the active `tools[]` catalog; it complements HLD 004's
  tool-definition normalization.
- A `function_call` and `function_call_output` match by `call_id`; names and arguments are preserved from
  the call item, while output content is preserved from the output item.
- Invalid history removal can reduce model context, but it is safer than sending Chat-invalid messages or
  fabricating missing tool results.

## Quality Constraints

Measurable NFRs live in [`./nfrs/`](./nfrs/). Constraints that change how code is written:

- Validate the emitted Chat message sequence, not just the individual message shapes.
- Keep the response translator stateless with respect to request-history cleanup.

## Changelog

| Date | Change | Ref |
| :---- | :---- | :---- |
| 2026-06-20 | HLD authored for strict-upstream failure on orphaned prior-turn tool calls. | #19 |
