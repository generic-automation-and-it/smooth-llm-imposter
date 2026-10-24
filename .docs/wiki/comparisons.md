# Comparisons

Imposter vs [claude-code-router](https://github.com/musistudio/claude-code-router) (CCR) vs [LiteLLM](https://github.com/BerriAI/litellm).

Legend: ✅ yes · ❌ no · ◐ partial · 🔜 planned. Sources: CCR README (`main`, fetched 2026-10-24) + CCR docs; LiteLLM repo + `docs.litellm.ai`; imposter HLD index (`.docs/hlds/`).

## TL;DR — what is special to the imposter

- **Routes subscriptions, not just API keys.** Forwards caller subscription credentials (`claude setup-token`, ChatGPT session) or per-provider subscription bearers; opt-in GitHub Copilot OAuth pair (HLD 013). CCR imports OAuth logins into its own credential pool; LiteLLM is virtual-keys-over-paid-keys.
- **Key-less routing, single native binary.** .NET NativeAOT, ~73 MB non-root Alpine. Persistence exists where needed (Copilot OAuth SQLite, optional credential store) and may grow — statelessness is not the differentiator.
- **Cache + tool work nobody else bundles.** Per-mapping cache injection, stable tool ordering, decision-model tool pre-filter with per-session sticky selection (HLD 014), opencode session forwarding (HLD 009), `--who?`/`--newsession` zero-upstream-call probes + skills (HLD 010).
- **Upstream-specific fixes.** Codex→OpenAI-SDK shape (HLD 004), Responses→Chat downgrade + history normalization (HLD 006), ZDR `encrypted_content` stripping (HLD 011), per-provider timeout ladder (HLD 012).
- **Built for local dev across TUIs + CLIs, not a company router.** Single dev runs it for Claude Code, Codex and friends; no multi-tenant keys, teams, spend limits or shared-state clustering. Chain behind LiteLLM/CCR for those (see README "how to combine it").

## Feature table

### Imposter core (exists today)

| Feature | Imposter | CCR | LiteLLM |
|---|---|---|---|
| Key-less routing (keys from config/env, Copilot OAuth excepted) | ✅ | ❌ (SQLite credential pools) | ◐ (virtual keys over paid keys) |
| NativeAOT single binary, ~73 MB | ✅ | ❌ (Node 22 / Electron) | ❌ (Python / Docker / Helm) |
| Dual-dialect edge (OpenAI + Anthropic prefixes) | ✅ | ✅ (multi-protocol gateway) | ✅ (`/chat`, `/responses`, `/messages`) |
| Model aliasing, first-match rewrite | ✅ | ✅ (ordered rules + rewrites) | ✅ (model groups + aliases) |
| Subscription-credential routing | ✅ | ◐ (OAuth import into pool) | ◐ (Copilot per-user OAuth only) |
| Per-provider auth injection, central rotation | ✅ | ✅ (pools, rotation) | ✅ (virtual keys, rotation) |
| Prompt-cache hint injection per mapping | ✅ | ❌ (observability only) | ◐ (response/semantic cache, not injection) |
| Decision-model tool pre-filter + sticky session | ✅ (HLD 014) | ❌ (ToolHub lazy-load instead) | ❌ (MCP access control only) |
| Session identity forwarding (`session_id`, `x-opencode-session`) | ✅ (HLD 009) | ◐ (session affinity pinning) | ◐ (sticky pod pinning) |
| `--who?` / `--newsession` zero-call probes + skills | ✅ (HLD 010) | ◐ (resolved-route logs only) | ❌ |
| Codex→OpenAI-SDK transformer | ✅ (HLD 004) | ✅ (`apply_patch` bridge) | ✅ (OpenAI-format unification) |
| Responses→Chat downgrade + history normalization | ✅ (HLD 006) | ✅ (multi-protocol) | ✅ |
| ZDR `encrypted_content` stripping | ✅ (HLD 011) | ❌ | ◐ (affinity handling) |
| Per-provider upstream timeout ladder | ✅ (HLD 012) | ◐ (retry backoff only) | ✅ (timeouts + retries) |
| Runtime provider CRUD admin API | ✅ (HLD 008) | ✅ (mgmt UI + API) | ✅ (keys/teams/models APIs + UI) |
| GitHub Copilot opt-in provider pair | ✅ (HLD 013) | ❌ | ◐ (Copilot as provider) |
| `/v1/models` aggregation | ✅ (HLD 005) | ✅ (catalog + discovery) | ✅ (model discovery) |
| Full inbound request debug dump | ✅ | ✅ (body capture budget) | ✅ (callbacks, redaction flags) |

### Planned (imposter)

| Feature | Imposter | CCR | LiteLLM |
|---|---|---|---|
| Anthropic Messages → Chat bridge | 🔜 (HLD 016, spec in progress) | ✅ (gateway translation) | ✅ (cross-provider translation) |
| Shared decision-model integration | 🔜 (HLD 015, discovery) | ❌ | ❌ |

### Competitor-led (imposter has no equivalent)

| Feature | Imposter | CCR | LiteLLM |
|---|---|---|---|
| Retries + ordered fallback models | ◐ (transport retries, same target; no fallback chain) | ✅ | ✅ |
| Load-balancing strategies (least-busy, latency, cost) | ❌ | ◐ (pools + weights) | ✅ |
| Cost tracking, budgets, spend limits | ❌ | ◐ (estimates, dashboards) | ✅ |
| Guardrails / content filters | ❌ | ❌ | ✅ |
| MCP gateway / ToolHub / capability fusion | ❌ | ✅ | ✅ |
| Management GUI + scoped client keys | ❌ | ✅ | ✅ |
| Embeddings / image / audio / realtime endpoints | ❌ | ◐ (vision/image-gen tools) | ✅ |
| Agent profiles + chat-platform relays | ❌ | ✅ | ❌ |
| Multi-instance shared state (Redis/Postgres) | ❌ (in-process only) | ❌ | ✅ |

## When to chain instead of replace

- Need Chat-only upstream from Claude Code today → put CCR behind the imposter (imposter routes/caches/filters, CCR translates). Costs one hop + Node runtime; cache breakpoints lost at CCR step.
- Need billing, guardrails, non-LLM endpoints → put LiteLLM in front or behind (README "how to combine it").
