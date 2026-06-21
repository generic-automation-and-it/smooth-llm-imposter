# LADR-006 — No in-proxy tool-name sanitization; preserve the transparent proxy

- **Date / Status:** 2026-06-20 · **Superseded by [HLD 004 LADR-01](../../../hlds/004-codex-to-openai-sdk-transformer/ladrs/LADR-01-normalize-proxy-side-client-vanilla.md)** (2026-06-21)

> **Superseded by [HLD 004 LADR-01](../../../hlds/004-codex-to-openai-sdk-transformer/ladrs/LADR-01-normalize-proxy-side-client-vanilla.md).**
> The project chose to keep clients vanilla and normalize **on the proxy side** instead of disabling
> client tools. HLD 004 is now **Completed** (shipped), so the proxy performs in-flight normalization
> and the "no in-proxy sanitization" behavior described below no longer ships. Kept for history.

## Context

Codex (`codex_sdk_ts`) routed `gpt-5.4` → `opencode-go` (openai dialect, `chat_completions`, upstream
Moonshot/kimi via `https://opencode.ai/zen/go`). A tool-using turn returns **HTTP 400**:

> `Error from provider (Moonshot AI): Invalid request: function name is invalid, must start with a
> letter and can contain letters, numbers, underscores, and dashes`

Moonshot enforces tool function names matching `^[a-zA-Z][a-zA-Z0-9_-]*$`. Codex sends names that
violate it: **leading underscore** GitHub connector tools (`_search_issues`, `_create_pull_request`,
`_update_file`, …) and the **dotted** built-in `multi_tool_use.parallel`. OpenAI's own backend accepts
these, so Codex never trips on this against ChatGPT; Moonshot is stricter. The proxy forwards the
(correct) request faithfully — this is a client-tool-naming ⇄ strict-upstream-validation conflict, not a
transport/auth/shape bug. (The auth, identity-header, model-authorization, and Responses→Chat tool-*shape*
links in the same chain were already fixed in #18; only the names themselves remain invalid.)

The tempting centralized fix is for the proxy to sanitize names outbound and map them back. The deciding
question is whether that can be **request-only** or also requires **response rewriting**:

- OpenAI Chat Completions **always** returns `tool_calls[].function.name` in the assistant message; any
  compliant gateway (Moonshot via opencode) echoes the name, not just the `id`.
- Codex dispatches a tool by matching `function.name` against its **local** tool registry; the `id` only
  correlates the result message back. If the proxy rewrites `_search_issues` → `search_issues` outbound
  and the upstream echoes `search_issues`, Codex looks up `search_issues`, finds only `_search_issues`,
  and **fails to dispatch**.

So a name-mangling proxy is **not** request-only: it must rewrite the **streamed SSE response** to map
sanitized names back — stateful, fragile across tool-call name fragments split over SSE deltas, and
needing a collision-free, reversible encoding (a naive underscore-strip can clash with a real
`search_issues`). That directly breaks the transparent-proxy / no-response-rewrite non-negotiables.

## Decision

SmoothLlmImposter does **not** sanitize, rename, or otherwise rewrite tool function names, and does not
rewrite the response. The transparent-proxy contract stands: the request body is relayed unchanged except
for the two documented exceptions (managed auth; caching injection), and the response is never rewritten.

The conflict is resolved **client-side**: configure the Codex/agent profile that targets a strict upstream
to **not expose tools whose names violate the upstream's rule** — disable the `_*` connector/plugin tools.
The dotted built-in `multi_tool_use.parallel` is a Codex/OpenAI parallel-tool-calling artifact that may not
be fully suppressible; that residual limitation against strict upstreams is **accepted and documented**
(see `.docs/wiki/setup.md`).

A future centralized fix is recorded — not rejected outright — as a **draft**:
[LADR-007 (Draft)](LADR-007-in-proxy-tool-name-sanitization.md). Building it requires its own HLD because
it overrides a non-negotiable.

## Consequences

- Zero code change; every transparent-proxy non-negotiable is preserved.
- A tool-using session against strict upstreams (Moonshot) works only with a client toolset whose names are
  upstream-valid; full connector/parallel-tool richness against such upstreams may remain limited until
  LADR-007 is taken up. The required client config and the accepted limitation live in `setup.md`.
- Upstreams with lenient tool-name validation (e.g. OpenAI itself) are unaffected — the proxy already
  forwards their tools verbatim.
