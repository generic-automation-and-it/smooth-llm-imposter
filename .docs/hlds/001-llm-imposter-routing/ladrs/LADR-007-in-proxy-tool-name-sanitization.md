# LADR-007 — In-proxy tool-name sanitization with response remap (DRAFT)

- **Date / Status:** 2026-06-20 · **Superseded by [HLD 004](../../../hlds/004-codex-to-openai-sdk-transformer/README.md)** (2026-06-21) — parked alternative, never implemented

> This was a parked alternative, kept so the option is not lost. It was **never built**: the project
> instead shipped **[HLD 004](../../../hlds/004-codex-to-openai-sdk-transformer/README.md)** (proxy-side
> normalization via removal, now Completed), which supersedes this sketch and the sibling
> [LADR-006](LADR-006-no-in-proxy-tool-name-sanitization.md). Kept for history.

## Context

Strict upstreams (Moonshot/kimi) reject tool function names that OpenAI accepts — leading-underscore
connector tools and the dotted `multi_tool_use.parallel` (see LADR-006 for the full 400 and rule). LADR-006
resolves this client-side. A centralized, client-agnostic alternative is for the proxy to make the names
upstream-valid and hide the change from the client.

## Sketch of the rejected-for-now approach

1. **Outbound request rewrite:** sanitize each invalid `tools[].function.name` to an upstream-valid token,
   recording a per-request, collision-free, **reversible** name map. Apply the same map to `tool_choice` and
   to any `tool_calls[].function.name` / role-`tool` echoes carried in subsequent turns (the
   `function_call` / `function_call_output` paths in `OpenAiRequestTransformer`).
2. **Inbound response rewrite (the hard part):** because Chat Completions echoes `function.name` and Codex
   dispatches tools **by name** (the `id` only correlates the result), the streamed SSE response must map
   sanitized names back to the originals — across tool-call name fragments that can split over multiple SSE
   deltas. This is stateful streaming JSON rewriting on the hot path.

## Why it is a draft, not a decision

- **Overrides non-negotiables.** Breaks "transparent proxy — do not rewrite the request" and "no response
  rewrite" (the second is implied by the streaming pass-through NFR and LADR-003's no-replay stance).
- **Fragility.** Reversible, collision-free encoding + partial-delta-safe SSE rewriting is non-trivial and
  risky to maintain.
- **The names are genuinely invalid upstream.** Centralizing a workaround for an invalid client contract is
  lower-value than fixing the client toolset (LADR-006).

## If revisited — required before building

- Empirically confirm the round-trip against a **real** Moonshot tool-call response (name echo location,
  delta fragmentation of `function.name`, whether any path keys on `id` only).
- Write a full HLD/LADR superseding LADR-006, defining the encoding scheme, the response-rewrite seam, and
  its test strategy (the in-process transport stub can drive crafted SSE tool-call frames).
