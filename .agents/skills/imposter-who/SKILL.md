---
name: imposter-who
description: Probe the SmoothLlmImposter router for the resolved upstream model behind the current harness/agent by issuing a non-streaming `--who?` request. Use when the user asks "which model am I really hitting", "who am I talking to", "what is the imposter target for a model", "am I being rerouted", or `/imposter-who`. Also use when verifying an imposter mapping (e.g. confirming `gpt-5.5` is rewritten to `glm-5.2`) without spending upstream tokens. Works around the streaming-harness limitation that silently bypasses the in-band `--who?` switch (LADR-05).

allowed-tools:
  - Bash(.agents/skills/imposter-who/scripts/imposter-who.sh:*)
  - Bash(${CLAUDE_PLUGIN_ROOT}/.agents/skills/imposter-who/scripts/imposter-who.sh:*)
  - Bash(curl:*)
models:
  claude: haiku      # low-complexity; single non-streaming curl, no deep reasoning
  copilot: gpt-5.4-mini
  codex: gpt-5.4-mini
---

# Imposter Who

Discover which upstream model the SmoothLlmImposter router resolves an inbound model to, by sending a non-streaming `--who?` probe directly to the router. The router short-circuits with a synthetic reply naming the inbound model, the resolved target (or `passthrough`), the auth scheme, and the session identity — with zero upstream HTTP calls.

## Why a skill instead of just typing `--who?`

Agent harnesses (Codex, Claude Code) stream chat requests by default (`"stream": true`). The router intentionally refuses to synthesize `--who?` replies for streaming requests (HLD 010, LADR-05), so a `--who?` typed into the harness forwards to the real upstream, which then answers it as an ordinary user turn — the probe is silently bypassed. This skill issues a separate non-streaming `curl` to the router, so the short-circuit fires and the real routing decision is reported.

## Workflow

This is a **single-shot** probe. Run the script once, relay the one-line reply, stop. Do not enumerate other models, sweep dialects, or re-probe unless the user names a specific model.

1. Determine the model and dialect to probe, in this order of precedence:
   - If the user named a model in their request, use that model. Pick the dialect from the model name: `gpt-*` (and most non-Claude chat models) → OpenAI; `claude-*` → Anthropic. The script auto-detects dialect from `OPENAI_BASE_URL` / `ANTHROPIC_BASE_URL`; pass `--dialect` only when the model name is ambiguous.
   - If the user did not name a model, run the script with no `--model` (it defaults to `who-probe`, which routes to the dialect default). Relay the single-line `Passthrough:` reply as-is — that is the honest answer for an unnamed model.
2. Run the probe **once**:

```bash
.agents/skills/imposter-who/scripts/imposter-who.sh --model gpt-5.5
```

For an Anthropic model:

```bash
.agents/skills/imposter-who/scripts/imposter-who.sh --dialect anthropic --model claude-sonnet-4-6
```

3. Relay the single-line output verbatim to the user. It has the shape:

```
Imposter: gpt-5.5 -> glm-5.2 (auth: Bearer, session: null)
```

- `Imposter: in to out` — the inbound model is rewritten to `out` upstream.
- `Passthrough: inbound` — no mapping matched; the request reaches the real provider unchanged.
- `auth:` — the auth scheme the router will use upstream (`Bearer` / `ApiKey` / `none` / `caller-passthrough`).
- `session:` — resolved session identity, or `null`.

**Do not** loop over candidate models, probe both dialects, or build a summary table unless the user explicitly asks for a sweep. The default invocation is one curl, one line, one answer.

## Script

`scripts/imposter-who.sh` — takes `--dialect`, `--model`, `--session`, `--base-url`. Derives the dialect from `OPENAI_BASE_URL` / `ANTHROPIC_BASE_URL` when not forced. No API key is required: the router resolves upstream credentials from its own config, not the inbound request.

Exit codes: `0` synthetic reply printed; `1` router did not short-circuit (feature disabled or unexpected response); `2` env/curl failure.

- `--port N` overrides the port in the resolved base URL (it replaces the `:<port>` authority in `OPENAI_BASE_URL` / `ANTHROPIC_BASE_URL`). Useful when the router runs on a non-default port or when you want to point at a different router instance without re-setting the env vars.

## Notes

- The probe never reaches the upstream provider — it is answered by the router itself, so it costs zero upstream tokens and works even when upstream credits are exhausted.
- `--session id` attaches a caller session id (body `session_id` for OpenAI, `session_id` header for Anthropic) so a prior `imposter-newsession` mapping reflects in the `session:` field of the reply.
- The model passed to `--model` should match what the harness actually sends; the router matches on the configured `From` entries (HLD 007).
