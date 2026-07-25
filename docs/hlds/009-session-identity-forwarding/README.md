# Session Identity Forwarding — High-Level Design

| | |
|---|---|
| **Status** | Accepted |
| **Owner** | Routing feature (@generic-automation-and-it/project) |
| **Tracker** | [Issue #72 — Forward session identity to opencode-go](https://github.com/generic-automation-and-it/smooth-llm-imposter/issues/72) |
| **Last updated** | 2026-07-24 |

> Design HLD. This document delivers **intent + spec** — what we are building and why, the
> decisions behind it, and the quality bar it must meet. Execution detail lives in the worktask
> (`.context/work-tasks/forward-session-identity-to-opencode-go.md`).

## Intent

Codex CLI and Claude Code traffic routed through Smooth to `opencode-go` is invisible in
opencode-go's session diag: those clients do not emit the session markers opencode natively
understands (`x-opencode-session` header and `session_id` body field). Smooth today is a
transparent proxy with no session/sticky support. This HLD adds an **opt-in, per-provider
session-identity forwarding** seam on matched imposter routes that resolves a session id
per-request (capture → derive → none) and stamps the signals opencode-go recognises — without
storing any session state (NFR-001) and without changing behaviour when the opt-in is absent.

## Key Goals

### 1. Opt-in session stamp on matched imposter routes only

A provider may set `SessionForwarding = opencode-go`. On a **matched imposter** route to that
provider the router stamps the resolved identity. Passthrough/default routes remain
byte-transparent for this concern.

**Acceptance criteria / DoD**

- Opt-in absent ⇒ no body field and no managed session header written by the router.
- Opt-in present + matched imposter ⇒ stamp applied; passthrough never stamps.

### 2. Stateless identity resolution (capture → derive → none)

Identity is resolved once per request from inbound material only:

1. Headers (case-insensitive, first match wins): `session_id`, `x-opencode-session`,
   `x-session-id`, `conversation_id`.
2. OpenAI body fields: `prompt_cache_key`, then `metadata.user_id`.
3. Fallback: SHA-256 fingerprint over stable caller identity material
   (`chatgpt-account-id`, `openai-organization`, `openai-project`, `Authorization`, `x-api-key`,
   and body `user` when present). The input set is **sorted canonicalized** (`name=value` lines,
   key-order-independent) before hashing so a caller that reorders its headers cannot fork the
   hash. Prefixed `derived-` + first 16-byte SHA-256 prefix (→ 32 hex chars). If nothing stable
   exists → `session=none` (never invent a random-per-request id).

**Acceptance criteria / DoD**

- Precedence is deterministic and covered by L0 tests.
- Raw values are never logged; the routing Information line carries only
  `session=captured|derived|none`.

### 3. Dual stamp for OpenAI; header-only for Anthropic

OpenAI dialect stamps **both** `session_id` (JSON body) and `x-opencode-session` (header).
Anthropic dialect stamps **header only** — no Anthropic Messages body injection. The
Responses→Chat downgrade allowlist carries `session_id` so the body stamp survives conversion.
When caching is enabled on a Responses upstream and a session was resolved, `prompt_cache_key`
prefers the session identity over the inbound model name.

**Acceptance criteria / DoD**

- Chat Completions / Responses→Chat paths both emit `session_id` when opted in.
- Anthropic body is unchanged by session forwarding.
- Header write is drop-then-write once (no inconsistent duplicates with a caller-supplied value).

### 4. Config surface mirrors existing provider scalars

`ProviderOptions.SessionForwarding` is startup-validated (`none` / `opencode-go`) and exposed on
the conventional env surface as `<PREFIX>_SESSION_FORWARDING`, plus runtime admin CRUD.

**Acceptance criteria / DoD**

- Startup validator rejects unknown values with a fail-fast message that names the provider key.
- `<PREFIX>_SESSION_FORWARDING` overrides the bound value via the existing `ImposterOptionsPostConfigure`
  surface; blank values are treated as absent.
- `/admin/providers` accepts and returns `SessionForwarding`; the field round-trips through `GET`→`PUT`.

## Core Separation of Concerns

> Session identity is **resolved and stamped per request**; it is never stored, and it is never
> part of transparent passthrough.

The router already owns two request-side rewrite classes (auth + caching) and a third
(normalization, HLD 004). Session forwarding is a **fourth sanctioned request-rewrite class**,
scoped the same way: matched imposter + provider opt-in only. Capture happens at the Host edge
via existing `CallerHeaders`; resolution and body stamp live in Application; header stamp lives
in the forwarder beside managed auth.

## Guiding Principle — Relay or derive, never invent noise

> Prefer a caller-supplied marker; else a stable fingerprint; else nothing.

A random-per-request id would fragment opencode-go sessions and defeat the feature. Silence
(`session=none`) is the correct failure mode when the request carries no stable identity.

## Probe findings

Live probe against `https://opencode.ai/zen/go` from this workspace (2026-07-24) authenticated
but rejected every attempted model id (`Model … is not supported`), so **diag visibility could
not be confirmed end-to-end**. Per issue #72 research, opencode clients natively send both
`x-opencode-session` and `session_id`; this design stamps **both** on OpenAI and the header on
Anthropic. **Follow-up validation:** with a working opencode-go model + key, confirm the session
appears in opencode diag for Codex traffic through Smooth (body-only vs header-only vs both).

## Diagrams

- [System Context (C1) + request flow](./diagrams/c4-context.md)

## Architecture Decisions (LADRs)

| LADR | Decision | Status |
|------|----------|--------|
| [LADR-01](./ladrs/LADR-01-fourth-rewrite-class-opt-in.md) | Session forwarding is a fourth sanctioned request-rewrite class, matched-imposter + per-provider opt-in only | Accepted |
| [LADR-02](./ladrs/LADR-02-dual-stamp-and-cache-key.md) | OpenAI dual-stamps body+header; Anthropic header-only; `prompt_cache_key` uses the session only when Responses caching is enabled and a session was resolved | Accepted |
| [LADR-03](./ladrs/LADR-03-stateless-resolve-precedence.md) | Capture → derive fingerprint → none; never random; never log raw values | Accepted |

## Non-Functional Requirements

| NFR | Attribute | Target (summary) | Status |
|-----|-----------|------------------|--------|
| [NFR-01](./nfrs/NFR-01-statelessness.md) | Statelessness | Zero persisted session state; resolve+stamp per request only | Accepted |
| [NFR-02](./nfrs/NFR-02-safe-default-transparency.md) | Safety / transparency | Opt-in absent ⇒ byte-identical to prior behaviour for this concern | Accepted |
| [NFR-03](./nfrs/NFR-03-secret-adjacent-logging.md) | Security / observability | Log only `session=captured\|derived\|none`; never raw identity or fingerprint inputs | Accepted |
