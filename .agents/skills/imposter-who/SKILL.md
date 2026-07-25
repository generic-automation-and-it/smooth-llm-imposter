---
name: imposter-who
description: Probe the SmoothLlmImposter router for the resolved upstream model behind the current harness/agent by issuing a non-streaming `--who?` request. Use when the user asks "which model am I really hitting", "who am I talking to", "what is the imposter target for a model", "am I being rerouted", or `/imposter-who`. Also use when verifying an imposter mapping (e.g. confirming `gpt-5.5` is rewritten to `glm-5.2`) without spending upstream tokens. Works around the streaming-harness limitation that silently bypasses the in-band `--who?` switch (LADR-05).
---

# Imposter Who

Discover which upstream model the SmoothLlmImposter router resolves an inbound model to, by sending a non-streaming `--who?` probe directly to the router. The router short-circuits with a synthetic reply naming the inbound model, the resolved target (or `passthrough`), the auth scheme, and the session identity — with zero upstream HTTP calls.

## Why a skill instead of just typing `--who?`

Agent harnesses (Codex, Claude Code) stream chat requests by default (`"stream": true`). The router intentionally refuses to synthesize `--who?` replies for streaming requests (HLD 010, LADR-05), so a `--who?` typed into the harness forwards to the real upstream, which then answers it as an ordinary user turn — the probe is silently bypassed. This skill issues a separate non-streaming `curl` to the router, so the short-circuit fires and the real routing decision is reported.

## Workflow

1. Determine the dialect: OpenAI (`gpt-*`, most chat models) or Anthropic (`claude-*`). The script auto-detects from `OPENAI_BASE_URL` / `ANTHROPIC_BASE_URL`; pass `--dialect` to force it.
2. Identify the inbound model — the model name the harness is configured to send (e.g. `gpt-5.5`, `claude-sonnet-4-6`). This is the model whose imposter mapping is being probed. Defaults to `who-probe` which routes to the dialect default.
3. Run the probe script:

```bash
.agents/skills/imposter-who/scripts/imposter-who.sh --model gpt-5.5
```

For an Anthropic dialect:

```bash
.agents/skills/imposter-who/scripts/imposter-who.sh --dialect anthropic --model claude-sonnet-4-6
```

4. Interpret the single-line output:

```
Imposter: gpt-5.5 → glm-5.2 (auth: Bearer, session: null)
```

- `Imposter: in to out` — the inbound model is rewritten to `<out>` upstream.
- `Passthrough: inbound` — no mapping matched; the request reaches the real provider unchanged.
- `auth:` — the auth scheme the router will use upstream (`Bearer` / `ApiKey` / `none` / `caller-passthrough`).
- `session:` — resolved session identity, or `null`.

## Script

`scripts/imposter-who.sh` — takes `--dialect`, `--model`, `--session`, `--base-url`. Derives the dialect from `OPENAI_BASE_URL` / `ANTHROPIC_BASE_URL` when not forced. No API key is required: the router resolves upstream credentials from its own config, not the inbound request.

Exit codes: `0` synthetic reply printed; `1` router did not short-circuit (feature disabled or unexpected response); `2` env/curl failure.

## Notes

- The probe never reaches the upstream provider — it is answered by the router itself, so it costs zero upstream tokens and works even when upstream credits are exhausted.
- `--session id` attaches a caller session id (body `session_id` for OpenAI, `session_id` header for Anthropic) so a prior `imposter-newsession` mapping reflects in the `session:` field of the reply.
- The model passed to `--model` should match what the harness actually sends; the router matches on the configured `From` entries (HLD 007).
