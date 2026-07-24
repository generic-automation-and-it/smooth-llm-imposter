# Task: Anthropic session identity → opencode-go (validate header; optional body transform)

> **Attached worktask for [#75](https://github.com/generic-automation-and-it/smooth-llm-imposter/issues/75).**  
> Local agent copy (gitignored): `.context/work-tasks/anthropic-session-identity-opencode-go.md`  
> Status: draft / parked for a later day.


**GitHub issue:** https://github.com/generic-automation-and-it/smooth-llm-imposter/issues/75 (reference as `#75` in commits and the PR body — `Closes #75`).

**Parent / related:** #72 (OpenAI + header baseline), PR #73, HLD 009.

**Status:** Draft / parked for a later day (issue title prefixed `[draft]`; add labels `draft`/`enhancement`/`task` manually — token cannot label). Related uneditable create: #74 (close as duplicate of #75).

## Contexts
- [ ] `AGENTS.md` (root) — project overview, test tiers, `*_AGENTS.md` maintenance policy
- [ ] `src/SmoothLlmImposter.Application/Features/Routing/ROUTING_AGENTS.md` — four sanctioned rewrite classes; session= log token; Anthropic header-only note from HLD 009
- [ ] `src/SmoothLlmImposter.Host/HOST_AGENTS.md` — `CaptureCallerHeaders` shared with plan
- [ ] `.docs/hlds/009-session-identity-forwarding/` — baseline design; probe follow-up; Anthropic body out-of-scope in LADR-02
- [ ] `.docs/hlds/001-llm-imposter-routing/nfrs/NFR-001-statelessness.md` — no stored state
- [ ] `.agents/rules/backend/architecture-slices.instructions.md`
- [ ] `.agents/rules/backend/backend-logging-conventions.instructions.md` — never log raw session values
- [ ] `.agents/rules/backend/wiremock-stubbing.instructions.md`
- [ ] `.agents/rules/git/git-policy.instructions.md` + `pr-standards.instructions.md`
- [ ] Existing implementation from #72/#73:
  - `SessionIdentityResolver.cs` — capture precedence (headers/body/fingerprint)
  - `AnthropicRequestTransformer.cs` — currently ignores `sessionIdentity` (header stamp is forwarder-only)
  - `UpstreamForwarder.ApplySessionIdentity` — `x-opencode-session` drop-then-write
  - `OpenAiRequestTransformer.cs` — dual-stamp precedent
  - Tests: `SessionIdentityResolverTests`, `RequestTransformerTests`, `RoutingIntegrationTests`, `UpstreamForwarderTests`
- [ ] Issue #75 body — probe matrix + options A–D

## Instructions

### Background
- #72/#73 already stamps `x-opencode-session` on **both** dialects for matched opted-in imposters.
- OpenAI also gets `session_id` body. Anthropic does **not** get body injection.
- Claude Code sends little/no opencode-native session marker; Smooth may derive a fingerprint from auth material.
- opencode research (issue #72) suggested Anthropic-side `metadata.user_id` as a possible additional signal.
- Live OpenAI-path probe in #73 was blocked on model availability; Anthropic-path diag validation was never done.

### Phase 0/1 — Live Anthropic probe FIRST
- [ ] Against opencode-go Anthropic Messages endpoint, vary:
  1. `x-opencode-session` header only
  2. body `metadata.user_id` only
  3. both
  4. neither
- [ ] Record which signal(s) appear in opencode-go diag; choose option A/B/C/D from the issue.
- [ ] If unreachable: stop at docs/probe plan only, or implement only what remains hermetically justifiable — do not guess a body field.

### Functional requirements (conditional on probe)
- [ ] **Option A:** document validation in HLD 009; optional L2 that Anthropic opted-in path emits `x-opencode-session`; close issue.
- [ ] **Option B:** on matched Anthropic imposter + `SessionForwarding=opencode-go`, stamp resolved identity into `metadata.user_id` (create `metadata` if needed; resolve once / write once; do not fight a deliberate caller value inconsistently). Keep header stamp.
- [ ] **Option C:** implement the probe-proven field only; update resolver capture if inbound Anthropic clients already send it.
- [ ] **Option D:** document Anthropic diag unsupported; keep best-effort header stamp.
- [ ] Passthrough/default: never stamp new body fields.
- [ ] Logging: still only `session=captured|derived|none`.

### Scope boundaries
- IN: Anthropic-path probe, HLD 009 update, optional Anthropic body stamp + tests.
- OUT: OpenAI redesign; session store; client changes; response rewrites; new SessionForwarding enum values unless probe demands a distinct profile.

### Testing expectations
- [ ] L0: Anthropic transformer stamps only when opted-in + imposter; byte-transparent otherwise; existing OpenAI tests stay green.
- [ ] L2: Anthropic matched opted-in route emits the probe-selected signal(s); passthrough does not.
- [ ] `dotnet build` / `dotnet test` green.

### Documentation
- [ ] Update HLD 009 probe findings + LADR (or addendum) with Anthropic decision.
- [ ] Update `ROUTING_AGENTS.md` if Anthropic body stamp becomes a sanctioned rewrite detail.
- [ ] Setup note only if operators must know about a new body field.

### Acceptance criteria
- [ ] Probe recorded (or blocked with explicit follow-up)
- [ ] Decision A/B/C/D documented
- [ ] If code: Claude→Anthropic→opencode-go session visible in diag (or hermetic proof of selected signal)
- [ ] No opt-in-absent / passthrough behaviour change
- [ ] No raw session logging
- [ ] Tests + AGENTS/HLD updated

### Known gotchas
- Anthropic Messages body is stricter than OpenAI Chat — prefer header or a single known metadata field over inventing `session_id` on Anthropic JSON.
- `AnthropicRequestTransformer` currently discards `sessionIdentity` by design; body stamp belongs there if needed, not in Host.
- Do not double-write metadata if caller already set `metadata.user_id` — follow resolve-once/write-once.
- Never log raw values (credentials-adjacent).

## Constraints
- Full path if body stamp (transformer + tests + HLD). Lightweight path if Option A docs-only.
- Commits allowed in Phase 6/8 per template override; conventional commits referencing `#75`. Push NOT allowed until asked.
- Issue is labeled `draft` — park until picked up deliberately.
